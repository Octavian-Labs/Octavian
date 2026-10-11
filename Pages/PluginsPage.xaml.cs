using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OctavianRSM.Services;
using Wpf.Ui.Controls;

namespace OctavianRSM.Pages;

public partial class PluginsPage : Page
{
    private List<PluginInfo> _all = new();

    public PluginsPage()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        _all = App.Plugins.Scan();
        App.Plugins.ApplyCfCache(_all); // статусы codefling из кэша
        ModuleCards.ItemsSource = App.Plugins.ScanModules();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = TxtSearch.Text.Trim();
        var items = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || p.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || p.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Cards.ItemsSource = items;
        TxtCount.Text = $"{items.Count(p => p.Enabled)} включено из {items.Count}";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", App.Settings.PluginsDir); } catch { }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts || ts.DataContext is not PluginInfo p) return;
        var want = ts.IsChecked == true;
        if (!App.Plugins.SetEnabled(p, want))
        {
            ts.IsChecked = p.Enabled;
            TxtStatus.Text = $"Ошибка переименования {p.FileName}";
            return;
        }
        TxtStatus.Text = $"{p.Name}: {(want ? "включён" : "выключен")}";
        if (want && App.Rcon.IsConnected) _ = App.Rcon.FireAsync($"c.reload {p.Name}");
        ApplyFilter();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not PluginInfo p) return;
        TxtStatus.Text = $"c.reload {p.Name}…";
        var resp = await App.Rcon.SendAsync($"c.reload {p.Name}");
        TxtStatus.Text = string.IsNullOrEmpty(resp) ? "отправлено" : resp;
    }

    // ⚙ — окно настроек плагина: таблица значений + JSON-редактор
    private void Config_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not PluginInfo p) return;
        // имя файла конфига = имя класса плагина (файл без расширения)
        var cfgName = Path.GetFileNameWithoutExtension(p.FileName.Replace(".disabled", ""));
        new PluginConfigWindow(cfgName) { Owner = Window.GetWindow(this) }.Show();
    }

    private void ModuleToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts || ts.DataContext is not CarbonModule m) return;
        var want = ts.IsChecked == true;
        if (!App.Plugins.SetModuleEnabled(m, want))
        {
            ts.IsChecked = m.Enabled;
            TxtStatus.Text = $"Ошибка записи config.json: {m.Name}";
            return;
        }
        TxtStatus.Text = $"Модуль {m.Name}: {(want ? "вкл" : "выкл")}";
        if (App.Rcon.IsConnected) _ = App.Rcon.FireAsync($"c.loadmoduleconfig {m.Name}");
    }

    // Перезагрузка всех модулей Carbon — переехала из «Управление → Carbon/Плагины»
    private async void ReloadModules_Click(object sender, RoutedEventArgs e)
    {
        TxtStatus.Text = "c.reloadmodules…";
        TxtStatus.Text = (await App.Ctx.RunRconAsync("c.reloadmodules")).Trim();
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not PluginInfo p) return;
        if (System.Windows.MessageBox.Show($"Удалить {p.Name}?\nФайл и конфиг переедут в plugins\\_trash (восстановимо).",
                "OctavianRSM", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        TxtStatus.Text = App.Plugins.Uninstall(p) ? $"Удалён: {p.Name}" : $"Ошибка удаления {p.Name}";
        Refresh();
    }
}
