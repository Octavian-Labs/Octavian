using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OctavianRSM.Models;
using OctavianRSM.Services;

namespace OctavianRSM;

// Мастер первого запуска: Local (скачать и настроить сервер рядом с EXE)
// или Remote (только RCON). Загрузка сервера идёт в фоне, пока пользователь
// проходит шаги настройки — на последнем шаге обычно остаётся только дождаться.
public partial class SetupWizardWindow : Window
{
    private const int PageMode = 0, PageProfile = 1, PageBasics = 2,
                      PageWorld = 3, PageInstall = 4, PageRemote = 5;

    private readonly RsmSettings _s;
    private readonly BootstrapService _boot;
    private CancellationTokenSource _cts = new();
    private Task<bool>? _chain;
    private int _step;
    private bool _chainDone, _chainOk;
    private List<ProfileCardVm> _profileCards = new();

    public SetupWizardWindow(RsmSettings settings, bool hadSettings)
    {
        InitializeComponent();
        _s = settings;
        _boot = new BootstrapService(settings);

        // Карточки профилей с точной настройкой: поля предзаполнены пресетом
        // (для "custom" — текущими настройками), при "Далее" применяются всегда.
        _profileCards = ServerProfiles.All.Select(p => new ProfileCardVm(p, _s)).ToList();
        LstProfiles.ItemsSource = _profileCards;
        LstProfiles.SelectedItem =
            _profileCards.FirstOrDefault(c => c.Profile.Key == _s.ServerProfile) ?? _profileCards[0];

        TxtHostname.Text = _s.Hostname;
        TxtIdentity.Text = _s.Identity;
        TxtDescription.Text = _s.Description;
        TxtGamePort.Text = _s.GamePort.ToString();
        TxtQueryPort.Text = _s.QueryPort.ToString();
        TxtRconPort.Text = _s.RconPort.ToString();
        TxtRconPassword.Text = _s.RconPassword;
        TxtMaxPlayers.Text = _s.MaxPlayers.ToString();
        TxtWorldSize.Text = _s.WorldSize.ToString();
        TxtSeed.Text = _s.WorldSeed.ToString();
        TxtExtraTags.Text = _s.ExtraTags;
        TxtRemoteHost.Text = _s.RconHost;
        TxtRemotePort.Text = _s.RconPort.ToString();
        TxtRemotePassword.Text = _s.RconPassword;
        ChkCarbon.IsChecked = _s.InstallCarbon;

        var serverMark = File.Exists(_s.ServerExePath) ? "✔ найден" : "✘ отсутствует";
        var steamMark = File.Exists(_s.SteamCmdExePath) ? "✔ найден" : "✘ отсутствует";
        TxtContext.Text =
            $"Папка программы: {_s.RootDir}\n" +
            $"RustDedicated.exe: {serverMark}   •   SteamCMD: {steamMark}" +
            (hadSettings ? "\nНайдена прежняя конфигурация — настройки подставлены." : "");

        Closing += (_, e) =>
        {
            if (DialogResult == true) return;
            if (!ConfirmCancel()) { e.Cancel = true; return; }
            _cts.Cancel();
        };
        ShowStep(PageMode);
    }

    // ── Навигация ───────────────────────────────────────────────────────────
    private void ShowStep(int step)
    {
        _step = step;
        StepMode.Visibility = step == PageMode ? Visibility.Visible : Visibility.Collapsed;
        StepProfile.Visibility = step == PageProfile ? Visibility.Visible : Visibility.Collapsed;
        StepBasics.Visibility = step == PageBasics ? Visibility.Visible : Visibility.Collapsed;
        StepWorld.Visibility = step == PageWorld ? Visibility.Visible : Visibility.Collapsed;
        StepInstall.Visibility = step == PageInstall ? Visibility.Visible : Visibility.Collapsed;
        StepRemote.Visibility = step == PageRemote ? Visibility.Visible : Visibility.Collapsed;

        TxtStepTitle.Text = step switch
        {
            PageMode => "Выбор режима",
            PageProfile => "Профиль сервера",
            PageBasics => "Основные параметры",
            PageWorld => "Мир и вайпы",
            PageInstall => "Загрузка и установка",
            PageRemote => "Удалённый сервер (RCON)",
            _ => ""
        };
        TxtStepNo.Text = step is PageMode or PageRemote ? "" : $"ШАГ {step} / 4";
        BtnBack.Visibility = step is > PageMode and not PageRemote ? Visibility.Visible
                           : step == PageRemote ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.IsEnabled = step != PageInstall || _chainDone; // не дёргаем шаги во время записи конфигов
        BtnNext.Visibility = step == PageMode ? Visibility.Collapsed : Visibility.Visible;
        BtnNext.Content = step is PageInstall or PageRemote ? "ГОТОВО" : "ДАЛЕЕ →";
        // мини-индикатор загрузки виден на всех шагах, кроме «Установка» —
        // там свой большой прогресс-бар с логом
        PnlMiniProgress.Visibility = _chain != null && step != PageInstall
            ? Visibility.Visible : Visibility.Collapsed;
        if (step == PageWorld) SyncWorldStep(); // пресет/карточка могли сменить мир и игроков
        if (step == PageInstall) UpdateInstallButtons();
        if (step == PageRemote) BtnNext.IsEnabled = true;
    }

