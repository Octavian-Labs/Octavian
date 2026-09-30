using System;
using System.Globalization;
using System.Threading.Tasks;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

// Watchdog + планировщик автоподнятия и авторестарта (как в RSM-FMX):
// watchdog: если сервер умер не по команде — поднимает через WatchdogDelaySec
// авторестарт: в заданные "HH:mm" шлёт `restart <warn>` — Rust сам гаснет → watchdog поднимает
public class WatchdogService : IDisposable
{
    private readonly RsmSettings _settings;
    private readonly ServerManager _server;
    private readonly RconService _rcon;
    private bool _intentionalStop;
    private bool _watching;
    private string _lastFiredMinute = "";
    private DateTime _lastAnnouncementAt = DateTime.MinValue;

    public event Action<string>? OnLog;

    public WatchdogService(RsmSettings settings, ServerManager server, RconService rcon)
    {
        _settings = settings;
        _server = server;
        _rcon = rcon;
        _server.OnStateChanged += Server_OnStateChanged;
        _server.OnUserStop += () => _intentionalStop = true;
        _server.OnUserStart += () => { _intentionalStop = false; };
    }

    public void Start() { _watching = true; _ = Loop(); }
    public void Stop() => _watching = false;
    public void ResetAnnouncementTimer() => _lastAnnouncementAt = DateTime.Now;

    private void Server_OnStateChanged(bool running)
    {
        if (_settings.IsRemoteMode || running || _intentionalStop || !_settings.WatchdogEnabled) return;
        // сервер умер сам — автоподнятие через задержку
        int delay = Math.Max(3, _settings.WatchdogDelaySec);
        OnLog?.Invoke($"[WATCHDOG] сервер неожиданно остановлен — перезапуск через {delay} сек");
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay * 1000);
            if (_intentionalStop || _server.IsRunning) return;
            OnLog?.Invoke("[WATCHDOG] запуск сервера…");
            _server.Start();
        });
    }

    private async Task Loop()
    {
        while (_watching)
        {
            await Task.Delay(20_000);
            try { await Tick(); } catch { }
        }
    }

    private async Task Tick()
    {
        // 1) RCON auto-reconnect (как таймер wsClient в RSM-FMX)
        var endpointAvailable = _settings.IsRemoteMode || _server.IsRunning;
        if (endpointAvailable && !_rcon.IsConnected)
            await _rcon.ConnectAsync(_settings.RconHost, _settings.RconPort, _settings.RconPassword, 3000);

        if (!endpointAvailable || !_rcon.IsConnected) return;

        var now = DateTime.Now;
        if (_settings.ScheduledAnnouncementEnabled && !string.IsNullOrWhiteSpace(_settings.ScheduledAnnouncementText))
        {
            if (_lastAnnouncementAt == DateTime.MinValue) _lastAnnouncementAt = now;
            var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.ScheduledAnnouncementIntervalMinutes));
            if (now - _lastAnnouncementAt >= interval)
            {
                _lastAnnouncementAt = now;
                var text = _settings.ScheduledAnnouncementText.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
                OnLog?.Invoke($"[ANNOUNCEMENT] {text}");
                await _rcon.FireAsync($"say \"{text}\"");
            }
        }

        if (_settings.IsRemoteMode || !_settings.AutoRestartEnabled) return;
        var hhmm = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (hhmm == _lastFiredMinute) return;
        foreach (var t in _settings.AutoRestartTimes.Split(new[] { ',', ';', ' ' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (t == hhmm)
            {
                _lastFiredMinute = hhmm;
                var warn = Math.Max(30, _settings.AutoRestartWarnSec);
                OnLog?.Invoke($"[AUTORESTART] restart {warn} (расписание {hhmm})");
                await _rcon.FireAsync($"restart {warn}");
                break;
            }
        }
    }

    public void Dispose() { _watching = false; _server.OnStateChanged -= Server_OnStateChanged; }
}
