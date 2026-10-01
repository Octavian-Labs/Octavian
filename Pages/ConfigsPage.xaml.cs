using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace OctavianRSM.Pages;

public partial class ConfigsPage : Page
{
    private class CfgFile { public string Name = ""; public string Path = ""; public override string ToString() => Name; }

    // Строка серверного cfg: либо распарсенная «ключ "значение"», либо сырая
    // (комментарий/команда) — сырые сохраняются как есть и в таблице не показываются
    private class CfgLine
    {
        public bool Parsed;
        public string Raw = "";
        public string Key = "";
        public string Value = "";
        public int BoolKind;          // 0 = не bool; 1 = "0"/"1"; 2 = "True"/"False"
        public CheckBox? Check;       // контрол таблицы для bool
        public TextBox? Box;          // контрол таблицы для остальных
    }

    // Rust cfg: ключ "значение" — значение всегда в кавычках, ключ без пробелов
    private static readonly Regex CfgRe = new(@"^([^\s""]+)\s+""(.*)""\s*$", RegexOptions.Compiled);

    private List<CfgFile> _files = new();
    private CfgFile? _current;
    private List<CfgLine> _cfgLines = new();
    private bool _trailingNewline;
    private bool _tableMode = true;
    private bool _dirty;
    private static IHighlightingDefinition? _jsonHl, _cfgHl;
    private bool _loading; // защита от ложного "изменён" при загрузке файла

    public ConfigsPage()
    {
        InitializeComponent();
        SetupEditor();
        LoadHighlighting();
        LoadFileList();
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdatePos();
    }

