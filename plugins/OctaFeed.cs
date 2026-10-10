using System.Linq;
using Oxide.Game.Rust.Cui;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using System.Collections.Generic;
using Facepunch;
using UnityEngine;
using Rust.Ai.Gen2;
using System;

namespace Oxide.Plugins
{
    // OctaFeed v2 — киллфид Octavian: кто кого убил, оружие, дистанция.
    // Геометрия — долями экрана (офсеты конфига трактуются как виртуальные
    // пиксели сетки 1280x720 и конвертируются в доли), поэтому лента выглядит
    // одинаково на любом разрешении и любом соотношении сторон.
    // Строки — список с индивидуальным TTL; перерисовка целиком (это дёшево:
    // до N строк по несколько элементов), вместо кольцевого буфера и утаскивания
    // элементов за край экрана.
    [Info("OctaFeed", "Octavian", "2.1.0")]
    [Description("Киллфид: кто кого убил, оружие, дистанция. Поддержка боссов OctaBoss")]
    public class OctaFeed : RustPlugin
    {
        [PluginReference] private Plugin OctaBoss;

        #region [Данные]

        private static OctaFeed _i;
        private OctaFeedConfig Cfg;

        // Персистентные настройки игроков (data/OctaFeed/PlayersData.json)
        public class PlayerData
        {
            public double LastConnectTime = 0; // последняя активность — ключ автоочистки (7 дней)
            public double LastToggleTime = 0;  // кулдаун переключения «скрывать ленту»
            public bool HideMessages = false;
            public bool HideMyName = false;
        }
        internal Dictionary<ulong, PlayerData> PlayersData = new Dictionary<ulong, PlayerData>();

        // Одна строка ленты. TTL считается от момента добавления.
        private class FeedRow
        {
            public string Message;
            public string Distance;
            public int WeaponIcon;
            public List<int> ModIcons;
            public float ExpiresAt; // realtimeSinceStartup
        }
        private readonly List<FeedRow> _rows = new List<FeedRow>(8);

        // Снимок последнего удара по игроку — для атрибуции «смерти от окружения»
        // (утонул/замёрз/упал/истёк кровью вскоре после выстрела — засчитываем киллеру).
        // Храним снимок, а не HitInfo: объект удара и сущности могут мутировать/умирать.
        private class RecentAttack
        {
            public bool FromPlayer;      // атаковал реальный игрок — его не перезаписывает NPC-урон
            public bool Allowed;         // гейт конфига для этого типа атакующего
            public string AttackerName;  // готовое rich-имя на момент удара
            public Vector3 AttackerPos;
            public DamageType MajType;
            public int WeaponIcon;
            public List<int> ModIcons;
            public float Time;
        }
        private readonly Dictionary<ulong, RecentAttack> _lastAttacks = new Dictionary<ulong, RecentAttack>();
        private float AssistWindowSec =>
            Cfg?.CoreConfig != null && Cfg.CoreConfig.AssistWindowSec > 0 ? Cfg.CoreConfig.AssistWindowSec : 8f;

        // Причина последнего экологического урона без атакующего (падение, холод...).
        // Нужна для формулировки смерти после нокдауна: финальный удар там обычно
        // Generic/Bleeding, а реальная причина — исходный урон. Окно шире ассиста:
        // в нокнутом состоянии можно лежать до минуты.
        private class EnvHit
        {
            public DamageType Type;
            public float Time;
        }
        private readonly Dictionary<ulong, EnvHit> _envHits = new Dictionary<ulong, EnvHit>();
        private const float EnvCauseWindow = 60f;

        // Типы урона, при которых смерть считается «от окружения» — кандидат на ассист.
        private static readonly HashSet<DamageType> EnvDamage = new HashSet<DamageType>
        {
            DamageType.Bleeding, DamageType.Cold, DamageType.ColdExposure, DamageType.Drowned,
            DamageType.Fall, DamageType.Hunger, DamageType.Thirst, DamageType.Poison,
            DamageType.Radiation, DamageType.RadiationExposure, DamageType.Suicide,
            DamageType.Fun_Water, DamageType.Generic,
        };

        // Дедупликация смертей: OnEntityDeath и OnPatrolHelicopterKill могут
        // сработать на одну смерть. net.ID -> время обработанной смерти.
        private readonly Dictionary<ulong, float> _handledDeaths = new Dictionary<ulong, float>();

        // Возвращает false, если смерть этой сущности уже давала строку за последние 3 сек
        private bool MarkDeath(BaseCombatEntity e)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            ulong id = e.net.ID.Value;
            if (_handledDeaths.TryGetValue(id, out float t) && now - t < 3f) return false;
            _handledDeaths[id] = now;
            return true;
        }

        // Кому сейчас открыт виджет настроек (временное состояние, не сохраняем)
        private readonly HashSet<ulong> _settingsOpen = new HashSet<ulong>();

        // prefabID/ShortPrefabName -> itemid: иконка оружия/объекта в строке
        private readonly Dictionary<uint, int> _prefab2Item = new Dictionary<uint, int>();
        private readonly Dictionary<string, int> _prefabName2Item = new Dictionary<string, int>
        {
            ["40mm_grenade_he"] = -1123473824,
            ["rocket_basic"] = 442886268,
            ["rocket_admin"] = 442886268,
            ["rocket_hv"] = 442886268,
            ["rocket_fire"] = 442886268,
        };

        private const int IconHeadshot = 996293980; // иконка «хедшот»
        private const int IconDefaultWeapon = 1545779598; // иконка оружия по умолчанию
        private const string PanelName = "DM";      // лента
        private const string GearName = "DMUI";     // шестерёнка настроек
        private const string WidgetName = "DMUISettings"; // всплывающий виджет

        // Виртуальные пиксели сетки, в которых заданы офсеты конфига.
        private const float RefW = 1280f, RefH = 720f;

        #endregion

        #region [Справочники имён и сообщений]

