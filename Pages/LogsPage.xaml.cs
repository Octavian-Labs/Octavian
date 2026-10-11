using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OctavianRSM.Services;

namespace OctavianRSM.Pages;

public partial class LogsPage : Page
{
    private class LogFile { public string Name = ""; public string Path = ""; public override string ToString() => Name; }

    private CancellationTokenLike _tail = new();

    public LogsPage()
    {
        InitializeComponent();
        LoadLogs();
        App.Server.OnOutput += Server_OnOutput;
        Unloaded += LogsPage_Unloaded;
        Loaded += (_, _) => Log.ScrollToEnd(); // вход на вкладку — журнал сразу на последней строке
        LoadHistory(); // журнал переживает переходы по вкладкам — читаем из SQLite
        _ = StartTailWhenNeeded();
    }

    private void LogsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _tail.Cancel();
        App.Server.OnOutput -= Server_OnOutput;
    }

    // ══ Живой журнал (переехал с дашборда) ═════════════════════════════════
    private void LoadHistory()
    {
        foreach (var (ts, type, msg) in App.Db.RecentLogs(300))
        {
            var kind = type == "server" ? Guess(msg)
                     : type.EndsWith("Chat") ? "chat"
                     : type.EndsWith("Error") ? "err" : "log";
            AppendLog(msg, kind, ts.Length >= 19 ? ts[11..] : ts);
        }
    }

    // Tail server.log — fallback: если процесс запущен не нами и stdout нам недоступен,
    // либо Unity молчит в stdout. Если сервер наш и консоль пишет — tail не нужен (дубли строк).
    private async System.Threading.Tasks.Task StartTailWhenNeeded()
    {
        if (!App.Ctx.IsLocal) return;
        for (int i = 0; i < 20 && !_tail.Cancelled; i++)
        {
            if (App.Server.OwnedByUs && App.Server.ConsoleOutputSeen) return;
            await System.Threading.Tasks.Task.Delay(500);
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
        Log.AddLine($"[{(stamp ?? DateTime.Now.ToString("HH:mm:ss"))}] {line}", kind, brush);
    }

    private void Auto_Changed(object sender, RoutedEventArgs e) => Log.AutoScroll = ChkAuto.IsChecked == true;

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var text = Log.VisibleText();
        if (text.Length > 0) Clipboard.SetText(text);
    }

    // ══ Файлы логов ════════════════════════════════════════════════════════
    // Источники: server.log, server\<identity>\*.log, carbon\logs\*.log,
    // собственные логи RSM (data\*.log — crash.log, config-errors.log).
    private void LoadLogs()
    {
        var files = new List<LogFile>();
        var s = App.Settings;
        void Add(string path, string name)
        {
            if (File.Exists(path)) files.Add(new LogFile { Name = name, Path = path });
        }

        // dedicated-сервер
        Add(Path.Combine(s.ServerDirPath, "server.log"), "server\\server.log");
        var identLogs = Path.Combine(s.ServerDirPath, "server", s.Identity);
        if (Directory.Exists(identLogs))
            foreach (var f in Directory.GetFiles(identLogs, "*.log").OrderByDescending(f => File.GetLastWriteTime(f)).Take(5))
                files.Add(new LogFile { Name = $"server\\{s.Identity}\\{Path.GetFileName(f)}", Path = f });

        // Carbon
        var carbonLogs = Path.Combine(s.ServerDirPath, "carbon", "logs");
        if (Directory.Exists(carbonLogs))
            foreach (var f in Directory.GetFiles(carbonLogs, "*.log").OrderByDescending(f => File.GetLastWriteTime(f)).Take(10))
                files.Add(new LogFile { Name = "carbon\\" + Path.GetFileName(f), Path = f });

        // собственные логи менеджера
        var dataDir = Path.Combine(s.RootDir, "data");
        if (Directory.Exists(dataDir))
            foreach (var f in Directory.GetFiles(dataDir, "*.log").OrderByDescending(f => File.GetLastWriteTime(f)))
                files.Add(new LogFile { Name = "rsm\\" + Path.GetFileName(f), Path = f });

        CmbLog.ItemsSource = files;
        if (files.Count > 0) CmbLog.SelectedIndex = 0;
    }

    private void ShowCurrent()
    {
        var lf = CmbLog.SelectedItem as LogFile;
        FileLog.ClearLog();
        if (lf == null) return;
        try
        {
            // потоковое чтение: TakeLast держит в памяти только хвост, а не весь лог;
            // подстрока фильтрует представление FileLog.FilterText — живой фильтр
            var lines = File.ReadLines(lf.Path).TakeLast(3000);
            FileLog.AddLines(lines, "log");
        }
        catch (Exception ex) { FileLog.AddLine("// " + ex.Message, "err"); }
        FileLog.FilterText = TxtFilter.Text.Trim();
        FileLog.ScrollToEnd();
    }

    private void CmbLog_Changed(object sender, SelectionChangedEventArgs e) => ShowCurrent();
    private void Refresh_Click(object sender, RoutedEventArgs e) => ShowCurrent();
    private void ReloadList_Click(object sender, RoutedEventArgs e) => LoadLogs();
    private void Filter_Changed(object sender, TextChangedEventArgs e)
        => FileLog.FilterText = TxtFilter.Text.Trim();
}
