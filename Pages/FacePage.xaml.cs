using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OctavianRSM.Services;

namespace OctavianRSM.Pages;

// Страница «Лицо сервера»: конструктор названия по паттернам топовых серверов
// + маркетинг (описание/URL/картинки/теги/домен) + живой превью карточки
public partial class FacePage : Page
{
    private bool _faceReady;
    // живые числа из serverinfo — превью показывает реальный онлайн/карту
    private int _statPlayers, _statMax;
    private string _statMap = "";
    private readonly CancellationTokenLike _tail = new();
    private string? _connectAddr;

    public FacePage()
    {
        InitializeComponent();
        var s = App.Settings;
        TxtSub.Text = s.IsRemoteMode
            ? $"{s.Hostname} • удалённый RCON {s.RconHost}:{s.RconPort}"
            : $"{s.Hostname} • {s.ServerDirPath}";
        InitFace();
        Unloaded += (_, _) => _tail.Cancel();
        _ = DetectConnect();
        _ = LoopStats();
    }

    private async Task DetectConnect()
    {
        var info = await ServerConnect.DetectAsync();
        _connectAddr = info.Address;
    }

    // онлайн/карта для живого превью — лёгкий опрос serverinfo, пока страница на экране
    private async Task LoopStats()
    {
        while (!_tail.Cancelled)
        {
            try
            {
                if (App.Rcon.IsConnected)
                {
                    var raw = await App.Rcon.SendAsync("serverinfo");
                    Dispatcher.Invoke(() => ApplyFaceStats(raw));
                }
            }
            catch { }
            await Task.Delay(10000);
        }
    }

