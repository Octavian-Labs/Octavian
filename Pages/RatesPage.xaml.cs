using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OctavianRSM.Models;

namespace OctavianRSM.Pages;

// Рейты сбора/крафта (convars server.cfg + RCON) и библиотека пресетов лута для BetterLoot/MagicLoot
public partial class RatesPage : Page
{
    // слайдер → секция GatherManager.json (плагин перебивает ванильные convars)
    private static readonly Dictionary<string, string> RateKeys = new()
    {
        ["SldDisp"] = "GatherResourceModifiers",      // сбор инструментом
        ["SldPickup"] = "PickupResourceModifiers",    // подбор с земли
        ["SldQuarry"] = "QuarryResourceModifiers",    // карьеры
        ["SldSurvey"] = "SurveyResourceModifiers",    // survey-заряды
        ["SldScale"] = "GatherDispenserModifiers",    // объём узлов (dispenser.scale)
        ["SldCraft"] = "craft.timescale",             // честный convar Rust → server.cfg
    };

    private string GatherCfgPath
        => Path.Combine(App.Settings.CarbonConfigDir, "GatherManager.json");

    public RatesPage()
    {
        InitializeComponent();
        LoadRates();
        DetectLootPlugins();
        RefreshPresets();
    }

    // ── Рейты ────────────────────────────────────────────────

