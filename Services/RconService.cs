using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OctavianRSM.Services;

// RCON-клиент Rust (WebSocket): ws://host:port/<password>
// Протокол: {"Identifier":N,"Message":"cmd","Type":"WebRcon"} -> ответы {"Message","Identifier","Type"}
public class RconService : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _nextId = 1;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    // Пуши сервера (консоль, чат) — Identifier -1/0
    public event Action<string, string>? OnMessage; // (type, message)
    public event Action? OnDisconnected;

    public async Task<bool> ConnectAsync(string host, int port, string password, int timeoutMs = 5000)
    {
        try
        {
            await DisconnectAsync();
            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            var uri = new Uri($"ws://{host}:{port}/{Uri.EscapeDataString(password)}");
            using var cto = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cto.CancelAfter(timeoutMs);
            await _ws.ConnectAsync(uri, cto.Token);
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> SendAsync(string command, int timeoutMs = 8000)
    {
        if (!IsConnected) return "[RCON offline]";
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new
        {
            Identifier = id,
            Message = command,
            Name = "WebRcon",
            Type = 3
        });
        try
        {
            await _ws!.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, _cts!.Token);
            using var cto = new CancellationTokenSource(timeoutMs);
            // по таймауту сразу вычищаем запрос из _pending — иначе он висит там вечно
            cto.Token.Register(() =>
            {
                if (_pending.TryRemove(id, out var t)) t.TrySetResult("[timeout]");
            });
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            return "[error] " + ex.Message;
        }
    }

    // Отправка без ожидания ответа (для broadcast-команд)
    public async Task FireAsync(string command)
    {
        if (!IsConnected) return;
        int id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.Serialize(new { Identifier = id, Message = command, Name = "WebRcon", Type = 3 });
        try { await _ws!.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, _cts!.Token); }
        catch { }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (_ws != null && _ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do
                {
                    res = await _ws.ReceiveAsync(buf, ct);
                    if (res.MessageType == WebSocketMessageType.Close) { await DisconnectAsync(); return; }
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.ToArray());
                Dispatch(text);
            }
        }
        catch { }
        OnDisconnected?.Invoke();
    }

    private void Dispatch(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            int id = root.TryGetProperty("Identifier", out var pId) ? pId.GetInt32() : -1;
            string msg = root.TryGetProperty("Message", out var pM) ? pM.GetString() ?? "" : "";
            string type = root.TryGetProperty("Type", out var pT) ? pT.GetString() ?? "Generic" : "Generic";

            if (id > 0 && _pending.TryRemove(id, out var tcs))
                tcs.TrySetResult(msg);
            else
                OnMessage?.Invoke(type, msg);
        }
        catch { OnMessage?.Invoke("Raw", raw); }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_ws != null && _ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        finally { _ws?.Dispose(); _ws = null; }
    }

    public void Dispose() { _ = DisconnectAsync(); }
}
