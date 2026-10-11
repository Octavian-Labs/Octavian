using System.Windows;
using System.Windows.Controls;
using OctavianRSM.Services;

namespace OctavianRSM.Pages;

/// <summary>
/// Автоматика: watchdog и авторестарт по расписанию.
/// Пути/режим — «Подключение», параметры сервера — «Сервер → Параметры».
/// </summary>
public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => AppNav.SelectTab(this);
        var s = App.Settings;
        ChkWatchdog.IsChecked = s.WatchdogEnabled;
        NumWatchdog.Value = s.WatchdogDelaySec;
        ChkAutoRestart.IsChecked = s.AutoRestartEnabled;
        TxtRestartTimes.Text = s.AutoRestartTimes;
        TxtRestartWarn.Text = s.AutoRestartWarnSec.ToString();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.WatchdogEnabled = ChkWatchdog.IsChecked == true;
        if (NumWatchdog.Value.HasValue) s.WatchdogDelaySec = (int)NumWatchdog.Value;
        s.AutoRestartEnabled = ChkAutoRestart.IsChecked == true;
        s.AutoRestartTimes = TxtRestartTimes.Text.Trim();
        if (int.TryParse(TxtRestartWarn.Text, out var warn)) s.AutoRestartWarnSec = warn;
        s.Save(App.SettingsPath);
        TxtSaved.Text = "Сохранено в " + App.SettingsPath;
    }
}
