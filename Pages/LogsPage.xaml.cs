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
    // прилипание журнала: _bulk — заливка истории без прокрутки,
    // _stick — держим низ; пользователь уходит вверх → пауза, возврат через ScrollResumeDelay
    private bool _bulk;
    private bool _stick = true;
    private DateTime _lastUserScroll = DateTime.MinValue;
    private static readonly TimeSpan ScrollResumeDelay = TimeSpan.FromSeconds(18);

    public LogsPage()
    {
        InitializeComponent();
        LoadLogs();
        App.Server.OnOutput += Server_OnOutput;
        Unloaded += LogsPage_Unloaded;
        Loaded += (_, _) => JumpToEnd(); // вход на вкладку — журнал сразу на последней строке
        LoadHistory(); // журнал переживает переходы по вкладкам — читаем из SQLite
        _ = StartTailWhenNeeded();
        _ = StickLoop();
    }

    private void LogsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _tail.Cancel();
        App.Server.OnOutput -= Server_OnOutput;
    }

    // ══ Живой журнал (переехал с дашборда) ═════════════════════════════════
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

    // Tail server.log — fallback: если процесс запущен не нами и stdout нам недоступен,
    // либо Unity молчит в stdout. Если сервер наш и консоль пишет — tail не нужен (дубли строк).
    private async System.Threading.Tasks.Task StartTailWhenNeeded()
    {
        if (App.Settings.IsRemoteMode) return;
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

    // Пауза прилипания: пользователь отпустил журнал — через задержку возвращаем автоскролл
    private async System.Threading.Tasks.Task StickLoop()
    {
        while (!_tail.Cancelled)
        {
            await System.Threading.Tasks.Task.Delay(5000);
            if (_stick) continue;
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
        if (lf == null) { TxtLog.Text = ""; return; }
        try
        {
            var filter = TxtFilter.Text.Trim();
            // потоковое чтение: TakeLast держит в памяти только хвост, а не весь лог
            var lines = File.ReadLines(lf.Path).TakeLast(3000);
            if (filter.Length > 0)
                lines = lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
            TxtLog.Text = string.Join("\n", lines);
        }
        catch (Exception ex) { TxtLog.Text = "// " + ex.Message; }
        Scroll.ScrollToEnd();
    }

    private void CmbLog_Changed(object sender, SelectionChangedEventArgs e) => ShowCurrent();
    private void Refresh_Click(object sender, RoutedEventArgs e) => ShowCurrent();
    private void ReloadList_Click(object sender, RoutedEventArgs e) => LoadLogs();
    private void Filter_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ShowCurrent();
    }
}
