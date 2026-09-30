using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

// Структурный editor server.cfg: хранит все строки файла, меняет значения по ключам,
// неизвестные/кастомные convar'ы сохраняет как есть (как uServerConfig.pas из RSM-FMX)
public class ServerConfigService
{
    private readonly RsmSettings _settings;
    public ServerConfigService(RsmSettings settings) => _settings = settings;

    // Ключ → значение (без кавычек)
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Строки файла, чтобы при сохранении сохранить порядок и неизвестные ключи
    private readonly List<string> _keyOrder = new();
    private readonly List<string> _otherLines = new(); // комментарии/пустые строки

    public string CfgPath
        => Path.Combine(_settings.ServerDirPath, "server", _settings.Identity, "cfg", "server.cfg");

    public void Load()
    {
        Values.Clear(); _keyOrder.Clear(); _otherLines.Clear();
        if (!File.Exists(CfgPath)) return;
        foreach (var raw in File.ReadAllLines(CfgPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#"))
            {
                _otherLines.Add(raw);
                continue;
            }
            if (!TryParseLine(line, out var key, out var val))
            {
                _otherLines.Add(raw);
                continue;
            }
            Values[key] = val;
            _keyOrder.Add(key);
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(CfgPath)!;
        Directory.CreateDirectory(dir);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();

        // сначала — в исходном порядке ключей файла
        foreach (var key in _keyOrder)
        {
            if (written.Contains(key)) continue;
            lines.Add($"{key} \"{Values.GetValueOrDefault(key, "")}\"");
            written.Add(key);
        }
        // затем — новые ключи, которых не было в файле
        foreach (var kv in Values)
        {
            if (written.Contains(kv.Key)) continue;
            lines.Add($"{kv.Key} \"{kv.Value}\"");
            written.Add(kv.Key);
        }
        lines.AddRange(_otherLines);
        File.WriteAllLines(CfgPath, lines);
    }

    // Строка вида `ключ [арг...] "значение"` или `ключ значение`.
    // Ключ МОЖЕТ содержать пробелы (gather.rate dispenser) — поэтому значение
    // определяем по ПОСЛЕДНЕЙ паре кавычек, а не по первому пробелу.
    public static bool TryParseLine(string line, out string key, out string value)
    {
        key = ""; value = "";
        if (line.Length == 0) return false;
        var qEnd = line.Length - 1;
        var qStart = qEnd > 0 && line[qEnd] == '"'
            ? line.LastIndexOf('"', qEnd - 1) : -1;
        if (qStart > 0)
        {
            key = line[..qStart].Trim().Replace("\"", "");
            value = line[(qStart + 1)..qEnd];
        }
        else
        {
            var sp = line.IndexOf(' ');
            if (sp <= 0) return false;
            key = line[..sp].Trim().Replace("\"", "");
            value = line[(sp + 1)..].Trim().Trim('"');
        }
        return key.Length > 0;
    }

    public string this[string key]
    {
        get => Values.GetValueOrDefault(key, "");
        set => Values[key] = value;
    }
}
