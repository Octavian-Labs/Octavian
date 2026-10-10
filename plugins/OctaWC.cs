using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    // OctaWC — провайдер даты вайпа для OctaHub и других панелей.
    // Два режима:
    //   interval — следующий вайп = начало текущего сейва (SaveRestore.SaveCreatedTime)
    //              + «Интервал вайпа в днях»;
    //   fixed    — ручная дата из конфига «Фиксированная дата вайпа UTC»
    //              (или установленная командой octawc.setwipe).
    // API (HookMethod):
    //   GetNextWipeUnix()        -> long unix-время следующего вайпа (0 = не задан)
    //   GetWipeInfo()            -> JObject {nextUnix, secondsLeft, mode, saveCreatedUnix}
    //   GetCountdownFormated_API -> строка вида "3д 14ч 22м" (совместимость с
    //                             старым WipeCountdown — токен {wipecountdown})
    [Info("OctaWC", "Octavian", "1.1.0")]
    [Description("Провайдер даты вайпа: интервал от сейва или фиксированная дата")]
    public class OctaWC : RustPlugin
    {
        private ConfigData Cfg;

        private class ConfigData
        {
            [JsonProperty("Режим: interval (от сейва) | fixed (ручная дата)")]
            public string Mode = "interval";
            [JsonProperty("Интервал вайпа в днях (режим interval)")]
            public int IntervalDays = 7;
            [JsonProperty("Фиксированная дата/время вайпа UTC (режим fixed), ISO 8601, напр. 2025-10-16T18:00:00Z")]
            public string FixedDateUtc = "";
            [JsonProperty("Сдвиг вайпа от начала сейва, часов (режим interval)")]
            public int IntervalOffsetHours = 0;
        }

        protected override void LoadDefaultConfig() => Config.WriteObject(new ConfigData(), true);

        private void Init()
        {
            Cfg = Config.ReadObject<ConfigData>() ?? new ConfigData();
            Config.WriteObject(Cfg, true);
        }

        // Следующий вайп в unix-секундах; 0 — если определить нельзя.
        private long ComputeNextWipeUnix()
        {
            if (Cfg == null) return 0;
            if (string.Equals(Cfg.Mode, "fixed", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTimeOffset.TryParse(Cfg.FixedDateUtc, out var dt))
                    return dt.ToUnixTimeSeconds();
                return 0;
            }
            // interval: начало текущего сейва + N дней + опциональный сдвиг
            var created = SaveRestore.SaveCreatedTime;
            if (created == default(DateTime) || created.Year < 2000) return 0;
            int days = Cfg.IntervalDays > 0 ? Cfg.IntervalDays : 7;
            var next = created.AddDays(days).AddHours(Cfg.IntervalOffsetHours);
            return new DateTimeOffset(next, TimeSpan.Zero).ToUnixTimeSeconds();
        }

        private string FormatCountdown(long secondsLeft)
        {
            if (secondsLeft <= 0) return "скоро";
            var ts = TimeSpan.FromSeconds(secondsLeft);
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}д {ts.Hours}ч";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}ч {ts.Minutes}м";
            return $"{(int)ts.TotalMinutes}м {ts.Seconds}с";
        }

        // ── API ───────────────────────────────────────────────────────────────

        [HookMethod("GetNextWipeUnix")]
        public long API_GetNextWipeUnix() => ComputeNextWipeUnix();

        [HookMethod("GetWipeInfo")]
        public JObject API_GetWipeInfo()
        {
            long next = ComputeNextWipeUnix();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new JObject
            {
                ["nextUnix"] = next,
                ["secondsLeft"] = next > 0 ? Math.Max(0, next - now) : 0,
                ["mode"] = Cfg?.Mode ?? "interval",
                ["saveCreatedUnix"] = SaveRestore.SaveCreatedTime.Year > 2000
                    ? new DateTimeOffset(SaveRestore.SaveCreatedTime, TimeSpan.Zero).ToUnixTimeSeconds()
                    : 0L,
                ["formatted"] = next > 0 ? FormatCountdown(next - now) : "—",
            };
        }

        // Octa API v1: GetWipeInfo в конверте {ok,code,data}
        // (Docs/OctaAPI-v1.md). GetWipeInfo/GetCountdownFormated_API — legacy.
        [HookMethod("WC_GetInfo")]
        public JObject WC_GetInfo() => new JObject
        {
            ["ok"] = true, ["code"] = "OK", ["data"] = API_GetWipeInfo()
        };

        // Совместимость со старым WipeCountdown — старые шаблоны {wipecountdown}
        // продолжают работать без изменений.
        [HookMethod("GetCountdownFormated_API")]
        public string API_GetCountdownFormated()
        {
            long next = ComputeNextWipeUnix();
            if (next <= 0) return "—";
            return FormatCountdown(next - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        // ── Команды ───────────────────────────────────────────────────────────

        [ConsoleCommand("octawc.setwipe")]
        private void CmdSetWipe(ConsoleSystem.Arg arg)
        {
            var p = arg?.Connection?.player as BasePlayer;
            if (p != null && !p.IsAdmin) return;
            if (!arg.HasArgs()) { arg.ReplyWith("octawc.setwipe <ISO8601 UTC, напр. 2025-10-16T18:00:00Z>"); return; }
            string iso = string.Join(" ", arg.Args);
            if (!DateTimeOffset.TryParse(iso, out var dt)) { arg.ReplyWith("Неверный формат даты."); return; }
            Cfg.Mode = "fixed";
            Cfg.FixedDateUtc = dt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            Config.WriteObject(Cfg, true);
            arg.ReplyWith($"Следующий вайп установлен: {dt.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        }

        [ConsoleCommand("octawc.info")]
        private void CmdInfo(ConsoleSystem.Arg arg)
        {
            var p = arg?.Connection?.player as BasePlayer;
            if (p != null && !p.IsAdmin) return;
            var info = API_GetWipeInfo();
            arg.ReplyWith($"OctaWC: mode={info["mode"]}, nextUnix={info["nextUnix"]}, left={info["formatted"]}, saveCreated={info["saveCreatedUnix"]}");
        }
    }
}
