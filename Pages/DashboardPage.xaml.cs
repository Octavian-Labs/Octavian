using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OctavianRSM.Services;

namespace OctavianRSM.Pages;

public partial class DashboardPage : Page
{
    private CancellationTokenLike _tail = new();
    private TimeSpan? _prevCpu;
    private DateTime _prevCpuAt;
    private bool _optsReady;

    public DashboardPage()
    {
        InitializeComponent();
        TxtSub.Text = App.Settings.IsRemoteMode
            ? $"{App.Settings.Hostname} • удалённый RCON {App.Settings.RconHost}:{App.Settings.RconPort}"
            : $"{App.Settings.Hostname} • {App.Settings.ServerDirPath}";
        var s0 = App.Settings;
        TxtPorts.Text = s0.IsRemoteMode
            ? $"rcon {s0.RconHost}:{s0.RconPort}  ·  игровой порт: {s0.GamePort}"
            : $"игра {s0.GamePort}  ·  query {s0.QueryPort}  ·  rcon {s0.RconPort}  ·  app {s0.AppPort}";
        _ = DetectConnectAddress();
        LoadOptions();
        App.Server.OnStateChanged += Server_OnStateChanged;
        // NavigationView пересоздаёт страницы — при уходе гасим фон и отписываемся,
        // иначе каждая загрузка дашборда плодит вечные циклы и подписчиков
        Unloaded += DashboardPage_Unloaded;
        SizeChanged += DashboardPage_SizeChanged;
        UpdateState();
        RefreshUpdateButton();
        App.Updater.OnChecked += Updater_OnChecked;
        _ = App.Updater.CheckAsync(); // повторная проверка при входе на вкладку (с троттлингом)
        _ = LoopStats();
        _ = ConnectRcon();
    }

