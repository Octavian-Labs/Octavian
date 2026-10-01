using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

// Прогресс фоновой установки: фаза, строка лога, процент (null — индетерминированный)
public sealed record BootProgress(string Phase, string Detail, double? Percent);

// Автономная установка сервера рядом с EXE менеджера:
// steamcmd.zip → RustDedicated (app 258550) → Carbon production → конфиги и плагины.
// Всё идемпотентно: повторный запуск докачивает/дописывает, SetupCompleted ставится только
// после успешного прохода цепочки — частично установленный сервер не считается готовым.
public class BootstrapService
{
    public const string SteamCmdZipUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    public const string CarbonReleaseApi = "https://api.github.com/repos/CarbonCommunity/Carbon/releases/tags/production_build";
    public const int RustAppId = 258550;

    private readonly RsmSettings _s;
    public BootstrapService(RsmSettings settings) => _s = settings;

    // ── SteamCMD ────────────────────────────────────────────────────────────
    public async Task<bool> EnsureSteamCmdAsync(IProgress<BootProgress> progress, CancellationToken ct)
    {
        var exe = _s.SteamCmdExePath;
        var dir = Path.GetDirectoryName(exe)!;
        if (!File.Exists(exe))
        {
            progress.Report(new("SteamCMD", "Загрузка steamcmd.zip…", null));
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(Path.GetTempPath(), "steamcmd_rsm.zip");
            await DownloadAsync(SteamCmdZipUrl, zip, progress, "SteamCMD", ct);
            progress.Report(new("SteamCMD", "Распаковка steamcmd.exe…", null));
            ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
        }
        // первый запуск steamcmd сам себя распаковывает (~200 MB) — прогоняем +quit
        if (!File.Exists(Path.Combine(dir, "steam.dll")) && !File.Exists(Path.Combine(dir, "linux32", "steamcmd")))
        {
            progress.Report(new("SteamCMD", "Первый запуск — self-update…", null));
            var code = await RunSteamCmdAsync("+quit", progress, ct);
            if (code != 0) { progress.Report(new("SteamCMD", $"self-update завершился с кодом {code}", null)); }
        }
        return File.Exists(exe);
    }

    // ── Rust Dedicated ──────────────────────────────────────────────────────
    public async Task<bool> InstallRustAsync(IProgress<BootProgress> progress, CancellationToken ct)
    {
        var args = $"+force_install_dir \"{_s.ServerDirPath}\" +login anonymous +app_update {RustAppId} +quit";
        progress.Report(new("Rust Dedicated", "app_update 258550 — загрузка ~7 GB может занять время", null));
        var code = await RunSteamCmdAsync(args, progress, ct);
        var ok = code == 0 && File.Exists(_s.ServerExePath);
        progress.Report(new("Rust Dedicated", ok ? "Сервер установлен" : $"Ошибка установки, код {code}", ok ? 100 : null));
        return ok;
    }

