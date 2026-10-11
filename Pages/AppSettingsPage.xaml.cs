using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OctavianRSM.Services;

namespace OctavianRSM.Pages;

/// <summary>
/// Настройки приложения: версия и обновление, файлы менеджера, мастер установки.
/// Серверные настройки здесь не живут — «Подключение», «Параметры», «Автоматика».
/// </summary>
public partial class AppSettingsPage : Page
{
    public AppSettingsPage()
    {
        InitializeComponent();
        TxtVersion.Text = "v" + UpdateService.CurrentVersion();
        TxtCfgPath.Text = App.SettingsPath;
        TxtDataPath.Text = App.DataDir;
        TxtExePath.Text = Environment.ProcessPath ?? "";
        App.Updater.OnChecked += Updater_Checked;
        Unloaded += (_, _) => App.Updater.OnChecked -= Updater_Checked;
        if (App.Updater.Checked) ApplyUpdateState();
    }

    private void Updater_Checked()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ApplyUpdateState); return; }
        ApplyUpdateState();
    }

    private void ApplyUpdateState()
    {
        var latest = App.Updater.Latest;
        if (latest != null)
        {
            TxtUpdate.Text = $"доступно {latest.Tag}";
            TxtUpdate.Foreground = (SolidColorBrush)FindResource("B.Warn");
            BtnApplyUpdate.IsEnabled = true;
            BtnApplyUpdate.Content = $"УСТАНОВИТЬ {latest.Tag}";
            TxtUpdateNotes.Text = latest.Notes;
        }
        else
        {
            TxtUpdate.Text = "актуально";
            TxtUpdate.Foreground = (SolidColorBrush)FindResource("B.Success");
            BtnApplyUpdate.IsEnabled = false;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        TxtUpdate.Text = "проверка…";
        await App.Updater.CheckAsync(force: true);
        if (!App.Updater.Checked) { TxtUpdate.Text = "ошибка проверки"; return; }
        ApplyUpdateState();
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        var latest = App.Updater.Latest;
        if (latest == null) return;
        if (!UiConfirm.Ask(Window.GetWindow(this), "Установить обновление",
                $"{latest.Tag} ({latest.AssetName})",
                "менеджер скачает релиз и перезапустится автоматически")) return;
        BtnApplyUpdate.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            var file = await App.Updater.DownloadAsync(latest,
                new Progress<double>(p => UpdateProgress.Value = p));
            App.Updater.ApplyAndRestart(file);
        }
        catch (Exception ex)
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            BtnApplyUpdate.IsEnabled = true;
            TxtUpdate.Text = "ошибка: " + ex.Message;
        }
    }

    private void OpenCfg_Click(object sender, RoutedEventArgs e)
        => OpenPath(App.SettingsPath);

    private void OpenData_Click(object sender, RoutedEventArgs e)
        => OpenPath(App.DataDir);

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"")
            { UseShellExecute = true });
        }
        catch { }
    }

    private void Wizard_Click(object sender, RoutedEventArgs e)
    {
        if (!UiConfirm.Ask(Window.GetWindow(this), "Запустить мастер установки",
                "SetupWizardWindow",
                "текущие настройки могут быть перезаписаны")) return;
        var wizard = new SetupWizardWindow(App.Settings, true);
        if (wizard.ShowDialog() == true)
        {
            App.Settings.Save(App.SettingsPath);
            App.Ctx.Refresh();
        }
    }
}
