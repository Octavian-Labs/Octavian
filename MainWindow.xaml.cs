using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace OctavianRSM;

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    // Псевдо-maximize: системный WindowState.Maximized у кастомного chrome
    // (WindowStyle=None) выходит за пределы рабочей области и режет нижний край —
    // разворачиваем окно вручную ровно по SystemParameters.WorkArea
    private bool _pseudoMax;
    private bool _suppressState;
    private Rect _restoredBounds = Rect.Empty;
    private WindowState _lastState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        var localVisibility = App.Settings.IsRemoteMode ? Visibility.Collapsed : Visibility.Visible;
        NavPlugins.Visibility = localVisibility;
        NavServerCfg.Visibility = localVisibility;
        NavRates.Visibility = localVisibility;
        NavLogs.Visibility = localVisibility;
        NavWipes.Visibility = localVisibility;
        NavInstall.Visibility = localVisibility;
        Loaded += (_, _) =>
        {
            // WPF-UI NavigationViewContentPresenter оборачивает страницу в динамический ScrollViewer —
            // из-за него внутренние ScrollViewer страниц получали бесконечную высоту и «глотали» колесо мыши
            NavView.ApplyTemplate();
            if (NavView.Template.FindName("PART_NavigationViewContentPresenter", NavView)
                    is Wpf.Ui.Controls.NavigationViewContentPresenter presenter)
            {
                // сеттер internal — ставим DP напрямую через SetValue
                var dp = typeof(Wpf.Ui.Controls.NavigationViewContentPresenter)
                    .GetField("IsDynamicScrollViewerEnabledProperty",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(null) as DependencyProperty;
                if (dp != null) presenter.SetValue(dp, false);
            }
            NavView.Navigate(typeof(Pages.DashboardPage));
        };
        StateChanged += (_, _) => OnStateChangedEx();
        App.Server.OnStateChanged += _ => Dispatcher.Invoke(UpdateChips);
        App.Rcon.OnDisconnected += () => Dispatcher.Invoke(UpdateChips);
        SbMode.Text = App.Settings.IsRemoteMode
            ? $"REMOTE • {App.Settings.RconHost}:{App.Settings.RconPort}"
            : $"LOCAL • порт {App.Settings.GamePort}";
        TxtVersion.Text = $"v{Services.UpdateService.CurrentVersion()} • Octavian";
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => SbClock.Text = DateTime.Now.ToString("HH:mm:ss");
        clock.Start();
        _ = StatusLoop();
        UpdateChips();
        ClearStatusBar();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        if (_pseudoMax) Unmaximize();
        else MaximizeBounds();
    }

    private void MaximizeBounds()
    {
        // RestoreBounds валиден, когда окно уже развёрнуто системой (двойной клик по шапке);
        // в Normal он может хранить устаревшее значение — берём текущие границы
        var rb = RestoreBounds;
        _restoredBounds = (WindowState == WindowState.Normal || rb.IsEmpty)
            ? new Rect(Left, Top, Width, Height) : rb;
        if (WindowState != WindowState.Normal)
        {
            _suppressState = true;
            WindowState = WindowState.Normal;
            _suppressState = false;
        }
        var wa = SystemParameters.WorkArea;
        Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;
        _pseudoMax = true;
        BtnMax.Content = "❐";
    }

    private void Unmaximize()
    {
        _pseudoMax = false;
        if (!_restoredBounds.IsEmpty && _restoredBounds.Width > 0)
        {
            Left = _restoredBounds.Left; Top = _restoredBounds.Top;
            Width = _restoredBounds.Width; Height = _restoredBounds.Height;
        }
        BtnMax.Content = "▢";
    }

    private void OnStateChangedEx()
    {
        if (_suppressState) { _lastState = WindowState; return; }
        if (WindowState == WindowState.Maximized && !_pseudoMax)
            MaximizeBounds();
        else if (WindowState == WindowState.Normal && _pseudoMax && _lastState == WindowState.Maximized)
            Unmaximize(); // системное восстановление из развёрнутого состояния
        _lastState = WindowState;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public void UpdateChips()
    {
        bool srv = App.Settings.IsRemoteMode ? App.Rcon.IsConnected : App.Server.IsRunning;
        DotServer.Fill = (SolidColorBrush)FindResource(srv ? "B.Success" : "B.Danger");
        TxtServerState.Text = App.Settings.IsRemoteMode
            ? (srv ? " REMOTE ONLINE" : " REMOTE OFFLINE")
            : (srv ? " РАБОТАЕТ" : " СТОП");
        bool rcon = App.Rcon.IsConnected;
        DotRcon.Fill = (SolidColorBrush)FindResource(rcon ? "B.Success" : "B.Danger");
        TxtRconState.Text = rcon ? " RCON" : " RCON OFF";
        // строка состояния
        SbDot.Fill = (SolidColorBrush)FindResource(srv ? "B.Success" : "B.Danger");
        SbState.Text = App.Settings.IsRemoteMode
            ? (srv ? "REMOTE ONLINE" : "REMOTE OFFLINE")
            : (srv ? "РАБОТАЕТ" : "СТОП");
    }

    private void ApplyStatusBar(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            int players = r.TryGetProperty("Players", out var p) ? p.GetInt32() : 0;
            int max = r.TryGetProperty("MaxPlayers", out var m) ? m.GetInt32() : 0;
            int fps = r.TryGetProperty("Framerate", out var f) ? (int)f.GetSingle() : 0;
            int uptime = r.TryGetProperty("Uptime", out var u) ? u.GetInt32() : 0;
            var ts = TimeSpan.FromSeconds(uptime);
            SbPlayers.Text = $"игроков {players}/{max}";
            SbFps.Text = $"fps {fps}";
            SbFps.Foreground = (SolidColorBrush)FindResource(
                fps >= 25 ? "B.Success" : fps >= 15 ? "B.Warn" : "B.Danger");
            SbUptime.Text = $"uptime {(ts.Days > 0 ? $"{ts.Days}д " : "")}{ts.Hours:00}:{ts.Minutes:00}";
        }
        catch { }
    }

    private void ClearStatusBar()
    {
        SbPlayers.Text = "игроков —";
        SbFps.Text = "fps —";
        SbFps.Foreground = (SolidColorBrush)FindResource("B.TextDim");
        SbUptime.Text = "uptime —";
    }

    private async Task StatusLoop()
    {
        while (true)
        {
            await Task.Delay(5000);
            if (Dispatcher.HasShutdownStarted) return; // окно закрыто — выходим
            try
            {
                Dispatcher.Invoke(UpdateChips);
                if (App.Rcon.IsConnected)
                {
                    var raw = await App.Rcon.SendAsync("serverinfo");
                    Dispatcher.Invoke(() => ApplyStatusBar(raw));
                }
                else Dispatcher.Invoke(ClearStatusBar);
            }
            catch { }
        }
    }
}
