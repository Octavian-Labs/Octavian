using System.Linq;
using System.Text.Json.Serialization;

namespace OctavianRSM.Models;

// Ответ RCON `playerlist` — JSON-массив игроков
public class PlayerInfo
{
    [JsonPropertyName("SteamID")] public string SteamID { get; set; } = "";
    [JsonPropertyName("OwnerSteamID")] public string OwnerSteamID { get; set; } = "";
    [JsonPropertyName("DisplayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("Ping")] public int Ping { get; set; }
    [JsonPropertyName("Address")] public string Address { get; set; } = "";
    [JsonPropertyName("ConnectedSeconds")] public int ConnectedSeconds { get; set; }
    [JsonPropertyName("VoiationLevel")] public float VoiationLevel { get; set; }
    [JsonPropertyName("Health")] public float Health { get; set; }

    // Не из JSON — заполняется из локальной базы при обновлении списка
    public string CountryCode { get; set; } = "";
    public string CountryName { get; set; } = "";
    public long TotalSecondsBase { get; set; }

    public string Connected => TimeSpanText(ConnectedSeconds);
    public string Ip => Address.Split(':')[0];
    public string Initials => string.Concat(DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public string HealthText => $"{Health:0}";
    public string CountryText => string.IsNullOrEmpty(CountryCode) ? "…" : CountryCode;
    public string CountryTip => string.IsNullOrEmpty(CountryName) ? "геолокация…" : CountryName;
    public string TotalText => OctavianRSM.Services.KnownPlayer.Fmt(TotalSecondsBase + ConnectedSeconds);
    private static string TimeSpanText(int sec) => $"{sec / 3600}ч {(sec % 3600) / 60}м";
}
