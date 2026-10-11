using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace OctavianRSM.Pages;

public partial class InstallPage : Page
{
    public InstallPage()
    {
        InitializeComponent();
        App.SteamCmd.OnOutput += SteamCmd_OnOutput;
        // отписка при уходе со страницы — иначе каждый визит добавляет подписчика
        Unloaded += (_, _) => App.SteamCmd.OnOutput -= SteamCmd_OnOutput;
    }

    private void SteamCmd_OnOutput(string line) => Dispatcher.Invoke(() => Append(line));

    private void Append(string line)
    {
        var kind = line.StartsWith(">>>") ? "sys"
                 : line.Contains("error", StringComparison.OrdinalIgnoreCase) ? "err" : "log";
        Log.AddLine(line, kind);
    }

    private string Branch => (CmbBranch.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "release";

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var branch = Branch == "release" ? "" : Branch;
        Append($">>> steamcmd app_update 258550 {(branch == "" ? "" : "-beta " + branch)}");
        var code = await App.SteamCmd.InstallOrUpdateAsync(validate: false, branch);
        Append($">>> завершено, код {code}");
    }

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        var branch = Branch == "release" ? "" : Branch;
        Append($">>> steamcmd validate");
        var code = await App.SteamCmd.InstallOrUpdateAsync(validate: true, branch);
        Append($">>> завершено, код {code}");
    }

    // проверка версии Carbon против последнего релиза на GitHub (как Mod Framework в RSM Web)
    private async void CarbonCheck_Click(object sender, RoutedEventArgs e)
    {
        TxtCarbonVer.Text = "проверяю…";
        try
        {
            var local = LocalCarbonVersion();
            using var http = Services.BootstrapService.NewHttp();
            var asset = await Services.BootstrapService.ResolveCarbonAssetAsync(http, default);
            var tag = asset?.Tag ?? "?";
            TxtCarbonVer.Text = local == null
                ? $"последний релиз: {tag}\nлокальная версия не найдена"
                : $"последний релиз: {tag}\nустановлено: {local}";
        }
        catch (Exception ex)
        {
            TxtCarbonVer.Text = $"ошибка проверки: {ex.Message}";
        }
    }

    // Скачивает последний релиз Carbon (Windows-сборка) и распаковывает поверх папки сервера
    private async void CarbonInstall_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server.IsRunning)
        {
            Append(">>> сервер запущен — остановите его перед обновлением Carbon");
            return;
        }
        if (MessageBox.Show("Скачать и распаковать последний Carbon поверх сервера?\n(существующие конфиги/данные не трогаем — только бинарники)",
                "OctavianRSM — Carbon", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Append(">>> carbon: получаю список релизов…");
        try
        {
            using var http = Services.BootstrapService.NewHttp();
            var asset = await Services.BootstrapService.ResolveCarbonAssetAsync(http, default);
            if (asset == null)
            {
                Append(">>> carbon: windows-архив не найден в релизе production_build");
                return;
            }
            Append($">>> carbon: {asset.Tag} → {asset.Name}");
            var zip = Path.Combine(Path.GetTempPath(), asset.Name);
            var bytes = await http.GetByteArrayAsync(asset.Url);
            await File.WriteAllBytesAsync(zip, bytes);
            Append($">>> carbon: скачано {bytes.Length / 1024} KB, распаковываю в {App.Settings.ServerDirPath}");
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, App.Settings.ServerDirPath, overwriteFiles: true);
            await File.WriteAllTextAsync(Path.Combine(App.Settings.ServerDirPath, "carbon", "version"), asset.Tag);
            Append(">>> carbon: готово. Перезапустите сервер.");
            TxtCarbonVer.Text = $"установлено: {asset.Tag}";
        }
        catch (Exception ex)
        {
            Append($">>> carbon: ошибка — {ex.Message}");
        }
    }

    // версия Carbon из лога/файлов — ищем carbon/version или читаем Carbon.Core.log
    private static string? LocalCarbonVersion()
    {
        var verFile = Path.Combine(App.Settings.ServerDirPath, "carbon", "version");
        if (File.Exists(verFile)) return File.ReadAllText(verFile).Trim();
        var log = Path.Combine(App.Settings.ServerDirPath, "carbon", "logs", "Carbon.Core.log");
        if (!File.Exists(log)) return null;
        var line = File.ReadLines(log)
            .FirstOrDefault(l => l.Contains("Carbon", StringComparison.OrdinalIgnoreCase)
                              && (l.Contains("v2.") || l.Contains("v1.")));
        return line?.Trim();
    }
}
