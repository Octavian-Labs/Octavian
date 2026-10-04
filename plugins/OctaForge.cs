// OctaForge — скорость печей по VIP-уровню владельца.
// Перекраска OxidationSmelting (kasvoton 1.4.15): сохранены Products (время/выход
// готовки), Fuel (побочки), NoBurntMeat, NoFuelRequired. Добавлено:
//   - базовый множитель = рейт сервера (по умолчанию x5);
//   - уровни: octaforge.t1..t6 ИЛИ OctVIP GetVipLevel — эффективная скорость
//     = Oven.Multiplier × Level.SpeedMultiplier; Instant=true — плавит весь
//     инпут за один цикл (топливо списывается как при обычной плавке);
//   - автосплит: сырьё раскладывается поровну по входным слотам печи;
//   - автотопливо: дерево подгружается из инвентаря — сколько нужно на
//     плавку лежащего сырья (или всё что есть, если не хватает);
//   - biofuel_generator.deployed: множитель делит processInterval,
//     Instant — почти непрерывная переработка и автопомешивание.
using System.Collections.Generic;
using Facepunch.Extend;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaForge", "Octavian", "1.0.0")]
    [Description("VIP-tiered smelting speed, cookable output and fuel settings")]

    public class OctaForge : RustPlugin
    {
        #region --- EVENTS -------------------------------------------------------------------------------------------------
        private void OnServerInitialized()
        {
            foreach (var perm in PermLevel)
                if (!string.IsNullOrEmpty(perm)) permission.RegisterPermission(perm, this);

            DefaultSettings();
            // печи могли добавиться после первого запуска — дописываем недостающие
            var dirty = false;
            foreach (var oven in Ovens)
                if (!Settings.Ovens.ContainsKey(oven))
                {
                    Settings.Ovens.Add(oven, new SettingsObject.OvenSettings());
                    dirty = true;
                }
            if (dirty) SaveConfig();

            timer.Every(5f, ScanBioGenerators);

            foreach (ItemDefinition Item in ItemManager.itemList)
            {
                foreach (ItemMod mod in Item.itemMods)
                {
                    ItemModCookable Cookable = mod as ItemModCookable;
                    if (Cookable != null)
                    {
                        SettingsObject.ProductSettings Info = null;
                        if (Settings.Products.TryGetValue(Item.shortname, out Info) != false)
                        {
                            Info.Amount = (Info.Amount < 1) ? 1 : Info.Amount;
                            Info.CookTime = (Info.CookTime < 0.1f) ? 0.1f : Info.CookTime;

                            Backup.Products.TryAdd(Item.shortname, new SettingsObject.ProductSettings
                            {
                                Amount = (int)Cookable.amountOfBecome,
                                CookTime = Cookable.cookTime,
                                LowTemp = Cookable.lowTemp,
                                HighTemp = Cookable.highTemp
                            });

                            Cookable.amountOfBecome = Info.Amount;
                            Cookable.cookTime = Info.CookTime;
                        }

                        if (Settings.NoBurntMeat && Item.shortname.Contains(".cooked"))
                        {
                            Backup.Products.TryAdd(Item.shortname, new SettingsObject.ProductSettings
                            {
                                Amount = (int)Cookable.amountOfBecome,
                                CookTime = Cookable.cookTime,
                                LowTemp = Cookable.lowTemp,
                                HighTemp = Cookable.highTemp
                            });

                            Cookable.highTemp = -1;
                            Cookable.lowTemp = -1;
                        }
                    }

                    ItemModBurnable Burnable = mod as ItemModBurnable;
                    if (Burnable != null && Burnable.byproductItem != null)
                    {
                        SettingsObject.FuelSettings Info = null;
                        if (Settings.Fuel.TryGetValue(Item.shortname, out Info) != false)
                        {
                            Backup.Fuel.Add(Item.shortname, new SettingsObject.FuelSettings
                            {
                                FuelAmount = Burnable.fuelAmount,
                                ByProductChance = Burnable.byproductChance,
                                ByProductAmount = (int)Burnable.byproductAmount
                            });

                            Burnable.fuelAmount = Info.FuelAmount;
                            Burnable.byproductAmount = Info.ByProductAmount;
                            Burnable.byproductChance = 1 - Info.ByProductChance;
                        }
                    }
                }
            };
        }

        private void Unload()
        {
            foreach (KeyValuePair<string, SettingsObject.ProductSettings> kvp in Backup.Products)
            {
                ItemModCookable Cookable = ItemManager.FindItemDefinition(kvp.Key)?.GetComponent<ItemModCookable>();
                if (Cookable == null) continue;

                Cookable.amountOfBecome = kvp.Value.Amount;
                Cookable.cookTime = kvp.Value.CookTime;
                Cookable.highTemp = kvp.Value.HighTemp;
                Cookable.lowTemp = kvp.Value.LowTemp;
            }

            foreach (KeyValuePair<string, SettingsObject.FuelSettings> kvp in Backup.Fuel)
            {
                ItemModBurnable Burnable = ItemManager.FindItemDefinition(kvp.Key)?.GetComponent<ItemModBurnable>();
                if (Burnable == null) continue;

                Burnable.fuelAmount = kvp.Value.FuelAmount;
                Burnable.byproductAmount = kvp.Value.ByProductAmount;
                Burnable.byproductChance = kvp.Value.ByProductChance;
            }
        }

        private void OnOvenCooked(BaseOven Oven, Item Fuel, BaseEntity Slot)
        {
            ItemModBurnable Burnable = Fuel?.info.GetComponent<ItemModBurnable>();

            SettingsObject.OvenSettings Info = null;
            if (!Settings.Ovens.TryGetValue(Oven?.ShortPrefabName, out Info))
                return;

            var level = ResolveLevel(Oven.OwnerID);
            var levelCfg = GetLevel(level);

            if (levelCfg.Instant)
            {
                // мгновенная плавка: крутим циклы, пока в печи есть что готовить.
                // топливо тратится честно — как будто плавили в обычном темпе
                var guard = 0;
                while (guard++ < InstantLoopCap && HasCookable(Oven))
                {
                    Oven.inventory.OnCycle(0.5f);
                    if (Slot) Slot.SendMessage("Cook", 0.5f, SendMessageOptions.DontRequireReceiver);

                    Fuel.fuel -= 0.5f * (Oven.cookingTemperature / 200f);
                    if (Fuel.fuel <= 0f)
                    {
                        Oven.ConsumeFuel(Fuel, Burnable);
                        if (Fuel == null || Fuel.amount <= 0 || Fuel.fuel <= 0f) break;
                    }
                }
                return;
            }

            var effective = Info.Multiplier * levelCfg.SpeedMultiplier;
            if (effective <= 1f) return;

            for (int x = 1; x < effective; x++)
            {
                Oven.inventory.OnCycle(0.5f);
                if (Slot) Slot.SendMessage("Cook", 0.5f, SendMessageOptions.DontRequireReceiver);

                Fuel.fuel -= 0.5f * (Oven.cookingTemperature / 200f);
                if (Fuel.fuel <= 0f) Oven.ConsumeFuel(Fuel, Burnable);
            }
        }

        private static bool HasCookable(BaseOven oven)
        {
            if (oven?.inventory?.itemList == null) return false;
            foreach (var item in oven.inventory.itemList)
            {
                if (item?.info == null) continue;
                if (item.info.GetComponent<ItemModCookable>() != null) return true;
            }
            return false;
        }

        // автосплит: раскладываем сырьё поровну по input-слотам + автотопливо
        private readonly HashSet<ItemContainerId> _busy = new HashSet<ItemContainerId>();

        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            var oven = container?.entityOwner as BaseOven;
            if (oven == null || item == null) return;
            if (!Settings.AutoSplit && !Settings.AutoFuel) return;
            if (_busy.Contains(container.uid)) return; // наши же MoveToContainer дёргают хук
            if (item.info?.GetComponent<ItemModCookable>() == null) return;

            // откладываем на тик — пакет переливания (шифт-клики) обработается разом
            _busy.Add(container.uid);
            timer.Once(0.05f, () =>
            {
                try
                {
                    if (oven != null && !oven.IsDestroyed)
                    {
                        if (Settings.AutoSplit) SplitInputs(oven);
                        if (Settings.AutoFuel) AutoFuel(oven);
                    }
                }
                finally
                {
                    _busy.Remove(container.uid);
                }
            });
        }

        private void OnOvenToggle(BaseOven oven, BasePlayer player)
        {
            if (oven == null || !oven.IsOn()) return;
            if (Settings.AutoSplit) SplitInputs(oven);
            if (Settings.AutoFuel) AutoFuel(oven, player);
        }

        // входные слоты: [fuelSlots, fuelSlots+inputSlots); топливо — первые fuelSlots
        private void SplitInputs(BaseOven oven)
        {
            var cont = oven.inventory;
            var first = oven.fuelSlots;
            var last = Mathf.Min(oven.fuelSlots + oven.inputSlots, cont.capacity);
            if (last - first < 2) return;

            var seen = new HashSet<ItemDefinition>();
            for (var i = first; i < last; i++)
            {
                var it = cont.GetSlot(i);
                if (it?.info?.GetComponent<ItemModCookable>() != null)
                    seen.Add(it.info);
            }
            foreach (var def in seen)
                Redistribute(oven, def, first, last);
            cont.MarkDirty();
            oven.SendNetworkUpdate();
        }

        private void Redistribute(BaseOven oven, ItemDefinition def, int first, int last)
        {
            var cont = oven.inventory;
            var pool = 0;
            var free = new List<int>();
            for (var i = first; i < last; i++)
            {
                var it = cont.GetSlot(i);
                if (it == null) free.Add(i);
                else if (it.info == def) { free.Add(i); pool += it.amount; it.RemoveFromContainer(); it.Remove(); }
            }
            if (pool == 0 || free.Count == 0) return;

            // поровну по всем доступным слотам (в т.ч. свободным)
            var per = Mathf.Max(1, Mathf.CeilToInt(pool / (float)free.Count));
            foreach (var slot in free)
            {
                if (pool <= 0) break;
                var ni = ItemManager.Create(def, Mathf.Min(per, pool));
                if (!ni.MoveToContainer(cont, slot))
                {
                    pool += ni.amount;
                    ni.Remove();
                }
                else pool -= ni.amount;
            }
            // остаток (не влез из-за maxstack) — в любой свободный слот контейнера
            if (pool > 0)
            {
                var rest = ItemManager.Create(def, pool);
                if (!rest.MoveToContainer(cont)) rest.Drop(oven.transform.position + Vector3.up, Vector3.zero);
            }
        }

        // подгружает топливо из инвентаря игрока, лутающего печь:
        // столько, сколько нужно на плавку лежащего сырья (или всё что есть)
        private void AutoFuel(BaseOven oven, BasePlayer player = null)
        {
            var def = oven.fuelType;
            if (def == null) return;
            player = player ?? FindLooter(oven);
            if (player == null) return;

            var burnable = def.GetComponent<ItemModBurnable>();
            var fuelPerUnit = burnable != null ? Mathf.Max(1f, burnable.fuelAmount) : 10f;

            var cont = oven.inventory;
            var first = oven.fuelSlots;
            var last = Mathf.Min(oven.fuelSlots + oven.inputSlots, cont.capacity);

            // сколько топлива съест плавка всего сырья: предмет готовится
            // за cookTime/smeltSpeed сек; расход = temp/200 топлива в секунду × фактор
            var totalTime = 0f;
            for (var i = first; i < last; i++)
            {
                var it = cont.GetSlot(i);
                var cook = it?.info?.GetComponent<ItemModCookable>();
                if (cook == null) continue;
                totalTime += it.amount * cook.cookTime;
            }
            if (totalTime <= 0f) return;

            var speed = Mathf.Max(1, oven.smeltSpeed);
            var fuelNeeded = (totalTime / speed) * (oven.cookingTemperature / 200f) * Settings.FuelBurnFactor;
            var fuelInOven = 0f;
            for (var i = 0; i < oven.fuelSlots; i++)
            {
                var it = cont.GetSlot(i);
                // item.fuel — остаток только у горящего юнита; остаток стека — полные единицы
                if (it != null && it.info == def) fuelInOven += it.fuel + (it.amount - 1) * fuelPerUnit;
            }
            var deficit = fuelNeeded - fuelInOven;
            if (deficit <= 0.5f) return;

            foreach (var from in new[] { player.inventory.containerMain, player.inventory.containerBelt })
            {
                for (var i = from.itemList.Count - 1; i >= 0 && deficit > 0f; i--)
                {
                    var it = from.itemList[i];
                    if (it.info != def) continue;
                    var take = (int)Mathf.Min(it.amount, Mathf.Ceil(deficit / fuelPerUnit));
                    if (take <= 0) continue;
                    Item moved = take >= it.amount ? it : it.SplitItem(take);
                    if (moved != null && moved.MoveToContainer(cont))
                    {
                        deficit -= take * fuelPerUnit;
                        it.MarkDirty();
                    }
                    else if (moved != null && moved != it)
                        moved.Remove();
                }
            }
            cont.MarkDirty();
            oven.SendNetworkUpdate();
        }

        private BasePlayer FindLooter(BaseEntity ent)
        {
            foreach (var p in BasePlayer.activePlayerList)
                if (p.inventory.loot.entitySource == ent) return p;
            return null;
        }

        // --- BiofuelGenerator: множитель делит processInterval, Instant — авто-помешивание
        private void ScanBioGenerators()
        {
            foreach (var ent in BaseEntity.saveList)
            {
                var bio = ent as BiofuelGenerator;
                if (bio == null) continue;
                ApplyBio(bio);
            }
        }

        private void ApplyBio(BiofuelGenerator bio)
        {
            SettingsObject.OvenSettings info;
            if (!Settings.Ovens.TryGetValue(BioPrefab, out info)) return;

            float baseInterval;
            if (!_bioBase.TryGetValue(bio.net?.ID.Value ?? 0, out baseInterval))
            {
                baseInterval = bio.processInterval;
                _bioBase[bio.net?.ID.Value ?? 0] = baseInterval;
            }

            var level = ResolveLevel(bio.OwnerID);
            var lc = GetLevel(level);
            if (lc.Instant)
            {
                bio.processInterval = 0.05f;
                bio.StirCharge = 1f;
                bio.SetStirred(true);
            }
            else
            {
                var eff = info.Multiplier * lc.SpeedMultiplier;
                bio.processInterval = Mathf.Max(0.1f, baseInterval / eff);
            }
        }

        private readonly Dictionary<ulong, float> _bioBase = new Dictionary<ulong, float>();
        private const string BioPrefab = "biofuel_generator.deployed";

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var bio = entity as BiofuelGenerator;
            if (bio == null) return;
            // OwnerID ставится при развёртывании — чуть позже спавна
            timer.Once(1f, () => { if (bio != null && !bio.IsDestroyed) ApplyBio(bio); });
        }

        private object OnFuelConsume(BaseOven Oven, Item Fuel, ItemModBurnable Burnable)
        {
            if (Oven == null || !Oven.OwnerID.IsSteamId()) return null;

            SettingsObject.OvenSettings Info = null;
            Settings.Ovens.TryGetValue(Oven?.ShortPrefabName, out Info);
            if (Info == null || !Info.NoFuelRequired) return null;

            if (!Oven.allowByproductCreation || Burnable.byproductItem == null
                    || UnityEngine.Random.Range(0f, 1f) <= Burnable.byproductChance) return false;

            Item Byproduct = ItemManager.Create(Burnable.byproductItem, Burnable.byproductAmount, 0UL);
            if (!Byproduct.MoveToContainer(Oven.inventory, -1, true, false))
            {
                Oven.OvenFull();
                Byproduct.Drop(Oven.inventory.dropPosition, Oven.inventory.dropVelocity, default(Quaternion));
            }

            Fuel.fuel = Burnable.fuelAmount;
            Fuel.MarkDirty();
            return true;
        }
        #endregion

        #region --- VIP-УРОВНИ -----------------------------------------------------------------------------------------------
        // пермишены уровней — выдаются группам vip_*; при наличии OctVIP уровень берётся из него
        private static readonly string[] PermLevel =
        {
            "", "octaforge.t1", "octaforge.t2", "octaforge.t3",
            "octaforge.t4", "octaforge.t5", "octaforge.t6"
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

        protected void DefaultSettings()
        {
            if (Settings.Products.Count > 0 || Settings.Fuel.Count > 0) return;
            if (ItemManager.itemList == null || ItemManager.itemList.Count == 0) return;

            Settings.NoBurntMeat = false;

            foreach (string Oven in Ovens)
                Settings.Ovens.Add(Oven, new SettingsObject.OvenSettings());

            foreach (ItemDefinition Item in ItemManager.itemList)
            {
                foreach (ItemMod mod in Item.itemMods)
                {
                    if (Item.shortname.Contains(".burned") || Item.shortname.Contains(".cooked")) continue;

                    ItemModCookable Cookable = mod as ItemModCookable;
                    if (Cookable != null)
                    {
                        Settings.Products.Add(Item.shortname, new SettingsObject.ProductSettings
                        { Amount = (int)Cookable.amountOfBecome, CookTime = Cookable.cookTime });
                    }

                    ItemModBurnable Burnable = mod as ItemModBurnable;
                    if (Burnable != null && Burnable.byproductItem != null)
                    {
                        Settings.Fuel.Add(Item.shortname, new SettingsObject.FuelSettings
                        {
                            FuelAmount = Burnable.fuelAmount,
                            ByProduct = Burnable.byproductItem.shortname,
                            ByProductAmount = (int)Burnable.byproductAmount,
                            ByProductChance = 1 - Burnable.byproductChance,
                        });
                    }
                }
            }

            PrintWarning($"Loaded v{Version} default config values");
            SaveConfig();
        }

        protected void ValidateSettings()
        {
            foreach (KeyValuePair<string, SettingsObject.OvenSettings> kvp in Settings.Ovens)
            {
                kvp.Value.Multiplier = Mathf.Clamp(kvp.Value.Multiplier, 1, 100);
            }

            foreach (KeyValuePair<int, SettingsObject.LevelSettings> kvp in Settings.Levels)
                kvp.Value.SpeedMultiplier = Mathf.Clamp(kvp.Value.SpeedMultiplier, 1f, 100f);
            Settings.DefaultLevel.SpeedMultiplier = Mathf.Clamp(Settings.DefaultLevel.SpeedMultiplier, 1f, 100f);

            foreach (KeyValuePair<string, SettingsObject.ProductSettings> kvp in Settings.Products)
            {
                kvp.Value.Amount = Mathf.Clamp(kvp.Value.Amount, 1, int.MaxValue);
                kvp.Value.CookTime = Mathf.Clamp(kvp.Value.CookTime, 0.5f, float.MaxValue);
            }

            foreach (KeyValuePair<string, SettingsObject.FuelSettings> kvp in Settings.Fuel)
            {
                kvp.Value.ByProductChance = Mathf.Clamp(kvp.Value.ByProductChance, 0f, 1f);
                kvp.Value.FuelAmount = Mathf.Clamp(kvp.Value.FuelAmount, 10f, float.MaxValue);
                kvp.Value.ByProductAmount = Mathf.Clamp(kvp.Value.ByProductAmount, 0, int.MaxValue);
            }
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
                Patch = 1
            };

            public VersionNumber Version
            { get; set; }

            public bool NoBurntMeat
            { get; set; }

            // сырьё раскладывается поровну по входным слотам печи
            [JsonProperty("Автосплит сырья по слотам")]
            public bool AutoSplit
            { get; set; } = true;

            // топливо подгружается из инвентаря игрока, лутающего печь
            [JsonProperty("Автозагрузка топлива")]
            public bool AutoFuel
            { get; set; } = true;

            // множитель расхода топлива при расчёте автозагрузки:
            // топливо = Σ(кол-во × cookTime / smeltSpeed) × (temp/200) × фактор.
            // 0.6 → ~0.5 дерева на руду серы (5000 руды = 2500 дерева)
            [JsonProperty("Расход топлива (множитель)")]
            public float FuelBurnFactor
            { get; set; } = 0.6f;

            // уровень владельца печи → доп. множитель или мгновенная плавка.
            // эффективная скорость = Oven.Multiplier × Level.SpeedMultiplier
            // (база печи уже = рейт сервера x5 → ванила 30 мин ~ за 6 мин)
            [JsonProperty("Уровни (0=default 1=bronze 2=silver 3=gold 4=brilliant 5=moderator 6=admin)")]
            public Dictionary<int, LevelSettings> Levels
            { get; set; } = new Dictionary<int, LevelSettings>
            {
                [0] = new LevelSettings { SpeedMultiplier = 1.0f },  // default: чистый рейт x5
                [1] = new LevelSettings { SpeedMultiplier = 1.5f },  // bronze:  x7.5
                [2] = new LevelSettings { SpeedMultiplier = 2.0f },  // silver:  x10
                [3] = new LevelSettings { Instant = true },          // gold+:   мгновенно
                [4] = new LevelSettings { Instant = true },
                [5] = new LevelSettings { Instant = true },
                [6] = new LevelSettings { Instant = true },
            };

            [JsonIgnore]
            public LevelSettings DefaultLevel
            { get; } = new LevelSettings { SpeedMultiplier = 1.0f };

            public Dictionary<string, OvenSettings> Ovens
            { get; set; } = new Dictionary<string, OvenSettings>();

            public Dictionary<string, ProductSettings> Products
            { get; set; } = new Dictionary<string, ProductSettings>();

            public Dictionary<string, FuelSettings> Fuel
            { get; set; } = new Dictionary<string, FuelSettings>();

            internal class LevelSettings
            {
                // доп. множитель к базовой скорости печи (1 = без бонуса)
                [JsonProperty("Множитель скорости")]
                public float SpeedMultiplier
                { get; set; } = 1.0f;

                // мгновенная плавка: весь инпут за один цикл, топливо списывается честно
                [JsonProperty("Мгновенная плавка")]
                public bool Instant
                { get; set; } = false;
            }

            internal class OvenSettings
            {
                // базовый множитель печи — выставлен = рейту сервера (x5)
                [JsonProperty("Базовый множитель")]
                public int Multiplier
                { get; set; } = 5;

                [JsonProperty("Без топлива")]
                public bool NoFuelRequired
                { get; set; } = false;
            }

            internal class ProductSettings
            {
                public float CookTime
                { get; set; } = 10f;

                public int Amount
                { get; set; } = 1;

                [JsonIgnore]
                public int LowTemp
                { get; set; } = -1;

                [JsonIgnore]
                public int HighTemp
                { get; set; } = -1;
            }

            internal class FuelSettings
            {
                public float FuelAmount
                { get; set; }

                public string ByProduct;

                public int ByProductAmount
                { get; set; }

                public float ByProductChance
                { get; set; }
            }
        }
        #endregion

        #region --- CACHE ----------------------------------------------------------------------------------------------------
        private Cache Backup = new Cache();

        internal class Cache
        {
            public Dictionary<string, SettingsObject.ProductSettings> Products
            { get; set; } = new Dictionary<string, SettingsObject.ProductSettings>();

            public Dictionary<string, SettingsObject.FuelSettings> Fuel
            { get; set; } = new Dictionary<string, SettingsObject.FuelSettings>();
        }

        private static readonly List<string> Ovens = new List<string>
        {
            "bbq.deployed",
            "biofuel_generator.deployed",
            "campfire",
            "fireplace.deployed",
            "furnace.large",
            "furnace",
            "hobobarrel_static",
            "hobobarrel.deployed",
            "refinery_small_deployed",
            "skull_fire_pit",
            "small_refinery_static"
        };
        #endregion

        #region --- LOGGING --------------------------------------------------------------------------------------------------
        internal void WriteInfo(string format, params object[] args)
            => Puts("[INFO] " + format, args);

        internal void WriteWarning(string format, params object[] args)
            => Puts("[WARNING] " + format, args);

        internal void WriteError(string format, params object[] args)
            => Puts("[ERROR] " + format, args);
        #endregion
    }
}
