using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

// Локальная память менеджера: журнал консоли/сервера и база игроков (SQLite, один файл)
public class DbService : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _sync = new();
    private int _sincePrune;

    public DbService(string dir)
    {
        Directory.CreateDirectory(dir);
        _db = new SqliteConnection($"Data Source={Path.Combine(dir, "octavian-rsm.db")}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS log(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL, type TEXT NOT NULL, msg TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_log_id ON log(id);
            CREATE TABLE IF NOT EXISTS players(
                steamid TEXT PRIMARY KEY,
                name TEXT, ip TEXT,
                first_seen TEXT, last_seen TEXT);
            """;
        cmd.ExecuteNonQuery();
        // миграции: геолокация и учёт наигранного времени по сессиям
        AddColumnIfMissing("players", "country_code", "TEXT");
        AddColumnIfMissing("players", "country_name", "TEXT");
        AddColumnIfMissing("players", "total_seconds", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("players", "last_connected", "INTEGER NOT NULL DEFAULT 0");
    }

    private void AddColumnIfMissing(string table, string col, string ddl)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{col}'";
        if ((long)(cmd.ExecuteScalar() ?? 0L) != 0) return;
        using var alter = _db.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {col} {ddl}";
        alter.ExecuteNonQuery();
    }

    // ── Журнал ───────────────────────────────────────────────

    public void Log(string type, string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return;
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO log(ts,type,msg) VALUES($ts,$t,$m)";
            cmd.Parameters.AddWithValue("$ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$m", msg);
            cmd.ExecuteNonQuery();
            // редкий прунинг — держим последние 30k строк
            if (++_sincePrune >= 500)
            {
                _sincePrune = 0;
                using var prune = _db.CreateCommand();
                prune.CommandText = "DELETE FROM log WHERE id < (SELECT MAX(id)-30000 FROM log)";
                prune.ExecuteNonQuery();
            }
        }
    }

    public List<(string ts, string type, string msg)> RecentLogs(int limit)
    {
        lock (_sync)
        {
            var list = new List<(string, string, string)>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT ts,type,msg FROM log ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2)));
            list.Reverse();
            return list;
        }
    }

    // ── База игроков (оффлайн-ростер) ────────────────────────

    // Текущая сессия игрока хранится в last_connected; при перерыве > 2 мин
    // между опросами считаем прошлую сессию завершённой и переносим в total_seconds
    public void UpsertPlayer(string steamid, string name, string ip, int connectedSeconds)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO players(steamid,name,ip,first_seen,last_seen,last_connected)
                VALUES($id,$n,$ip,$now,$now,$conn)
                ON CONFLICT(steamid) DO UPDATE SET
                    name=$n, ip=$ip, last_seen=$now,
                    total_seconds = total_seconds + CASE
                        WHEN last_connected>0 AND (julianday($now)-julianday(last_seen))*86400 > 120
                        THEN last_connected ELSE 0 END,
                    last_connected = CASE
                        WHEN (julianday($now)-julianday(last_seen))*86400 > 120 THEN $conn
                        ELSE MAX(last_connected,$conn) END
                """;
            cmd.Parameters.AddWithValue("$id", steamid);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$ip", ip);
            cmd.Parameters.AddWithValue("$conn", connectedSeconds);
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
    }

    // Игроки, давно не попадавшие в playerlist: их последняя сессия закрылась —
    // досчитываем last_connected в общий счётчик
    public void FlushOfflineSessions()
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE players SET total_seconds=total_seconds+last_connected, last_connected=0
                WHERE last_connected>0 AND last_seen < $cutoff
                """;
            cmd.Parameters.AddWithValue("$cutoff", DateTime.Now.AddMinutes(-2).ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
    }

    public void SetCountry(string steamid, string code, string name)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE players SET country_code=$c, country_name=$cn
                WHERE steamid=$id AND (country_code IS NULL OR country_code='')
                """;
            cmd.Parameters.AddWithValue("$id", steamid);
            cmd.Parameters.AddWithValue("$c", code);
            cmd.Parameters.AddWithValue("$cn", name);
            cmd.ExecuteNonQuery();
        }
    }

    public List<KnownPlayer> GetPlayers()
    {
        lock (_sync)
        {
            var list = new List<KnownPlayer>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT steamid,name,ip,first_seen,last_seen,
                       country_code,country_name,total_seconds,last_connected
                FROM players ORDER BY last_seen DESC
                """;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new KnownPlayer
                {
                    SteamId = rd.GetString(0), Name = rd.GetString(1), Ip = rd.GetString(2),
                    FirstSeen = rd.GetString(3), LastSeen = rd.GetString(4),
                    CountryCode = rd.IsDBNull(5) ? "" : rd.GetString(5),
                    CountryName = rd.IsDBNull(6) ? "" : rd.GetString(6),
                    TotalSeconds = rd.GetInt64(7) + rd.GetInt64(8)
                });
            return list;
        }
    }

    public void Dispose() { lock (_sync) _db.Dispose(); }
}

public class KnownPlayer
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public string FirstSeen { get; set; } = "";
    public string LastSeen { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string CountryName { get; set; } = "";
    public long TotalSeconds { get; set; }
    // эмодзи-флагов в Windows нет — показываем код страны, название в tooltip
    public string CountryText => string.IsNullOrEmpty(CountryCode) ? "—" : CountryCode;
    public string CountryTip => string.IsNullOrEmpty(CountryName) ? "" : CountryName;
    public string TotalText => Fmt(TotalSeconds);
    public static string Fmt(long s) => s >= 86400 ? $"{s / 86400}д {s % 86400 / 3600}ч"
        : s >= 3600 ? $"{s / 3600}ч {s % 3600 / 60}м" : $"{s / 60}м";
}
