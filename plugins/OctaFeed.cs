using System.Linq;
using Oxide.Game.Rust.Cui;
using Newtonsoft.Json;
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
    [Info("OctaFeed", "Octavian", "2.0.0")]
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
            public double LastConnectTime = 0;
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

        // Последний урон по игроку — для атрибуции «смерти от окружения»
        // (утонул/замёрз/истёк кровью через 8 сек после выстрела — засчитываем киллеру).
        private class RecentAttack
        {
            public HitInfo Hit;
            public float Time;
        }
        private readonly Dictionary<ulong, RecentAttack> _lastAttacks = new Dictionary<ulong, RecentAttack>();
        private const float AssistWindow = 8f;

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
            if (entity is BasePlayer player && player.IsNonNpcPlayer() && info?.Initiator != null && info.Initiator != entity)
            {
                _lastAttacks[player.userID] = new RecentAttack { Hit = info, Time = UnityEngine.Time.realtimeSinceStartup };
            }
        }

        private void OnEntityDeath(BaseCombatEntity victim, HitInfo info)
        {
            if (victim == null || victim.net == null)
                return;

            // Атрибуция: если игрок умер «от окружения» (утонул/кровотечение/холод/падение),
            // но за последние 8 сек получал урон от кого-то — виновник тот, кто нанёс урон.
            if (victim is BasePlayer vp && vp.IsNonNpcPlayer())
            {
                bool isEnvOrSelf = info == null || info.Initiator == null || info.Initiator == victim;
                if (!isEnvOrSelf && info.damageTypes != null)
                {
                    var maj = info.damageTypes.GetMajorityDamageType();
                    if (maj == DamageType.Drowned || maj == DamageType.Bleeding || maj == DamageType.Cold)
                        isEnvOrSelf = true;
                }
                if (isEnvOrSelf && _lastAttacks.TryGetValue(vp.userID, out var last) &&
                    last.Hit?.Initiator != null && last.Hit.Initiator != victim &&
                    UnityEngine.Time.realtimeSinceStartup - last.Time <= AssistWindow)
                {
                    info = last.Hit;
                }
                _lastAttacks.Remove(vp.userID);
            }

            FeedRow row = null;

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
                    // игрок убил не-игрока: NPC, вертолёт, танк, боссы OctaBoss.
                    // Обычных животных и прочие объекты в ленту не пускаем.
                    row = BuildRow(victim, killer, info, GateForVictim(victim));
                }
            }

            // чистая смерть без инициатора (суицид, падение, голод)
            if (row == null && victim is BasePlayer solo && solo.IsNonNpcPlayer())
                row = BuildRow(solo, null, info, Cfg.CoreConfig.PlayerKilledByEnv);

            if (row != null) PushRow(row);
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
            // прочее — только боссы и свита OctaBoss
            try { return !string.IsNullOrEmpty(OctaBoss?.Call("GetBossName", victim) as string); }
            catch { return false; }
        }

        private void OnPatrolHelicopterKill(PatrolHelicopter victim, HitInfo info)
        {
            if (victim == null || info?.damageTypes == null || info.InitiatorPlayer == null) return;
            var row = BuildRow(victim, info.InitiatorPlayer, info, Cfg.CoreConfig.Helicopter);
            if (row != null) PushRow(row);
        }

        #endregion

        #region [Построение строки]

        // victim — погибший, initiator — убийца (может быть null).
        private FeedRow BuildRow(BaseCombatEntity victim, BaseCombatEntity initiator, HitInfo info, bool allowed)
        {
            if (!allowed) return null;

            var tpl = GetDeathMessage(info?.damageTypes?.GetMajorityDamageType() ?? DamageType.Generic);
            if (string.IsNullOrEmpty(tpl)) return null;

            var row = new FeedRow
            {
                Distance = FormatDistance(victim, initiator),
                ModIcons = new List<int>(6),
            };
            FillIcons(row, info, initiator);

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

        private string NameOf(BaseCombatEntity entity)
        {
            if (entity == null) return "";

            var player = entity as BasePlayer;
            if (player != null && !player.IsNpcPlayer())
            {
                string name = player.displayName;
                if (PlayersData.TryGetValue(player.userID, out var pd) && pd.HideMyName)
                    name = RandomUsernames.Get(player.userID + (ulong)UnityEngine.Random.Range(0, 100000));
                if (name.Length > 16) name = name.Substring(0, 16);
                return $"<size={Cfg.UIConfig.NameProperty.Size}><color={GetColorFor(player)}>{name}</color></size>";
            }

            // боссы OctaBoss и именованные животные — с золотой звездой
            try
            {
                string bossName = OctaBoss?.Call("GetBossName", entity) as string;
                if (!string.IsNullOrEmpty(bossName))
                    return $"<size={Cfg.UIConfig.NameProperty.Size}><color=#FFD700>★ {bossName}</color></size>";
            }
            catch { }

            var livestock = entity as LivestockAnimal;
            if (livestock != null && !string.IsNullOrEmpty(livestock.AnimalName))
                return $"<size={Cfg.UIConfig.NameProperty.Size}><color=#FFD700>★ {livestock.AnimalName}</color></size>";

            // NPC/объекты — локализованное имя из конфига или имя префаба
            string raw = entity.ShortPrefabName;
            string nice = Cfg.CoreConfig.RenameNPC ? GetNpcName(raw) : (entity is NPCPlayer npc ? npc.displayName : raw);
            return $"<size={Cfg.UIConfig.NameProperty.Size}><color={Cfg.UIConfig.NameProperty.Color}>{nice}</color></size>";
        }

        private void FillIcons(FeedRow row, HitInfo info, BaseCombatEntity initiator)
        {
            if (info == null)
            {
                row.WeaponIcon = IconHeadshot;
                return;
            }
            if (info.isHeadshot)
                row.ModIcons.Add(IconHeadshot);

            if (initiator != null)
            {
                ItemDefinition initiatorDef = ItemDefOf(initiator);
                if (initiatorDef != null)
                {
                    row.WeaponIcon = initiatorDef.itemid;
                    Item attackItem = GetAttackItem(info);
                    if (attackItem == null)
                    {
                        ItemDefinition attackDef = GetAttackDefinition(info);
                        if (attackDef != null && attackDef.itemid != row.WeaponIcon)
                            row.ModIcons.Add(attackDef.itemid);
                    }
                    else
                    {
                        row.ModIcons.Add(attackItem.info.itemid);
                        if (attackItem.contents != null)
                            row.ModIcons.AddRange(attackItem.contents.itemList.Select(x => x.info.itemid));
                    }
                }
            }
            else
            {
                Item attackItem = GetAttackItem(info);
                if (attackItem != null)
                {
                    row.WeaponIcon = attackItem.info.itemid;
                    if (attackItem.contents != null)
                        row.ModIcons.AddRange(attackItem.contents.itemList.Select(x => x.info.itemid));
                }
                else
                {
                    ItemDefinition attackDef = GetAttackDefinition(info);
                    row.WeaponIcon = attackDef?.itemid ?? (info.isHeadshot ? 0 : IconHeadshot);
                }
            }
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

        // PluginReference залипает на старой инстанции после перезагрузки OctVIP —
        // ищем живой плагин по имени при каждом обращении (киллы редкие, это дёшево)
        private Plugin FindOctVIP()
        {
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
                if (p.Name == "OctVIP" && p.IsLoaded) return p;
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

                // текст события: прижат вправо, вплотную к иконкам
                var msg = new CuiElement
                {
                    Name = rowName + "K",
                    Parent = rowName,
                    Components = {
                        new CuiTextComponent { Text = row.Message, Color = HexToRGBA(Cfg.UIConfig.TextProperty.Color),
                            Align = TextAnchor.MiddleRight, FontSize = Cfg.UIConfig.TextProperty.Size, Font = "robotocondensed-bold.ttf" },
                        new CuiRectTransformComponent { AnchorMin = "0.02 0", AnchorMax = "0.665 1" },
                    }
                };
                if (Cfg.UIConfig.Outline)
                    msg.Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
                container.Add(msg);

                // иконка оружия + до 5 модов — равномерные слоты правее текста
                var icons = new List<int> { row.WeaponIcon };
                if (row.ModIcons != null) icons.AddRange(row.ModIcons);
                int slot = 0;
                foreach (var icon in icons)
                {
                    if (icon == 0 || slot >= 6) { slot++; continue; }
                    float ix1 = 0.675f + slot * 0.0367f;
                    var el = new CuiElement
                    {
                        Name = $"{rowName}I{slot}",
                        Parent = rowName,
                        Components = {
                            new CuiImageComponent { ItemId = icon, Color = "1 1 1 1" },
                            new CuiRectTransformComponent { AnchorMin = $"{Fx(ix1)} 0.08", AnchorMax = $"{Fx(ix1 + 0.031f)} 0.92" },
                        }
                    };
                    if (Cfg.UIConfig.ImageOutline)
                        el.Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
                    container.Add(el);
                    slot++;
                }

                // дистанция — крайняя правая колонка
                var dist = new CuiElement
                {
                    Name = rowName + "DT",
                    Parent = rowName,
                    Components = {
                        new CuiTextComponent { Text = row.Distance, Color = HexToRGBA(Cfg.UIConfig.DistanceProperty.Color),
                            Align = TextAnchor.MiddleCenter, FontSize = Cfg.UIConfig.DistanceProperty.Size, Font = "robotocondensed-bold.ttf" },
                        new CuiRectTransformComponent { AnchorMin = "0.90 0", AnchorMax = "1 1" },
                    }
                };
                if (Cfg.UIConfig.Outline)
                    dist.Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
                container.Add(dist);
            }

            CuiHelper.AddUi(player, container);
        }

        // Шестерёнка настроек слева от первого ряда ленты
        private void DrawGear(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, GearName);
            CuiHelper.DestroyUi(player, WidgetName);
            if (Cfg?.UIConfig == null || !Cfg.UIConfig.EnableUISettings || !Cfg.UIConfig.EnableUISettingsButton) return;

            float ox = (Cfg.UIConfig.OffsetX) / RefW, oy = (Cfg.UIConfig.OffsetY) / RefH;
            float w = RowWidth / RefW;
            float gx2 = 1f - 0.0039f - ox - w - 0.004f; // левее ленты
            float gx1 = gx2 - 20f / RefW;
            float gy2 = 1f - 0.0069f - oy;
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

        // Всплывающая панель настроек — левее шестерёнки, долями экрана
        private void DrawWidget(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, WidgetName);

            float ox = (Cfg.UIConfig.OffsetX) / RefW, oy = (Cfg.UIConfig.OffsetY) / RefH;
            float w = RowWidth / RefW;
            float gx1 = 1f - 0.0039f - ox - w - 0.004f - 20f / RefW;
            float gy2 = 1f - 0.0069f - oy;
            float wx2 = gx1 - 0.004f;
            float wx1 = wx2 - 175f / RefW;
            float wy2 = gy2 + 4f / RefH;
            float wy1 = wy2 - 44f / RefH;

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

            // устаревшие «последние атаки» тоже чистим — словарь не растёт бесконечно
            if (_lastAttacks.Count > 0)
            {
                var stale = _lastAttacks.Where(kv => now - kv.Value.Time > AssistWindow).Select(kv => kv.Key).ToList();
                foreach (var k in stale) _lastAttacks.Remove(k);
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

            if (pd.LastConnectTime + 15000 > nowMs)
            {
                SendReply(player, $"<color=#E8B84B>◆ OctaFeed</color> Настройка ещё недоступна, подожди: <color=#FFD35C>{(pd.LastConnectTime + 15000 - nowMs) / 1000}</color> сек.");
                return null;
            }
            pd.HideMessages = !pd.HideMessages;
            pd.LastConnectTime = nowMs;
            DrawFeed(player);
            return pd.HideMessages;
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
