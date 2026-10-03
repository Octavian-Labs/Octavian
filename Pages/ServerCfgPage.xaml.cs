using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using OctavianRSM.Services;

namespace OctavianRSM.Pages;

// Структурный редактор server.cfg (по образцу RSM Web Server Config):
// форма поверх convar'ов, launch-параметры блокируются при работающем сервере
public partial class ServerCfgPage : Page
{
    // ключи, которые формой не покрываются и не должны теряться при сохранении
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "server.hostname", "server.description", "server.url", "server.tags",
        "server.headerimage", "server.logoimage", "server.favoritesendpoint",
        "server.maxplayers", "server.saveinterval", "server.idlekick",
        "server.pve", "censorplayerlist.enabled", "radiation.enabled",
        "server.levelurl", "server.seed", "server.worldsize",
        "server.ip", "server.port", "server.queryport", "rcon.port", "rcon.password"
    };

    public ServerCfgPage()
    {
        InitializeComponent();
        Load();
        SetupFileEditor();
        LoadFileLists();
        FileEditor.TextArea.Caret.PositionChanged += (_, _) =>
            TxtFilePos.Text = $"Строка {FileEditor.TextArea.Caret.Line}, столбец {FileEditor.TextArea.Caret.Column}";
    }

    private void Load()
    {
        var cfg = App.ServerCfg;
        var s = App.Settings;
        cfg.Load();
        TxtPath.Text = cfg.CfgPath;
        TxtFile.Text = File.Exists(cfg.CfgPath) ? "server.cfg" : "файл не найден — будет создан";

        // Пустой convar → дефолт из настроек менеджера (иначе пустые поля пишут мусор в cfg)
        string Val(string key, string fallback) => string.IsNullOrWhiteSpace(cfg[key]) ? fallback : cfg[key];

        // «лицо сервера» (hostname/description/url/tags/картинки/домен) переехало
        // на дашборд → ЛИЦО СЕРВЕРА — здесь ключи остаются только в Known,
        // чтобы не попасть в кастом-поле и не потеряться при Save
        In_MaxPlayers.Text = Val("server.maxplayers", s.MaxPlayers.ToString());
        In_SaveInterval.Text = Val("server.saveinterval", s.SaveInterval.ToString());
        In_IdleKick.Text = Val("server.idlekick", "30");
        Sw_Pve.IsChecked = IsTrue(cfg["server.pve"]);
        Sw_Censor.IsChecked = IsTrue(cfg["censorplayerlist.enabled"]);
        Sw_Radiation.IsChecked = IsTrue(cfg["radiation.enabled"]);
        In_LevelUrl.Text = Val("server.levelurl", s.LevelUrl);
        In_Seed.Text = Val("server.seed", s.WorldSeed.ToString());
        In_WorldSize.Text = Val("server.worldsize", s.WorldSize.ToString());
        In_Ip.Text = Val("server.ip", s.ServerIp);
        In_Port.Text = Val("server.port", s.GamePort.ToString());
        In_QueryPort.Text = Val("server.queryport", s.QueryPort.ToString());
        In_RconPort.Text = Val("rcon.port", s.RconPort.ToString());
        In_RconPass.Text = Val("rcon.password", s.RconPassword);

        In_MapsFolder.Text = s.MapsFolder;
        LoadMapsList();

        // неизвестные ключи показываем как кастомные строки
        In_Custom.Text = string.Join(Environment.NewLine,
            cfg.Values.Where(kv => !Known.Contains(kv.Key))
                      .Select(kv => $"{kv.Key} \"{kv.Value}\""));

        ApplyLock();
        TxtStatus.Text = "загружено";
    }

    private void ApplyLock()
    {
        // Rust читает карту и сеть только при старте — блокируем как в RSM Web
        var locked = App.Server.IsRunning;
        PnlMap.IsEnabled = !locked;
        PnlNet.IsEnabled = !locked;
        TxtLockMap.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        TxtLockNet.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
    }

    // Числовые/сетевые ключи: пустое значение ломает парсинг Rust
    // (saveinterval "" → автосейв каждую секунду) — пустые не пишем вовсе
    private static readonly HashSet<string> NumericKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "server.maxplayers", "server.saveinterval", "server.idlekick",
        "server.seed", "server.worldsize", "server.ip", "server.port",
        "server.queryport", "rcon.port", "rcon.password"
    };

    private void Set(string key, string value)
    {
        if (value.Length == 0 && NumericKeys.Contains(key))
            App.ServerCfg.Values.Remove(key); // ключ отсутствует → дефолт Rust
        else
            App.ServerCfg[key] = value;
    }

    private void Save()
    {
        var cfg = App.ServerCfg;
        Set("server.maxplayers", In_MaxPlayers.Text.Trim());
        Set("server.saveinterval", In_SaveInterval.Text.Trim());
        Set("server.idlekick", In_IdleKick.Text.Trim());
        cfg["server.pve"] = ToFlag(Sw_Pve.IsChecked);
        cfg["censorplayerlist.enabled"] = ToFlag(Sw_Censor.IsChecked);
        cfg["radiation.enabled"] = ToFlag(Sw_Radiation.IsChecked);

        if (!App.Server.IsRunning)
        {
            cfg["server.levelurl"] = In_LevelUrl.Text.Trim();
            Set("server.seed", In_Seed.Text.Trim());
            Set("server.worldsize", In_WorldSize.Text.Trim());
            Set("server.ip", In_Ip.Text.Trim());
            Set("server.port", In_Port.Text.Trim());
            Set("server.queryport", In_QueryPort.Text.Trim());
            Set("rcon.port", In_RconPort.Text.Trim());
            Set("rcon.password", In_RconPass.Text.Trim());
        }

        // кастомные convars — общий парсер (ключи могут содержать пробелы)
        foreach (var raw in In_Custom.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (ServerConfigService.TryParseLine(line, out var key, out var val))
                cfg[key] = val;
        }

        cfg.Save();
        // лицо сервера (hostname/description/домен) — на дашборде, там же синхрон настроек
        TxtStatus.Text = "✓ сохранено";
    }

    // ── Библиотека кастомных карт ────────────────────────────────────────
    // Папка с .map файлами → выпадающий список с размером и датой,
    // выбор пишет file:/// URI в server.levelurl (Rust грузит локальные карты
    // по file://, для внешнего хостинга нужен http-URL как раньше)

    private class MapEntry
    {
        public string Path = "";
        public string Display = "";
        public override string ToString() => Display;
    }

    private void LoadMapsList()
    {
        var folder = In_MapsFolder.Text.Trim();
        CbMaps.Items.Clear();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            TxtMapInfo.Text = folder.Length == 0 ? "" : "папка не найдена";
            return;
        }
        try
        {
            foreach (var f in Directory.GetFiles(folder, "*.map"))
            {
                var fi = new FileInfo(f);
                CbMaps.Items.Add(new MapEntry
                {
                    Path = f,
                    Display = $"{Path.GetFileNameWithoutExtension(f)}  ·  {fi.Length / 1048576.0:0.#} MB  ·  {fi.LastWriteTime:dd.MM.yyyy}"
                });
            }
            TxtMapInfo.Text = CbMaps.Items.Count == 0
                ? "в папке нет .map файлов"
                : $"найдено карт: {CbMaps.Items.Count}. Выбор пропишет file:/// в server.levelurl.";
        }
        catch (Exception ex) { TxtMapInfo.Text = ex.Message; }
    }

    private void MapsFolder_Changed(object sender, RoutedEventArgs e)
    {
        App.Settings.MapsFolder = In_MapsFolder.Text.Trim();
        App.Settings.Save(App.SettingsPath);
        LoadMapsList();
    }

    private void BrowseMaps_Click(object sender, RoutedEventArgs e)
    {
        // OpenFileDialog с FileName="" работает как выбор папки без лишних зависимостей
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите папку с .map картами (откройте её и нажмите «Открыть»)",
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = "выбор папки",
            Filter = "Папка|*.map"
        };
        if (dlg.ShowDialog() != true) return;
        var dir = Path.GetDirectoryName(dlg.FileName);
        if (string.IsNullOrEmpty(dir)) return;
        In_MapsFolder.Text = dir;
        MapsFolder_Changed(sender, e);
    }

    private void Map_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (CbMaps.SelectedItem is not MapEntry m) return;
        var fi = new FileInfo(m.Path);
        In_LevelUrl.Text = new Uri(fi.FullName).AbsoluteUri; // file:///D:/...
        TxtMapInfo.Text = $"{fi.Name} — {fi.Length / 1048576.0:0.#} MB, изменена {fi.LastWriteTime:dd.MM.yyyy HH:mm}. " +
                          $"Записано в server.levelurl — применится при старте/вайпе.";
    }

    // ── Вкладка «Редактор файлов»: сырые cfg/json сервера и ядра Carbon ────

    private class CfgFileRef
    {
        public string Name = "";
        public string Path = "";
        public override string ToString() => Name;
    }

    private CfgFileRef? _curFile;
    private bool _fileLoading;
    private static IHighlightingDefinition? _jsonHl, _cfgHl;

    private void SetupFileEditor()
    {
        FileEditor.Options.ConvertTabsToSpaces = true;
        FileEditor.Options.HighlightCurrentLine = true;
        FileEditor.Options.CutCopyWholeLine = true;
        FileEditor.Options.EnableHyperlinks = false;
        FileEditor.Options.EnableEmailHyperlinks = false;
        FileEditor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xD9, 0xA9, 0x3F));
        FileEditor.TextArea.SelectionBorder = null;
        FileEditor.TextArea.SelectionForeground = null;
        FileEditor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x18, 0xD9, 0xA9, 0x3F));
        FileEditor.TextArea.TextView.CurrentLineBorder =
            new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0xD9, 0xA9, 0x3F)), 1);
        FileEditor.TextArea.Caret.CaretBrush = (SolidColorBrush)FindResource("B.Accent");
        LoadHighlighting();
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

    private void LoadFileLists()
    {
        var s = App.Settings;
        List<CfgFileRef> server = new(), carbon = new();
        void collect(string dir, string pattern, List<CfgFileRef> into)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, pattern))
                into.Add(new CfgFileRef { Name = Path.GetFileName(f), Path = f });
        }
        // Только серверные identity-cfg и ядро Carbon — конфиги плагинов
        // правятся отдельным редактором по ⚙ на вкладке «Плагины»
        collect(Path.Combine(s.ServerDirPath, "server", s.Identity, "cfg"), "*.cfg", server);
        collect(Path.Combine(s.ServerDirPath, "carbon"), "*.json", carbon);
        LstServer.ItemsSource = server;
        LstCarbon.ItemsSource = carbon;
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // снимаем выбор в соседнем списке — активным должен быть один файл
        if (sender == LstServer && LstServer.SelectedItem != null) LstCarbon.SelectedItem = null;
        if (sender == LstCarbon && LstCarbon.SelectedItem != null) LstServer.SelectedItem = null;
        _curFile = (sender as ListBox)?.SelectedItem as CfgFileRef;
        if (_curFile == null) return;
        try
        {
            _fileLoading = true;
            FileEditor.Text = File.ReadAllText(_curFile.Path);
            FileEditor.SyntaxHighlighting = _curFile.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? _jsonHl : _cfgHl;
            FileEditor.ScrollToHome();
            TxtFileName.Text = _curFile.Name;
            TxtFileDirty.Text = "";
            TxtFileInfo.Text = $"{new FileInfo(_curFile.Path).Length / 1024.0:0.#} KB";
        }
        catch (Exception ex) { FileEditor.Text = "// " + ex.Message; }
        finally { _fileLoading = false; }
    }

    private void FileEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_fileLoading || _curFile == null) return;
        TxtFileDirty.Text = "● изменён";
        TxtFileDirty.Foreground = (Brush)FindResource("B.Warn");
    }

    private void FileEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FileSave_Click(sender, e);
            e.Handled = true;
        }
    }

    private void FileSave_Click(object sender, RoutedEventArgs e)
    {
        if (_curFile == null) return;
        try
        {
            File.WriteAllText(_curFile.Path, FileEditor.Text);
            TxtFileDirty.Text = "✓ сохранено " + DateTime.Now.ToString("HH:mm:ss");
            TxtFileDirty.Foreground = (Brush)FindResource("B.Success");
            // server.cfg сервер перечитывает по server.writecfg
            if (App.Rcon.IsConnected
                && _curFile.Name.Equals("server.cfg", StringComparison.OrdinalIgnoreCase))
            {
                _ = App.Rcon.FireAsync("server.writecfg");
                TxtFileDirty.Text += " · server.writecfg";
            }
        }
        catch (Exception ex)
        {
            TxtFileDirty.Text = "ошибка: " + ex.Message;
            TxtFileDirty.Foreground = (Brush)FindResource("B.Danger");
        }
    }

    private void FileRevert_Click(object sender, RoutedEventArgs e)
    {
        if (_curFile == null) return;
        var sel = LstServer.SelectedItem != null ? LstServer : LstCarbon;
        FileList_SelectionChanged(sel, null!); // перечитать с диска
    }

    private void BtnSave_Click(object s, RoutedEventArgs e) => Save();

    private void BtnRevert_Click(object s, RoutedEventArgs e) => Load();

    private async void BtnReadCfg_Click(object s, RoutedEventArgs e)
    {
        if (!App.Rcon.IsConnected) { TxtStatus.Text = "RCON не подключён"; return; }
        Save();
        var r = await App.Rcon.SendAsync("server.readcfg");
        TxtStatus.Text = $"readcfg → {Trim(r)}";
    }

    private static bool IsTrue(string v)
        => v is "1" or "true" || v.Equals("True", StringComparison.OrdinalIgnoreCase);

    private static string ToFlag(bool? b) => b == true ? "1" : "0";

    private static string Trim(string s) => s.Length > 80 ? s[..80] + "…" : s;
}
