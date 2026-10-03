using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OctavianRSM.Models;
using OctavianRSM.Services;

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
        LoadStackPanel();
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

            // название сервера само подтянет новые X-рейты, если включён авто-режим
            _ = ServerTitleBuilder.AutoApplyAsync();
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

    // ── Стаки (OctaStack): пресеты живут в конфиге плагина, применение = чистое состояние ──

    // релизный плагин приоритетнее dev-версии
    private string? StackPluginName
    {
        get
        {
            var dir = App.Settings.PluginsDir;
            if (File.Exists(Path.Combine(dir, "OctaStack.cs"))) return "OctaStack";
            if (File.Exists(Path.Combine(dir, "OctaStackTest.cs"))) return "OctaStackTest";
            return null;
        }
    }

    private string? StackCfgPath => StackPluginName == null ? null
        : Path.Combine(App.Settings.CarbonConfigDir, StackPluginName + ".json");

    private void LoadStackPanel()
    {
        var plugin = StackPluginName;
        TxtStackPlugin.Text = plugin == null ? "не найден в carbon\\plugins" : $"✓ {plugin}";
        TxtStackPlugin.Foreground = (System.Windows.Media.Brush)FindResource(
            plugin == null ? "B.Warn" : "B.Success");
        TxtStackState.Text = CurrentWoodStack();
        BuildStackPresetButtons();
    }

    // индикатор текущего состояния — стак дерева из конфига плагина
    private string CurrentWoodStack()
    {
        try
        {
            var cfg = StackCfgPath;
            if (cfg == null || !File.Exists(cfg)) return "—";
            var items = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfg))
                ?["Абсолютные размеры стака по предметам"]?.AsObject();
            return items?["wood"]?.GetValue<int>() is { } w ? $"wood = {w:N0}" : "—";
        }
        catch { return "—"; }
    }

    private void BuildStackPresetButtons()
    {
        PnlStackPresets.Children.Clear();
        var labels = new Dictionary<string, string>
        {
            ["vanilla"] = "ВАНИЛЛА — откат всего к заводским",
            ["balanced"] = "РАЗУМНЫЕ — ресурсы 10к, взрывчатка ваниль",
            ["boosted"] = "БУСТЕР — ресурсы 50к, взрывчатка ×2",
            ["arcade"] = "АРКАДА — ресурсы 100к, взрывчатка ×5",
        };
        var keys = ReadStackPresetKeys();
        if (keys.Count == 0) keys = labels.Keys.ToList();
        foreach (var key in keys)
        {
            var b = new Wpf.Ui.Controls.Button
            {
                Content = labels.TryGetValue(key, out var l) ? l : key,
                Tag = key,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            if (key != "vanilla") b.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            b.Click += StackPreset_Click;
            PnlStackPresets.Children.Add(b);
        }
    }

    // ключи пресетов — из конфига плагина (юзер может добавить свои через редактор)
    private List<string> ReadStackPresetKeys()
    {
        try
        {
            var cfg = StackCfgPath;
            if (cfg == null || !File.Exists(cfg)) return new List<string>();
            var presets = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfg))
                ?["Пресеты стаков"]?.AsObject();
            return presets?.Select(kv => kv.Key).ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    private async void StackPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string key) return;
        if (StackPluginName == null)
        { TxtStatus.Text = "OctaStack не установлен — положи плагин в carbon\\plugins"; return; }

        // живой путь: команда плагина — единый источник логики + мгновенное применение
        if (App.Rcon.IsConnected)
        {
            var r = await App.Rcon.SendAsync($"octastack.preset {key}");
            TxtStatus.Text = $"octastack.preset {key} → {Trim(r)}";
            TxtStackState.Text = CurrentWoodStack();
            _ = ServerTitleBuilder.AutoApplyAsync();
            return;
        }

        // оффлайн-путь: правим конфиг напрямую — ванилла из datafile + оверрайды пресета
        try
        {
            var cfg = StackCfgPath!;
            var vanillaPath = Path.Combine(App.Settings.ServerDirPath, "carbon", "data",
                StackPluginName + "_vanilla.json");
            if (!File.Exists(cfg) || !File.Exists(vanillaPath))
            { TxtStatus.Text = "RCON не подключён и нет конфига/datafile плагина — запусти сервер хотя бы раз"; return; }

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfg))!.AsObject();
            var preset = node["Пресеты стаков"]?[key]?.AsObject();
            if (preset == null) { TxtStatus.Text = $"пресет «{key}» не найден в конфиге плагина"; return; }

            var vanilla = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, int>>(File.ReadAllText(vanillaPath))
                ?? new Dictionary<string, int>();
            foreach (var kv in preset)
                vanilla[kv.Key] = kv.Value?.GetValue<int>() ?? 0;

            node["Абсолютные размеры стака по предметам"] =
                System.Text.Json.Nodes.JsonNode.Parse(
                    System.Text.Json.JsonSerializer.Serialize(vanilla));
            node["Множители по предметам (shortname или itemid)"] =
                new System.Text.Json.Nodes.JsonObject();
            node["Глобальный множитель стака"] = 1;
            if (node["Множители по категориям"] is System.Text.Json.Nodes.JsonObject cats)
                foreach (var c in cats.Select(x => x.Key).ToList()) cats[c] = 1;

            File.Copy(cfg, cfg + ".bak", overwrite: true);
            File.WriteAllText(cfg, node.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            TxtStatus.Text = $"✓ пресет «{key}» записан в {Path.GetFileName(cfg)} — применится при рестарте";
            TxtStackState.Text = CurrentWoodStack();
            _ = ServerTitleBuilder.AutoApplyAsync();
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
