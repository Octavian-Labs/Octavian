using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

// Вайп: перенос файлов сохранения в _wipe_backup/<timestamp> (обратимо)
// Map wipe = *.sav / *.sav.* ; Full wipe = + player.*.db (блюпринты, состояния)
public class WipeService
{
    private readonly RsmSettings _settings;
    public WipeService(RsmSettings settings) => _settings = settings;

    public string SaveDir => Path.Combine(_settings.ServerDirPath, "server", _settings.Identity);

    public List<FileInfo> MapFiles()
    {
        var dir = new DirectoryInfo(SaveDir);
        if (!dir.Exists) return new List<FileInfo>();
        return dir.GetFiles()
            .Where(f => f.Name.EndsWith(".sav", StringComparison.OrdinalIgnoreCase)
                        || f.Name.Contains(".sav.", StringComparison.OrdinalIgnoreCase)
                        || f.Name.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public List<FileInfo> PlayerFiles()
    {
        var dir = new DirectoryInfo(SaveDir);
        if (!dir.Exists) return new List<FileInfo>();
        return dir.GetFiles()
            .Where(f => f.Name.StartsWith("player.", StringComparison.OrdinalIgnoreCase)
                        && f.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // Дата последнего вайпа = время создания самого старого .sav (новая карта -> новый файл)
    public DateTime? LastWipe()
    {
        if (_settings.LastWipeDate.HasValue) return _settings.LastWipeDate;
        var savs = MapFiles();
        return savs.Count == 0 ? null : savs.Min(f => f.CreationTime);
    }

    public DateTime? NextWipe()
        => LastWipe()?.AddDays(Math.Max(1, _settings.AutoWipeIntervalDays));

    public bool IsDue()
        => _settings.AutoWipeEnabled && NextWipe() is { } next && DateTime.Now >= next;

    // Переносит файлы в бэкап — ничего не удаляет
    public (int moved, string backupDir) Execute(bool full)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
        var backup = Path.Combine(_settings.ServerDirPath, "_wipe_backup", stamp + (full ? "_full" : "_map"));
        Directory.CreateDirectory(backup);

        var files = MapFiles();
        if (full) files.AddRange(PlayerFiles());

        int moved = 0;
        foreach (var f in files)
        {
            try { f.MoveTo(Path.Combine(backup, f.Name)); moved++; } catch { }
        }
        _settings.LastWipeDate = DateTime.Now;
        _settings.Save(App.SettingsPath);
        return (moved, backup);
    }
}
