// OctaCraft — скорость крафта по VIP-уровню игрока.
// Перекраска CraftingController (Whispers88 3.3.4, авторы базы Nivex & Mughisi):
// сохранены блокировка предметов, уровни верстаков, скины по умолчанию,
// мгновенная выдача пачки, проверка места в инвентаре.
//   Формула: время крафта = ванилла × GlobalRate% × LevelBonus%.
//   Уровни: octacraft.t1..t6 ИЛИ OctVIP GetVipLevel. Instant — выдача пачки сразу.
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using Facepunch;

namespace Oxide.Plugins
{
    [Info("OctaCraft", "Octavian", "1.0.0")]
    [Description("VIP-tiered crafting speed and crafting rules control")]

    public class OctaCraft : RustPlugin
    {
        #region Config
        private Configuration config;
        private static Dictionary<string, CraftingData> defaultsetup = new Dictionary<string, CraftingData>();
        public struct CraftingData
        {
            public bool canCraft;
            public bool canResearch;
            public bool useCrafteRateMultiplier;
            public float craftTime;
            public int workbenchLevel;
            public ulong defaultskinid;
        }

        public class LevelData
        {
            // процент от БАЗОВОГО времени (GlobalRate уже применён к bp.time):
            // итог = ванилла × GlobalRate% × BonusPercent%
            [JsonProperty("Процент от базового времени")]
            public float BonusPercent = 100f;

            // мгновенный крафт — предметы выдаются пачкой сразу
            [JsonProperty("Мгновенный крафт")]
            public bool Instant = false;
        }

        public class Configuration
        {
            [JsonProperty("Базовый процент времени крафта для всех (100 = ванилла)")]
            public float CraftingRate = 50;