    // Шаг «Мир и вайпы» перечитываем из настроек: карточка профиля могла
    // переписать MaxPlayers/WorldSize/WipeSchedule после инициализации полей
    private void SyncWorldStep()
    {
        TxtMaxPlayers.Text = _s.MaxPlayers.ToString();
        TxtWorldSize.Text = _s.WorldSize.ToString();
        TxtSeed.Text = _s.WorldSeed.ToString();
        TxtExtraTags.Text = _s.ExtraTags;
        SelectComboByContent(CmbLevel, _s.Level);
        SelectComboByContent(CmbWipe, _s.WipeSchedule);
    }

    private static void SelectComboByContent(ComboBox cmb, string value)
    {
        foreach (ComboBoxItem it in cmb.Items)
            if (it.Content?.ToString() == value) { cmb.SelectedItem = it; return; }
        // значения нет в списке — добавляем, чтобы не затирать конфиг дефолтом
        var extra = new ComboBoxItem { Content = value };
        cmb.Items.Add(extra);
        cmb.SelectedItem = extra;
    }

    private void UpdateInstallButtons()
    {
        BtnNext.IsEnabled = _chainDone && _chainOk;
        BtnNext.Content = _chainDone ? "ГОТОВО" : "ОЖИДАНИЕ…";
        BtnRetry.Visibility = _chainDone && !_chainOk ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.IsEnabled = _chainDone;
    }

