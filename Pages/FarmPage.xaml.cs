using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OctavianRSM.Pages;

// Ферма: скот (Livestock Update Oct-2026), плотность животных, рост растений.
// Все значения — честные Rust-convars: пишем в server.cfg + шлём по RCON.
public partial class FarmPage : Page
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // имя слайдера → convar (bool'ы отдельно — они CheckBox'ы)
    private static readonly Dictionary<string, string> FarmKeys = new()
    {
        ["SldBreedCd"]  = "livestock.breedcooldown",
        ["SldCalving"]  = "livestock.calvingcooldownscale",
        ["SldGrazeCd"]  = "livestock.overgrazedareacooldownminutes",
        ["SldNeglect"]  = "livestock.conditionfallseconds",
    };

    private static readonly Dictionary<string, string> FarmBools = new()
    {
        ["ChkBreeding"]    = "livestock.breedingenabled",
        ["ChkMounting"]    = "livestock.allowmounting",
        ["ChkOvergrazing"] = "livestock.overgrazingenabled",
    };

    private static readonly Dictionary<string, string> PlantKeys = new()
    {
        ["SldPlantScale"] = "server.planttickscale",
        ["SldPlantTick"]  = "server.planttick",
        ["SldWildDeath"]  = "server.nonplanterdeathchancepertick", // слайдер в %, convar в доле
        ["SldSaturation"] = "server.optimalplanterqualitysaturation",
    };

    private static readonly Dictionary<string, string> PlantBools = new()
    {
        ["ChkLightDetect"]  = "server.plantlightdetection",
        ["ChkMinCondition"] = "server.useminimumplantcondition",
    };

    // (подпись, convar, группа: 0 скот / 1 мирные / 2 опасные, дефолт на км²)
    private static readonly (string Label, string Convar, int Group, double Def)[] Animals =
    {
        ("Коровы",                "cow.population",          0, 2),
        ("Овцы",                  "sheep.population",        0, 2),
        ("Кабаны",                "boar.population",         1, 5),
        ("Курицы",                "chicken.population",      1, 3),
        ("Олени",                 "stag.population",         1, 3),
        ("Лошади",                "ridablehorse.population", 1, 2),
        ("Кролики",               "rabbit.population",       1, 2),
        ("Медведи",               "bear.population",         2, 2),
        ("Белые медведи",         "polarbear.population",    2, 1),
        ("Волки",                 "wolf2.population",        2, 2),
        ("Крокодилы",             "crocodile.population",    2, 1),
        ("Тигры",                 "tiger.population",        2, 0.5),
        ("Пантеры",               "panther.population",      2, 0.5),
        ("Змеи",                  "snakehazard.population",  2, 5),
        ("Зомби (ивент)",         "zombie.population",       2, 0),
    };

    private readonly Dictionary<string, Slider> _popSliders = new();

    public FarmPage()
    {
        InitializeComponent();
        BuildAnimalRows();
        LoadFromCfg();
        _ = RefreshFromRconAsync();
    }

    // ── Построение строк плотности ───────────────────────────

    private void BuildAnimalRows()
    {
        var stacks = new[] { StackLivestock, StackPeaceful, StackDangerous };
        foreach (var (label, convar, group, _) in Animals)
        {
            var val = new TextBlock
            {
                Text = "—",
                Foreground = (Brush)FindResource("B.Accent"),
                FontWeight = FontWeights.Bold,
            };
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
            head.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("OctaCmdSub") });
            DockPanel.SetDock(val, Dock.Right);
            head.Children.Add(val);

            var slider = new Slider
            {
                Minimum = 0, Maximum = 20, TickFrequency = 0.5, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = $"{convar} — особей на км²",
                Tag = val,
            };
            slider.ValueChanged += (s, _) =>
            {
                if (s is Slider sl && sl.Tag is TextBlock t)
                    t.Text = sl.Value.ToString("0.#", Inv);
            };

            _popSliders[convar] = slider;
            stacks[group].Children.Add(head);
            stacks[group].Children.Add(slider);
        }
    }

    // ── Загрузка текущих значений ────────────────────────────

    private void LoadFromCfg()
    {
        App.ServerCfg.Load();
        var cfg = App.ServerCfg;

        // скот: agescale обратно — слайдер показывает «скорость», convar — множитель времени
        if (TryNum(cfg["livestock.agescale"], out var age) && age > 0)
            SldGrowth.Value = Math.Clamp(1 / age, SldGrowth.Minimum, SldGrowth.Maximum);
        ChkNoAging.IsChecked = age == 0;

        foreach (var (ctl, key) in FarmKeys)
            if (TryNum(cfg[key], out var v) && FindName(ctl) is Slider s)
                s.Value = Math.Clamp(v, s.Minimum, s.Maximum);
        foreach (var (ctl, key) in FarmBools)
            if (FindName(ctl) is CheckBox c && bool.TryParse(cfg[key], out var b))
                c.IsChecked = b;

        foreach (var (_, convar, _, def) in Animals)
            _popSliders[convar].Value = TryNum(cfg[convar], out var v)
                ? Math.Clamp(v, 0, 20) : def;

        foreach (var (ctl, key) in PlantKeys)
            if (TryNum(cfg[key], out var v) && FindName(ctl) is Slider s)
                s.Value = Math.Clamp(key.EndsWith("deathchancepertick") ? v * 100 : v,
                    s.Minimum, s.Maximum);
        foreach (var (ctl, key) in PlantBools)
            if (FindName(ctl) is CheckBox c && bool.TryParse(cfg[key], out var b))
                c.IsChecked = b;
    }

    // RCON: опросить живые значения convars (ответ вида `cow.population "2"` или число)
    private async Task RefreshFromRconAsync()
    {
        if (!App.Rcon.IsConnected) return;
        var keys = new List<string> { "livestock.agescale" };
        keys.AddRange(FarmKeys.Values);
        keys.AddRange(FarmBools.Values);
        foreach (var a in Animals) keys.Add(a.Convar);
        keys.AddRange(PlantKeys.Values);
        keys.AddRange(PlantBools.Values);

        var live = new Dictionary<string, string>();
        foreach (var k in keys)
        {
            try
            {
                var r = await App.Rcon.SendAsync(k);
                if (!string.IsNullOrWhiteSpace(r)) live[k] = r.Trim();
            }
            catch { }
        }
        if (live.Count == 0) return;

        Dispatcher.Invoke(() =>
        {
            if (TryNum(live.GetValueOrDefault("livestock.agescale"), out var age))
            {
                if (age > 0) SldGrowth.Value = Math.Clamp(1 / age, SldGrowth.Minimum, SldGrowth.Maximum);
                ChkNoAging.IsChecked = age == 0;
            }
            foreach (var (ctl, key) in FarmKeys)
                if (TryNum(live.GetValueOrDefault(key), out var v) && FindName(ctl) is Slider s)
                    s.Value = Math.Clamp(v, s.Minimum, s.Maximum);
            foreach (var (ctl, key) in FarmBools)
                if (FindName(ctl) is CheckBox c && TryBool(live.GetValueOrDefault(key), out var b))
                    c.IsChecked = b;
            foreach (var a in Animals)
                if (TryNum(live.GetValueOrDefault(a.Convar), out var v))
                    _popSliders[a.Convar].Value = Math.Clamp(v, 0, 20);
            foreach (var (ctl, key) in PlantKeys)
                if (TryNum(live.GetValueOrDefault(key), out var v) && FindName(ctl) is Slider s)
                    s.Value = Math.Clamp(key.EndsWith("deathchancepertick") ? v * 100 : v,
                        s.Minimum, s.Maximum);
            foreach (var (ctl, key) in PlantBools)
                if (FindName(ctl) is CheckBox c && TryBool(live.GetValueOrDefault(key), out var b))
                    c.IsChecked = b;
            TxtStatus.Text = "✓ значения прочитаны с сервера";
        });
    }

    // ── Подписи у слайдеров ──────────────────────────────────

    private void Farm_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider s || s.Tag is not string lbl || FindName(lbl) is not TextBlock t)
            return;
        t.Text = s.Name switch
        {
            "SldBreedCd" or "SldNeglect" or "SldPlantTick" => $"{s.Value:0} с",
            "SldGrazeCd"  => $"{s.Value:0} мин",
            "SldWildDeath" => $"{s.Value:0.#}%",
            "SldSaturation" => s.Value.ToString("0.00", Inv),
            _ => $"×{s.Value:0.#}",
        };
    }

    // ── Пресеты ──────────────────────────────────────────────

    private void FarmPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        switch (fe.Tag as string)
        {
            case "vanilla":
                SldGrowth.Value = 1; SldBreedCd.Value = 600; SldCalving.Value = 1;
                ChkNoAging.IsChecked = false; ChkBreeding.IsChecked = true;
                ChkMounting.IsChecked = true; ChkOvergrazing.IsChecked = true;
                SldGrazeCd.Value = 30; SldNeglect.Value = 3600;
                break;
            case "fast":
                SldGrowth.Value = 2; SldBreedCd.Value = 300; SldCalving.Value = 0.5;
                ChkNoAging.IsChecked = false;
                break;
            case "farm5":
                SldGrowth.Value = 5; SldBreedCd.Value = 120; SldCalving.Value = 0.5;
                SldNeglect.Value = 7200; ChkNoAging.IsChecked = false;
                break;
            case "forever":
                ChkNoAging.IsChecked = true; ChkBreeding.IsChecked = false;
                break;
        }
    }

    private void PopPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var tag = fe.Tag as string;
        foreach (var (_, convar, group, def) in Animals)
        {
            var s = _popSliders[convar];
            s.Value = tag switch
            {
                "vanilla" => def,
                "sparse"  => Math.Round(def * 0.5 * 2) / 2,
                "dense"   => Math.Min(20, def * 2),
                "farm"    => group == 0 ? Math.Min(20, def * 3)
                           : group == 1 ? Math.Round(def * 1.5 * 2) / 2
                           : Math.Round(def * 0.25 * 2) / 2,
                "empty"   => 0,
                _ => s.Value,
            };
        }
    }

    private void PlantPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        switch (fe.Tag as string)
        {
            case "vanilla":
                SldPlantScale.Value = 1; SldPlantTick.Value = 60;
                SldWildDeath.Value = 0.5; SldSaturation.Value = 0.6;
                ChkLightDetect.IsChecked = true; ChkMinCondition.IsChecked = true;
                break;
            case "fast":
                SldPlantScale.Value = 3;
                break;
            case "warm":
                SldPlantScale.Value = 5; SldWildDeath.Value = 0.1; SldSaturation.Value = 0.8;
                break;
        }
    }

    // ── Применение ───────────────────────────────────────────

    private async void ApplyFarm_Click(object sender, RoutedEventArgs e)
    {
        var pairs = new List<(string Key, string Val)>
        {
            ("livestock.agescale", ChkNoAging.IsChecked == true
                ? "0" : (1 / SldGrowth.Value).ToString("0.###", Inv)),
        };
        foreach (var (ctl, key) in FarmKeys)
            pairs.Add((key, Fmt(((Slider)FindName(ctl)!).Value)));
        foreach (var (ctl, key) in FarmBools)
            pairs.Add((key, ((CheckBox)FindName(ctl)!).IsChecked == true ? "True" : "False"));
        await ApplyAsync(pairs, "скот");
    }

    private async void ApplyPop_Click(object sender, RoutedEventArgs e)
    {
        var pairs = new List<(string Key, string Val)>();
        foreach (var (_, convar, _, _) in Animals)
            pairs.Add((convar, Fmt(_popSliders[convar].Value)));
        await ApplyAsync(pairs, "плотность");
    }

    private async void ApplyPlants_Click(object sender, RoutedEventArgs e)
    {
        var pairs = new List<(string Key, string Val)>();
        foreach (var (ctl, key) in PlantKeys)
        {
            var v = ((Slider)FindName(ctl)!).Value;
            if (key.EndsWith("deathchancepertick")) v /= 100;
            pairs.Add((key, v.ToString("0.####", Inv)));
        }
        foreach (var (ctl, key) in PlantBools)
            pairs.Add((key, ((CheckBox)FindName(ctl)!).IsChecked == true ? "True" : "False"));
        await ApplyAsync(pairs, "растения");
    }

    // server.cfg — чтобы пережило рестарт; RCON — чтобы применилось живьём
    private async Task ApplyAsync(List<(string Key, string Val)> pairs, string what)
    {
        try
        {
            foreach (var (k, v) in pairs)
                App.ServerCfg[k] = v;
            App.ServerCfg.Save();

            if (App.Rcon.IsConnected)
            {
                var ok = 0;
                foreach (var (k, v) in pairs)
                {
                    try { await App.Rcon.SendAsync($"{k} {v}"); ok++; }
                    catch { }
                }
                TxtStatus.Text = $"✓ {what}: {ok}/{pairs.Count} convars отправлены, записано в server.cfg";
            }
            else
                TxtStatus.Text = $"✓ {what}: {pairs.Count} convars в server.cfg — применятся при рестарте";
        }
        catch (Exception ex) { TxtStatus.Text = $"ошибка: {ex.Message}"; }
    }

    private static string Fmt(double v) => v.ToString("0.###", Inv);

    private static bool TryNum(string? raw, out double v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        // ответ вида `cow.population "2"` — берём последний токен, чистим кавычки
        var t = raw.Trim().Trim('"');
        var sp = t.LastIndexOf(' ');
        if (sp >= 0) t = t[(sp + 1)..].Trim().Trim('"');
        return double.TryParse(t, NumberStyles.Float, Inv, out v);
    }

    private static bool TryBool(string? raw, out bool b)
    {
        b = false;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var t = raw.Trim().Trim('"');
        var sp = t.LastIndexOf(' ');
        if (sp >= 0) t = t[(sp + 1)..].Trim().Trim('"');
        if (bool.TryParse(t, out b)) return true;
        if (t == "1") { b = true; return true; }
        if (t == "0") return true;
        return false;
    }
}
