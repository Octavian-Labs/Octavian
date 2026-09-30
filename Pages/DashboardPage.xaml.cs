using System;
using System.IO;
using System.Linq;
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
    // прилипание журнала: _bulk — заливка истории без прокрутки,
    // _stick — держим низ; пользователь уходит вверх → пауза, возврат через ScrollResumeDelay
    private bool _bulk;
    private bool _stick = true;
    private DateTime _lastUserScroll = DateTime.MinValue;
    private static readonly TimeSpan ScrollResumeDelay = TimeSpan.FromSeconds(18);

    public DashboardPage()
    {
        InitializeComponent();
        TxtSub.Text = App.Settings.IsRemoteMode
            ? $"{App.Settings.Hostname} • удалённый RCON {App.Settings.RconHost}:{App.Settings.RconPort}"
            : $"{App.Settings.Hostname} • {App.Settings.ServerDirPath}";
        App.Server.OnOutput += Server_OnOutput;
        App.Server.OnStateChanged += Server_OnStateChanged;
        // NavigationView пересоздаёт страницы — при уходе гасим фон и отписываемся,
        // иначе каждая загрузка дашборда плодит вечные циклы и подписчиков
        Unloaded += DashboardPage_Unloaded;
        Loaded += (_, _) => JumpToEnd(); // вход на вкладку — журнал сразу на последней строке
        UpdateState();
        RefreshUpdateButton();
        App.Updater.OnChecked += Updater_OnChecked;
        _ = App.Updater.CheckAsync(); // повторная проверка при входе на вкладку (с троттлингом)
        LoadHistory(); // журнал переживает переходы по вкладкам — читаем из SQLite
        _ = StartTailWhenNeeded();
        _ = LoopStats();
        _ = ConnectRcon();
    }

    private void LoadHistory()
    {
        _bulk = true;
        foreach (var (ts, type, msg) in App.Db.RecentLogs(300))
        {
            var kind = type == "server" ? Guess(msg)
                     : type.EndsWith("Chat") ? "chat"
                     : type.EndsWith("Error") ? "err" : "log";
            AppendLog(msg, kind, ts.Length >= 19 ? ts[11..] : ts);
        }
        _bulk = false;
    }

    // ScrollViewer внутри дефолтного шаблона ListBox: Border → ScrollViewer
    private ScrollViewer? LogScroll()
        => VisualTreeHelper.GetChildrenCount(LogItems) > 0
            ? (VisualTreeHelper.GetChild(LogItems, 0) as Border)?.Child as ScrollViewer
            : null;

    private void JumpToEnd() => LogScroll()?.ScrollToEnd();

    private void Log_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0) return; // вырос контент при добавлении — не жест пользователя
        if (e.OriginalSource is not ScrollViewer sv) return;
        if (sv.VerticalOffset >= sv.ScrollableHeight - 4) _stick = true;
        else { _stick = false; _lastUserScroll = DateTime.UtcNow; }
    }

    private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _tail.Cancel();
        App.Server.OnOutput -= Server_OnOutput;
        App.Server.OnStateChanged -= Server_OnStateChanged;
        App.Updater.OnChecked -= Updater_OnChecked;
    }

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
            AppendLog($"[RSM] Обновление не удалось: {ex.Message}", "err");
        }
    }

    // Tail server.log — fallback: если процесс запущен не нами и stdout нам недоступен,
    // либо Unity молчит в stdout. Если сервер наш и консоль пишет — tail не нужен (дубли строк).
    private async Task StartTailWhenNeeded()
    {
        if (App.Settings.IsRemoteMode) return;
        for (int i = 0; i < 20 && !_tail.Cancelled; i++)
        {
            if (App.Server.OwnedByUs && App.Server.ConsoleOutputSeen) return;
            await Task.Delay(500);
        }
        if (_tail.Cancelled) return;
        // server.log — из -logfile при запуске через менеджер; фолбэк — лог start.bat (server\<identity>\server.log)
        var s = App.Settings;
        var candidates = new[]
        {
            Path.Combine(s.ServerDirPath, "server.log"),
            Path.Combine(s.ServerDirPath, "server", s.Identity, "server.log"),
        };
        var logPath = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        await App.Server.TailLogAsync(logPath,
            line => Dispatcher.Invoke(() => AppendLog(line, Guess(line))), _tail);
    }

    private void Server_OnOutput(string line) => Dispatcher.Invoke(() => AppendLog(line, "sys"));
    private void Server_OnStateChanged(bool running) => Dispatcher.Invoke(UpdateState);

    private static string Guess(string line)
    {
        if (line.Contains("[ERRO") || line.Contains("Error")) return "err";
        if (line.Contains("[WARN") || line.Contains("Warning")) return "warn";
        if (line.Contains("joined") || line.Contains("[CHAT")) return "chat";
        return "log";
    }

    private void AppendLog(string line, string kind, string? stamp = null)
    {
        var brush = (SolidColorBrush)FindResource(kind switch
        {
            "err" => "B.Danger",
            "warn" => "B.Warn",
            "chat" => "B.Success",
            "sys" => "B.Cyan",
            _ => "B.TextDim"
        });
        var item = new TextBlock
        {
            Text = $"[{(stamp ?? DateTime.Now.ToString("HH:mm:ss"))}] {line}",
            Foreground = brush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        LogItems.Items.Add(item);
        while (LogItems.Items.Count > 500) LogItems.Items.RemoveAt(0);
        // прокрутка вниз — только при включённом автоскролле и прилипании к последней строке
        if (!_bulk && _stick && ChkAuto.IsChecked == true) JumpToEnd();
    }

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (LogItems.SelectedItem is TextBlock t) Clipboard.SetText(t.Text);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine,
            LogItems.Items.Cast<object>().OfType<TextBlock>().Select(t => t.Text));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private void LogItems_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+C копирует выделенные строки журнала
        if (e.Key == System.Windows.Input.Key.C &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            var sel = LogItems.SelectedItems.Cast<object>().OfType<TextBlock>().Select(t => t.Text);
            var text = string.Join(Environment.NewLine, sel);
            if (text.Length > 0) Clipboard.SetText(text);
        }
    }

    private void UpdateState()
    {
        bool local = !App.Settings.IsRemoteMode;
        bool running = local ? App.Server.IsRunning : App.Rcon.IsConnected;
        BtnStart.IsEnabled = local && !running;
        BtnStop.IsEnabled = local && running;
        BtnKill.IsEnabled = local && running;
        BtnStart.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        BtnStop.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        BtnKill.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
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
            // пользователь отпустил журнал — через паузу возвращаем прилипание к последней строке
            if (!_stick)
                Dispatcher.Invoke(() =>
                {
                    if (ChkAuto.IsChecked == true && DateTime.UtcNow - _lastUserScroll > ScrollResumeDelay)
                    {
                        _stick = true;
                        JumpToEnd();
                    }
                });
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
            StatMap.Text = string.IsNullOrEmpty(map) ? "карта —" : map;
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
                AppendLog($"[RSM] Автовайп: перемещено {moved} файлов → {dir}", "sys");
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

    private async void Q(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var cmd = b.Tag?.ToString() ?? b.Content?.ToString() ?? "";
        TxtQuickResult.Text = "> " + cmd;
        var resp = await App.Rcon.SendAsync(cmd);
        TxtQuickResult.Text = $"> {cmd}\n{resp}";
    }
}
