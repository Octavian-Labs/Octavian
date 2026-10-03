using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using Facepunch;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaSpawnTest", "Octavian", "0.1.0")]
    [Description("[DEV] Случайные точки спавна по биомам карты: вдали от построек, воды и монументов. Биомы включаются по онлайну.")]
    public class OctaSpawnTest : RustPlugin
    {
        [PluginReference]
        private Plugin ZoneManager;

        private readonly Dictionary<TerrainBiome.Enum, List<Vector3>> _spawnpoints =
            new Dictionary<TerrainBiome.Enum, List<Vector3>>();

        private bool _disabled;

        // слои для проверки «рядом постройки» и «точка на террейне»
        private const int VisRaycastLayers = 1 << 8 | 1 << 17 | 1 << 21;
        private const int PointRaycastLayers = 1 << 4 | 1 << 8 | 1 << 10 | 1 << 15 | 1 << 16 | 1 << 21 | 1 << 23 | 1 << 27 | 1 << 28 | 1 << 29;

        private int _blockedTopology;

        private ConfigData _config;

        // dev-анонс в чат при загрузке/перезагрузке
        private void Init() => timer.Once(1f, () =>
            PrintToChat($"Плагин \"{Name}\" dev — загружен"));

        private void Loaded()
        {
            _blockedTopology = 0;
            foreach (var name in _config.Spawn.BlockedTopologies)
            {
                if (Enum.TryParse(name, out TerrainTopology.Enum value))
                    _blockedTopology |= (int)value;
                else
                    PrintWarning($"Неизвестная топология '{name}'");
            }
        }

        private void OnServerInitialized() => GenerateSpawnpoints();

        // Перехватывает КАЖДЫЙ респавн — включая спальники (поведение оригинала)
        private object OnPlayerRespawn(BasePlayer player)
        {
            if (_disabled) return null;

            var target = GetSpawnPoint(false);
            if (target == null) return null;

            return new BasePlayer.SpawnPoint
            {
                pos = (Vector3)target,
                rot = Quaternion.identity
            };
        }

        #region Генерация точек

        private void GenerateSpawnpoints()
        {
            float halfSize = World.Size * 0.5f;

            for (int i = 0; i < _config.Generation.Attempts; i++)
            {
                Vector2 rnd = UnityEngine.Random.insideUnitCircle * 0.95f * halfSize;
                var position = new Vector3(rnd.x, 500f, rnd.y);

                if (ContainsTopologyAtPoint(_blockedTopology, position))
                    continue;

                RaycastHit hit;
                if (!RaycastTerrain(position, out hit))
                    continue;

                // честная проверка склона — в оригинале MaxSlope висел мёртвым
                if (Vector3.Angle(hit.normal, Vector3.up) > _config.Generation.MaxSlope)
                    continue;

                position.y = hit.point.y;

                if (IsPositionInZone(position))
                    continue;

                var biome = (TerrainBiome.Enum)TerrainMeta.BiomeMap.GetBiomeMaxType(position);

                List<Vector3> list;
                if (!_spawnpoints.TryGetValue(biome, out list))
                    _spawnpoints[biome] = list = new List<Vector3>();
                list.Add(position);
            }

            Puts($"Сгенерировано {_spawnpoints.Sum(x => x.Value.Count)} точек спавна");
        }

        private object GetSpawnPoint(bool ignorePlayerRestriction)
        {
            if (_spawnpoints.Count == 0) return null;

            TerrainBiome.Enum biome;
            if (ignorePlayerRestriction)
            {
                biome = _spawnpoints.Keys.ElementAt(UnityEngine.Random.Range(0, _spawnpoints.Count));
            }
            else
            {
                int online = BasePlayer.activePlayerList.Count;
                var available = Pool.Get<List<TerrainBiome.Enum>>();
                foreach (var kvp in _config.Spawn.Biomes)
                    if (kvp.Value.Enabled && online >= kvp.Value.Players)
                        available.Add(kvp.Key);

                if (available.Count == 0)
                {
                    Pool.FreeUnmanaged(ref available);
                    return null;
                }
                biome = available.GetRandom();
                Pool.FreeUnmanaged(ref available);
            }

            return GetSpawnPoint(biome);
        }

        private object GetSpawnPoint(TerrainBiome.Enum biome)
        {
            List<Vector3> points;
            if (!_spawnpoints.TryGetValue(biome, out points) || points.Count == 0)
                return null;

            var position = points.GetRandom();
            if (position == Vector3.zero) return null;

            // точка застроена — выкидываем и пробуем ещё
            List<BaseEntity> near = Pool.Get<List<BaseEntity>>();
            Vis.Entities(position, _config.Generation.BuildingDistance, near, VisRaycastLayers);
            int count = near.Count;
            Pool.FreeUnmanaged(ref near);

            if (count > 0)
            {
                points.Remove(position);
                if (points.Count == 0)
                {
                    PrintWarning("Все точки спавна застроены — регенерирую!");
                    GenerateSpawnpoints();
                }
                return GetSpawnPoint(biome);
            }

            return position;
        }

        #endregion

        #region Helpers

        private bool ContainsTopologyAtPoint(int mask, Vector3 position) =>
            (TerrainMeta.TopologyMap.GetTopology(position) & mask) != 0;

        private bool RaycastTerrain(Vector3 position, out RaycastHit hit) =>
            Physics.Raycast(position, Vector3.down, out hit, 500f, PointRaycastLayers)
            && hit.collider is TerrainCollider;

        private bool IsPositionInZone(Vector3 position)
        {
            if (_config.Spawn.BlockedZones.Length == 0) return false;

            // PluginReference залипает после перезагрузки ZoneManager — ищем по имени
            Plugin zm = ZoneManager;
            if (zm == null || !zm.IsLoaded)
            {
                zm = null;
                foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
                    if (p.Name == "ZoneManager" && p.IsLoaded) { zm = p; break; }
            }
            if (zm == null) return false;

            foreach (var zoneId in _config.Spawn.BlockedZones)
            {
                var success = zm.Call("IsPositionInZone", zoneId, position);
                if (success is bool && (bool)success)
                    return true;
            }
            return false;
        }

        private TerrainBiome.Enum ParseBiome(string type)
        {
            try { return (TerrainBiome.Enum)Enum.Parse(typeof(TerrainBiome.Enum), type, true); }
            catch { return TerrainBiome.Enum.Temperate; }
        }

        #endregion

        #region API

        [HookMethod("GetSpawnPointAtBiome")]
        public object GetSpawnPointAtBiome(string biomeTypeStr) => GetSpawnPoint(ParseBiome(biomeTypeStr));

        [HookMethod("DisableSpawnSystem")]
        public void DisableSpawnSystem() => _disabled = true;

        [HookMethod("GetSpawnPoint")]
        public object GetSpawnPointHook() => GetSpawnPoint(true);

        #endregion

        #region Команды

        [ChatCommand("octaspawn.show")]
        private void CmdShowSpawns(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            SendReply(player, $"Всего точек: {_spawnpoints.Sum(x => x.Value.Count)}");

            foreach (var kvp in _spawnpoints)
            {
                Color color = kvp.Key == TerrainBiome.Enum.Arctic ? Color.blue
                            : kvp.Key == TerrainBiome.Enum.Arid ? Color.red
                            : kvp.Key == TerrainBiome.Enum.Temperate ? Color.green
                            : Color.white;

                foreach (var position in kvp.Value)
                {
                    player.SendConsoleCommand("ddraw.sphere", 30f, color, position, 1f);
                    player.SendConsoleCommand("ddraw.line", 30f, color, position, position + Vector3.up * 200f);
                }
            }
        }

        [ConsoleCommand("octaspawntest.stats")]
        private void CmdStats(ConsoleSystem.Arg arg)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Точек спавна: {_spawnpoints.Sum(x => x.Value.Count)}, биомов: {_spawnpoints.Count}, disabled={_disabled}");
            foreach (var kvp in _spawnpoints)
            {
                _config.Spawn.Biomes.TryGetValue(kvp.Key, out var opts);
                var enabled = opts != null && opts.Enabled;
                var players = opts != null ? opts.Players : 0;
                sb.AppendLine($"  {kvp.Key}: {kvp.Value.Count} точек (enabled={enabled}, minOnline={players})");
            }
            Puts(sb.ToString());
        }

        #endregion

        #region Config

        private class ConfigData
        {
            [JsonProperty("Генерация")]
            public GenerationOptions Generation { get; set; } = new GenerationOptions();

            [JsonProperty("Спавн")]
            public SpawnOptions Spawn { get; set; } = new SpawnOptions();

            public class GenerationOptions
            {
                [JsonProperty("Попыток генерации точек")]
                public int Attempts { get; set; } = 3000;

                [JsonProperty("Максимальный уклон (градусы)")]
                public float MaxSlope { get; set; } = 45f;

                [JsonProperty("Минимальная дистанция от построек (метры)")]
                public float BuildingDistance { get; set; } = 15f;
            }

            public class SpawnOptions
            {
                [JsonProperty("Биомы")]
                public Dictionary<TerrainBiome.Enum, BiomeOptions> Biomes { get; set; } =
                    new Dictionary<TerrainBiome.Enum, BiomeOptions>
                    {
                        [TerrainBiome.Enum.Arctic]   = new BiomeOptions { Enabled = true, Players = 30 },
                        [TerrainBiome.Enum.Tundra]   = new BiomeOptions { Enabled = true, Players = 20 },
                        [TerrainBiome.Enum.Arid]     = new BiomeOptions { Enabled = true, Players = 10 },
                        [TerrainBiome.Enum.Temperate] = new BiomeOptions { Enabled = true, Players = 1 },
                    };

                [JsonProperty("Запретные зоны ZoneManager (ID зон)")]
                public string[] BlockedZones { get; set; } = new string[0];

                [JsonProperty("Запретные топологии")]
                public string[] BlockedTopologies { get; set; } =
                    { "Cliff", "Cliffside", "Lake", "Ocean", "Monument", "Offshore", "River", "Swamp", "Rail" };
            }

            public class BiomeOptions
            {
                [JsonProperty("Точки генерируются в этом биоме")]
                public bool Enabled { get; set; }

                [JsonProperty("Минимальный онлайн для выбора этого биома")]
                public int Players { get; set; }
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) LoadDefaultConfig();
            }
            catch
            {
                PrintWarning($"Ошибка чтения конфигурации 'carbon/configs/{Name}', создаём новую.");
                LoadDefaultConfig();
            }
            NextTick(SaveConfig);
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);
        protected override void LoadDefaultConfig() => _config = new ConfigData();

        #endregion
    }
}
