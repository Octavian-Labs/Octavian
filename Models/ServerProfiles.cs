using System;
using System.Collections.Generic;
using System.Linq;

namespace OctavianRSM.Models;

// Согласованный пресет роли сервера: рейты, режим, теги и рекомендованный мир.
// Собран по официальным тегам server.browser (Facepunch wiki) и практике
// популярных форматов: 1x/2x/5x/10x, аркадные 1000x и x1000000 (battlefield),
// training, builds, roleplay, PvE и Hardcore. Gather/stack — это Carbon-плагины,
// поэтому пресет пишет и в server.cfg, и в конфиги плагинов (см. BootstrapService).
public sealed record ServerProfile(
    string Key,
    string Title,
    string Subtitle,
    double Gather,
    double Stack,
    double CraftTimescale,
    bool Pve,
    string Gamemode,
    string WipeTag,   // weekly|biweekly|monthly — группа взаимоисключающая
    string ModeTag,   // vanilla|pve|hardcore|"" — тоже одна из группы
    int MaxPlayers,
    int WorldSize,
    bool NeedsCarbon);

public static class ServerProfiles
{
    public static readonly IReadOnlyList<ServerProfile> All = new List<ServerProfile>
    {
        new("vanilla", "VANILLA 1x", "Классический Rust без плагинов. Официальные рейты и стаки.",
            1, 1, 1, false, "vanilla", "monthly", "vanilla", 100, 4000, false),
        new("balanced2x", "МОДНЫЙ 2x", "Самый популярный формат: сбор x2, стаки x2, недельный вайп.",
            2, 2, 1, false, "vanilla", "weekly", "", 100, 4000, true),
        new("fast5x", "БЫСТРЫЙ 5x", "Быстрый прогресс: сбор x5, стаки x5, крафт x2. Для коротких вайпов.",
            5, 5, 2, false, "vanilla", "weekly", "", 75, 3500, true),
        new("heavy10x", "ТЯЖЁЛЫЙ 10x", "Аркадный гринд: сбор x10, стаки x10, крафт x5.",
            10, 10, 5, false, "vanilla", "weekly", "", 50, 3000, true),
        // Аркадные форматы: высокие рейты, маленький мир, частые файты
        new("arcade1000x", "АРКАДА 1000x", "Короткий вайп без гринда: сбор x1000, стаки x1000, крафт x10.",
            1000, 1000, 10, false, "vanilla", "weekly", "", 100, 2500, true),
        new("million", "БЕЗУМИЕ x1000000", "Battlefield-формат: миллионный сбор, стаки x100000, инста-крафт — чистый файт.",
            1_000_000, 100_000, 100, false, "vanilla", "weekly", "battlefield", 100, 1500, true),
        new("training", "ТРЕНИРОВКА / AIM", "Песочница для аима и перестрелок: ускоренный лут, малый мир, тег training.",
            10, 10, 10, false, "vanilla", "weekly", "training", 60, 1500, true),
        // Спокойные/ролевые форматы
        new("pve", "PvE / СТРОЙКА", "Без урона между игроками (server.pve). Спокойный режим, месячный вайп.",
            2, 2, 1, true, "vanilla", "monthly", "pve", 50, 4000, true),
        new("creative", "КРЕАТИВ / BUILDS", "Стройка без ограничений: PvE, сбор x100, стаки x1000, инста-крафт, тег builds.",
            100, 1000, 100, true, "vanilla", "monthly", "builds", 50, 3000, true),
        new("roleplay", "ROLEPLAY", "Отыгрыш роли: ванильные рейты, месячный вайп, тег roleplay.",
            1, 1, 1, false, "vanilla", "monthly", "roleplay", 100, 4250, true),
        new("hardcore", "HARDCORE", "Режим hardcore Facepunch: минимум удобств, ванильные рейты.",
            1, 1, 1, false, "hardcore", "monthly", "hardcore", 100, 4000, false),
        new("custom", "СВОЙ ВАРИАНТ", "Не применять пресет — все значения останутся как введены ниже.",
            -1, -1, -1, false, "", "", "", 0, 0, true),
    };

    public static ServerProfile Get(string key)
        => All.FirstOrDefault(p => p.Key == key) ?? All.First(p => p.Key == "balanced2x");

    // Применить пресет к настройкам. "custom" не трогает числовые поля.
    public static void Apply(ServerProfile p, RsmSettings s)
    {
        s.ServerProfile = p.Key;
        if (p.Key == "custom") return;
        s.GatherRate = p.Gather;
        s.StackMultiplier = p.Stack;
        s.CraftTimescale = p.CraftTimescale;
        s.ServerPve = p.Pve;
        s.Gamemode = p.Gamemode;
        s.WipeSchedule = p.WipeTag;
        if (p.MaxPlayers > 0) s.MaxPlayers = p.MaxPlayers;
        if (p.WorldSize > 0) s.WorldSize = p.WorldSize;
        s.InstallCarbon = p.NeedsCarbon;
    }

    // Итоговая строка server.tags: wipe-группа + mode-группа + свободные теги.
    // Обе группы взаимоисключающие — берём по одному значению из каждой.
    public static string BuildTags(RsmSettings s)
    {
        var tags = new List<string>();
        var p = Get(s.ServerProfile);
        if (!string.IsNullOrWhiteSpace(s.WipeSchedule)) tags.Add(s.WipeSchedule.Trim().ToLowerInvariant());
        else if (!string.IsNullOrWhiteSpace(p.WipeTag)) tags.Add(p.WipeTag);
        var mode = !string.IsNullOrWhiteSpace(p.ModeTag) ? p.ModeTag
                 : s.Gamemode is "hardcore" or "softcore" ? s.Gamemode : "";
        if (mode.Length > 0 && mode != "vanilla") tags.Add(mode);
        else if (s.ServerProfile == "vanilla") tags.Add("vanilla");
        if (s.ServerPve && !tags.Contains("pve")) tags.Add("pve");
        if (!string.IsNullOrWhiteSpace(s.ExtraTags))
            tags.AddRange(s.ExtraTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.Join(',', tags.Where(t => t.Length > 0).Distinct());
    }
}
