using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace OctavianRSM.Services;

/// <summary>
/// Адрес подключения игроков (client.connect / steam://connect).
/// Общий владелец логики — раньше жила в DashboardPage, теперь нужна и «Лицу сервера».
/// </summary>
public static class ServerConnect
{
    /// <summary>Адрес + подпись для UI. IsDomain = подключение по DNS SRV без порта.</summary>
    public sealed record Info(string Address, string Label, bool IsDomain);

    /// <summary>
    /// Приоритет адреса: remote → RconHost; заданный домен → он (SRV сам резолвит порт);
    /// заданный ServerIp → он; иначе внешний IP через ipwho.is, фолбэк — LAN.
    /// </summary>
    public static async Task<Info> DetectAsync()
    {
        var s = App.Settings;
        if (s.IsRemoteMode)
            return new($"{s.RconHost}:{s.GamePort}", "УДАЛЬНЫЙ СЕРВЕР — подключение в игре (F1)", false);
        if (!string.IsNullOrWhiteSpace(s.ServerDomain))
            return new(s.ServerDomain.Trim(), "ДОМЕН СЕРВЕРА (DNS SRV) — подключение в игре (F1)", true);
        if (!string.IsNullOrWhiteSpace(s.ServerIp) && s.ServerIp != "0.0.0.0")
            return new($"{s.ServerIp}:{s.GamePort}", "АДРЕС СЕРВЕРА — подключение в игре (F1)", false);

        var lan = LocalIPv4() ?? "127.0.0.1";
        var pub = await PublicIPv4Async();
        return pub != null
            ? new($"{pub}:{s.GamePort}", "ВНЕШНИЙ IP — подключение в игре (F1)", false)
            : new($"{lan}:{s.GamePort}", "ЛОКАЛЬНЫЙ АДРЕС (LAN) — подключение в игре (F1)", false);
    }

    /// <summary>
    /// steam:// URL для «подключиться в один клик». Для домена без порта
    /// резолвим SRV/A сами; если DNS молчит — +connect сделает SRV-резолв уже клиент Rust.
    /// </summary>
    public static async Task<string> SteamUrlAsync(string address)
    {
        if (address.Contains(':'))
            return $"steam://connect/{address}";
        var ep = await Task.Run(() => DnsSrvResolver.ResolveRust(address, App.Settings.GamePort));
        return ep != null
            ? $"steam://connect/{ep.Target}:{ep.Port}"
            : $"steam://run/252490//+connect%20{address}";
    }

    public static void OpenSteamUrl(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static string? LocalIPv4()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                                  && !IPAddress.IsLoopback(a))?.ToString();
        }
        catch { return null; }
    }

    // Свой публичный IP — тот же ipwho.is, что и геолокация игроков
    private static async Task<string?> PublicIPv4Async()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var json = await http.GetStringAsync("https://ipwho.is/?fields=ip");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ip", out var ip) ? ip.GetString() : null;
        }
        catch { return null; }
    }
}