    private async Task<int> RunSteamCmdAsync(string args, IProgress<BootProgress> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _s.SteamCmdExePath,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(_s.SteamCmdExePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        // метка активности вывода: steamcmd без консоли может молча ждать ввод —
        // тогда цепочка висела бы вечно; тишина > 3 мин = прерываем с ошибкой
        var lastTick = new[] { DateTime.UtcNow.Ticks };
        void Activity() => Interlocked.Exchange(ref lastTick[0], DateTime.UtcNow.Ticks);
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Activity();
            // steamcmd пишет прогресс вида "Update state (0x61) downloading, progress: 42.10 (x / y)"
            var pct = ParseSteamProgress(e.Data);
            progress.Report(new("SteamCMD", e.Data, pct));
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Activity();
            progress.Report(new("SteamCMD", e.Data, null));
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
        var exited = p.WaitForExitAsync(ct);
        while (!exited.IsCompleted)
        {
            await Task.WhenAny(exited, Task.Delay(5000));
            if (exited.IsCompleted) break;
            var silentSec = (DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastTick[0]))).TotalSeconds;
            if (silentSec > 180)
            {
                progress.Report(new("SteamCMD",
                    "Нет вывода более 3 минут — процесс завис или ждёт ввод. Прервано, нажмите ПОВТОРИТЬ.", null));
                try { p.Kill(); } catch { }
            }
        }
        await exited;
        return p.ExitCode;
    }

    private static double? ParseSteamProgress(string line)
    {
        var i = line.IndexOf("progress:", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var span = line[(i + 9)..].Trim();
        var end = span.IndexOfAny(new[] { ' ', '(' });
        if (end > 0) span = span[..end];
        return double.TryParse(span, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // ── Carbon ──────────────────────────────────────────────────────────────
    public sealed record CarbonAsset(string Tag, string Name, string Url, long Size);

    public static async Task<CarbonAsset?> ResolveCarbonAssetAsync(HttpClient http, CancellationToken ct)
    {
        var json = await http.GetStringAsync(CarbonReleaseApi, ct);
        var root = JsonDocument.Parse(json).RootElement;
        var tag = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            // production Windows-сборка — именно Carbon.Windows.Release.zip
            if (name.Equals("Carbon.Windows.Release.zip", StringComparison.OrdinalIgnoreCase)
                || (name.Contains("windows", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("debug", StringComparison.OrdinalIgnoreCase)))
            {
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                var size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                return new CarbonAsset(tag, name, url, size);
            }
        }
        return null;
    }

    public async Task<bool> InstallCarbonAsync(IProgress<BootProgress> progress, CancellationToken ct)
    {
        using var http = NewHttp();
        var asset = await ResolveCarbonAssetAsync(http, ct);
        if (asset == null) { progress.Report(new("Carbon", "Не найден Windows-релиз в ответе GitHub", null)); return false; }
        progress.Report(new("Carbon", $"Загрузка {asset.Name} ({asset.Size / 1048576} MB)…", 0));
        var zip = Path.Combine(Path.GetTempPath(), asset.Name);
        await DownloadAsync(asset.Url, zip, progress, "Carbon", ct);
        progress.Report(new("Carbon", "Распаковка поверх папки сервера…", null));
        // конфиги/данные/плагины пользователя архивом не перекрываются — в нём только бинарники Carbon
        ZipFile.ExtractToDirectory(zip, _s.ServerDirPath, overwriteFiles: true);
        try { await File.WriteAllTextAsync(Path.Combine(_s.ServerDirPath, "carbon", "version"), asset.Tag, ct); } catch { }
        progress.Report(new("Carbon", $"Установлен {asset.Tag}", 100));
        return true;
    }

    // ── Конфигурация ────────────────────────────────────────────────────────
    // server.cfg + конфиги плагинов рейтов. Существующий cfg читаем через ServerConfigService —
    // неизвестные ключи и комментарии выживают.
    public Task WriteConfigurationAsync(CancellationToken ct)
    {
        var cfg = new ServerConfigService(_s);
        cfg.Load();
        cfg["server.hostname"] = _s.Hostname;
        cfg["server.description"] = _s.Description;
        cfg["server.url"] = _s.ServerUrl;
        cfg["server.headerimage"] = _s.HeaderImage;
        cfg["server.logoimage"] = _s.LogoImage;
        if (!string.IsNullOrWhiteSpace(_s.ServerDomain))
            cfg["server.favoritesEndpoint"] = _s.ServerDomain.Trim();
        cfg["server.tags"] = ServerProfiles.BuildTags(_s);
        cfg["server.maxplayers"] = _s.MaxPlayers.ToString();
        cfg["server.worldsize"] = _s.WorldSize.ToString();
        cfg["server.seed"] = _s.WorldSeed.ToString();
        cfg["server.saveinterval"] = _s.SaveInterval.ToString();
        cfg["server.pve"] = _s.ServerPve ? "1" : "0";
        if (_s.CraftTimescale is > 0 and not 1.0)
            cfg["craft.timescale"] = _s.CraftTimescale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        cfg.Save();

        if (_s.InstallCarbon)
        {
            WriteJsonIfMissingOrManaged(Path.Combine(_s.CarbonConfigDir, "GatherManager.json"), GatherConfig(_s.GatherRate));
            WriteJsonIfMissingOrManaged(Path.Combine(_s.CarbonConfigDir, "StackSizeController.json"), StackConfig(_s.StackMultiplier));
        }
        return Task.CompletedTask;
    }

    // Плагин сам дописывает недостающие ключи при загрузке — генерируем минимум.
    // Если файл уже существует и не наш (без метки octavian-managed) — не трогаем.
    private static void WriteJsonIfMissingOrManaged(string path, string content)
    {
        if (File.Exists(path))
        {
            var head = File.ReadAllText(path);
            if (!head.Contains("\"_managed\": \"octavian-rsm\"", StringComparison.Ordinal)) return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string GatherConfig(double rate) => $$"""
    {
      "_managed": "octavian-rsm",
      "Options": {
        "GatherResourceModifiers": { "*": {{rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}} },
        "PickupResourceModifiers": { "*": {{rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}} },
        "QuarryResourceModifiers": { "*": {{rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}} },
        "SurveyResourceModifiers": { "*": {{rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}} },
        "ExcavatorResourceModifiers": { "*": {{rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }
      }
    }
    """;

    private static string StackConfig(double mult) => $$"""
    {
      "_managed": "octavian-rsm",
      "RevertStackSizesToVanillaOnUnload": true,
      "GlobalStackMultiplier": {{mult.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
    }
    """;

    // ── Базовые плагины (встроены в EXE как ресурсы) ─────────────────────────
    public static readonly string[] BaselinePlugins = { "OctaAdminTools" };

    public int ExtractBaselinePlugins()
    {
        var asm = Assembly.GetExecutingAssembly();
        int count = 0;
        Directory.CreateDirectory(_s.PluginsDir);
        foreach (var name in BaselinePlugins)
        {
            var resName = $"OctavianRSM.plugins.{name}.cs";
            var target = Path.Combine(_s.PluginsDir, name + ".cs");
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) continue;
            // не затираем существующий (возможно отредактированный) файл
            if (File.Exists(target)) continue;
            using var fs = File.Create(target);
            stream.CopyTo(fs);
            count++;
        }
        return count;
    }

    // ── Утилиты ─────────────────────────────────────────────────────────────
    public static HttpClient NewHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OctavianRSM");
        http.Timeout = TimeSpan.FromMinutes(30);
        return http;
    }

    private static async Task DownloadAsync(string url, string dest, IProgress<BootProgress> progress,
        string phase, CancellationToken ct)
    {
        using var http = NewHttp();
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        var tmp = dest + ".part";
        await using (var input = await resp.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(tmp))
        {
            var buf = new byte[64 * 1024];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buf, ct)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                var pct = total is > 0 ? done * 100.0 / total.Value : (double?)null;
                progress.Report(new(phase, $"{done / 1048576} MB{(total is > 0 ? $" / {total.Value / 1048576} MB" : "")}", pct));
            }
        }
        ct.ThrowIfCancellationRequested();
        File.Move(tmp, dest, overwrite: true);
    }
}
