using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

    private List<CfgFile> _files = new();
    private CfgFile? _current;
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
        addDir(Path.Combine(s.ServerDirPath, "server", s.Identity, "cfg"), "*.cfg", "server");
        addDir(s.CarbonConfigDir, "*.json", "carbon");
        addDir(Path.Combine(s.ServerDirPath, "cfg"), "*.cfg", "root");
        addDir(Path.Combine(s.ServerDirPath, "carbon", "data"), "*.json", "data");
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

    private void LstFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = LstFiles.SelectedItem as CfgFile;
        if (_current == null) return;
        try
        {
            _loading = true;
            Editor.Text = File.ReadAllText(_current.Path);
            Editor.SyntaxHighlighting = _current.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? _jsonHl : _cfgHl;
            Editor.ScrollToHome();
            TxtFileName.Text = _current.Name;
            TxtDirty.Text = "";
            TxtInfo.Text = $"{new FileInfo(_current.Path).Length / 1024.0:0.#} KB";
            UpdatePos();
        }
        catch (Exception ex) { Editor.Text = "// " + ex.Message; }
        finally { _loading = false; }
    }

    private void UpdatePos()
    {
        var c = Editor.TextArea.Caret;
        TxtPos.Text = $"Строка {c.Line}, столбец {c.Column}";
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_loading || _current == null) return;
        TxtDirty.Text = "● изменён";
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
        File.WriteAllText(_current.Path, Editor.Text);
        TxtDirty.Text = "✓ сохранено " + DateTime.Now.ToString("HH:mm:ss");
        // конфиг плагина Carbon → горячая перезагрузка плагина через RCON (как в RSM-FMX)
        if (App.Rcon.IsConnected
            && _current.Path.Contains(Path.Combine("carbon", "configs"), StringComparison.OrdinalIgnoreCase))
        {
            var pluginName = Path.GetFileNameWithoutExtension(_current.Path);
            _ = App.Rcon.FireAsync($"c.reload {pluginName}");
            TxtDirty.Text += $" · c.reload {pluginName}";
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        LstFiles_SelectionChanged(sender, null!); // перечитать с диска
    }
}
