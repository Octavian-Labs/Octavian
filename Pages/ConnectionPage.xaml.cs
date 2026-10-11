using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OctavianRSM.Services;

namespace OctavianRSM.Pages;

/// <summary>
/// Подключение: режим Local/Remote, доступ к RCON (владелец rcon.host/port/password),
/// локальные пути сервера. Порты самого сервера — в «Сервер → Параметры».
/// </summary>
public partial class ConnectionPage : Page
{
    public ConnectionPage()
    {
        InitializeComponent();
        var s = App.Settings;
        TxtHost.Text = s.RconHost;
        TxtPort.Text = s.RconPort.ToString();
        TxtPassword.Password = s.RconPassword;
        ChkWebSocket.IsChecked = s.RconWebSocket;
        TxtServerDir.Text = s.ServerDir;
        TxtSteamCmd.Text = s.SteamCmdExe;
        foreach (ComboBoxItem it in CmbMode.Items)
            if (it.Content?.ToString() == s.DeploymentMode) { CmbMode.SelectedItem = it; break; }
        if (CmbMode.SelectedIndex < 0) CmbMode.SelectedIndex = 0;
        App.Ctx.Changed += Ctx_Changed;
        Unloaded += (_, _) => App.Ctx.Changed -= Ctx_Changed;
        ApplyState();
    }

    private void Ctx_Changed()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ApplyState); return; }
        ApplyState();
    }

    private void ApplyState()
    {
        var ctx = App.Ctx;
        DotRcon.Fill = (SolidColorBrush)FindResource(ctx.RconConnected ? "B.Success" : "B.Danger");
        TxtRconState.Text = ctx.RconConnected ? "подключён" : "offline";
        bool remote = IsRemoteSelected;
        TxtModeHint.Text = remote
            ? "Remote: управление только через RCON — файлы, процесс и установка недоступны."
            : "Local: менеджер запускает и обслуживает RustDedicated.exe в папке сервера.";
        PnlLocal.Opacity = remote ? 0.45 : 1.0;
        PnlLocal.IsEnabled = !remote;
        bool exe = File.Exists(App.Settings.ServerExePath);
        TxtExeState.Text = remote ? "—" : exe ? "exe найден" : "exe НЕ найден";
        TxtExeState.Foreground = (SolidColorBrush)FindResource(
            remote ? "B.TextDim" : exe ? "B.Success" : "B.Danger");
        TxtExePath.Text = remote ? "" : App.Settings.ServerExePath;
    }

    private bool IsRemoteSelected =>
        ((CmbMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Local")
            .Equals("Remote", StringComparison.OrdinalIgnoreCase);

    private void Mode_Changed(object sender, SelectionChangedEventArgs e) => ApplyState();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.DeploymentMode = (CmbMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Local";
        s.RconHost = TxtHost.Text.Trim();
        if (int.TryParse(TxtPort.Text, out var rp)) s.RconPort = rp;
        s.RconPassword = TxtPassword.Password;
        s.RconWebSocket = ChkWebSocket.IsChecked == true;
        s.ServerDir = s.MakePortablePath(TxtServerDir.Text.Trim());
        s.SteamCmdExe = s.MakePortablePath(TxtSteamCmd.Text.Trim());
        s.Save(App.SettingsPath);
        App.Ctx.Refresh();
        ApplyState();
        TxtSaved.Text = "Сохранено. Параметры запуска применятся при следующем старте сервера.";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        TxtRconState.Text = "подключение…";
        var ok = await App.Rcon.ConnectAsync(s.RconHost, s.RconPort, s.RconPassword);
        App.Ctx.Refresh();
        TxtRconState.Text = ok ? "подключён" : "не удалось";
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await App.Rcon.DisconnectAsync();
        App.Ctx.Refresh();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
        => _ = await App.Ctx.RunRconAsync("serverinfo");

    private void OpenParams_Click(object sender, RoutedEventArgs e) => AppNav.Go("server.params");

    private void BrowseServer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите RustDedicated.exe в папке сервера",
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = "выбор папки",
            Filter = "RustDedicated|RustDedicated.exe|Все файлы|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        var dir = Path.GetDirectoryName(dlg.FileName);
        if (!string.IsNullOrEmpty(dir))
            TxtServerDir.Text = dir;
    }
}
