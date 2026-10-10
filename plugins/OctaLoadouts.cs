// OctaLoadouts — пресеты экипировки при возрождении.
// Серверные пресеты по правам/OctVIP-benefits + личные сохранения игроков.
// Один источник правил: /loadout, API и UI идут через те же обработчики.
// OctaKits (выдача наборов) не дублируется и не вызывается — отдельная система.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaLoadouts", "Octavian", "1.0.0")]
    [Description("Пресеты экипировки при возрождении: серверные по правам + личные loadout-ы игроков.")]
    public class OctaLoadouts : RustPlugin
    {
        #region Константы и поля

        private const string PermUse          = "octaloadouts.use";
        private const string PermAdmin        = "octaloadouts.admin";
        private const string PermAdminPresets = "octaloadouts.admin.presets";
        private const string PermAdminLimits  = "octaloadouts.admin.limits";
        private const string UiName           = "OctaLoadouts_UI";
        private const int    SchemaVersion    = 1;
        private const int    MaxNameLen       = 24;

        // OctaUI/OctVIP ищутся живым поиском (PITFALLS §11) — PluginReference не объявляем

        private Cfg _cfg;
        private int _rev = 1;                          // ревизия данных для envelope-ответов

        // Данные игроков — по файлу на uid: запись одного не портит остальных
        private readonly Dictionary<ulong, PlayerLo> _players = new Dictionary<ulong, PlayerLo>();
        private readonly HashSet<ulong> _dirty = new HashSet<ulong>();

        // Гард от двойной выдачи на один респавн (повторные вызовы хука/таймеров)
        private readonly Dictionary<ulong, float> _spawnApplied = new Dictionary<ulong, float>();

        // Идемпотентность мутирующих вызовов API (requestId → ответ, 300с)
        private readonly Dictionary<string, JObject> _idem = new Dictionary<string, JObject>();
        private readonly Dictionary<string, float> _idemAt = new Dictionary<string, float>();

        private static readonly string[] ContainerKeys = { "main", "belt", "wear" };

        #endregion

        #region Конфигурация

        private class Cfg
        {
            // "add" — докидывать в свободные слоты (дефолт, безопасно)
            // "replace" — Strip() всего инвентаря перед выдачей (явный opt-in админа)
            // "disabled" — авто-выдача выключена, пресеты только по командам
            [JsonProperty("Режим возрождения (add|replace|disabled)")]
            public string RespawnMode = "add";

            [JsonProperty("Сохранять заряженный магазин (глобально, по умолчанию выкл — баланс)")]
            public bool StoreLoadedAmmo = false;

            [JsonProperty("Задержка применения после респавна, сек")]
            public float ApplyDelay = 0f;

            [JsonProperty("Серверные пресеты")]
            public List<PresetCfg> Presets = new List<PresetCfg>();

            [JsonProperty("Миграция LoadoutController включена")]
            public bool MigrateLegacy = true;
        }

        private class PresetCfg
        {
            [JsonProperty("Ключ")]                    public string Key = "default";
            [JsonProperty("Название")]                public string DisplayName = "Стандартный";
            [JsonProperty("Приоритет")]               public int Priority;
            [JsonProperty("Включён")]                 public bool Enabled = true;
            [JsonProperty("Можно выбрать вручную")]   public bool Selectable = true;
            [JsonProperty("Авто-выдача при возрождении")] public bool AutoSpawn = true;
            [JsonProperty("Oxide permission")]        public string Permission = "";
            [JsonProperty("OctVIP benefit")]          public string Benefit = "";
            [JsonProperty("Личные сохранения разрешены")] public bool AllowPersonalSaves = true;
            [JsonProperty("Максимум личных сохранений")]  public int MaxPersonalSaves = 2;

            // "respect" — лимиты проверяются и при выдаче серверного пресета;
            // "adminOverride" — админ выдал что хотел, обходы пишутся в аудит
            [JsonProperty("Режим лимитов (respect|adminOverride)")]
            public string LimitsMode = "adminOverride";

            [JsonProperty("Лимиты личных сохранений")] public LimitsCfg Limits = new LimitsCfg();
            [JsonProperty("Предметы")]                 public List<LoItem> Items = new List<LoItem>();
        }

        private class LimitsCfg
        {
            // Семантика пропусков ЯВНАЯ: -1 = без отдельного лимита, 0 = запрещено,
            // ключ отсутствует → действует категорийный/общий лимит (а не «запрещено»).
            [JsonProperty("Максимум предметов всего (-1 = без лимита)")]
            public int MaxItemsTotal = -1;

            [JsonProperty("Максимум в main (-1 = без лимита)")]  public int MaxMain = -1;
            [JsonProperty("Максимум в belt (-1 = без лимита)")]  public int MaxBelt = -1;
            [JsonProperty("Максимум в wear (-1 = без лимита)")]  public int MaxWear = -1;

            [JsonProperty("Разрешённые категории (пусто = все)")]
            public List<string> AllowedCategories = new List<string>();

            [JsonProperty("Максимум по shortname (-1 = без лимита, 0 = запрещён)")]
            public Dictionary<string, int> MaxPerShortname = new Dictionary<string, int>();

            [JsonProperty("Запрещённые скины")]  public List<ulong> BannedSkins = new List<ulong>();
            [JsonProperty("Разрешённые скины (пусто = все)")] public List<ulong> AllowedSkins = new List<ulong>();

            [JsonProperty("Сохранять заряженный магазин (уровень пресета)")]
            public bool StoreAmmo = false;
            [JsonProperty("Разрешённые типы боеприпасов (пусто = все из сохранённых)")]
            public List<string> AllowedAmmo = new List<string>();
            [JsonProperty("Максимум патронов в магазине (-1 = без лимита)")]
            public int MaxLoadedAmmo = -1;

            [JsonProperty("Максимум модулей на оружие (-1 = без лимита)")]
            public int MaxAttachments = -1;
            [JsonProperty("Разрешённые модули (пусто = все)")]
            public List<string> AllowedMods = new List<string>();
        }

        protected override void LoadConfig()
        {
            // PITFALLS §1: Config == null при горячей загрузке Carbon
            if (Config == null)
            {
                var path = Path.Combine(Interface.Oxide.ConfigDirectory, Name + ".json");
                var dyn = new DynamicConfigFile(path);
                if (!dyn.Exists(null)) dyn.WriteObject(new Cfg());
                typeof(Plugin).GetProperty("Config").SetValue(this, dyn);
            }
            base.LoadConfig();
            try { _cfg = Config.ReadObject<Cfg>(); }
            catch (Exception ex) { PrintError($"Конфиг не прочитан, используются дефолты: {ex.Message}"); _cfg = null; }
            if (_cfg == null) _cfg = new Cfg();
            if (_cfg.Presets == null || _cfg.Presets.Count == 0) _cfg.Presets = DefaultPresets();
            SaveConfig();
        }

        protected override void LoadDefaultConfig() => _cfg = new Cfg { Presets = DefaultPresets() };

        protected override void SaveConfig()
        {
            if (_cfg == null) return;
            Config.WriteObject(_cfg, true);
        }

        private static List<PresetCfg> DefaultPresets() => new List<PresetCfg>
        {
            new PresetCfg
            {
                Key = "default", DisplayName = "Стартовый", Priority = 0,
                Limits = new LimitsCfg
                {
                    MaxItemsTotal = 12,
                    MaxPerShortname = new Dictionary<string, int>
                    {
                        ["rifle.ak"] = 0, ["rifle.bolt"] = 0, ["rifle.lr300"] = 0  // пример: 0 = запрещён
                    }
                }
            },
            new PresetCfg
            {
                Key = "vip", DisplayName = "VIP", Priority = 10, Benefit = "loadout.vip",
                MaxPersonalSaves = 4,
                Limits = new LimitsCfg { MaxItemsTotal = 20, StoreAmmo = true, MaxLoadedAmmo = 30, MaxAttachments = 3 }
            },
            new PresetCfg
            {
                Key = "event", DisplayName = "Ивентовый", Priority = 20, Permission = "octaloadouts.event",
                AllowPersonalSaves = false, AutoSpawn = false
            }
        };

        #endregion

        #region Модель данных

        // Предмет пресета: shortname — стабильный ключ, ItemId — подсказка совместимости
        private class LoItem
        {
            [JsonProperty("shortname")]  public string Short;
            [JsonProperty("itemId")]     public int ItemId;
            [JsonProperty("amount")]     public int Amount = 1;
            [JsonProperty("container")]  public string Cont = "main"; // main|belt|wear
            [JsonProperty("pos")]        public int Pos = -1;
            [JsonProperty("skin")]       public ulong Skin;
            [JsonProperty("ammoType")]   public string AmmoType;
            [JsonProperty("ammoCount")]  public int AmmoCount;
            [JsonProperty("mods")]       public List<string> Mods;

            internal JObject ToJson()
            {
                var o = new JObject
                {
                    ["shortname"] = Short, ["amount"] = Amount, ["container"] = Cont,
                    ["pos"] = Pos, ["skin"] = Skin
                };
                if (AmmoCount > 0 || !string.IsNullOrEmpty(AmmoType))
                { o["ammoType"] = AmmoType; o["ammoCount"] = AmmoCount; }
                if (Mods != null && Mods.Count > 0) o["mods"] = new JArray(Mods);
                return o;
            }
        }

        private class OwnPreset
        {
            [JsonProperty("id")]       public string Id;
            [JsonProperty("name")]     public string Name;
            [JsonProperty("level")]    public string LevelKey;   // уровень доступа, под которым сохранён
            [JsonProperty("saved")]    public long SavedUnix;
            [JsonProperty("items")]    public List<LoItem> Items = new List<LoItem>();
        }

        private class PlayerLo
        {
            [JsonProperty("schema")]    public int Schema = SchemaVersion;
            [JsonProperty("active")]    public string Active;      // "srv:<key>" | "own:<id>" | null
            [JsonProperty("presets")]   public List<OwnPreset> Presets = new List<OwnPreset>();
            [JsonProperty("migrated")]  public string MigratedFrom; // маркер идемпотентности миграции
        }

        #endregion

        #region Жизненный цикл

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermAdminPresets, this);
            permission.RegisterPermission(PermAdminLimits, this);
            permission.RegisterPermission("octaloadouts.event", this);
            LoadAllPlayers();
            if (_cfg.MigrateLegacy) TryMigrateLegacy();
        }

        private void Unload()
        {
            foreach (var pl in BasePlayer.activePlayerList)
                if (pl != null) CuiHelper.DestroyUi(pl, UiName);
            SaveAllDirty();
        }

        private void OnServerSave() => SaveAllDirty();

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiName);
            _spawnApplied.Remove((ulong)player.userID);
        }

        #endregion

        #region Хранилище

        private string DataPath(ulong uid) => $"OctaLoadouts/players/{uid}";

        private void LoadAllPlayers()
        {
            var dir = Path.Combine(Interface.Oxide.DataDirectory, "OctaLoadouts", "players");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var uidStr = Path.GetFileNameWithoutExtension(file);
                    ulong uid;
                    if (!ulong.TryParse(uidStr, out uid)) continue;
                    var pd = Interface.Oxide.DataFileSystem.ReadObject<PlayerLo>(DataPath(uid));
                    if (pd != null) _players[uid] = pd;
                }
                catch (Exception ex) { PrintError($"Не удалось прочитать данные {file}: {ex.Message}"); }
            }
            Puts($"Данных игроков: {_players.Count}");
        }

        private PlayerLo Pd(ulong uid)
        {
            PlayerLo pd;
            if (!_players.TryGetValue(uid, out pd)) pd = _players[uid] = new PlayerLo();
            return pd;
        }

        private void MarkDirty(ulong uid)
        {
            _dirty.Add(uid);
            _rev++;
            SavePlayer(uid); // сохраняем на каждое изменение — потеря данных недопустима
        }

        private void SavePlayer(ulong uid)
        {
            PlayerLo pd;
            if (!_players.TryGetValue(uid, out pd)) return;
            try
            {
                // .bak перед записью: ошибка записи не оставляет игрока без старых данных
                var fs = Interface.Oxide.DataFileSystem.GetFile(DataPath(uid));
                var path = fs?.Filename;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    try { File.Copy(path, path + ".bak", true); } catch { /* бэкап — лучшее усилие */ }
                Interface.Oxide.DataFileSystem.WriteObject(DataPath(uid), pd);
                _dirty.Remove(uid);
            }
            catch (Exception ex) { PrintError($"Запись данных {uid}: {ex.Message}"); }
        }

        private void SaveAllDirty()
        {
            foreach (var uid in _dirty.ToList()) SavePlayer(uid);
        }

        #endregion

        #region Интеграции

        // живой поиск — PluginReference залипает после reload цели (PITFALLS §11)
        private Plugin FindPlugin(string name)
        {
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
                if (p.Name == name && p.IsLoaded) return p;
            return null;
        }

        private Plugin UiLib() => FindPlugin("OctaUI");

        // deny-by-default: нет OctVIP → benefit-доступа нет
        private bool? HasBenefit(BasePlayer player, string benefitId)
        {
            if (string.IsNullOrEmpty(benefitId)) return null;
            var vip = FindPlugin("OctVIP");
            if (vip == null) return false;
            var res = vip.Call("Vip_HasBenefit", (ulong)player.userID, benefitId) as JObject;
            if (res == null) return false; // старый OctVIP без контракта → нет доступа к benefit-пресету
            return res["ok"]?.Value<bool>() == true && res["data"]?["has"]?.Value<bool>() == true;
        }

        #endregion

        #region Доступ и резолвинг пресетов

        private bool IsAdmin(BasePlayer p) =>
            p.IsAdmin || permission.UserHasPermission(p.UserIDString, PermAdmin);

        // Доступ к серверному пресету: permission И/ИЛИ benefit (оба заданы → нужны оба? Нет:
        // приоритет benefit, если задан; permission — дополнительный фильтр, если задан).
        private bool CanAccess(BasePlayer p, PresetCfg pr, out string reason)
        {
            reason = null;
            if (!pr.Enabled) { reason = "disabled"; return false; }
            if (!string.IsNullOrEmpty(pr.Permission) &&
                !permission.UserHasPermission(p.UserIDString, pr.Permission) &&
                !IsAdmin(p))
            {
                reason = "permission:" + pr.Permission;
                return false;
            }
            if (!string.IsNullOrEmpty(pr.Benefit) && !IsAdmin(p))
            {
                var has = HasBenefit(p, pr.Benefit);
                if (has != true)
                {
                    reason = FindPlugin("OctVIP") == null ? "provider_unavailable" : "benefit:" + pr.Benefit;
                    return false;
                }
            }
            return true;
        }

        // личный пресет доступен, если его уровень по-прежнему доступен игроку
        private bool OwnAccessible(BasePlayer p, OwnPreset own, out string reason)
        {
            var lvl = FindPreset(own.LevelKey);
            if (lvl == null) { reason = "level_removed"; return false; }
            if (!lvl.AllowPersonalSaves) { reason = "saves_disabled"; return false; }
            return CanAccess(p, lvl, out reason);
        }

        private PresetCfg FindPreset(string key) =>
            _cfg.Presets?.FirstOrDefault(pr => string.Equals(pr.Key, key, StringComparison.OrdinalIgnoreCase));

        // детерминированный выбор: максимальный Priority; при равенстве — ключ по алфавиту
        private PresetCfg ResolveAutoPreset(BasePlayer p)
        {
            PresetCfg best = null;
            foreach (var pr in _cfg.Presets)
            {
                if (!pr.AutoSpawn) continue;
                string reason;
                if (!CanAccess(p, pr, out reason)) continue;
                if (best == null || pr.Priority > best.Priority ||
                    pr.Priority == best.Priority && string.CompareOrdinal(pr.Key, best.Key) < 0)
                    best = pr;
            }
            return best;
        }

        // Что применять на респавне: личный активный → серверный авто по приоритету → ничего
        private List<LoItem> ResolveRespawnItems(BasePlayer p, out string srcDesc)
        {
            var uid = (ulong)p.userID;
            var pd = Pd(uid);
            srcDesc = null;
            if (!string.IsNullOrEmpty(pd.Active))
            {
                if (pd.Active.StartsWith("own:"))
                {
                    var own = pd.Presets?.FirstOrDefault(o => o.Id == pd.Active.Substring(4));
                    string reason;
                    if (own != null && OwnAccessible(p, own, out reason))
                    { srcDesc = "own:" + own.Name; return own.Items; }
                }
                else if (pd.Active.StartsWith("srv:"))
                {
                    var pr = FindPreset(pd.Active.Substring(4));
                    string reason;
                    if (pr != null && pr.Enabled && CanAccess(p, pr, out reason))
                    { srcDesc = "srv:" + pr.Key; return pr.Items; }
                }
            }
            var auto = ResolveAutoPreset(p);
            if (auto != null) { srcDesc = "auto:" + auto.Key; return auto.Items; }
            return null;
        }

        #endregion

        #region Валидация и захват инвентаря

        private ItemContainer ContainerOf(BasePlayer p, string cont)
        {
            switch (cont)
            {
                case "belt": return p.inventory.containerBelt;
                case "wear": return p.inventory.containerWear;
                default:   return p.inventory.containerMain;
            }
        }

        private int ContainerMax(LimitsCfg lim, string cont)
        {
            switch (cont)
            {
                case "belt": return lim.MaxBelt;
                case "wear": return lim.MaxWear;
                default:   return lim.MaxMain;
            }
        }

        // Проверка предмета на возможность сохранения. Возвращает код причины или null.
        private string CheckSaveAllowed(Item item, LimitsCfg lim, Dictionary<string, int> perShort,
                                        Dictionary<string, int> perCont, int keptTotal)
        {
            var def = item.info;
            if (def == null) return "unknown";
            var baseDef = def.isRedirectOf ?? def; // скин-варианты → база (PITFALLS §2)

            if (lim.BannedSkins != null && lim.BannedSkins.Contains(item.skin)) return "banned_skin";
            if (lim.AllowedSkins != null && lim.AllowedSkins.Count > 0 && !lim.AllowedSkins.Contains(item.skin))
                return "skin_not_allowed";
            if (lim.AllowedCategories != null && lim.AllowedCategories.Count > 0 &&
                !lim.AllowedCategories.Contains(baseDef.category.ToString()))
                return "category:" + baseDef.category;

            int perShortLimit;
            if (lim.MaxPerShortname != null && lim.MaxPerShortname.TryGetValue(baseDef.shortname, out perShortLimit))
            {
                if (perShortLimit == 0) return "banned_item";
                int used;
                perShort.TryGetValue(baseDef.shortname, out used);
                if (perShortLimit > 0 && used >= perShortLimit) return "shortname_limit";
            }

            if (lim.MaxItemsTotal >= 0 && keptTotal >= lim.MaxItemsTotal) return "total_limit";

            int contMax = ContainerMax(lim, ContainerKeyOf(item));
            if (contMax >= 0)
            {
                int used;
                perCont.TryGetValue(ContainerKeyOf(item), out used);
                if (used >= contMax) return "container_limit";
            }
            return null;
        }

        private static string ContainerKeyOf(Item item)
        {
            var cont = item.GetRootContainer();
            var owner = cont?.GetOwnerPlayer() as BasePlayer;
            if (owner != null)
            {
                if (cont == owner.inventory.containerBelt) return "belt";
                if (cont == owner.inventory.containerWear) return "wear";
            }
            return "main";
        }

        // Захват инвентаря с отчётом: kept — что войдёт в пресет, skipped — что отфильтруется и почему
        private class CaptureReport
        {
            public List<LoItem> Kept = new List<LoItem>();
            public List<JObject> Skipped = new List<JObject>();
        }

        private CaptureReport CaptureInventory(BasePlayer p, LimitsCfg lim)
        {
            var rep = new CaptureReport();
            var perShort = new Dictionary<string, int>();
            var perCont = new Dictionary<string, int>();
            foreach (var kv in new[]
                     {
                         new KeyValuePair<string, ItemContainer>("main", p.inventory.containerMain),
                         new KeyValuePair<string, ItemContainer>("belt", p.inventory.containerBelt),
                         new KeyValuePair<string, ItemContainer>("wear", p.inventory.containerWear)
                     })
            {
                var cont = kv.Value;
                if (cont?.itemList == null) continue;
                foreach (var item in cont.itemList)
                {
                    if (item == null || !item.IsValid()) continue;
                    var def = item.info;
                    var baseDef = def?.isRedirectOf ?? def;
                    var shortname = baseDef?.shortname ?? "unknown";

                    var reason = CheckSaveAllowed(item, lim, perShort, perCont, rep.Kept.Count);
                    if (reason != null)
                    {
                        rep.Skipped.Add(new JObject
                        { ["shortname"] = shortname, ["amount"] = item.amount, ["reason"] = reason });
                        continue;
                    }

                    var li = new LoItem
                    {
                        Short = shortname,
                        ItemId = baseDef.itemid,
                        Amount = item.amount,
                        Cont = kv.Key,
                        Pos = item.position,
                        Skin = item.skin
                    };

                    // боеприпасы — только если разрешены политикой уровня
                    var proj = item.GetHeldEntity() as BaseProjectile;
                    if (proj != null && proj.primaryMagazine != null)
                    {
                        var storeAmmo = lim.StoreAmmo || _cfg.StoreLoadedAmmo;
                        var atype = proj.primaryMagazine.ammoType?.shortname;
                        if (storeAmmo && proj.primaryMagazine.contents > 0 &&
                            (lim.AllowedAmmo == null || lim.AllowedAmmo.Count == 0 || lim.AllowedAmmo.Contains(atype)))
                        {
                            li.AmmoType = atype;
                            li.AmmoCount = lim.MaxLoadedAmmo < 0
                                ? proj.primaryMagazine.contents
                                : Math.Min(proj.primaryMagazine.contents, lim.MaxLoadedAmmo);
                        }
                    }

                    // модули оружия — из item.contents, только для стволов
                    if (proj != null && item.contents?.itemList != null && item.contents.itemList.Count > 0)
                    {
                        var mods = new List<string>();
                        foreach (var mod in item.contents.itemList)
                        {
                            var md = mod?.info;
                            if (md == null) continue;
                            if (lim.AllowedMods != null && lim.AllowedMods.Count > 0 &&
                                !lim.AllowedMods.Contains(md.shortname))
                            {
                                rep.Skipped.Add(new JObject
                                { ["shortname"] = md.shortname, ["amount"] = 1, ["reason"] = "mod_not_allowed" });
                                continue;
                            }
                            if (lim.MaxAttachments >= 0 && mods.Count >= lim.MaxAttachments)
                            {
                                rep.Skipped.Add(new JObject
                                { ["shortname"] = md.shortname, ["amount"] = 1, ["reason"] = "mods_limit" });
                                continue;
                            }
                            mods.Add(md.shortname);
                        }
                        if (mods.Count > 0) li.Mods = mods;
                    }

                    rep.Kept.Add(li);
                    perShort[shortname] = (perShort.ContainsKey(shortname) ? perShort[shortname] : 0) + 1;
                    perCont[kv.Key] = (perCont.ContainsKey(kv.Key) ? perCont[kv.Key] : 0) + 1;
                }
            }
            return rep;
        }

        // Проверка сохранённого предмета при загрузке/выдаче: определение должно существовать,
        // слот и количество — валидными; битая запись пропускается, не роняя остальные
        private ItemDefinition ValidateItem(LoItem li)
        {
            if (li == null || string.IsNullOrEmpty(li.Short) || li.Amount <= 0) return null;
            var def = ItemManager.FindItemDefinition(li.Short);
            if (def == null && li.ItemId != 0 && li.ItemId != -1)
                def = ItemManager.FindItemDefinition(li.ItemId); // shortname устарел → ItemId-подсказка
            if (def == null) return null;
            if (li.Cont != "belt" && li.Cont != "wear") li.Cont = "main";
            if (li.Amount > def.stackable) li.Amount = Math.Max(1, def.stackable);
            return def;
        }

        #endregion

        #region Выдача и респавн

        // Одна операция на один респавн: повторный вызов хука/колбэка — no-op
        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null || player.IsNpc) return;
            if (string.Equals(_cfg.RespawnMode, "disabled", StringComparison.OrdinalIgnoreCase)) return;
            var uid = (ulong)player.userID;
            float last;
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (_spawnApplied.TryGetValue(uid, out last) && now - last < 4f) return;
            _spawnApplied[uid] = now;
            var delay = _cfg.ApplyDelay <= 0 ? 0f : _cfg.ApplyDelay;
            if (delay <= 0f) NextTick(() => ApplyLoadout(player));
            else timer.Once(delay, () => ApplyLoadout(player));
        }

        private void ApplyLoadout(BasePlayer player)
        {
            if (player == null || !player.IsConnected || player.IsDead() || player.IsNpc) return;
            string src;
            var items = ResolveRespawnItems(player, out src);
            if (items == null || items.Count == 0) return;
            GiveLoadout(player, items, src);
        }

        // Выдача: replace = Strip() по явному opt-in; add = в свободные слоты,
        // не влезшее падает под ноги — ничего не теряется молча
        private void GiveLoadout(BasePlayer player, List<LoItem> items, string srcDesc)
        {
            if (string.Equals(_cfg.RespawnMode, "replace", StringComparison.OrdinalIgnoreCase))
                player.inventory.Strip();

            int given = 0, skipped = 0;
            foreach (var li in items)
            {
                var def = ValidateItem(li);
                if (def == null) { skipped++; continue; }

                var item = ItemManager.CreateByItemID(def.itemid, Math.Max(1, li.Amount), li.Skin);
                if (item == null) { skipped++; continue; }

                var proj = item.GetHeldEntity() as BaseProjectile;
                if (proj != null && proj.primaryMagazine != null)
                {
                    if (!string.IsNullOrEmpty(li.AmmoType))
                    {
                        var aDef = ItemManager.FindItemDefinition(li.AmmoType);
                        if (aDef != null) proj.primaryMagazine.ammoType = aDef;
                    }
                    proj.primaryMagazine.contents = Math.Max(0, li.AmmoCount);

                    if (li.Mods != null && item.contents != null)
                    {
                        foreach (var modName in li.Mods)
                        {
                            var md = ItemManager.FindItemDefinition(modName);
                            if (md == null) continue;
                            var mod = ItemManager.CreateByItemID(md.itemid, 1, 0);
                            if (mod == null) continue;
                            if (!mod.MoveToContainer(item.contents)) mod.Remove();
                        }
                    }
                    item.MarkDirty();
                }

                var cont = ContainerOf(player, li.Cont);
                if (cont == null) { item.Remove(); skipped++; continue; }
                bool ok = li.Pos >= 0 && item.MoveToContainer(cont, li.Pos, false);
                if (!ok) ok = item.MoveToContainer(cont, -1, false);
                if (!ok)
                {
                    // все контейнеры перебираем, потом — дроп под ноги
                    ok = item.MoveToContainer(player.inventory.containerMain, -1, false) ||
                         item.MoveToContainer(player.inventory.containerBelt, -1, false);
                    if (!ok) item.Drop(player.GetDropPosition(), Vector3.up * 0.5f);
                }
                given++;
            }
            if (given > 0 || skipped > 0)
            {
                Msg(player, skipped > 0
                    ? $"Применён loadout ({srcDesc}): выдано {given}, пропущено {skipped}"
                    : $"Применён loadout ({srcDesc}): выдано {given}");
            }
        }

        #endregion

        #region Внутренние операции (общий путь для команд и API)

        private (string code, string msg, JObject data) SavePersonal(BasePlayer p, string name)
        {
            var uid = (ulong)p.userID;
            var lvl = _cfg.Presets?.Where(pr => pr.AllowPersonalSaves)
                          .Where(pr => { string r; return CanAccess(p, pr, out r); })
                          .OrderByDescending(pr => pr.Priority).FirstOrDefault();
            if (lvl == null) return ("NO_PERMISSION", "Нет уровня с личными сохранениями", null);
            var pd = Pd(uid);
            if (pd.Presets.Count >= lvl.MaxPersonalSaves)
                return ("LIMIT_REACHED", $"Лимит личных пресетов: {lvl.MaxPersonalSaves}", null);

            name = SanitizeName(name);
            if (string.IsNullOrEmpty(name)) return ("INVALID_ARGUMENT", "Укажи имя пресета", null);

            var rep = CaptureInventory(p, lvl.Limits);
            if (rep.Kept.Count == 0) return ("INVALID_ARGUMENT", "В инвентаре нет предметов, разрешённых к сохранению", null);

            var own = new OwnPreset
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = name,
                LevelKey = lvl.Key,
                SavedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Items = rep.Kept
            };
            pd.Presets.Add(own);
            MarkDirty(uid);
            var data = new JObject
            {
                ["id"] = own.Id, ["name"] = own.Name, ["level"] = lvl.Key,
                ["kept"] = rep.Kept.Count,
                ["skipped"] = new JArray(rep.Skipped)
            };
            return ("OK", "Сохранено", data);
        }

        private (string code, string msg, JObject data) SelectPreset(BasePlayer p, string sel)
        {
            var uid = (ulong)p.userID;
            var pd = Pd(uid);
            if (string.IsNullOrEmpty(sel) || sel == "none") { pd.Active = null; MarkDirty(uid); return ("OK", "Выбор сброшен", null); }
            if (sel.StartsWith("srv:"))
            {
                var pr = FindPreset(sel.Substring(4));
                if (pr == null) return ("NOT_FOUND", "Пресет не найден", null);
                if (!pr.Selectable) return ("NO_PERMISSION", "Этот пресет нельзя выбрать вручную", null);
                string reason;
                if (!CanAccess(p, pr, out reason)) return ("NO_PERMISSION", reason ?? "no_access", null);
                pd.Active = sel; MarkDirty(uid);
                return ("OK", "Выбран серверный пресет", null);
            }
            if (sel.StartsWith("own:"))
            {
                var own = pd.Presets?.FirstOrDefault(o => o.Id == sel.Substring(4));
                if (own == null) return ("NOT_FOUND", "Личный пресет не найден", null);
                string reason;
                if (!OwnAccessible(p, own, out reason)) return ("NO_PERMISSION", reason ?? "no_access", null);
                pd.Active = sel; MarkDirty(uid);
                return ("OK", "Выбран личный пресет", null);
            }
            return ("INVALID_ARGUMENT", "Неизвестный идентификатор пресета", null);
        }

        private (string code, string msg, JObject data) DeletePreset(BasePlayer p, string id)
        {
            var uid = (ulong)p.userID;
            var pd = Pd(uid);
            var own = pd.Presets?.FirstOrDefault(o => o.Id == id);
            if (own == null) return ("NOT_FOUND", "Личный пресет не найден", null);
            pd.Presets.Remove(own);
            if (pd.Active == "own:" + id) pd.Active = null;
            MarkDirty(uid);
            return ("OK", $"Пресет «{own.Name}» удалён", null);
        }

        private static string SanitizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Replace("<", "").Replace(">", "").Replace("&", "").Trim();
            return s.Length > MaxNameLen ? s.Substring(0, MaxNameLen) : s;
        }

        #endregion

        #region Octa API v1

        private JObject Res(bool ok, string code, string msg, object data = null)
        {
            var o = new JObject { ["ok"] = ok, ["code"] = code, ["msg"] = msg ?? "", ["rev"] = _rev };
            o["data"] = data != null ? JToken.FromObject(data) : JValue.CreateNull();
            return o;
        }

        private JObject ResErr(string code, string msg) => Res(false, code, msg);
        private JObject ResOk(object data, string msg = "") => Res(true, "OK", msg, data);

        private bool IdemHit(string requestId, out JObject cached)
        {
            cached = null;
            if (string.IsNullOrEmpty(requestId)) return false;
            float at;
            if (_idem.TryGetValue(requestId, out cached) && _idemAt.TryGetValue(requestId, out at) &&
                UnityEngine.Time.realtimeSinceStartup - at < 300f)
                return true;
            cached = null;
            return false;
        }

        private void IdemStore(string requestId, JObject res)
        {
            if (string.IsNullOrEmpty(requestId) || res == null) return;
            _idem[requestId] = res;
            _idemAt[requestId] = UnityEngine.Time.realtimeSinceStartup;
        }

        [HookMethod("Octa_GetManifest")]
        private object API_Manifest() => new JObject
        {
            ["module"] = "OctaLoadouts",
            ["version"] = "1.0.0",
            ["api"] = 1,
            ["methods"] = new JArray("Loadouts_GetCatalog", "Loadouts_GetPlayerState", "Loadouts_GetPreset",
                                     "Loadouts_PreviewSave", "Loadouts_Save", "Loadouts_Select", "Loadouts_Delete")
        };

        // Каталог: серверные пресеты (с доступностью) + личные сохранения игрока
        [HookMethod("Loadouts_GetCatalog")]
        private object API_GetCatalog(BasePlayer player)
        {
            if (player == null || player.IsNpc) return ResErr("INVALID_ARGUMENT", "player required");
            var uid = (ulong)player.userID;
            var pd = Pd(uid);
            var srv = new JArray();
            foreach (var pr in _cfg.Presets ?? new List<PresetCfg>())
            {
                string reason;
                var access = CanAccess(player, pr, out reason);
                srv.Add(new JObject
                {
                    ["key"] = pr.Key, ["name"] = pr.DisplayName, ["priority"] = pr.Priority,
                    ["enabled"] = pr.Enabled, ["selectable"] = pr.Selectable, ["autoSpawn"] = pr.AutoSpawn,
                    ["itemsCount"] = pr.Items?.Count ?? 0,
                    ["allowSaves"] = pr.AllowPersonalSaves, ["maxSaves"] = pr.MaxPersonalSaves,
                    ["accessible"] = access, ["reason"] = reason,
                    ["active"] = pd.Active == "srv:" + pr.Key
                });
            }
            var own = new JArray();
            foreach (var o in pd.Presets ?? new List<OwnPreset>())
            {
                string reason;
                var acc = OwnAccessible(player, o, out reason);
                own.Add(new JObject
                {
                    ["id"] = o.Id, ["name"] = o.Name, ["level"] = o.LevelKey,
                    ["itemsCount"] = o.Items?.Count ?? 0, ["saved"] = o.SavedUnix,
                    ["accessible"] = acc, ["reason"] = reason,
                    ["active"] = pd.Active == "own:" + o.Id
                });
            }
            return ResOk(new JObject
            {
                ["server"] = srv, ["own"] = own,
                ["active"] = pd.Active, ["mode"] = _cfg.RespawnMode
            });
        }

        [HookMethod("Loadouts_GetPlayerState")]
        private object API_GetPlayerState(BasePlayer player)
        {
            if (player == null) return ResErr("INVALID_ARGUMENT", "player required");
            var uid = (ulong)player.userID;
            var pd = Pd(uid);
            var lvl = _cfg.Presets?.Where(pr => pr.AllowPersonalSaves)
                          .Where(pr => { string r; return CanAccess(player, pr, out r); })
                          .OrderByDescending(pr => pr.Priority).FirstOrDefault();
            return ResOk(new JObject
            {
                ["active"] = pd.Active,
                ["ownCount"] = pd.Presets?.Count ?? 0,
                ["ownLimit"] = lvl?.MaxPersonalSaves ?? 0,
                ["saveLevel"] = lvl?.Key,
                ["mode"] = _cfg.RespawnMode
            });
        }

        [HookMethod("Loadouts_GetPreset")]
        private object API_GetPreset(BasePlayer player, string presetRef)
        {
            if (player == null || string.IsNullOrEmpty(presetRef)) return ResErr("INVALID_ARGUMENT", "preset required");
            if (presetRef.StartsWith("srv:"))
            {
                var pr = FindPreset(presetRef.Substring(4));
                if (pr == null) return ResErr("NOT_FOUND", "preset not found");
                string reason;
                var access = CanAccess(player, pr, out reason);
                if (!access) return ResErr("NO_PERMISSION", reason);
                return ResOk(new JObject
                {
                    ["key"] = pr.Key, ["name"] = pr.DisplayName,
                    ["items"] = new JArray((pr.Items ?? new List<LoItem>()).Select(i => i.ToJson())),
                    ["limits"] = JObject.FromObject(pr.Limits ?? new LimitsCfg())
                });
            }
            if (presetRef.StartsWith("own:"))
            {
                var uid = (ulong)player.userID;
                var own = Pd(uid).Presets?.FirstOrDefault(o => o.Id == presetRef.Substring(4));
                if (own == null) return ResErr("NOT_FOUND", "preset not found");
                string reason;
                if (!OwnAccessible(player, own, out reason)) return ResErr("NO_PERMISSION", reason);
                return ResOk(new JObject
                {
                    ["id"] = own.Id, ["name"] = own.Name, ["level"] = own.LevelKey,
                    ["items"] = new JArray((own.Items ?? new List<LoItem>()).Select(i => i.ToJson()))
                });
            }
            return ResErr("INVALID_ARGUMENT", "bad preset ref");
        }

        // Превью: что войдёт и что отфильтруется, БЕЗ сохранения
        [HookMethod("Loadouts_PreviewSave")]
        private object API_PreviewSave(BasePlayer player)
        {
            if (player == null) return ResErr("INVALID_ARGUMENT", "player required");
            var lvl = _cfg.Presets?.Where(pr => pr.AllowPersonalSaves)
                          .Where(pr => { string r; return CanAccess(player, pr, out r); })
                          .OrderByDescending(pr => pr.Priority).FirstOrDefault();
            if (lvl == null) return ResErr("NO_PERMISSION", "no save level");
            var rep = CaptureInventory(player, lvl.Limits);
            return ResOk(new JObject
            {
                ["level"] = lvl.Key,
                ["kept"] = new JArray(rep.Kept.Select(i => i.ToJson())),
                ["skipped"] = new JArray(rep.Skipped)
            });
        }

        [HookMethod("Loadouts_Save")]
        private object API_Save(BasePlayer player, string name, string requestId)
        {
            if (player == null || player.IsNpc) return ResErr("INVALID_ARGUMENT", "player required");
            JObject cached;
            if (IdemHit(requestId, out cached)) return cached;
            var r = SavePersonal(player, name);
            var res = r.code == "OK" ? Res(true, r.code, r.msg, r.data) : ResErr(r.code, r.msg);
            IdemStore(requestId, res);
            return res;
        }

        [HookMethod("Loadouts_Select")]
        private object API_Select(BasePlayer player, string presetRef)
        {
            if (player == null) return ResErr("INVALID_ARGUMENT", "player required");
            var r = SelectPreset(player, presetRef);
            return r.code == "OK" ? ResOk(null, r.msg) : ResErr(r.code, r.msg);
        }

        [HookMethod("Loadouts_Delete")]
        private object API_Delete(BasePlayer player, string presetId, string requestId)
        {
            if (player == null) return ResErr("INVALID_ARGUMENT", "player required");
            JObject cached;
            if (IdemHit(requestId, out cached)) return cached;
            var r = DeletePreset(player, presetId);
            var res = r.code == "OK" ? ResOk(null, r.msg) : ResErr(r.code, r.msg);
            IdemStore(requestId, res);
            return res;
        }

        #endregion

        #region Команды игрока

        // фирменный префикс — золотой (PITFALLS §9)
        private void Msg(BasePlayer p, string text) =>
            p?.ChatMessage($"<color=#E8B84B>◆ OctaLoadouts</color> {text}");

        [ChatCommand("loadout")]
        private void CmdLoadout(BasePlayer p, string cmd, string[] args)
        {
            if (p == null || p.IsNpc) return;
            args = args ?? new string[0];
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            if (sub == "" || sub == "help")
            {
                if (UiLib() != null && sub == "") { UiOpen(p); return; }
                Chat(p, "◆ <color=#E8B84B>OctaLoadouts</color> — пресеты экипировки\n" +
                        "  /loadout — открыть меню\n" +
                        "  /loadout list — доступные и личные пресеты\n" +
                        "  /loadout save <имя> — сохранить инвентарь (превью)\n" +
                        "  /loadout select srv:<ключ> | own:<id> | none — выбор для респавна\n" +
                        "  /loadout delete <id> — удалить свой пресет");
                if (sub == "") UiOpen(p);
                return;
            }

            switch (sub)
            {
                case "list":
                {
                    var cat = API_GetCatalog(p) as JObject;
                    if (cat == null || cat["ok"]?.Value<bool>() != true) { Chat(p, "Нет данных"); return; }
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("◆ <color=#E8B84B>OctaLoadouts</color>");
                    foreach (var s in (JArray)cat["data"]["server"])
                    {
                        var mark = s["active"]?.Value<bool>() == true ? " ▶" : "";
                        var st = s["accessible"]?.Value<bool>() == true ? "" : $" <color=#E06666>({s["reason"]})</color>";
                        sb.AppendLine($"  srv:{s["key"]} — {s["name"]}{mark}{st}");
                    }
                    foreach (var o in (JArray)cat["data"]["own"])
                    {
                        var mark = o["active"]?.Value<bool>() == true ? " ▶" : "";
                        var st = o["accessible"]?.Value<bool>() == true ? "" : $" <color=#E06666>({o["reason"]})</color>";
                        sb.AppendLine($"  own:{o["id"]} — {o["name"]}{mark}{st}");
                    }
                    Chat(p, sb.ToString());
                    return;
                }
                case "save":
                {
                    if (args.Length < 2) { Chat(p, "Укажи имя: /loadout save <имя>"); return; }
                    if (args.Length >= 3 && args[2] == "confirm")
                    {
                        var r = SavePersonal(p, args[1]);
                        Chat(p, r.code == "OK"
                            ? $"Сохранено «{r.data["name"]}»: предметов {r.data["kept"]}, отфильтровано {((JArray)r.data["skipped"]).Count}"
                            : $"Не сохранено: {r.msg}");
                        return;
                    }
                    var prev = API_PreviewSave(p) as JObject;
                    if (prev == null || prev["ok"]?.Value<bool>() != true)
                    { Chat(p, $"Превью недоступно: {prev?["msg"]}"); return; }
                    var kept = (JArray)prev["data"]["kept"];
                    var skipped = (JArray)prev["data"]["skipped"];
                    var sb2 = new System.Text.StringBuilder();
                    sb2.AppendLine($"◆ Превью «{args[1]}» — войдёт {kept.Count} предм.:");
                    foreach (var it in kept.Take(12))
                        sb2.AppendLine($"  <color=#9BC76B>✓</color> {it["shortname"]} x{it["amount"]}");
                    if (kept.Count > 12) sb2.AppendLine($"  … и ещё {kept.Count - 12}");
                    foreach (var it in skipped)
                        sb2.AppendLine($"  <color=#E06666>✗</color> {it["shortname"]} — {it["reason"]}");
                    sb2.AppendLine($"Подтверди: /loadout save {args[1]} confirm");
                    Chat(p, sb2.ToString());
                    return;
                }
                case "select":
                {
                    if (args.Length < 2) { Chat(p, "Укажи: /loadout select srv:<ключ> | own:<id> | none"); return; }
                    var r = SelectPreset(p, args[1]);
                    Chat(p, r.code == "OK" ? $"✓ {r.msg}" : $"✗ {r.msg}");
                    return;
                }
                case "delete":
                {
                    if (args.Length < 2) { Chat(p, "Укажи: /loadout delete <id>"); return; }
                    var r = DeletePreset(p, args[1]);
                    Chat(p, r.code == "OK" ? $"✓ {r.msg}" : $"✗ {r.msg}");
                    return;
                }
                case "rename":
                {
                    if (args.Length < 3) { Chat(p, "Укажи: /loadout rename <id> <имя>"); return; }
                    var pd2 = Pd((ulong)p.userID);
                    var own = pd2.Presets?.FirstOrDefault(o => o.Id == args[1]);
                    if (own == null) { Chat(p, "Пресет не найден"); return; }
                    var nm = SanitizeName(args[2]);
                    if (string.IsNullOrEmpty(nm)) { Chat(p, "Плохое имя"); return; }
                    own.Name = nm; MarkDirty((ulong)p.userID);
                    Chat(p, $"Переименовано в «{nm}»");
                    return;
                }
            }
            Chat(p, "Неизвестная команда. /loadout help");
        }

        private void Chat(BasePlayer p, string text)
        {
            foreach (var line in text.Split('\n'))
                Msg(p, line.TrimEnd());
        }

        #endregion

        #region Админ-команды (консоль)

        [ConsoleCommand("octaloadouts.list")]
        private void CCmdList(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return; // §15: консольные команды проверяют сами
            foreach (var pr in _cfg.Presets)
                Puts($"[{pr.Key}] «{pr.DisplayName}» prio={pr.Priority} enabled={pr.Enabled} auto={pr.AutoSpawn} items={pr.Items?.Count ?? 0}");
        }

        // Админский импорт из своего инвентаря — только через превью (octaloadouts.import <key>)
        // и подтверждение (octaloadouts.import <key> confirm), с записью аудита
        [ConsoleCommand("octaloadouts.import")]
        private void CCmdImport(ConsoleSystem.Arg arg)
        {
            var p = arg.Connection?.player as BasePlayer;
            if (p == null) { arg.ReplyWith("только в игре"); return; }
            if (!p.IsAdmin && !permission.UserHasPermission(p.UserIDString, PermAdminPresets)) return;
            var key = arg.GetString(0);
            if (string.IsNullOrEmpty(key)) { arg.ReplyWith("octaloadouts.import <key> [confirm]"); return; }
            var pr = FindPreset(key);
            if (pr == null) { arg.ReplyWith("пресет не найден"); return; }
            var rep = CaptureInventory(p, new LimitsCfg()); // без лимитов — админ видит всё
            if (!string.Equals(arg.GetString(1), "confirm", StringComparison.OrdinalIgnoreCase))
            {
                arg.ReplyWith($"Превью импорта в «{key}»: {rep.Kept.Count} предметов, отфильтровано {rep.Skipped.Count}. " +
                              $"Подтверди: octaloadouts.import {key} confirm");
                return;
            }
            pr.Items = rep.Kept;
            SaveConfig();
            Audit(p, $"import preset={key} items={rep.Kept.Count} mode={pr.LimitsMode}");
            arg.ReplyWith($"«{key}» обновлён: {rep.Kept.Count} предметов (аудит записан)");
        }

        private void Audit(BasePlayer who, string what)
        {
            try
            {
                var dir = Path.Combine(Interface.Oxide.DataDirectory, "OctaLoadouts");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "audit.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {who?.UserIDString} {who?.displayName}: {what}\n");
            }
            catch (Exception ex) { PrintError($"audit: {ex.Message}"); }
        }

        [ConsoleCommand("octaloadouts.migrate")]
        private void CCmdMigrate(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            var rep = TryMigrateLegacy(true);
            arg.ReplyWith(rep);
        }

        #endregion

        #region Миграция LoadoutController

        // Читает legacy-файлы (НЕ изменяя их), конвертирует валидное, пишет отчёт.
        // Идемпотентно: маркер MigratedFrom + файл-отчёт — повтор не создаёт дублей.
        private string TryMigrateLegacy(bool verbose = false)
        {
            var report = new List<string>();
            int okCnt = 0, skipCnt = 0;
            try
            {
                var legDir = Path.Combine(Interface.Oxide.DataDirectory, "LoadoutController", "PlayerLoadouts");
                if (!Directory.Exists(legDir))
                    return verbose ? "LoadoutController/PlayerLoadouts не найден — миграция не требуется" : null;

                var repDir = Path.Combine(Interface.Oxide.DataDirectory, "OctaLoadouts");
                Directory.CreateDirectory(repDir);
                foreach (var file in Directory.GetFiles(legDir, "*.json"))
                {
                    var uidStr = Path.GetFileNameWithoutExtension(file);
                    ulong uid;
                    if (!ulong.TryParse(uidStr, out uid)) { skipCnt++; report.Add($"{uidStr}: не steamid — пропуск"); continue; }

                    var pd = Pd(uid);
                    if (pd.MigratedFrom == "LoadoutController") { continue; } // уже мигрирован

                    JObject leg;
                    try { leg = JObject.Parse(File.ReadAllText(file)); }
                    catch (Exception ex) { skipCnt++; report.Add($"{uid}: не JSON ({ex.Message})"); continue; }

                    var items = new List<LoItem>();
                    // Толерантный парсинг: ищем массивы предметов по типичным именам
                    foreach (var prop in leg.Properties())
                    {
                        var arr = prop.Value as JArray;
                        if (arr == null) continue;
                        foreach (var el in arr.OfType<JObject>())
                        {
                            var it = LegacyItem(el);
                            if (it != null) items.Add(it);
                        }
                    }
                    if (items.Count == 0) { skipCnt++; report.Add($"{uid}: валидных предметов не найдено"); continue; }

                    pd.Presets.Add(new OwnPreset
                    {
                        Id = "legacy" + uidStr.Substring(Math.Max(0, uidStr.Length - 4)),
                        Name = "Мигрированный",
                        LevelKey = _cfg.Presets.FirstOrDefault()?.Key ?? "default",
                        SavedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Items = items
                    });
                    pd.MigratedFrom = "LoadoutController";
                    MarkDirty(uid);
                    okCnt++;
                    report.Add($"{uid}: перенесено предметов {items.Count}");
                }
                var stamp = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} миграция: ok={okCnt} skip={skipCnt}";
                File.AppendAllText(Path.Combine(repDir, "migration-report.log"),
                    stamp + "\n" + string.Join("\n", report) + "\n\n");
                return $"{stamp}; подробности — data/OctaLoadouts/migration-report.log";
            }
            catch (Exception ex)
            {
                PrintError($"Миграция: {ex}");
                return $"Миграция провалилась: {ex.Message}";
            }
        }

        // Лучшее-усилие парсинг записи старого формата: поля называются иначе —
        // берём из нескольких вариантов, shortname обязателен
        private LoItem LegacyItem(JObject el)
        {
            var sn = (string)(el["Shortname"] ?? el["shortname"] ?? el["ShortName"]);
            if (string.IsNullOrEmpty(sn)) return null;
            var def = ItemManager.FindItemDefinition(sn);
            if (def == null) return null;
            return new LoItem
            {
                Short = def.shortname,
                ItemId = def.itemid,
                Amount = Math.Max(1, (int?)(el["Amount"] ?? el["amount"]) ?? 1),
                Cont = (string)(el["Container"] ?? el["container"]) ?? "main",
                Pos = (int?)(el["Position"] ?? el["pos"]) ?? -1,
                Skin = (ulong?)(el["SkinID"] ?? el["skin"] ?? el["Skin"]) ?? 0UL,
                AmmoType = (string)(el["Ammotype"] ?? el["ammoType"]),
                AmmoCount = (int?)(el["Ammo"] ?? el["ammoCount"]) ?? 0
            };
        }

        #endregion

        #region UI (OctaUI, опционально)

        // Живой поиск OctaUI; при отсутствии — команды/чат продолжают работать
        private void UiOpen(BasePlayer p) => UiDraw(p, null);

        private void UiDraw(BasePlayer p, string sel)
        {
            var ui = UiLib();
            if (ui == null) { Chat(p, "UI недоступен (нет OctaUI) — используй /loadout list"); return; }
            var cat = API_GetCatalog(p) as JObject;
            if (cat == null || cat["ok"]?.Value<bool>() != true) { Chat(p, "Нет данных каталога"); return; }

            var c = new CuiElementContainer();
            var root = ui.Call("Root", c, new JObject
            {
                ["name"] = UiName, ["title"] = "OCTALOADOUTS",
                ["subtitle"] = "Пресеты экипировки при возрождении",
                ["icon"] = "sprite:assets/icons/gear.png",
                ["close"] = "octaloadoutsui close", ["accent"] = "gold",
                ["rect"] = "0.15 0.10 0.85 0.90"
            }) as JObject;
            if (root == null) return;
            var content = (string)root["content"];

            // Левая колонка: серверные + личные
            ui.Call("Section", c, content, UiName + "_S1", new JObject
            { ["rect"] = "0.01 0.90 0.32 0.97", ["text"] = "СЕРВЕРНЫЕ ПРЕСЕТЫ", ["accent"] = "gold" });
            float y = 0.89f;
            int idx = 0;
            foreach (var s in (JArray)cat["data"]["server"])
            {
                var key = (string)s["key"];
                var active = s["active"]?.Value<bool>() == true;
                var access = s["accessible"]?.Value<bool>() == true;
                var sub2 = access
                    ? $"{s["itemsCount"]} предм. · авто {(s["autoSpawn"]?.Value<bool>() == true ? "вкл" : "выкл")}"
                    : $"недоступен: {s["reason"]}";
                ui.Call("ListCard", c, content, UiName + "_srv" + idx, new JObject
                {
                    ["rect"] = $"0.01 {y - 0.075f:0.###} 0.32 {y:0.###}",
                    ["title"] = s["name"], ["sub"] = sub2,
                    ["selected"] = sel == "srv:" + key, ["enabled"] = access,
                    ["strip"] = active ? "gold" : "none",
                    ["cmd"] = $"octaloadoutsui view srv:{key}", ["accent"] = "gold"
                });
                y -= 0.085f; idx++;
            }

            y -= 0.03f;
            ui.Call("Section", c, content, UiName + "_S2", new JObject
            { ["rect"] = $"0.01 {y - 0.05f:0.###} 0.32 {y:0.###}", ["text"] = "МОИ ПРЕСЕТЫ", ["accent"] = "gold" });
            y -= 0.06f;
            var state = API_GetPlayerState(p) as JObject;
            var limit = state?["data"]?["ownLimit"]?.Value<int>() ?? 0;
            var count = state?["data"]?["ownCount"]?.Value<int>() ?? 0;
            idx = 0;
            foreach (var o in (JArray)cat["data"]["own"])
            {
                var oid = (string)o["id"];
                var acc = o["accessible"]?.Value<bool>() == true;
                var sub3 = acc ? $"{o["itemsCount"]} предм." : $"недоступен: {o["reason"]}";
                ui.Call("ListCard", c, content, UiName + "_own" + idx, new JObject
                {
                    ["rect"] = $"0.01 {y - 0.075f:0.###} 0.32 {y:0.###}",
                    ["title"] = o["name"], ["sub"] = sub3,
                    ["selected"] = sel == "own:" + oid, ["enabled"] = acc,
                    ["strip"] = o["active"]?.Value<bool>() == true ? "gold" : "none",
                    ["cmd"] = $"octaloadoutsui view own:{oid}", ["accent"] = "gold"
                });
                y -= 0.085f; idx++;
            }
            if (y > 0.10f)
            {
                ui.Call("Button", c, content, UiName + "_SAVENEW", new JObject
                {
                    ["rect"] = $"0.01 {y - 0.075f:0.###} 0.32 {y:0.###}",
                    ["text"] = $"＋ СОХРАНИТЬ ИНВЕНТАРЬ ({count}/{limit})",
                    ["cmd"] = count < limit ? "octaloadoutsui presave" : "",
                    ["color"] = count < limit ? "0.28 0.22 0.10 0.95" : "0.15 0.15 0.15 0.9",
                    ["accent"] = "gold"
                });
            }

            // Правая часть — детали выбранного пресета
            UiDrawDetail(ui, c, content, p, sel);
            ui.Call("Show", p, c);
        }

        private void UiDrawDetail(Plugin ui, CuiElementContainer c, string content, BasePlayer p, string sel)
        {
            const string D = "0.345 0.02 0.99 0.97";
            if (string.IsNullOrEmpty(sel))
            {
                ui.Call("Text", c, content, UiName + "_hint", new JObject
                { ["rect"] = D, ["text"] = "Выбери пресет слева.\nАктивный пресет применится при возрождении.", ["size"] = 12, ["align"] = "middlecenter", ["color"] = "0.6 0.6 0.6 1" });
                return;
            }
            var pres = API_GetPreset(p, sel) as JObject;
            if (pres == null || pres["ok"]?.Value<bool>() != true)
            {
                ui.Call("Text", c, content, UiName + "_err", new JObject
                { ["rect"] = D, ["text"] = "Пресет недоступен: " + pres?["msg"], ["size"] = 11, ["align"] = "middlecenter", ["color"] = "0.9 0.4 0.4 1" });
                return;
            }
            var d = pres["data"];
            ui.Call("Text", c, content, UiName + "_dt", new JObject
            { ["rect"] = "0.35 0.90 0.98 0.97", ["text"] = (string)(d["name"] ?? d["key"]), ["size"] = 15, ["align"] = "middleleft", ["color"] = "0.95 0.75 0.30 1" });

            var items = d["items"] as JArray ?? new JArray();
            var i = 0;
            foreach (var it in items.Take(30))
            {
                var rect = ui.Call("GridRect", new JObject
                { ["rect"] = "0.35 0.30 0.98 0.86", ["cols"] = 6, ["gap"] = 0.012f, ["index"] = i }) as string;
                ui.Call("Panel", c, content, null, new JObject { ["rect"] = rect, ["color"] = "0.08 0.08 0.07 0.9" });
                ui.Call("ItemIcon", c, content, null, new JObject
                {
                    ["rect"] = rect, ["item"] = (string)it["shortname"],
                    ["skin"] = it["skin"]?.Value<ulong>() ?? 0UL, ["amount"] = it["amount"]?.Value<int>() ?? 0
                });
                i++;
            }

            // Кнопки действий
            var y = 0.20f;
            ui.Call("Button", c, content, UiName + "_SEL", new JObject
            { ["rect"] = $"0.35 {y:0.###} 0.55 {y + 0.06f:0.###}", ["text"] = "ВЫБРАТЬ ДЛЯ РЕСПАВНА", ["cmd"] = $"octaloadoutsui select {sel}", ["accent"] = "gold" });
            if (sel.StartsWith("own:"))
            {
                var oid = sel.Substring(4);
                ui.Call("Button", c, content, UiName + "_REN", new JObject
                { ["rect"] = $"0.56 {y:0.###} 0.76 {y + 0.06f:0.###}", ["text"] = "ПЕРЕИМЕНОВАТЬ", ["cmd"] = $"octaloadoutsui rename {oid}", ["color"] = "0.20 0.20 0.18 0.95" });
                ui.Call("Button", c, content, UiName + "_DEL", new JObject
                { ["rect"] = $"0.77 {y:0.###} 0.98 {y + 0.06f:0.###}", ["text"] = "УДАЛИТЬ", ["cmd"] = $"octaloadoutsui askdel {oid}", ["color"] = "0.45 0.12 0.10 0.95" });
            }
            ui.Call("Button", c, content, UiName + "_NONE", new JObject
            { ["rect"] = $"0.35 {y - 0.075f:0.###} 0.55 {y - 0.015f:0.###}", ["text"] = "СБРОСИТЬ ВЫБОР", ["cmd"] = "octaloadoutsui select none", ["color"] = "0.15 0.15 0.15 0.9" });
        }

        // Превью сохранения: kept-иконки + причины отфильтрованных, подтверждение
        private void UiDrawSavePreview(Plugin ui, BasePlayer p)
        {
            var prev = API_PreviewSave(p) as JObject;
            if (prev == null || prev["ok"]?.Value<bool>() != true)
            { ui.Call("Toast", p, UiName, "Превью: " + prev?["msg"], "red"); return; }

            var c = new CuiElementContainer();
            var kept = (JArray)prev["data"]["kept"];
            var skipped = (JArray)prev["data"]["skipped"];
            var root = ui.Call("Root", c, new JObject
            {
                ["name"] = UiName, ["title"] = "СОХРАНЕНИЕ ПРЕСЕТА",
                ["subtitle"] = $"Войдёт {kept.Count} · отфильтровано {skipped.Count}",
                ["icon"] = "sprite:assets/icons/gear.png",
                ["close"] = "octaloadoutsui close", ["accent"] = "gold",
                ["rect"] = "0.15 0.10 0.85 0.90"
            }) as JObject;
            if (root == null) return;
            var content = (string)root["content"];

            var i = 0;
            foreach (var it in kept.Take(42))
            {
                var rect = ui.Call("GridRect", new JObject
                { ["rect"] = "0.02 0.30 0.66 0.94", ["cols"] = 7, ["gap"] = 0.010f, ["index"] = i }) as string;
                ui.Call("Panel", c, content, null, new JObject { ["rect"] = rect, ["color"] = "0.08 0.08 0.07 0.9" });
                ui.Call("ItemIcon", c, content, null, new JObject
                { ["rect"] = rect, ["item"] = (string)it["shortname"], ["skin"] = it["skin"]?.Value<ulong>() ?? 0UL, ["amount"] = it["amount"]?.Value<int>() ?? 0 });
                i++;
            }
            var sb = new System.Text.StringBuilder();
            foreach (var sk in skipped.Take(16))
                sb.AppendLine($"✗ {sk["shortname"]} — {sk["reason"]}");
            if (skipped.Count > 16) sb.AppendLine($"… ещё {skipped.Count - 16}");
            ui.Call("Text", c, content, null, new JObject
            { ["rect"] = "0.68 0.30 0.98 0.94", ["text"] = sb.Length > 0 ? sb.ToString() : "Всё войдёт в пресет.", ["size"] = 10, ["align"] = "upperleft", ["color"] = "0.9 0.55 0.45 1" });

            ui.Call("Input", c, content, UiName + "_nm", new JObject
            { ["rect"] = "0.02 0.10 0.50 0.20", ["text"] = "имя пресета", ["limit"] = MaxNameLen, ["cmd"] = "octaloadoutsui savename" });
            ui.Call("Button", c, content, UiName + "_ok", new JObject
            { ["rect"] = "0.52 0.10 0.72 0.20", ["text"] = "СОХРАНИТЬ", ["cmd"] = "octaloadoutsui savedo", ["accent"] = "gold" });
            ui.Call("Button", c, content, UiName + "_no", new JObject
            { ["rect"] = "0.73 0.10 0.98 0.20", ["text"] = "ОТМЕНА", ["cmd"] = "octaloadoutsui cancel", ["color"] = "0.30 0.10 0.08 0.95" });
            ui.Call("Show", p, c);
        }

        private readonly Dictionary<ulong, string> _pendingName = new Dictionary<ulong, string>();

        [ConsoleCommand("octaloadoutsui")]
        private void CCmdUi(ConsoleSystem.Arg arg)
        {
            var p = arg.Connection?.player as BasePlayer;
            if (p == null) return;
            var a0 = arg.GetString(0);
            var ui = UiLib();
            switch (a0)
            {
                case "close":
                    CuiHelper.DestroyUi(p, UiName);
                    return;
                case "view":
                    UiDraw(p, arg.GetString(1));
                    return;
                case "select":
                {
                    var r = SelectPreset(p, arg.GetString(1));
                    ui?.Call("Toast", p, UiName, r.msg, r.code == "OK" ? "accent" : "red");
                    if (r.code == "OK") UiDraw(p, arg.GetString(1));
                    return;
                }
                case "presave":
                    if (ui != null) UiDrawSavePreview(ui, p);
                    return;
                case "savename":
                    // FullString = "savename <введённый текст>" — берём всё после первого слова
                    var fs = arg.FullString.ToString() ?? "";
                    var sp = fs.IndexOf(' ');
                    _pendingName[(ulong)p.userID] = sp >= 0 ? fs.Substring(sp + 1).Trim() : "";
                    return;
                case "savedo":
                {
                    string nm;
                    _pendingName.TryGetValue((ulong)p.userID, out nm);
                    var r = SavePersonal(p, nm);
                    ui?.Call("Toast", p, UiName, r.msg, r.code == "OK" ? "accent" : "red");
                    _pendingName.Remove((ulong)p.userID);
                    if (r.code == "OK") UiDraw(p, "own:" + (string)r.data["id"]);
                    return;
                }
                case "cancel":
                    _pendingName.Remove((ulong)p.userID);
                    UiDraw(p, null);
                    return;
                case "askdel":
                {
                    var c = new CuiElementContainer();
                    ui?.Call("Confirm", c, UiName, null, new JObject
                    {
                        ["title"] = "Удалить пресет?", ["text"] = "Действие необратимо.",
                        ["yes"] = $"octaloadoutsui del {arg.GetString(1)}",
                        ["no"] = "octaloadoutsui cancel", ["accent"] = "gold"
                    });
                    ui?.Call("Show", p, c);
                    return;
                }
                case "del":
                {
                    var r = DeletePreset(p, arg.GetString(1));
                    ui?.Call("Toast", p, UiName, r.msg, r.code == "OK" ? "accent" : "red");
                    UiDraw(p, null);
                    return;
                }
                case "rename":
                {
                    // переименование через чат: подсказываем команду (ввод через инпут — следующая итерация)
                    ui?.Call("Toast", p, UiName, $"Чатом: /loadout rename {arg.GetString(1)} <имя>", "accent");
                    return;
                }
            }
        }

        #endregion
    }
}
