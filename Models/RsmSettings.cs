using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OctavianRSM.Models;

// Конфиг менеджера: пути, порты, доступ к RCON — лежит в корне Octavian
public class RsmSettings
{
    public string ServerDir { get; set; } = "server";
    public string SteamCmdExe { get; set; } = @"steamcmd\steamcmd.exe";
    public string ServerExe { get; set; } = "RustDedicated.exe";
    public bool SetupCompleted { get; set; }
    public string DeploymentMode { get; set; } = "Local";
    public string RconHost { get; set; } = "127.0.0.1";
    public bool InstallCarbon { get; set; } = true;
    public string ServerProfile { get; set; } = "balanced2x";
    public double GatherRate { get; set; } = 2;
    public double StackMultiplier { get; set; } = 2;
    public int TeamSize { get; set; } = 4;
    public string WipeSchedule { get; set; } = "weekly";
    public string RegionTag { get; set; } = "EU";
    public bool ServerPve { get; set; }
    public double CraftTimescale { get; set; } = 1;
    public string ServerUrl { get; set; } = "";
    public string HeaderImage { get; set; } = "";
    public string LogoImage { get; set; } = "";
    public string ExtraTags { get; set; } = ""; // свободные теги через запятую (после wipe/mode)

    public string Hostname { get; set; } = "OCTAVIAN RUST";
    public string Description { get; set; } = "Octavian Rust Server — dev";
    public string ServerIp { get; set; } = "0.0.0.0";
    public int GamePort { get; set; } = 28115;
    public int RconPort { get; set; } = 28116;
    public string RconPassword { get; set; } = "Octavian_2026_ChangeMe";
    public bool RconWebSocket { get; set; } = true;
    public int QueryPort { get; set; } = 28117;
    public int AppPort { get; set; } = 28182;
    public string Identity { get; set; } = "octavian";
    public int WorldSize { get; set; } = 3500;
    public uint WorldSeed { get; set; } = 239552852;
    public int MaxPlayers { get; set; } = 50;
    public int SaveInterval { get; set; } = 300;
    public string Level { get; set; } = "Procedural Map";

    // Автовайп
    public bool AutoWipeEnabled { get; set; } = false;
    public int AutoWipeIntervalDays { get; set; } = 7;
    public bool AutoWipeFull { get; set; } = false;
    public DateTime? LastWipeDate { get; set; }

    // Watchdog: автоподнятие сервера после падения/выхода
    public bool WatchdogEnabled { get; set; } = true;
    public int WatchdogDelaySec { get; set; } = 10;

    // Авторестарт по расписанию: "04:00,16:00" — шлёт restart <warn>, watchdog поднимает
    public bool AutoRestartEnabled { get; set; } = false;
    public string AutoRestartTimes { get; set; } = "04:00";
    public int AutoRestartWarnSec { get; set; } = 300;

    public bool ScheduledAnnouncementEnabled { get; set; }
    public string ScheduledAnnouncementText { get; set; } = "";
    public int ScheduledAnnouncementIntervalMinutes { get; set; } = 30;
    public List<string> AnnouncementTemplates { get; set; } = new()
    {
        "[OCTAVIAN] Сервер сохраняет мир. Возможна короткая задержка.",
        "[OCTAVIAN] Плановый рестарт через 5 минут.",
        "[OCTAVIAN] Сообщайте об ошибках администрации сервера.",
        "[OCTAVIAN] Уважайте других игроков и соблюдайте правила."
    };
    public Dictionary<string, string> LoadModeSnapshot { get; set; } = new();
    public Dictionary<string, string> WorldModeSnapshot { get; set; } = new();

    // Доп. параметры запуска
    public string Gamemode { get; set; } = "vanilla";
    public string LevelUrl { get; set; } = ""; // кастомная карта: server.levelurl

    // Библиотека пресетов лута (BetterLoot/MagicLoot JSON + описание)
    public List<LootPreset> LootPresets { get; set; } = new();

    [JsonIgnore] public string RootDir { get; private set; } = AppContext.BaseDirectory;
    [JsonIgnore] public bool IsRemoteMode => DeploymentMode.Equals("Remote", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string ServerDirPath => ResolvePath(ServerDir);
    [JsonIgnore] public string SteamCmdExePath => ResolvePath(SteamCmdExe);
    [JsonIgnore] public string ServerExePath => Path.Combine(ServerDirPath, ServerExe);
    [JsonIgnore] public string PluginsDir => Path.Combine(ServerDirPath, "carbon", "plugins");
    [JsonIgnore] public string CarbonConfigDir => Path.Combine(ServerDirPath, "carbon", "configs");

    public void AttachRoot(string root) => RootDir = Path.GetFullPath(root);

    public string ResolvePath(string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(RootDir, path));

    public string MakePortablePath(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(RootDir, full);
        return relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : relative;
    }

    // Аргументы запуска RustDedicated.exe — как в start.bat
    public string BuildServerArgs()
    {
        var args = new List<string?>
        {
            "-batchmode", "-nographics", "-silent-crashes",
            "-logfile", $"\"{Path.Combine(ServerDirPath, "server.log")}\"",
            $"+server.ip \"{ServerIp}\"",
            $"+server.port {GamePort}",
            $"+rcon.ip \"{ServerIp}\"",
            $"+rcon.port {RconPort}",
            $"+rcon.password \"{RconPassword}\"",
            $"+rcon.web {(RconWebSocket ? 1 : 0)}",
            $"+server.queryport {QueryPort}",
            $"+app.port {AppPort}",
            $"+server.identity \"{Identity}\"",
            string.IsNullOrWhiteSpace(LevelUrl)
                ? $"+server.level \"{Level}\""
                : $"+server.levelurl \"{LevelUrl}\"",
            string.IsNullOrWhiteSpace(Gamemode) || Gamemode == "vanilla"
                ? null
                : $"+server.gamemode \"{Gamemode}\"",
            $"+server.seed {WorldSeed}",
            $"+server.worldsize {WorldSize}",
            $"+server.maxplayers {MaxPlayers}",
            $"+server.hostname \"{Hostname}\"",
            $"+server.description \"{Description}\"",
            $"+server.saveinterval {SaveInterval}"
        };
        return string.Join(" ", args.Where(a => a != null));
    }

    public static RsmSettings Load(string path)
    {
        RsmSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<RsmSettings>(File.ReadAllText(path)) ?? new RsmSettings()
                : new RsmSettings();
        }
        catch { settings = new RsmSettings(); }
        settings.AttachRoot(Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory);
        return settings;
    }

    public void Save(string path)
    {
        AttachRoot(Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory);
        Directory.CreateDirectory(RootDir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
    }
}

// Именованный пресет наполнения лута — код/JSON + человеческое описание
public class LootPreset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
    public string Target { get; set; } = "BetterLoot"; // имя плагина, чей конфиг принимает пресет
}
