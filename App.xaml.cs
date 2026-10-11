using System.IO;
using System.Windows;
using OctavianRSM.Models;
using OctavianRSM.Services;

namespace OctavianRSM;

public partial class App : Application
{
    public static RsmSettings Settings { get; private set; } = new();
    public static RconService Rcon { get; private set; } = new();
    public static ServerManager Server { get; private set; } = null!;
    public static SteamCmdService SteamCmd { get; private set; } = null!;
    public static PluginService Plugins { get; private set; } = null!;
    public static WatchdogService Watchdog { get; private set; } = null!;
    public static ServerConfigService ServerCfg { get; private set; } = null!;
    public static ChatLogService ChatLog { get; private set; } = null!;
    public static DbService Db { get; private set; } = null!;
    public static GeoService Geo { get; private set; } = new();
    public static UpdateService Updater { get; private set; } = new();

    /// <summary>Единая модель состояния и возможностей — создаётся после InitializeServices.</summary>
    public static ServerContext Ctx { get; private set; } = null!;

    public static string SettingsPath { get; private set; } = "";
    public static string AppRoot => Path.GetDirectoryName(SettingsPath) ?? AppContext.BaseDirectory;
    public static string DataDir => Path.Combine(AppRoot, "data");
    public static bool HasManagedLocalServer => !Settings.IsRemoteMode && File.Exists(Settings.ServerExePath);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // ни одна ошибка UI не должна убивать приложение молча — пишем в data\crash.log
        DispatcherUnhandledException += (_, a) =>
        { LogCrash(a.Exception); a.Handled = true; };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) =>
        { LogCrash(a.Exception); a.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            LogCrash(a.ExceptionObject as Exception);

        SettingsPath = ResolveSettingsPath();
        // Диагностика: OctavianRSM.exe --test-config <PluginName> — открывает окно конфига,
        // ошибки пишутся в data\config-errors.log
        if (e.Args.Length >= 2 && e.Args[0] == "--test-config")
        {
            Settings = RsmSettings.Load(SettingsPath);
            new PluginConfigWindow(e.Args[1]).Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = Current.Windows.Count > 0 ? Current.Windows[0] : null;
            return;
        }
        var hadSettings = File.Exists(SettingsPath);
        Settings = RsmSettings.Load(SettingsPath);
        if (hadSettings && File.Exists(Settings.ServerExePath) && !Settings.SetupCompleted)
        {
            Settings.SetupCompleted = true;
            Settings.DeploymentMode = "Local";
            Settings.Save(SettingsPath);
        }

        if (!Settings.SetupCompleted || (!Settings.IsRemoteMode && !File.Exists(Settings.ServerExePath)))
        {
            var wizard = new SetupWizardWindow(Settings, hadSettings);
            if (wizard.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            Settings.Save(SettingsPath);
        }

        InitializeServices();
        UpdateService.CleanupLeftovers();
        _ = Updater.CheckAsync(); // фоновая проверка обновления на GitHub
        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private static string ResolveSettingsPath()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "octavian-rsm.json");
        if (File.Exists(portable)) return portable;
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current != null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "octavian-rsm.json");
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) && File.Exists(candidate))
                return candidate;
        }
        return portable;
    }

    private static void InitializeServices()
    {
        Directory.CreateDirectory(DataDir);
        Server = new ServerManager(Settings);
        SteamCmd = new SteamCmdService(Settings);
        Plugins = new PluginService(Settings);
        Watchdog = new WatchdogService(Settings, Server, Rcon);
        Watchdog.OnLog += line => Server.Echo(line);
        Watchdog.Start();
        ServerCfg = new ServerConfigService(Settings);
        ChatLog = new ChatLogService(Settings);
        Rcon.OnMessage += ChatLog.OnRconMessage;
        Db = new DbService(DataDir);
        Rcon.OnMessage += (type, msg) => Db.Log($"rcon:{type}", msg);
        Server.OnOutput += line => Db.Log("server", line);
        Ctx = new ServerContext(Settings, Server, Rcon);
        Ctx.Refresh();
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var dir = string.IsNullOrEmpty(SettingsPath) ? Path.Combine(AppContext.BaseDirectory, "data") : DataDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"═══ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══\n{ex}\n\n");
        }
        catch { }
        MessageBox.Show($"Ошибка (подробности — data\\crash.log):\n{ex.Message}",
            "OctavianRSM", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Watchdog?.Stop();
        Rcon?.Dispose();
        Db?.Dispose();
        base.OnExit(e);
    }
}
