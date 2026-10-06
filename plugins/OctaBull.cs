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
    // OctaBull — «Бык-Босс»: элитное животное с расписанием спавна,
    // мап-маркером, трекингом урона, лутом и наградами.
    // Модель: живой Gen2-булл (Rust.Ai.Gen2.Cow, префаб bull) —
    // родной FSM умеет бодаться, агрессия форсируется через Provoke.
    [Info("OctaBull", "Octavian", "1.1.0")]
    [Description("Элитный босс-бык: расписание спавна, маркер на карте, лут, награды")]
    public class OctaBull : RustPlugin
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
            [JsonProperty("Префаб босса")] public string Prefab = "assets/rust.ai/agents/bull/bull.prefab";
            [JsonProperty("Имя босса")] public string DisplayName = "БЫК-БОСС";
            [JsonProperty("Здоровье")] public float Health = 9000f;
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
            [JsonProperty("Множитель скорости (1 = обычная)")] public float SpeedMultiplier = 2f;
            [JsonProperty("Свита: коров вокруг босса (шт)")] public int EscortCount = 5;
            [JsonProperty("Свита: радиус спавна (м)")] public float EscortRadius = 10f;
        }

        private class SpawnSettings
        {
            [JsonProperty("Интервал спавна (мин)")] public float IntervalMinutes = 180f;
            [JsonProperty("Разброс интервала ± (мин)")] public float IntervalJitterMinutes = 30f;
            [JsonProperty("Предупреждение за (мин)")] public float WarnBeforeMinutes = 5f;
            [JsonProperty("Кратер маркера на карте (0-1, доля карты)")] public float MarkerRadius = 0.03f;
            [JsonProperty("Показывать маркер на карте")] public bool ShowMarker = true;
            [JsonProperty("Держаться от края карты (м)")] public float MapMargin = 400f;
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
                // ресурсы
                new LootEntry { ShortName = "scrap", MinAmount = 1500, MaxAmount = 3000, Chance = 1f },
                new LootEntry { ShortName = "metal.refined", MinAmount = 300, MaxAmount = 700, Chance = 0.9f },
                new LootEntry { ShortName = "sulfur", MinAmount = 6000, MaxAmount = 12000, Chance = 0.9f },
                new LootEntry { ShortName = "gunpowder", MinAmount = 3000, MaxAmount = 6000, Chance = 0.7f },
                new LootEntry { ShortName = "tech.trash", MinAmount = 30, MaxAmount = 60, Chance = 0.6f },
                // карты
                new LootEntry { ShortName = "keycard_blue", MinAmount = 1, MaxAmount = 1, Chance = 0.35f },
                new LootEntry { ShortName = "keycard_red", MinAmount = 1, MaxAmount = 1, Chance = 0.2f },
                // топ-оружие
                new LootEntry { ShortName = "lmg.m249", MinAmount = 1, MaxAmount = 1, Chance = 0.15f },
                new LootEntry { ShortName = "rifle.lr300", MinAmount = 1, MaxAmount = 1, Chance = 0.25f },
                new LootEntry { ShortName = "rifle.l96", MinAmount = 1, MaxAmount = 1, Chance = 0.15f },
                new LootEntry { ShortName = "rocket.launcher", MinAmount = 1, MaxAmount = 1, Chance = 0.2f },
                new LootEntry { ShortName = "multiplegrenadelauncher", MinAmount = 1, MaxAmount = 1, Chance = 0.2f },
                new LootEntry { ShortName = "shotgun.spas12", MinAmount = 1, MaxAmount = 1, Chance = 0.25f },
                new LootEntry { ShortName = "ammo.rifle.expl", MinAmount = 60, MaxAmount = 128, Chance = 0.4f },
                new LootEntry { ShortName = "ammo.rocket.hv", MinAmount = 2, MaxAmount = 4, Chance = 0.3f },
                // фрагменты продвинутого чертежа — гарантированно 3 шт
                new LootEntry { ShortName = "advancedblueprintfragment", MinAmount = 3, MaxAmount = 3, Chance = 1f },
                // чертежи 3-го верстака
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "rifle.ak", MinAmount = 1, MaxAmount = 1, Chance = 0.35f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "rifle.lr300", MinAmount = 1, MaxAmount = 1, Chance = 0.25f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "lmg.m249", MinAmount = 1, MaxAmount = 1, Chance = 0.08f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "rocket.launcher", MinAmount = 1, MaxAmount = 1, Chance = 0.15f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "ammo.rocket.basic", MinAmount = 1, MaxAmount = 1, Chance = 0.2f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "explosive.timed", MinAmount = 1, MaxAmount = 1, Chance = 0.18f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "metal.facemask", MinAmount = 1, MaxAmount = 1, Chance = 0.3f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "metal.chestplate", MinAmount = 1, MaxAmount = 1, Chance = 0.3f },
                new LootEntry { ShortName = "blueprintbase", BlueprintTarget = "shotgun.spas12", MinAmount = 1, MaxAmount = 1, Chance = 0.2f },
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
        private Rust.Ai.Gen2.LivestockAnimal _bossAi;
        private readonly List<BaseCombatEntity> _escorts = new List<BaseCombatEntity>();
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
            permission.RegisterPermission("octabull.admin", this);
            ScheduleNext(true);
            _timerSpawn = timer.Every(30f, SpawnTick);
            Puts($"OctaBull: следующий спавн через {(_nextSpawnAt - DateTime.UtcNow).TotalMinutes:0} мин");
        }

        private void Unload()
        {
            _timerSpawn?.Destroy();
            _timerAggro?.Destroy();
            ClearMarker();
            // босса не трогаем — пусть доживает как обычный бык
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
                Announce($"Бык-Босс появится через {_cfg.Spawn.WarnBeforeMinutes:0} мин. Готовьтесь — это не кабанчик с ранчо!");
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

        private void SpawnBoss(Vector3? at = null)
        {
            var pos = at ?? FindSpawnPos();
            if (pos == Vector3.zero)
            {
                PrintError("OctaBull: не нашёл точку спавна за 60 попыток — перенос на следующий цикл");
                ScheduleNext();
                return;
            }

            var ent = GameManager.server.CreateEntity(_cfg.Boss.Prefab, pos, Quaternion.identity) as BaseCombatEntity;
            if (ent == null)
            {
                PrintError($"OctaBull: префаб {_cfg.Boss.Prefab} не заспавнился");
                ScheduleNext();
                return;
            }

            ent.enableSaving = false;
            ent.networkEntityScale = true;
            ent.Spawn();

            _boss = ent;
            _bossAi = ent as Rust.Ai.Gen2.LivestockAnimal;
            _bossSpawnedAt = DateTime.UtcNow;
            _raging = false;
            _damageBy.Clear();

            // характеристики
            _boss.InitializeHealth(_cfg.Boss.Health, _cfg.Boss.Health);
            _boss.startHealth = _cfg.Boss.Health;
            _boss.maxHealthOverride = _cfg.Boss.Health;
            try
            {
                _boss.transform.localScale = Vector3.one * _cfg.Boss.Scale;
                _boss.SendNetworkUpdateImmediate();
            }
            catch { }

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

            if (_bossAi != null)
            {
                _bossAi.IsMale = true;
                try { _bossAi.AnimalName = _cfg.Boss.DisplayName; } catch { }
            }

            SpawnMarker(pos);
            SpawnEscorts(pos);
            StartAggroLoop();

            var grid = GridOf(pos);
            Announce($"★ Бык-Босс появился! Квадрат: <color=#FFD35C>{grid}</color>. HP {_cfg.Boss.Health:0} — берите отряд!");
            NotifyDiscord($"Бык-Босс появился в квадрате {grid} ({pos.x:0},{pos.z:0})");
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

                var cow = GameManager.server.CreateEntity(_cfg.Boss.Prefab, p, Quaternion.identity) as BaseCombatEntity;
                if (cow == null) continue;
                cow.enableSaving = false;
                cow.Spawn();
                // самка — корова, не бык
                var ai = cow as Rust.Ai.Gen2.LivestockAnimal;
                if (ai != null) { try { ai.IsMale = false; } catch { } }
                _escorts.Add(cow);
            }
        }

        private void KillEscorts()
        {
            foreach (var e in _escorts)
                if (e != null && !e.IsDestroyed) e.Kill();
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
            _timerAggro = timer.Every(4f, AggroTick);
        }

        private void AggroTick()
        {
            if (!BossAlive) { _timerAggro?.Destroy(); return; }

            // деспавн по старости жизни
            if ((DateTime.UtcNow - _bossSpawnedAt).TotalMinutes > _cfg.Boss.MaxLifeMinutes)
            {
                Announce("Бык-Босс ушёл в закат — никто не решился.");
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
                Announce("★ Бык-Босс ВПАЛ В ЯРОСТЬ — урон вырос!");
            }

            if (_bossAi == null) return;

            // форс-агро: ближайший игрок в радиусе становится целью
            var radius = _raging ? _cfg.Boss.AggroRadius * 1.5f : _cfg.Boss.AggroRadius;
            BasePlayer closest = null;
            var best = float.MaxValue;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.IsConnected || p.IsDead() || p.IsFlying || p.limitNetworking) continue;
                var d = Vector3.Distance(p.transform.position, _boss.transform.position);
                if (d < radius && d < best) { best = d; closest = p; }
            }
            if (closest == null) return;
            try { _bossAi.Provoke(closest); } catch { }
            try { _bossAi.RememberAggressor(closest); } catch { }
        }

        #endregion

        #region Damage

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            // урон ПО боссу: броня + иммунитеты + трекинг урона
            if (BossAlive && entity == _boss)
            {
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
            if (bp != null && !bp.IsNpc &&
                _buffUntil.TryGetValue((ulong)bp.userID, out var until) && until > DateTime.UtcNow)
            {
                info.damageTypes.ScaleAll(_cfg.Rewards.BuffDamage);
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
            if (entity == null || !BossAlive && _boss == null) return;
            if (entity != _boss) return;

            var killer = info?.InitiatorPlayer;
            var killerName = killer != null ? killer.displayName.ToString() : "сервер";
            _lastKillInfo = $"{killerName} — {DateTime.UtcNow:HH:mm}";

            ClearMarker();
            _timerAggro?.Destroy();
            SpawnLoot(_boss.transform.position + Vector3.up * 0.5f);
            GiveRewards(killer);

            Announce($"★ Бык-Босс повержен! Добил: <color=#FFD35C>{killerName}</color>. Лут на месте падения.");
            NotifyDiscord($"Бык-Босс убит. Добил: {killerName}");

            _boss = null;
            _bossAi = null;
            _escorts.Clear(); // свита остаётся пастись — обычные коровы
            ScheduleNext();
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
                        PrintWarning($"OctaBull: неизвестная цель чертежа: {e.BlueprintTarget}");
                        continue;
                    }
                    item = ItemManager.CreateByName("blueprintbase", amount);
                    if (item == null)
                    {
                        PrintWarning("OctaBull: нет blueprintbase в сборке");
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
                        PrintWarning($"OctaBull: неизвестный предмет в луте: {e.ShortName}");
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
                SendReply(killer, $"<color=#E8B84B>◆ OctaBull</color> Бафф победителя: +{(_cfg.Rewards.BuffDamage - 1f) * 100:0}% урона на {_cfg.Rewards.BuffMinutes:0} мин");
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
                    SendReply(player, $"<color=#E8B84B>◆ OctaBull</color> +{amount} RP {reason}");
                    return;
                }
            }
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
            {
                if (p.Name == "Economics" && p.IsLoaded)
                {
                    p.Call("Deposit", player.UserIDString, (double)amount);
                    SendReply(player, $"<color=#E8B84B>◆ OctaBull</color> +{amount} монет {reason}");
                    return;
                }
            }
        }

        #endregion

        #region Notify

        private void Announce(string msg)
        {
            if (!_cfg.Notify.ChatGlobal) return;
            Server.Broadcast($"<color=#E8B84B>◆ OctaBull</color> {msg}");
        }

        private void NotifyDiscord(string text)
        {
            if (string.IsNullOrEmpty(_cfg.Notify.DiscordWebhook)) return;
            var body = JsonConvert.SerializeObject(new { content = $"🐂 {text}" });
            webrequest.Enqueue(_cfg.Notify.DiscordWebhook, body, (code, resp) => { }, this,
                RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" });
        }

        #endregion

        #region Commands

        [ChatCommand("bosstimer")]
        private void CmdTimer(BasePlayer player)
        {
            if (BossAlive)
            {
                SendReply(player, $"<color=#E8B84B>◆ OctaBull</color> Бык жив! Квадрат {GridOf(_boss.transform.position)}, HP {_boss.Health():0}/{_boss.MaxHealth():0}{(_raging ? " — ЯРОСТЬ!" : "")}");
                return;
            }
            var left = _nextSpawnAt - DateTime.UtcNow;
            SendReply(player, $"<color=#E8B84B>◆ OctaBull</color> Следующий Бык-Босс через {(int)left.TotalHours}ч {left.Minutes}м");
        }

        [ChatCommand("bossinfo")]
        private void CmdInfo(BasePlayer player)
        {
            if (BossAlive)
            {
                var dmg = _damageBy.OrderByDescending(k => k.Value).FirstOrDefault();
                var top = dmg.Key > 0 ? (FindPlayer(dmg.Key)?.displayName.ToString() ?? "?") + $" ({dmg.Value:0} dmg)" : "—";
                SendReply(player, $"<color=#E8B84B>◆ OctaBull</color> Жив: HP {_boss.Health():0}/{_boss.MaxHealth():0}, квадрат {GridOf(_boss.transform.position)}, топ-дамагер: {top}");
            }
            else SendReply(player, "<color=#E8B84B>◆ OctaBull</color> Быка на карте нет. /bosstimer — время до спавна.");
        }

        [ChatCommand("bosslast")]
        private void CmdLast(BasePlayer player)
        {
            SendReply(player, $"<color=#E8B84B>◆ OctaBull</color> Последний килл: {_lastKillInfo}");
        }

        [ConsoleCommand("octabull.spawn")]
        private void CmdSpawn(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var p = arg.Connection?.player as BasePlayer;
            SpawnBoss(p?.transform.position + p?.transform.forward * 15f);
            Puts("OctaBull: принудительный спавн");
        }

        [ConsoleCommand("octabull.kill")]
        private void CmdKill(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            if (!BossAlive) { Puts("OctaBull: босса нет"); return; }
            _boss.Kill();
            Puts("OctaBull: убит администратором");
        }

        [ConsoleCommand("octabull.next")]
        private void CmdNext(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var mins = 30f;
            if (arg.Args != null && arg.Args.Length > 0) float.TryParse(arg.Args[0].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out mins);
            _nextSpawnAt = DateTime.UtcNow.AddMinutes(Math.Max(0.1f, mins));
            _warned = true;
            Puts($"OctaBull: следующий спавн через {mins:0.#} мин");
        }

        #endregion
    }
}
