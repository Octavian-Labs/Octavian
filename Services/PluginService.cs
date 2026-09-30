using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

public class PluginInfo
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; }
    public long SizeBytes { get; set; }

    // Статус сверки с codefling (кэшируется в data\codefling-versions.json)
    public string CfVersion { get; set; } = "";
    public string CfUrl { get; set; } = "";
    public string CfStatus { get; set; } = "";
    public bool CfUpdateAvailable => CfStatus.StartsWith("↑");

    public string SizeText => SizeBytes < 1024 ? $"{SizeBytes} B" : $"{SizeBytes / 1024.0:0.#} KB";
    public string MetaLine => string.Join(" · ",
        new[] { Author, string.IsNullOrEmpty(Version) ? "" : $"v{Version}", SizeText }
            .Where(s => !string.IsNullOrEmpty(s)));
}

// Модуль Carbon: карточка из carbon\modules\<Name>\config.json
public class CarbonModule
{
    public string Name { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public bool Enabled { get; set; }
}

// Сканер carbon/plugins: читает [Info]/[Description], вкл/выкл через переименование .cs<->.disabled
public class PluginService
{
    private readonly RsmSettings _settings;
    public PluginService(RsmSettings settings) => _settings = settings;

    // [Info("Название", "Автор", "1.0.0")] и [Description("текст")]
    private static readonly Regex InfoRe = new(@"\[Info\(\s*""([^""]*)""\s*,\s*""([^""]*)""\s*,\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex DescRe = new(@"\[Description\(\s*""([^""]*)""", RegexOptions.Compiled);

    public List<PluginInfo> Scan()
    {
        var list = new List<PluginInfo>();
        var dir = _settings.PluginsDir;
        if (!Directory.Exists(dir)) return list;

        foreach (var file in Directory.GetFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".cs" && ext != ".disabled" && ext != ".csx") continue;

            var p = new PluginInfo
            {
                FileName = Path.GetFileName(file),
                FullPath = file,
                Enabled = ext == ".cs",
                SizeBytes = new FileInfo(file).Length
            };

            try
            {
                var text = File.ReadAllText(file);
                var head = text.Length > 8192 ? text[..8192] : text;
                var m = InfoRe.Match(head);
                if (m.Success)
                {
                    p.Name = m.Groups[1].Value;
                    p.Author = m.Groups[2].Value;
                    p.Version = m.Groups[3].Value;
                }
                var d = DescRe.Match(head);
                if (d.Success) p.Description = d.Groups[1].Value;
            }
            catch { }
            if (string.IsNullOrEmpty(p.Name)) p.Name = Path.GetFileNameWithoutExtension(file);
            list.Add(p);
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    // Выключение = переименование .cs -> .cs.disabled (Carbon не подхватывает)
    public bool SetEnabled(PluginInfo p, bool enabled)
    {
        try
        {
            if (enabled && !p.Enabled)
            {
                var to = p.FullPath[..^".disabled".Length];
                File.Move(p.FullPath, to);
                p.FullPath = to; p.Enabled = true; p.FileName = Path.GetFileName(to);
            }
            else if (!enabled && p.Enabled)
            {
                var to = p.FullPath + ".disabled";
                File.Move(p.FullPath, to);
                p.FullPath = to; p.Enabled = false; p.FileName = Path.GetFileName(to);
            }
            return true;
        }
        catch { return false; }
    }

    // ── Carbon modules: carbon\modules\<Name>\config.json {"Enabled":bool} ──
    private static readonly Regex EnabledRe = new(@"""Enabled""\s*:\s*(true|false)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public List<CarbonModule> ScanModules()
    {
        var list = new List<CarbonModule>();
        var root = Path.Combine(_settings.ServerDirPath, "carbon", "modules");
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var cfg = Path.Combine(dir, "config.json");
            if (!File.Exists(cfg)) continue;
            var m = new CarbonModule { Name = Path.GetFileName(dir), ConfigPath = cfg };
            try
            {
                var mm = EnabledRe.Match(File.ReadAllText(cfg));
                if (mm.Success) m.Enabled = mm.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            list.Add(m);
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    // Правка поля Enabled в config.json модуля (горячее применение — через RCON c.loadmoduleconfig)
    public bool SetModuleEnabled(CarbonModule m, bool enabled)
    {
        try
        {
            var json = File.ReadAllText(m.ConfigPath);
            var mm = EnabledRe.Match(json);
            json = mm.Success
                ? json[..mm.Groups[1].Index] + (enabled ? "true" : "false") + json[(mm.Groups[1].Index + mm.Groups[1].Length)..]
                : json.TrimEnd('}', ' ', '\r', '\n') + (json.TrimEnd().EndsWith("{") ? "" : ",") + $"\"Enabled\":{(enabled ? "true" : "false")}}}";
            File.WriteAllText(m.ConfigPath, json);
            m.Enabled = enabled;
            return true;
        }
        catch { return false; }
    }

    // ── Кэш версий codefling (data\codefling-versions.json) ────────────────
    private static string CfCachePath => Path.Combine(App.DataDir, "codefling-versions.json");

    public static void SaveCfCache(IEnumerable<PluginInfo> plugins)
    {
        try
        {
            var map = plugins.Where(p => !string.IsNullOrEmpty(p.CfVersion))
                .ToDictionary(p => p.Name, p => new { v = p.CfVersion, u = p.CfUrl });
            File.WriteAllText(CfCachePath, System.Text.Json.JsonSerializer.Serialize(map));
        }
        catch { }
    }

    // Проставляет CfVersion/CfUrl/CfStatus из кэша после скана
    public void ApplyCfCache(List<PluginInfo> list)
    {
        try
        {
            if (!File.Exists(CfCachePath)) return;
            var map = System.Text.Json.JsonSerializer.Deserialize<
                Dictionary<string, System.Text.Json.JsonElement>>(File.ReadAllText(CfCachePath));
            if (map == null) return;
            foreach (var p in list)
            {
                if (!map.TryGetValue(p.Name, out var el)) continue;
                p.CfVersion = el.TryGetProperty("v", out var v) ? v.GetString() ?? "" : "";
                p.CfUrl = el.TryGetProperty("u", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(p.CfVersion)) { p.CfStatus = "—"; continue; }
                p.CfStatus = CodeflingService.IsNewer(p.CfVersion, p.Version)
                    ? $"↑ {p.CfVersion}" : $"✓ {p.CfVersion}";
            }
        }
        catch { }
    }

    // Удаление плагина: файл + связанный конфиг уходят в plugins\_trash (обратимо)
    public bool Uninstall(PluginInfo p)
    {
        try
        {
            var trash = Path.Combine(_settings.PluginsDir, "_trash");
            Directory.CreateDirectory(trash);
            File.Move(p.FullPath, Path.Combine(trash, p.FileName), true);
            var cfg = Path.Combine(_settings.CarbonConfigDir,
                Path.GetFileNameWithoutExtension(p.FileName.Replace(".disabled", "")) + ".json");
            if (File.Exists(cfg))
                File.Move(cfg, Path.Combine(trash, Path.GetFileName(cfg)), true);
            return true;
        }
        catch { return false; }
    }
}