        internal Dictionary<string, string> Names = new Dictionary<string, string>()
        {
            ["npcplayer"] = "NPC",
            ["guntrap.deployed"] = "Guntrap",
            ["landmine"] = "Landmine",
            ["beartrap"] = "Bear trap",
            ["flameturret.deployed"] = "Flame turret",
            ["flameturret_fireball"] = "Flame turret",
            ["autoturret_deployed"] = "Turret",
            ["sentry.scientist.static"] = "Turret NPC",
            ["sentry.bandit.static"] = "Turret NPC",
            ["spikes.floor"] = "Spikes",
            ["spikes_static"] = "Spikes",
            ["teslacoil.deployed"] = "Tesla",
            ["barricade.wood"] = "Barricade",
            ["barricade.woodwire"] = "Barricade",
            ["barricade.metal"] = "Barricade",
            ["bradleyapc"] = "BradleyAPC",
            ["gates.external.high.wood"] = "Gates",
            ["gates.external.high.stone"] = "Gates",
            ["icewall"] = "Ice Wall",
            ["wall.external.high.ice"] = "Ice wall",
            ["wall.external.high.stone"] = "Wall",
            ["wall.external.high.wood"] = "Wall",
            ["campfire"] = "Campfire",
            ["skull_fire_pit"] = "Campfire",
            ["lock.code"] = "Codelock",
            ["boar"] = "Boar",
            ["bear"] = "Bear",
            ["polarbear"] = "Polar Bear",
            ["wolf"] = "Wolf",
            ["stag"] = "Stag",
            ["chicken"] = "Chicken",
            ["horse"] = "Horse",
            ["cow"] = "Cow",
            ["bull"] = "Bull",
            ["calf"] = "Calf",
            ["sheep"] = "Sheep",
            ["lamb"] = "Lamb",
            ["simpleshark"] = "Shark",
            ["crocodile"] = "Crocodile",
            ["tiger"] = "Tiger",
            ["panther"] = "Panther",
            ["minicopter.entity"] = "Minicopter",
            ["scraptransporthelicopter"] = "Transport helicopter",
            ["patrolhelicopter"] = "Patrol helicopter",
            ["napalm"] = "Napalm",
            ["fireball_small"] = "Fire",
            ["fireball_small_shotgun"] = "Fire",
            ["fireball_small_arrow"] = "Fire",
            ["sam_site_turret_deployed"] = "SAM",
            ["cactus-1"] = "Cactus",
            ["cactus-2"] = "Cactus",
            ["cactus-3"] = "Cactus",
            ["cactus-4"] = "Cactus",
            ["cactus-5"] = "Cactus",
            ["cactus-6"] = "Cactus",
            ["cactus-7"] = "Cactus",
            ["hotairballoon"] = "Hot Air Balloon",
            ["cave_lift_trigger"] = "Lift",
            ["wolf2"] = "Wolf",
            ["door_barricade_b"] = "Barricade",
            ["modular_car_1mod_storage"] = "Car",
            ["modular_car_1mod_trade"] = "Car",
            ["modular_car_2mod_fuel_tank"] = "Car",
            ["modular_car_camper_storage"] = "Car",
            ["modular_car_fuel_storage"] = "Car",
            ["modular_car_i4_engine_storage"] = "Car",
            ["modular_car_sleepingbag_campervan"] = "Car",
            ["modular_car_v8_engine_storage"] = "Car",
            ["sam_static"] = "SAM",
            ["sentry.bandit.static"] = "Bandit",
            ["sentry.scientist.static"] = "Scientist",
            ["supply_drop"] = "Supply Drop",
            ["npc_bandit_guard"] = "Bandit Guard",
            ["scientistnpc_arena"] = "Scientist",
            ["scientistnpc_bradley"] = "Scientist",
            ["scientistnpc_bradley_heavy"] = "Heavy Scientist",
            ["scientistnpc_cargo"] = "Scientist",
            ["scientistnpc_cargo_turret_any"] = "Scientist",
            ["scientistnpc_cargo_turret_lr300"] = "Scientist",
            ["scientistnpc_ch47_gunner"] = "Scientist",
            ["scientistnpc_excavator"] = "Scientist",
            ["scientistnpc_full_any"] = "Scientist",
            ["scientistnpc_full_lr300"] = "Scientist",
            ["scientistnpc_full_mp5"] = "Scientist",
            ["scientistnpc_full_pistol"] = "Scientist",
            ["scientistnpc_full_shotgun"] = "Scientist",
            ["scientistnpc_heavy"] = "Heavy Scientist",
            ["scientistnpc_junkpile_pistol"] = "Scientist",
            ["scientistnpc_oilrig"] = "Scientist",
            ["scientistnpc_patrol"] = "Scientist",
            ["scientistnpc_peacekeeper"] = "Scientist",
            ["scientistnpc_roam"] = "Scientist",
            ["scientistnpc_roam_nvg_variant"] = "Scientist",
            ["scientistnpc_roamethetered"] = "Scientist",
            ["npc_tunneldweller"] = "Tunnel Dweller",
            ["npc_tunneldwellerspawned"] = "Tunnel Dweller",
            ["npc_underwaterdweller"] = "Underwater Dweller",
            ["npcplayertest"] = "Player Test",
        };

        internal Dictionary<string, List<string>> Messages = new Dictionary<string, List<string>>()
        {
            ["AntiVehicle"] = new List<string>() { "{1} убил {0}" },
            ["Arrow"] = new List<string>() { "{1} убил {0}" },
            ["Bite"] = new List<string>() { "{1} убил {0}" },
            ["Bleeding"] = new List<string>() { "{0} умер от кровотечения" },
            ["Blunt"] = new List<string>() { "{1} убил {0}" },
            ["Bullet"] = new List<string>() { "{1} убил {0}" },
            ["Cold"] = new List<string>() { "{0} замерз" },
            ["ColdExposure"] = new List<string>() { "{0} замерз" },
            ["Collision"] = new List<string>() { "{1} убил {0}" },
            ["Decay"] = new List<string>() { "{1} убил {0}" },
            ["Drowned"] = new List<string>() { "{0} утонул" },
            ["ElectricShock"] = new List<string>() { "{0} заискрился" },
            ["Explosion"] = new List<string>() { "{1} убил {0}" },
            ["Fall"] = new List<string>() { "{0} упал с высоты" },
            ["Fun_Water"] = new List<string>() { "{0} погиб" },
            ["Generic"] = new List<string>() { "{0} погиб" },
            ["Heat"] = new List<string>() { "{1} убил {0}" },
            ["Hunger"] = new List<string>() { "{0} умер от голода" },
            ["Poison"] = new List<string>() { "{0} умер от отравления" },
            ["Radiation"] = new List<string>() { "{0} умер от радиации" },
            ["RadiationExposure"] = new List<string>() { "{0} умер от радиации" },
            ["Slash"] = new List<string>() { "{1} убил {0}" },
            ["Stab"] = new List<string>() { "{1} убил {0}" },
            ["Suicide"] = new List<string>() { "{0} убил себя" },
            ["Thirst"] = new List<string>() { "{0} умер от жажды" },
        };

        #endregion

        #region [Жизненный цикл]

        private void Init()
        {
            Cfg = Config.ReadObject<OctaFeedConfig>();
            if (Cfg?.CoreConfig == null || Cfg.UIConfig == null || Cfg.CoreConfig.Names == null || Cfg.CoreConfig.Names.Count == 0
                || Cfg.CoreConfig.Messages == null || Cfg.CoreConfig.Messages.Count == 0 || Cfg.UIConfig.Colors == null)
            {
                Cfg = GetDefaultConfig();
            }
            Config.WriteObject(Cfg, true);
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(GetDefaultConfig(), true);
        }

