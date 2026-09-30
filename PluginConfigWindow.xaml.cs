using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using OctavianRSM.Services;

namespace OctavianRSM;

// Редактор конфига плагина: два режима — таблица «настройка/значение»
// (bool→чекбокс, число→поле, строка→текст, массив/объект→JSON) и сырой JSON
public partial class PluginConfigWindow : Window
{
    private class CfgRow
    {
        public required string[] Segments { get; init; } // путь ключей объекта до значения
        public required string Kind { get; init; }       // bool|num|str|json
        public required FrameworkElement Editor { get; init; }
    }

    private readonly string _pluginName;
    private readonly string _path;
    private JsonNode? _root;
    private readonly List<CfgRow> _rows = new();
    private bool _tableMode = true;
    private bool _loading;
    private bool _dirty;
    private static IHighlightingDefinition? _jsonHl;

    public PluginConfigWindow(string pluginName)
    {
        InitializeComponent();
        _pluginName = pluginName;
        _path = Path.Combine(App.Settings.CarbonConfigDir, pluginName + ".json");
        Title = $"OctavianRSM — {pluginName}";
        TxtTitle.Text = $"{pluginName} — настройки";
        TxtPath.Text = _path;
        SetupEditor();
        LoadFile();
    }

    // ── Загрузка ────────────────────────────────────────────

