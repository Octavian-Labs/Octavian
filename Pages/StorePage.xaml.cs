using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using OctavianRSM.Services;

namespace OctavianRSM.Pages;

// Встроенный браузер codefling.com. Скачанные .cs/.zip автоматически
// устанавливаются в carbon/plugins — вкладка «Плагины» подхватит после скана.
public partial class StorePage : Page
{
    private const string Home = "https://codefling.com/?category=rust-plugins";
    private string _dlDir = "";
    private FileSystemWatcher? _watcher;

    public StorePage()
    {
        InitializeComponent();
        Loaded += (_, _) => InitWeb();
        Unloaded += (_, _) => StopWatcher();
    }

    private async void InitWeb()
    {
        try
        {
            // Профиль WebView2 в data\ — сессия codefling (логин/куки) переживает перезапуск
            var dataDir = Path.Combine(App.DataDir, "webview2");
            Directory.CreateDirectory(dataDir);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await Web.EnsureCoreWebView2Async(env);

            _dlDir = Path.Combine(App.AppRoot, "downloads");
            Directory.CreateDirectory(_dlDir);
            Web.CoreWebView2.Profile.DefaultDownloadFolderPath = _dlDir;
            Web.CoreWebView2.DownloadStarting += OnDownloadStarting;
            Web.CoreWebView2.SourceChanged += (_, _) =>
            {
                TxtUrl.Text = Web.Source.ToString();
                TxtStatus.Text = Web.Source.Host == "codefling.com" ? "codefling.com" : Web.Source.Host;
            };
            StartWatcher();
            Web.Source = new Uri(Home);
        }
        catch (Exception ex)
        {
            Web.Visibility = Visibility.Collapsed;
            WebFallback.Visibility = Visibility.Visible;
            TxtStatus.Text = "WebView2: " + ex.Message;
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // прячем стандартный диалог — файл кладётся прямо в downloads\
        e.Handled = true;
        var name = Path.GetFileName(e.ResultFilePath);
        TxtDll.Text = $"⬇ {name}";
    }

    // Следим за downloads\: новые .cs копируем в plugins, .zip — распаковываем .cs
    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_dlDir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnDownloaded;
        _watcher.Renamed += OnDownloaded;
    }

    private void StopWatcher() { _watcher?.Dispose(); _watcher = null; }

    private void OnDownloaded(object sender, FileSystemEventArgs e)
    {
        // файл может быть ещё недописан — ждём, пока отпустит
        var path = e.FullPath;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".cs" && ext != ".zip") return;

        System.Threading.Tasks.Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await System.Threading.Tasks.Task.Delay(700);
                try { using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); break; }
                catch { }
            }
            try
            {
                int installed = 0;
                if (ext == ".cs") installed += InstallCs(path);
                else
                {
                    using var zip = ZipFile.OpenRead(path);
                    foreach (var entry in zip.Entries)
                    {
                        if (!entry.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                        var target = Path.Combine(App.Settings.PluginsDir, Path.GetFileName(entry.FullName));
                        Directory.CreateDirectory(App.Settings.PluginsDir);
                        entry.ExtractToFile(target, true);
                        installed++;
                    }
                }
                var msg = installed > 0
                    ? $"✓ установлено плагинов: {installed} ({Path.GetFileName(path)})"
                    : $"скачан {Path.GetFileName(path)} — .cs не найден";
                Dispatcher.Invoke(() => TxtDll.Text = msg);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => TxtDll.Text = "ошибка установки: " + ex.Message);
            }
        });
    }

    private int InstallCs(string src)
    {
        Directory.CreateDirectory(App.Settings.PluginsDir);
        File.Copy(src, Path.Combine(App.Settings.PluginsDir, Path.GetFileName(src)), true);
        return 1;
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoBack == true) Web.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoForward == true) Web.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) { Web.CoreWebView2?.Reload(); }
    private void Home_Click(object sender, RoutedEventArgs e) { Web.Source = new Uri(Home); }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Home) { UseShellExecute = true }); } catch { }
    }

    // Сверка версий установленных плагинов с codefling — проставляет статус на карточках
    private async void CheckVersions_Click(object sender, RoutedEventArgs e)
    {
        TxtStatus.Text = "сверяю версии с codefling…";
        var plugins = App.Plugins.Scan();
        var svc = new CodeflingService();
        int found = 0, updates = 0;
        foreach (var p in plugins)
        {
            var hit = await svc.FindLatestAsync(p.Name);
            if (hit == null) { p.CfStatus = "—"; continue; }
            found++;
            p.CfVersion = hit.Value.Version;
            p.CfUrl = hit.Value.Url;
            var newer = CodeflingService.IsNewer(hit.Value.Version, p.Version);
            if (newer) updates++;
            p.CfStatus = newer ? $"↑ {hit.Value.Version}" : $"✓ {hit.Value.Version}";
            TxtStatus.Text = $"проверено {found}/{plugins.Count}…";
        }
        // карточки на странице «Плагины» перечитают статус при следующем скане;
        // здесь показываем итог
        TxtStatus.Text = $"codefling: найдено {found} из {plugins.Count}, обновлений {updates}";
        PluginService.SaveCfCache(plugins);
    }
}
