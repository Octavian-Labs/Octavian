using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaStackTest", "Octavian", "0.1.0")]
    [Description("[DEV] Управление размером стаков: глобальный множитель, по категориям и по предметам. Без внешних зависимостей.")]
    public class OctaStackTest : CovalencePlugin
    {
        [PluginReference]
        private Plugin AirFuel, GetToDaChoppa, VehicleVendorOptions;

        private const string PermAdmin = "octastack.admin";
        private const string VanillaFile = "OctaStackTest_vanilla";

        private ConfigData _config;

        // заводские размеры стаков — снапшот на первой загрузке, дальше из datafile
        private Dictionary<string, int> _vanilla;

        private readonly List<string> _ignoreList = new List<string>
        {
            "water", "water.salt", "cardtable", "hat.bunnyhat", "rustige_egg_e"
        };

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);

            // dev-анонс в чат при загрузке/перезагрузке
            timer.Once(1f, () =>
                BroadcastToChat($"Плагин \"{Name}\" dev — загружен"));

            LoadVanillaDefaults();
            EnsureConfigIntegrity();
            PopulateItemStackSizes();
            SetStackSizes();

            AddCovalenceCommand("octastack.set", nameof(CmdSetStack), PermAdmin);
            AddCovalenceCommand("octastack.setcat", nameof(CmdSetCategory), PermAdmin);
            AddCovalenceCommand("octastack.setall", nameof(CmdSetAll), PermAdmin);
            AddCovalenceCommand("octastack.search", nameof(CmdItemSearch), PermAdmin);
            AddCovalenceCommand("octastack.categories", nameof(CmdListCategories), PermAdmin);
            AddCovalenceCommand("octastack.catitems", nameof(CmdListCategoryItems), PermAdmin);
            AddCovalenceCommand("octastack.preset", nameof(CmdPreset), PermAdmin);
            AddCovalenceCommand("octastack.vd", nameof(CmdRegenVanilla), PermAdmin);
        }

        private void BroadcastToChat(string msg)
        {
            foreach (var p in BasePlayer.activePlayerList)
                p.SendConsoleCommand("chat.add", 0, "0", msg);
        }

        private void Unload()
        {
            if (_config.RevertOnUnload)
                RevertStackSizes();
        }

        #region Config

        private class ConfigData
        {
            [JsonProperty("Откатывать стеки к ванильным при выгрузке плагина")]
            public bool RevertOnUnload = true;

            [JsonProperty("Разрешить стакать предметы с прочностью")]
            public bool AllowStackingDurability = true;

            [JsonProperty("Глобальный множитель стака")]
            public float GlobalStackMultiplier = 1f;

            [JsonProperty("Множители по категориям")]
            public Dictionary<string, float> CategoryStackMultipliers =
                Enum.GetNames(typeof(ItemCategory)).ToDictionary(c => c, c => 1f);

            [JsonProperty("Множители по предметам (shortname или itemid)")]
            public Dictionary<string, float> IndividualItemStackMultipliers = new Dictionary<string, float>();

            [JsonProperty("Абсолютные размеры стака по предметам")]
            public Dictionary<string, int> IndividualItemStackSize = new Dictionary<string, int>();

            // пресеты = набор абсолютных размеров по shortname; применение сбрасывает
            // все множители и ставит остальным предметам ваниллу → состояние предсказуемо
            [JsonProperty("Пресеты стаков")]
            public Dictionary<string, Dictionary<string, int>> StackPresets = DefaultPresets();
        }

        // Ресурсы 10к/50к/100к — абсолютные капы, не множители (по ресёрчу uMod/Codefling).
        // Оружие, тулзы, двери (ванилла=1) и прочность не перечисляем — их не трогаем никогда.
        // gunpowder/explosives — ресурсы рейдовой экономики, держим заниженно.
        internal static Dictionary<string, Dictionary<string, int>> DefaultPresets()
        {
            var res = new[] { "wood", "stones", "metal.fragments", "sulfur", "charcoal",
                "scrap", "cloth", "leather", "bone.fragments", "fat.animal", "fertilizer" };
            var comps = new[] { "gears", "metalpipe", "metalspring", "sewingkit", "propanetank",
                "rope", "glue", "techparts", "sheetmetal", "metalblade", "roadsigns", "tarp",
                "riflebody", "semibody", "smgbody", "fuse" };
            var ammoHi = new[] { "ammo.rifle", "ammo.rifle.hv", "ammo.rifle.incendiary",
                "ammo.pistol", "ammo.pistol.hv", "ammo.pistol.fire", "ammo.paintball" };
            var ammoLo = new[] { "ammo.shotgun", "ammo.shotgun.fire", "ammo.handmade.shell",
                "ammo.nailgun.nails" };
            var seeds = new[] { "seed.hemp", "seed.corn", "seed.pumpkin", "seed.wheat",
                "seed.potato", "seed.black.berry", "seed.blue.berry", "seed.green.berry",
                "seed.red.berry", "seed.white.berry", "seed.yellow.berry" };

            Dictionary<string, int> Build(int resCap, int gunpowder, int explosives, int hqm,
                int fuel, int comp, int diesel, int aHi, int aLo, int slug, int ammoExpl,
                int rocket, int mlrs, int nade, int beancan, int molly, int util, int c4,
                int bandage, int syringe, int seed)
            {
                var d = new Dictionary<string, int>();
                foreach (var s in res) d[s] = resCap;
                d["gunpowder"] = gunpowder; d["explosives"] = explosives;
                d["metal.refined"] = hqm;
                d["crude.oil"] = fuel; d["lowgradefuel"] = fuel;
                foreach (var s in comps) d[s] = comp;
                d["diesel_barrel"] = diesel;
                foreach (var s in ammoHi) d[s] = aHi;
                foreach (var s in ammoLo) d[s] = aLo;
                d["ammo.shotgun.slug"] = slug;
                if (ammoExpl > 0) d["ammo.rifle.explosive"] = ammoExpl;
                if (rocket > 0)
                {
                    d["ammo.rocket.basic"] = rocket; d["ammo.rocket.fire"] = rocket;
                    d["ammo.rocket.hv"] = rocket; d["ammo.rocket.smoke"] = rocket;
                    d["ammo.rocket.seeker"] = rocket + 1; d["ammo.rocket.mlrs"] = mlrs;
                }
                if (nade > 0)
                {
                    d["grenade.f1"] = nade; d["grenade.beancan"] = beancan;
                    d["grenade.molotov"] = molly; d["grenade.bee"] = nade;
                    d["grenade.smoke"] = util; d["grenade.flashbang"] = util;
                }
                if (c4 > 0) { d["explosive.timed"] = c4; d["explosive.satchel"] = c4; }
                d["bandage"] = bandage; d["medical.honey.bandage"] = bandage;
                d["syringe.medical"] = syringe;
                foreach (var s in seeds) d[s] = seed;
                d["scraptea"] = seed / 5;
                d["scraptea.pure"] = seed / 10; d["scraptea.pure.milky"] = seed / 10;
                d["scraptea.pure.creamy"] = seed / 10; d["scraptea.advanced"] = seed / 10;
                return d;
            }

            return new Dictionary<string, Dictionary<string, int>>
            {
                // ванилла — пустой набор: применение просто откатывает всё к заводским
                ["vanilla"] = new Dictionary<string, int>(),
                // A «Разумные»: ресурсы 10к, взрывчатка ванильная
                ["balanced"] = Build(10000, 2000, 500, 1000, 2000, 200, 100,
                    512, 256, 128, 0, 0, 0, 0, 0, 0, 0, 0, 15, 10, 200),
                // B «Бустер»: ресурсы 50к, взрывчатка x2
                ["boosted"] = Build(50000, 10000, 2000, 5000, 5000, 500, 200,
                    1280, 640, 320, 256, 6, 12, 6, 8, 6, 10, 20, 30, 20, 500),
                // C «Аркада»: ресурсы 100к, взрывчатка x5
                ["arcade"] = Build(100000, 25000, 5000, 10000, 10000, 2000, 500,
                    2560, 1280, 640, 640, 15, 30, 15, 20, 15, 25, 50, 50, 40, 1000),
            };
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
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);
        protected override void LoadDefaultConfig() => _config = new ConfigData();

        private void EnsureConfigIntegrity()
        {
            var def = new ConfigData();
            _config.CategoryStackMultipliers ??= def.CategoryStackMultipliers;
            _config.IndividualItemStackMultipliers ??= def.IndividualItemStackMultipliers;
            _config.IndividualItemStackSize ??= def.IndividualItemStackSize;
            _config.StackPresets ??= def.StackPresets;

            // новые категории из обновлений игры
            foreach (var cat in Enum.GetNames(typeof(ItemCategory)))
                _config.CategoryStackMultipliers.TryAdd(cat, 1f);

            SaveConfig();
        }

        // полный список предметов в конфиг — видно и правится через редактор RMS
        private void PopulateItemStackSizes()
        {
            foreach (var def in ItemManager.GetItemDefinitions())
                if (!_config.IndividualItemStackSize.ContainsKey(def.shortname))
                    _config.IndividualItemStackSize[def.shortname] = VanillaSize(def);
            SaveConfig();
        }

        #endregion

        #region Ванильные значения — без внешней загрузки

        private void LoadVanillaDefaults()
        {
            // datafile — источник истины (переживает рестарты и патченые стеки)
            _vanilla = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, int>>(VanillaFile);

            if (_vanilla != null && _vanilla.Count > 0)
            {
                // докидываем предметы из новых апдейтов игры
                bool added = false;
                foreach (var def in ItemManager.GetItemDefinitions())
                    if (!_vanilla.ContainsKey(def.shortname))
                    {
                        _vanilla[def.shortname] = def.stackable;
                        added = true;
                    }
                if (added) Interface.Oxide.DataFileSystem.WriteObject(VanillaFile, _vanilla);
                return;
            }

            // первая загрузка: stackable ещё ванильный — снимаем снапшот
            _vanilla = new Dictionary<string, int>();
            foreach (var def in ItemManager.GetItemDefinitions())
                _vanilla[def.shortname] = def.stackable;
            Interface.Oxide.DataFileSystem.WriteObject(VanillaFile, _vanilla);
            Puts($"Снапшот ванильных стаков: {_vanilla.Count} предметов");
        }

        private int VanillaSize(ItemDefinition def) =>
            _vanilla != null && _vanilla.TryGetValue(def.shortname, out var v) ? v : def.stackable;

        #endregion

        #region Хуки

        // фикс начального топлива у магазинных вертов: они берут 20% стака lowgrade —
        // при жирных стаках давали бы тысячи топлива
        private void OnEntitySpawned(Minicopter heli)
        {
            if (AirFuel != null || GetToDaChoppa != null || VehicleVendorOptions != null)
                return;

            NextTick(() =>
            {
                if (heli == null || !heli.IsSafe() || heli.StartingFuelUnits() != -1)
                    return;

                var fuelItem = (heli.GetFuelSystem() as EntityFuelSystem)?.GetFuelItem();
                if (fuelItem == null
                    || fuelItem.info.shortname != "lowgradefuel"
                    || fuelItem.amount != fuelItem.info.stackable / 5)
                    return;

                var hookResult = Interface.CallHook("OnVendorHeliFuelAdjust", heli);
                if (hookResult is bool && !(bool)hookResult)
                    return;

                fuelItem.amount = 100;
                fuelItem.MarkDirty();
            });
        }

        private int OnMaxStackable(Item item) => GetStackSize(item.info);

        #endregion

        #region Расчёт и применение

        private int GetStackSize(ItemDefinition def)
        {
            try
            {
                if (_ignoreList.Contains(def.shortname))
                    return VanillaSize(def);

                int stackable = VanillaSize(def);

                // абсолютный размер по shortname
                if (_config.IndividualItemStackSize.TryGetValue(def.shortname, out var custom))
                    stackable = custom;

                // множитель по предмету (shortname или itemid)
                if (_config.IndividualItemStackMultipliers.TryGetValue(def.shortname, out var m1))
                    return Mathf.RoundToInt(stackable * m1);
                if (_config.IndividualItemStackMultipliers.TryGetValue(def.itemid.ToString(), out var m2))
                    return Mathf.RoundToInt(stackable * m2);

                // ванилла не стакается (оружие, тулзы, двери) — множители категорий
                // и глобальный их не трогают; меняются только явным размером/множителем
                if (VanillaSize(def) <= 1)
                    return stackable;

                // множитель категории
                var cat = def.category.ToString();
                if (_config.CategoryStackMultipliers.TryGetValue(cat, out var cm) && cm > 1f)
                    return Mathf.RoundToInt(stackable * cm);

                return Mathf.RoundToInt(stackable * _config.GlobalStackMultiplier);
            }
            catch
            {
                return VanillaSize(def);
            }
        }

        private void SetStackSizes()
        {
            foreach (var def in ItemManager.GetItemDefinitions())
            {
                if (def.condition.enabled && !_config.AllowStackingDurability)
                {
                    def.stackable = Mathf.Clamp(VanillaSize(def), 1, int.MaxValue);
                    continue;
                }
                if (_ignoreList.Contains(def.shortname)) continue;
                def.stackable = Mathf.Clamp(GetStackSize(def), 1, int.MaxValue);
            }
        }

        private void RevertStackSizes()
        {
            foreach (var def in ItemManager.GetItemDefinitions())
            {
                if (def.condition.enabled && !_config.AllowStackingDurability) continue;
                if (_ignoreList.Contains(def.shortname)) continue;
                def.stackable = Mathf.Clamp(VanillaSize(def), 1, int.MaxValue);
            }
        }

        #endregion

        #region Команды

        private void Reply(IPlayer player, string msg) =>
            player.Reply($"<color=#E8B84B>◆ OctaStack</color> {msg}");

        // octastack.set <shortname|id> <N|Nx>
        private void CmdSetStack(IPlayer player, string command, string[] args)
        {
            if (args.Length != 2) { Reply(player, "Формат: octastack.set <предмет> <размер|Nx>"); return; }

            var def = ItemManager.FindItemDefinition(args[0]);
            if (def == null) { Reply(player, "Предмет не найден. Поиск: octastack.search <часть имени>"); return; }

            var val = args[1].TrimEnd('x');
            if (args[1].EndsWith("x"))
            {
                if (!float.TryParse(val, out var mult)) { Reply(player, "Множитель не число"); return; }
                _config.IndividualItemStackMultipliers[def.shortname] = mult;
            }
            else
            {
                if (!int.TryParse(val, out var size)) { Reply(player, "Размер не число"); return; }
                _config.IndividualItemStackSize[def.shortname] = size;
            }

            SaveConfig();
            SetStackSizes();
            Reply(player, $"Готово: {def.shortname} → {GetStackSize(def):N0}");
        }

        // octastack.setcat <категория> <N>
        private void CmdSetCategory(IPlayer player, string command, string[] args)
        {
            if (args.Length != 2) { Reply(player, "Формат: octastack.setcat <категория> <Nx>"); return; }
            if (!Enum.TryParse(args[0], true, out ItemCategory cat))
            { Reply(player, "Категория не найдена. Список: octastack.categories"); return; }

            _config.CategoryStackMultipliers[cat.ToString()] = Convert.ToSingle(args[1].TrimEnd('x'));
            SaveConfig();
            SetStackSizes();
            Reply(player, $"Категория {cat} → x{args[1]}");
        }

        // octastack.setall <N>
        private void CmdSetAll(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1 || !float.TryParse(args[0].TrimEnd('x'), out var mult))
            { Reply(player, "Формат: octastack.setall <Nx>"); return; }

            foreach (var cat in _config.CategoryStackMultipliers.Keys.ToList())
                _config.CategoryStackMultipliers[cat] = mult;

            SaveConfig();
            SetStackSizes();
            Reply(player, $"Все категории → x{mult}");
        }

        // octastack.search <строка>
        private void CmdItemSearch(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1) { Reply(player, "Формат: octastack.search <часть имени>"); return; }

            var found = ItemManager.itemList.Where(d =>
                d.displayName.english.IndexOf(args[0], StringComparison.OrdinalIgnoreCase) >= 0 ||
                d.shortname.IndexOf(args[0], StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (found.Count == 0) { Reply(player, "Ничего не найдено"); return; }

            var output = new TextTable();
            output.AddColumns("Id", "Shortname", "Category", "Vanilla", "Custom");
            foreach (var d in found.Take(40))
                output.AddRow(d.itemid.ToString(), d.shortname, d.category.ToString(),
                    VanillaSize(d).ToString("N0"), Mathf.Clamp(GetStackSize(d), 0, int.MaxValue).ToString("N0"));
            if (found.Count > 40) Reply(player, $"...и ещё {found.Count - 40}, уточни поиск");
            player.Reply(output.ToString());
        }

        private void CmdListCategories(IPlayer player, string command, string[] args)
        {
            var output = new TextTable();
            output.AddColumns("Категория", "Предметов", "Множитель");
            foreach (var cat in Enum.GetNames(typeof(ItemCategory)))
                output.AddRow(cat,
                    ItemManager.itemList.Count(x => x.category.ToString() == cat).ToString(),
                    _config.CategoryStackMultipliers[cat].ToString());
            player.Reply(output.ToString());
        }

        // octastack.catitems <категория>
        private void CmdListCategoryItems(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1) { Reply(player, "Формат: octastack.catitems <категория>"); return; }
            if (!Enum.TryParse(args[0], true, out ItemCategory cat))
            { Reply(player, "Категория не найдена. Список: octastack.categories"); return; }

            var output = new TextTable();
            output.AddColumns("Id", "Shortname", "Vanilla", "Custom", "Множ. кат.");
            foreach (var d in ItemManager.GetItemDefinitions().Where(d => d.category == cat))
                output.AddRow(d.itemid.ToString(), d.shortname,
                    VanillaSize(d).ToString("N0"),
                    Mathf.Clamp(GetStackSize(d), 0, int.MaxValue).ToString("N0"),
                    _config.CategoryStackMultipliers[cat.ToString()].ToString());
            player.Reply(output.ToString());
        }

        // octastack.preset <vanilla|balanced|boosted|arcade> — предсказуемое
        // состояние: все множители в 1, стеки = ванилла + оверрайды пресета
        private void CmdPreset(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1 || !_config.StackPresets.TryGetValue(args[0].ToLower(), out var preset))
            {
                Reply(player, $"Формат: octastack.preset <{string.Join("|", _config.StackPresets.Keys)}>");
                return;
            }

            _config.GlobalStackMultiplier = 1f;
            _config.IndividualItemStackMultipliers.Clear();
            foreach (var c in _config.CategoryStackMultipliers.Keys.ToList())
                _config.CategoryStackMultipliers[c] = 1f;

            foreach (var def in ItemManager.GetItemDefinitions())
                _config.IndividualItemStackSize[def.shortname] = VanillaSize(def);
            foreach (var kv in preset)
                _config.IndividualItemStackSize[kv.Key] = kv.Value;

            SaveConfig();
            SetStackSizes();
            Reply(player, $"Пресет «{args[0].ToLower()}» применён: {preset.Count} оверрайдов, " +
                $"wood → {GetStackSize(ItemManager.FindItemDefinition("wood")):N0}");
        }

        // octastack.vd — переснять ванильный снапшот (после апдейтов игры)
        private void CmdRegenVanilla(IPlayer player, string command, string[] args)
        {
            RevertStackSizes();
            _vanilla = new Dictionary<string, int>();
            foreach (var def in ItemManager.GetItemDefinitions())
                _vanilla[def.shortname] = def.stackable;
            Interface.Oxide.DataFileSystem.WriteObject(VanillaFile, _vanilla);
            SetStackSizes();
            Reply(player, $"Ванильный снапшот переснят: {_vanilla.Count} предметов");
        }

        #endregion
    }
}
