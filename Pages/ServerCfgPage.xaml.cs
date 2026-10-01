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

        In_Hostname.Text = Val("server.hostname", s.Hostname);
        In_Description.Text = Val("server.description", s.Description);
        In_Url.Text = cfg["server.url"];
        In_Tags.Text = cfg["server.tags"];
        In_Header.Text = cfg["server.headerimage"];
        In_Logo.Text = cfg["server.logoimage"];
        In_Domain.Text = Val("server.favoritesendpoint", s.ServerDomain);
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
        cfg["server.hostname"] = In_Hostname.Text.Trim();
        cfg["server.description"] = In_Description.Text.Trim();
        cfg["server.url"] = In_Url.Text.Trim();
        cfg["server.tags"] = In_Tags.Text.Trim();
        cfg["server.headerimage"] = In_Header.Text.Trim();
        cfg["server.logoimage"] = In_Logo.Text.Trim();
        cfg["server.favoritesendpoint"] = In_Domain.Text.Trim().ToLowerInvariant();
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
        // hostname/description живут и в launch-аргументах — держим настройки в синхроне
        App.Settings.Hostname = In_Hostname.Text.Trim();
        App.Settings.Description = In_Description.Text.Trim();
        // Домен нужен и менеджеру: строка подключения на дашборде
        App.Settings.ServerDomain = In_Domain.Text.Trim().ToLowerInvariant();
        App.Settings.Save(App.SettingsPath);
        TxtStatus.Text = "✓ сохранено";
    }

    // Проверка публикации: валидность полей/тегов, состояние локальных портов и
    // реальный A2S-ответ Steam Query. Ничего не сохраняет — только диагностика.
    private async void CheckPublication_Click(object s, RoutedEventArgs e)
    {
        var errors = ValidatePublication();
        var ip = IPGlobalProperties.GetIPGlobalProperties();
        var udpPorts = ip.GetActiveUdpListeners().Select(p => p.Port).ToHashSet();
        var tcpPorts = ip.GetActiveTcpListeners().Select(p => p.Port).ToHashSet();
        var query = await QueryA2s(App.Settings.QueryPort);
        PubPortState.Text = $"Game UDP {PortState(udpPorts, App.Settings.GamePort)} • Query {query} • " +
            $"RCON TCP {PortState(tcpPorts, App.Settings.RconPort)} • RCON {(App.Rcon.IsConnected ? "ON" : "OFF")}";
        PubPortState.Foreground = (Brush)FindResource(
            errors.Count == 0 && App.Rcon.IsConnected && query.StartsWith("A2S OK") ? "B.Success" : "B.Warn");
        PubValidation.Text = errors.Count == 0
            ? "Поля и группы tags валидны. Внешняя видимость зависит от NAT/firewall и регистрации в Steam master."
            : string.Join(Environment.NewLine, errors);
    }

    private List<string> ValidatePublication()
    {
        var errors = new List<string>();
        var hostname = In_Hostname.Text.Trim();
        if (hostname.Length is < 1 or > 200) errors.Add("Название: требуется 1–200 символов.");
        foreach (var (label, text) in new[] { ("URL", In_Url.Text), ("Header", In_Header.Text), ("Logo", In_Logo.Text) })
            if (!string.IsNullOrWhiteSpace(text) &&
                (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
                 || (uri.Scheme != "http" && uri.Scheme != "https")))
                errors.Add($"{label}: нужен абсолютный http/https URL.");

        var tags = In_Tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "monthly", "biweekly", "weekly", "vanilla", "hardcore", "softcore", "pve", "roleplay",
            "creative", "minigame", "training", "battlefield", "broyale", "builds", "tut", "premium",
            "NA", "SA", "EU", "WA", "EA", "OC", "AF"
        };
        var invalid = tags.Where(t => !allowed.Contains(t)).ToArray();
        if (invalid.Length > 0) errors.Add("Неизвестные tags: " + string.Join(", ", invalid));
        if (tags.Count(t => new[] { "monthly", "biweekly", "weekly" }.Contains(t, StringComparer.OrdinalIgnoreCase)) > 1)
            errors.Add("Tags: выберите только один график вайпа.");
        if (tags.Count(t => new[] { "vanilla", "hardcore", "softcore" }.Contains(t, StringComparer.OrdinalIgnoreCase)) > 1)
            errors.Add("Tags: выберите только одну сложность.");
        if (tags.Length > 4) errors.Add("В server browser отображаются только первые четыре тега.");
        if (new[] { App.Settings.GamePort, App.Settings.QueryPort, App.Settings.RconPort }.Distinct().Count() != 3)
            errors.Add("Game, Query и RCON порты должны различаться.");
        var domain = In_Domain.Text.Trim();
        if (domain.Length > 0 && !Uri.CheckHostName(domain).Equals(UriHostNameType.Dns))
            errors.Add("Домен: нужен вид rust.example.com (A-запись, без http:// и порта).");
        return errors;
    }

    private static string PortState(HashSet<int> ports, int port)
        => ports.Contains(port) ? $"{port} OK" : $"{port} НЕ СЛУШАЕТ";

    // Встроенный гайд по DNS (сокращённая версия Docs/DOMAIN.md в репозитории)
    private void DomainGuide_Click(object s, RoutedEventArgs e)
        => new InfoWindow("Свой домен для сервера", DomainGuideText, Window.GetWindow(this)).Show();

    private const string DomainGuideText =
