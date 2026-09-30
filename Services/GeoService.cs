using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OctavianRSM.Services;

// Геолокация по IP через ipwho.is (бесплатно, без ключа; только публичные адреса)
public class GeoService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly ConcurrentDictionary<string, (string code, string name)?> _cache = new();
    private int _inFlight;

    public async Task<(string code, string name)?> LookupAsync(string ip)
    {
        if (_cache.TryGetValue(ip, out var hit)) return hit;
        if (!IPAddress.TryParse(ip, out var addr) || IsPrivate(addr))
            return _cache[ip] = null;
        // не долбим API пачкой — максимум 8 параллельных запросов
        if (Interlocked.Increment(ref _inFlight) > 8)
        {
            Interlocked.Decrement(ref _inFlight);
            return null;
        }
        try
        {
            var json = await Http.GetStringAsync($"https://ipwho.is/{ip}?fields=success,country_code,country");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var s) && s.GetBoolean()
                && root.TryGetProperty("country_code", out var cc)
                && cc.GetString() is { Length: > 0 } code)
            {
                var name = root.TryGetProperty("country", out var cn) ? cn.GetString() ?? "" : "";
                return _cache[ip] = (code, name);
            }
            return _cache[ip] = null;
        }
        catch
        {
            return null; // сбои не кэшируем — повторим при следующем обновлении
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    private static bool IsPrivate(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal;
        var b = a.GetAddressBytes();
        return b[0] == 0 || b[0] == 10 ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254);
    }
}