    private void LoadRates()
    {
        App.ServerCfg.Load();
        // GatherManager.json — реальный источник множителей (convars gather.rate он перекрывает)
        System.Text.Json.Nodes.JsonObject? opts = null;
        try
        {
            if (File.Exists(GatherCfgPath))
                opts = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(GatherCfgPath))
                    ?["Options"]?.AsObject();
        }
        catch { }
        foreach (var (slider, key) in RateKeys)
        {
            var ctl = (Slider)FindName(slider)!;
            double v = 1;
            if (key == "craft.timescale")
            {
                double.TryParse(App.ServerCfg[key], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v);
            }
            else if (opts != null)
            {
                if (key == "GatherDispenserModifiers")
                    v = opts[key]?["Tree"]?.GetValue<double>()
                        ?? opts[key]?["*"]?.GetValue<double>() ?? 1;
                else
                    v = opts[key]?["*"]?.GetValue<double>() ?? 1;
            }
            if (v > 0) ctl.Value = Math.Clamp(v, ctl.Minimum, ctl.Maximum);
        }
    }

    private void Rate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider s && s.Tag is string lbl && FindName(lbl) is TextBlock t)
            t.Text = $"×{s.Value:0.#}";
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !double.TryParse(fe.Tag?.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return;
        foreach (var slider in RateKeys.Keys)
            ((Slider)FindName(slider)!).Value = v;
    }

    // ── Применение: GatherManager.json + c.reload; craft.timescale → cfg+RCON ─
    private async void ApplyRates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var node = File.Exists(GatherCfgPath)
                ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(GatherCfgPath))!.AsObject()
                : new System.Text.Json.Nodes.JsonObject();
            var opts = node["Options"]?.AsObject()
                       ?? (System.Text.Json.Nodes.JsonObject)(node["Options"] = new System.Text.Json.Nodes.JsonObject());
            foreach (var (slider, key) in RateKeys)
            {
                var v = ((Slider)FindName(slider)!).Value;
                if (key == "craft.timescale") continue;
                if (opts[key] is not System.Text.Json.Nodes.JsonObject o)
                    o = (System.Text.Json.Nodes.JsonObject)(opts[key] = new System.Text.Json.Nodes.JsonObject());
                if (key == "GatherDispenserModifiers")
                    foreach (var d in new[] { "Tree", "Ore", "Flesh" }) o[d] = v;
                else
                    o["*"] = v;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(GatherCfgPath)!);
            File.WriteAllText(GatherCfgPath, node.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // craft.timescale — честный convar Rust
            var craft = ((Slider)FindName("SldCraft")!).Value.ToString("0.#",
                System.Globalization.CultureInfo.InvariantCulture);
            App.ServerCfg["craft.timescale"] = craft;
            App.ServerCfg.Save();

            if (App.Rcon.IsConnected)
            {
                var r = await App.Rcon.SendAsync("c.reload GatherManager");
                await App.Rcon.SendAsync($"craft.timescale {craft}");
                TxtStatus.Text = $"✓ рейты применены (GatherManager.json + reload → {Trim(r)})";
            }
            else
                TxtStatus.Text = "✓ рейты записаны в GatherManager.json — применятся при рестарте/reload";
        }
        catch (Exception ex) { TxtStatus.Text = $"ошибка: {ex.Message}"; }
    }

    // ── Лут-плагины ──────────────────────────────────────────

    private void DetectLootPlugins()
    {
        var dir = App.Settings.PluginsDir;
        var bl = File.Exists(Path.Combine(dir, "BetterLoot.cs"));
        var ml = File.Exists(Path.Combine(dir, "MagicLoot.cs"));
        LoadLootMults();
        TxtBL.Text = bl ? "✓ установлен" : "не найден в carbon\\plugins";
        TxtBL.Foreground = (System.Windows.Media.Brush)FindResource(bl ? "B.Success" : "B.Warn");
        TxtML.Text = ml ? "✓ установлен" : "не найден в carbon\\plugins";
        TxtML.Foreground = (System.Windows.Media.Brush)FindResource(ml ? "B.Success" : "B.Warn");
    }

    // ── Множители BetterLoot (бочки/ящики/трупы — их не касается GatherManager) ──

    private string BlCfgPath
        => Path.Combine(App.Settings.CarbonConfigDir, "BetterLoot.json");

    private void LoadLootMults()
    {
        try
        {
            if (!File.Exists(BlCfgPath)) return;
            var loot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(BlCfgPath))
                ?["Loot Configuration"]?.AsObject();
            if (loot == null) return;
            if (loot["Loot Multiplier"]?.GetValue<double>() is { } lm && lm > 0)
                SldLootMult.Value = Math.Clamp(lm, 1, 10);
            if (loot["Scrap Multipler"]?.GetValue<double>() is { } sm && sm > 0)
                SldScrapMult.Value = Math.Clamp(sm, 1, 10);
        }
        catch { }
    }

    private async void ApplyLootMult_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var node = File.Exists(BlCfgPath)
                ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(BlCfgPath))!.AsObject()
                : new System.Text.Json.Nodes.JsonObject();
            var loot = node["Loot Configuration"]?.AsObject()
                       ?? (System.Text.Json.Nodes.JsonObject)(node["Loot Configuration"] = new System.Text.Json.Nodes.JsonObject());
            loot["Loot Multiplier"] = (int)SldLootMult.Value;
            loot["Scrap Multipler"] = (int)SldScrapMult.Value;
            File.WriteAllText(BlCfgPath, node.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (App.Rcon.IsConnected)
            {
                var r = await App.Rcon.SendAsync("c.reload BetterLoot");
                TxtStatus.Text = $"✓ лут ×{(int)SldLootMult.Value} / скрап ×{(int)SldScrapMult.Value} · c.reload → {Trim(r)}";
            }
            else
                TxtStatus.Text = "✓ записано в BetterLoot.json — применится при рестарте";
        }
        catch (Exception ex) { TxtStatus.Text = $"ошибка: {ex.Message}"; }
    }

    private void OpenBL_Click(object s, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://codefling.com/plugins/betterloot") { UseShellExecute = true });

    private void OpenML_Click(object s, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://codefling.com/plugins/magicloot") { UseShellExecute = true });

    private void OpenPlugins_Click(object s, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", App.Settings.PluginsDir) { UseShellExecute = true });

    private void OpenBLCfg_Click(object s, RoutedEventArgs e)
    {
        var cfg = Path.Combine(App.Settings.CarbonConfigDir, "BetterLoot.json");
        if (File.Exists(cfg))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{cfg}\"") { UseShellExecute = true });
        else
            TxtStatus.Text = "BetterLoot.json ещё не создан — плагин сгенерирует его при первом запуске";
    }

    private async void ReloadBL_Click(object s, RoutedEventArgs e)
    {
        if (!App.Rcon.IsConnected) { TxtStatus.Text = "RCON не подключён"; return; }
        var r = await App.Rcon.SendAsync("c.reload BetterLoot");
        TxtStatus.Text = $"c.reload BetterLoot → {Trim(r)}";
    }

    // ── Пресеты лута ─────────────────────────────────────────

    private void RefreshPresets()
    {
        LstPresets.ItemsSource = null;
        LstPresets.ItemsSource = App.Settings.LootPresets;
        LstPresets.DisplayMemberPath = "Name";
    }

    private void Preset_Selected(object s, SelectionChangedEventArgs e)
    {
        if (LstPresets.SelectedItem is not LootPreset p) return;
        In_PresetName.Text = p.Name;
        In_PresetDesc.Text = p.Description;
        In_PresetCode.Text = p.Content;
        foreach (ComboBoxItem it in CmbPresetTarget.Items)
            if ((it.Tag as string) == p.Target) { CmbPresetTarget.SelectedItem = it; break; }
    }

    private void NewPreset_Click(object s, RoutedEventArgs e)
    {
        var p = new LootPreset { Name = "Новый пресет", Description = "", Content = "{}" };
        App.Settings.LootPresets.Add(p);
        App.Settings.Save(App.SettingsPath);
        RefreshPresets();
        LstPresets.SelectedItem = p;
    }

    private void SavePreset_Click(object s, RoutedEventArgs e)
    {
        if (LstPresets.SelectedItem is not LootPreset p) { TxtStatus.Text = "выберите пресет слева"; return; }
        p.Name = In_PresetName.Text.Trim();
        p.Description = In_PresetDesc.Text.Trim();
        p.Content = In_PresetCode.Text;
        p.Target = (CmbPresetTarget.SelectedItem as ComboBoxItem)?.Tag as string ?? "BetterLoot";
        App.Settings.Save(App.SettingsPath);
        RefreshPresets();
        TxtStatus.Text = $"✓ пресет «{p.Name}» сохранён";
    }

    private void DeletePreset_Click(object s, RoutedEventArgs e)
    {
        if (LstPresets.SelectedItem is not LootPreset p) return;
        if (MessageBox.Show($"Удалить пресет «{p.Name}»?", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.Settings.LootPresets.Remove(p);
        App.Settings.Save(App.SettingsPath);
        RefreshPresets();
    }

    private async void ApplyPreset_Click(object s, RoutedEventArgs e)
    {
        if (LstPresets.SelectedItem is not LootPreset p) { TxtStatus.Text = "выберите пресет слева"; return; }
        // пресет-код looty.cc: содержимое = looty-id, цель "looty" → шлём команду плагину
        if (p.Target.Equals("looty", StringComparison.OrdinalIgnoreCase))
        {
            if (!App.Rcon.IsConnected) { TxtStatus.Text = "RCON не подключён"; return; }
            var r = await App.Rcon.SendAsync($"looty {p.Content.Trim()}");
            TxtStatus.Text = $"looty {p.Content.Trim()} → {Trim(r)}";
            return;
        }
        var dataDir = Path.Combine(App.Settings.ServerDirPath, "carbon", "data");
        var path = p.Target switch
        {
            "BetterLootTables" => Path.Combine(dataDir, "BetterLoot", "LootTables.json"),
            "BetterLootGroups" => Path.Combine(dataDir, "BetterLoot", "LootGroups.json"),
            "BetterLootBlacklist" => Path.Combine(dataDir, "BetterLoot", "Blacklist.json"),
            _ => Path.Combine(App.Settings.CarbonConfigDir, $"{p.Target}.json"),
        };
        try
        {
            // бэкап текущего конфига перед перезаписью
            if (File.Exists(path))
                File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllText(path, In_PresetCode.Text);
            TxtStatus.Text = $"✓ записано в {Path.GetFileName(path)}";
            if (App.Rcon.IsConnected)
            {
                // для data-файлов BetterLoot перезагружаем сам плагин
                var plugin = p.Target.StartsWith("BetterLoot") ? "BetterLoot" : p.Target;
                var r = await App.Rcon.SendAsync($"c.reload {plugin}");
                TxtStatus.Text += $" · c.reload → {Trim(r)}";
            }
        }
        catch (Exception ex) { TxtStatus.Text = $"ошибка записи: {ex.Message}"; }
    }

    private static string Trim(string s) => s.Length > 80 ? s[..80] + "…" : s;
}