        private void OnServerInitialized()
        {
            _i = this;

            PlayersData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerData>>("OctaFeed\\PlayersData")
                          ?? new Dictionary<ulong, PlayerData>();

            // вычищаем записи старше недели
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var uid in PlayersData.Where(kv => kv.Value.LastConnectTime + 604800_000 < nowMs).Select(kv => kv.Key).ToList())
                PlayersData.Remove(uid);

            foreach (var groupColor in Cfg.UIConfig.Colors)
                permission.RegisterPermission(groupColor.Key, this);

            BuildItemIconMaps();

            foreach (var player in BasePlayer.activePlayerList)
                DrawAll(player);

            // раз в секунду снимаем истёкшие строки
            timer.Every(1f, ExpireRows);
        }

        private void OnServerSave() => SavePlayers();

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, PanelName);
                CuiHelper.DestroyUi(player, GearName);
                CuiHelper.DestroyUi(player, WidgetName);
            }
            SavePlayers();
            _rows.Clear();
            _i = null;
        }

        private void SavePlayers() => Interface.Oxide.DataFileSystem.WriteObject("OctaFeed\\PlayersData", PlayersData);

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            if (PlayersData.TryGetValue(player.userID, out var pd))
                pd.LastConnectTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            DrawAll(player);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player != null) _settingsOpen.Remove(player.userID);
        }

        // Страховка: клиент может сбрасывать HUD при респавне — перерисуем
        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player != null) DrawAll(player);
        }

        // Карта prefab -> itemid для иконок. Собирается один раз на старте.
        private void BuildItemIconMaps()
        {
            foreach (ItemDefinition def in ItemManager.GetItemDefinitions())
            {
                Item cacheItem = null;
                try
                {
                    cacheItem = ItemManager.CreateByName(def.shortname, 1, 0);
                    BaseEntity entity = cacheItem?.GetHeldEntity();
                    if (entity != null)
                    {
                        _prefab2Item[entity.prefabID] = def.itemid;
                        _prefabName2Item[entity.ShortPrefabName] = def.itemid;
                        _prefabName2Item[entity.ShortPrefabName.Replace(".entity", ".deployed")] = def.itemid;
                        _prefabName2Item[entity.ShortPrefabName + ".deployed"] = def.itemid;
                    }

                    if (def.HasComponent<ItemModDeployable>())
                    {
                        string deployablePrefab = def.GetComponent<ItemModDeployable>()?.entityPrefab?.resourcePath;
                        if (!string.IsNullOrEmpty(deployablePrefab))
                        {
                            string shortPrefabName = GameManager.server.FindPrefab(deployablePrefab)?.GetComponent<BaseEntity>()?.ShortPrefabName;
                            if (!string.IsNullOrEmpty(shortPrefabName))
                                _prefabName2Item[shortPrefabName] = def.itemid;
                        }
                    }
                }
                catch { }
                finally { cacheItem?.Remove(); }
            }
        }

        #endregion

        #region [Роутинг смертей]

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!(entity is BasePlayer player) || !player.IsNonNpcPlayer()) return;
            if (info?.damageTypes == null || info.damageTypes.Total() <= 0f) return;

            // Запоминаем причину экологического урона: у финального удара смерти
            // после нокдауна тип будет Bleeding/Generic — без этой записи вышло бы
            // «погиб» вместо «упал с высоты». В нокнутом состоянии не перезаписываем:
            // тики кровотечения там — следствие исходной причины, а не новая.
            if ((info.Initiator == null || info.Initiator == entity) &&
                EnvDamage.Contains(info.damageTypes.GetMajorityDamageType()) && !player.IsWounded())
            {
                _envHits[player.userID] = new EnvHit
                {
                    Type = info.damageTypes.GetMajorityDamageType(),
                    Time = UnityEngine.Time.realtimeSinceStartup,
                };
            }

            if (info.Initiator == null || info.Initiator == entity) return;
            var attacker = info.Initiator as BaseCombatEntity;
            if (attacker == null || attacker.net == null) return;

            bool fromPlayer = attacker is BasePlayer ap && ap.IsNonNpcPlayer();
            // Урон от NPC/объекта не должен перезаписывать свежую атаку реального игрока
            if (!fromPlayer && _lastAttacks.TryGetValue(player.userID, out var prev)
                && prev.FromPlayer && UnityEngine.Time.realtimeSinceStartup - prev.Time <= AssistWindowSec)
                return;

            var atk = new RecentAttack
            {
                FromPlayer = fromPlayer,
                Allowed = GateForPlayerDeath(attacker),
                AttackerName = NameOf(attacker),
                AttackerPos = attacker.transform.position,
                MajType = info.damageTypes.GetMajorityDamageType(),
                ModIcons = new List<int>(6),
                Time = UnityEngine.Time.realtimeSinceStartup,
            };
            // Иконки — снимок на момент удара: у игрока это оружие из HitInfo,
            // у объекта (турель/ловушка) — сама сущность, если мапится на предмет.
            var iconSrc = !fromPlayer && ItemDefOf(attacker) != null ? attacker : null;
            atk.WeaponIcon = ResolveIcons(info, iconSrc, atk.ModIcons);
            _lastAttacks[player.userID] = atk;
        }

        private void OnEntityDeath(BaseCombatEntity victim, HitInfo info)
        {
            if (victim == null || victim.net == null)
                return;

            FeedRow row = null;

            // Атрибуция «смерти от окружения»: утопление/кровотечение/холод/падение/голод,
            // но в окне ассиста был реальный урон — виновник тот, кто наносил урон.
            if (victim is BasePlayer vp && vp.IsNonNpcPlayer())
            {
                bool isEnvOrSelf = info == null || info.Initiator == null || info.Initiator == victim;
                if (!isEnvOrSelf && info.damageTypes != null &&
                    EnvDamage.Contains(info.damageTypes.GetMajorityDamageType()))
                    isEnvOrSelf = true;

                if (isEnvOrSelf && _lastAttacks.TryGetValue(vp.userID, out var last) &&
                    UnityEngine.Time.realtimeSinceStartup - last.Time <= AssistWindowSec)
                    row = BuildRowFromAssist(vp, last); // null по гейту → упадём в обычную env-ветку
                _lastAttacks.Remove(vp.userID);

                if (row != null)
                {
                    if (MarkDeath(vp)) PushRow(row);
                    return;
                }
            }

            if (info?.damageTypes != null && info.Initiator is BaseCombatEntity initiator &&
                initiator.net != null && initiator != victim)
            {
                if (victim is BasePlayer victimPlayer && victimPlayer.IsNonNpcPlayer())
                {
                    // убили игрока
                    row = BuildRow(victimPlayer, initiator, info, GateForPlayerDeath(initiator));
                }
                else if (initiator is BasePlayer killer && killer.IsNonNpcPlayer())
                {
                    // игрок убил не-игрока: NPC, вертолёт, танк, боссы OctaBoss, животных
                    row = BuildRow(victim, killer, info, GateForVictim(victim));
                }
            }

            // чистая смерть без инициатора (суицид, падение, голод)
            if (row == null && victim is BasePlayer solo && solo.IsNonNpcPlayer())
            {
                // Если недавно был экологический урон — берём его тип, иначе тип
                // самого финального удара: «упал с высоты» вместо безликого «погиб».
                DamageType dtype = info?.damageTypes?.GetMajorityDamageType() ?? DamageType.Generic;
                if (_envHits.TryGetValue(solo.userID, out var eh) &&
                    UnityEngine.Time.realtimeSinceStartup - eh.Time <= EnvCauseWindow)
                    dtype = eh.Type;
                _envHits.Remove(solo.userID);
                row = BuildRow(solo, null, info, Cfg.CoreConfig.PlayerKilledByEnv, dtype);
            }

            if (row != null && MarkDeath(victim)) PushRow(row);
        }

        // Строка из снимка последнего удара: игрок умер от окружения,
        // но килл засчитываем последнему атаковавшему в окне ассиста.
        private FeedRow BuildRowFromAssist(BasePlayer victim, RecentAttack atk)
        {
            if (!atk.Allowed) return null;
            var tpl = GetDeathMessage(atk.MajType);
            if (string.IsNullOrEmpty(tpl)) return null;
            return new FeedRow
            {
                Message = string.Format(tpl, NameOf(victim), atk.AttackerName),
                Distance = Vector3.Distance(victim.transform.position, atk.AttackerPos).ToString("F1") + "m",
                WeaponIcon = atk.WeaponIcon,
                ModIcons = atk.ModIcons ?? new List<int>(),
            };
        }

        // Каким флагом конфига гейтится смерть игрока от этого инициатора
        private bool GateForPlayerDeath(BaseCombatEntity initiator)
        {
            if (initiator is BasePlayer p)
                return p.IsNpcPlayer() ? Cfg.CoreConfig.PlayerKilledByNPC : true; // PvP — всегда
            if (initiator is PatrolHelicopter || initiator is BradleyAPC)
                return Cfg.CoreConfig.PlayerKilledByAnimal;
            return Cfg.CoreConfig.PlayerKilledByAnimal; // животное/босс/турель/ловушка
        }

        // Каким флагом гейтится смерть этой жертвы от игрока
        private bool GateForVictim(BaseCombatEntity victim)
        {
            if (victim is NPCPlayer) return Cfg.CoreConfig.NPC;
            if (victim is PatrolHelicopter) return Cfg.CoreConfig.Helicopter;
            if (victim is BradleyAPC) return Cfg.CoreConfig.Bradley;
            // боссы и свита OctaBoss — всегда
            try { if (!string.IsNullOrEmpty(BossNameOf(victim))) return true; } catch { }
            // обычные животные — по флагу [3.1]
            if (victim is BaseAnimalNPC || victim is LivestockAnimal) return Cfg.CoreConfig.Animal;
            return false;
        }

        private void OnPatrolHelicopterKill(PatrolHelicopter victim, HitInfo info)
        {
            if (victim == null || info?.damageTypes == null || info.InitiatorPlayer == null) return;
            var row = BuildRow(victim, info.InitiatorPlayer, info, Cfg.CoreConfig.Helicopter);
            if (row != null && MarkDeath(victim)) PushRow(row);
        }

        #endregion

        #region [Построение строки]

        // victim — погибший, initiator — убийца (может быть null).
        // dtype — явный тип смерти; если не задан, берётся из HitInfo.
        private FeedRow BuildRow(BaseCombatEntity victim, BaseCombatEntity initiator, HitInfo info, bool allowed, DamageType? dtype = null)
        {
            if (!allowed) return null;

            var tpl = GetDeathMessage(dtype ?? info?.damageTypes?.GetMajorityDamageType() ?? DamageType.Generic);
            if (string.IsNullOrEmpty(tpl)) return null;

            var row = new FeedRow
            {
                Distance = FormatDistance(victim, initiator),
                ModIcons = new List<int>(6),
            };
            // Иконка инициатора-объекта (турель/ловушка) — только когда у сущности
            // есть маппинг на предмет. Убийца-игрок/животное -> иконка = само оружие из HitInfo.
            var iconInitiator = initiator != null && !(initiator is BasePlayer) && ItemDefOf(initiator) != null ? initiator : null;
            FillIcons(row, info, iconInitiator);

            string vName = NameOf(victim);
            string iName = NameOf(initiator);
            row.Message = string.Format(tpl, vName, iName);
            return row;
        }

        private string FormatDistance(BaseCombatEntity victim, BaseCombatEntity initiator)
        {
            if (initiator == null) return "0m";
            return Vector3.Distance(victim.transform.position, initiator.transform.position).ToString("F1") + "m";
        }

        // Идентификация сущности OctaBoss: v1-контракт Boss_ResolveEntity
        // (различает босса и свиту, работает и посмертно — retained-реестр).
        // Фолбэк — legacy GetBossName для старого OctaBoss без контракта.
        private string BossNameOf(BaseCombatEntity ent)
        {
            try
            {
                var res = OctaBoss?.Call("Boss_ResolveEntity", ent) as JObject;
                if (res != null)
                    return res["ok"]?.Value<bool>() == true ? res["data"]?["name"]?.ToString() : null;
            }
            catch { }
            try { return OctaBoss?.Call("GetBossName", ent) as string; } catch { return null; }
        }

        private string NameOf(BaseCombatEntity entity)
        {
            if (entity == null) return "";

            var player = entity as BasePlayer;
            if (player != null && !player.IsNpcPlayer())
            {
                string name = SanitizeRich(player.displayName);
                if (PlayersData.TryGetValue(player.userID, out var pd) && pd.HideMyName)
                    name = RandomUsernames.Get(player.userID + (ulong)UnityEngine.Random.Range(0, 100000));
                if (name.Length > 16) name = name.Substring(0, 16);
                return $"<size={Cfg.UIConfig.NameProperty.Size}><color={GetColorFor(player)}>{name}</color></size>";
            }

            // боссы OctaBoss и именованные животные — с золотой звездой
            try
            {
                string bossName = BossNameOf(entity);
                if (!string.IsNullOrEmpty(bossName))
                    return $"<size={Cfg.UIConfig.NameProperty.Size}><color=#FFD700>★ {bossName}</color></size>";
            }
            catch { }

            var livestock = entity as LivestockAnimal;
            if (livestock != null && !string.IsNullOrEmpty(livestock.AnimalName))
                return $"<size={Cfg.UIConfig.NameProperty.Size}><color=#FFD700>★ {SanitizeRich(livestock.AnimalName)}</color></size>";

            // NPC/объекты — локализованное имя из конфига или имя префаба
            string raw = entity.ShortPrefabName;
            string nice = Cfg.CoreConfig.RenameNPC ? GetNpcName(raw) : (entity is NPCPlayer npc ? SanitizeRich(npc.displayName) : raw);
            return $"<size={Cfg.UIConfig.NameProperty.Size}><color={Cfg.UIConfig.NameProperty.Color}>{nice}</color></size>";
        }

        private void FillIcons(FeedRow row, HitInfo info, BaseCombatEntity iconInitiator)
            => row.WeaponIcon = ResolveIcons(info, iconInitiator, row.ModIcons);

        // Возвращает itemid основной иконки и наполняет modIcons (хедшот/обвесы).
        // iconInitiator — сущность с маппингом на предмет (турель/ловушка), иначе null.
        private int ResolveIcons(HitInfo info, BaseCombatEntity iconInitiator, List<int> modIcons)
        {
            if (info == null) return IconHeadshot;
            if (info.isHeadshot) modIcons.Add(IconHeadshot);

            if (iconInitiator != null)
            {
                ItemDefinition initiatorDef = ItemDefOf(iconInitiator);
                if (initiatorDef != null)
                {
                    Item attackItem = GetAttackItem(info);
                    if (attackItem == null)
                    {
                        ItemDefinition attackDef = GetAttackDefinition(info);
                        if (attackDef != null && attackDef.itemid != initiatorDef.itemid)
                            modIcons.Add(attackDef.itemid);
                    }
                    else
                    {
                        modIcons.Add(attackItem.info.itemid);
                        if (attackItem.contents != null)
                            modIcons.AddRange(attackItem.contents.itemList.Select(x => x.info.itemid));
                    }
                    return initiatorDef.itemid;
                }
            }

            Item item = GetAttackItem(info);
            if (item != null)
            {
                if (item.contents != null)
                    modIcons.AddRange(item.contents.itemList.Select(x => x.info.itemid));
                return item.info.itemid;
            }
            ItemDefinition def = GetAttackDefinition(info);
            return def?.itemid ?? (info.isHeadshot ? 0 : IconHeadshot);
        }

        private Item GetAttackItem(HitInfo info)
        {
            return GetWeaponFromEntity(info.Weapon)
                   ?? GetWeaponFromEntity(info.WeaponPrefab)
                   ?? GetWeaponFromEntity(info.ProjectilePrefab?.sourceWeaponPrefab);
        }

        private ItemDefinition GetAttackDefinition(HitInfo info)
        {
            return ItemDefOf(info.Weapon)
                   ?? ItemDefOf(info.WeaponPrefab)
                   ?? ItemDefOf(info.ProjectilePrefab?.sourceWeaponPrefab)
                   ?? ItemDefOf(info.ProjectilePrefab?.sourceProjectilePrefab?.sourceWeaponPrefab);
        }

        private Item GetWeaponFromEntity(BaseEntity e) => e?.GetItem();

        private ItemDefinition ItemDefOf(BaseEntity e)
        {
            if (e == null) return null;
            if (!_prefab2Item.TryGetValue(e.prefabID, out int itemID) &&
                !_prefabName2Item.TryGetValue(e.ShortPrefabName, out itemID))
                return null;
            return ItemManager.itemDictionary.TryGetValue(itemID, out ItemDefinition item) ? item : null;
        }

        private string GetDeathMessage(DamageType damageType)
        {
            if (Cfg.CoreConfig.Messages.TryGetValue(damageType.ToString(), out List<string> variants) && variants.Count > 0)
                return variants[UnityEngine.Random.Range(0, variants.Count)];
            return null;
        }

        internal string GetNpcName(string shortPrefabName)
        {
            return Cfg.CoreConfig.Names.TryGetValue(shortPrefabName, out string name) ? name : shortPrefabName;
        }

        // Вырезаем rich-теги из динамических имён — иначе <color>/<size> в нике
        // ломают разметку строки ленты
        private static string SanitizeRich(string s) =>
            string.IsNullOrEmpty(s) ? s : s.Replace("<", "‹").Replace(">", "›");

        // PluginReference залипает на старой инстанции после перезагрузки OctVIP —
        // держим кэш живой ссылки; при отсутствии плагина ищем не чаще раза в 10 сек.
        private Plugin _vip;
        private float _vipMissedAt;
        private Plugin FindOctVIP()
        {
            if (_vip != null && _vip.IsLoaded) return _vip;
            _vip = null;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _vipMissedAt < 10f) return null;
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
                if (p.Name == "OctVIP" && p.IsLoaded) { _vip = p; return _vip; }
            _vipMissedAt = now;
            return null;
        }

        internal string GetColorFor(BasePlayer player)
        {
            foreach (var color in Cfg.UIConfig.Colors)
                if (permission.UserHasPermission(player.UserIDString, color.Key))
                    return color.Value;

            // цвет ника из OctVIP, если игрок с VIP-уровнем
            var vip = FindOctVIP();
            if (vip != null)
            {
                ulong uid = (ulong)player.userID;
                int level = vip.Call<int>("GetVipLevel", uid);
                if (level > 0) return vip.Call<string>("GetVipColor", uid);
            }
            return Cfg.UIConfig.NameProperty.Color;
        }

        #endregion

        #region [UI — экранные доли]

        private static string Fx(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        private static string HexToRGBA(string hexColor)
        {
            if (ColorUtility.TryParseHtmlString(hexColor, out Color color))
                return string.Format("{0:F2} {1:F2} {2:F2} {3:F2}", color.r, color.g, color.b, color.a);
            return "1 1 1 1";
        }

        private int RowWidth => Cfg?.UIConfig != null && Cfg.UIConfig.RowWidth > 0 ? Cfg.UIConfig.RowWidth : 480;

        // Рамка ленты в долях экрана. Исходная логика: правый верхний угол,
        // ширина RowWidth вирт.пкс, строки по 25 пкс, сдвиги OffsetX/OffsetY.
        private void FeedBounds(out float x1, out float y1, out float x2, out float y2)
        {
            float ox = (Cfg?.UIConfig?.OffsetX ?? 0) / RefW;
            float oy = (Cfg?.UIConfig?.OffsetY ?? 0) / RefH;
            float w = RowWidth / RefW;
            int rows = Math.Max(1, Cfg?.UIConfig?.Rows ?? 5);
            float h = rows * 25f / RefH;
            x2 = 1f - 0.0039f - ox;  // -5px от правого края
            x1 = x2 - w;
            y2 = 1f - 0.0069f - oy;  // -5px от верха
            y1 = y2 - h;
        }

        private bool FeedVisible(BasePlayer player) =>
            !(PlayersData.TryGetValue(player.userID, out var pd) && pd.HideMessages);

        // Полная перерисовка: шестерёнка + лента текущих строк.
        private void DrawAll(BasePlayer player)
        {
            DrawGear(player);
            DrawFeed(player);
        }

        private void DrawAllForAll()
        {
            foreach (var p in BasePlayer.activePlayerList) DrawAll(p);
        }

        private void DrawFeed(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, PanelName);
            if (!FeedVisible(player) || _rows.Count == 0) return;

            FeedBounds(out float x1, out float y1, out float x2, out float y2);
            int rows = Math.Max(1, Cfg.UIConfig.Rows);
            float rh = 1f / rows;

            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = PanelName,
                Parent = "Hud",
                Components = {
                    new CuiRectTransformComponent { AnchorMin = $"{Fx(x1)} {Fx(y1)}", AnchorMax = $"{Fx(x2)} {Fx(y2)}" },
                    new CuiImageComponent { Color = "0 0 0 0" },
                }
            });

            for (int i = 0; i < _rows.Count && i < rows; i++)
            {
                var row = _rows[i];
                string rowName = $"{PanelName}R{i}";
                // строка i — сверху вниз
                string ryMin = $"0 {Fx(1f - (i + 1) * rh)}";
                string ryMax = $"1 {Fx(1f - i * rh)}";

                container.Add(new CuiElement
                {
                    Name = rowName,
                    Parent = PanelName,
                    Components = {
                        new CuiRectTransformComponent { AnchorMin = ryMin, AnchorMax = ryMax },
                        new CuiImageComponent { Color = HexToRGBA(Cfg.UIConfig.RowColor) },
                    }
                });

                // Раскладка строки в вирт.пикселях сетки 1280x720 внутри ширины w:
                // [текст ....][иконки 18px, шаг 20px][дистанция 55px]
                // Иконки квадратные и вплотную за текстом — как в оригинале.
                float w = RowWidth;
                var icons = new List<int>(6);
                if (row.WeaponIcon != 0) icons.Add(row.WeaponIcon);
                if (row.ModIcons != null)
                    foreach (var m in row.ModIcons)
                        if (m != 0 && icons.Count < 6) icons.Add(m);

                float iconStart = w - 57f - icons.Count * 20f; // полоса иконок прижата к колонке дистанции
                float msgEnd = (iconStart - 6f) / w;

                // текст события: прижат вправо, вплотную к первой иконке
                var msg = new CuiElement
                {
                    Name = rowName + "K",
                    Parent = rowName,
                    Components = {
                        new CuiTextComponent { Text = row.Message, Color = HexToRGBA(Cfg.UIConfig.TextProperty.Color),
                            Align = TextAnchor.MiddleRight, FontSize = Cfg.UIConfig.TextProperty.Size, Font = "robotocondensed-bold.ttf" },
                        new CuiRectTransformComponent { AnchorMin = "0.012 0", AnchorMax = $"{Fx(msgEnd)} 1" },
                    }
                };
                if (Cfg.UIConfig.Outline)
                    msg.Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
                container.Add(msg);

                for (int slot = 0; slot < icons.Count; slot++)
                {
                    float ix1 = (iconStart + slot * 20f) / w;
                    var el = new CuiElement
                    {
                        Name = $"{rowName}I{slot}",
                        Parent = rowName,
                        Components = {
                            new CuiImageComponent { ItemId = icons[slot], Color = "1 1 1 1" },
                            // квадрат 18x18 по центру строки 25px высотой
                            new CuiRectTransformComponent { AnchorMin = $"{Fx(ix1)} 0.14", AnchorMax = $"{Fx((iconStart + slot * 20f + 18f) / w)} 0.86" },
                        }
                    };
                    if (Cfg.UIConfig.ImageOutline)
                        el.Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
                    container.Add(el);
                }

                // дистанция — крайняя правая колонка 55px с собственной подложкой
                container.Add(new CuiElement
                {
                    Name = rowName + "DB",
                    Parent = rowName,
                    Components = {
                        new CuiImageComponent { Color = HexToRGBA(Cfg.UIConfig.DistanceColor),
                            Sprite = "assets/content/ui/ui.background.transparent.linear.psd" },
                        new CuiRectTransformComponent { AnchorMin = $"{Fx((w - 55f) / w)} 0", AnchorMax = "1 1" },
                    }
                });
                var dist = new CuiElement
                {
                    Name = rowName + "DT",
                    Parent = rowName,
                    Components = {
                        new CuiTextComponent { Text = row.Distance, Color = HexToRGBA(Cfg.UIConfig.DistanceProperty.Color),
                            Align = TextAnchor.MiddleCenter, FontSize = Cfg.UIConfig.DistanceProperty.Size, Font = "robotocondensed-bold.ttf" },
                        new CuiRectTransformComponent { AnchorMin = $"{Fx((w - 55f) / w)} 0", AnchorMax = "1 1" },
                    }
                };
                if (Cfg.UIConfig.Outline)
                    dist.Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
                container.Add(dist);
            }

            CuiHelper.AddUi(player, container);
        }

        // Шестерёнка настроек — под правым нижним углом ленты, не мешает тексту
        private void DrawGear(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, GearName);
            CuiHelper.DestroyUi(player, WidgetName);
            if (Cfg?.UIConfig == null || !Cfg.UIConfig.EnableUISettings || !Cfg.UIConfig.EnableUISettingsButton) return;

            FeedBounds(out float fx1, out float fy1, out float fx2, out float fy2);
            float gx2 = fx2;
            float gx1 = gx2 - 20f / RefW;
            float gy2 = fy1 - 4f / RefH;
            float gy1 = gy2 - 20f / RefH;

            var container = new CuiElementContainer();
            var gear = new CuiElement
            {
                Name = GearName,
                Parent = "Overlay",
                Components = {
                    new CuiRectTransformComponent { AnchorMin = $"{Fx(gx1)} {Fx(gy1)}", AnchorMax = $"{Fx(gx2)} {Fx(gy2)}" },
                    new CuiImageComponent { Color = "1 1 1 0.8", Sprite = "assets/icons/tools.png", Material = "assets/icons/iconmaterial.mat" },
                }
            };
            if (Cfg.UIConfig.Outline)
                gear.Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
            container.Add(gear);
            container.Add(new CuiButton
            {
                Button = { Command = "octafeed toggle", Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Text = { Text = "" },
            }, GearName, GearName + ".Button");
            CuiHelper.AddUi(player, container);

            if (_settingsOpen.Contains(player.userID))
                DrawWidget(player);
        }

        // Всплывающая панель настроек — под шестерёнкой, правым краем по ленте
        private void DrawWidget(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, WidgetName);

            FeedBounds(out float fx1, out float fy1, out float fx2, out float fy2);
            float wx2 = fx2;
            float wx1 = wx2 - 175f / RefW;
            float wy2 = fy1 - 4f / RefH - 20f / RefH - 4f / RefH;
            float wy1 = wy2 - 46f / RefH;

            PlayersData.TryGetValue(player.userID, out var pd);
            bool hideName = pd?.HideMyName ?? false;
            bool hideFeed = pd?.HideMessages ?? false;

            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = WidgetName,
                Parent = "Hud",
                Components = {
                    new CuiRectTransformComponent { AnchorMin = $"{Fx(wx1)} {Fx(wy1)}", AnchorMax = $"{Fx(wx2)} {Fx(wy2)}" },
                    new CuiImageComponent { Color = "0 0 0 0.55", Material = "assets/icons/iconmaterial.mat" },
                }
            });
            container.Add(new CuiButton
            {
                Button = { Command = "octafeed hidemessages", Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.03 0.52", AnchorMax = "0.97 0.95" },
                Text = { Text = hideFeed ? "☑ Скрывать киллфид" : "☐ Скрывать киллфид",
                    Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.9", Font = "robotocondensed-bold.ttf", FontSize = 11 },
            }, WidgetName, WidgetName + ".HideMessages");
            container.Add(new CuiButton
            {
                Button = { Command = "octafeed hidemyname", Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.03 0.05", AnchorMax = "0.97 0.48" },
                Text = { Text = hideName ? "☑ Скрывать мой ник" : "☐ Скрывать мой ник",
                    Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.9", Font = "robotocondensed-bold.ttf", FontSize = 11 },
            }, WidgetName, WidgetName + ".HideMyName");
            CuiHelper.AddUi(player, container);
        }

        // Новая строка: вставляем сверху, обрезаем хвост, перерисовываем всем
        private void PushRow(FeedRow row)
        {
            row.ExpiresAt = UnityEngine.Time.realtimeSinceStartup + Math.Max(1f, Cfg.UIConfig.Time);
            _rows.Insert(0, row);
            int cap = Math.Max(1, Cfg.UIConfig.Rows);
            while (_rows.Count > cap) _rows.RemoveAt(_rows.Count - 1);
            DrawAllForAll();
        }

        // Снимаем истёкшие строки каждую секунду; перерисовываем только если что-то сняли
        private void ExpireRows()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            int removed = _rows.RemoveAll(r => now > r.ExpiresAt);
            if (removed > 0)
                foreach (var p in BasePlayer.activePlayerList) DrawFeed(p);

            // устаревшие «последние атаки» и метки смертей чистим — словари не растут
            if (_lastAttacks.Count > 0)
            {
                var stale = _lastAttacks.Where(kv => now - kv.Value.Time > AssistWindowSec).Select(kv => kv.Key).ToList();
                foreach (var k in stale) _lastAttacks.Remove(k);
            }
            if (_handledDeaths.Count > 0)
            {
                var stale = _handledDeaths.Where(kv => now - kv.Value > 3f).Select(kv => kv.Key).ToList();
                foreach (var k in stale) _handledDeaths.Remove(k);
            }
            if (_envHits.Count > 0)
            {
                var stale = _envHits.Where(kv => now - kv.Value.Time > EnvCauseWindow).Select(kv => kv.Key).ToList();
                foreach (var k in stale) _envHits.Remove(k);
            }
        }

        #endregion

        #region [Команды]

        [ChatCommand("dm")]
        private void CmdDm(BasePlayer player, string cmd, string[] args)
        {
            if (!Cfg.UIConfig.EnableUISettings) return;
            if (args.Length > 0)
            {
                switch (args[0])
                {
                    case "hidemyname": SwitchHideMyName(player); break;
                    case "hidemessages": SwitchHideMessages(player); break;
                }
            }
            PlayersData.TryGetValue(player.userID, out var pd);
            SendReply(player, $"<color=#E8B84B>◆ OctaFeed</color>\nЛента: {(pd != null && pd.HideMessages ? "скрыта" : "видна")}\nСкрытие ника: {(pd != null && pd.HideMyName ? "вкл" : "выкл")}\nИспользование: /dm hidemessages|hidemyname");
        }

        [ConsoleCommand("octafeed")]
        private void CmdConsole(ConsoleSystem.Arg arg)
        {
            if (!Cfg.UIConfig.EnableUISettings || !Cfg.UIConfig.EnableUISettingsButton) return;
            var player = arg.Player();
            if (player == null || !arg.HasArgs()) return;

            switch (arg.Args[0].ToString())
            {
                case "toggle":
                    if (!_settingsOpen.Remove(player.userID)) { _settingsOpen.Add(player.userID); DrawWidget(player); }
                    else CuiHelper.DestroyUi(player, WidgetName);
                    break;
                case "show": _settingsOpen.Add(player.userID); DrawWidget(player); break;
                case "hide": _settingsOpen.Remove(player.userID); CuiHelper.DestroyUi(player, WidgetName); break;
                case "hidemyname": SwitchHideMyName(player); DrawWidget(player); break;
                case "hidemessages": SwitchHideMessages(player); DrawWidget(player); break;
            }
        }

        private bool SwitchHideMyName(BasePlayer player)
        {
            if (!PlayersData.TryGetValue(player.userID, out var pd))
                pd = PlayersData[player.userID] = new PlayerData();
            pd.HideMyName = !pd.HideMyName;
            return pd.HideMyName;
        }

        private bool? SwitchHideMessages(BasePlayer player)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!PlayersData.TryGetValue(player.userID, out var pd))
                pd = PlayersData[player.userID] = new PlayerData();

            if (pd.LastToggleTime + 15000 > nowMs)
            {
                SendReply(player, $"<color=#E8B84B>◆ OctaFeed</color> Настройка ещё недоступна, подожди: <color=#FFD35C>{(pd.LastToggleTime + 15000 - nowMs) / 1000}</color> сек.");
                return null;
            }
            pd.HideMessages = !pd.HideMessages;
            pd.LastToggleTime = nowMs;
            pd.LastConnectTime = nowMs; // активность — чтобы запись не вычищалась автоочисткой
            DrawFeed(player);
            return pd.HideMessages;
        }

        // ── API для других плагинов (OctaHub «Настройки») ────────────────────

        [HookMethod("API_GetSettings")]
        public JObject API_GetSettings(ulong userId)
        {
            PlayersData.TryGetValue(userId, out var pd);
            return new JObject
            {
                ["hideMessages"] = pd?.HideMessages ?? false,
                ["hideMyName"] = pd?.HideMyName ?? false,
            };
        }

        // Установка флага без кулдауна — вызывает доверенный UI-провайдер.
        // Возвращает новое значение флага.
        [HookMethod("API_SetSetting")]
        public bool API_SetSetting(ulong userId, string key, bool value)
        {
            if (!PlayersData.TryGetValue(userId, out var pd))
                pd = PlayersData[userId] = new PlayerData();
            switch (key)
            {
                case "hideMessages": pd.HideMessages = value; break;
                case "hideMyName": pd.HideMyName = value; break;
                default: return false;
            }
            pd.LastConnectTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var player = BasePlayer.FindByID(userId);
            if (player != null) DrawFeed(player);
            return value;
        }

        // Совместимость со старым API: внешние плагины могут класть строки в ленту
        private void CreateNote(string message, float distance, int weaponIcon, List<int> weaponMods)
        {
            var row = new FeedRow { Message = message, Distance = distance.ToString("F1") + "m", WeaponIcon = weaponIcon, ModIcons = weaponMods ?? new List<int>() };
            PushRow(row);
        }

        [Obsolete]
        private void CreateNote(string vName, string iName, float distance, string vColor, string iColor, string weaponName, string[] weaponMods, bool isHeadshot, bool needRenameVictim, bool needRenameInitiator)
        {
            string message = $"<color={iColor}>{(needRenameInitiator ? GetNpcName(iName) : iName)}</color> убил <color={vColor}>{(needRenameVictim ? GetNpcName(vName) : vName)}</color>";
            int weaponIcon = IconDefaultWeapon;
            var modsList = new List<int>(6);
            if (ItemManager.itemDictionaryByName.TryGetValue(weaponName, out ItemDefinition itemDefinition))
                weaponIcon = itemDefinition.itemid;
            if (isHeadshot) modsList.Add(IconHeadshot);
            foreach (var weaponMod in weaponMods)
                if (ItemManager.itemDictionaryByName.TryGetValue(weaponMod, out ItemDefinition modDefinition))
                    modsList.Add(modDefinition.itemid);
            CreateNote(message, distance, weaponIcon, modsList);
        }

        #endregion

        #region [Конфиг]

        public class OctaFeedConfig
        {
            public class CoreConfiguration
            {
                [JsonProperty("[3.3] Показывать смерть Helicopter")]
                public bool Helicopter = true;
                [JsonProperty("[4.2] Показывать, когда игрок умирает от окружения")]
                public bool PlayerKilledByEnv = true;
                [JsonProperty("[3.1] Показывать смерть животных")]
                public bool Animal = true;
                [JsonProperty("[3.2] Показывать смерть NPC")]
                public bool NPC = true;
                [JsonProperty("[1] Названия NPC")]
                public Dictionary<string, string> Names;
                [JsonProperty("[5] Заменять имена NPC на имена из конфига")]
                public bool RenameNPC = true;
                [JsonProperty("[4.1] Показывать, когда NPC убивают игрока")]
                public bool PlayerKilledByNPC = true;
                [JsonProperty("[4.3] Показывать, когда животные убивают игрока")]
                public bool PlayerKilledByAnimal = true;
                [JsonProperty("[4.4] Окно ассиста для смерти от окружения, сек")]
                public float AssistWindowSec = 8f;
                [JsonProperty("[3.4] Показывать смерть BradleyAPC")]
                public bool Bradley = true;
                [JsonProperty("[2] Варианты сообщений для киллбара")]
                public Dictionary<string, List<string>> Messages;
            }

            public class DMTextProperty
            {
                [JsonProperty("[1] Размер")]
                public int Size = 11;
                [JsonProperty("[2] Цвет")]
                public string Color = "#FFFFFF";
            }

            [JsonProperty("[1] Настройки плагина")]
            public CoreConfiguration CoreConfig = new CoreConfiguration();

            public class UIConfiguration
            {
                [JsonProperty("[1.1] Никнеймы")]
                public DMTextProperty NameProperty = new DMTextProperty();
                [JsonProperty("[1.2] Текст")]
                public DMTextProperty TextProperty = new DMTextProperty();
                [JsonProperty("[1.3] Дистанция")]
                public DMTextProperty DistanceProperty = new DMTextProperty();
                [JsonProperty("[2] Настройка цвета ника по привилегиям")]
                public Dictionary<string, string> Colors = new Dictionary<string, string>();
                [JsonProperty("[3] Количество строк")]
                public int Rows = 5;
                [JsonProperty("[4] Время жизни строки в секундах")]
                public float Time = 5;
                [JsonProperty("[5.1] Включить обводку текста")]
                public bool Outline = true;
                [JsonProperty("[5.2] Включить обводку изображений")]
                public bool ImageOutline = true;
                [JsonProperty("[6] Цвет задней панели убийства")]
                public string RowColor = "#00000000";
                [JsonProperty("[7] Цвет панели с дистанцией")]
                public string DistanceColor = "#FFFFFFC0";
                [JsonProperty("[8.1] Отступ сверху в вирт.пкс (сетка 1280x720)")]
                public int OffsetY = 0;
                [JsonProperty("[8.2] Отступ сбоку в вирт.пкс (сетка 1280x720)")]
                public int OffsetX = 0;
                [JsonProperty("[8.3] Ширина строки киллфида в вирт.пкс (сетка 1280x720)")]
                public int RowWidth = 480;
                [JsonProperty("[9.1] Включить настройку плагина")]
                public bool EnableUISettings = true;
                [JsonProperty("[9.2] Включить кнопку настроек плагина")]
                public bool EnableUISettingsButton = true;
            }

            [JsonProperty("[2] Настройки UI")]
            public UIConfiguration UIConfig = new UIConfiguration();
        }

        public OctaFeedConfig GetDefaultConfig()
        {
            return new OctaFeedConfig
            {
                CoreConfig = new OctaFeedConfig.CoreConfiguration
                {
                    Names = Names,
                    Messages = Messages,
                    Animal = true,
                    NPC = true,
                    PlayerKilledByAnimal = true,
                    PlayerKilledByNPC = true,
                    PlayerKilledByEnv = true,
                    RenameNPC = true,
                    Bradley = true,
                    Helicopter = true,
                },
                UIConfig = new OctaFeedConfig.UIConfiguration
                {
                    NameProperty = new OctaFeedConfig.DMTextProperty { Color = "#D3D3D3", Size = 11 },
                    TextProperty = new OctaFeedConfig.DMTextProperty { Color = "#F0F0F0", Size = 10 },
                    DistanceProperty = new OctaFeedConfig.DMTextProperty { Color = "#ffffff", Size = 11 },
                    Colors = new Dictionary<string, string>
                    {
                        ["octafeed.deluxe"] = "#7303c0",
                        ["octafeed.vip"] = "#f9ff55",
                        ["octafeed.premium"] = "#55ff8a",
                    },
                    Rows = 5,
                    Time = 5f,
                    Outline = true,
                    ImageOutline = true,
                    DistanceColor = "#FFFFFFC0",
                    RowColor = "#00000000",
                    OffsetX = 0,
                    OffsetY = 0,
                    RowWidth = 480,
                    EnableUISettings = true,
                    EnableUISettingsButton = true,
                },
            };
        }

        #endregion
    }
}
