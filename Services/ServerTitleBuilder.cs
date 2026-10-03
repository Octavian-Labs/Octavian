using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

// Конструктор server.hostname по паттерну топовых серверов:
//   [Регион] Имя | Рейты | Пати | Фича | Фича | Вайп
// Динамические части (рейты/пати/вайп) пересчитываются из живых источников —
// при смене рейтов или лимита пати название пересобирается само (TitleAuto).
public static class ServerTitleBuilder
{
    // ── Сбор частей ───────────────────────────────────────────────────────

    // «X5» или «X5/X10»: gather/stack берём из живого GatherManager.json,
    // фолбэк — настройки профиля (SetupWizard)
    public static string RatesText(RsmSettings s)
    {
        var g = s.GatherRate;
        double? stack = null; // стеки показываем только если реально стоит StackSizeController
        try
        {
            // GatherManager.json может остаться от старого профиля без самого плагина —
            // читаем множитель только при установленном .cs, иначе честный дефолт из настроек
            var path = Path.Combine(s.CarbonConfigDir, "GatherManager.json");
            if (File.Exists(path) && File.Exists(Path.Combine(s.PluginsDir, "GatherManager.cs")))
            {
                var o = JsonNode.Parse(File.ReadAllText(path))?["Options"]?.AsObject();
                if (o?["GatherResourceModifiers"]?["*"] is JsonNode gn)
                    g = gn.GetValue<double>();
            }
            // стек показываем только если плагин StackSizeController реально стоит —
            // осиротевший json от старого профиля не должен рисовать x1000
            var spath = Path.Combine(s.CarbonConfigDir, "StackSizeController.json");
            if (File.Exists(spath) && File.Exists(Path.Combine(s.PluginsDir, "StackSizeController.cs")))
            {
                var so = JsonNode.Parse(File.ReadAllText(spath))?.AsObject();
                if (so?["GlobalStackMultiplier"] is JsonNode sn) stack = sn.GetValue<double>();
            }
        }
        catch { }
        var st = stack ?? g; // без плагина стеков второй X не показываем
        var gi = Math.Round(g); var si = Math.Round(st);
        bool gInt = Math.Abs(g - gi) < 0.01, sInt = Math.Abs(st - si) < 0.01;
        string gs = gInt ? $"X{gi:0}" : $"X{g:0.#}";
        string ss = sInt ? $"X{si:0}" : $"X{st:0.#}";
        return Math.Abs(g - st) < 0.01 ? gs : $"{gs}/{ss}";
    }

    // Пати-лимит: convar relationshipmanager.maxteamsize из server.cfg → фолбэк настройки
    public static int TeamLimit(RsmSettings s, ServerConfigService cfg)
    {
        if (int.TryParse(cfg["relationshipmanager.maxteamsize"], out var n) && n >= 0)
            return n;
        return s.TeamSize;
    }

    // maxteamsize: 0 = тимы ОТКЛЮЧЕНЫ (не «без лимита»!), >16 считаем nolimit
    public static string TeamText(int team)
        => team == 0 ? "NO TEAMS"
         : team == 1 ? "SOLO"
         : team == 2 ? "DUO"
         : team == 3 ? "TRIO"
         : team == 4 ? "QUAD"
         : team > 16 ? "NOLIMIT"
         : $"MAX{team}";

    // Выбор пользователя: auto → TeamLimit, иначе явный режим
    public static string TeamText(string mode, RsmSettings s, ServerConfigService cfg)
        => mode switch
        {
            "off" => "",
            "nolimit" => "NOLIMIT",
            "solo" => "SOLO",
            "duo" => "DUO",
            "trio" => "TRIO",
            "quad" => "QUAD",
            var m when m.StartsWith("max") && int.TryParse(m[3..], out var n) => $"MAX{n}",
            _ => TeamText(TeamLimit(s, cfg)),
        };

    // Вайп-суффикс: auto → LastWipeDate («WIPED 03.10», сегодняшний — «JUST WIPED»)
    public static string WipeText(RsmSettings s)
        => s.TitleWipeMode switch
        {
            "off" => "",
            "custom" => s.TitleWipeCustom.Trim(),
            _ => s.LastWipeDate is { } d
                ? (d.Date == DateTime.Today ? "JUST WIPED"
                 : d.Date == DateTime.Today.AddDays(-1) ? "WIPED ВЧЕРА"
                 : $"WIPED {d:dd.MM}")
                : "",
        };

