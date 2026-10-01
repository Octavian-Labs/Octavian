using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

    public SetupWizardWindow(RsmSettings settings, bool hadSettings)
    {
        InitializeComponent();
        _s = settings;
        _boot = new BootstrapService(settings);

        LstProfiles.ItemsSource = ServerProfiles.All;
        LstProfiles.SelectedItem = ServerProfiles.Get(_s.ServerProfile);

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
        if (step == PageInstall) UpdateInstallButtons();
        if (step == PageRemote) BtnNext.IsEnabled = true;
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
                UpdateInstallButtons();
            });
        });
    }

    private void OnProgress(BootProgress p)
    {
        TxtPhase.Text = $"{p.Phase}: {p.Detail}";
        if (p.Percent is double pct)
        {
            PbInstall.IsIndeterminate = false;
            PbInstall.Value = Math.Clamp(pct, 0, 100);
            TxtPct.Text = $"{pct:0}%";
        }
        else if (p.Detail.Length > 0 && _step == PageInstall)
        {
            // без числа — не трогаем индетерминированность
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
                if (LstProfiles.SelectedItem is ServerProfile p)
                    ServerProfiles.Apply(p, _s);
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