    private void ApplyFaceStats(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            _statPlayers = r.TryGetProperty("Players", out var p) ? p.GetInt32() : 0;
            _statMax = r.TryGetProperty("MaxPlayers", out var m) ? m.GetInt32() : 0;
            _statMap = r.TryGetProperty("Map", out var mp) ? mp.GetString() ?? "" : "";
            UpdateFacePreview();
        }
        catch { }
    }

    private void InitFace()
    {
        var s = App.Settings;
        // конструктор названия
        InTitleRegion.Text = s.TitleRegion;
        InTitleBase.Text = s.TitleBase;
        ChkTitleRates.IsChecked = s.TitleShowRates;
        InTitleRates.Text = s.TitleRatesCustom;
        InTitleRates.PlaceholderText = $"авто: {ServerTitleBuilder.RatesText(s)}";
        InTitleFeatures.Text = string.Join(", ", s.TitleFeatures);
        InTitleWipeCustom.Text = s.TitleWipeCustom;
        ChkTitleAuto.IsChecked = s.TitleAuto;
        SelectComboTag(CbTitleTeam, s.TitleTeamMode);
        SelectComboTag(CbTitleWipe, s.TitleWipeMode);
        foreach (var p in ServerTitleBuilder.Presets)
        {
            var b = new Wpf.Ui.Controls.Button
            {
                Content = p.Name,
                Tag = p,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = p.Sample,
            };
            b.Click += TitlePreset_Click;
            PnlTitlePresets.Children.Add(b);
        }
        App.ServerCfg.Load();
        InTeamLimit.Text = ServerTitleBuilder.TeamLimit(s, App.ServerCfg).ToString();

        // маркетинг — из живого server.cfg с фолбэком на настройки
        string Val(string key, string fallback) =>
            string.IsNullOrWhiteSpace(App.ServerCfg[key]) ? fallback : App.ServerCfg[key];
        InFaceDesc.Text = Val("server.description", s.Description);
        InFaceUrl.Text = App.ServerCfg["server.url"];
        InFaceTags.Text = App.ServerCfg["server.tags"];
        InFaceHeader.Text = App.ServerCfg["server.headerimage"];
        InFaceLogo.Text = App.ServerCfg["server.logoimage"];
        InFaceDomain.Text = Val("server.favoritesendpoint", s.ServerDomain);

        _faceReady = true;
        UpdateTitlePreview();
        LoadPreviewImages();
        _ = RefreshTeamLimitAsync(); // живое maxteamsize точнее настроек
    }

    // maxteamsize — «Generated» convar: в server.cfg его может не быть,
    // поэтому при живом RCON читаем реальное значение и синкаем в настройки
    private async Task RefreshTeamLimitAsync()
    {
        if (!App.Rcon.IsConnected) return;
        try
        {
            var raw = (await App.Rcon.SendAsync("relationshipmanager.maxteamsize")).Trim();
            if (raw.StartsWith('[')) return;
            // ответ «maxteamsize "8"» или просто «8» — берём последнее число
            var digits = System.Text.RegularExpressions.Regex.Match(raw, @"(\d+)(?!.*\d)");
            if (!digits.Success || !int.TryParse(digits.Value, out var n)) return;
            Dispatcher.Invoke(() =>
            {
                App.Settings.TeamSize = n;
                InTeamLimit.Text = n.ToString();
                UpdateTitlePreview();
            });
        }
        catch { }
    }

    private static void SelectComboTag(ComboBox cb, string tag)
    {
        foreach (var it in cb.Items.OfType<ComboBoxItem>())
            if (Equals(it.Tag?.ToString(), tag)) { cb.SelectedItem = it; return; }
        cb.SelectedIndex = 0;
    }

    private static string ComboTag(ComboBox cb, string fallback)
        => (cb.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    // Любое изменение частей → сохранить в настройки + пересобрать предпросмотр
    private void TitlePart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_faceReady) return;
        var s = App.Settings;
        s.TitleRegion = InTitleRegion.Text.Trim();
        s.TitleBase = InTitleBase.Text.Trim();
        s.TitleShowRates = ChkTitleRates.IsChecked == true;
        s.TitleRatesCustom = InTitleRates.Text.Trim();
        s.TitleTeamMode = ComboTag(CbTitleTeam, "auto");
        s.TitleWipeMode = ComboTag(CbTitleWipe, "auto");
        s.TitleWipeCustom = InTitleWipeCustom.Text.Trim();
        s.TitleFeatures = InTitleFeatures.Text
            .Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        s.TitleAuto = ChkTitleAuto.IsChecked == true;
        s.Save(App.SettingsPath);
        InTitleWipeCustom.IsEnabled = s.TitleWipeMode == "custom";
        UpdateTitlePreview();
    }

    private void UpdateTitlePreview()
    {
        var title = ServerTitleBuilder.Build(App.Settings, App.ServerCfg);
        TxtTitlePreview.Text = title.Length > 0 ? title : "(пусто)";
        TxtTitleLen.Text = $"{title.Length} симв.";
        TxtTitleLen.Foreground = (Brush)FindResource(title.Length > 200 ? "B.Danger" : "B.TextDim");
        UpdateFacePreview();
    }

    // ── Живой превью карточки подключения (правая колонка) ────────────────

    private void UpdateFacePreview()
    {
        if (PnlFaceTagsChips == null) return; // InitializeComponent ещё не отработал
        // чипы тегов как в браузере серверов
        PnlFaceTagsChips.Children.Clear();
        var tags = InFaceTags.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(4).ToArray();
        if (tags.Length == 0) tags = new[] { "теги" };
        foreach (var t in tags)
        {
            PnlFaceTagsChips.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x38)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 4),
                Child = new TextBlock { Text = t.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold,
                                        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAD)) }
            });
        }
        TxtFaceDesc.Text = string.IsNullOrWhiteSpace(InFaceDesc.Text)
            ? "server.description — описание в списке серверов"
            : InFaceDesc.Text;
        TxtFaceSite.Text = string.IsNullOrWhiteSpace(InFaceUrl.Text)
            ? "ВНЕШНИЙ САЙТ" : $"🔗 {InFaceUrl.Text.Trim()}";
        var s = App.Settings;
        TxtFacePlayers.Text = _statPlayers > 0 || _statMax > 0 ? $"{_statPlayers}/{_statMax}" : $"0/{s.MaxPlayers}";
        TxtFaceMap.Text = !string.IsNullOrEmpty(_statMap) ? _statMap
            : string.IsNullOrWhiteSpace(s.LevelUrl) ? $"Procedural {s.WorldSize}" : "Custom";
        TxtFaceWipe.Text = ServerTitleBuilder.WipeText(s) is { Length: > 0 } w ? w : "—";
    }

    // текстовые поля маркетинга → мгновенный превью + дебаунс на картинки
    private int _imgSeq;
    private async void FaceField_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_faceReady) return;
        UpdateFacePreview();
        var seq = ++_imgSeq;
        await Task.Delay(700);
        if (seq != _imgSeq) return; // юзер ещё печатает — ждём паузы
        LoadPreviewImages();
    }

    private void LoadPreviewImages()
    {
        ImgFaceHeader.Source = TryImage(InFaceHeader.Text, ImgFaceHeader);
        TxtHeaderPlaceholder.Visibility = ImgFaceHeader.Source == null ? Visibility.Visible : Visibility.Collapsed;
        var logo = TryImage(InFaceLogo.Text, ImgFaceLogo);
        ImgFaceLogo.Source = logo;
        ImgFaceLogo.Visibility = logo == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // BitmapImage по URL: null при невалидной ссылке; при фейле скачивания
    // сбрасываем Source, чтобы плейсхолдер вернулся
    private static System.Windows.Media.Imaging.BitmapImage? TryImage(string url, Image target)
    {
        try
        {
            url = url.Trim();
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")) return null;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DownloadFailed += (_, _) =>
            {
                if (ReferenceEquals(target.Source, bmp)) target.Source = null;
            };
            bmp.EndInit();
            return bmp;
        }
        catch { return null; }
    }

    private async void FaceConnect_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var addr = _connectAddr ??= (await ServerConnect.DetectAsync()).Address;
        try { ServerConnect.OpenSteamUrl(await ServerConnect.SteamUrlAsync(addr)); }
        catch (Exception ex) { FaceValidation.Text = $"Не удалось вызвать Steam: {ex.Message}"; }
    }

    private void TitlePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ServerTitleBuilder.TitlePreset p) return;
        _faceReady = false;
        p.Fill(App.Settings);
        App.Settings.TitleRatesCustom = ""; // пресет = честные авто-рейты, кастом сбрасываем
        InTitleRegion.Text = App.Settings.TitleRegion;
        ChkTitleRates.IsChecked = App.Settings.TitleShowRates;
        InTitleRates.Text = "";
        InTitleFeatures.Text = string.Join(", ", App.Settings.TitleFeatures);
        InTitleWipeCustom.Text = App.Settings.TitleWipeCustom;
        SelectComboTag(CbTitleTeam, App.Settings.TitleTeamMode);
        SelectComboTag(CbTitleWipe, App.Settings.TitleWipeMode);
        _faceReady = true;
        App.Settings.Save(App.SettingsPath);
        UpdateTitlePreview();
    }

    private async void TitleApply_Click(object sender, RoutedEventArgs e)
    {
        var title = await ServerTitleBuilder.ApplyAsync(App.Settings, App.ServerCfg, rcon: true);
        TxtSub.Text = App.Settings.IsRemoteMode
            ? $"{title} • удалённый RCON {App.Settings.RconHost}:{App.Settings.RconPort}"
            : $"{title} • {App.Settings.ServerDirPath}";
        TxtTitleStatus.Text = App.Rcon.IsConnected
            ? "✓ название применено (cfg + RCON)"
            : "✓ записано в server.cfg — применится при рестарте";
    }

    // Лимит пати: relationshipmanager.maxteamsize → cfg+RCON, затем пересбор названия
    private async void TeamApply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(InTeamLimit.Text, out var n) || n < 0 || n > 128)
        { TxtTitleStatus.Text = "лимит пати: число 0–128"; return; }
        App.Settings.TeamSize = n;
        App.Settings.Save(App.SettingsPath);
        App.ServerCfg.Load();
        App.ServerCfg["relationshipmanager.maxteamsize"] = n.ToString();
        App.ServerCfg.Save();
        if (App.Rcon.IsConnected)
            await App.Rcon.SendAsync($"relationshipmanager.maxteamsize {n}");
        UpdateTitlePreview();
        TxtTitleStatus.Text = $"✓ лимит пати = {(n == 0 ? "тимы выкл." : n.ToString())}";
        if (App.Settings.TitleAuto)
            TitleApply_Click(sender, e);
    }

    // ── Маркетинг ────────────────────────────────────────────────────────

    private async void FaceSave_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.ServerCfg;
        cfg.Load();
        cfg["server.description"] = InFaceDesc.Text.Trim();
        cfg["server.url"] = InFaceUrl.Text.Trim();
        cfg["server.tags"] = InFaceTags.Text.Trim();
        cfg["server.headerimage"] = InFaceHeader.Text.Trim();
        cfg["server.logoimage"] = InFaceLogo.Text.Trim();
        cfg["server.favoritesendpoint"] = InFaceDomain.Text.Trim().ToLowerInvariant();
        cfg.Save();

        App.Settings.Description = InFaceDesc.Text.Trim();
        App.Settings.ServerDomain = InFaceDomain.Text.Trim().ToLowerInvariant();
        App.Settings.ServerUrl = InFaceUrl.Text.Trim();
        App.Settings.HeaderImage = InFaceHeader.Text.Trim();
        App.Settings.LogoImage = InFaceLogo.Text.Trim();
        App.Settings.Save(App.SettingsPath);

        if (App.Rcon.IsConnected)
        {
            await App.Rcon.SendAsync($"server.description \"{InFaceDesc.Text.Trim().Replace("\"", "'")}\"");
            await App.Rcon.SendAsync("server.readcfg");
            FaceValidation.Text = "✓ сохранено в server.cfg + readcfg";
        }
        else FaceValidation.Text = "✓ сохранено в server.cfg — применится при рестарте";
        FaceValidation.Foreground = (Brush)FindResource("B.Success");
        LoadPreviewImages();
        UpdateFacePreview();
    }

    // Авто-теги: регион + график вайпа + режим — словарь Steam ограничен
    private void FaceAutoTags_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.TitleRegion)) tags.Add(s.TitleRegion.Trim().ToUpperInvariant());
        var wipe = s.AutoWipeEnabled && s.AutoWipeIntervalDays > 0
            ? (s.AutoWipeIntervalDays <= 7 ? "weekly" : s.AutoWipeIntervalDays <= 14 ? "biweekly" : "monthly")
            : (s.WipeSchedule ?? "").ToLowerInvariant();
        if (wipe is "weekly" or "biweekly" or "monthly") tags.Add(wipe);
        var mode = s.ServerPve ? "pve" : (s.Gamemode ?? "vanilla").ToLowerInvariant();
        var allowedModes = new HashSet<string> { "vanilla", "hardcore", "softcore", "pve", "roleplay",
            "creative", "minigame", "training", "battlefield", "broyale", "builds" };
        if (allowedModes.Contains(mode) && mode != "vanilla") tags.Add(mode); // vanilla = дефолт, тег не тратим
        InFaceTags.Text = string.Join(",", tags);
    }

    private async void FacePublication_Click(object s, RoutedEventArgs e)
    {
        var errors = FaceValidate();
        var ip = IPGlobalProperties.GetIPGlobalProperties();
        var udpPorts = ip.GetActiveUdpListeners().Select(p => p.Port).ToHashSet();
        var tcpPorts = ip.GetActiveTcpListeners().Select(p => p.Port).ToHashSet();
        var query = await QueryA2s(App.Settings.QueryPort);
        FacePortState.Text = $"Game UDP {PortState(udpPorts, App.Settings.GamePort)} • Query {query} • " +
            $"RCON TCP {PortState(tcpPorts, App.Settings.RconPort)} • RCON {(App.Rcon.IsConnected ? "ON" : "OFF")}";
        FacePortState.Foreground = (Brush)FindResource(
            errors.Count == 0 && App.Rcon.IsConnected && query.StartsWith("A2S OK") ? "B.Success" : "B.Warn");
        FaceValidation.Foreground = (Brush)FindResource("B.Warn");
        FaceValidation.Text = errors.Count == 0
            ? "Поля и группы tags валидны. Внешняя видимость зависит от NAT/firewall и регистрации в Steam master."
            : string.Join(Environment.NewLine, errors);
    }

    private List<string> FaceValidate()
    {
        var errors = new List<string>();
        var hostname = TxtTitlePreview.Text.Trim();
        if (hostname.Length is < 1 or > 200) errors.Add("Название: требуется 1–200 символов.");
        foreach (var (label, text) in new[] { ("URL", InFaceUrl.Text), ("Header", InFaceHeader.Text), ("Logo", InFaceLogo.Text) })
            if (!string.IsNullOrWhiteSpace(text) &&
                (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
                 || (uri.Scheme != "http" && uri.Scheme != "https")))
                errors.Add($"{label}: нужен абсолютный http/https URL.");
        var tags = InFaceTags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
        var domain = InFaceDomain.Text.Trim();
        if (domain.Length > 0 && !Uri.CheckHostName(domain).Equals(UriHostNameType.Dns))
            errors.Add("Домен: нужен вид rust.example.com (A-запись, без http:// и порта).");
        return errors;
    }

    private static string PortState(HashSet<int> ports, int port)
        => ports.Contains(port) ? $"{port} OK" : $"{port} НЕ СЛУШАЕТ";

    private void FaceDomainGuide_Click(object s, RoutedEventArgs e)
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
}
