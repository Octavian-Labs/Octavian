using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace OctavianRSM.Services;

// Самообновление через GitHub Releases: фоновая проверка latest-релиза,
// скачивание EXE-ассета и подмена себя через внешний updater-скрипт.
// Репозиторий приватный — токен берётся из окружения (GH_TOKEN/GITHUB_TOKEN),
// в бинарник и конфиг не записывается.
public class UpdateService
{
    private const string Repo = "Octavian-Labs/Octavian";

    public UpdateInfo? Latest { get; private set; }
    public bool Checked { get; private set; }
    public bool UpdateAvailable => Latest != null;
    public event Action? OnChecked;
    private DateTime _lastCheck = DateTime.MinValue; // троттлинг повторных проверок

    public static Version CurrentVersion()
    {
        var info = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        info = (info ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0")
            .Split('+', '-')[0];
        return Version.TryParse(info, out var v) ? v : new Version(0, 0, 0);
    }

    private static HttpClient NewClient(bool octetStream = false)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OctavianRSM");
        var token = Environment.GetEnvironmentVariable("GH_TOKEN")
                 ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (octetStream)
            http.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        http.Timeout = octetStream ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(15); // скачиванию таймаут не нужен
        return http;
    }

    // Проверка с троттлингом: повторные вызовы (смена вкладки) чаще 30 сек игнорируются
    public async Task CheckAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(30)) return;
        _lastCheck = DateTime.UtcNow;
        Checked = false;
        try
        {
            using var http = NewClient();
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var rel = doc.RootElement;
            var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var ver = ParseTag(tag);
            if (ver == null || ver <= CurrentVersion()) { Checked = true; OnChecked?.Invoke(); return; }

            string? apiUrl = null; string name = ""; long size = 0;
            if (rel.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var n = a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    apiUrl = a.TryGetProperty("url", out var au) ? au.GetString() : null;
                    size = a.TryGetProperty("size", out var az) ? az.GetInt64() : 0;
                    name = n;
                    break;
                }
            if (apiUrl != null)
            {
                var notes = rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                Latest = new UpdateInfo(tag, ver, apiUrl, name, size, notes);
            }
        }
        catch { }
        Checked = true;
        // след в журнал менеджера и в data\update-check.log — видно, что проверка отработала
        try
        {
            var msg = Latest != null
                ? $"[RSM] Доступно обновление {Latest.Tag} (текущая {CurrentVersion()})"
                : $"[RSM] Обновлений нет (текущая {CurrentVersion()})";
            App.Db?.Log("sys", msg);
            File.AppendAllText(Path.Combine(App.DataDir, "update-check.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\n");
        }
        catch { }
        OnChecked?.Invoke();
    }

    private static Version? ParseTag(string tag)
    {
        tag = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(tag, out var v) ? v : null;
    }

    // Скачивает ассет релиза в <exe>.new — для приватного репо нужен API-url с Accept: octet-stream
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress)
    {
        var exe = Environment.ProcessPath!;
        var tmp = exe + ".new";
        using var http = NewClient(octetStream: true);
        using var resp = await http.GetAsync(info.ApiUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? info.Size;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[256 * 1024];
        long got = 0; int read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read));
            got += read;
            if (total > 0) progress.Report((double)got / total);
        }
        return tmp;
    }

    // Внешний updater: ждёт выхода процесса, подменяет EXE, перезапускает
    public void ApplyAndRestart(string newFile)
    {
        var exe = Environment.ProcessPath!;
        var pid = Environment.ProcessId;
        var script = Path.Combine(App.DataDir, "updater.ps1");
        Directory.CreateDirectory(App.DataDir);
        File.WriteAllText(script, UpdaterScript, new System.Text.UTF8Encoding(false));
        var psi = new ProcessStartInfo("powershell",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -OldPid {pid} -NewFile \"{newFile}\" -Target \"{exe}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        Process.Start(psi);
        Application.Current.Shutdown();
    }

    // Уборка следов прошлого обновления (updater.ps1, *.new)
    public static void CleanupLeftovers()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".new")) File.Delete(exe + ".new");
            var upd = Path.Combine(App.DataDir, "updater.ps1");
            if (File.Exists(upd)) File.Delete(upd);
        }
        catch { }
    }

    private const string UpdaterScript = @"
param([int]$OldPid, [string]$NewFile, [string]$Target)
Wait-Process -Id $OldPid -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
for ($i = 0; $i -lt 30; $i++) {
    try { Copy-Item $NewFile $Target -Force; break } catch { Start-Sleep 1 }
}
Start-Process $Target -WorkingDirectory (Split-Path $Target)
Remove-Item $NewFile -ErrorAction SilentlyContinue
Remove-Item $PSCommandPath -ErrorAction SilentlyContinue
";
}

public record UpdateInfo(string Tag, Version Version, string ApiUrl, string AssetName, long Size, string Notes);
