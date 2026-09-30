using System.Windows;
using System.Windows.Controls;

namespace OctavianRSM.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        var s = App.Settings;
        TxtServerDir.Text = s.ServerDir;
        TxtSteamCmd.Text = s.SteamCmdExe;
        TxtRconHost.Text = s.RconHost;
        foreach (ComboBoxItem it in CmbDeploymentMode.Items)
            if (it.Content?.ToString() == s.DeploymentMode) { CmbDeploymentMode.SelectedItem = it; break; }
        if (CmbDeploymentMode.SelectedIndex < 0) CmbDeploymentMode.SelectedIndex = 0;
        TxtGamePort.Text = s.GamePort.ToString();
        TxtRconPort.Text = s.RconPort.ToString();
        TxtQueryPort.Text = s.QueryPort.ToString();
        TxtRconPassword.Password = s.RconPassword;
        TxtSeed.Text = s.WorldSeed.ToString();
        TxtWorldSize.Text = s.WorldSize.ToString();
        TxtMaxPlayers.Text = s.MaxPlayers.ToString();
        TxtIdentity.Text = s.Identity;
        TxtAppPort.Text = s.AppPort.ToString();
        TxtSaveInterval.Text = s.SaveInterval.ToString();
        // если тип карты из конфига не входит в список — добавляем его, чтобы не затирать
        bool found = false;
        foreach (ComboBoxItem it in CmbLevel.Items)
            if (it.Content?.ToString() == s.Level) { CmbLevel.SelectedItem = it; found = true; break; }
        if (!found && !string.IsNullOrWhiteSpace(s.Level))
        {
            var extra = new ComboBoxItem { Content = s.Level };
            CmbLevel.Items.Add(extra);
            CmbLevel.SelectedItem = extra;
        }
        if (CmbLevel.SelectedIndex < 0) CmbLevel.SelectedIndex = 0;
        TxtLevelUrl.Text = s.LevelUrl;
        foreach (ComboBoxItem it in CmbGamemode.Items)
            if (it.Content?.ToString() == s.Gamemode) { CmbGamemode.SelectedItem = it; break; }
        if (CmbGamemode.SelectedIndex < 0) CmbGamemode.SelectedIndex = 0;
        ChkWatchdog.IsChecked = s.WatchdogEnabled;
        NumWatchdog.Value = s.WatchdogDelaySec;
        ChkAutoRestart.IsChecked = s.AutoRestartEnabled;
        TxtRestartTimes.Text = s.AutoRestartTimes;
        TxtRestartWarn.Text = s.AutoRestartWarnSec.ToString();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        // пути храним относительно корня, когда возможно — переносимость папки сервера
        s.ServerDir = s.MakePortablePath(TxtServerDir.Text.Trim());
        s.SteamCmdExe = s.MakePortablePath(TxtSteamCmd.Text.Trim());
        s.DeploymentMode = (CmbDeploymentMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Local";
        s.RconHost = TxtRconHost.Text.Trim();
        // при неудачном парсинге оставляем старое значение — иначе опечатка обнуляет порт/seed
        if (int.TryParse(TxtGamePort.Text, out var gp)) s.GamePort = gp;
        if (int.TryParse(TxtRconPort.Text, out var rp)) s.RconPort = rp;
        if (int.TryParse(TxtQueryPort.Text, out var qp)) s.QueryPort = qp;
        s.RconPassword = TxtRconPassword.Password;
        if (uint.TryParse(TxtSeed.Text, out var seed)) s.WorldSeed = seed;
        if (int.TryParse(TxtWorldSize.Text, out var ws)) s.WorldSize = ws;
        if (int.TryParse(TxtMaxPlayers.Text, out var mp)) s.MaxPlayers = mp;
        s.Identity = TxtIdentity.Text.Trim();
        if (int.TryParse(TxtAppPort.Text, out var ap)) s.AppPort = ap;
        if (int.TryParse(TxtSaveInterval.Text, out var si)) s.SaveInterval = si;
        s.Level = (CmbLevel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Procedural Map";
        s.LevelUrl = TxtLevelUrl.Text.Trim();
        s.Gamemode = (CmbGamemode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "vanilla";
        s.WatchdogEnabled = ChkWatchdog.IsChecked == true;
        if (NumWatchdog.Value.HasValue) s.WatchdogDelaySec = (int)NumWatchdog.Value;
        s.AutoRestartEnabled = ChkAutoRestart.IsChecked == true;
        s.AutoRestartTimes = TxtRestartTimes.Text.Trim();
        if (int.TryParse(TxtRestartWarn.Text, out var warn)) s.AutoRestartWarnSec = warn;
        s.Save(App.SettingsPath);
        TxtSaved.Text = "Сохранено в " + App.SettingsPath;
    }
}