    private void LoadFile()
    {
        _loading = true;
        try
        {
            var text = File.Exists(_path) ? File.ReadAllText(_path) : "";
            if (string.IsNullOrWhiteSpace(text))
            {
                _root = new JsonObject();
                TxtHint.Text = File.Exists(_path)
                    ? "Конфиг пуст — плагин обычно создаёт его при первой загрузке."
                    : "Файл конфига ещё не создан — сохранение создаст его в carbon/configs.";
            }
            else
            {
                _root = JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = false });
                TxtHint.Text = "Таблица — типизированные поля; JSON — прямой редактор. Ctrl+S / кнопка — сохранить и c.reload.";
            }
            Editor.Text = text;
            RebuildRows();
            SetMode(true);
            SetDirty(false);
            TxtStatus.Text = "";
        }
        catch (Exception ex)
        {
            _root = null;
            Editor.Text = File.Exists(_path) ? File.ReadAllText(_path) : "";
            TxtHint.Text = $"JSON не парсится — доступен только сырой редактор: {ex.Message}";
            SetMode(false);
            BtnTable.IsEnabled = false;
        }
        finally { _loading = false; }
    }

    // ── Таблица: раскладка JSON в строки ─────────────────────

    private void RebuildRows()
    {
        _rows.Clear();
        Rows.Children.Clear();
        if (_root != null) Flatten(_root, Array.Empty<string>(), 0);
    }

    private void Flatten(JsonNode node, string[] path, int depth)
    {
        if (node is JsonObject obj && depth < 12)
        {
            if (obj.Count == 0)
                AddRow(path, "json", "{ }"); // пустой объект — редактируем как JSON
            foreach (var kv in obj)
                if (kv.Value is JsonObject || kv.Value is JsonArray)
                {
                    if (kv.Value is JsonObject o && o.Count <= 24 && o.All(v => v.Value is not JsonObject))
                        Flatten(kv.Value, path.Concat(new[] { kv.Key }).ToArray(), depth + 1);
                    else
                        AddRow(path.Concat(new[] { kv.Key }).ToArray(), "json", kv.Value!.ToJsonString());
                }
                else
                    AddScalarRow(path.Concat(new[] { kv.Key }).ToArray(), kv.Value);
        }
        else
        {
            AddRow(path, "json", node.ToJsonString());
        }
    }

    private void AddScalarRow(string[] path, JsonNode? node)
    {
        switch (node)
        {
            case null:
                AddRow(path, "str", "");
                break;
            case JsonValue v when v.TryGetValue<bool>(out var b):
                AddRow(path, "bool", b ? "true" : "false");
                break;
            case JsonValue v when v.TryGetValue<long>(out var l):
                AddRow(path, "num", l.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValue v when v.TryGetValue<double>(out var d):
                AddRow(path, "num", d.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                AddRow(path, "str", s ?? "");
                break;
            default:
                AddRow(path, "json", node.ToJsonString());
                break;
        }
    }

    private void AddRow(string[] path, string kind, string text)
    {
        var dim = (Brush)FindResource("B.TextDim");
        var accent = (Brush)FindResource("B.Accent");
        var label = new TextBlock
        {
            Text = string.Join(" › ", path.Skip(1)), // корневой ключ не показываем дважды
            Foreground = dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, ToolTip = string.Join(".", path), MaxWidth = 300
        };
        if (path.Length <= 1) { label.Text = path.LastOrDefault() ?? "(root)"; label.FontWeight = FontWeights.SemiBold; label.Foreground = accent; }

        FrameworkElement editor = kind switch
        {
            "bool" => MakeCheck(text == "true"),
            "num" => MakeTextBox(text, 140, true),
            "json" => MakeTextBox(text, 420, false, true),
            _ => MakeTextBox(text, 420, false),
        };
        _rows.Add(new CfgRow { Segments = path, Kind = kind, Editor = editor });

        var row = new DockPanel { Margin = new Thickness(4 + Math.Min(path.Length - 1, 8) * 12, 2, 4, 2) };
        DockPanel.SetDock(label, Dock.Left);
        label.Width = 300;
        row.Children.Add(label);
        row.Children.Add(editor);
        Rows.Children.Add(row);
    }

    private CheckBox MakeCheck(bool on)
    {
        var cb = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        cb.Checked += (_, _) => MarkDirty();
        cb.Unchecked += (_, _) => MarkDirty();
        return cb;
    }

    private TextBox MakeTextBox(string text, double width, bool numeric, bool json = false)
    {
        var tb = new TextBox
        {
            Text = text, Width = width, FontSize = 12,
            Foreground = (Brush)FindResource("B.Text"),
            Background = (Brush)FindResource("B.Bg2"),
            BorderBrush = (Brush)FindResource("B.Border"),
            BorderThickness = new Thickness(1), Padding = new Thickness(8, 3, 8, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = json ? new FontFamily("Consolas") : new FontFamily("Segoe UI"),
            AcceptsReturn = json, TextWrapping = json ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MaxHeight = json ? 120 : double.NaN
        };
        if (json) { tb.MinHeight = 30; tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        tb.TextChanged += (_, _) => MarkDirty();
        return tb;
    }

    private void MarkDirty()
    {
        if (_loading) return;
        SetDirty(true);
    }

    private void SetDirty(bool d)
    {
        _dirty = d;
        TxtStatus.Text = d ? "● несохранённые изменения" : "";
        TxtStatus.Foreground = d ? (Brush)FindResource("B.Warn") : (Brush)FindResource("B.TextDim");
    }

    // ── Сохранение ──────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string text;
            if (_tableMode && _root != null)
            {
                ApplyRows();
                text = _root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            else
            {
                text = Editor.Text;
                JsonNode.Parse(text); // валидация
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, text);
            if (!_tableMode) _root = JsonNode.Parse(text);
            SetDirty(false);
            var reloaded = "";
            if (App.Rcon.IsConnected)
            {
                _ = App.Rcon.FireAsync($"c.reload {_pluginName}");
                reloaded = $" · c.reload {_pluginName}";
            }
            TxtStatus.Foreground = (Brush)FindResource("B.Accent");
            TxtStatus.Text = $"✓ сохранено {DateTime.Now:HH:mm:ss}{reloaded}";
        }
        catch (Exception ex)
        {
            TxtStatus.Foreground = (Brush)FindResource("B.Danger");
            TxtStatus.Text = "ошибка: " + ex.Message;
        }
    }

    // Перенос значений строк обратно в JsonNode-граф
    private void ApplyRows()
    {
        foreach (var r in _rows)
        {
            JsonNode? parent = _root;
            for (var i = 0; i < r.Segments.Length - 1; i++)
                parent = parent![r.Segments[i]];
            if (parent == null) continue;

            var key = r.Segments[^1];
            JsonNode? value = r.Kind switch
            {
                "bool" => JsonValue.Create((r.Editor as CheckBox)!.IsChecked == true),
                "num" => NumValue((r.Editor as TextBox)!.Text.Trim()),
                "json" => JsonNode.Parse((r.Editor as TextBox)!.Text),
                _ => JsonValue.Create((r.Editor as TextBox)!.Text),
            };
            parent[key] = value;
        }
    }

    private static JsonNode NumValue(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var dc))
            return JsonValue.Create(dc);
        throw new FormatException($"«{s}» — не число");
    }

    // ── Режимы таблица/JSON ──────────────────────────────────

    private void SetMode(bool table)
    {
        if (table && _root == null) return; // битый JSON — только сырой редактор
        _tableMode = table;
        PaneTable.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        PaneJson.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
        BtnTable.Appearance = table ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        BtnJson.Appearance = table ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
    }

    private void ModeTable_Click(object sender, RoutedEventArgs e)
    {
        if (_tableMode) return;
        try
        {
            _root = JsonNode.Parse(Editor.Text);
            RebuildRows();
            SetMode(true);
        }
        catch (Exception ex)
        {
            TxtStatus.Foreground = (Brush)FindResource("B.Danger");
            TxtStatus.Text = $"JSON не парсится, сначала исправьте: {ex.Message}";
        }
    }

    private void ModeJson_Click(object sender, RoutedEventArgs e)
    {
        if (!_tableMode) return;
        try
        {
            if (_root != null) { ApplyRows(); Editor.Text = _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); }
        }
        catch { /* строки не применились — JSON-режим покажет старый текст ниже */ }
        SetMode(false);
    }

    // ── JSON-редактор ───────────────────────────────────────

    private void SetupEditor()
    {
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.CutCopyWholeLine = true;
        Editor.SyntaxHighlighting = LoadJsonHl();
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xD9, 0xA9, 0x3F));
        Editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x18, 0xD9, 0xA9, 0x3F));
        Editor.TextArea.Caret.CaretBrush = (SolidColorBrush)FindResource("B.Accent");
    }

    private static IHighlightingDefinition? LoadJsonHl()
    {
        if (_jsonHl != null) return _jsonHl;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "JsonMode.xshd");
            Stream? stream = File.Exists(path) ? File.OpenRead(path)
                : Assembly.GetExecutingAssembly().GetManifestResourceStream("OctavianRSM.Assets.JsonMode.xshd");
            if (stream == null) return null;
            using (stream)
            using (var reader = XmlReader.Create(stream))
                _jsonHl = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch { }
        return _jsonHl;
    }

    private void Editor_TextChanged(object? sender, EventArgs e) => MarkDirty();

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => LoadFile();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty && MessageBox.Show("Есть несохранённые изменения. Закрыть без сохранения?",
                "OctavianRSM", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