    private void SetupEditor()
    {
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.ShowSpaces = false;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.CutCopyWholeLine = true;
        // Тёмные кисти текстовой области — иначе скролл/выделение смотрятся светлыми
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xD9, 0xA9, 0x3F));
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x18, 0xD9, 0xA9, 0x3F));
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0xD9, 0xA9, 0x3F)), 1);
        Editor.TextArea.Caret.CaretBrush = (SolidColorBrush)FindResource("B.Accent");
    }

    private static void LoadHighlighting()
    {
        if (_jsonHl != null) return;
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
        _jsonHl = LoadXshd(Path.Combine(dir, "JsonMode.xshd"));
        _cfgHl = LoadXshd(Path.Combine(dir, "CfgMode.xshd"));
    }

    private static IHighlightingDefinition? LoadXshd(string path)
    {
        try
        {
            // файл рядом с EXE приоритетнее — встроенный ресурс как fallback (single-file)
            Stream? stream = File.Exists(path) ? File.OpenRead(path)
                : Assembly.GetExecutingAssembly().GetManifestResourceStream(
                    "OctavianRSM.Assets." + Path.GetFileName(path));
            if (stream == null) return null;
            using (stream)
            using (var reader = XmlReader.Create(stream))
                return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch { return null; }
    }

    private void LoadFileList()
    {
        _files = new List<CfgFile>();
        var s = App.Settings;
        void addDir(string dir, string pattern, string group)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, pattern))
                _files.Add(new CfgFile { Name = $"{group} · {Path.GetFileName(f)}", Path = f });
        }
        // Только конфиги сервера: identity-cfg и ядро Carbon (корень carbon/, без configs/ плагинов —
        // у них свой редактор по кнопке ⚙ на вкладке «Плагины»)
        addDir(Path.Combine(s.ServerDirPath, "server", s.Identity, "cfg"), "*.cfg", "server");
        addDir(Path.Combine(s.ServerDirPath, "carbon"), "*.json", "carbon");
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = TxtSearch.Text.Trim();
        LstFiles.ItemsSource = string.IsNullOrEmpty(q)
            ? _files
            : _files.Where(f => f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private static bool IsCfgFile(string path)
        => path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase);

    private void LstFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = LstFiles.SelectedItem as CfgFile;
        if (_current == null) return;
        try
        {
            _loading = true;
            var text = File.ReadAllText(_current.Path);
            _trailingNewline = text.EndsWith("\n");
            Editor.Text = text;
            ParseCfg(text);
            Editor.SyntaxHighlighting = _current.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? _jsonHl : _cfgHl;
            Editor.ScrollToHome();
            TxtFileName.Text = _current.Name;
            TxtInfo.Text = $"{new FileInfo(_current.Path).Length / 1024.0:0.#} KB";
            // .cfg → таблица по умолчанию; .json ядра carbon — только текст
            BtnTable.IsEnabled = IsCfgFile(_current.Path);
            SetMode(IsCfgFile(_current.Path));
            SetDirty(false);
            UpdatePos();
        }
        catch (Exception ex) { Editor.Text = "// " + ex.Message; }
        finally { _loading = false; }
    }

    // ── Парсинг «ключ "значение"» ──────────────────────────────────────────

    private void ParseCfg(string text)
    {
        _cfgLines = new List<CfgLine>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            var m = CfgRe.Match(line);
            if (!m.Success)
            {
                _cfgLines.Add(new CfgLine { Raw = line });
                continue;
            }
            var v = m.Groups[2].Value;
            var l = new CfgLine { Parsed = true, Key = m.Groups[1].Value, Value = v };
            if (v is "0" or "1") l.BoolKind = 1;
            else if (v.Equals("True", StringComparison.OrdinalIgnoreCase)
                  || v.Equals("False", StringComparison.OrdinalIgnoreCase)) l.BoolKind = 2;
            _cfgLines.Add(l);
        }
        // Последняя «строка» после завершающего \n — пустая, не добавляем лишнюю сырую
        if (_cfgLines.Count > 0 && !_cfgLines[^1].Parsed && _cfgLines[^1].Raw == "")
            _cfgLines.RemoveAt(_cfgLines.Count - 1);
    }

    private void BuildTable()
    {
        CfgRows.Children.Clear();
        int parsed = _cfgLines.Count(l => l.Parsed);
        if (parsed == 0)
        {
            CfgRows.Children.Add(new TextBlock
            {
                Text = "Файл не в формате «ключ \"значение\"» — редактируйте в текстовом режиме ({ } ТЕКСТ).",
                Foreground = (Brush)FindResource("B.TextDim"), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }
        foreach (var l in _cfgLines)
        {
            if (!l.Parsed) continue;
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            var key = new TextBlock
            {
                Text = l.Key, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Width = 330, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("B.TextDim"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = l.Key
            };
            row.Children.Add(key);
            if (l.BoolKind != 0)
            {
                var chk = new CheckBox
                {
                    IsChecked = l.BoolKind == 1 ? l.Value == "1"
                              : l.Value.Equals("True", StringComparison.OrdinalIgnoreCase),
                    VerticalAlignment = VerticalAlignment.Center
                };
                chk.Click += (_, _) => MarkDirty();
                l.Check = chk;
                row.Children.Add(chk);
            }
            else
            {
                var box = new TextBox
                {
                    Text = l.Value, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                box.TextChanged += (_, _) => MarkDirty();
                l.Box = box;
                row.Children.Add(box);
            }
            CfgRows.Children.Add(row);
        }
        int hidden = _cfgLines.Count - parsed;
        if (hidden > 0)
            CfgRows.Children.Add(new TextBlock
            {
                Text = $"⋯ ещё {hidden} служебных строк (команды/комментарии) — сохраняются как есть, правьте их в текстовом режиме",
                Foreground = (Brush)FindResource("B.TextDim"), FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0)
            });
    }

    private void MarkDirty()
    {
        if (_loading) return;
        SetDirty(true);
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        TxtDirty.Text = dirty ? "● изменён" : "";
    }

    // Таблица → строки файла
    private void ApplyTable()
    {
        foreach (var l in _cfgLines.Where(l => l.Parsed))
        {
            if (l.Check != null)
                l.Value = l.Check.IsChecked == true
                    ? (l.BoolKind == 1 ? "1" : "True")
                    : (l.BoolKind == 1 ? "0" : "False");
            else if (l.Box != null)
                l.Value = l.Box.Text;
        }
    }

    private string ComposeText()
    {
        var text = string.Join("\r\n", _cfgLines.Select(l =>
            l.Parsed ? $"{l.Key} \"{l.Value}\"" : l.Raw));
        return _trailingNewline ? text + "\r\n" : text;
    }

    // ── Переключение режимов ────────────────────────────────────────────────

    private void SetMode(bool table)
    {
        _tableMode = table && _current != null && IsCfgFile(_current.Path);
        if (_tableMode)
        {
            if (Editor.Visibility == Visibility.Visible)
                ParseCfg(Editor.Text); // подтянуть правки из текстового режима
            BuildTable();
            PaneTable.Visibility = Visibility.Visible;
            Editor.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (PaneTable.Visibility == Visibility.Visible)
            {
                ApplyTable();
                _loading = true;
                Editor.Text = ComposeText();
                _loading = false;
            }
            PaneTable.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
        }
        BtnTable.Appearance = _tableMode ? Wpf.Ui.Controls.ControlAppearance.Primary
                                       : Wpf.Ui.Controls.ControlAppearance.Secondary;
        BtnText.Appearance = !_tableMode ? Wpf.Ui.Controls.ControlAppearance.Primary
                                        : Wpf.Ui.Controls.ControlAppearance.Secondary;
        TxtPos.Text = _tableMode ? "" : PosText();
    }

    private void ModeTable_Click(object sender, RoutedEventArgs e) => SetMode(true);
    private void ModeText_Click(object sender, RoutedEventArgs e) => SetMode(false);

    // ── Сохранение ──────────────────────────────────────────────────────────

    private string PosText()
    {
        var c = Editor.TextArea.Caret;
        return $"Строка {c.Line}, столбец {c.Column}";
    }

    private void UpdatePos()
    {
        if (!_tableMode) TxtPos.Text = PosText();
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_loading || _current == null) return;
        SetDirty(true);
    }

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        try
        {
            string text;
            if (_tableMode)
            {
                ApplyTable();
                text = ComposeText();
            }
            else text = Editor.Text;
            File.WriteAllText(_current.Path, text);
            TxtDirty.Text = "✓ сохранено " + DateTime.Now.ToString("HH:mm:ss");
            TxtDirty.Foreground = (Brush)FindResource("B.Success");
            _dirty = false;
            // server.cfg читается сервером при server.writecfg/restart — изменения
            // вступают в силу после перезапуска или writecfg
            if (App.Rcon.IsConnected && IsCfgFile(_current.Path)
                && Path.GetFileName(_current.Path).Equals("server.cfg", StringComparison.OrdinalIgnoreCase))
            {
                _ = App.Rcon.FireAsync("server.writecfg");
                TxtDirty.Text += " · server.writecfg";
            }
        }
        catch (Exception ex)
        {
            TxtDirty.Text = "ошибка: " + ex.Message;
            TxtDirty.Foreground = (Brush)FindResource("B.Danger");
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        LstFiles_SelectionChanged(sender, null!); // перечитать с диска
    }
}
