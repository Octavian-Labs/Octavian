// OctaRecycle — скорость переработчиков по VIP-уровню игрока.
// Механика: переработчик крутит RecycleThink() (приватный, рефлексия) —
// один вызов перерабатывает стек из одного входного слота. Плагин вешает
// свой таймер на включённый переработчик с интервалом по уровню:
//   без статуса 5с, бронза 4с, серебро 3с, золото 2с, бриллиант 1с.
// За тик делается Multiplier × Level.SpeedMultiplier циклов; Instant —
// крутит цикл, пока во входных слотах есть что перерабатывать.
// Уровни: octarecycle.t1..t6 ИЛИ OctVIP GetVipLevel — та же лестница,
// что и в OctaForge. Пользователь = игрок, включивший переработчик
// (OnRecyclerToggle), иначе тот, кто его сейчас лутает.
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaRecycle", "Octavian", "1.1.0")]
    [Description("VIP-tiered recycling speed")]

    public class OctaRecycle : RustPlugin
    {
        #region --- EVENTS -------------------------------------------------------------------------------------------------
        private void OnServerInitialized()
        {
            foreach (var perm in PermLevel)
                if (!string.IsNullOrEmpty(perm)) permission.RegisterPermission(perm, this);

            // подхватываем переработчики, оставшиеся включёнными после перезагрузки
            foreach (var rec in UnityEngine.Object.FindObjectsOfType<Recycler>())
                if (rec.IsOn()) Arm(rec);
        }

        // кто включил переработчик — его уровень и задаёт скорость.
        // хук стреляет ДО применения состояния — читаем IsOn на следующий тик
        private void OnRecyclerToggle(Recycler recycler, BasePlayer player)
        {
            if (recycler?.net == null || player == null) return;
            var id = recycler.net.ID.Value;
            var uid = player.userID;
            NextTick(() =>
            {
                if (recycler == null || recycler.IsDestroyed) return;
                if (recycler.IsOn())
                {
                    _toggler[id] = uid;
                    Arm(recycler);
                }
                else
                {
                    _toggler.Remove(id);
                    KillTimer(id);
                }
            });
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity is Recycler && entity.net != null)
            {
                _toggler.Remove(entity.net.ID.Value);
                KillTimer(entity.net.ID.Value);
            }
        }

        private void Unload()
        {
            foreach (var t in _timers.Values) t?.Destroy();
            _timers.Clear();
            _toggler.Clear();
        }
        #endregion

        #region --- ЦИКЛ -----------------------------------------------------------------------------------------------------
        // RecycleThink приватный — крутим рефлексией
        private static readonly MethodInfo _recycleThink =
            typeof(Recycler).GetMethod("RecycleThink",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

        private static void RecycleThink(Recycler recycler)
        {
            if (recycler == null || recycler.IsDestroyed) return;
            _recycleThink?.Invoke(recycler, null);
        }

        // вешает таймер с интервалом уровня на работающий переработчик
        private void Arm(Recycler recycler)
        {
            if (recycler?.net == null) return;
            var id = recycler.net.ID.Value;
            var level = ResolveLevel(FindUser(recycler));
            var lc = GetLevel(level);
            var interval = IntervalFor(level, lc);
            KillTimer(id);
            _timers[id] = timer.Every(interval, () => Burst(recycler));
        }

        private void Burst(Recycler recycler)
        {
            if (recycler == null || recycler.IsDestroyed) return;
            var id = recycler.net.ID.Value;
            if (!recycler.IsOn() || !recycler.HasRecyclable())
            {
                KillTimer(id);
                return;
            }

            var level = ResolveLevel(FindUser(recycler));
            var lc = GetLevel(level);

            var effective = lc.Instant ? InstantLoopCap : (int)(Settings.Multiplier * lc.SpeedMultiplier);
            for (var i = 0; i < effective; i++)
            {
                if (!recycler.IsOn() || !recycler.HasRecyclable()) break;
                var before = InputCount(recycler);
                RecycleThink(recycler);
                // выход забит или предмет не перерабатывается — выходим, не крутим впустую
                if (InputCount(recycler) >= before) break;
            }
        }

        // сумма предметов во входных слотах (первая половина контейнера)
        private static int InputCount(Recycler recycler)
        {
            var total = 0;
            var n = recycler.inventory.capacity / 2;
            for (var i = 0; i < n; i++)
            {
                var it = recycler.inventory.GetSlot(i);
                if (it != null) total += it.amount;
            }
            return total;
        }

        // кто пользуется: сейчас лутает > кто включал > владелец
        private ulong FindUser(Recycler recycler)
        {
            foreach (var p in BasePlayer.activePlayerList)
                if (p.inventory.loot.entitySource == recycler) return p.userID;
            if (recycler.net != null && _toggler.TryGetValue(recycler.net.ID.Value, out var uid))
                return uid;
            return recycler.OwnerID;
        }

        private void KillTimer(ulong id)
        {
            if (_timers.TryGetValue(id, out var t)) { t?.Destroy(); _timers.Remove(id); }
        }

        private readonly Dictionary<ulong, ulong> _toggler = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, Timer> _timers = new Dictionary<ulong, Timer>();
        #endregion

        #region --- VIP-УРОВНИ -----------------------------------------------------------------------------------------------
        // пермишены уровней — выдаются группам vip_*; при наличии OctVIP уровень берётся из него
        private static readonly string[] PermLevel =
        {
            "", "octarecycle.t1", "octarecycle.t2", "octarecycle.t3",
            "octarecycle.t4", "octarecycle.t5", "octarecycle.t6"
        };

        private const int InstantLoopCap = 20000; // страховка от бесконечного цикла

        // интервал тика по уровню, если в конфиге 0 (дефолт): 5/4/3/2/1/1/1 сек
        private static readonly float[] DefaultInterval = { 5f, 4f, 3f, 2f, 1f, 1f, 1f };

        private float IntervalFor(int level, SettingsObject.LevelSettings lc)
        {
            if (lc.IntervalSeconds > 0f) return lc.IntervalSeconds;
            if (level < 0) level = 0;
            if (level >= DefaultInterval.Length) level = DefaultInterval.Length - 1;
            return DefaultInterval[level];
        }

        // PluginReference залипает на выгруженной инстанции — ищем живой плагин по имени (PITFALLS §11)
        private Plugin FindOctaPlugin(string name)
        {
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
            {
                if (p.Name == name && p.IsLoaded)
                    return p;
            }
            return null;
        }

        // 0 = без уровня; 1..6 — bronze..admin. OctVIP в приоритете, пермишены — фолбэк
        private int ResolveLevel(ulong userId)
        {
            if (userId == 0) return 0;

            var vip = FindOctaPlugin("OctVIP");
            if (vip != null)
            {
                var lvl = vip.Call<int>("GetVipLevel", userId);
                if (lvl > 0) return lvl;
            }

            var idStr = userId.ToString();
            for (var i = PermLevel.Length - 1; i >= 1; i--)
                if (permission.UserHasPermission(idStr, PermLevel[i])) return i;
            return 0;
        }

        private SettingsObject.LevelSettings GetLevel(int level)
        {
            SettingsObject.LevelSettings cfg;
            return Settings.Levels.TryGetValue(level, out cfg) ? cfg : Settings.DefaultLevel;
        }
        #endregion

        #region --- CONFIGURATION ------------------------------------------------------------------------------------------
        private SettingsObject Settings;

        protected override void LoadConfig()
        {
            try
            {
                base.LoadConfig();
                Settings = Config.ReadObject<SettingsObject>();

                if (Settings == null || Settings.Version != Settings.Revision)
                    WriteWarning($"Your config file needs to be updated to v{Settings.Revision}.");
            }
            catch
            {
                LoadDefaultConfig();
            }
            finally
            {
                ValidateSettings();
                SaveConfig();
            }
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning($"Created a new default v{Version} config file");
            Settings = new SettingsObject();
            Settings.Version = Settings.Revision;
        }

        protected void ValidateSettings()
        {
            Settings.Multiplier = Mathf.Clamp(Settings.Multiplier, 1, 100);
            foreach (KeyValuePair<int, SettingsObject.LevelSettings> kvp in Settings.Levels)
            {
                kvp.Value.SpeedMultiplier = Mathf.Clamp(kvp.Value.SpeedMultiplier, 1f, 100f);
                kvp.Value.IntervalSeconds = Mathf.Clamp(kvp.Value.IntervalSeconds, 0f, 60f);
            }
            Settings.DefaultLevel.SpeedMultiplier = Mathf.Clamp(Settings.DefaultLevel.SpeedMultiplier, 1f, 100f);
        }

        protected override void SaveConfig()
            => Config.WriteObject(Settings, true);

        internal class SettingsObject
        {
            [JsonIgnore]
            public readonly VersionNumber Revision = new VersionNumber
            {
                Major = 1,
                Minor = 1,
                Patch = 0
            };

            public VersionNumber Version
            { get; set; }

            // базовый множитель — выставлен = рейту сервера (x5)
            [JsonProperty("Базовый множитель")]
            public int Multiplier
            { get; set; } = 5;

            // уровень игрока → интервал тика, доп. множитель или мгновенная
            // переработка. Интервал 0 = таблица по умолчанию (5/4/3/2/1с).
            // Эффективная скорость = Multiplier × Level.SpeedMultiplier циклов
            // за тик; Instant — все входные слоты за тик.
            [JsonProperty("Уровни (0=default 1=bronze 2=silver 3=gold 4=brilliant 5=moderator 6=admin)")]
            public Dictionary<int, LevelSettings> Levels
            { get; set; } = new Dictionary<int, LevelSettings>
            {
                [0] = new LevelSettings { SpeedMultiplier = 1.0f },  // default: 5с, x5
                [1] = new LevelSettings { SpeedMultiplier = 1.5f },  // bronze:  4с, x7.5
                [2] = new LevelSettings { SpeedMultiplier = 2.0f },  // silver:  3с, x10
                [3] = new LevelSettings { SpeedMultiplier = 3.0f },  // gold:    2с, x15
                [4] = new LevelSettings { Instant = true },          // brilliant: 1с, всё сразу
                [5] = new LevelSettings { Instant = true },
                [6] = new LevelSettings { Instant = true },
            };

            [JsonIgnore]
            public LevelSettings DefaultLevel
            { get; } = new LevelSettings { SpeedMultiplier = 1.0f };

            internal class LevelSettings
            {
                // интервал между тиками переработки, сек (0 = авто: 5/4/3/2/1)
                [JsonProperty("Интервал тика (сек)")]
                public float IntervalSeconds
                { get; set; } = 0f;

                // циклов за тик = Multiplier × это значение (1 = без бонуса)
                [JsonProperty("Множитель скорости")]
                public float SpeedMultiplier
                { get; set; } = 1.0f;

                // мгновенная переработка: все входные слоты за тик
                [JsonProperty("Мгновенная переработка")]
                public bool Instant
                { get; set; } = false;
            }
        }
        #endregion

        #region --- LOGGING --------------------------------------------------------------------------------------------------
        internal void WriteWarning(string format, params object[] args)
            => Puts("[WARNING] " + format, args);
        #endregion
    }
}
