using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OctavianRSM.Services;
using Wpf.Ui.Controls;

namespace OctavianRSM;

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    private readonly Dictionary<NavigationViewItem, AppRoute> _routeByItem = new();
    private readonly Dictionary<string, NavigationViewItem> _itemByRoute = new();
    private readonly List<(NavigationViewItemHeader Header, List<NavigationViewItem> Items)> _groups = new();
    private string _currentRouteId = "dashboard";
    private AppRoute? _pendingRoute;
    private AppRoute? _clickedRoute;
    private bool _forceNextNav;
    private bool _ctxCollapsed;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        BuildMenu();
        Loaded += (_, _) =>
        {
            // WPF-UI NavigationViewContentPresenter оборачивает страницу в динамический ScrollViewer —
            // из-за него внутренние ScrollViewer страниц получали бесконечную высоту и «глотали» колесо мыши
            NavView.ApplyTemplate();
            if (NavView.Template.FindName("PART_NavigationViewContentPresenter", NavView)
                    is NavigationViewContentPresenter presenter)
            {
                var dp = typeof(NavigationViewContentPresenter)
                    .GetField("IsDynamicScrollViewerEnabledProperty",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(null) as DependencyProperty;
                if (dp != null) presenter.SetValue(dp, false);
            }
            NavigateToRoute("dashboard", force: true);
        };
        App.Ctx.Changed += () => Dispatcher.Invoke(ApplyContextState);
        App.Ctx.CommandResultPosted += (cmd, result) => Dispatcher.Invoke(() => ApplyLastResult(cmd, result));
        SbMode.Text = App.Settings.IsRemoteMode
            ? $"REMOTE • {App.Settings.RconHost}:{App.Settings.RconPort}"
            : $"LOCAL • порт {App.Settings.GamePort}";
        TxtVersion.Text = $"v{UpdateService.CurrentVersion()} • Octavian";
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => SbClock.Text = DateTime.Now.ToString("HH:mm:ss");
        clock.Start();
        _ = StatusLoop();
        ApplyContextState();
        ClearStatusBar();
    }

    // ── Меню из реестра маршрутов ───────────────────────────────────────

    private void BuildMenu()
    {
        // единый перехват: черновик → capability → параметр маршрута.
        // Срабатывает и на клики, и на программные Navigate — до создания/показа страницы
        NavView.Navigating += Nav_Navigating;
        foreach (var group in AppRoutes.Groups)
        {
            var header = new NavigationViewItemHeader { Text = group.Title };
            NavView.MenuItems.Add(header);
            var items = new List<NavigationViewItem>();
            foreach (var route in group.Routes)
            {
                var item = new NavigationViewItem
                {
                    Content = route.Title,
                    Tag = route,
                    TargetPageType = route.PageType,
                    TargetPageTag = route.Id, // id маршрута: Navigate(tag) находит именно этот пункт
                    Icon = Enum.TryParse<SymbolRegular>(route.Icon, out var sym)
                        ? new SymbolIcon(sym) : new SymbolIcon(SymbolRegular.Home24),
                };
                // Click бьёт до внутренней навигации — запоминаем маршрут,
                // потому что у одного PageType может быть несколько маршрутов
                var r = route;
                item.Click += (_, _) => _clickedRoute = r;
                NavView.MenuItems.Add(item);
                _routeByItem[item] = route;
                _itemByRoute[route.Id] = item;
                items.Add(item);
            }
            _groups.Add((header, items));
        }
    }

    private void Nav_Navigating(NavigationView sender, NavigatingCancelEventArgs e)
    {
        var pageType = e.Page?.GetType();
        // приоритет: явно запрошенный маршрут → кликнутый пункт → первый маршрут с этим PageType
        var route = _pendingRoute ?? _clickedRoute ?? AppRoutes.FindByType(pageType);
        _pendingRoute = null; _clickedRoute = null;
        if (route == null || route.PageType != pageType)
        {
            AppNav.Arm(_currentRouteId, null);
            return;
        }
        if (route.Requires != null && !route.Requires(App.Ctx)) { e.Cancel = true; return; }
        var force = _forceNextNav;
        _forceNextNav = false;
        if (!force && !App.Ctx.ConfirmLeaveDirty(this)) { e.Cancel = true; return; }
        AppNav.Arm(route.Id, route.Param);
        _currentRouteId = route.Id;
    }

    /// <summary>Единая точка переходов: навигация по id маршрута (tag пункта меню).</summary>
    public void NavigateToRoute(string routeId, bool force = false)
    {
        var route = AppRoutes.Find(routeId);
        if (route == null) return;
        _pendingRoute = route;
        _forceNextNav = force; // принудительный переход (закрывшийся маршрут): без вопроса о черновике
        NavView.Navigate(routeId); // tag пункта меню — попадаем в нужный маршрут при общем PageType
    }

    // ── Возможности: скрываем маршруты, недоступные в текущем режиме ─────

    private void ApplyCapabilities()
    {
        foreach (var (item, route) in _routeByItem)
            item.Visibility = route.Requires == null || route.Requires(App.Ctx)
                ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (header, items) in _groups)
        {
            bool any = false;
            foreach (var i in items)
                if (i.Visibility == Visibility.Visible) { any = true; break; }
            header.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
        // текущий маршрут стал недоступен (переключили режим/отвалился RCON на странице без partial) → на обзор
        var cur = AppRoutes.Find(_currentRouteId);
        if (cur?.Requires != null && !cur.Requires(App.Ctx))
            NavigateToRoute("dashboard", force: true);
    }

    // ── Состояние оболочки от ServerContext ─────────────────────────────

    private void ApplyContextState()
    {
        var ctx = App.Ctx;
        ApplyCapabilities();

        bool srv = ctx.IsRemote ? ctx.RconConnected : ctx.ServerRunning;
        DotServer.Fill = (SolidColorBrush)FindResource(srv ? "B.Success" : "B.Danger");
        TxtServerState.Text = ctx.IsRemote
            ? (srv ? " REMOTE ONLINE" : " REMOTE OFFLINE")
            : (srv ? " РАБОТАЕТ" : " СТОП");
        DotRcon.Fill = (SolidColorBrush)FindResource(ctx.RconConnected ? "B.Success" : "B.Danger");
        TxtRconState.Text = ctx.RconConnected ? " RCON" : " RCON OFF";
        SbDot.Fill = (SolidColorBrush)FindResource(srv ? "B.Success" : "B.Danger");
        SbMode.Text = ctx.IsRemote
            ? $"REMOTE • {App.Settings.RconHost}:{App.Settings.RconPort}"
            : $"LOCAL • порт {App.Settings.GamePort}";
        SbState.Text = ctx.IsRemote
            ? (srv ? "REMOTE ONLINE" : "REMOTE OFFLINE")
            : (srv ? "РАБОТАЕТ" : "СТОП");

        // контекстная панель
        var p = ctx.SelectedPlayer;
        CtxTargetName.Text = p?.DisplayName ?? "не выбрана";
        CtxTargetId.Text = p?.SteamId ?? "игрок для адресных действий";
        CtxOpName.Text = ctx.Operation ?? "—";
        CtxOpDot.Fill = (SolidColorBrush)FindResource(ctx.Busy ? "B.Warn" : "B.TextDim");
        SbBusy.Text = ctx.Busy ? $"│ ⏳ {ctx.Operation}" : "";
    }

    private void ApplyLastResult(string command, string result)
    {
        CtxLastCmd.Text = "❯ " + command;
        var one = result.Replace("\r", " ").Replace("\n", " ⏎ ");
        CtxLastResult.Text = one.Length > 300 ? one[..300] + "…" : one;
    }

    private void CtxTarget_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateToRoute("players");

    private void CtxResult_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateToRoute("console");

    private void CtxCollapse_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _ctxCollapsed = !_ctxCollapsed;
        CtxBody.Visibility = _ctxCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CtxPanel.Width = _ctxCollapsed ? 34 : 224;
        TxtCtxToggle.Text = _ctxCollapsed ? "◣" : "◢";
    }

    // ── Строка состояния: serverinfo по RCON ────────────────────────────

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
            if (Dispatcher.HasShutdownStarted) return;
            try
            {
                if (App.Ctx.RconConnected)
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
