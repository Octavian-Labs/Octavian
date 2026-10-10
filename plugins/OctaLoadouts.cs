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

        // Игровые команды открыты всем — доступ к контенту решают пресеты
        // (permission/benefit на уровне пресета). Админские пермишены гранулярны.
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

        // Гард «одна выдача на один цикл респавна»: OnPlayerRespawn снимает метку
        // (новый цикл смерть→возрождение), OnPlayerRespawned ставит её. Дубли хука
        // внутри одного цикла — no-op; новый респавн всегда получает выдачу.
        private readonly HashSet<ulong> _appliedThisSpawn = new HashSet<ulong>();

        // анти-даблклик по операции (uid|op) — защита команд без requestId
        private readonly Dictionary<string, float> _lastOp = new Dictionary<string, float>();

        // Идемпотентность мутирующих вызовов API (requestId → ответ, 300с)
        private readonly Dictionary<string, JObject> _idem = new Dictionary<string, JObject>();
        private readonly Dictionary<string, float> _idemAt = new Dictionary<string, float>();

        private static readonly string[] ContainerKeys = { "main", "belt", "wear" };

        #endregion

        #region Конфигурация

        private class Cfg
        {
            // "add" — докидывать в свободные слоты (дефолт, безопасно)
            // "replace" — Strip() всего инвентаря перед выдачей (явный opt-in админа;
            //             предметы предсобираются и валидируются ДО очистки, но отката
            //             уже размещённых нет — это не транзакция Rust-инвентаря)
            // "disabled" — авто-выдача выключена, пресеты только по командам
            [JsonProperty("Режим возрождения (add|replace|disabled)")]
            public string RespawnMode = "add";

            // что делать с предметом, который не влез в контейнеры:
            // "skip_with_message" (дефолт) — не выдавать, сообщить игроку;
            // "drop_near_player" — выбросить под ноги; "cancel_loadout" — прервать выдачу
            [JsonProperty("Политика переполнения (skip_with_message|drop_near_player|cancel_loadout)")]
            public string OverflowPolicy = "skip_with_message";

            // глобальный верхний выключатель заряженных магазинов:
            // магазин сохраняется только если StoreLoadedAmmo И lim.StoreAmmo одновременно
            [JsonProperty("Глобально разрешить заряженные магазины (master-switch, дефолт выкл)")]
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
            [JsonProperty("Показывать в каталоге недоступным игрокам")]
            public bool VisibleWhenUnavailable = true;
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

            // семантика — ШТУКИ предмета (item.amount), не стаки/слоты
            [JsonProperty("Максимум штук по shortname (-1 = без лимита, 0 = запрещён)")]
            public Dictionary<string, int> MaxAmountPerShortname = new Dictionary<string, int>();

            // семантика — суммарное количество по ItemCategory (Weapon, Ammo и т.д.)
            [JsonProperty("Максимум штук по категории (-1 = без лимита, 0 = запрещена)")]
            public Dictionary<string, int> MaxAmountPerCategory = new Dictionary<string, int>();

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
            _rev++; // конфиг — часть состояния, которое видит envelope
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
                    MaxAmountPerShortname = new Dictionary<string, int>
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
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermAdminPresets, this);
            permission.RegisterPermission(PermAdminLimits, this);
            permission.RegisterPermission("octaloadouts.event", this);
            if (_cfg.MigrateLegacy) TryMigrateLegacy();
        }

        private void Unload()
        {
            foreach (var pl in BasePlayer.activePlayerList)
                if (pl != null) UiDestroyAll(pl);
            _appliedThisSpawn.Clear(); _lastOp.Clear();
            _idem.Clear(); _idemAt.Clear();
            _pendingName.Clear(); _pendingRename.Clear(); _pendingChatSave.Clear();
            SaveAllDirty();
        }

        private void OnServerSave() => SaveAllDirty();

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null) return;
            UiDestroyAll(player);
            var duid = (ulong)player.userID;
            _appliedThisSpawn.Remove(duid);
            _pendingName.Remove(duid);
            _pendingRename.Remove(duid);
            _pendingChatSave.Remove(duid);
        }

        #endregion

        #region Хранилище

        private string DataPath(ulong uid) => $"OctaLoadouts/players/{uid}";

        // Ленивая загрузка: файл читается по первому обращению к uid —
        // на крупном сервере не держим все JSON в памяти сразу
        private PlayerLo Pd(ulong uid)
        {
            PlayerLo pd;
            if (_players.TryGetValue(uid, out pd)) return pd;
            pd = null;
            if (Interface.Oxide.DataFileSystem.ExistsDatafile(DataPath(uid)))
            {
                try { pd = Interface.Oxide.DataFileSystem.ReadObject<PlayerLo>(DataPath(uid)); }
                catch (Exception ex) { PrintError($"Данные {uid} не прочитались: {ex.Message}"); }
            }
            return _players[uid] = pd ?? new PlayerLo();
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
        // perShort/perCont/perCat считают ШТУКИ (item.amount), а не слоты.
        private string CheckSaveAllowed(Item item, string contKey, LimitsCfg lim,
                                        Dictionary<string, int> perShort,
                                        Dictionary<string, int> perCont,
                                        Dictionary<string, int> perCat, int keptTotal)
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

            int lim2, used;
            if (lim.MaxAmountPerShortname != null &&
                lim.MaxAmountPerShortname.TryGetValue(baseDef.shortname, out lim2))
            {
                if (lim2 == 0) return "banned_item";
                perShort.TryGetValue(baseDef.shortname, out used);
                if (lim2 > 0 && used + item.amount > lim2) return "shortname_limit";
            }
            var cat = baseDef.category.ToString();
            if (lim.MaxAmountPerCategory != null &&
                lim.MaxAmountPerCategory.TryGetValue(cat, out lim2))
            {
                if (lim2 == 0) return "category_banned";
                perCat.TryGetValue(cat, out used);
                if (lim2 > 0 && used + item.amount > lim2) return "category_limit";
            }

            if (lim.MaxItemsTotal >= 0 && keptTotal >= lim.MaxItemsTotal) return "total_limit";

            var contMax = ContainerMax(lim, contKey);
            if (contMax >= 0)
            {
                perCont.TryGetValue(contKey, out used);
                if (used + item.amount > contMax) return "container_limit";
            }
            return null;
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
            var perCat  = new Dictionary<string, int>();
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

                    var reason = CheckSaveAllowed(item, kv.Key, lim, perShort, perCont, perCat, rep.Kept.Count);
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

                    // боеприпасы — только если и глобальный master-switch, И уровень разрешили
                    var proj = item.GetHeldEntity() as BaseProjectile;
                    if (proj != null && proj.primaryMagazine != null)
                    {
                        var storeAmmo = _cfg.StoreLoadedAmmo && lim.StoreAmmo;
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
                    perShort[shortname] = (perShort.ContainsKey(shortname) ? perShort[shortname] : 0) + item.amount;
                    perCont[kv.Key] = (perCont.ContainsKey(kv.Key) ? perCont[kv.Key] : 0) + item.amount;
                    var catN = (baseDef ?? def).category.ToString();
                    perCat[catN] = (perCat.ContainsKey(catN) ? perCat[catN] : 0) + item.amount;
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

        // Начало нового цикла «смерть→возрождение»: снимаем метку — следующий
        // OnPlayerRespawned гарантированно отработает, даже если респавны идут подряд
        private void OnPlayerRespawn(BasePlayer player)
        {
            if (player == null || player.IsNpc) return;
            _appliedThisSpawn.Remove((ulong)player.userID);
        }

        // Одна выдача на один цикл: дубль OnPlayerRespawned в том же цикле — no-op,
        // новый цикл разблокирован предыдущим хуком — кулдаунов по времени нет
        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null || player.IsNpc) return;
            if (string.Equals(_cfg.RespawnMode, "disabled", StringComparison.OrdinalIgnoreCase)) return;
            var uid = (ulong)player.userID;
            if (!_appliedThisSpawn.Add(uid)) return;
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

        // Выдача в два прохода:
        // 1) предсборка — все предметы создаются и валидируются ДО любых изменений;
        //    если собрать нечего — инвентарь игрока не трогаем вообще (в т.ч. replace)
        // 2) раскладка по контейнерам; не влезшее — по OverflowPolicy
        // replace: Strip() после успешной предсборки; отката уже размещённого нет —
        // это НЕ транзакция Rust-инвентаря, режим opt-in и выключен по умолчанию
        private void GiveLoadout(BasePlayer player, List<LoItem> items, string srcDesc)
        {
            // проход 1: предсборка
            var built = new List<KeyValuePair<LoItem, Item>>();
            var invalid = 0;
            foreach (var li in items)
            {
                var def = ValidateItem(li);
                var item = def == null ? null
                         : ItemManager.CreateByItemID(def.itemid, Math.Max(1, li.Amount), li.Skin);
                if (item == null) { invalid++; continue; }
                SetupWeapon(li, item);
                built.Add(new KeyValuePair<LoItem, Item>(li, item));
            }
            if (built.Count == 0) return;

            var replace = string.Equals(_cfg.RespawnMode, "replace", StringComparison.OrdinalIgnoreCase);
            var policy  = _cfg.OverflowPolicy ?? "skip_with_message";

            if (replace) player.inventory.Strip();

            // проход 2: раскладка
            var given = 0;
            var lost = new List<string>(); // имена не размещённых — отчёт игроку
            var cancelled = false;
            for (var i = 0; i < built.Count; i++)
            {
                var li = built[i].Key;
                var item = built[i].Value;
                if (cancelled) { item?.Remove(); continue; }
                if (item == null || !item.IsValid()) { lost.Add(li.Short); continue; }

                // wear принимает только носимое — иначе цель переносится в main
                var cont = ContainerOf(player, li.Cont);
                if (li.Cont == "wear" && item.info != null && !item.info.isWearable)
                    cont = player.inventory.containerMain;

                var ok = false;
                if (cont != null)
                {
                    // позиция только если валидна и свободна; иначе — любой слот контейнера
                    if (li.Pos >= 0 && li.Pos < cont.capacity && cont.GetSlot(li.Pos) == null)
                        ok = item.MoveToContainer(cont, li.Pos, false);
                    if (!ok) ok = item.MoveToContainer(cont, -1, false);
                }
                if (!ok)
                    ok = item.MoveToContainer(player.inventory.containerMain, -1, false) ||
                         item.MoveToContainer(player.inventory.containerBelt, -1, false);

                if (ok) { given++; continue; }

                switch (policy)
                {
                    case "drop_near_player":
                        item.Drop(player.GetDropPosition(), Vector3.up * 0.5f);
                        given++;
                        break;
                    case "cancel_loadout":
                        item.Remove();
                        lost.Add(li.Short);
                        cancelled = true; // остальные не размещаем и не теряем молча
                        for (var j = i + 1; j < built.Count; j++)
                        { built[j].Value?.Remove(); lost.Add(built[j].Key.Short); }
                        i = built.Count;
                        break;
                    default: // skip_with_message
                        item.Remove();
                        lost.Add(li.Short);
                        break;
                }
            }

            // событие для Hub/наблюдателей: итог применения после респавна
            Interface.Oxide.CallHook("OnOctaLoadoutApplied", player, srcDesc, given, lost.Count);

            var totalSkipped = invalid + lost.Count;
            if (given == 0 && totalSkipped == 0) return;
            var names = lost.Count > 0
                ? " — не влезло: " + string.Join(", ", lost.Take(6)) + (lost.Count > 6 ? "…" : "")
                : "";
            Msg(player, cancelled
                ? $"Выдача прервана ({srcDesc}): инвентарь полон{names}"
                : totalSkipped > 0
                    ? $"Применён loadout ({srcDesc}): выдано {given}, пропущено {totalSkipped}{names}"
                    : $"Применён loadout ({srcDesc}): выдано {given}");
        }

        // магазин (с клампом по вместимости — PITFALLS §3 builtInSize) и модули оружия
        private void SetupWeapon(LoItem li, Item item)
        {
            var proj = item.GetHeldEntity() as BaseProjectile;
            if (proj == null || proj.primaryMagazine == null) return;
            if (!string.IsNullOrEmpty(li.AmmoType))
            {
                var aDef = ItemManager.FindItemDefinition(li.AmmoType);
                if (aDef != null) proj.primaryMagazine.ammoType = aDef;
            }
            var magDef = proj.primaryMagazine.definition;
            var cap = magDef.builtInSize > 0 ? magDef.builtInSize
                    : proj.primaryMagazine.capacity;
            var ammo = Math.Max(0, li.AmmoCount);
            proj.primaryMagazine.contents = cap > 0 ? Math.Min(ammo, cap) : ammo;

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

        #endregion

        #region Внутренние операции (общий путь для команд и API)

        // Анти-даблклик: один и тот же op у того же игрока чаще, чем раз в window сек — no-op.
        // Защищает пути без requestId (chat-confirm, кнопки UI).
        private bool OpRepeat(ulong uid, string op, float window = 1.5f)
        {
            var k = uid + "|" + op;
            float last;
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (_lastOp.TryGetValue(k, out last) && now - last < window) return true;
            _lastOp[k] = now;
            return false;
        }

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
            if (pd.Presets.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
                return ("INVALID_ARGUMENT", "Имя уже занято другим твоим пресетом", null);

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

        // Политика личных пресетов (явно зафиксирована):
        //   просмотр недоступного — разрешён;
        //   удаление своего недоступного — разрешено (уборка собственных данных);
        //   переименование и замена содержимого — только при действующем уровне;
        //   чужой id никогда не открывает чужие данные.
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

        private (string code, string msg, JObject data) RenamePreset(BasePlayer p, string id, string name)
        {
            var uid = (ulong)p.userID;
            var pd = Pd(uid);
            var own = pd.Presets?.FirstOrDefault(o => o.Id == id);
            if (own == null) return ("NOT_FOUND", "Личный пресет не найден", null);
            string reason;
            if (!OwnAccessible(p, own, out reason)) return ("NO_PERMISSION", reason ?? "no_access", null);
            var nm = SanitizeName(name);
            if (string.IsNullOrEmpty(nm)) return ("INVALID_ARGUMENT", "Плохое имя", null);
            if (pd.Presets.Any(o => o.Id != id && string.Equals(o.Name, nm, StringComparison.OrdinalIgnoreCase)))
                return ("INVALID_ARGUMENT", "Имя уже занято", null);
            own.Name = nm;
            MarkDirty(uid);
            return ("OK", $"Переименовано в «{nm}»", null);
        }

        // Перезапись содержимого своего пресета текущим инвентарём (уровень пресета — прежний)
        private (string code, string msg, JObject data) ReplacePresetItems(BasePlayer p, string id)
        {
            var uid = (ulong)p.userID;
            var pd = Pd(uid);
            var own = pd.Presets?.FirstOrDefault(o => o.Id == id);
            if (own == null) return ("NOT_FOUND", "Личный пресет не найден", null);
            string reason;
            if (!OwnAccessible(p, own, out reason)) return ("NO_PERMISSION", reason ?? "no_access", null);
            var lvl = FindPreset(own.LevelKey);
            var rep = CaptureInventory(p, lvl?.Limits ?? new LimitsCfg());
            if (rep.Kept.Count == 0) return ("INVALID_ARGUMENT", "В инвентаре нет предметов, разрешённых к сохранению", null);
            own.Items = rep.Kept;
            own.SavedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            MarkDirty(uid);
            return ("OK", $"Содержимое «{own.Name}» обновлено: {rep.Kept.Count} предм., отфильтровано {rep.Skipped.Count}",
                new JObject { ["kept"] = rep.Kept.Count, ["skipped"] = new JArray(rep.Skipped) });
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

        // Ключ идемпотентности: uid|op|requestId — одинаковый requestId у двух
        // игроков или в двух операциях никогда не пересекается
        private bool IdemHit(ulong uid, string op, string requestId, out JObject cached)
        {
            cached = null;
            if (string.IsNullOrEmpty(requestId)) return false;
            var k = $"{uid}|{op}|{requestId}";
            float at;
            if (_idem.TryGetValue(k, out cached) && _idemAt.TryGetValue(k, out at) &&
                UnityEngine.Time.realtimeSinceStartup - at < 300f)
                return true;
            cached = null;
            return false;
        }

        private void IdemStore(ulong uid, string op, string requestId, JObject res)
        {
            if (string.IsNullOrEmpty(requestId) || res == null) return;
            // подчистка: истёкшие ключи и потолок размера, чтобы словари не росли вечно
            if (_idem.Count > 300)
            {
                var now = UnityEngine.Time.realtimeSinceStartup;
                foreach (var kv in _idemAt.Where(kv => now - kv.Value > 300f).Select(kv => kv.Key).ToList())
                { _idem.Remove(kv); _idemAt.Remove(kv); }
                foreach (var kv in _lastOp.Where(kv => now - kv.Value > 60f).Select(kv => kv.Key).ToList())
                    _lastOp.Remove(kv);
            }
            var k = $"{uid}|{op}|{requestId}";
            _idem[k] = res;
            _idemAt[k] = UnityEngine.Time.realtimeSinceStartup;
        }

        [HookMethod("Octa_GetManifest")]
        private object API_Manifest() => new JObject
        {
            ["module"] = "OctaLoadouts",
            ["version"] = "1.0.0",
            ["api"] = 1,
            ["methods"] = new JArray("Loadouts_GetCatalog", "Loadouts_GetPlayerState", "Loadouts_GetPreset",
                                     "Loadouts_PreviewSave", "Loadouts_Save", "Loadouts_Select",
                                     "Loadouts_Delete", "Loadouts_Rename", "Loadouts_Replace")
        };

        // Каталог: серверные пресеты (с доступностью) + личные сохранения игрока
        [HookMethod("Loadouts_GetCatalog")]
        private object API_GetCatalog(BasePlayer player)
        {
            if (player == null || player.IsNpc) return ResErr("INVALID_ARGUMENT", "player required");
            var uid = (ulong)player.userID;
            var pd = Pd(uid);
            var srv = new JArray();
            var isAdmin = IsAdmin(player);
            foreach (var pr in _cfg.Presets ?? new List<PresetCfg>())
            {
                string reason;
                var access = CanAccess(player, pr, out reason);
                // приватный пресет недоступному игроку не раскрываем вообще
                if (!access && !isAdmin && !pr.VisibleWhenUnavailable) continue;
                var o = new JObject
                {
                    ["key"] = pr.Key, ["name"] = pr.DisplayName,
                    ["accessible"] = access, ["reason"] = access ? null : (reason ?? "no_access"),
                    ["active"] = pd.Active == "srv:" + pr.Key
                };
                // детали (счётчик, флаги) — только доступному или админу
                if (access || isAdmin)
                {
                    o["priority"] = pr.Priority;
                    o["enabled"] = pr.Enabled; o["selectable"] = pr.Selectable;
                    o["autoSpawn"] = pr.AutoSpawn;
                    o["itemsCount"] = pr.Items?.Count ?? 0;
                    o["allowSaves"] = pr.AllowPersonalSaves; o["maxSaves"] = pr.MaxPersonalSaves;
                }
                srv.Add(o);
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
                var resp = new JObject
                {
                    ["key"] = pr.Key, ["name"] = pr.DisplayName,
                    ["items"] = new JArray((pr.Items ?? new List<LoItem>()).Select(i => i.ToJson()))
                };
                // внутренние правила — только админу; клиентскому потребителю они не нужны
                if (IsAdmin(player))
                    resp["limits"] = JObject.FromObject(pr.Limits ?? new LimitsCfg());
                return ResOk(resp);
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
            var uid = (ulong)player.userID;
            JObject cached;
            if (IdemHit(uid, "save", requestId, out cached)) return cached;
            var r = SavePersonal(player, name);
            var res = r.code == "OK" ? Res(true, r.code, r.msg, r.data) : ResErr(r.code, r.msg);
            IdemStore(uid, "save", requestId, res);
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
            var uid = (ulong)player.userID;
            JObject cached;
            if (IdemHit(uid, "del", requestId, out cached)) return cached;
            var r = DeletePreset(player, presetId);
            var res = r.code == "OK" ? ResOk(null, r.msg) : ResErr(r.code, r.msg);
            IdemStore(uid, "del", requestId, res);
            return res;
        }

        [HookMethod("Loadouts_Rename")]
        private object API_Rename(BasePlayer player, string presetId, string name, string requestId)
        {
            if (player == null) return ResErr("INVALID_ARGUMENT", "player required");
            var uid = (ulong)player.userID;
            JObject cached;
            if (IdemHit(uid, "ren", requestId, out cached)) return cached;
            var r = RenamePreset(player, presetId, name);
            var res = r.code == "OK" ? ResOk(null, r.msg) : ResErr(r.code, r.msg);
            IdemStore(uid, "ren", requestId, res);
            return res;
        }

        // Перезапись содержимого своего пресета текущим инвентарём
        [HookMethod("Loadouts_Replace")]
        private object API_Replace(BasePlayer player, string presetId, string requestId)
        {
            if (player == null) return ResErr("INVALID_ARGUMENT", "player required");
            var uid = (ulong)player.userID;
            JObject cached;
            if (IdemHit(uid, "repl", requestId, out cached)) return cached;
            var r = ReplacePresetItems(player, presetId);
            var res = r.code == "OK" ? Res(true, r.code, r.msg, r.data) : ResErr(r.code, r.msg);
            IdemStore(uid, "repl", requestId, res);
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
                        "  /loadout save <имя> — превью, затем /loadout confirm\n" +
                        "  /loadout select srv:<ключ> | own:<id> | none — выбор для респавна\n" +
                        "  /loadout delete <id> confirm — удалить свой пресет\n" +
                        "  /loadout rename <id> <новое имя> — переименовать");
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
                    // имя — ВСЕ аргументы после sub: «/loadout save PvP Set» → «PvP Set»
                    if (args.Length < 2) { Chat(p, "Укажи имя: /loadout save <имя>"); return; }
                    var name = SanitizeName(string.Join(" ", args.Skip(1)));
                    if (string.IsNullOrEmpty(name)) { Chat(p, "Укажи имя: /loadout save <имя>"); return; }
                    var prev = API_PreviewSave(p) as JObject;
                    if (prev == null || prev["ok"]?.Value<bool>() != true)
                    { Chat(p, $"Превью недоступно: {prev?["msg"]}"); return; }
                    // превью закрепляется за именем; выполняет его отдельный /loadout confirm —
                    // так пробелы в имени не ломают подтверждение
                    _pendingChatSave[(ulong)p.userID] = name;
                    var kept = (JArray)prev["data"]["kept"];
                    var skipped = (JArray)prev["data"]["skipped"];
                    var sb2 = new System.Text.StringBuilder();
                    sb2.AppendLine($"◆ Превью «{name}» — войдёт {kept.Count} предм.:");
                    foreach (var it in kept.Take(12))
                        sb2.AppendLine($"  <color=#9BC76B>✓</color> {it["shortname"]} x{it["amount"]}");
                    if (kept.Count > 12) sb2.AppendLine($"  … и ещё {kept.Count - 12}");
                    foreach (var it in skipped)
                        sb2.AppendLine($"  <color=#E06666>✗</color> {it["shortname"]} — {it["reason"]}");
                    sb2.AppendLine("Подтверди: /loadout confirm");
                    Chat(p, sb2.ToString());
                    return;
                }
                case "confirm":
                {
                    var uidC = (ulong)p.userID;
                    string pend;
                    if (!_pendingChatSave.TryGetValue(uidC, out pend))
                    { Chat(p, "Нет ожидающего сохранения — сначала /loadout save <имя>"); return; }
                    _pendingChatSave.Remove(uidC);
                    if (OpRepeat(uidC, "save")) { Chat(p, "Повтори чуть позже"); return; }
                    // инвентарь перечитывается заново — сохраняется актуальное состояние,
                    // а не устаревшее превью
                    var r = SavePersonal(p, pend);
                    Chat(p, r.code == "OK"
                        ? $"Сохранено «{r.data["name"]}»: предметов {r.data["kept"]}, отфильтровано {((JArray)r.data["skipped"]).Count}"
                        : $"Не сохранено: {r.msg}");
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
                    // единая политика подтверждения: как в UI, сначала предупреждение
                    if (args.Length < 3 || !string.Equals(args[2], "confirm", StringComparison.OrdinalIgnoreCase))
                    { Chat(p, $"Безвозвратно. Подтверди: /loadout delete {args[1]} confirm"); return; }
                    if (OpRepeat((ulong)p.userID, "del:" + args[1])) { Chat(p, "Повтори чуть позже"); return; }
                    var r = DeletePreset(p, args[1]);
                    Chat(p, r.code == "OK" ? $"✓ {r.msg}" : $"✗ {r.msg}");
                    return;
                }
                case "rename":
                {
                    if (args.Length < 3) { Chat(p, "Укажи: /loadout rename <id> <новое имя>"); return; }
                    var r = RenamePreset(p, args[1], string.Join(" ", args.Skip(2)));
                    Chat(p, r.code == "OK" ? $"✓ {r.msg}" : $"✗ {r.msg}");
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

        // Правка лимитов пресета из консоли: octaloadouts.setlimit <key> <field> <value>
        [ConsoleCommand("octaloadouts.setlimit")]
        private void CCmdSetLimit(ConsoleSystem.Arg arg)
        {
            var p = arg.Connection?.player as BasePlayer;
            if (p != null && !p.IsAdmin && !permission.UserHasPermission(p.UserIDString, PermAdminLimits)) return;
            var pr = FindPreset(arg.GetString(0));
            if (pr == null) { arg.ReplyWith("octaloadouts.setlimit <preset> <field> <value>"); return; }
            var field = arg.GetString(1).ToLowerInvariant();
            var val = arg.GetInt(2, int.MinValue);
            if (val == int.MinValue) { arg.ReplyWith("value должен быть числом"); return; }
            pr.Limits = pr.Limits ?? new LimitsCfg();
            switch (field)
            {
                case "maxitemstotal": pr.Limits.MaxItemsTotal = val; break;
                case "maxmain":       pr.Limits.MaxMain = val; break;
                case "maxbelt":       pr.Limits.MaxBelt = val; break;
                case "maxwear":       pr.Limits.MaxWear = val; break;
                case "maxloadedammo": pr.Limits.MaxLoadedAmmo = val; break;
                case "maxattachments":pr.Limits.MaxAttachments = val; break;
                default: arg.ReplyWith("field: maxitemstotal|maxmain|maxbelt|maxwear|maxloadedammo|maxattachments"); return;
            }
            SaveConfig();
            Audit(p, $"setlimit preset={pr.Key} {field}={val}");
            arg.ReplyWith($"«{pr.Key}»: {field} = {val} (аудит записан)");
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

        // ── Миграция LoadoutController ──────────────────────────────────────
        // Формат файла игрока data/LoadoutController/PlayerLoadouts/<uid>.json:
        //   PlayerLoadoutOption {
        //     ActiveLoadout: <int индекс | string имя>,
        //     Loadouts[] { SavedPerm, SaveName, Items[] }
        //   }
        // Каждый сохранённый loadout → отдельный личный пресет; ActiveLoadout →
        // выбранный пресет. Файлы legacy НЕ изменяются. Идемпотентность:
        // pd.MigratedFrom (данные) + migration-state.json (в т.ч. пустые/битые
        // файлы получают статус и не переобрабатываются бесконечно).
        // Порядок на файл: запись данных игрока → строка отчёта → метка статуса.
        private const string MigStatePath = "OctaLoadouts/migration-state.json";

        private Dictionary<string, string> MigStateLoad()
        {
            try
            {
                if (!Interface.Oxide.DataFileSystem.ExistsDatafile(MigStatePath))
                    return new Dictionary<string, string>();
                return Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, string>>(MigStatePath)
                       ?? new Dictionary<string, string>();
            }
            catch { return new Dictionary<string, string>(); }
        }

        private void MigStateSave(Dictionary<string, string> st)
        {
            try { Interface.Oxide.DataFileSystem.WriteObject(MigStatePath, st); }
            catch (Exception ex) { PrintError($"migration-state: {ex.Message}"); }
        }

        private string TryMigrateLegacy(bool verbose = false)
        {
            var report = new List<string>();
            int okCnt = 0, skipCnt = 0, errCnt = 0;
            try
            {
                var legDir = Path.Combine(Interface.Oxide.DataDirectory, "LoadoutController", "PlayerLoadouts");
                var repDir = Path.Combine(Interface.Oxide.DataDirectory, "OctaLoadouts");
                Directory.CreateDirectory(repDir);
                var state = MigStateLoad();

                // серверные LoadoutOptions — отдельный слой; переносим честно: только в отчёт
                var legRoot = Path.Combine(Interface.Oxide.DataDirectory, "LoadoutController");
                if (Directory.Exists(legRoot))
                {
                    var srvFiles = Directory.GetFiles(legRoot, "*.json", SearchOption.TopDirectoryOnly);
                    if (srvFiles.Length > 0)
                        report.Add($"серверные пресеты LoadoutController ({srvFiles.Length} файл. в корне data/LoadoutController) — автоперенос НЕ выполняется, требуется ручной перенос в конфиг");
                }

                if (!Directory.Exists(legDir))
                    return verbose ? "LoadoutController/PlayerLoadouts не найден — миграция не требуется" : null;

                foreach (var file in Directory.GetFiles(legDir, "*.json"))
                {
                    var uidStr = Path.GetFileNameWithoutExtension(file);
                    ulong uid;
                    if (!ulong.TryParse(uidStr, out uid))
                    { skipCnt++; report.Add($"{uidStr}: не steamid — пропуск"); continue; }

                    // статусные метки: не переобрабатываем то, что уже разобрано
                    string st;
                    if (state.TryGetValue(uidStr, out st)) { skipCnt++; continue; }

                    var pd = Pd(uid);
                    if (pd.MigratedFrom == "LoadoutController") { state[uidStr] = "migrated"; skipCnt++; continue; }

                    JObject leg;
                    try { leg = JObject.Parse(File.ReadAllText(file)); }
                    catch (Exception ex)
                    {
                        errCnt++; report.Add($"{uid}: не JSON ({ex.Message})");
                        state[uidStr] = "error:invalid_json"; MigStateSave(state);
                        continue;
                    }

                    var loadouts = leg["Loadouts"] as JArray;
                    if (loadouts == null || loadouts.Count == 0)
                    {
                        skipCnt++; report.Add($"{uid}: пустой список Loadouts — статус 'empty'");
                        state[uidStr] = "empty"; MigStateSave(state);
                        continue;
                    }

                    // каждый сохранённый loadout → отдельный личный пресет
                    var migratedIds = new List<string>();
                    var emptySaves = 0;
                    for (var i = 0; i < loadouts.Count; i++)
                    {
                        var sv = loadouts[i] as JObject;
                        if (sv == null) continue;
                        var items = new List<LoItem>();
                        var itArr = sv["Items"] as JArray;
                        if (itArr != null)
                            foreach (var el in itArr.OfType<JObject>())
                            {
                                var it = LegacyItem(el);
                                if (it != null) items.Add(it);
                            }
                        if (items.Count == 0) { emptySaves++; continue; }

                        var sPerm = (string)(sv["SavedPerm"] ?? sv["savedPerm"] ?? sv["Permission"]);
                        var sName = (string)(sv["SaveName"] ?? sv["saveName"] ?? sv["Name"]) ?? $"Мигрированный {i + 1}";
                        var oid = $"leg{i}";
                        pd.Presets.Add(new OwnPreset
                        {
                            Id = oid,
                            Name = SanitizeName(sName) ?? $"Мигрированный {i + 1}",
                            LevelKey = MapLegacyPerm(sPerm),
                            SavedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            Items = items
                        });
                        migratedIds.Add(oid);
                    }

                    if (migratedIds.Count == 0)
                    {
                        skipCnt++; report.Add($"{uid}: ни в одном сохранении нет валидных предметов — статус 'empty'");
                        state[uidStr] = "empty"; MigStateSave(state);
                        continue;
                    }

                    // ActiveLoadout: int-индекс или имя сохранения → own:<id>
                    var act = leg["ActiveLoadout"] ?? leg["activeLoadout"];
                    var actIdx = -1;
                    if (act != null && act.Type == JTokenType.Integer) actIdx = act.Value<int>();
                    else if (act != null && act.Type == JTokenType.String)
                    {
                        var nm = act.Value<string>();
                        for (var i = 0; i < loadouts.Count; i++)
                        {
                            var sname = loadouts[i]?["SaveName"]?.Value<string>() ??
                                        loadouts[i]?["saveName"]?.Value<string>();
                            if (string.Equals(sname, nm, StringComparison.OrdinalIgnoreCase)) { actIdx = i; break; }
                        }
                        int parsed;
                        if (actIdx < 0 && int.TryParse(nm, out parsed)) actIdx = parsed;
                    }
                    if (actIdx >= 0 && actIdx < migratedIds.Count)
                        pd.Active = "own:" + migratedIds[Math.Min(actIdx, migratedIds.Count - 1)];

                    // атомарность: сначала данные и отчёт, потом метка статуса
                    pd.MigratedFrom = "LoadoutController";
                    MarkDirty(uid); // запись данных игрока (с .bak)
                    okCnt++;
                    report.Add($"{uid}: пресетов {migratedIds.Count} (пустых сохранений: {emptySaves})" +
                               (actIdx >= 0 ? $", активный → own:{migratedIds[Math.Min(actIdx, migratedIds.Count - 1)]}" : ""));
                    state[uidStr] = "migrated"; MigStateSave(state);
                }

                var stamp = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} миграция: ok={okCnt} skip={skipCnt} err={errCnt}";
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

        // SavedPerm старого плагина → ключ нашего пресета: точный матч по permission,
        // иначе первый уровень с личными сохранениями, иначе дефолт
        private string MapLegacyPerm(string savedPerm)
        {
            if (!string.IsNullOrEmpty(savedPerm))
            {
                var byPerm = _cfg.Presets?.FirstOrDefault(pr =>
                    string.Equals(pr.Permission, savedPerm, StringComparison.OrdinalIgnoreCase));
                if (byPerm != null) return byPerm.Key;
            }
            return _cfg.Presets?.FirstOrDefault(pr => pr.AllowPersonalSaves)?.Key
                   ?? _cfg.Presets?.FirstOrDefault()?.Key ?? "default";
        }

        // Парсинг записи предмета старого формата: поля берём из нескольких
        // вариантов имён, shortname обязателен и должен резолвиться
        private LoItem LegacyItem(JObject el)
        {
            var sn = (string)(el["Shortname"] ?? el["shortname"] ?? el["ShortName"] ?? el["Name"]);
            if (string.IsNullOrEmpty(sn)) return null;
            var def = ItemManager.FindItemDefinition(sn);
            if (def == null)
            {
                var id = (int?)(el["ItemID"] ?? el["itemId"] ?? el["ItemId"]) ?? 0;
                if (id != 0 && id != -1) def = ItemManager.FindItemDefinition(id);
            }
            if (def == null) return null;
            var mods = new List<string>();
            var modsArr = el["Mods"] as JArray ?? el["mods"] as JArray ?? el["Attachments"] as JArray;
            if (modsArr != null)
                foreach (var m in modsArr)
                {
                    var msn = m.Value<string>();
                    if (!string.IsNullOrEmpty(msn) && ItemManager.FindItemDefinition(msn) != null)
                        mods.Add(msn);
                }
            return new LoItem
            {
                Short = def.shortname,
                ItemId = def.itemid,
                Amount = Math.Max(1, (int?)(el["Amount"] ?? el["amount"]) ?? 1),
                Cont = NormContainer((string)(el["Container"] ?? el["container"])),
                Pos = (int?)(el["Position"] ?? el["pos"] ?? el["Slot"]) ?? -1,
                Skin = (ulong?)(el["SkinID"] ?? el["skin"] ?? el["Skin"]) ?? 0UL,
                AmmoType = (string)(el["Ammotype"] ?? el["ammoType"] ?? el["AmmoType"]),
                AmmoCount = (int?)(el["Ammo"] ?? el["ammoCount"] ?? el["AmmoCount"]) ?? 0,
                Mods = mods.Count > 0 ? mods : null
            };
        }

        private static string NormContainer(string c)
        {
            if (string.IsNullOrEmpty(c)) return "main";
            c = c.ToLowerInvariant();
            if (c.Contains("belt")) return "belt";
            if (c.Contains("wear")) return "wear";
            return "main";
        }

        #endregion

        #region UI (OctaUI, опционально)

        // Живой поиск OctaUI; при отсутствии — команды/чат продолжают работать
        private void UiOpen(BasePlayer p) => UiDraw(p, null);

        // Имена оверлеев, которые OctaUI вешает на UiName: Confirm/Prompt/Toast.
        // Если родителя нет — они становятся сиротами на Overlay и DestroyUi(UiName)
        // до них не достаёт; сносим всегда по всем известным именам.
        private void UiDestroyAll(BasePlayer p)
        {
            CuiHelper.DestroyUi(p, UiName);
            CuiHelper.DestroyUi(p, UiName + "_CFM");
            CuiHelper.DestroyUi(p, UiName + "_PRM");
            CuiHelper.DestroyUi(p, UiName + "_TST");
        }

        // только оверлеи — корневое окно остаётся (Prompt/Confirm — его дети)
        private void UiDestroyOverlays(BasePlayer p)
        {
            CuiHelper.DestroyUi(p, UiName + "_CFM");
            CuiHelper.DestroyUi(p, UiName + "_PRM");
        }

        private void UiDraw(BasePlayer p, string sel)
        {
            UiDestroyAll(p); // иначе повторная отрисовка наслаивает слои с теми же именами

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
            ui.Call("ItemGrid", c, content, UiName + "_grid", new JObject
            {
                ["rect"] = "0.35 0.30 0.98 0.86", ["cols"] = 6, ["gap"] = 0.012f,
                ["max"] = 30, ["items"] = items
            });

            // Кнопки действий
            var y = 0.20f;
            ui.Call("Button", c, content, UiName + "_SEL", new JObject
            { ["rect"] = $"0.35 {y:0.###} 0.50 {y + 0.06f:0.###}", ["text"] = "ВЫБРАТЬ", ["cmd"] = $"octaloadoutsui select {sel}", ["accent"] = "gold" });
            if (sel.StartsWith("own:"))
            {
                var oid = sel.Substring(4);
                ui.Call("Button", c, content, UiName + "_REN", new JObject
                { ["rect"] = $"0.51 {y:0.###} 0.64 {y + 0.06f:0.###}", ["text"] = "ИМЯ", ["cmd"] = $"octaloadoutsui rename {oid}", ["color"] = "0.20 0.20 0.18 0.95" });
                ui.Call("Button", c, content, UiName + "_REPL", new JObject
                { ["rect"] = $"0.65 {y:0.###} 0.82 {y + 0.06f:0.###}", ["text"] = "ОБНОВИТЬ ИЗ ИНВЕНТАРЯ", ["cmd"] = $"octaloadoutsui askrepl {oid}", ["color"] = "0.14 0.28 0.14 0.95" });
                ui.Call("Button", c, content, UiName + "_DEL", new JObject
                { ["rect"] = $"0.83 {y:0.###} 0.98 {y + 0.06f:0.###}", ["text"] = "УДАЛИТЬ", ["cmd"] = $"octaloadoutsui askdel {oid}", ["color"] = "0.45 0.12 0.10 0.95" });
            }
            ui.Call("Button", c, content, UiName + "_NONE", new JObject
            { ["rect"] = $"0.35 {y - 0.075f:0.###} 0.55 {y - 0.015f:0.###}", ["text"] = "СБРОСИТЬ ВЫБОР", ["cmd"] = "octaloadoutsui select none", ["color"] = "0.15 0.15 0.15 0.9" });
        }

        // Превью сохранения: kept-иконки + причины отфильтрованных, подтверждение
        private void UiDrawSavePreview(Plugin ui, BasePlayer p)
        {
            UiDestroyAll(p);
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

            ui.Call("ItemGrid", c, content, UiName + "_pg", new JObject
            {
                ["rect"] = "0.02 0.30 0.66 0.94", ["cols"] = 7, ["gap"] = 0.010f,
                ["max"] = 42, ["items"] = kept
            });
            var sb = new System.Text.StringBuilder();
            foreach (var sk in skipped.Take(16))
                sb.AppendLine($"✗ {sk["shortname"]} — {sk["reason"]}");
            if (skipped.Count > 16) sb.AppendLine($"… ещё {skipped.Count - 16}");
            ui.Call("Text", c, content, null, new JObject
            { ["rect"] = "0.68 0.30 0.98 0.94", ["text"] = sb.Length > 0 ? sb.ToString() : "Всё войдёт в пресет.", ["size"] = 10, ["align"] = "upperleft", ["color"] = "0.9 0.55 0.45 1" });

            // имя вводится в Prompt-модалке: кнопка открывает её, текст прилетает в savename
            ui.Call("Button", c, content, UiName + "_ok", new JObject
            { ["rect"] = "0.02 0.10 0.50 0.20", ["text"] = "СОХРАНИТЬ (указать имя)", ["cmd"] = "octaloadoutsui saveask", ["accent"] = "gold" });
            ui.Call("Button", c, content, UiName + "_no", new JObject
            { ["rect"] = "0.52 0.10 0.72 0.20", ["text"] = "ОТМЕНА", ["cmd"] = "octaloadoutsui cancel", ["color"] = "0.30 0.10 0.08 0.95" });
            ui.Call("Show", p, c);
        }

        private readonly Dictionary<ulong, string> _pendingName = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _pendingRename = new Dictionary<ulong, string>();
        // ожидающее chat-сохранение (uid → имя пресета), живёт до /loadout confirm
        private readonly Dictionary<ulong, string> _pendingChatSave = new Dictionary<ulong, string>();

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
                    UiDestroyAll(p);
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
                case "saveask":
                {
                    UiDestroyOverlays(p);
                    var pc = new CuiElementContainer();
                    ui?.Call("Prompt", pc, UiName, null, new JObject
                    {
                        ["title"] = "ИМЯ ПРЕСЕТА", ["text"] = "Как назвать этот loadout?",
                        ["placeholder"] = "мой сет", ["limit"] = MaxNameLen,
                        ["cmdInput"] = "octaloadoutsui savename",
                        ["cmdYes"] = "octaloadoutsui savedo",
                        ["cmdNo"] = "octaloadoutsui cancel", ["accent"] = "gold"
                    });
                    ui?.Call("Show", p, pc);
                    return;
                }
                case "savename":
                    // FullString = "savename <введённый текст>" — берём всё после первого слова
                    var fs = arg.FullString.ToString() ?? "";
                    var sp = fs.IndexOf(' ');
                    _pendingName[(ulong)p.userID] = sp >= 0 ? fs.Substring(sp + 1).Trim() : "";
                    return;
                case "savedo":
                {
                    var uidS = (ulong)p.userID;
                    string nm;
                    _pendingName.TryGetValue(uidS, out nm);
                    if (OpRepeat(uidS, "save")) { ui?.Call("Toast", p, UiName, "Повтори чуть позже", "red"); return; }
                    var r = SavePersonal(p, nm);
                    ui?.Call("Toast", p, UiName, r.msg, r.code == "OK" ? "accent" : "red");
                    _pendingName.Remove(uidS);
                    if (r.code == "OK") UiDraw(p, "own:" + (string)r.data["id"]);
                    return;
                }
                case "cancel":
                    _pendingName.Remove((ulong)p.userID);
                    UiDraw(p, null);
                    return;
                case "askdel":
                {
                    UiDestroyOverlays(p);
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
                    var did = arg.GetString(1);
                    if (OpRepeat((ulong)p.userID, "del:" + did)) return;
                    var r = DeletePreset(p, did);
                    ui?.Call("Toast", p, UiName, r.msg, r.code == "OK" ? "accent" : "red");
                    UiDraw(p, null);
                    return;
                }
                case "askrepl":
                {
                    UiDestroyOverlays(p);
                    var c = new CuiElementContainer();
                    ui?.Call("Confirm", c, UiName, null, new JObject
                    {
                        ["title"] = "Обновить содержимое?", ["text"] = "Текущий инвентарь перезапишет предметы пресета.",
                        ["yes"] = $"octaloadoutsui repldo {arg.GetString(1)}",
                        ["no"] = "octaloadoutsui cancel", ["accent"] = "gold"
                    });
                    ui?.Call("Show", p, c);
                    return;
                }
                case "repldo":
                {
                    var rid2 = arg.GetString(1);
                    if (OpRepeat((ulong)p.userID, "repl:" + rid2)) return;
                    var r = ReplacePresetItems(p, rid2);
                    ui?.Call("Toast", p, UiName, r.msg, r.code == "OK" ? "accent" : "red");
                    UiDraw(p, "own:" + rid2);
                    return;
                }
                case "rename":
                {
                    var oid = arg.GetString(1);
                    UiDestroyOverlays(p);
                    var rc = new CuiElementContainer();
                    ui?.Call("Prompt", rc, UiName, null, new JObject
                    {
                        ["title"] = "ПЕРЕИМЕНОВАТЬ", ["text"] = "Новое имя пресета",
                        ["limit"] = MaxNameLen,
                        ["cmdInput"] = $"octaloadoutsui renname {oid}",
                        ["cmdYes"] = $"octaloadoutsui rensave {oid}",
                        ["cmdNo"] = "octaloadoutsui cancel", ["accent"] = "gold"
                    });
                    ui?.Call("Show", p, rc);
                    return;
                }
                case "renname":
                {
                    var fs2 = arg.FullString.ToString() ?? "";
                    var sp2 = fs2.IndexOf(' ');
                    var rest = sp2 >= 0 ? fs2.Substring(sp2 + 1) : "";
                    var sp3 = rest.IndexOf(' ');
                    _pendingName[(ulong)p.userID] = sp3 >= 0 ? rest.Substring(sp3 + 1).Trim() : "";
                    _pendingRename[(ulong)p.userID] = arg.GetString(1);
                    return;
                }
                case "rensave":
                {
                    var uid2 = (ulong)p.userID;
                    string nm2, rid;
                    _pendingName.TryGetValue(uid2, out nm2);
                    _pendingRename.TryGetValue(uid2, out rid);
                    _pendingName.Remove(uid2);
                    _pendingRename.Remove(uid2);
                    var r = RenamePreset(p, rid, nm2); // проверка уровня — внутри операции
                    ui?.Call("Toast", p, UiName, r.msg, r.code == "OK" ? "accent" : "red");
                    if (r.code == "OK") UiDraw(p, "own:" + rid);
                    return;
                }
            }
        }

        #endregion
    }
}
