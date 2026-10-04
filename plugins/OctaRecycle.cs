// OctaRecycle — скорость переработчиков по VIP-уровню игрока.
// Механика: ванильный RecycleThink() обрабатывает один предмет из входных
// слотов за цикл. Плагин после каждого ванильного цикла догоняет цикл
// вручную (рефлексией) — эффективная скорость = Базовый множитель (рейт
// сервера x5) × Множитель уровня. Instant — крутит цикл, пока во входных
// слотах есть что перерабатывать (все 6 слотов за один тик).
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
    [Info("OctaRecycle", "Octavian", "1.0.0")]
    [Description("VIP-tiered recycling speed")]

    public class OctaRecycle : RustPlugin
    {
        #region --- EVENTS -------------------------------------------------------------------------------------------------
        private void OnServerInitialized()
        {
            foreach (var perm in PermLevel)
                if (!string.IsNullOrEmpty(perm)) permission.RegisterPermission(perm, this);
        }

        // кто включил переработчик — его уровень и задаёт скорость
        private void OnRecyclerToggle(Recycler recycler, BasePlayer player)
        {
            if (recycler?.net == null || player == null) return;
            var id = recycler.net.ID.Value;
            if (recycler.IsOn()) _toggler[id] = player.userID;
            else _toggler.Remove(id);
        }

        // ванильный цикл переработал один предмет — догоняем по уровню
        private void OnItemRecycle(Recycler recycler, Item item)
        {
            if (recycler?.net == null || recycler.inventory == null) return;
            var id = recycler.net.ID.Value;
            if (!_inFlight.Add(id)) return; // наш ручной цикл сам дёргает хук

            // на следующий тик — после завершения ванильного цикла
            NextTick(() =>
            {
                try
                {
                    if (recycler == null || recycler.IsDestroyed || !recycler.IsOn()) return;

                    var level = ResolveLevel(FindUser(recycler));
                    var lc = GetLevel(level);

                    if (lc.Instant)
                    {
                        var guard = 0;
                        while (recycler.IsOn() && recycler.HasRecyclable() && guard++ < InstantLoopCap)
                            RecycleThink(recycler);
                        return;
                    }

                    var effective = Settings.Multiplier * lc.SpeedMultiplier;
                    for (var i = 1; i < (int)effective && recycler.IsOn() && recycler.HasRecyclable(); i++)
                        RecycleThink(recycler);
                }
                finally
                {
                    _inFlight.Remove(id);
                }
            });
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity is Recycler && entity.net != null)
                _toggler.Remove(entity.net.ID.Value);
        }

        private void Unload()
        {
            _toggler.Clear();
            _inFlight.Clear();
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

        // кто пользуется: сейчас лутает > кто включал > владелец
        private ulong FindUser(Recycler recycler)
        {
            foreach (var p in BasePlayer.activePlayerList)
                if (p.inventory.loot.entitySource == recycler) return p.userID;
            if (recycler.net != null && _toggler.TryGetValue(recycler.net.ID.Value, out var uid))
                return uid;
            return recycler.OwnerID;
        }

        private readonly Dictionary<ulong, ulong> _toggler = new Dictionary<ulong, ulong>();
        private readonly HashSet<ulong> _inFlight = new HashSet<ulong>();
        #endregion

        #region --- VIP-УРОВНИ -----------------------------------------------------------------------------------------------
        // пермишены уровней — выдаются группам vip_*; при наличии OctVIP уровень берётся из него
        private static readonly string[] PermLevel =
        {
            "", "octarecycle.t1", "octarecycle.t2", "octarecycle.t3",
            "octarecycle.t4", "octarecycle.t5", "octarecycle.t6"
        };

        private const int InstantLoopCap = 20000; // страховка от бесконечного цикла

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
                kvp.Value.SpeedMultiplier = Mathf.Clamp(kvp.Value.SpeedMultiplier, 1f, 100f);
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
                Minor = 0,
                Patch = 0
            };

            public VersionNumber Version
            { get; set; }

            // базовый множитель — выставлен = рейту сервера (x5)
            [JsonProperty("Базовый множитель")]
            public int Multiplier
            { get; set; } = 5;

            // уровень игрока → доп. множитель или мгновенная переработка.
            // эффективная скорость = Multiplier × Level.SpeedMultiplier
            [JsonProperty("Уровни (0=default 1=bronze 2=silver 3=gold 4=brilliant 5=moderator 6=admin)")]
            public Dictionary<int, LevelSettings> Levels
            { get; set; } = new Dictionary<int, LevelSettings>
            {
                [0] = new LevelSettings { SpeedMultiplier = 1.0f },  // default: чистый рейт x5
                [1] = new LevelSettings { SpeedMultiplier = 1.5f },  // bronze:  x7.5
                [2] = new LevelSettings { SpeedMultiplier = 2.0f },  // silver:  x10
                [3] = new LevelSettings { SpeedMultiplier = 3.0f },  // gold:    x15
                [4] = new LevelSettings { Instant = true },          // brilliant: все слоты сразу
                [5] = new LevelSettings { Instant = true },
                [6] = new LevelSettings { Instant = true },
            };

            [JsonIgnore]
            public LevelSettings DefaultLevel
            { get; } = new LevelSettings { SpeedMultiplier = 1.0f };

            internal class LevelSettings
            {
                // доп. множитель к базовой скорости (1 = без бонуса)
                [JsonProperty("Множитель скорости")]
                public float SpeedMultiplier
                { get; set; } = 1.0f;

                // мгновенная переработка: все входные слоты за один тик
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