            [JsonProperty("Уровни скорости (0=default 1=bronze 2=silver 3=gold 4=brilliant 5=moderator 6=admin)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<int, LevelData> Levels = new Dictionary<int, LevelData>
            {
                [1] = new LevelData { BonusPercent = 80f },   // bronze:    итог 40% ваниллы
                [2] = new LevelData { BonusPercent = 60f },   // silver:    итог 30%
                [3] = new LevelData { BonusPercent = 40f },   // gold:      итог 20%
                [4] = new LevelData { BonusPercent = 25f },   // brilliant: итог 12.5%
                [5] = new LevelData { BonusPercent = 15f },   // moderator: итог 7.5%
                [6] = new LevelData { Instant = true },       // vip_admin: мгновенно
            };

            [JsonProperty("Save commands to config (save config changes via command to the configuration)")]
            public bool SaveCommands = true;

            [JsonProperty("Simple Mode (disables: instant bulk craft, skin options and full inventory checks for better performance)")]
            public bool SimpleMode = false;

            [JsonProperty("Allow crafting when inventory is full")]
            public bool FullInventory = false;

            [JsonProperty("Complete crafting on server shut down")]
            public bool CompleteCrafting = false;

            [JsonProperty("Craft items with random skins if not already skinned")]
            public bool RandomSkins = false;

            [JsonProperty("Show Crafting Notes")]
            public bool ShowCraftNotes = false;

            [JsonProperty("Advanced Crafting Options", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, CraftingData> CraftingOptions = new Dictionary<string, CraftingData>();

            [JsonProperty("Version")]
            public float Version = 100;

            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonConvert.DeserializeObject<Dictionary<string, object>>(ToJson());
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    throw new JsonException();
                }
            }
            catch
            {
                Puts($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Puts($"Configuration changes saved to {Name}.json");
            Config.WriteObject(config, true);
        }

        #endregion Config

        #region Init
        private const string perminstant = "octacraft.instant";
        private const string permblockitems = "octacraft.blockitems";
        private const string permitemrate = "octacraft.itemrate";
        private const string permcraftingrate = "octacraft.craftingrate";
        private const string permsetbenchlvl = "octacraft.setbenchlvl";
        private const string permsetskins = "octacraft.setskins";

        // пермишены уровней скорости — выдаются группам vip_*; OctVIP в приоритете
        private static readonly string[] PermLevel =
        {
            "", "octacraft.t1", "octacraft.t2", "octacraft.t3",
            "octacraft.t4", "octacraft.t5", "octacraft.t6"
        };

        private List<string> permissions = new List<string> { perminstant, permblockitems, permitemrate, permcraftingrate, permsetbenchlvl, permsetskins };
        private List<string> commands = new List<string> { nameof(CommandCraftingRate), nameof(CommandCraftTime), nameof(CommandBlockItem), nameof(CommandUnblockItem), nameof(CommandSetDefaultSkin), nameof(CommandWorkbenchLVL) };
        private void OnServerInitialized()
        {
            //Set default stats for unload
            ItemManager.bpList.ForEach(bp => defaultsetup[bp.name] = new CraftingData() { canCraft = bp.userCraftable, canResearch = bp.isResearchable, craftTime = bp.time, workbenchLevel = bp.workbenchLevelRequired });

            //register permissions
            permissions.ForEach(perm => permission.RegisterPermission(perm, this));
            foreach (var perm in PermLevel)
                if (!string.IsNullOrEmpty(perm)) permission.RegisterPermission(perm, this);
            //register commands
            commands.ForEach(command => AddLocalizedCommand(command));

            if (config.SimpleMode)
            {
                Unsubscribe("OnItemCraft");
                Unsubscribe("OnItemCraftFinished");
                Unsubscribe("OnItemCraftCancelled");
            }
            ItemManager.bpList.ForEach(item => {
                if (!config.CraftingOptions.ContainsKey(item.targetItem.shortname))
                    config.CraftingOptions.Add(item.targetItem.shortname, new CraftingData() { craftTime = item.time, workbenchLevel = item.workbenchLevelRequired, canCraft = item.userCraftable, canResearch = item.isResearchable });
            });

            SaveConfig();
            UpdateCraftingRate();
        }

        private void Unload()
        {
            //Reset to defaults
            foreach (var bp in ItemManager.bpList)
            {
                CraftingData craftingData;
                if (!defaultsetup.TryGetValue(bp.name, out craftingData))
                    continue;

                bp.time = craftingData.craftTime;
                bp.workbenchLevelRequired = craftingData.workbenchLevel;
                bp.userCraftable = craftingData.canCraft;
                bp.isResearchable = craftingData.canResearch;
            }
        }

        #endregion Init

        #region VIP-уровни
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
        private int ResolveLevel(BasePlayer player)
        {
            if (player == null) return 0;
            var userId = (ulong)player.userID;

            var vip = FindOctaPlugin("OctVIP");
            if (vip != null)
            {
                var lvl = vip.Call<int>("GetVipLevel", userId);
                if (lvl > 0) return lvl;
            }

            var idStr = player.UserIDString;
            for (var i = PermLevel.Length - 1; i >= 1; i--)
                if (permission.UserHasPermission(idStr, PermLevel[i])) return i;
            return 0;
        }
        #endregion VIP-уровни

        #region Localization
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoInvSpace"] = "You don't have enough room to craft this item!",
                ["NoPerms"] = "You don't have permission to use this command.",
                ["CannotFindItem"] = "Cannot find item {0}.",
                ["ItemBlocked"] = "{0} has been blocked from crafting.",
                ["ItemUnblocked"] = "{0} has been unblocked from crafting.",
                ["NeedsAdvancedOptions"] = "You need to enable advanced crafting options in your config to use this.",
                ["WrongNumberInput"] = "Your input needs to be a number.",
                ["ItemCraftTimeSet"] = "{0} craft time set to {1} seconds",
                ["WorkbenchLevelSet"] = "{0} workbench level set to {1}",
                ["CurrentCraftinRate"] = "The current crafting rate is {0}%",
                ["CraftingRateUpdated"] = "The crafting rate was updated to {0}%",
                ["CraftTime2Args"] = "This command needs two arguments in the format /crafttime item.shortname timetocraft",
                ["BlockItem1Args"] = "This command needs one argument in the format /blockitem item.shortname",
                ["UnblockItem1Args"] = "This command needs one argument in the format /unblockitem item.shortname",
                ["WorckBenchLvl2Args"] = "This command needs two arguments in the format /benchlvl item.shortname workbenchlvl",
                ["BenchLevelInput"] = "The work bench level must be between 0 and 3",
                ["SetSkin2Args"] = "This command needs two arguments in the format /setcraftskin item.shortname skinworkshopid",
                ["SkinSet"] = "The default skin for {0} was set to {1}",
                ["CraftTimeCheck"] = "The craft time of this item is {0}",
                //Commands
                ["CommandCraftingRate"] = "craftrate",
                ["CommandCraftTime"] = "crafttime",
                ["CommandBlockItem"] = "blockitem",
                ["CommandUnblockItem"] = "unblockitem",
                ["CommandWorkbenchLVL"] = "benchlvl",
                ["CommandSetDefaultSkin"] = "setcraftskin"

            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoInvSpace"] = "Недостаточно места в инвентаре для этого предмета!",
                ["NoPerms"] = "Нет прав на эту команду.",
                ["CannotFindItem"] = "Предмет {0} не найден.",
                ["ItemBlocked"] = "{0} — крафт заблокирован.",
                ["ItemUnblocked"] = "{0} — крафт разблокирован.",
                ["NeedsAdvancedOptions"] = "Для этого нужно включить расширенные опции крафта в конфиге.",
                ["WrongNumberInput"] = "Значение должно быть числом.",
                ["ItemCraftTimeSet"] = "{0}: время крафта установлено на {1} сек",
                ["WorkbenchLevelSet"] = "{0}: уровень верстака установлен на {1}",
                ["CurrentCraftinRate"] = "Текущая скорость крафта: {0}%",
                ["CraftingRateUpdated"] = "Скорость крафта обновлена: {0}%",
                ["CraftTime2Args"] = "Формат: /crafttime item.shortname время_крафта",
                ["BlockItem1Args"] = "Формат: /blockitem item.shortname",
                ["UnblockItem1Args"] = "Формат: /unblockitem item.shortname",
                ["WorckBenchLvl2Args"] = "Формат: /benchlvl item.shortname уровень_верстака",
                ["BenchLevelInput"] = "Уровень верстака должен быть от 0 до 3",
                ["SetSkin2Args"] = "Формат: /setcraftskin item.shortname id_скина_workshop",
                ["SkinSet"] = "Скин по умолчанию для {0}: {1}",
                ["CraftTimeCheck"] = "Время крафта предмета: {0}",
                //Commands
                ["CommandCraftingRate"] = "craftrate",
                ["CommandCraftTime"] = "crafttime",
                ["CommandBlockItem"] = "blockitem",
                ["CommandUnblockItem"] = "unblockitem",
                ["CommandWorkbenchLVL"] = "benchlvl",
                ["CommandSetDefaultSkin"] = "setcraftskin"

            }, this, "ru");
        }