@"Rust умеет подключаться к серверу по домену через DNS SRV-запись
(wiki.facepunch.com/rust/dns-records). Нужны ДВЕ записи у вашего
DNS-провайдера (Cloudflare, GoDaddy, reg.ru и т.п.):

  1) A-запись:    rust.example.com  ->  IPv4 сервера
  2) SRV-запись:  _rust._udp.<имя A-записи>  ->  target: rust.example.com,
                  port: ваш server.port

ПРИМЕР ДЛЯ CLOUDFLARE  (DNS -> Records -> Add record):

  • Type A:    Name = rust, IPv4 = ваш IP,
               Proxy status = DNS only  <-- важно, серое облако!
  • Type SRV:  Service = _rust, Proto = _udp, Name = rust,
               Priority = 0, Weight = 0,
               Port = ваш server.port, Target = rust.example.com

ВАЖНО:
  • Оранжевое облако (проксирование Cloudflare) у A-записи ВЫКЛЮЧИТЬ —
    игровой трафик Rust не HTTP и через HTTP-прокси не пойдёт.
  • Туннель cloudflared для игрового UDP-трафика не нужен:
    достаточно обычных DNS-записей.
  • На сервере должен быть открыт/проброшен UDP-порт server.port
    (и query-порт для списка серверов).

ПОСЛЕ НАСТРОЙКИ:
  • Подключение из консоли клиента (F1):
        client.connect rust.example.com
    Порт указывать не нужно — SRV-запись отдаёт его сама.
  • Впишите домен в поле слева — RSM пропишет
    server.favoritesEndpoint в server.cfg: игроки, добавившие
    сервер в избранное, найдут его даже после смены IP/порта.
  • На дашборде строка «client.connect» начнёт показывать домен.

ПРОВЕРКА ЗАПИСЕЙ (cmd/PowerShell):
    nslookup -type=SRV _rust._udp.rust.example.com
    Resolve-DnsName -Type SRV _rust._udp.rust.example.com";

    private static async Task<string> QueryA2s(int port)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(IPAddress.Loopback, port);
            var request = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, 0x54 };
            request.AddRange(Encoding.ASCII.GetBytes("Source Engine Query\0"));
            await udp.SendAsync(request.ToArray());
            var result = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var data = result.Buffer;
            if (data.Length >= 9 && data[4] == 0x41)
            {
                request.AddRange(data.Skip(5).Take(4));
                await udp.SendAsync(request.ToArray());
                data = (await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2))).Buffer;
            }
            if (data.Length < 7 || data[4] != 0x49) return $"A2S НЕОЖИДАННЫЙ ({port})";
            var index = 6;
            var end = Array.IndexOf(data, (byte)0, index);
            var name = end > index ? Encoding.UTF8.GetString(data, index, end - index) : "Rust";
            return $"A2S OK {port} ({name})";
        }
        catch { return $"A2S НЕТ ОТВЕТА {port}"; }
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
