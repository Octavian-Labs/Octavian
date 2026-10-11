using System;
using System.Windows;
using System.Windows.Controls;
using OctavianRSM.Services;

namespace OctavianRSM.Pages;

public partial class WipePage : Page
{
    private readonly WipeService _wipe = new(App.Settings);

    public WipePage()
    {
        InitializeComponent();
        SwAuto.IsChecked = App.Settings.AutoWipeEnabled;
        NumDays.Value = App.Settings.AutoWipeIntervalDays;
        ChkFull.IsChecked = App.Settings.AutoWipeFull;
        RefreshStats();
    }

    private void RefreshStats()
    {
        var last = _wipe.LastWipe();
        TxtLastWipe.Text = last?.ToString("dd.MM.yyyy") ?? "нет данных";
        TxtFiles.Text = $"файлов карты: {_wipe.MapFiles().Count} · игроков: {_wipe.PlayerFiles().Count}";

        var next = _wipe.NextWipe();
        TxtNextWipe.Text = next?.ToString("dd.MM.yyyy") ?? "—";
        if (next.HasValue)
        {
            var left = next.Value - DateTime.Now;
            TxtCountdown.Text = left.TotalSeconds <= 0
                ? "ПРОСРОЧЕН — пора вайпать"
                : $"осталось: {left.Days}д {left.Hours}ч {left.Minutes}м";
        }
        else TxtCountdown.Text = "включите автовайп или сделайте первый вайп";
    }

    private void SaveCfg_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.AutoWipeEnabled = SwAuto.IsChecked == true;
        App.Settings.AutoWipeIntervalDays = (int)Math.Max(1, NumDays.Value ?? 7);
        App.Settings.AutoWipeFull = ChkFull.IsChecked == true;
        App.Settings.Save(App.SettingsPath);
        TxtResult.Text = "✓ настройки автовайпа сохранены";
        RefreshStats();
    }

    private void MapWipe_Click(object sender, RoutedEventArgs e) => DoWipe(false);
    private void FullWipe_Click(object sender, RoutedEventArgs e) => DoWipe(true);

    private void DoWipe(bool full)
    {
        if (!App.Ctx.CanWipe)
        {
            TxtResult.Text = "Сервер работает — сначала остановите его (Дашборд → ОСТАНОВИТЬ).";
            return;
        }
        var what = full ? "ПОЛНЫЙ вайп (карта + блюпринты)" : "вайп карты (.sav)";
        if (MessageBox.Show($"Выполнить {what}?\nФайлы будут ПЕРЕМЕЩЕНЫ в _wipe_backup (восстановимо).",
                "OctavianRSM — вайп", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var (moved, dir) = _wipe.Execute(full);
        TxtResult.Text = moved == 0
            ? "Файлов сохранения не найдено — вайпать нечего."
            : $"✓ Перемещено файлов: {moved} → {dir}";
        // опция как в RSM Web: после вайпа генерируем новый seed для следующей карты;
        // канон — server.cfg, json-зеркало — для launch-аргументов
        if (moved > 0 && ChkSeed.IsChecked == true)
        {
            var seed = (uint)Random.Shared.Next(1, int.MaxValue);
            App.Settings.WorldSeed = seed;
            App.Settings.Save(App.SettingsPath);
            App.ServerCfg["server.seed"] = seed.ToString();
            App.ServerCfg.Save();
            TxtResult.Text += $" · новый seed: {seed}";
        }
        RefreshStats();
    }
}
