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
    [Info("OctaFeed", "Octavian", "1.0.3")]
    [Description("Киллфид: кто кого убил, оружие, дистанция. Поддержка боссов OctaBoss")]
    public partial class OctaFeed : RustPlugin
    {
        [PluginReference] private Plugin OctaBoss;

        private static int RowWidth => Instance.Cfg?.UIConfig != null && Instance.Cfg.UIConfig.RowWidth > 0 ? Instance.Cfg.UIConfig.RowWidth : 480;
        private static int GetWpStart(int totalWidth, int modCount) => (totalWidth - 57) - (20 + modCount * 18);

        private void RemoveUITemplate(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, PANEL_NAME);
        }

        private void AddUISettings(BasePlayer player)
        {
            int w = RowWidth;
            int ox = Instance.Cfg.UIConfig.OffsetX;
            int oy = Instance.Cfg.UIConfig.OffsetY;
            CuiElementContainer container = new CuiElementContainer();
            container.Add(new CuiElement { Name = "DMUI", Parent = "Overlay", Components = { new CuiRectTransformComponent { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = $"{-w - 25 - ox} {-25 - oy}", OffsetMax = $"{-w - 5 - ox} {-5 - oy}" }, new CuiImageComponent { Color = "1 1 1 0.8", Sprite = "assets/icons/tools.png", Material = "assets/icons/iconmaterial.mat", } } });
            if (Instance.Cfg.UIConfig.Outline)
                container[container.Count - 1].Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
            container.Add(new CuiButton { Button = { Command = $"octafeed show", Color = "0 0 0 0", }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", }, Text = { Text = "", }, }, "DMUI", "DMUI.Button");
            CuiHelper.AddUi(player, container);
        }
        private class RecentAttack
        {
            public HitInfo Hit;
            public float Time;
        }
        private readonly Dictionary<ulong, RecentAttack> LastAttacks = new Dictionary<ulong, RecentAttack>();

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity is BasePlayer player && player.IsNonNpcPlayer() && info?.Initiator != null && info.Initiator != entity)
            {
                LastAttacks[player.userID] = new RecentAttack { Hit = info, Time = UnityEngine.Time.realtimeSinceStartup };
            }
        }

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
            ["simpleshark"] = "Shark",
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
        internal Dictionary<string, int> PrefabName2Item = new Dictionary<string, int>()
        {
            ["40mm_grenade_he"] = -1123473824,
            ["rocket_basic"] = 442886268,
            ["rocket_admin"] = 442886268,
            ["rocket_hv"] = 442886268,
            ["rocket_fire"] = 442886268,
        };

        private void RemoveUISettings(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "DMUI");
        }

        internal static string HexToRGBA(string hexColor)
        {
            if (ColorUtility.TryParseHtmlString(hexColor, out Color color))
                return string.Format("{0:F2} {1:F2} {2:F2} {3:F2}", color.r, color.g, color.b, color.a);
            return "1 1 1 1";
        }

        [ChatCommand("dm")]
        private void CMDChatMain(BasePlayer player, string cmd, string[] args)
        {
            if (Instance.Cfg.UIConfig.EnableUISettings == false)
                return;
            PlayersData.TryGetValue(player.userID, out PlayerData playerData);
            if (args.Length > 0)
            {
                switch (args[0])
                {
                    case "hidemyname":
                    {
                        bool result = SwitchHideMyName(player);
                        SendReply(player, $"<color=#E8B84B>◆ OctaFeed</color>\nЛента: {(playerData != null && playerData.HideMessages ? "скрыта" : "видна")}\nСкрытие ника: {(result ? "вкл" : "выкл")}\nИспользование: /dm hidemessages|hidemyname");
                        break;
                    }

                    case "hidemessages":
                    {
                        bool? result = SwitchHideMessages(player);
                        if (result.HasValue)
                            SendReply(player, $"<color=#E8B84B>◆ OctaFeed</color>\nЛента: {(result.Value ? "скрыта" : "видна")}\nСкрытие ника: {(playerData != null && playerData.HideMyName ? "вкл" : "выкл")}\nИспользование: /dm hidemessages|hidemyname");
                        break;
                    }
                }
            }
            else
            {
                SendReply(player, $"<color=#E8B84B>◆ OctaFeed</color>\nЛента: {(playerData != null && playerData.HideMessages ? "скрыта" : "видна")}\nСкрытие ника: {(playerData != null && playerData.HideMyName ? "вкл" : "выкл")}\nИспользование: /dm hidemessages|hidemyname");
            }
        }
        private void AddUIRow(string message, string distance, int weaponIcon, List<int> weaponModIcons)
        {
            LAST_ROW_MODIFY_TIME = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            int w = RowWidth;
            int wpStart = GetWpStart(w, weaponModIcons.Count);
            CuiElementContainer container = new CuiElementContainer();
            for (int i = Instance.Cfg.UIConfig.Rows - 1; i > 0; i--)
            {
                container.Add(new CuiElement { Name = $"{PANEL_NAME}R{(LAST_ROW_ID + i - 1) % Instance.Cfg.UIConfig.Rows}", Components = { new CuiRectTransformComponent { OffsetMin = i >= DeathRow.DeathRows.Count ? $"{w} {-20 - i * 25}" : $"0 {-20 - i * 25}", OffsetMax = $"{w} {0 - i * 25}" } }, Update = true });
            }

            LAST_ROW_ID = (LAST_ROW_ID - 1 + Instance.Cfg.UIConfig.Rows) % Instance.Cfg.UIConfig.Rows;
            container.Add(new CuiElement { Name = $"{PANEL_NAME}R{LAST_ROW_ID}", Components = { new CuiRectTransformComponent { OffsetMin = "0 -20", OffsetMax = $"{w} 0" } }, Update = true });
            container.Add(new CuiElement { Name = $"{PANEL_NAME}R{LAST_ROW_ID}DT", Components = { new CuiTextComponent { Text = distance }, }, Update = true });
            container.Add(new CuiElement { Name = $"{PANEL_NAME}R{LAST_ROW_ID}K", Components = { new CuiTextComponent { Text = message }, new CuiRectTransformComponent { OffsetMin = "6 0", OffsetMax = $"{wpStart - 6} 20" }, }, Update = true });
            container.Add(new CuiElement { Name = $"{PANEL_NAME}R{LAST_ROW_ID}WP", Components = { new CuiRectTransformComponent { OffsetMin = $"{wpStart} 0", OffsetMax = $"{w - 57} 20" }, }, Update = true });
            container.Add(new CuiElement { Name = $"{PANEL_NAME}R{LAST_ROW_ID}IW", Components = { new CuiImageComponent { ItemId = weaponIcon }, }, Update = true });
            for (int j = 0; j < weaponModIcons.Count; j++)
            {
                container.Add(new CuiElement { Name = $"{PANEL_NAME}R{LAST_ROW_ID}IM{5 - j}", Components = { new CuiImageComponent { ItemId = weaponModIcons[j] }, }, Update = true });
            }

            string jsonContainer = container.ToJson();
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (Instance.PlayersData.TryGetValue(player.userID, out PlayerData playerData))
                {
                    if (playerData.HideMessages)
                        continue;
                }

                CuiHelper.AddUi(player, jsonContainer);
            }
        }

        private void SwitchPlayerHideMessages(BasePlayer player)
        {
            SwitchHideMessages(player);
        }
		   		 		  						  	   		  	   		  	 				  		 			  	 		
        private void AddUIRows(BasePlayer player)
        {
            if (Instance.PlayersData.TryGetValue(player.userID, out PlayerData playerData))
            {
                if (playerData.HideMessages)
                    return;
            }

            int w = RowWidth;
            CuiElementContainer container = new CuiElementContainer();
            for (int i = Instance.Cfg.UIConfig.Rows - 1; i >= 0; i--)
            {
                if (i < DeathRow.DeathRows.Count)
                {
                    int currentRowIndex = (LAST_ROW_ID + i) % Instance.Cfg.UIConfig.Rows;
                    var row = DeathRow.DeathRows[i];
                    int wpStart = GetWpStart(w, row.WeaponModsIcons.Count);
                    container.Add(new CuiElement { Name = PANEL_NAME + "R" + currentRowIndex, Parent = PANEL_NAME, Components = { new CuiRectTransformComponent { OffsetMin = $"0 {-20 - (i) * 25}", OffsetMax = $"{w} {0 - (i) * 25}" }, }, Update = true });
                    container.Add(new CuiElement { Name = PANEL_NAME + "R" + currentRowIndex + "D" + "T", Parent = PANEL_NAME + "R" + currentRowIndex, Components = { new CuiTextComponent { Text = row.Distance, }, }, Update = true });
                    container.Add(new CuiElement { Name = PANEL_NAME + "R" + currentRowIndex + "K", Parent = PANEL_NAME + "R" + currentRowIndex, Components = { new CuiTextComponent { Text = row.Message, }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "6 0", OffsetMax = $"{wpStart - 6} 20" }, }, Update = true });
                    container.Add(new CuiElement { Name = PANEL_NAME + "R" + currentRowIndex + "WP", Parent = PANEL_NAME + "R" + currentRowIndex, Components = { new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{wpStart} 0", OffsetMax = $"{w - 57} 20" }, }, Update = true });
                    container.Add(new CuiElement { Name = PANEL_NAME + "R" + currentRowIndex + "I" + "W", Parent = PANEL_NAME + "R" + currentRowIndex + "WP", Components = { new CuiImageComponent { ItemId = row.WeaponIcon, } }, Update = true });
                    for (int j = 0; j < row.WeaponModsIcons.Count; j++)
                    {
                        container.Add(new CuiElement { Name = PANEL_NAME + "R" + currentRowIndex + "I" + "M" + (5 - j).ToString(), Parent = PANEL_NAME + "R" + currentRowIndex + "WP", Components = { new CuiImageComponent { ItemId = row.WeaponModsIcons.ElementAt(j), }, }, Update = true });
                    }
                }
            }

            CuiHelper.AddUi(player, container);
        }

        private string CreateUITemplate()
        {
            int w = RowWidth;
            int ox = Instance.Cfg.UIConfig.OffsetX;
            int oy = Instance.Cfg.UIConfig.OffsetY;
            int defaultWpStart = GetWpStart(w, 0);

            CuiElementContainer container = new CuiElementContainer();
            container.Add(new CuiElement { Name = PANEL_NAME, Parent = HUD, Components = { new CuiRectTransformComponent { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = $"{-w - 5 - ox} {-250 - oy}", OffsetMax = $"{-5 - ox} {-5 - oy}" }, new CuiImageComponent { Color = "0 0 0 0" }, } });
            for (int i = 0; i < Instance.Cfg.UIConfig.Rows; i++)
            {
                container.Add(new CuiElement { Name = PANEL_NAME + "R" + i, Parent = PANEL_NAME, Components = { new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{w} {-20 - i * 25}", OffsetMax = $"{w} {0 - i * 25}" }, new CuiImageComponent { Color = HexToRGBA(Instance.Cfg.UIConfig.RowColor) }, } });
                container.Add(new CuiElement { Name = PANEL_NAME + "R" + i + "D", Parent = PANEL_NAME + "R" + i, Components = { new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{w - 55} -20", OffsetMax = $"{w + 5} 40" }, new CuiRawImageComponent { Color = HexToRGBA(Instance.Cfg.UIConfig.DistanceColor), Material = "assets/icons/iconmaterial.mat", Sprite = $"assets/icons/subtract.png", }, } });
                container.Add(new CuiElement { Name = PANEL_NAME + "R" + i + "D" + "T", Parent = PANEL_NAME + "R" + i, Components = { new CuiTextComponent { Text = "", Color = HexToRGBA(Instance.Cfg.UIConfig.DistanceProperty.Color), Align = TextAnchor.MiddleCenter, FontSize = Instance.Cfg.UIConfig.DistanceProperty.Size, Font = "robotocondensed-bold.ttf", }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{w - 55} 0", OffsetMax = $"{w + 5} 20" } } });
                if (Instance.Cfg.UIConfig.Outline)
                    container[container.Count - 1].Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
                container.Add(new CuiElement { Name = PANEL_NAME + "R" + i + "WP", Parent = PANEL_NAME + "R" + i, Components = { new CuiScrollViewComponent { Vertical = false, Horizontal = true, MovementType = UnityEngine.UI.ScrollRect.MovementType.Unrestricted, ContentTransform = new CuiRectTransform { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "0 0", OffsetMax = "128 20" }, HorizontalScrollbar = new CuiScrollbar { Size = 0, HandleColor = "0 0 0 0", HighlightColor = "0 0 0 0", PressedColor = "0 0 0 0", TrackColor = "0 0 0 0", }, VerticalScrollbar = new CuiScrollbar { Size = 0, HandleColor = "0 0 0 0", HighlightColor = "0 0 0 0", PressedColor = "0 0 0 0", TrackColor = "0 0 0 0", } }, new CuiImageComponent { Color = "0 0 0 0", }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{defaultWpStart} 0", OffsetMax = $"{w - 57} 20" }, } });
                container.Add(new CuiElement { Name = PANEL_NAME + "R" + i + "I" + "W", Parent = PANEL_NAME + "R" + i + "WP", Components = { new CuiImageComponent { ItemId = 1545779598, Color = "1 1 1 1", }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{1} 1", OffsetMax = $"{19} 19" } } });
                if (Instance.Cfg.UIConfig.ImageOutline)
                    container[container.Count - 1].Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
                for (int j = 0; j < 6; j++)
                {
                    container.Add(new CuiElement { Name = PANEL_NAME + "R" + i + "I" + "M" + j, Parent = PANEL_NAME + "R" + i + "WP", Components = { new CuiImageComponent { ItemId = 952603248, Color = "1 1 1 1", }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{112 - j * 18} 2", OffsetMax = $"{128 - j * 18} 18" } } });
                    if (Instance.Cfg.UIConfig.ImageOutline)
                        container[container.Count - 1].Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
                }

                container.Add(new CuiElement { Name = PANEL_NAME + "R" + i + "K", Parent = PANEL_NAME + "R" + i, Components = { new CuiTextComponent { Text = "", Color = HexToRGBA(Instance.Cfg.UIConfig.TextProperty.Color), Align = TextAnchor.MiddleRight, FontSize = Instance.Cfg.UIConfig.TextProperty.Size, Font = "robotocondensed-bold.ttf", }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "6 0", OffsetMax = $"{defaultWpStart - 6} 20" } } });
                if (Instance.Cfg.UIConfig.Outline)
                    container[container.Count - 1].Components.Add(new CuiOutlineComponent { Color = "0 0 0 1", Distance = "-0.5 0.5" });
            }

            return container.ToJson();
        }

        private bool? SwitchHideMessages(BasePlayer player)
        {
            if (PlayersData.TryGetValue(player.userID, out PlayerData playerData))
            {
                var nowSeconds = new DateTimeOffset(DateTime.Now).ToUnixTimeMilliseconds();
                if (playerData.LastConnectTime + 15000 > nowSeconds)
                {
                    SendReply(player, $"<color=#E8B84B>◆ OctaFeed</color> Настройка ещё недоступна, подожди: <color=#FFD35C>{(playerData.LastConnectTime + 15000 - nowSeconds) / 1000}</color> сек.");
                    return null;
                }
		   		 		  						  	   		  	   		  	 				  		 			  	 		
                playerData.HideMessages = !playerData.HideMessages;
                playerData.LastConnectTime = nowSeconds;
                if (playerData.HideMessages == false)
                {
                    AddTemplateUI(player);
                    AddUIRows(player);
                }
                else
                {
                    RemoveUITemplate(player);
                }
            }
            else
            {
                PlayersData[player.userID] = playerData = new PlayerData()
                {
                    HideMessages = true,
                    HideMyName = false,
                    LastConnectTime = new DateTimeOffset(DateTime.Now).ToUnixTimeMilliseconds()
                };
                RemoveUITemplate(player);
            }

            return playerData.HideMessages;
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                RemoveUITemplate(player);
                RemoveUISettings(player);
            }

            Interface.Oxide.DataFileSystem.WriteObject("OctaFeed\\PlayersData", PlayersData);
            DeathRow.DeathRows.Clear();
            Instance = null;
        }

        private void CreateNote(string message, float distance, int weaponIcon, List<int> weaponMods)
        {
            new DeathRow(message, distance.ToString("F1") + "m", weaponIcon, weaponMods).Run();
        }
        internal Dictionary<uint, int> Prefab2Item = new Dictionary<uint, int>();

        internal const string PANEL_NAME = "DM";

        [Obsolete]
        private void CreateNote(string vName, string iName, float distance, string vColor, string iColor, string weaponName, string[] weaponMods, bool isHeadshot, bool needRenameVictim, bool needRenameInitiator)
        {
            string message = $"<color={iColor}>{(needRenameInitiator ? Instance.GetNpcName(iName) : iName)}</color> убил <color={vColor}>{(needRenameVictim ? Instance.GetNpcName(vName) : vName)}</color>";
            int weaponIcon = 1545779598;
            List<int> weaponModsList = new List<int>(6);
            if (ItemManager.itemDictionaryByName.TryGetValue(weaponName, out ItemDefinition itemDefinition))
                weaponIcon = itemDefinition.itemid;
            if (isHeadshot)
                weaponModsList.Add(996293980);
            foreach (var weaponMod in weaponMods)
            {
                if (ItemManager.itemDictionaryByName.TryGetValue(weaponMod, out ItemDefinition modDefinition))
                    weaponModsList.Add(modDefinition.itemid);
            }

            CreateNote(message, distance, weaponIcon, weaponModsList);
        }

        public class DeathRow
        {
            public string Message { get; private set; }
            public string Distance { get; private set; }
            public int WeaponIcon { get; private set; }
            public List<int> WeaponModsIcons { get; private set; }

            public bool Initialized() => string.IsNullOrEmpty(this.Message) == false;
            public static List<DeathRow> DeathRows = new List<DeathRow>();
            public DeathRow(BasePlayer victim, BasePlayer initiator, HitInfo info)
            {
                FillDistance(victim, initiator);
                FillIcons(info);
                string messageVariable = Instance.GetDeathMessage(info.damageTypes.GetMajorityDamageType());
                if (string.IsNullOrEmpty(messageVariable) == false)
                    this.Message = string.Format(messageVariable, GetNameString(victim), GetNameString(initiator));
            }

            public DeathRow(BasePlayer victim, NPCPlayer initiator, HitInfo info)
            {
                if (Instance.Cfg.CoreConfig.PlayerKilledByNPC == false)
                    return;
                FillDistance(victim, initiator);
                FillIcons(info);
                string messageVariable = Instance.GetDeathMessage(info.damageTypes.GetMajorityDamageType());
                if (string.IsNullOrEmpty(messageVariable) == false)
                    this.Message = string.Format(messageVariable, GetNameString(victim), Instance.Cfg.CoreConfig.RenameNPC ? GetPrefabName(initiator.ShortPrefabName) : initiator.displayName);
            }

            public DeathRow(NPCPlayer victim, BasePlayer initiator, HitInfo info)
            {
                if (Instance.Cfg.CoreConfig.NPC == false)
                    return;
                FillDistance(victim, initiator);
                FillIcons(info);
                string messageVariable = Instance.GetDeathMessage(info.damageTypes.GetMajorityDamageType());
                if (string.IsNullOrEmpty(messageVariable) == false)
                    this.Message = string.Format(messageVariable, Instance.Cfg.CoreConfig.RenameNPC ? GetPrefabName(victim.ShortPrefabName) : victim.displayName, GetNameString(initiator));
            }

            public DeathRow(BaseCombatEntity victim, BasePlayer initiator, HitInfo info)
            {
                if (Instance.Cfg.CoreConfig.Animal == false)
                    return;
                FillDistance(victim, initiator);
                FillIcons(info);
                string messageVariable = Instance.GetDeathMessage(info.damageTypes.GetMajorityDamageType());
                if (string.IsNullOrEmpty(messageVariable) == false)
                    this.Message = string.Format(messageVariable, GetEntityName(victim), GetNameString(initiator));
            }

            public DeathRow(BasePlayer victim, BaseCombatEntity initiator, HitInfo info)
            {
                if (Instance.Cfg.CoreConfig.PlayerKilledByAnimal == false)
                    return;
                FillDistance(victim, initiator);
                FillIcons(info);
                string messageVariable = Instance.GetDeathMessage(info.damageTypes.GetMajorityDamageType());
                if (string.IsNullOrEmpty(messageVariable) == false)
                    this.Message = string.Format(messageVariable, GetNameString(victim), GetEntityName(initiator));
            }
		   		 		  						  	   		  	   		  	 				  		 			  	 		
            public DeathRow(PatrolHelicopter victim, BasePlayer initiator, HitInfo info)
            {
                if (Instance.Cfg.CoreConfig.Helicopter == false)
                    return;
                FillDistance(victim, initiator);
                FillIcons(info);
                string messageVariable = Instance.GetDeathMessage(info.damageTypes.GetMajorityDamageType());
                if (string.IsNullOrEmpty(messageVariable) == false && Instance.GetNpcName(victim.ShortPrefabName) != victim.ShortPrefabName)
                    this.Message = string.Format(messageVariable, GetPrefabName(victim.ShortPrefabName), GetNameString(initiator));
            }

            public DeathRow(BradleyAPC victim, BasePlayer initiator, HitInfo info)
            {
                if (Instance.Cfg.CoreConfig.Bradley == false)
                    return;
                FillDistance(victim, initiator);
                FillIcons(info);
                string messageVariable = Instance.GetDeathMessage(info.damageTypes.GetMajorityDamageType());
                if (string.IsNullOrEmpty(messageVariable) == false && Instance.GetNpcName(victim.ShortPrefabName) != victim.ShortPrefabName)
                    this.Message = string.Format(messageVariable, GetPrefabName(victim.ShortPrefabName), GetNameString(initiator));
            }

            public DeathRow(BasePlayer victim, DecayEntity initiator, HitInfo info)
            {
                if (Instance.Cfg.CoreConfig.PlayerKilledByEnv == false)
                    return;
                FillDistance(victim, initiator);
                FillIcons(info, initiator);
                string messageVariable = Instance.GetDeathMessage(info.damageTypes.GetMajorityDamageType());
                if (string.IsNullOrEmpty(messageVariable) == false && Instance.GetNpcName(initiator.ShortPrefabName) != initiator.ShortPrefabName)
                    this.Message = string.Format(messageVariable, GetNameString(victim), GetPrefabName(initiator.ShortPrefabName));
            }

            public DeathRow(BasePlayer victim, HitInfo info)
            {
                if (Instance.Cfg.CoreConfig.PlayerKilledByEnv == false)
                    return;
                FillDistance(victim, null);
                FillIcons(info, null);
                string messageVariable = Instance.GetDeathMessage(info?.damageTypes?.GetMajorityDamageType() ?? DamageType.Generic);
                if (string.IsNullOrEmpty(messageVariable) == false)
                    this.Message = string.Format(messageVariable, GetNameString(victim), "");
            }

            public DeathRow(string message, string distance, int weaponIcon, List<int> weaponMods)
            {
                this.Message = message;
                this.Distance = distance;
                this.WeaponIcon = weaponIcon;
                this.WeaponModsIcons = weaponMods;
            }

            public void Run()
            {
                if (Initialized())
                {
                    if (DeathRows.Count == Instance.Cfg.UIConfig.Rows)
                        DeathRows.RemoveAt(DeathRows.Count - 1);
                    DeathRows.Insert(0, this);
                    Instance.AddUIRow(this.Message, this.Distance, this.WeaponIcon, this.WeaponModsIcons);
                }
            }

            private void FillDistance(BaseCombatEntity victim, BaseCombatEntity initiator = null)
            {
                if (initiator != null)
                    this.Distance = $"{Vector3.Distance(victim.transform.position, initiator.transform.position).ToString("F1")}m";
                else
                    this.Distance = "0m";
            }

            private void FillIcons(HitInfo info, BaseCombatEntity initiator = null)
            {
                this.WeaponModsIcons = new List<int>(5);
                if (info == null)
                {
                    this.WeaponIcon = 996293980;
                    return;
                }

                if (info.isHeadshot)
                    this.WeaponModsIcons.Add(996293980);
                if (initiator != null)
                {
                    ItemDefinition initiatorDefinition = GetInitiatorDefinition(initiator);
                    if (initiatorDefinition != null)
                    {
                        this.WeaponIcon = initiatorDefinition.itemid;
                        Item attackItem = GetAttackItem(info);
                        if (attackItem == null)
                        {
                            ItemDefinition attackDefinition = GetAttackDefinition(info);
                            if (attackDefinition != null && attackDefinition.itemid != this.WeaponIcon)
                                this.WeaponModsIcons.Add(attackDefinition.itemid);
                        }
                        else
                        {
                            this.WeaponModsIcons.Add(attackItem.info.itemid);
                            this.WeaponModsIcons.AddRange(attackItem.contents.itemList.Select(x => x.info.itemid).ToList());
                        }
                    }
                }
                else
                {
                    Item attackItem = GetAttackItem(info);
                    if (attackItem == null)
                    {
                        ItemDefinition attackDefinition = GetAttackDefinition(info);
                        if (attackDefinition != null)
                        {
                            this.WeaponIcon = attackDefinition.itemid;
                        }
                        else
                        {
                            if (info.isHeadshot == false)
                                this.WeaponIcon = 996293980;
                        }
                    }
                    else
                    {
                        this.WeaponIcon = attackItem.info.itemid;
                        if (attackItem.contents != null)
                            this.WeaponModsIcons.AddRange(attackItem.contents.itemList.Select(x => x.info.itemid).ToList());
                    }
                }
            }

            private Item GetAttackItem(HitInfo info)
            {
                Item attackItem = GetWeaponFromEntity(info.Weapon);
                if (attackItem == null)
                    attackItem = GetWeaponFromEntity(info.WeaponPrefab);
                if (attackItem == null)
                    attackItem = GetWeaponFromEntity(info.ProjectilePrefab?.sourceWeaponPrefab);
                return attackItem;
            }
		   		 		  						  	   		  	   		  	 				  		 			  	 		
            private ItemDefinition GetAttackDefinition(HitInfo info)
            {
                ItemDefinition attackDefinition = GetWeaponFromEntityName(info.Weapon);
                if (attackDefinition == null)
                    attackDefinition = GetWeaponFromEntityName(info.WeaponPrefab);
                if (attackDefinition == null)
                    attackDefinition = GetWeaponFromEntityName(info.ProjectilePrefab?.sourceWeaponPrefab);
                if (attackDefinition == null)
                    attackDefinition = GetWeaponFromEntityName(info.ProjectilePrefab?.sourceProjectilePrefab?.sourceWeaponPrefab);
                return attackDefinition;
            }

            private ItemDefinition GetInitiatorDefinition(BaseCombatEntity entity)
            {
                return GetWeaponFromEntityName(entity);
            }

            private Item GetWeaponFromEntity(BaseEntity attackEntity)
            {
                if (attackEntity == null || attackEntity.GetItem() == null)
                    return null;
                return attackEntity.GetItem();
            }

            private ItemDefinition GetWeaponFromEntityName(BaseEntity attackEntity)
            {
                if (attackEntity == null)
                    return null;
                if (!Instance.Prefab2Item.TryGetValue(attackEntity.prefabID, out int itemID))
                {
                    if (!Instance.PrefabName2Item.TryGetValue(attackEntity.ShortPrefabName, out itemID))
                    {
                        return null;
                    }
                }
		   		 		  						  	   		  	   		  	 				  		 			  	 		
                if (!ItemManager.itemDictionary.TryGetValue(itemID, out ItemDefinition item))
                    return null;
                return item;
            }

            private string GetNameString(BasePlayer player)
            {
                string playerName = player.displayName.ToString();
                if (player.IsNpc == false && Instance.PlayersData.TryGetValue(player.userID, out PlayerData playerData) && playerData.HideMyName)
                    playerName = RandomUsernames.Get(player.userID + (ulong)((long)UnityEngine.Random.Range(0, 100000)));
                return $"<size={Instance.Cfg.UIConfig.NameProperty.Size}><color={Instance.GetColorFor(player)}>{playerName.Substring(0, Math.Min(16, playerName.Length))}</color></size>";
            }

            private string GetEntityName(BaseCombatEntity entity)
            {
                if (entity == null) return "Неизвестно";
                try
                {
                    string bossName = Instance.OctaBoss?.Call("GetBossName", entity) as string;
                    if (!string.IsNullOrEmpty(bossName))
                        return $"<size={Instance.Cfg.UIConfig.NameProperty.Size}><color=#FFD700>★ {bossName}</color></size>";
                }
                catch { }

                var livestock = entity as LivestockAnimal;
                if (livestock != null && !string.IsNullOrEmpty(livestock.AnimalName))
                    return $"<size={Instance.Cfg.UIConfig.NameProperty.Size}><color=#FFD700>★ {livestock.AnimalName}</color></size>";

                return GetPrefabName(entity.ShortPrefabName);
            }

            private string GetPrefabName(string shortPrefabName)
            {
                return $"<size={Instance.Cfg.UIConfig.NameProperty.Size}><color={Instance.Cfg.UIConfig.NameProperty.Color}>{Instance.GetNpcName(shortPrefabName)}</color></size>";
            }
        }
        public class PlayerData
        {
            public double LastConnectTime = 0;
            public bool HideMessages = false;
            public bool HideMyName = false;
        }
        private bool SwitchHideMyName(BasePlayer player)
        {
            if (PlayersData.TryGetValue(player.userID, out PlayerData playerData))
            {
                playerData.HideMyName = !playerData.HideMyName;
            }
            else
            {
                PlayersData[player.userID] = new PlayerData()
                {
                    HideMessages = false,
                    HideMyName = true,
                    LastConnectTime = new DateTimeOffset(DateTime.Now).ToUnixTimeMilliseconds()
                };
                playerData = PlayersData[player.userID];
            }

            return playerData.HideMyName;
        }

        private void OnServerSave()
        {
            Interface.Oxide.DataFileSystem.WriteObject("OctaFeed\\PlayersData", PlayersData);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            AddTemplateUI(player);
            AddUIRows(player);
            if (PlayersData.TryGetValue(player.userID, out PlayerData playerData))
                playerData.LastConnectTime = new DateTimeOffset(DateTime.Now).ToUnixTimeMilliseconds();
            if (Instance.Cfg.UIConfig.EnableUISettings && Instance.Cfg.UIConfig.EnableUISettingsButton)
                AddUISettings(player);
        }

        private void AddUISettingsWidget(BasePlayer player)
        {
            CuiElementContainer container = new CuiElementContainer();
            container.Add(new CuiElement { Name = "DMUISettings", Parent = "DMUI", Components = { new CuiRectTransformComponent { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = $"{5} {-43}", OffsetMax = $"{180} {5}" }, new CuiImageComponent { Color = "1 0.95 0.85 0.2", Material = "assets/icons/iconmaterial.mat", } } });
            Instance.PlayersData.TryGetValue(player.userID, out PlayerData playerData);
            container.Add(new CuiButton { Button = { Command = "octafeed hidemyname", Color = "0 0 0 0", }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "5 3", OffsetMax = "170 23" }, Text = { Text = playerData != null && playerData.HideMyName ? "☑ Скрывать мой ник" : "☐ Скрывать мой ник", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.8", Font = "robotocondensed-bold.ttf", FontSize = 11 }, }, "DMUISettings", "DMUISettings.HideMyName");
            container.Add(new CuiButton { Button = { Command = "octafeed hidemessages", Color = "0 0 0 0", }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "5 23", OffsetMax = "170 43" }, Text = { Text = playerData != null && playerData.HideMessages ? "☑ Скрывать киллфид" : "☐ Скрывать киллфид", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.8", Font = "robotocondensed-bold.ttf", FontSize = 11 }, }, "DMUISettings", "DMUISettings.HideMessages");
            CuiHelper.AddUi(player, container);
        }
		   		 		  						  	   		  	   		  	 				  		 			  	 		
        private bool IsPlayerNameHidden(BasePlayer player)
        {
            return PlayersData.TryGetValue(player.userID, out PlayerData playerData) && playerData.HideMyName;
        }
        internal Dictionary<ulong, PlayerData> PlayersData = new Dictionary<ulong, PlayerData>();

        private void OnPlayerDisconnected(BasePlayer player)
        {
        }

        private void UpdateUISettingsButton(BasePlayer player, string command)
        {
            CuiElementContainer container = new CuiElementContainer();
            container.Add(new CuiButton { Button = { Command = $"octafeed {command}", Color = "0 0 0 0", }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", }, Text = { Text = "", }, }, "DMUI", "DMUI.Button", "DMUI.Button");
            CuiHelper.AddUi(player, container);
        }

        private bool IsPlayerHideMessages(BasePlayer player)
        {
            return PlayersData.TryGetValue(player.userID, out PlayerData playerData) && playerData.HideMessages;
        }

        private void SetDefaultConfig()
        {
            Cfg = GetDefaultConfig();
            Config.WriteObject(Cfg, true);
        }
        internal const string HUD = "Hud";
        internal string GetDeathMessage(DamageType damageType)
        {
            if (Cfg.CoreConfig.Messages.TryGetValue(damageType.ToString(), out List<string> variables))
                return variables[UnityEngine.Random.Range(0, variables.Count)];
            return null;
        }

        [ConsoleCommand("octafeed")]
        private void CMDMain(ConsoleSystem.Arg arg)
        {
            if (Instance.Cfg.UIConfig.EnableUISettings == false || Instance.Cfg.UIConfig.EnableUISettingsButton == false)
                return;
            BasePlayer player = arg.Player();
            if (player != null && arg.HasArgs())
            {
                string command = arg.Args[0].ToString();
                switch (command)
                {
                    case "show":
                    {
                        UpdateUISettingsButton(player, "hide");
                        AddUISettingsWidget(player);
                        break;
                    }

                    case "hide":
                    {
                        CuiHelper.DestroyUi(player, "DMUISettings");
                        UpdateUISettingsButton(player, "show");
                        break;
                    }
		   		 		  						  	   		  	   		  	 				  		 			  	 		
                    case "hidemyname":
                    {
                        SwitchHideMyName(player);
                        UpdateUISettingsWidget(player);
                        break;
                    }

                    case "hidemessages":
                    {
                        SwitchHideMessages(player);
                        UpdateUISettingsWidget(player);
                        break;
                    }
                }
            }
        }
        private void OnEntityDeath(BaseCombatEntity victim, HitInfo info)
        {
            if (victim == null || victim.net == null)
                return;

            DeathRow outputRow = null;

            // Если жертва — живой игрок, а смертельный урон пришел без инициатора или как урон окружения
            // (Drowned, Bleeding, Fall, Cold), проверяем последний урон от игрока/босса за 8 секунд
            if (victim is BasePlayer vp && vp.IsNonNpcPlayer())
            {
                bool isEnvOrSelf = info == null || info.Initiator == null || info.Initiator == victim;
                if (!isEnvOrSelf && info.damageTypes != null)
                {
                    var maj = info.damageTypes.GetMajorityDamageType();
                    if (maj == DamageType.Drowned || maj == DamageType.Bleeding || maj == DamageType.Cold)
                        isEnvOrSelf = true;
                }

                if (isEnvOrSelf)
                {
                    RecentAttack lastAttack;
                    if (LastAttacks.TryGetValue(vp.userID, out lastAttack) && lastAttack != null &&
                        lastAttack.Hit?.Initiator != null && lastAttack.Hit.Initiator != victim)
                    {
                        if (UnityEngine.Time.realtimeSinceStartup - lastAttack.Time <= 8f)
                        {
                            info = lastAttack.Hit;
                        }
                    }
                }
                LastAttacks.Remove(vp.userID);
            }

            if (info != null && info.damageTypes != null && info.Initiator is BaseCombatEntity initiator &&
                initiator.net != null && initiator != victim)
            {
                // Жертва — живой игрок
                if (victim is BasePlayer victimPlayer && victimPlayer.IsNonNpcPlayer())
                {
                    if (initiator is BasePlayer initPlayer)
                    {
                        if (initPlayer.IsNpcPlayer())
                            outputRow = new DeathRow(victimPlayer, initPlayer as NPCPlayer, info);
                        else
                            outputRow = new DeathRow(victimPlayer, initPlayer, info);
                    }
                    else if (initiator is PatrolHelicopter || initiator is BradleyAPC)
                    {
                        outputRow = new DeathRow(victimPlayer, initiator, info);
                    }
                    else
                    {
                        // Животное / босс / турель / ловушка убили игрока!
                        outputRow = new DeathRow(victimPlayer, initiator, info);
                    }
                }
                // Жертва — животное / босс / NPC / вертолет / танк, а убийца — живой игрок
                else if (initiator is BasePlayer killerPlayer && killerPlayer.IsNonNpcPlayer())
                {
                    if (victim is NPCPlayer npcVictim)
                        outputRow = new DeathRow(npcVictim, killerPlayer, info);
                    else if (victim is PatrolHelicopter heli)
                        outputRow = new DeathRow(heli, killerPlayer, info);
                    else if (victim is BradleyAPC bradley)
                        outputRow = new DeathRow(bradley, killerPlayer, info);
                    else
                        // Игрок убил животное / босса!
                        outputRow = new DeathRow(victim, killerPlayer, info);
                }
            }

            // Чистая смерть без инициатора (суицид, падение, голод, чистое утопление вне боя)
            if (outputRow == null && victim is BasePlayer player && player.IsNonNpcPlayer())
            {
                outputRow = new DeathRow(player, info);
            }

            outputRow?.Run();
        }
        public OctaFeedConfig GetDefaultConfig()
        {
            return new OctaFeedConfig
            {
                CoreConfig = new OctaFeedConfig.CoreConfiguration()
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
                    NameProperty = new OctaFeedConfig.DMTextProperty()
                    {
                        Color = "#D3D3D3",
                        Size = 11,
                    },
                    TextProperty = new OctaFeedConfig.DMTextProperty()
                    {
                        Color = "#F0F0F0",
                        Size = 10,
                    },
                    DistanceProperty = new OctaFeedConfig.DMTextProperty()
                    {
                        Color = "#ffffff",
                        Size = 11,
                    },
                    Colors = new Dictionary<string, string>()
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
                    EnableUISettings = true,
                    EnableUISettingsButton = true
                },
            };
        }
        private void OnServerInitialized()
        {
            Instance = this;
            PlayersData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerData>>("OctaFeed\\PlayersData");
            List<ulong> tempData = new List<ulong>();
            foreach (var playerData in PlayersData)
            {
                if (playerData.Value.LastConnectTime + 604800 < new DateTimeOffset(DateTime.Now).ToUnixTimeMilliseconds())
                    tempData.Add(playerData.Key);
            }

            foreach (var userID in tempData)
                PlayersData.Remove(userID);
            foreach (var groupColor in Cfg.UIConfig.Colors)
            {
                permission.RegisterPermission(groupColor.Key, this);
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                RemoveUITemplate(player);
                AddTemplateUI(player);
                RemoveUISettings(player);
                if (Instance.Cfg.UIConfig.EnableUISettings && Instance.Cfg.UIConfig.EnableUISettingsButton)
                    AddUISettings(player);
            }

            foreach (ItemDefinition itemDefinition in ItemManager.GetItemDefinitions())
            {
                Item cacheItem = ItemManager.CreateByName(itemDefinition.shortname, 1, 0);
                BaseEntity entity = cacheItem.GetHeldEntity();
                if (entity != null)
                {
                    Prefab2Item[entity.prefabID] = itemDefinition.itemid;
                    PrefabName2Item[entity.ShortPrefabName] = itemDefinition.itemid;
                    PrefabName2Item[entity.ShortPrefabName.Replace(".entity", ".deployed")] = itemDefinition.itemid;
                    PrefabName2Item[entity.ShortPrefabName + ".deployed"] = itemDefinition.itemid;
                }

                if (itemDefinition.HasComponent<ItemModDeployable>())
                {
                    string deployablePrefab = itemDefinition.GetComponent<ItemModDeployable>()?.entityPrefab?.resourcePath;
                    if (string.IsNullOrEmpty(deployablePrefab) == false)
                    {
                        string shortPrefabName = GameManager.server.FindPrefab(deployablePrefab)?.GetComponent<BaseEntity>()?.ShortPrefabName;
                        if (string.IsNullOrEmpty(shortPrefabName) == false)
                        {
                            PrefabName2Item[shortPrefabName] = itemDefinition.itemid;
                        }
                    }
                }

                cacheItem.Remove();
            }

            timer.Every(1.0f, () =>
            {
                try
                {
                    RemoveLastUIRow();
                }
                catch (Exception ex)
                {
                    PrintError(ex.ToString());
                }
            });
        }

        private void UpdateUISettingsWidget(BasePlayer player)
        {
            CuiElementContainer container = new CuiElementContainer();
            Instance.PlayersData.TryGetValue(player.userID, out PlayerData playerData);
            container.Add(new CuiButton { Button = { Command = "octafeed hidemyname", Color = "0 0 0 0", }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "5 3", OffsetMax = "170 23" }, Text = { Text = playerData != null && playerData.HideMyName ? "☑ Скрывать мой ник" : "☐ Скрывать мой ник", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.8", Font = "robotocondensed-bold.ttf", FontSize = 11 }, }, "DMUISettings", "DMUISettings.HideMyName", "DMUISettings.HideMyName");
            container.Add(new CuiButton { Button = { Command = "octafeed hidemessages", Color = "0 0 0 0", }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "5 23", OffsetMax = "170 43" }, Text = { Text = playerData != null && playerData.HideMessages ? "☑ Скрывать киллфид" : "☐ Скрывать киллфид", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.8", Font = "robotocondensed-bold.ttf", FontSize = 11 }, }, "DMUISettings", "DMUISettings.HideMessages", "DMUISettings.HideMessages");
            CuiHelper.AddUi(player, container);
        }

        internal string GetNpcName(string shortPrefabName)
        {
            if (Cfg.CoreConfig.Names.TryGetValue(shortPrefabName, out string name))
                return name;
            return shortPrefabName;
        }

        private void RemoveLastUIRow()
        {
            if (DeathRow.DeathRows.Count == 0)
                return;
            if ((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - LAST_ROW_MODIFY_TIME < Instance.Cfg.UIConfig.Time)
                return;
            LAST_ROW_MODIFY_TIME = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            int lastVisibleRowId = (LAST_ROW_ID + (DeathRow.DeathRows.Count - 1) + Instance.Cfg.UIConfig.Rows) % Instance.Cfg.UIConfig.Rows;
            string jsonContainer = $"[{{\"name\":\"DMR{lastVisibleRowId}\",\"parent\":\"DM\",\"components\":[{{\"type\":\"RectTransform\",\"offsetmin\":\"0 100\",\"offsetmax\":\"0 100\"}}],\"update\":true}}]";
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (Instance.PlayersData.TryGetValue(player.userID, out PlayerData playerData))
                {
                    if (playerData.HideMessages)
                        continue;
                }
		   		 		  						  	   		  	   		  	 				  		 			  	 		
                CuiHelper.AddUi(player, jsonContainer);
            }
		   		 		  						  	   		  	   		  	 				  		 			  	 		
            DeathRow.DeathRows.RemoveAt(DeathRow.DeathRows.Count - 1);
        }

        private void OnPatrolHelicopterKill(PatrolHelicopter victim, HitInfo info)
        {
            if (victim == null)
                return;
            if (info == null)
                return;
            if (info.damageTypes == null)
                return;
            if (info.InitiatorPlayer == null)
                return;
            new DeathRow(victim, info.InitiatorPlayer, info).Run();
        }
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
                [JsonProperty("[4] Время, через которое пропадёт последняя строка")]
                public float Time = 5;
                [JsonProperty("[5.1] Включить обводку текста")]
                public bool Outline = true;
                [JsonProperty("[5.2] Включить обводку изображений")]
                public bool ImageOutline = true;
                [JsonProperty("[6] Цвет задней панели убийства")]
                public string RowColor = "#00000000";
                [JsonProperty("[7] Цвет панели с дистанцией")]
                public string DistanceColor = "#FFFFFFC0";
                [JsonProperty("[8.1] Отступ сверху в пикселях (при разрешении 1360x768)")]
                public int OffsetY = 0;
                [JsonProperty("[8.2] Отступ сбоку в пикселях (при разрешении 1360x768)")]
                public int OffsetX = 0;
                [JsonProperty("[8.3] Ширина строки киллфида в пикселях")]
                public int RowWidth = 480;
                [JsonProperty("[9.1] Включить настройку плагина")]
                public bool EnableUISettings = true;
                [JsonProperty("[9.2] Включить кнопку настроек плагина")]
                public bool EnableUISettingsButton = true;
            }
            [JsonProperty("[2] Настройки UI")]
            public UIConfiguration UIConfig = new UIConfiguration();
        }

        private void SwitchPlayerNameHidden(BasePlayer player)
        {
            SwitchHideMyName(player);
        }
		   		 		  						  	   		  	   		  	 				  		 			  	 		
        public OctaFeedConfig Cfg;

        private void Init()
        {
            Cfg = Config.ReadObject<OctaFeedConfig>();
            if (Cfg.CoreConfig == null || Cfg.UIConfig == null || Cfg.CoreConfig.Names == null || Cfg.CoreConfig.Names.Count == 0 || Cfg.CoreConfig.Messages == null || Cfg.CoreConfig.Messages.Count == 0 || Cfg.UIConfig.Colors == null)
            {
                SetDefaultConfig();
                return;
            }

            Config.WriteObject(Cfg, true);
        }
        internal double LAST_ROW_MODIFY_TIME = 0;

        // PluginReference залипает на старой инстанции после перезагрузки OctVIP —
        // ищем живой плагин по имени при каждом обращении (киллы редкие, это дёшево)
        private Plugin FindOctVIP()
        {
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
            {
                if (p.Name == "OctVIP" && p.IsLoaded)
                    return p;
            }
            return null;
        }

        internal static OctaFeed Instance;
        internal string TEMPLATE_JSON_CONTAINER = string.Empty;

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(GetDefaultConfig(), true);
        }

        private void AddTemplateUI(BasePlayer player)
        {
            if (Instance.PlayersData.TryGetValue(player.userID, out PlayerData playerData))
            {
                if (playerData.HideMessages)
                    return;
            }

            if (string.IsNullOrEmpty(TEMPLATE_JSON_CONTAINER))
                TEMPLATE_JSON_CONTAINER = CreateUITemplate();
            CuiHelper.AddUi(player, TEMPLATE_JSON_CONTAINER);
        }
        internal Dictionary<string, List<string>> Messages = new Dictionary<string, List<string>>()
        {
            ["AntiVehicle"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Arrow"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Bite"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Bleeding"] = new List<string>()
            {
                "{0} умер от кровотечения"
            },
            ["Blunt"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Bullet"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Cold"] = new List<string>()
            {
                "{0} замерз"
            },
            ["ColdExposure"] = new List<string>()
            {
                "{0} замерз"
            },
            ["Collision"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Decay"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Drowned"] = new List<string>()
            {
                "{0} утонул"
            },
            ["ElectricShock"] = new List<string>()
            {
                "{0} заискрился"
            },
            ["Explosion"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Fall"] = new List<string>()
            {
                "{0} упал с высоты"
            },
            ["Fun_Water"] = new List<string>()
            {
                "{0} погиб"
            },
            ["Generic"] = new List<string>()
            {
                "{0} погиб"
            },
            ["Heat"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Hunger"] = new List<string>()
            {
                "{0} умер от голода"
            },
            ["Poison"] = new List<string>()
            {
                "{0} умер от отравления"
            },
            ["Radiation"] = new List<string>()
            {
                "{0} умер от радиации"
            },
            ["RadiationExposure"] = new List<string>()
            {
                "{0} умер от радиации"
            },
            ["Slash"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Stab"] = new List<string>()
            {
                "{1} убил {0}"
            },
            ["Suicide"] = new List<string>()
            {
                "{0} убил себя"
            },
            ["Thirst"] = new List<string>()
            {
                "{0} умер от жажды"
            },
        };

        internal string GetColorFor(BasePlayer player)
        {
            foreach (var color in Instance.Cfg.UIConfig.Colors)
            {
                if (Instance.permission.UserHasPermission(player.UserIDString, color.Key))
                {
                    return color.Value;
                }
            }

            // цвет ника из OctVIP, если игрок с VIP-уровнем
            // userID = EncryptedValue<ulong> — в Call обязателен явный каст
            Plugin vip = FindOctVIP();
            if (vip != null)
            {
                ulong uid = (ulong)player.userID;
                int level = vip.Call<int>("GetVipLevel", uid);
                if (level > 0)
                    return vip.Call<string>("GetVipColor", uid);
            }

            return Instance.Cfg.UIConfig.NameProperty.Color;
        }
        internal int LAST_ROW_ID = 0;
    }
}