        #endregion Localization

        #region Commands
        private void CommandCraftingRate(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permitemrate))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            int craftingrate;
            if (args.Length == 0)
            {
                Message(iplayer, "CurrentCraftinRate", config.CraftingRate);
                return;
            }
            if (!int.TryParse(args[0], out craftingrate))
            {
                Message(iplayer, "WrongNumberInput");
                return;
            }
            if (craftingrate < 0) craftingrate = 0;
            config.CraftingRate = craftingrate;
            UpdateCraftingRate();
            Message(iplayer, "CraftingRateUpdated", config.CraftingRate);
            if (config.SaveCommands) SaveConfig();

        }

        private void CommandCraftTime(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permitemrate))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length == 1)
            {
                var itemcheck = FindItem(args[0]);
                if (itemcheck)
                {
                    Message(iplayer, "CraftTimeCheck", itemcheck.Blueprint.time);
                    return;
                }
            }
            if (args.Length < 2)
            {
                Message(iplayer, "CraftTime2Args");
                return;
            }
            var setitem = FindItem(args[0]);
            if (!setitem)
            {
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            if (args[1].ToLower() == "default")
            {
                if (!config.CraftingOptions.TryGetValue(setitem.shortname, out CraftingData craftingdata))
                    return;

                craftingdata.useCrafteRateMultiplier = true;
                craftingdata.craftTime = defaultsetup[setitem.Blueprint.name].craftTime * (config.CraftingRate / 100);

                Message(iplayer, "ItemCraftTimeSet", setitem.shortname, (setitem.Blueprint.time).ToString());
                if (config.SaveCommands) SaveConfig();
                return;
            }
            int crafttime;
            if (!int.TryParse(args[1], out crafttime))
            {
                Message(iplayer, "WrongNumberInput");
                return;
            }
            if (!config.CraftingOptions.TryGetValue(setitem.shortname, out CraftingData craftingdata1))
                return;

            craftingdata1.craftTime = crafttime;
            craftingdata1.useCrafteRateMultiplier = false;

            ItemBlueprint bp = ItemManager.itemDictionaryByName[setitem.shortname].Blueprint;
            bp.time = crafttime;
            Message(iplayer, "ItemCraftTimeSet", setitem.shortname, crafttime.ToString());
            if (config.SaveCommands) SaveConfig();
        }

        private void CommandBlockItem(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permblockitems))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length < 1)
            {
                Message(iplayer, "BlockItem1Args");
                return;
            }
            var blockitem = FindItem(args[0]);
            if (!blockitem)
            {
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            if (config.CraftingOptions.TryGetValue(blockitem.shortname, out CraftingData craftingdata))
            {
                craftingdata.canCraft = false;
                craftingdata.canResearch = false;
            }
            ItemBlueprint bp = ItemManager.itemDictionaryByName[blockitem.shortname].Blueprint;
            bp.userCraftable = false;
            bp.isResearchable = false;
            Message(iplayer, "ItemBlocked", blockitem.shortname);
            if (config.SaveCommands) SaveConfig();
        }

        private void CommandUnblockItem(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permblockitems))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length < 1)
            {
                Message(iplayer, "UnblockItem1Args");
                return;
            }
            var blockitem = FindItem(args[0]);
            if (!blockitem)
            {
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            if (!config.CraftingOptions.TryGetValue(blockitem.shortname, out CraftingData craftingdata))
                return;
            craftingdata.canCraft = true;
            craftingdata.canResearch = true;
            ItemBlueprint bp = ItemManager.itemDictionaryByName[blockitem.shortname].Blueprint;
            bp.userCraftable = true;
            bp.isResearchable = true;
            Message(iplayer, "ItemUnblocked", blockitem.shortname);
            if (config.SaveCommands) SaveConfig();
        }
        private void CommandWorkbenchLVL(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permsetbenchlvl))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length < 2)
            {
                Message(iplayer, "WorckBenchLvl2Args");
                return;
            }
            var item = FindItem(args[0]);
            if (!item)
            {
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            int benchlvl;
            if (!int.TryParse(args[1], out benchlvl))
            {
                Message(iplayer, "WrongNumberInput");
                return;
            }
            if (benchlvl < 0 || benchlvl > 3)
            {
                Message(iplayer, "BenchLevelInput");
                return;
            }
            if (!config.CraftingOptions.TryGetValue(item.shortname, out CraftingData craftingdata))
                return;
            craftingdata.workbenchLevel = benchlvl;
            ItemBlueprint bp = ItemManager.itemDictionaryByName[item.shortname].Blueprint;
            bp.workbenchLevelRequired = benchlvl;
            Message(iplayer, "WorkbenchLevelSet", item.shortname, benchlvl.ToString());
            if (config.SaveCommands) SaveConfig();
        }
        private void CommandSetDefaultSkin(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permsetskins))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length < 2)
            {
                Message(iplayer, "SetSkin2Args");
                return;
            }
            var setitem = FindItem(args[0]);
            if (!setitem)
            {
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            ulong skinid;
            if (!ulong.TryParse(args[1], out skinid))
            {
                Message(iplayer, "WrongNumberInput", args);
                return;
            }
            if (!config.CraftingOptions.TryGetValue(setitem.shortname, out CraftingData craftingdata))
                return;
            craftingdata.defaultskinid = skinid;
            Message(iplayer, "SkinSet", setitem.shortname, skinid.ToString());
            if (config.SaveCommands) SaveConfig();
        }
        #endregion Commands

        #region Methods
        private void UpdateCraftingRate()
        {
            foreach (var bp in ItemManager.bpList)
            {
                CraftingData data;
                if (!config.CraftingOptions.TryGetValue(bp.targetItem.shortname, out data)) continue;
                bp.userCraftable = data.canCraft;
                bp.isResearchable = data.canResearch;
                if (config.CraftingRate == 0f)
                    bp.time = 0f;
                else if (!data.useCrafteRateMultiplier)
                    bp.time = data.craftTime;
                else
                    bp.time *= (float)(config.CraftingRate / 100);

                if (data.workbenchLevel > 4) data.workbenchLevel = 3;
                if (data.workbenchLevel >= 0)
                    bp.workbenchLevelRequired = data.workbenchLevel;
            }
        }

        private void InstantBulkCraft(BasePlayer player, ItemCraftTask task, ItemDefinition item, List<int> stacks, int craftSkin, ulong skin)
        {
            if (skin == 0uL && craftSkin != 0)
            {
                skin = ItemDefinition.FindSkin(item.itemid, craftSkin);
            }
            foreach (var stack in stacks)
            {
                var itemtogive = ItemManager.Create(item, stack, craftSkin != 0 && skin == 0uL ? (ulong)craftSkin : skin);
                itemtogive.OnVirginSpawn(player);
                itemtogive.SetItemOwnership(player, ItemOwnershipPhrases.CraftedPhrase);
                var held = itemtogive.GetHeldEntity();
                if (held != null)
                {
                    held.skinID = skin == 0uL ? (ulong)craftSkin : skin;
                    held.SendNetworkUpdate();
                }
                player.GiveItem(itemtogive);
                //if (config.ShowCraftNotes) player.Command(string.Concat(new object[] { "note.inv ", item.itemid, " ", stack }), new object[0]);
                Interface.CallHook("OnItemCraftFinished", task, itemtogive, player.inventory.crafting);
            }
        }

        private static void CompleteCrafting(BasePlayer player)
        {
            if (player.inventory.crafting.queue.Count == 0) return;
            player.inventory.crafting.FinishCrafting(player.inventory.crafting.queue.First.Value);
            player.inventory.crafting.queue.RemoveFirst();
        }
        private static void CancelAllCrafting(BasePlayer player)
        {
            ItemCrafter crafter = player.inventory.crafting;
            crafter.CancelAll();
        }

        #endregion Methods

        #region Hooks
        private Dictionary<ItemCraftTask, ulong> skinupdate = new Dictionary<ItemCraftTask, ulong>();
        private object OnItemCraft(ItemCraftTask task, BasePlayer player, Item fromTempBlueprint)
        {
            var target = task.blueprint.targetItem;
            if (task == null || target == null || task?.instanceData?.dataInt != null || task?.amount == 0) return null;
            var stacks = GetStacks(target, task.amount * task.blueprint.amountToCreate);
            ulong defaultskin = 0uL;
            int freeslots = FreeSlots(player);
            bool f = false;
            if (!config.FullInventory && stacks.Count >= freeslots)
            {
                f = true;
                int space = FreeSpace(player, target);
                if (space < 1)
                {
                    ReturnCraft(task, player);
                    return true;
                }
                int taskamt = task.amount * task.blueprint.amountToCreate;
                for (int i = 0; i < 20 && taskamt > space; i++)
                {
                    var oldtaskamt = taskamt;
                    taskamt = space;
                    foreach (var item in task.takenItems)
                    {
                        var itemtogive = item;
                        double fraction = (double)taskamt / (double)oldtaskamt;
                        int amttogive = (int)(item.amount * (1 - fraction));
                        if (amttogive <= 1)
                        {
                            ReturnCraft(task, player);
                            return true;
                        }
                        itemtogive = ItemManager.Create(item.info, amttogive, 0uL);
                        item.amount -= amttogive;

                        player.GiveItem(itemtogive);
                    }
                    space -= (freeslots - FreeSlots(player)) * target.stackable;
                    if (space < 1 || taskamt < 1)
                    {
                        ReturnCraft(task, player);
                        return true;
                    }
                    if (taskamt <= space) break;

                }
                task.amount = (int)(taskamt / task.blueprint.amountToCreate);
            }


            if (task.skinID == 0)
            {
                CraftingData data;
                if (config.CraftingOptions.TryGetValue(target.shortname, out data))
                {
                    defaultskin = data.defaultskinid;
                }

                if (config.RandomSkins && defaultskin == 0)
                {
                    List<ulong> skins = GetSkins(ItemManager.FindItemDefinition(target.itemid));
                    defaultskin = skins.GetRandom();
                }

                if (defaultskin > 999999)
                    skinupdate[task] = defaultskin;
                else
                    task.skinID = (int)defaultskin;
            }

            // VIP-уровень: бонус к базовому времени (bp.time уже = ванилла × GlobalRate%)
            var level = ResolveLevel(player);
            LevelData levelCfg = null;
            if (level > 0 && config.Levels.TryGetValue(level, out levelCfg) && levelCfg != null && !levelCfg.Instant)
            {
                var bonus = levelCfg.BonusPercent;
                if (bonus < 100f)
                {
                    task.blueprint = UnityEngine.Object.Instantiate(task.blueprint);
                    task.blueprint.time *= bonus / 100;
                }
            }

            var instant = (levelCfg != null && levelCfg.Instant)
                || task.blueprint.time == 0f
                || HasPerm(player.UserIDString, perminstant);
            if (instant)
            {
                skinupdate.Remove(task);
                if (f)
                    stacks = GetStacks(target, task.amount * task.blueprint.amountToCreate);
                InstantBulkCraft(player, task, target, stacks, task.skinID, defaultskin);
                task.cancelled = true;
                return true;
            }
            return null;
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item)
        {
            ulong skinid;
            if (!skinupdate.TryGetValue(task, out skinid)) return;

            item.skin = skinid;
            var held = item.GetHeldEntity();

            if (held != null)
            {
                held.skinID = skinid;
                held.SendNetworkUpdate();
            }
            if (task.amount == 0)
                skinupdate.Remove(task);
        }

        void OnItemCraftCancelled(ItemCraftTask task)
        {
            ulong skinid;
            if (!skinupdate.TryGetValue(task, out skinid)) return;
            skinupdate.Remove(task);
        }

        private void OnServerQuit()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player.inventory.crafting.queue.Count == 0) continue;
                if (config.CompleteCrafting)
                    CompleteCrafting(player);
                CancelAllCrafting(player);
            }
        }

        #endregion Hooks

        #region Helpers
        private void ReturnCraft(ItemCraftTask task, BasePlayer crafter)
        {
            task.cancelled = true;
            Message(crafter.IPlayer, "NoInvSpace");
            foreach (var item in task.takenItems)
            {
                if (item.amount > 0)
                    crafter.GiveItem(item);
            }
        }

        private ItemDefinition FindItem(string itemNameOrId)
        {
            ItemDefinition itemDef;
            if (int.TryParse(itemNameOrId, out int itemId))
            {
                itemDef = ItemManager.FindItemDefinition(itemId);
                return itemDef;
            }
            itemDef = ItemManager.FindItemDefinition(itemNameOrId.ToLower());
            return itemDef;
        }

        private int FreeSpace(BasePlayer player, ItemDefinition item)
        {
            var slots = player.inventory.containerMain.capacity + player.inventory.containerBelt.capacity;
            List<Item> containeritems = Pool.Get<List<Item>>();
            Dictionary<ItemDefinition, int> queueamts = new Dictionary<ItemDefinition, int>();
            containeritems.AddRange(player.inventory.containerMain.itemList);
            containeritems.AddRange(player.inventory.containerBelt.itemList);

            int value = 0;
            //Sum all items in crafting queue not including the item to be crafted
            foreach (var queueitem in player.inventory.crafting.queue)
            {
                if (queueitem.blueprint.targetItem == item) continue;
                if (queueamts.TryGetValue(queueitem.blueprint.targetItem, out value))
                {
                    queueamts[queueitem.blueprint.targetItem] += queueitem.amount * queueitem.blueprint.amountToCreate;
                    continue;
                }
                queueamts[queueitem.blueprint.targetItem] = queueitem.amount * queueitem.blueprint.amountToCreate;
            }
            //Take into account room of other stacks
            int queuestacks = 0;
            foreach (var i in queueamts)
            {
                queuestacks += GetStacks(i.Key, i.Value - Stackroom(containeritems, i.Key.shortname)).Count;
            }

            //calculate total room in inventory for the item required
            int invstackroom = (slots - containeritems.Count - queuestacks) * item.stackable;
            containeritems.ForEach(x => { if (x.info == item && x.amount < x.MaxStackable()) invstackroom += x.MaxStackable() - x.amount; });
            foreach (var x in player.inventory.crafting.queue)
            {
                if (x.blueprint.targetItem.shortname == item.shortname)
                {
                    invstackroom -= x.amount * x.blueprint.amountToCreate;
                }
            }
            Pool.FreeUnmanaged(ref containeritems);
            return invstackroom;
        }
        private int FreeSlots(BasePlayer player)
        {
            var slots = player.inventory.containerMain.capacity + player.inventory.containerBelt.capacity;
            var taken = player.inventory.containerMain.itemList.Count + player.inventory.containerBelt.itemList.Count;
            return slots - taken;
        }

        private int Stackroom(List<Item> items, string item)
        {
            int stackroom = 0;
            items.ForEach(x => { if (x.info.shortname == item && x.amount < x.MaxStackable()) stackroom += x.MaxStackable() - x.amount; });
            return stackroom;
        }

        private List<int> GetStacks(ItemDefinition item, int amount)
        {
            List<int> list = new List<int>();
            int maxStack = item.stackable;
            int maxstacks = amount / maxStack;
            if (maxStack == 0) return list;
            for (int i = 0; i <= maxstacks; i++)
            {
                if (maxStack > amount)
                {
                    if (amount >= 1)
                        list.Add(amount);
                    return list;
                }
                list.Add(maxStack);
                amount -= maxStack;
            }
            return list;
        }
        private readonly Dictionary<string, List<ulong>> skinsCache = new Dictionary<string, List<ulong>>();
        private List<ulong> GetSkins(ItemDefinition def)
        {
            List<ulong> skins;
            if (skinsCache.TryGetValue(def.shortname, out skins)) return skins;
            skins = new List<ulong>();
            foreach (var skin in ItemSkinDirectory.ForItem(def))
            {
                skins.Add((ulong)skin.id);
            }
            foreach (var skin in Rust.Workshop.Approved.All.Values)
            {
                if (skin.Skinnable.ItemName == def.shortname)
                    skins.Add(skin.WorkshopdId);
            }
            skinsCache.Add(def.shortname, skins);
            return skins;
        }

        private string GetLang(string langKey, string playerId = null, params object[] args)
        {
            return string.Format(lang.GetMessage(langKey, this, playerId), args);
        }
        private void Message(IPlayer player, string langKey, params object[] args)
        {
            if (player.IsConnected) player.Message(GetLang(langKey, player.Id, args));
        }

        private bool HasPerm(string id, string perm) => permission.UserHasPermission(id, perm);

        private void AddLocalizedCommand(string command)
        {
            foreach (string language in lang.GetLanguages(this))
            {
                Dictionary<string, string> messages = lang.GetMessages(language, this);
                foreach (KeyValuePair<string, string> message in messages)
                {
                    if (!message.Key.Equals(command)) continue;

                    if (string.IsNullOrEmpty(message.Value)) continue;

                    AddCovalenceCommand(message.Value, command);
                }
            }
        }
        #endregion Helpers
    }
}
