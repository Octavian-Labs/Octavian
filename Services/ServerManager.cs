using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

// Управление процессом RustDedicated.exe: запуск, остановка, статус, перехват вывода
public class ServerManager
{
    private Process? _process;
    private readonly RsmSettings _settings;

    public event Action<string>? OnOutput;
    public event Action<bool>? OnStateChanged; // isRunning
    public event Action? OnUserStart;  // старт по кнопке/автовайпу — сброс "умышленной остановки"
    public event Action? OnUserStop;   // остановка по кнопке — watchdog не должен поднимать

    // Отметить, что сервер гаснет по желанию пользователя (например RCON `quit`)
    public void NoteIntentionalStop() => OnUserStop?.Invoke();

    // Простой канал лога в UI
    public void Echo(string line) => OnOutput?.Invoke(line);

    public ServerManager(RsmSettings settings) => _settings = settings;

    public bool IsRunning => Alive(_process) || FindRunning() != null;
    public bool OwnedByUs => Alive(_process);
    public bool ConsoleOutputSeen { get; private set; } // stdout процесса реально пишет — tail лога не нужен

    public ServerProcessSnapshot? GetProcessSnapshot()
    {
        var owned = Alive(_process);
        var process = owned ? _process : FindRunning();
        if (process == null) return null;
        try
        {
            process.Refresh();
            return new ServerProcessSnapshot(process.Id, process.WorkingSet64, process.TotalProcessorTime);
        }
        catch { return null; }
        finally { if (!owned) process.Dispose(); }
    }

    // HasExited бросает, если процесс ещё не был запущен — страхуемся
    private static bool Alive(Process? p)
    {
        try { return p is { HasExited: false }; }
        catch { return false; }
    }

    private Process? FindRunning()
    {
        try
        {
            return Process.GetProcessesByName("RustDedicated")
                .FirstOrDefault(p => p.MainModule?.FileName?.StartsWith(_settings.ServerDirPath, StringComparison.OrdinalIgnoreCase) == true);
        }
        catch { return null; }
    }

    public bool Start()
    {
        if (IsRunning) return false;
        if (_settings.IsRemoteMode)
        {
            OnOutput?.Invoke("[RSM] Remote-режим: локальный процесс сервера недоступен");
            return false;
        }
        var exe = _settings.ServerExePath;
        if (!File.Exists(exe)) { OnOutput?.Invoke($"[RSM] Не найден {exe}"); return false; }

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = _settings.BuildServerArgs(),
                WorkingDirectory = _settings.ServerDirPath,
                UseShellExecute = false,
                // консоль dedicated-сервера перенаправляем в журнал RSM — отдельного окна нет
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true, // stdin — можно слать quit без RCON
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _process.Exited += (_, _) => { OnStateChanged?.Invoke(false); OnOutput?.Invoke("[RSM] Сервер остановлен"); };
        _process.OutputDataReceived += (_, ev) =>
        {
            if (ev.Data != null) { ConsoleOutputSeen = true; OnOutput?.Invoke(ev.Data); }
        };
        _process.ErrorDataReceived += (_, ev) =>
        {
            if (ev.Data != null) { ConsoleOutputSeen = true; OnOutput?.Invoke("[STDERR] " + ev.Data); }
        };
        ConsoleOutputSeen = false;
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        OnUserStart?.Invoke();
        OnOutput?.Invoke($"[RSM] Сервер запущен, PID {_process.Id}");
        OnStateChanged?.Invoke(true);
        return true;
    }

    public async Task<bool> StopAsync(bool force = false)
    {
        var p = _process ?? FindRunning();
        if (p == null || p.HasExited) { OnStateChanged?.Invoke(false); return true; }
        OnUserStop?.Invoke();
        try
        {
            // наш процесс — сначала штатный quit в stdin консоли (сервер сохранится и выйдет сам)
            if (!force && OwnedByUs && _process != null)
            {
                try { _process.StandardInput.WriteLine("quit"); _process.StandardInput.Flush(); } catch { }
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (Alive(_process)) { OnOutput?.Invoke("[RSM] quit не отработал за 15с — убиваю"); _process.Kill(); }
            }
            else
            {
                try { p.Kill(); } catch { }
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        catch { try { p.Kill(); } catch { } }
        _process = null;
        OnStateChanged?.Invoke(false);
        OnOutput?.Invoke("[RSM] Сервер остановлен");
        return true;
    }

    // Чтение server.log в реальном времени — Rust пишет туда весь консольный вывод
    public async Task TailLogAsync(string logPath, Action<string> onLine, CancellationTokenLike ct)
    {
        long pos = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        while (!ct.Cancelled)
        {
            try
            {
                if (File.Exists(logPath))
                {
                    var len = new FileInfo(logPath).Length;
                    if (len < pos) pos = 0; // лог пересоздан
                    if (len > pos)
                    {
                        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fs.Seek(pos, SeekOrigin.Begin);
                        using var sr = new StreamReader(fs);
                        string? line;
                        while ((line = await sr.ReadLineAsync()) != null) onLine(line);
                        pos = fs.Position;
                    }
                }
            }
            catch { }
            await Task.Delay(500);
        }
    }
}

public readonly record struct ServerProcessSnapshot(int Id, long WorkingSet, TimeSpan CpuTime);

// Простая обёртка вместо CancellationToken, чтобы не тянуть threading в интерфейс события
public class CancellationTokenLike
{
    public bool Cancelled;
    public void Cancel() => Cancelled = true;
}