    // Полная строка hostname
    public static string Build(RsmSettings s, ServerConfigService cfg)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.TitleRegion))
            parts.Add($"[{s.TitleRegion.Trim()}]");
        var head = s.TitleBase.Trim();
        if (parts.Count > 0 && head.Length > 0)
            parts[^1] += " " + head;                 // «[EU] OCTAVIAN RUST» — одна часть
        else if (head.Length > 0)
            parts.Add(head);
        if (s.TitleShowRates)
        {
            var r = s.TitleRatesCustom.Trim();
            parts.Add(r.Length > 0 ? r : RatesText(s)); // свой текст → как есть (маркетинг)
        }
        var team = TeamText(s.TitleTeamMode, s, cfg);
        if (team.Length > 0) parts.Add(team);
        foreach (var f in s.TitleFeatures)
            if (!string.IsNullOrWhiteSpace(f)) parts.Add(f.Trim());
        var wipe = WipeText(s);
        if (wipe.Length > 0) parts.Add(wipe);
        return string.Join(" | ", parts);
    }

    // ── Применение ────────────────────────────────────────────────────────

    // Собрать → записать server.hostname в cfg + настройки + живьём по RCON.
    // Возвращает собранное название (для статуса в UI).
    public static async Task<string> ApplyAsync(RsmSettings s, ServerConfigService cfg, bool rcon)
    {
        var title = Build(s, cfg);
        cfg.Load();
        cfg["server.hostname"] = title;
        cfg.Save();
        s.Hostname = title;
        s.Save(App.SettingsPath);
        if (rcon && App.Rcon.IsConnected)
            await App.Rcon.SendAsync($"server.hostname \"{title}\"");
        return title;
    }

    // Авто-пересбор после смены рейтов/лимита пати — вызывают RatesPage и др.
    public static async Task AutoApplyAsync()
    {
        var s = App.Settings;
        if (!s.TitleAuto || string.IsNullOrWhiteSpace(s.TitleBase)) return;
        try { await ApplyAsync(s, App.ServerCfg, rcon: true); }
        catch { }
    }

    // ── Пресеты по паттернам популярных серверов ──────────────────────────
    // Каждый пресет заполняет поля конструктора — пользователь допиливает дальше.

    public record TitlePreset(string Name, string Sample, Action<RsmSettings> Fill);

    public static readonly TitlePreset[] Presets =
    {
        new("Ванилла Solo/Duo", "[EU] OCTAVIAN | Solo/Duo | WIPED", s =>
        {
            s.TitleRegion = "EU"; s.TitleShowRates = false;
            s.TitleTeamMode = "auto"; s.TitleFeatures = new List<string>();
            s.TitleWipeMode = "auto";
        }),
        new("Модded X5", "[EU] OCTAVIAN | X5/X10 | NOLIMIT | LOOT+ | KITS | 03.10", s =>
        {
            s.TitleRegion = "EU"; s.TitleShowRates = true;
            s.TitleTeamMode = "nolimit";
            s.TitleFeatures = new List<string> { "LOOT+", "KITS", "TP", "HOME" };
            s.TitleWipeMode = "auto";
        }),
        new("Solo/Duo LARGE", "OCTAVIAN 5x Solo/Duo LARGE | Loot x5 | TP | Home | Kits | WIPED", s =>
        {
            s.TitleRegion = ""; s.TitleShowRates = true;
            s.TitleTeamMode = "auto";
            s.TitleFeatures = new List<string> { "TP", "HOME", "KITS", "SHOP" };
            s.TitleWipeMode = "auto";
        }),
        new("Баттлфилд", "OCTAVIAN x1000 | BATTLEFIELD | KITS | TP | CLANS | PVP", s =>
        {
            s.TitleRegion = ""; s.TitleShowRates = true;
            s.TitleTeamMode = "off";
            s.TitleFeatures = new List<string> { "BATTLEFIELD", "KITS", "TP", "CLANS", "SKINBOX", "PVP" };
            s.TitleWipeMode = "off";
        }),
        new("Just Wipe", "[EU] OCTAVIAN 5X | TP | KITS | JUST WIPED", s =>
        {
            s.TitleRegion = "EU"; s.TitleShowRates = true;
            s.TitleTeamMode = "off";
            s.TitleFeatures = new List<string> { "TP", "KITS" };
            s.TitleWipeMode = "custom"; s.TitleWipeCustom = "JUST WIPED";
        }),
    };
}