    private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _tail.Cancel();
        App.Server.OnStateChanged -= Server_OnStateChanged;
        App.Updater.OnChecked -= Updater_OnChecked;
    }

    private void Server_OnStateChanged(bool running) => Dispatcher.Invoke(UpdateState);

    // Адаптивная раскладка: на узком окне правая инфо-панель не влезает —
    // переносим её в общий столбец сразу после блока «Подключение»;
    // при расширении окна возвращаем на место справа.
    private void DashboardPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 840;
        if (narrow && ReferenceEquals(PnlInfo.Parent, RootGrid))
        {
            RootGrid.Children.Remove(PnlInfo);
            PnlInfo.ClearValue(Grid.ColumnProperty);
            PnlInfo.Margin = new Thickness(0, 0, 0, 14);
            LeftStack.Children.Insert(3, PnlInfo);
        }
        else if (!narrow && ReferenceEquals(PnlInfo.Parent, LeftStack))
        {
            LeftStack.Children.Remove(PnlInfo);
            PnlInfo.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(PnlInfo, 1);
            RootGrid.Children.Add(PnlInfo);
        }
    }

    // Однострочный фидбек действий (бывший журнал переехал во вкладку «Логи»)
    private void Note(string msg) => TxtActionResult.Text = msg;

    // ── Самообновление ──────────────────────────────────────────────────────
    private void Updater_OnChecked()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(RefreshUpdateButton);
    }

    private void RefreshUpdateButton()
    {
        var info = App.Updater.Latest;
        if (info == null || _updating) { if (!_updating) BtnUpdate.Visibility = Visibility.Collapsed; return; }
        BtnUpdate.Content = $"⬆ ОБНОВИТЬ → {info.Tag}";
        BtnUpdate.ToolTip = $"Доступна новая версия {info.Tag} ({info.Size / 1048576} MB)\n" +
                            $"текущая: {UpdateService.CurrentVersion()}";
        BtnUpdate.Visibility = Visibility.Visible;
    }

    private bool _updating;
    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        var info = App.Updater.Latest;
        if (info == null || _updating) return;
        if (MessageBox.Show(
                $"Скачать {info.Tag} и обновить OctavianRSM?\nПриложение перезапустится; сервер продолжит работать.",
                "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _updating = true;
        BtnUpdate.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => BtnUpdate.Content = $"⬇ {p * 100:0}%");
            var newFile = await App.Updater.DownloadAsync(info, progress);
            BtnUpdate.Content = "ПЕРЕЗАПУСК…";
            await Task.Delay(400);
            App.Updater.ApplyAndRestart(newFile);
        }
        catch (Exception ex)
        {
            _updating = false;
            BtnUpdate.IsEnabled = true;
            RefreshUpdateButton();
            Note($"Обновление не удалось: {ex.Message}");
        }
    }

    private void UpdateState()
    {
        bool local = !App.Settings.IsRemoteMode;
        bool running = local ? App.Server.IsRunning : App.Rcon.IsConnected;
        BtnStart.IsEnabled = local && !running;
        BtnStop.IsEnabled = local && running;
        BtnKill.IsEnabled = local && running;
        BtnRestart.IsEnabled = local && running;
        BtnForceSave.IsEnabled = App.Rcon.IsConnected;
        // в remote нет локального процесса — кнопки питания прячем, FORCE SAVE остаётся (RCON)
        BtnStart.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        BtnStop.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        BtnKill.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        BtnRestart.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        if (!running)
        {
            StatOnline.Text = "—"; StatFps.Text = "—"; StatUptime.Text = "—"; StatRcon.Text = "OFF";
            StatQueue.Text = "—"; StatNet.Text = "—"; StatMem.Text = "—"; StatWipe.Text = "—";
            StatState.Text = App.Settings.IsRemoteMode ? "OFFLINE" : "СТОП";
            StatState.Foreground = (SolidColorBrush)FindResource("B.Danger");
            StatCpu.Text = "—"; StatRam.Text = "—"; StatDisk.Text = "—";
            StatOnlineSub.Text = "сервер остановлен";
            _prevCpu = null;
        }
    }

    private async Task ConnectRcon()
    {
        if (!App.Rcon.IsConnected)
            await App.Rcon.ConnectAsync(App.Settings.RconHost, App.Settings.RconPort, App.Settings.RconPassword);
        Dispatcher.Invoke(() => { StatRcon.Text = App.Rcon.IsConnected ? "ON" : "OFF"; UpdateState(); });
    }

    private async Task LoopStats()
    {
        while (!_tail.Cancelled) // живём пока страница на экране
        {
            try
            {
                if ((App.Settings.IsRemoteMode || App.Server.IsRunning) && App.Rcon.IsConnected)
                {
                    var raw = await App.Rcon.SendAsync("serverinfo");
                    Dispatcher.Invoke(() => ApplyStats(raw));
                }
            }
            catch { }
            await Task.Delay(5000);
        }
    }

    private void ApplyStats(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            int players = r.TryGetProperty("Players", out var p) ? p.GetInt32() : 0;
            int max = r.TryGetProperty("MaxPlayers", out var m) ? m.GetInt32() : 0;
            int queued = r.TryGetProperty("Queued", out var q) ? q.GetInt32() : 0;
            int fps = r.TryGetProperty("Framerate", out var f) ? (int)f.GetSingle() : 0;
            int ents = r.TryGetProperty("EntityCount", out var e) ? e.GetInt32() : 0;
            int uptime = r.TryGetProperty("Uptime", out var u) ? u.GetInt32() : 0;
            string map = r.TryGetProperty("Map", out var mp) ? mp.GetString() ?? "" : "";
            int joining = r.TryGetProperty("Joining", out var j) ? j.GetInt32() : 0;
            long netIn = r.TryGetProperty("NetworkIn", out var ni) ? ni.GetInt64() : 0;
            long netOut = r.TryGetProperty("NetworkOut", out var no) ? no.GetInt64() : 0;
            long mem = r.TryGetProperty("MemoryUsageSystem", out var mm) ? mm.GetInt64() : 0;
            bool restarting = r.TryGetProperty("Restarting", out var rst) && rst.GetBoolean();
            string wipe = r.TryGetProperty("SaveCreatedTime", out var wt) ? wt.GetString() ?? "" : "";

            StatState.Text = restarting ? "РЕСТАРТ" : fps >= 25 ? "НОРМА" : fps >= 15 ? "ВНИМАНИЕ" : "НИЗКИЙ FPS";
            StatState.Foreground = (SolidColorBrush)FindResource(
                restarting ? "B.Warn" : fps >= 25 ? "B.Success" : fps >= 15 ? "B.Warn" : "B.Danger");
            StatStateSub.Text = restarting ? "идёт перезапуск" : $"fps {fps}";
            StatOnline.Text = $"{players}/{max}";
            StatOnlineSub.Text = queued > 0 ? $"очередь {queued}" : "игроков";
            StatFps.Text = fps.ToString();
            StatEntities.Text = $"{ents:N0} сущностей";
            var ts = TimeSpan.FromSeconds(uptime);
            StatUptime.Text = ts.Days > 0 ? $"{ts.Days}д {ts.Hours}ч" : $"{ts.Hours}ч {ts.Minutes}м";
            StatMap.Text = string.IsNullOrEmpty(map) ? "" : map;
            StatRcon.Text = "ON";
            StatQueue.Text = $"{queued} / {joining}";
            StatNet.Text = $"{FmtBytes(netIn)}/{FmtBytes(netOut)}";
            StatMem.Text = mem > 0 ? $"{mem / 1048576.0:0} MB" : "—";
            StatWipe.Text = wipe.Length >= 10 ? wipe[..10] : (wipe == "" ? "—" : wipe);

            // CPU/RAM процесса — только локальный сервер; в remote их не измерить
            var snap = App.Server.GetProcessSnapshot();
            if (snap is { } proc)
            {
                StatRam.Text = FmtBytes(proc.WorkingSet);
                var now = DateTime.UtcNow;
                if (_prevCpu.HasValue)
                {
                    var cpuDelta = (proc.CpuTime - _prevCpu.Value).TotalMilliseconds;
                    var wall = (now - _prevCpuAt).TotalMilliseconds * Environment.ProcessorCount;
                    StatCpu.Text = wall > 0 ? $"{Math.Clamp(cpuDelta / wall * 100, 0, 100):0.0}%" : "—";
                }
                else StatCpu.Text = "замер…";
                _prevCpu = proc.CpuTime;
                _prevCpuAt = now;
            }
            else { StatCpu.Text = "—"; StatRam.Text = mem > 0 ? $"{mem / 1048576.0:0} MB" : "—"; }

            try
            {
                StatDisk.Text = App.Settings.IsRemoteMode ? "remote"
                    : FmtBytes(new DriveInfo(Path.GetPathRoot(App.Settings.ServerDirPath)!).AvailableFreeSpace);
            }
            catch { StatDisk.Text = "—"; }
        }
        catch { }
    }

    private static string FmtBytes(long b)
        => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB"
         : b >= 1L << 20 ? $"{b / 1048576} MB"
         : b >= 1L << 10 ? $"{b / 1024} KB" : $"{b} B";

    // ── Кнопки управления ───────────────────────────────────────────────────
    private async void BtnKill_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("УБИТЬ процесс сервера (без сохранения)?", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await App.Server.StopAsync(force: true);
        UpdateState();
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var wipe = new Services.WipeService(App.Settings);
        if (wipe.IsDue())
        {
            var full = App.Settings.AutoWipeFull;
            var ask = MessageBox.Show(
                $"Автовайп просрочен.\nВыполнить {(full ? "ПОЛНЫЙ" : "map")} вайп перед запуском?\n(файлы уйдут в _wipe_backup)",
                "OctavianRSM — автовайп", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (ask == MessageBoxResult.Cancel) return;
            if (ask == MessageBoxResult.Yes)
            {
                var (moved, dir) = wipe.Execute(full);
                Note($"Автовайп: перемещено {moved} файлов → {dir}");
            }
        }
        if (App.Server.Start()) { UpdateState(); _ = ConnectRcon(); }
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (App.Rcon.IsConnected) await App.Rcon.FireAsync("quit");
        await Task.Delay(4000);
        await App.Server.StopAsync();
        UpdateState();
    }

    // Рестарт: сохранить → quit → дождаться остановки → поднять заново.
    // StopAsync помечает остановку намеренной (watchdog молчит), Start её снимает.
    private async void BtnRestart_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Перезапустить сервер? Игроков кикнет, мир сохранится.",
                "OctavianRSM — рестарт", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Note("Рестарт: сохранение и остановка…");
        if (App.Rcon.IsConnected) { await App.Rcon.FireAsync("server.save"); await App.Rcon.FireAsync("quit"); }
        await App.Server.StopAsync();
        for (int i = 0; i < 40 && App.Server.IsRunning; i++) await Task.Delay(500);
        if (App.Server.IsRunning) await App.Server.StopAsync(force: true);
        if (App.Server.Start()) { UpdateState(); _ = ConnectRcon(); Note("✔ сервер перезапущен"); }
        else Note("✘ не удалось запустить сервер — смотрите «Логи»");
    }

    private async void ForceSave_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Rcon.IsConnected) { Note("RCON не подключён — сохранение недоступно"); return; }
        Note("> server.save");
        await App.Rcon.FireAsync("server.save");
        Note("✔ server.save отправлен");
    }

    // ── Опции автоматики: сохраняются сразу, watchdog читает их вживую ──────
    private void LoadOptions()
    {
        var s = App.Settings;
        ChkWatchdog.IsChecked = s.WatchdogEnabled;
        TxtWatchdogDelay.Text = s.WatchdogDelaySec.ToString();
        ChkAutoRestart.IsChecked = s.AutoRestartEnabled;
        TxtRestartTimes.Text = s.AutoRestartTimes;
        TxtRestartWarn.Text = s.AutoRestartWarnSec.ToString();
        ChkAutoWipe.IsChecked = s.AutoWipeEnabled;
        TxtWipeDays.Text = s.AutoWipeIntervalDays.ToString();
        ChkAutoWipeFull.IsChecked = s.AutoWipeFull;
        _optsReady = true;
    }

    private void Opt_Changed(object sender, RoutedEventArgs e)
    {
        if (!_optsReady) return;
        var s = App.Settings;
        s.WatchdogEnabled = ChkWatchdog.IsChecked == true;
        if (int.TryParse(TxtWatchdogDelay.Text, out var wd) && wd >= 3) s.WatchdogDelaySec = wd;
        s.AutoRestartEnabled = ChkAutoRestart.IsChecked == true;
        s.AutoRestartTimes = TxtRestartTimes.Text.Trim();
        if (int.TryParse(TxtRestartWarn.Text, out var rw) && rw >= 0) s.AutoRestartWarnSec = rw;
        s.AutoWipeEnabled = ChkAutoWipe.IsChecked == true;
        if (int.TryParse(TxtWipeDays.Text, out var dd) && dd >= 1) s.AutoWipeIntervalDays = dd;
        s.AutoWipeFull = ChkAutoWipeFull.IsChecked == true;
        s.Save(App.SettingsPath);
        TxtOptSaved.Text = $"✔ сохранено · {DateTime.Now:HH:mm:ss}";
    }

    // ── Подключение: адрес для игроков ─────────────────────────────────────
    private string? _connectAddr; // "ip:port"

    // Приоритет адреса: remote → RconHost; заданный домен → он (SRV-запись
    // сама резолвит порт); заданный ServerIp → он; иначе LAN сразу +
    // попытка узнать внешний IP через ipwho.is.
    private async Task DetectConnectAddress()
    {
        var s = App.Settings;
        if (s.IsRemoteMode) { SetConnect(s.RconHost, "УДАЛЬНЫЙ СЕРВЕР — подключение в игре (F1)"); return; }
        if (!string.IsNullOrWhiteSpace(s.ServerDomain))
        { SetConnectDomain(s.ServerDomain.Trim()); return; }
        if (!string.IsNullOrWhiteSpace(s.ServerIp) && s.ServerIp != "0.0.0.0")
        { SetConnect(s.ServerIp, "АДРЕС СЕРВЕРА — подключение в игре (F1)"); return; }

        var lan = LocalIPv4() ?? "127.0.0.1";
        SetConnect(lan, "ЛОКАЛЬНЫЙ АДРЕС (LAN) — подключение в игре (F1)");
        var pub = await PublicIPv4Async();
        if (pub != null) SetConnect(pub, "ВНЕШНИЙ IP — подключение в игре (F1)");
    }

    // Подключение по домену: порт не пишем — SRV-запись отдаёт его сама
    private void SetConnectDomain(string domain)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            _connectAddr = domain;
            TxtConnectLabel.Text = "ДОМЕН СЕРВЕРА (DNS SRV) — подключение в игре (F1)";
            TxtConnect.Text = $"client.connect {domain}";
        });
    }

    private void SetConnect(string ip, string label)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            _connectAddr = $"{ip}:{App.Settings.GamePort}";
            TxtConnectLabel.Text = label;
            TxtConnect.Text = $"client.connect {_connectAddr}";
        });
    }

    private static string? LocalIPv4()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                                  && !IPAddress.IsLoopback(a))?.ToString();
        }
        catch { return null; }
    }

    // Свой публичный IP — тот же ipwho.is, что и геолокация игроков
    private static async Task<string?> PublicIPv4Async()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var json = await http.GetStringAsync("https://ipwho.is/?fields=ip");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ip", out var ip) ? ip.GetString() : null;
        }
        catch { return null; }
    }

    private async void CopyConnect_Click(object sender, RoutedEventArgs e)
    {
        var addr = _connectAddr ?? $"127.0.0.1:{App.Settings.GamePort}";
        Clipboard.SetText($"client.connect {addr}");
        BtnCopyConnect.Content = "✔ СКОПИРОВАНО";
        await Task.Delay(1500);
        BtnCopyConnect.Content = "📋 КОПИРОВАТЬ";
    }

    private void SteamConnect_Click(object sender, RoutedEventArgs e)
    {
        var addr = _connectAddr ?? $"127.0.0.1:{App.Settings.GamePort}";
        // домен хранится без порта, а steam://connect требует host:port
        if (!addr.Contains(':')) addr += $":{App.Settings.GamePort}";
        try
        {
            Process.Start(new ProcessStartInfo($"steam://connect/{addr}") { UseShellExecute = true });
        }
        catch (Exception ex) { Note($"steam://connect не открылся: {ex.Message}"); }
    }
}
