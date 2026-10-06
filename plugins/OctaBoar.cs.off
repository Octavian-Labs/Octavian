using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    // OctaBoar — «Кабан-Босс»: элитный кабан со свитой поросят,
    // спавнится по расписанию недалеко от ранчо, мап-маркер, лут, награды.
    // Клон OctaBull: boar.prefab — кабаны агрессивны сами по себе.
    [Info("OctaBoar", "Octavian", "1.1.1")]
    [Description("Элитный босс-кабан у ранчо: расписание спавна, свита поросят, лут, награды")]
    public class OctaBoar : RustPlugin
    {
        #region Config

        private ConfigData _cfg;

        private class LootEntry
        {
            [JsonProperty("Шортнейм предмета")] public string ShortName = "scrap";
            [JsonProperty("Мин. кол-во")] public int MinAmount = 100;
            [JsonProperty("Макс. кол-во")] public int MaxAmount = 300;
            [JsonProperty("Шанс (0-1)")] public float Chance = 0.5f;
            [JsonProperty("Скин ID (0 = без)")] public ulong SkinId = 0;
            [JsonProperty("Чертёж: целевой предмет (если шортнейм = blueprint)")] public string BlueprintTarget = "";
        }

        private class BossSettings
        {
            [JsonProperty("Префаб босса")] public string Prefab = "assets/rust.ai/agents/boar/boar.prefab";
            [JsonProperty("Имя босса")] public string DisplayName = "КАБАН-БОСС";
            [JsonProperty("Здоровье")] public float Health = 2000f;
            [JsonProperty("Множитель входящего урона (пули)")] public float BulletScale = 0.7f;
            [JsonProperty("Множитель входящего урона (взрывы)")] public float ExplosionScale = 0.9f;
            [JsonProperty("Множитель входящего урона (миляшник/прочее)")] public float OtherScale = 0.9f;
            [JsonProperty("Иммунитет к огню/кровотечению/отравлению/холоду")] public bool ElementalImmune = true;
            [JsonProperty("Множитель исходящего урона")] public float DamageMultiplier = 3f;
            [JsonProperty("Порог ярости (доля HP)")] public float RageThreshold = 0.5f;
            [JsonProperty("Множитель урона в ярости")] public float RageDamageMultiplier = 1.5f;
            [JsonProperty("Радиус агро (м)")] public float AggroRadius = 90f;
            [JsonProperty("Макс. время жизни (мин)")] public float MaxLifeMinutes = 60f;
            [JsonProperty("Масштаб модели (1 = обычный)")] public float Scale = 2f;
            [JsonProperty("Множитель скорости (1 = обычная)")] public float SpeedMultiplier = 1.5f;
            [JsonProperty("Экстра агрессия (не убегает от пуль)")] public bool HyperAggro = true;
            [JsonProperty("Свита: кабанчиков вокруг босса (шт)")] public int EscortCount = 5;
            [JsonProperty("Свита: радиус спавна (м)")] public float EscortRadius = 10f;
            [JsonProperty("Свита: масштаб кабанчиков (1 = обычный)")] public float EscortScale = 0.55f;
        }

        private class SpawnSettings
        {
            [JsonProperty("Интервал спавна (мин)")] public float IntervalMinutes = 180f;
            [JsonProperty("Разброс интервала ± (мин)")] public float IntervalJitterMinutes = 30f;
            [JsonProperty("Предупреждение за (мин)")] public float WarnBeforeMinutes = 5f;
            [JsonProperty("Кратер маркера на карте (0-1, доля карты)")] public float MarkerRadius = 0.03f;
            [JsonProperty("Показывать маркер на карте")] public bool ShowMarker = true;
            [JsonProperty("Держаться от края карты (м)")] public float MapMargin = 400f;
            [JsonProperty("Имена монументов-ранчо (через запятую, частичное совпадение)")] public string RanchNames = "ranch,barn";
            [JsonProperty("Дистанция от края ранчо (м, мин-макс)")] public float RanchMinDist = 15f;
            [JsonProperty("Дистанция от края ранчо (макс)")] public float RanchMaxDist = 70f;
        }

        private class RewardSettings
        {
            [JsonProperty("Валюта за убийство (ServerRewards/Economics)")] public int Currency = 750;
            [JsonProperty("Валюта участникам (нанёс >= порога урона)")] public int CurrencyParticipation = 150;
            [JsonProperty("Порог участия (доля урона)")] public float ParticipationPct = 0.05f;
            [JsonProperty("Бафф урона победителя (x)")] public float BuffDamage = 1.2f;
            [JsonProperty("Длительность баффа (мин)")] public float BuffMinutes = 15f;
            [JsonProperty("Время жизни мешка с лутом (сек)")] public float LootLifetimeSeconds = 3600f;
            [JsonProperty("Лут-таблица")] public List<LootEntry> Loot = new List<LootEntry>
            {
                // ресурсы (~в 3 раза скромнее быка)
                new LootEntry { ShortName = "scrap", MinAmount = 500, MaxAmount = 1000, Chance = 1f },
                new LootEntry { ShortName = "metal.refined", MinAmount = 100, MaxAmount = 250, Chance = 0.9f },
                new LootEntry { ShortName = "sulfur", MinAmount = 2000, MaxAmount = 4000, Chance = 0.9f },
                new LootEntry { ShortName = "gunpowder", MinAmount = 1000, MaxAmount = 2000, Chance = 0.7f },
                new LootEntry { ShortName = "techparts", MinAmount = 10, MaxAmount = 20, Chance = 0.6f },
                // карты
                new LootEntry { ShortName = "keycard_blue", MinAmount = 1, MaxAmount = 1, Chance = 0.15f },
                new LootEntry { ShortName = "keycard_red", MinAmount = 1, MaxAmount = 1, Chance = 0.07f },
                // топ-оружие (реже, чем у быка)
                new LootEntry { ShortName = "lmg.m249", MinAmount = 1, MaxAmount = 1, Chance = 0.05f },
                new LootEntry { ShortName = "rifle.lr300", MinAmount = 1, MaxAmount = 1, Chance = 0.08f },
                new LootEntry { ShortName = "rifle.l96", MinAmount = 1, MaxAmount = 1, Chance = 0.05f },
                new LootEntry { ShortName = "rocket.launcher", MinAmount = 1, MaxAmount = 1, Chance = 0.07f },
                new LootEntry { ShortName = "multiplegrenadelauncher", MinAmount = 1, MaxAmount = 1, Chance = 0.07f },
                new LootEntry { ShortName = "shotgun.spas12", MinAmount = 1, MaxAmount = 1, Chance = 0.08f },
                new LootEntry { ShortName = "ammo.rifle.explosive", MinAmount = 20, MaxAmount = 42, Chance = 0.25f },
                new LootEntry { ShortName = "ammo.rocket.hv", MinAmount = 1, MaxAmount = 2, Chance = 0.15f },
                // фрагмент продвинутого чертежа — гарантированно 1 шт
                new LootEntry { ShortName = "advancedblueprintfragment", MinAmount = 1, MaxAmount = 1, Chance = 1f },
                // чертежи 3-го верстака (реже, чем у быка)
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "rifle.ak", MinAmount = 1, MaxAmount = 1, Chance = 0.12f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "rifle.lr300", MinAmount = 1, MaxAmount = 1, Chance = 0.08f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "lmg.m249", MinAmount = 1, MaxAmount = 1, Chance = 0.03f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "rocket.launcher", MinAmount = 1, MaxAmount = 1, Chance = 0.05f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "ammo.rocket.basic", MinAmount = 1, MaxAmount = 1, Chance = 0.07f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "explosive.timed", MinAmount = 1, MaxAmount = 1, Chance = 0.06f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "metal.facemask", MinAmount = 1, MaxAmount = 1, Chance = 0.1f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "metal.plate.torso", MinAmount = 1, MaxAmount = 1, Chance = 0.1f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "shotgun.spas12", MinAmount = 1, MaxAmount = 1, Chance = 0.07f },
            };
        }

        private class NotifySettings
        {
            [JsonProperty("Глобальный чат")] public bool ChatGlobal = true;
            [JsonProperty("Discord webhook (пусто = выкл)")] public string DiscordWebhook = "";
        }

        private class ConfigData
        {
            [JsonProperty("Босс")] public BossSettings Boss = new BossSettings();
            [JsonProperty("Спавн")] public SpawnSettings Spawn = new SpawnSettings();
            [JsonProperty("Награды")] public RewardSettings Rewards = new RewardSettings();
            [JsonProperty("Уведомления")] public NotifySettings Notify = new NotifySettings();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _cfg = Config.ReadObject<ConfigData>(); }
            catch { _cfg = null; }
            if (_cfg == null) SaveConfig();
        }

        protected override void LoadDefaultConfig() => _cfg = new ConfigData();
        protected override void SaveConfig() => Config.WriteObject(_cfg = _cfg ?? new ConfigData(), true);

        #endregion

        #region State

        private BaseCombatEntity _boss;
        private readonly List<BaseCombatEntity> _escorts = new List<BaseCombatEntity>();
        // трупы — отдельные сущности; матчим по parentEnt + фолбэк по позиции смерти.
        // TTL 5 мин в обеих структурах; pending-кортеж хранит сущность для точечного снятия
        private readonly Dictionary<BaseEntity, Tuple<float, DateTime>> _corpseByParent = new Dictionary<BaseEntity, Tuple<float, DateTime>>();
        private readonly List<Tuple<BaseEntity, Vector3, float, DateTime>> _pendingCorpseScale = new List<Tuple<BaseEntity, Vector3, float, DateTime>>();
        private MapMarkerGenericRadius _marker;
        private DateTime _nextSpawnAt;
        private DateTime _bossSpawnedAt;
        private bool _warned;
        private bool _raging;
        private readonly Dictionary<ulong, float> _damageBy = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, DateTime> _buffUntil = new Dictionary<ulong, DateTime>();
        private string _lastKillInfo = "ещё никто";
        private Timer _timerSpawn, _timerAggro;

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission("octaboar.admin", this);
            ScheduleNext(true);
            _timerSpawn = timer.Every(30f, SpawnTick);
            Puts($"OctaBoar: следующий спавн через {(_nextSpawnAt - DateTime.UtcNow).TotalMinutes:0} мин");
        }

        private void Unload()
        {
            _timerSpawn?.Destroy();
            _timerAggro?.Destroy();
            ClearMarker();
            // босса и свиту не трогаем — пусть доживают как обычные животные
        }

        private void ScheduleNext(bool first = false)
        {
            var jitter = UnityEngine.Random.Range(-_cfg.Spawn.IntervalJitterMinutes, _cfg.Spawn.IntervalJitterMinutes);
            var mins = Math.Max(1f, _cfg.Spawn.IntervalMinutes + jitter);
            _nextSpawnAt = DateTime.UtcNow.AddMinutes(first ? Math.Min(mins, 5f) : mins);
            _warned = false;
        }

        #endregion

        #region Spawn

        private void SpawnTick()
        {
            var now = DateTime.UtcNow;
            if (BossAlive) return;
            if (!_warned && now >= _nextSpawnAt.AddMinutes(-_cfg.Spawn.WarnBeforeMinutes))
            {
                _warned = true;
                Announce($"Кабан-Босс появится через {_cfg.Spawn.WarnBeforeMinutes:0} мин возле ранчо. Готовьтесь!");
            }
            if (now < _nextSpawnAt) return;
            SpawnBoss();
        }

        private bool BossAlive => _boss != null && !_boss.IsDestroyed && _boss.IsAlive();

        private static readonly int AvoidTopo =
            (int)(TerrainTopology.Enum.Monument | TerrainTopology.Enum.Building | TerrainTopology.Enum.Ocean |
                  TerrainTopology.Enum.Lake | TerrainTopology.Enum.Cliff | TerrainTopology.Enum.Swamp |
                  TerrainTopology.Enum.River | TerrainTopology.Enum.Riverside | TerrainTopology.Enum.Beach |
                  TerrainTopology.Enum.Beachside);

        private Vector3 FindSpawnPos()
        {
            var size = ConVar.Server.worldsize;
            var half = size * 0.5f - _cfg.Spawn.MapMargin;
            for (var i = 0; i < 60; i++)
            {
                var x = UnityEngine.Random.Range(-half, half);
                var z = UnityEngine.Random.Range(-half, half);
                var p = new Vector3(x, 0, z);
                var h = TerrainMeta.HeightMap.GetHeight(p);
                if (h < 1.5f) continue; // вода/пляж
                p.y = h;
                try
                {
                    if (TerrainMeta.TopologyMap.GetTopology(p, AvoidTopo)) continue;
                }
                catch { }
                p.y += 0.5f;
                return p;
            }
            return Vector3.zero;
        }

        // точка рядом со случайным ранчо (monument name содержит ranch/barn)
        private Vector3 FindRanchSpawnPos()
        {
            try
            {
                var wanted = _cfg.Spawn.RanchNames
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Where(s => s.Length > 0)
                    .ToArray();
                var ranches = TerrainMeta.Path?.Monuments?
                    .Where(m => m != null && wanted.Any(w => (m.name ?? "").ToLowerInvariant().Contains(w)))
                    .ToList();
                if (ranches == null || ranches.Count == 0) return Vector3.zero;

                var ranch = ranches[UnityEngine.Random.Range(0, ranches.Count)];
                var center = ranch.Bounds.center;
                var edge = Mathf.Max(ranch.Bounds.extents.x, ranch.Bounds.extents.z);

                for (var i = 0; i < 30; i++)
                {
                    var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    var dist = edge + UnityEngine.Random.Range(_cfg.Spawn.RanchMinDist, _cfg.Spawn.RanchMaxDist);
                    var p = center + new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
                    var h = TerrainMeta.HeightMap.GetHeight(p);
                    if (h < 1.5f) continue;
                    p.y = h;
                    try
                    {
                        if (TerrainMeta.TopologyMap.GetTopology(p, AvoidTopo)) continue;
                    }
                    catch { }
                    p.y += 0.5f;
                    return p;
                }
            }
            catch (Exception ex) { PrintWarning($"OctaBoar: поиск ранчо упал: {ex.Message}"); }
            return Vector3.zero;
        }

        private void SpawnBoss(Vector3? at = null)
        {
            // если старый босс жив — убираем тихо (Kill() не триггерит OnEntityDeath,
            // значит без лута/наград/перепланировки): иначе получим двух боссов
            if (BossAlive)
            {
                KillEscorts();
                ClearMarker();
                _boss.Kill();
                _boss = null;
            }

            var pos = at ?? FindRanchSpawnPos();
            if (pos == Vector3.zero) pos = FindSpawnPos(); // ранчо нет на карте — обычный спавн
            if (pos == Vector3.zero)
            {
                PrintError("OctaBoar: не нашёл точку спавна за 60 попыток — перенос на следующий цикл");
                ScheduleNext();
                return;
            }

            var ent = GameManager.server.CreateEntity(_cfg.Boss.Prefab, pos, Quaternion.identity) as BaseCombatEntity;
            if (ent == null)
            {
                PrintError($"OctaBoar: префаб {_cfg.Boss.Prefab} не заспавнился");
                ScheduleNext();
                return;
            }

            // масштаб — ДО Spawn(): иначе игроки рядом получат снапшот с дефолтным размером
            ent.enableSaving = false;
            ent.networkEntityScale = true;
            ent.transform.localScale = Vector3.one * _cfg.Boss.Scale;
            ent.Spawn();

            _boss = ent;
            _bossSpawnedAt = DateTime.UtcNow;
            _raging = false;
            _damageBy.Clear();

            // характеристики
            _boss.InitializeHealth(_cfg.Boss.Health, _cfg.Boss.Health);
            _boss.startHealth = _cfg.Boss.Health;
            _boss.maxHealthOverride = _cfg.Boss.Health;
            try { _boss.SendNetworkUpdateImmediate(); } catch { }

            // скорость: NavMesh-агент Gen2 — крутим все пороги ходьбы/бега
            if (Mathf.Abs(_cfg.Boss.SpeedMultiplier - 1f) > 0.01f)
            {
                try
                {
                    var agent = _boss.GetComponentInChildren<Rust.Ai.Gen2.RustNavMeshAgent>();
                    if (agent != null)
                    {
                        var sm = _cfg.Boss.SpeedMultiplier;
                        agent.speed *= sm;
                        agent.angularSpeed *= sm;
                        agent.sneakSpeed *= sm;
                        agent.walkSpeed *= sm;
                        agent.jogSpeed *= sm;
                        agent.runSpeed *= sm;
                        agent.sprintSpeed *= sm;
                        agent.fullSprintSpeed *= sm;
                    }
                }
                catch { }
            }

            SpawnMarker(pos);
            SpawnEscorts(pos);
            StartAggroLoop();

            var grid = GridOf(pos);
            Announce($"★ Кабан-Босс появился возле ранчо! Квадрат: <color=#FFD35C>{grid}</color>. HP {_cfg.Boss.Health:0} — и его свита уже рыщет!");
            NotifyDiscord($"Кабан-Босс появился в квадрате {grid} ({pos.x:0},{pos.z:0})");
        }

        private void SpawnMarker(Vector3 pos)
        {
            ClearMarker();
            if (!_cfg.Spawn.ShowMarker) return;
            var marker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", pos) as MapMarkerGenericRadius;
            if (marker == null) return;
            marker.enableSaving = false;
            marker.Spawn();
            marker.radius = _cfg.Spawn.MarkerRadius;
            marker.color1 = new Color(0.85f, 0.1f, 0.1f);
            marker.color2 = new Color(0.4f, 0f, 0f);
            marker.alpha = 0.55f;
            marker.SendUpdate();
            marker.SendNetworkUpdate();
            _marker = marker;
        }

        private void SpawnEscorts(Vector3 center)
        {
            _escorts.Clear();
            for (var i = 0; i < _cfg.Boss.EscortCount; i++)
            {
                var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                var dist = UnityEngine.Random.Range(4f, _cfg.Boss.EscortRadius);
                var p = center + new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
                try { p.y = TerrainMeta.HeightMap.GetHeight(p) + 0.3f; } catch { p.y = center.y; }

                var pig = GameManager.server.CreateEntity(_cfg.Boss.Prefab, p, Quaternion.identity) as BaseCombatEntity;
                if (pig == null) continue;
                pig.enableSaving = false;
                pig.networkEntityScale = true;
                // масштаб поросёнка — ДО Spawn(), иначе клиенты получат обычный размер
                pig.transform.localScale = Vector3.one * _cfg.Boss.EscortScale;
                pig.Spawn();
                try { pig.SendNetworkUpdateImmediate(); } catch { }
                _escorts.Add(pig);
            }
        }

        private void KillEscorts()
        {
            // Kill() не дёргает OnEntityDeath — регистрируем масштаб туши вручную,
            // иначе труп поросёнка «вырастет» до обычного кабана
            foreach (var e in _escorts)
                if (e != null && !e.IsDestroyed)
                {
                    RegisterCorpseScale(e, _cfg.Boss.EscortScale);
                    e.Kill();
                }
            _escorts.Clear();
        }

        private void ClearMarker()
        {
            if (_marker != null && !_marker.IsDestroyed) _marker.Kill();
            _marker = null;
        }

        private static string GridOf(Vector3 pos)
        {
            try { return MapHelper.PositionToString(pos); }
            catch { return $"{pos.x:0}, {pos.z:0}"; }
        }

        #endregion

        #region Aggro & Rage

        private void StartAggroLoop()
        {
            _timerAggro?.Destroy();
            _timerAggro = timer.Every(1f, AggroTick);
        }

        private void AggroTick()
        {
            if (!BossAlive) { _timerAggro?.Destroy(); return; }

            // деспавн по старости жизни
            if ((DateTime.UtcNow - _bossSpawnedAt).TotalMinutes > _cfg.Boss.MaxLifeMinutes)
            {
                Announce("Кабан-Босс убежал в лес — никто не решился.");
                ClearMarker();
                KillEscorts();
                _boss.Kill();
                _boss = null;
                ScheduleNext();
                return;
            }

            // ярость
            if (!_raging && _boss.Health() <= _boss.MaxHealth() * _cfg.Boss.RageThreshold)
            {
                _raging = true;
                Announce("★ Кабан-Босс ВПАЛ В ЯРОСТЬ — урон вырос!");
            }

            // экстра-агрессия: каждую секунду снимаем факты страха из blackboard
            // и держим боевой статус — FSM не даём уйти в бегство
            if (_cfg.Boss.HyperAggro)
            {
                var npc = _boss as Rust.Ai.Gen2.BaseNPC2;
                if (npc != null)
                {
                    try
                    {
                        var bb = npc.GetComponent<Rust.Ai.Gen2.BlackboardComponent>();
                        if (bb != null)
                        {
                            bb.Remove("flee");
                            bb.Remove("fleeing");
                            bb.Remove("threatened");
                            bb.Remove("flee_damage_percentage");
                        }
                    }
                    catch { }

                    var radius = _raging ? _cfg.Boss.AggroRadius * 1.5f : _cfg.Boss.AggroRadius;
                    foreach (var p in BasePlayer.activePlayerList)
                    {
                        if (p == null || !p.IsConnected || p.IsDead() || p.IsFlying || p.limitNetworking) continue;
                        if (Vector3.Distance(p.transform.position, _boss.transform.position) < radius)
                        {
                            try { npc.MarkHostileFor(60f); } catch { }
                            break;
                        }
                    }
                }
            }
        }

        #endregion

        #region Damage

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            // урон ПО боссу: броня + иммунитеты + трекинг урона
            if (BossAlive && entity == _boss)
            {
                // гипер-агрессия: каждый выстрел = боевой статус + снос фактов страха
                if (_cfg.Boss.HyperAggro && info.InitiatorPlayer != null)
                {
                    try
                    {
                        var n = entity as Rust.Ai.Gen2.BaseNPC2;
                        n?.MarkHostileFor(60f);
                        var bb = n?.GetComponent<Rust.Ai.Gen2.BlackboardComponent>();
                        if (bb != null) { bb.Remove("flee"); bb.Remove("fleeing"); bb.Remove("threatened"); }
                    }
                    catch { }
                }
                ApplyBossArmor(info);
                var attacker = info.InitiatorPlayer;
                if (attacker != null && !attacker.IsNpc)
                    _damageBy[(ulong)attacker.userID] = _damageBy.TryGetValue((ulong)attacker.userID, out var cur)
                        ? cur + info.damageTypes.Total()
                        : info.damageTypes.Total();
                return;
            }

            // урон ОТ босса
            if (BossAlive && info.Initiator == _boss)
            {
                var mult = _cfg.Boss.DamageMultiplier * (_raging ? _cfg.Boss.RageDamageMultiplier : 1f);
                info.damageTypes.ScaleAll(mult);
                return;
            }

            // бафф победителя на исходящий урон игрока
            var bp = info.InitiatorPlayer;
            if (bp != null && !bp.IsNpc && _buffUntil.TryGetValue((ulong)bp.userID, out var until))
            {
                if (until > DateTime.UtcNow) info.damageTypes.ScaleAll(_cfg.Rewards.BuffDamage);
                else _buffUntil.Remove((ulong)bp.userID); // протухший бафф — выкидываем
            }
        }

        private void ApplyBossArmor(HitInfo info)
        {
            var dt = info.damageTypes;
            if (_cfg.Boss.ElementalImmune)
            {
                dt.Set(DamageType.Heat, 0f);
                dt.Set(DamageType.Bleeding, 0f);
                dt.Set(DamageType.Poison, 0f);
                dt.Set(DamageType.Cold, 0f);
                dt.Set(DamageType.Radiation, 0f);
            }
            dt.Scale(DamageType.Bullet, _cfg.Boss.BulletScale);
            dt.Scale(DamageType.Slash, _cfg.Boss.OtherScale);
            dt.Scale(DamageType.Blunt, _cfg.Boss.OtherScale);
            dt.Scale(DamageType.Stab, _cfg.Boss.OtherScale);
            dt.Scale(DamageType.Explosion, _cfg.Boss.ExplosionScale);
            dt.Scale(DamageType.AntiVehicle, _cfg.Boss.ExplosionScale);
            dt.Scale(DamageType.Fun_Water, 0f);
            dt.Scale(DamageType.Fall, 0f);
            dt.Scale(DamageType.Decay, 0f);
        }

        #endregion

        #region Death & Rewards

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;

            // поросёнок из свиты — его труп тоже должен остаться маленьким
            if (_escorts.Contains(entity))
            {
                _escorts.Remove(entity);
                RegisterCorpseScale(entity, _cfg.Boss.EscortScale);
                return;
            }

            if (entity == _boss) HandleBossDeath(entity, info);
        }

        // общий обработчик смерти босса — вызывается из хука И вручную из админ-килла
        // (BaseCombatEntity.Kill() не всегда триггерит OnEntityDeath)
        private void HandleBossDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity != _boss) return; // дабл-кол хук+вручную не нужен

            // туша босса — новая сущность с дефолтным масштабом; масштабируем в OnEntitySpawned по parentEnt
            RegisterCorpseScale(entity, _cfg.Boss.Scale);

            var killer = info?.InitiatorPlayer;
            var killerName = killer != null ? killer.displayName.ToString() : "сервер";
            _lastKillInfo = $"{killerName} — {DateTime.UtcNow:HH:mm}";

            ClearMarker();
            _timerAggro?.Destroy();
            SpawnLoot(_boss.transform.position + Vector3.up * 0.5f);
            GiveRewards(killer);

            Announce($"★ Кабан-Босс повержен! Добил: <color=#FFD35C>{killerName}</color>. Лут на месте падения.");
            NotifyDiscord($"Кабан-Босс убит. Добил: {killerName}");

            _boss = null;
            _escorts.Clear(); // поросята остаются — обычные кабанчики
            ScheduleNext();
        }

        // регистрация «на этой смерти ждём труп с масштабом scale» + ленивая чистка протухшего
        private void RegisterCorpseScale(BaseEntity dead, float scale)
        {
            var expiry = DateTime.UtcNow.AddMinutes(5);
            _corpseByParent[dead] = Tuple.Create(scale, expiry);
            _pendingCorpseScale.Add(Tuple.Create(dead, dead.transform.position, scale, expiry));

            var now = DateTime.UtcNow;
            foreach (var k in _corpseByParent.Where(kv => kv.Value.Item2 < now).Select(kv => kv.Key).ToList())
                _corpseByParent.Remove(k);
            _pendingCorpseScale.RemoveAll(p => p.Item4 < now);
        }

        // труп животного — отдельная entity: ставим масштаб до того, как он уйдёт клиентам.
        // труп спавнится внутри Die() РАНЬШЕ OnEntityDeath — матчим по parentEnt, а не по очереди
        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var bc = entity as BaseCorpse;
            if (bc == null) return;
            var src = bc.parentEnt;
            float? scale = null;
            if (src != null && _corpseByParent.TryGetValue(src, out var entry))
            {
                _corpseByParent.Remove(src);
                if (entry.Item2 > DateTime.UtcNow)
                {
                    scale = entry.Item1;
                    // снимаем позиционный фолбэк этой же смерти — туша соседнего поросёнка
                    // не должна подобрать чужой scale по координатам
                    _pendingCorpseScale.RemoveAll(p => p.Item1 == src);
                }
            }
            // фолбэк по очереди (если parentEnt пуст — по позиции из pending)
            if (scale == null && _pendingCorpseScale.Count > 0)
            {
                for (var i = _pendingCorpseScale.Count - 1; i >= 0; i--)
                {
                    var p = _pendingCorpseScale[i];
                    if (DateTime.UtcNow > p.Item4) { _pendingCorpseScale.RemoveAt(i); continue; }
                    if (Vector3.Distance(bc.transform.position, p.Item2) > 8f) continue;
                    scale = p.Item3;
                    if (p.Item1 != null) _corpseByParent.Remove(p.Item1);
                    _pendingCorpseScale.RemoveAt(i);
                    break;
                }
            }
            if (scale == null) return;
            bc.networkEntityScale = true;
            bc.transform.localScale = Vector3.one * scale.Value;
        }

        private void SpawnLoot(Vector3 pos)
        {
            // мешок чуть в стороне от туши — коллизия x2-босса перекрывает взаимодействие
            var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            var dropPos = pos + new Vector3(Mathf.Cos(ang) * 2f, 0.6f, Mathf.Sin(ang) * 2f);

            var bag = GameManager.server.CreateEntity("assets/prefabs/misc/item drop/item_drop_backpack.prefab", dropPos, Quaternion.Euler(0f, 90f, 0f)) as DroppedItemContainer;
            if (bag == null) return;
            bag.lootPanelName = "generic_resizable";
            bag.playerName = _cfg.Boss.DisplayName;
            bag.playerSteamID = 0;
            bag.onlyOwnerLoot = false;
            bag.buryLeftoverItems = false;

            // свой контейнер до Spawn() — как в Backpacks: ServerInit иначе делает свой
            bag.inventory = new ItemContainer();
            bag.inventory.ServerInitialize(null, 30);
            bag.inventory.GiveUID();
            bag.inventory.entityOwner = bag;
            bag.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);

            var rng = new System.Random();
            foreach (var e in _cfg.Rewards.Loot)
            {
                if (rng.NextDouble() > e.Chance) continue;
                var amount = rng.Next(e.MinAmount, e.MaxAmount + 1);

                Item item;
                if (!string.IsNullOrEmpty(e.BlueprintTarget))
                {
                    // чертёж: blueprintbase + blueprintTarget=итем
                    var bpTarget = ItemManager.FindItemDefinition(e.BlueprintTarget);
                    if (bpTarget == null)
                    {
                        PrintWarning($"OctaBoar: неизвестная цель чертежа: {e.BlueprintTarget}");
                        continue;
                    }
                    item = ItemManager.CreateByName("blueprintbase", amount);
                    if (item == null)
                    {
                        PrintWarning("OctaBoar: нет blueprintbase в сборке");
                        continue;
                    }
                    item.blueprintTarget = bpTarget.itemid;
                    item.blueprintAmount = 1;
                }
                else
                {
                    item = ItemManager.CreateByName(e.ShortName, amount, e.SkinId);
                    if (item == null)
                    {
                        PrintWarning($"OctaBoar: неизвестный предмет в луте: {e.ShortName}");
                        continue;
                    }
                }
                if (!item.MoveToContainer(bag.inventory))
                    item.Remove();
            }

            bag.Spawn();
            bag.ResetRemovalTime(Mathf.Max(_cfg.Rewards.LootLifetimeSeconds, bag.CalculateRemovalTime()));
        }

        private void GiveRewards(BasePlayer killer)
        {
            if (_cfg.Rewards.Currency > 0 && killer != null)
                PayCurrency(killer, _cfg.Rewards.Currency, "за добивание");

            // участники выше порога
            var total = _damageBy.Values.Sum();
            if (total <= 0) return;
            foreach (var kv in _damageBy)
            {
                if (kv.Value / total < _cfg.Rewards.ParticipationPct) continue;
                if (killer != null && kv.Key == (ulong)killer.userID) continue;
                var p = FindPlayer(kv.Key);
                if (p != null) PayCurrency(p, _cfg.Rewards.CurrencyParticipation, "за участие");
            }

            // бафф победителя
            if (killer != null && _cfg.Rewards.BuffDamage > 1f && _cfg.Rewards.BuffMinutes > 0)
            {
                _buffUntil[(ulong)killer.userID] = DateTime.UtcNow.AddMinutes(_cfg.Rewards.BuffMinutes);
                SendReply(killer, $"<color=#E8B84B>◆ OctaBoar</color> Бафф победителя: +{(_cfg.Rewards.BuffDamage - 1f) * 100:0}% урона на {_cfg.Rewards.BuffMinutes:0} мин");
            }
        }

        private static BasePlayer FindPlayer(ulong id)
        {
            foreach (var p in BasePlayer.activePlayerList)
                if ((ulong)p.userID == id) return p;
            return null;
        }

        private void PayCurrency(BasePlayer player, int amount, string reason)
        {
            // ServerRewards -> Economics, поиск живой инстанции (PluginReference залипает)
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
            {
                if (p.Name == "ServerRewards" && p.IsLoaded)
                {
                    p.Call("AddPoints", (ulong)player.userID, amount);
                    SendReply(player, $"<color=#E8B84B>◆ OctaBoar</color> +{amount} RP {reason}");
                    return;
                }
            }
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
            {
                if (p.Name == "Economics" && p.IsLoaded)
                {
                    p.Call("Deposit", player.UserIDString, (double)amount);
                    SendReply(player, $"<color=#E8B84B>◆ OctaBoar</color> +{amount} монет {reason}");
                    return;
                }
            }
        }

        #endregion

        #region Notify

        private void Announce(string msg)
        {
            if (!_cfg.Notify.ChatGlobal) return;
            Server.Broadcast($"<color=#E8B84B>◆ OctaBoar</color> {msg}");
        }

        private void NotifyDiscord(string text)
        {
            if (string.IsNullOrEmpty(_cfg.Notify.DiscordWebhook)) return;
            var body = JsonConvert.SerializeObject(new { content = $"🐗 {text}" });
            webrequest.Enqueue(_cfg.Notify.DiscordWebhook, body, (code, resp) => { }, this,
                RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" });
        }

        #endregion

        #region Commands

        [ChatCommand("boartimer")]
        private void CmdTimer(BasePlayer player)
        {
            if (BossAlive)
            {
                SendReply(player, $"<color=#E8B84B>◆ OctaBoar</color> Кабан жив! Квадрат {GridOf(_boss.transform.position)}, HP {_boss.Health():0}/{_boss.MaxHealth():0}{(_raging ? " — ЯРОСТЬ!" : "")}");
                return;
            }
            var left = _nextSpawnAt - DateTime.UtcNow;
            SendReply(player, $"<color=#E8B84B>◆ OctaBoar</color> Следующий Кабан-Босс через {(int)left.TotalHours}ч {left.Minutes}м");
        }

        [ChatCommand("boarinfo")]
        private void CmdInfo(BasePlayer player)
        {
            if (BossAlive)
            {
                var dmg = _damageBy.OrderByDescending(k => k.Value).FirstOrDefault();
                var top = dmg.Key > 0 ? (FindPlayer(dmg.Key)?.displayName.ToString() ?? "?") + $" ({dmg.Value:0} dmg)" : "—";
                SendReply(player, $"<color=#E8B84B>◆ OctaBoar</color> Жив: HP {_boss.Health():0}/{_boss.MaxHealth():0}, квадрат {GridOf(_boss.transform.position)}, топ-дамагер: {top}");
            }
            else SendReply(player, "<color=#E8B84B>◆ OctaBoar</color> Кабана на карте нет. /boartimer — время до спавна.");
        }

        [ChatCommand("boarlast")]
        private void CmdLast(BasePlayer player)
        {
            SendReply(player, $"<color=#E8B84B>◆ OctaBoar</color> Последний килл: {_lastKillInfo}");
        }

        [ConsoleCommand("octaboar.spawn")]
        private void CmdSpawn(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var p = arg.Connection?.player as BasePlayer;
            SpawnBoss(p?.transform.position + p?.transform.forward * 15f);
            Puts("OctaBoar: принудительный спавн");
        }

        [ConsoleCommand("octaboar.kill")]
        private void CmdKill(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            if (!BossAlive) { Puts("OctaBoar: босса нет"); return; }
            var b = _boss;
            b.Kill();
            if (_boss == b) HandleBossDeath(b, null); // Kill() не всегда дёргает OnEntityDeath
            Puts("OctaBoar: убит администратором");
        }

        [ConsoleCommand("octaboar.next")]
        private void CmdNext(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var mins = 30f;
            if (arg.Args != null && arg.Args.Length > 0) float.TryParse(arg.Args[0].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out mins);
            _nextSpawnAt = DateTime.UtcNow.AddMinutes(Math.Max(0.1f, mins));
            _warned = true;
            Puts($"OctaBoar: следующий спавн через {mins:0.#} мин");
        }

        #endregion
    }
}