    // ── Шаг 0: выбор режима ────────────────────────────────────────────────
    private void ModeLocal_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        StartDownloadChain();
        ShowStep(PageProfile);
    }

    private void ModeRemote_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => ShowStep(PageRemote);

    // ── Фоновая цепочка: SteamCMD → Rust Dedicated ─────────────────────────
    private void StartDownloadChain()
    {
        if (_chain != null) return;
        var progress = new Progress<BootProgress>(OnProgress);
        var ct = _cts.Token;
        _chain = Task.Run(async () =>
        {
            if (!await _boot.EnsureSteamCmdAsync(progress, ct)) return false;
            return await _boot.InstallRustAsync(progress, ct);
        });
        _chain.ContinueWith(t =>
        {
            _chainDone = true;
            _chainOk = t.Status == TaskStatus.RanToCompletion && t.Result;
            Dispatcher.Invoke(() =>
            {
                if (_chainOk) TxtPhase.Text = "Загрузка завершена — можно завершать настройку";
                else TxtPhase.Text = t.Status == TaskStatus.Canceled || _cts.IsCancellationRequested
                    ? "Загрузка отменена" : "Ошибка загрузки — нажмите ПОВТОРИТЬ";
                PbInstall.IsIndeterminate = false;
                if (_chainOk) PbInstall.Value = 100;
                PbMini.IsIndeterminate = false;
                if (_chainOk)
                {
                    PbMini.Value = 100;
                    TxtMiniPct.Text = "100%";
                    TxtMiniPhase.Text = "Загрузка завершена — продолжайте настройку";
                    TxtMiniPhase.Foreground = (Brush)FindResource("B.Success");
                }
                else
                {
                    TxtMiniPhase.Text = "Загрузка не удалась — детали и ПОВТОРИТЬ на шаге «Установка»";
                    TxtMiniPhase.Foreground = (Brush)FindResource("B.Danger");
                }
                UpdateInstallButtons();
            });
        });
    }

    private void OnProgress(BootProgress p)
    {
        TxtPhase.Text = $"{p.Phase}: {p.Detail}";
        // мини-полоса: деталь обрезаем — steamcmd пишет длинные строки прогресса
        var detail = p.Detail.Length > 70 ? p.Detail[..70] + "…" : p.Detail;
        TxtMiniPhase.Text = $"{p.Phase}: {detail}";
        if (p.Percent is double pct)
        {
            PbInstall.IsIndeterminate = false;
            PbInstall.Value = Math.Clamp(pct, 0, 100);
            TxtPct.Text = $"{pct:0}%";
            PbMini.IsIndeterminate = false;
            PbMini.Value = Math.Clamp(pct, 0, 100);
            TxtMiniPct.Text = $"{pct:0}%";
        }
        var line = $"[{p.Phase}] {p.Detail}\n";
        TxtInstallLog.Text += line;
        if (TxtInstallLog.Text.Length > 60_000)
            TxtInstallLog.Text = TxtInstallLog.Text[^40_000..];
        LogScroll.ScrollToEnd();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_chain is { IsCompleted: false }) return;
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _chain = null; _chainDone = false; _chainOk = false;
        PbInstall.IsIndeterminate = true;
        TxtPhase.Text = "Повторная попытка…";
        StartDownloadChain();
        UpdateInstallButtons();
    }

    // ── Далее/Назад ─────────────────────────────────────────────────────────
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ShowStep(_step switch
        {
            PageProfile or PageRemote => PageMode,
            PageBasics => PageProfile,
            PageWorld => PageBasics,
            PageInstall => PageWorld,
            _ => PageMode
        });
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case PageProfile:
                if (LstProfiles.SelectedItem is ProfileCardVm card)
                    ApplyProfileCard(card);
                ShowStep(PageBasics);
                break;
            case PageBasics:
                if (!ValidateBasics()) return;
                ShowStep(PageWorld);
                break;
            case PageWorld:
                if (!ValidateWorld()) return;
                ShowStep(PageInstall);
                break;
            case PageRemote:
                FinishRemote();
                break;
            case PageInstall:
                await FinishLocalAsync();
                break;
        }
    }

    // ── Шаг 1: карточки профилей ────────────────────────────────────────────

    // Раскрытие карточки = интерес к этому профилю: делаем её выбранной,
    // иначе правки полей внутри уйдут вникуда — применяется SelectedItem.
    private void ProfileCard_Expanded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProfileCardVm card)
            LstProfiles.SelectedItem = card;
    }

    // Пресет + точные значения из полей карточки. Поля предзаполнены пресетом,
    // поэтому применяем их всегда: правки пользователя — это и есть итоговый конфиг.
    private void ApplyProfileCard(ProfileCardVm card)
    {
        var p = card.Profile;
        ServerProfiles.Apply(p, _s);
        double Rate(string s, double fb) => double.TryParse(s, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fb;
        _s.GatherRate = Rate(card.Gather, _s.GatherRate);
        _s.StackMultiplier = Rate(card.Stack, _s.StackMultiplier);
        _s.CraftTimescale = Rate(card.Craft, _s.CraftTimescale);
        _s.ServerPve = card.Pve;
        if (int.TryParse(card.MaxPlayers, out var mp) && mp is >= 1 and <= 500)
            _s.MaxPlayers = mp;
        if (int.TryParse(card.WorldSize, out var ws) && ws is >= 1000 and <= 6000)
            _s.WorldSize = ws;
    }

    // Модель карточки профиля: пресет + редактируемые поля точной настройки.
    // Для "custom" поля предзаполняются текущими настройками.
    private sealed class ProfileCardVm
    {
        public ProfileCardVm(ServerProfile p, RsmSettings s)
        {
            Profile = p;
            var custom = p.Key == "custom";
            var inv = CultureInfo.InvariantCulture;
            Gather = (custom || p.Gather <= 0 ? s.GatherRate : p.Gather).ToString("0.#", inv);
            Stack = (custom || p.Stack <= 0 ? s.StackMultiplier : p.Stack).ToString("0.#", inv);
            Craft = (custom || p.CraftTimescale <= 0 ? s.CraftTimescale : p.CraftTimescale).ToString("0.#", inv);
            Pve = custom ? s.ServerPve : p.Pve;
            MaxPlayers = (custom || p.MaxPlayers <= 0 ? s.MaxPlayers : p.MaxPlayers).ToString();
            WorldSize = (custom || p.WorldSize <= 0 ? s.WorldSize : p.WorldSize).ToString();
        }

        public ServerProfile Profile { get; }
        public string Title => Profile.Title;
        public string Subtitle => Profile.Subtitle;
        public string Gather { get; set; }
        public string Stack { get; set; }
        public string Craft { get; set; }
        public bool Pve { get; set; }
        public string MaxPlayers { get; set; }
        public string WorldSize { get; set; }
    }

    // ── Валидация ───────────────────────────────────────────────────────────
    private bool ValidateBasics()
    {
        var host = TxtHostname.Text.Trim();
        var ident = TxtIdentity.Text.Trim();
        if (host.Length == 0) return Warn("Укажите название сервера.");
        if (ident.Length == 0 || ident.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Warn("Identity — только допустимые символы имени папки.");
        if (!int.TryParse(TxtGamePort.Text, out var gp) || gp is < 1024 or > 65535)
            return Warn("Game port: 1024–65535.");
        if (!int.TryParse(TxtQueryPort.Text, out var qp) || qp is < 1024 or > 65535)
            return Warn("Query port: 1024–65535.");
        if (!int.TryParse(TxtRconPort.Text, out var rp) || rp is < 1024 or > 65535)
            return Warn("RCON port: 1024–65535.");
        if (gp == qp || gp == rp || qp == rp)
            return Warn("Порты должны быть разными.");
        if (TxtRconPassword.Text.Length < 8)
            return Warn("RCON пароль — минимум 8 символов.");
        _s.Hostname = host; _s.Identity = ident;
        _s.Description = TxtDescription.Text.Trim();
        _s.GamePort = gp; _s.QueryPort = qp; _s.RconPort = rp;
        _s.RconPassword = TxtRconPassword.Text;
        _s.InstallCarbon = ChkCarbon.IsChecked == true;
        return true;
    }

    private bool ValidateWorld()
    {
        if (!int.TryParse(TxtWorldSize.Text, out var ws) || ws is < 1000 or > 6000)
            return Warn("Размер мира: 1000–6000.");
        if (!uint.TryParse(TxtSeed.Text, out var seed))
            return Warn("Seed — целое число 0–4294967295.");
        if (!int.TryParse(TxtMaxPlayers.Text, out var mp) || mp is < 1 or > 500)
            return Warn("Макс. игроков: 1–500.");
        _s.WorldSize = ws; _s.WorldSeed = seed; _s.MaxPlayers = mp;
        _s.Level = (CmbLevel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Procedural Map";
        _s.WipeSchedule = (CmbWipe.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "weekly";
        _s.ExtraTags = TxtExtraTags.Text.Trim();
        return true;
    }

    private static bool Warn(string msg)
    {
        MessageBox.Show(msg, "OctavianRSM — проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    // ── Завершение ──────────────────────────────────────────────────────────
    private void FinishRemote()
    {
        var host = TxtRemoteHost.Text.Trim();
        if (host.Length == 0) { Warn("Укажите хост удалённого сервера."); return; }
        if (!int.TryParse(TxtRemotePort.Text, out var port) || port is < 1 or > 65535)
        { Warn("RCON port: 1–65535."); return; }
        _s.DeploymentMode = "Remote";
        _s.RconHost = host;
        _s.RconPort = port;
        _s.RconPassword = TxtRemotePassword.Text;
        _s.SetupCompleted = true;
        DialogResult = true;
    }

    private async Task FinishLocalAsync()
    {
        BtnNext.IsEnabled = false;
        var progress = new Progress<BootProgress>(OnProgress);
        try
        {
            TxtPhase.Text = "Запись конфигурации…";
            await _boot.WriteConfigurationAsync(_cts.Token);
            if (_s.InstallCarbon)
            {
                var ok = await _boot.InstallCarbonAsync(progress, _cts.Token);
                if (!ok) { TxtPhase.Text = "Carbon не установлен — можно доустановить позже из вкладки «Установка»"; }
                if (ok && ChkPlugins.IsChecked == true)
                {
                    var n = _boot.ExtractBaselinePlugins();
                    OnProgress(new("Плагины", $"Установлено базовых плагинов: {n}", 100));
                }
            }
            _s.DeploymentMode = "Local";
            _s.SetupCompleted = true;
            DialogResult = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TxtPhase.Text = "Ошибка: " + ex.Message;
            BtnNext.IsEnabled = true;
        }
    }

    // ── Кнопки формы ────────────────────────────────────────────────────────
    private void GenPass_Click(object sender, RoutedEventArgs e)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        var buf = new char[20];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        TxtRconPassword.Text = new string(buf);
    }

    private void SeedRandom_Click(object sender, RoutedEventArgs e)
        => TxtSeed.Text = RandomNumberGenerator.GetInt32(int.MaxValue).ToString();

    private async void TestRemote_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtRemotePort.Text, out var port)) { TxtRemoteResult.Text = "Некорректный порт"; return; }
        TxtRemoteResult.Text = "подключаюсь…";
        var ok = await App.Rcon.ConnectAsync(TxtRemoteHost.Text.Trim(), port, TxtRemotePassword.Text, 5000);
        if (!ok) { TxtRemoteResult.Text = "✘ не удалось подключиться"; return; }
        try
        {
            var info = await App.Rcon.SendAsync("serverinfo", 5000);
            TxtRemoteResult.Text = info.Length > 0 ? "✔ сервер отвечает" : "✔ подключено";
        }
        catch { TxtRemoteResult.Text = "✔ подключено"; }
    }

    private bool ConfirmCancel()
    {
        if (_chain is { IsCompleted: false })
            return MessageBox.Show("Загрузка сервера ещё идёт. Прервать и выйти?\n(частично скачанные файлы сохранятся)",
                "OctavianRSM", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        return MessageBox.Show("Прервать первую настройку и выйти?", "OctavianRSM",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCancel()) return;
        _cts.Cancel();
        DialogResult = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

