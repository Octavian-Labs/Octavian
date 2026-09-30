using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace OctavianRSM.Pages;

public partial class LogsPage : Page
{
    private class LogFile { public string Name = ""; public string Path = ""; public override string ToString() => Name; }

    public LogsPage()
    {
        InitializeComponent();
        LoadLogs();
    }

    private void LoadLogs()
    {
        var files = new List<LogFile>();
        var s = App.Settings;
        var serverLog = Path.Combine(s.ServerDirPath, "server.log");
        if (File.Exists(serverLog)) files.Add(new LogFile { Name = "server.log", Path = serverLog });
        var carbonLogs = Path.Combine(s.ServerDirPath, "carbon", "logs");
        if (Directory.Exists(carbonLogs))
            foreach (var f in Directory.GetFiles(carbonLogs, "*.log").OrderByDescending(f => File.GetLastWriteTime(f)).Take(10))
                files.Add(new LogFile { Name = "carbon\\" + Path.GetFileName(f), Path = f });
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
    private void Filter_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ShowCurrent();
    }
}
