using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctAmmo", "Octavian", "1.1.0")]
    [Description("Гибкая ёмкость магазинов по уровням (Default/Senior/Middle/High) через permissions. Без UI. Хранит базовые значения из игры.")]
    public class OctAmmo : CovalencePlugin
    {
        // ===== Permissions =====
        private const string PermPrefix = "octammo";
        private const string PermAdmin  = PermPrefix + ".admin";   // админ-команды

        // ===== Runtime =====
        private PluginConfig _config;
        private const int PageSize = 30;

        // NPC (учёные и пр.) имеют маленькие положительные userID, у реальных игроков — SteamID64
        private static bool IsNpc(BasePlayer player) => player.IsNpc || player.userID < 10_000_000;

        #region Config
        [Serializable]
        public class GroupLevel
        {
            public string Name;           // "High", "Middle", "Senior"
            public string Permission;     // "octammo.high", ...
            public int Priority;          // чем больше, тем выше приоритет
        }

        [Serializable]
        public class WeaponEntry
        {
            public string Shortname;      // "rifle.ak"
            public string DisplayName;    // "Assault Rifle"

            // Базовая (из игры) ёмкость магазина, чтобы видеть «заводское» значение
            public int BaseGame = -1;

            // Текущее значение по умолчанию (для игроков без пермишенов)
            public int Default = 30;

            // Кастомные уровни по имени группы (Senior/Middle/High/и т.д.)
            public Dictionary<string, int> Levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        [Serializable]
        public class PluginConfig
        {
            public List<GroupLevel> Groups = new List<GroupLevel>
            {
                new GroupLevel { Name = "High",   Permission = PermPrefix + ".high",   Priority = 3 },
                new GroupLevel { Name = "Middle", Permission = PermPrefix + ".middle", Priority = 2 },
                new GroupLevel { Name = "Senior", Permission = PermPrefix + ".senior", Priority = 1 }
            };

            public Dictionary<string, WeaponEntry> Weapons = new Dictionary<string, WeaponEntry>(StringComparer.OrdinalIgnoreCase);

            // уровень OctVIP → имя тира из Groups (1=Bronze .. 6=Admin); срабатывает, если octammo.*-пермишен не выбран
            public Dictionary<int, string> VipLevelTiers = new Dictionary<int, string>
            {
                [1] = "Senior",
                [2] = "Senior",
                [3] = "Middle",
                [4] = "Middle",
                [5] = "High",
                [6] = "High",
            };

            public int MinCapacity = 0;
            public int MaxCapacity = 512;
            public float ReapplyInterval = 10.0f; // сек
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();

            // Примеры — остальной огнестрел подтянется из ItemManager
            SeedWeapon("rifle.ak",       "Assault Rifle", 30, new Dictionary<string,int> {{"Senior",35},{"Middle",45},{"High",60}});
            SeedWeapon("rifle.lr300",    "LR-300",        30, new Dictionary<string,int> {{"Senior",35},{"Middle",45},{"High",60}});
            SeedWeapon("rifle.semiauto", "Semi-Auto",     16, new Dictionary<string,int> {{"Senior",20},{"Middle",28},{"High",36}});
            SeedWeapon("smg.thompson",   "Thompson",      20, new Dictionary<string,int> {{"Senior",25},{"Middle",35},{"High",50}});
            SeedWeapon("pistol.m92",     "M92",           15, new Dictionary<string,int> {{"Senior",18},{"Middle",25},{"High",35}});
            SeedWeapon("shotgun.spas12", "SPAS-12",        6, new Dictionary<string,int> {{"Senior",8},{"Middle",10},{"High",12}});
            SeedWeapon("rocket.launcher", "Rocket Launcher", 3, new Dictionary<string,int> {{"Middle",6},{"High",8}});

            SaveConfig();
        }

        private void SeedWeapon(string shortname, string display, int def, Dictionary<string, int> levels)
        {
            var we = new WeaponEntry
            {
                Shortname = shortname,
                DisplayName = display,
                BaseGame = def,
                Default = def,
                Levels = levels != null
                    ? new Dictionary<string, int>(levels, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            };
            _config.Weapons[shortname] = we;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();

            // Нормализуем группы: только с правильным префиксом плагина
            foreach (var g in _config.Groups)
            {
                if (string.IsNullOrWhiteSpace(g.Name)) g.Name = "Level";
                if (string.IsNullOrWhiteSpace(g.Permission))
                    g.Permission = $"{PermPrefix}.{g.Name.ToLowerInvariant()}";
                else if (!g.Permission.StartsWith(PermPrefix + ".", StringComparison.OrdinalIgnoreCase))
                {
                    // Переписываем в каноничный вид, сохранив "суффикс"
                    var suffix = g.Permission.Contains(".")
                        ? g.Permission.Substring(g.Permission.LastIndexOf('.') + 1)
                        : g.Permission;
                    g.Permission = $"{PermPrefix}.{suffix.ToLowerInvariant()}";
                }
            }

            // Уникальные имена, сортировка по приоритету
            _config.Groups = _config.Groups
                .Where(g => !string.IsNullOrWhiteSpace(g.Permission))
                .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Priority).First())
                .OrderByDescending(g => g.Priority)
                .ToList();

            // Подстрахуем новые поля
            if (_config.Weapons == null)
                _config.Weapons = new Dictionary<string, WeaponEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in _config.Weapons.ToList())
            {
                if (kv.Value.Levels == null)
                    kv.Value.Levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            SaveConfig();
        }

        protected override void SaveConfig()
        {
            try
            {
                Config.WriteObject(_config, true);
            }
            catch (Exception ex)
            {
                PrintError($"Ошибка при сохранении конфига: {ex.Message}");
            }
        }
        #endregion

        #region Lifecycle
        void Init()
        {
            permission.RegisterPermission(PermAdmin, this);

            // Регистрируем только каноничные permissions плагина
            foreach (var g in _config?.Groups ?? Enumerable.Empty<GroupLevel>())
            {
                if (!string.IsNullOrWhiteSpace(g.Permission))
                    permission.RegisterPermission(g.Permission, this);
            }
        }

        void OnServerInitialized()
        {
            if (_config == null)
            {
                Puts($"[{Name}] Config is null, cannot initialize properly");
                return;
            }

            TryAutofillWeaponsFromGame();
            StartReapplyTimer();
            Puts($"[{Name}] Weapons tracked: {_config.Weapons.Count}, groups: {string.Join(", ", _config.Groups?.Select(g => $"{g.Name}({g.Permission}, prio={g.Priority})") ?? new[] { "none" })}");
        }
        #endregion

        #region Commands (без UI)
        // Фирменный префикс Octavian для всех чат-ответов
        private const string ChatPrefix = "<color=#E8B84B>◆ OctAmmo</color>\n";
        private void Reply(IPlayer player, string msg) => player.Reply(ChatPrefix + msg);
        private static string Val(object v) => $"<color=#FFD35C>{v}</color>";

        [Command("at")]
        private void CmdAt(IPlayer player, string cmd, string[] args)
        {
            if (player == null || !player.IsConnected) return;
            var bp = player.Object as BasePlayer;
            if (bp == null) return;

            // Подсказка
            if (args.Length == 0)
            {
                Reply(player,
                    "Ёмкости магазинов по уровням доступа.\n\n" +
                    $"  {Val("/at list")} [filter] [page] — список оружия\n" +
                    $"  {Val("/at get")} <оружие> — показать ёмкости\n" +
                    $"  {Val("/at set")} <оружие> <уровень> <число> — задать ёмкость\n" +
                    $"  {Val("/at save")} — сохранить конфиг\n" +
                    $"  {Val("/at reload")} — перечитать конфиг\n\n" +
                    $"Уровни выдаются пермишенами: {Val(PermPrefix + ".high")} и др."
                );
                return;
            }

            var sub = args[0].ToLowerInvariant();
            switch (sub)
            {
                case "list":
                    if (!HasAdmin(bp, player)) return;
                    HandleList(player, args.Skip(1).ToArray());
                    break;

                case "get":
                    if (!HasAdmin(bp, player)) return;
                    HandleGet(player, args.Skip(1).ToArray());
                    break;

                case "set":
                    if (!HasAdmin(bp, player)) return;
                    HandleSet(player, args.Skip(1).ToArray());
                    break;

                case "save":
                    if (!HasAdmin(bp, player)) return;
                    SaveConfig();
                    Reply(player, "Конфиг сохранён.");
                    break;

                case "reload":
                    if (!HasAdmin(bp, player)) return;
                    LoadConfig();
                    Reply(player, "Конфиг перезагружен.");
                    break;

                default:
                    Reply(player, $"Неизвестная команда. Смотри {Val("/at")}");
                    break;
            }
        }

        private bool HasAdmin(BasePlayer bp, IPlayer p)
        {
            if (p.HasPermission(PermAdmin) || (bp != null && bp.IsAdmin)) return true;
            Reply(p, $"Недостаточно прав. Требуется {Val(PermAdmin)} или статус администратора.");
            return false;
        }

        private void HandleList(IPlayer player, string[] args)
        {
            if (_config?.Weapons == null)
            {
                Reply(player, "Конфигурация не загружена.");
                return;
            }

            string filter = null;
            int page = 1;
            if (args.Length >= 1) filter = args[0];
            if (args.Length >= 2 && int.TryParse(args[1], out var p)) page = Mathf.Max(1, p);

            var all = _config.Weapons.Values
                .OrderBy(w => w.DisplayName)
                .ThenBy(w => w.Shortname)
                .ToList();
            if (!string.IsNullOrEmpty(filter))
            {
                filter = filter.ToLowerInvariant();
                all = all.Where(w => (w.DisplayName ?? "").ToLowerInvariant().Contains(filter)
                                   || (w.Shortname ?? "").ToLowerInvariant().Contains(filter))
                         .ToList();
            }

            var totalPages = Mathf.Max(1, Mathf.CeilToInt(all.Count / (float)PageSize));
            page = Mathf.Clamp(page, 1, totalPages);
            var slice = all.Skip((page - 1) * PageSize).Take(PageSize);
            var lines = slice.Select(w => $"{w.DisplayName} <color=#8A8F98>({w.Shortname})</color> — {Val(w.Default)}");

            Reply(player, $"Оружие: {Val(all.Count)} — стр. {page}/{totalPages}\n" + string.Join("\n", lines));
        }

        private void HandleGet(IPlayer player, string[] args)
        {
            if (_config?.Weapons == null)
            {
                Reply(player, "Конфигурация не загружена.");
                return;
            }

            if (args.Length < 1) { Reply(player, $"Использование: {Val("/at get")} <оружие>"); return; }
            var sn = args[0];
            if (!_config.Weapons.TryGetValue(sn, out var w))
            {
                Reply(player, $"Оружие {Val(sn)} не найдено.");
                return;
            }

            var groups = _config.Groups?.Select(g =>
            {
                var has = w.Levels?.TryGetValue(g.Name, out var v) == true ? Val(v) : "<color=#8A8F98>—</color>";
                return $"{g.Name}: {has}";
            }) ?? new[] { "<color=#8A8F98>нет уровней</color>" };

            Reply(player,
                $"{Val(w.DisplayName)} <color=#8A8F98>({w.Shortname})</color>\n" +
                $"Заводская: {Val(w.BaseGame)} · По умолчанию: {Val(w.Default)}\n" +
                string.Join("  ·  ", groups)
            );
        }

        private void HandleSet(IPlayer player, string[] args)
        {
            if (_config?.Weapons == null)
            {
                Reply(player, "Конфигурация не загружена.");
                return;
            }

            if (args.Length < 3)
            {
                Reply(player, $"Использование: {Val("/at set")} <оружие> <уровень> <число>");
                return;
            }

            var sn = args[0];
            var tier = args[1];
            var valStr = args[2];

            if (!_config.Weapons.TryGetValue(sn, out var w))
            {
                Reply(player, $"Оружие {Val(sn)} не найдено.");
                return;
            }

            if (!int.TryParse(valStr, out var val))
            {
                Reply(player, "Значение должно быть целым числом.");
                return;
            }

            val = Mathf.Clamp(val, _config.MinCapacity, _config.MaxCapacity);

            if (tier.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                w.Default = val;
            }
            else
            {
                if (w.Levels == null) w.Levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                w.Levels[tier] = val;
            }

            SaveConfig();
            foreach (var bp in BasePlayer.activePlayerList ?? Enumerable.Empty<BasePlayer>())
                ApplyForActiveItem(bp);

            Reply(player, $"{Val(w.DisplayName)} <color=#8A8F98>({w.Shortname})</color> — {Val(tier)} = {Val(val)}");
        }
        #endregion

        #region Discovery
        /// <summary>Реальная заводская ёмкость магазина — читаем из префаба, а не гадаем по имени.</summary>
        private int GetPrefabCapacity(ItemDefinition def)
        {
            try
            {
                var modEntity = def?.itemMods?.OfType<ItemModEntity>().FirstOrDefault()
                             ?? def?.GetComponent<ItemModEntity>();
                var proj = modEntity?.entityPrefab?.GetEntity() as BaseProjectile;
                var mag = proj?.primaryMagazine;
                if (mag == null) return -1;
                var cap = mag.definition.builtInSize;
                if (cap <= 0) cap = mag.capacity;
                return cap > 0 ? cap : -1;
            }
            catch
            {
                return -1;
            }
        }

        private void TryAutofillWeaponsFromGame()
        {
            if (ItemManager.itemList == null || _config?.Weapons == null) return;

            var dead = new List<string>();

            foreach (var def in ItemManager.itemList)
            {
                if (def == null) continue;
                if (def.category != ItemCategory.Weapon) continue;

                // Редиректы (crystal/paid варианты AK и пр.) не создают своих записей —
                // в рантайме они резолвятся на базовый предмет через isRedirectOf
                if (def.isRedirectOf != null)
                {
                    dead.Add(def.shortname);
                    continue;
                }

                // Только настоящее оружие с магазином — мили, аттачи, гранаты отпадают сами
                var prefabCap = GetPrefabCapacity(def);
                if (prefabCap < 0)
                {
                    dead.Add(def.shortname);
                    continue;
                }

                if (!_config.Weapons.ContainsKey(def.shortname))
                {
                    _config.Weapons[def.shortname] = new WeaponEntry
                    {
                        Shortname = def.shortname,
                        DisplayName = def.displayName?.english ?? def.shortname,
                        BaseGame = prefabCap,
                        Default = prefabCap,
                        Levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    };
                }
                else
                {
                    var w = _config.Weapons[def.shortname];
                    // BaseGame всегда из префаба — живое значение могло быть уже нами изменено
                    if (w.BaseGame != prefabCap)
                    {
                        if (w.Default == w.BaseGame)
                            w.Default = prefabCap;
                        w.BaseGame = prefabCap;
                    }
                }
            }

            // Чистим записи без магазина (мили/аттачи и пр.) — оружия, которого нет в игре, не трогаем
            foreach (var sn in dead)
            {
                if (_config.Weapons.ContainsKey(sn))
                    _config.Weapons.Remove(sn);
            }

            SaveConfig();
        }
        #endregion

        #region Runtime Enforcement
        private int GetCapacityFor(BasePlayer player, string shortname)
        {
            if (player == null || string.IsNullOrEmpty(shortname) || _config == null || _config.Weapons == null)
                return -1;

            if (!_config.Weapons.TryGetValue(shortname, out var we))
                return -1;

            if (IsNpc(player))
                return -1;

            foreach (var g in _config.Groups?.OrderByDescending(x => x.Priority) ?? Enumerable.Empty<GroupLevel>())
            {
                var perm = g.Permission; // уже каноничное octammo.*
                if (string.IsNullOrEmpty(perm)) continue;

                try
                {
                    if (permission.UserHasPermission(player.UserIDString, perm))
                    {
                        if (we.Levels != null && we.Levels.TryGetValue(g.Name, out var cap))
                            return Mathf.Clamp(cap, _config.MinCapacity, _config.MaxCapacity);
                    }
                }
                catch
                {
                    continue;
                }
            }

            // VIP-тир из OctVIP, если ни один octammo.*-пермишен не сработал
            var vipTier = GetVipTier((ulong)player.userID);
            if (vipTier != null && we.Levels != null && we.Levels.TryGetValue(vipTier, out var vipCap))
                return Mathf.Clamp(vipCap, _config.MinCapacity, _config.MaxCapacity);

            return Mathf.Clamp(we.Default, _config.MinCapacity, _config.MaxCapacity);
        }

        // PluginReference залипает на выгруженной инстанции — ищем живой плагин по имени
        private Oxide.Core.Plugins.Plugin FindOctaPlugin(string name)
        {
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
            {
                if (p.Name == name && p.IsLoaded)
                    return p;
            }
            return null;
        }

        // имя тира по VIP-уровню из конфига "VipLevelTiers"
        private string GetVipTier(ulong userId)
        {
            var vip = FindOctaPlugin("OctVIP");
            if (vip == null || _config == null || _config.VipLevelTiers == null)
                return null;
            var lvl = vip.Call<int>("GetVipLevel", userId);
            return lvl > 0 && _config.VipLevelTiers.TryGetValue(lvl, out var tier) ? tier : null;
        }

        private void ApplyForItem(BasePlayer player, Item item)
        {
            if (player == null || item == null || _config == null || _config.Weapons == null)
                return;

            if (IsNpc(player))
                return;

            // Скин-варианты (rifle.ak.glass и пр.) резолвим на базовый предмет
            var info = item.info?.isRedirectOf ?? item.info;
            var shortname = info?.shortname;
            if (string.IsNullOrEmpty(shortname))
                return;

            var proj = item.GetHeldEntity() as BaseProjectile;
            if (proj == null || proj.primaryMagazine == null)
                return;

            // Защита от раннего вызова: ждём, пока предмет действительно активен у игрока
            try
            {
                if (player.GetActiveItem() != item)
                    return;
            }
            catch
            {
                return;
            }

            // Проверка состояния игрока - не модифицируем оружие в критических состояниях
            if (player.IsReceivingSnapshot || !player.IsConnected || player.IsDestroyed)
                return;

            // BaseGame читаем из префаба — capacity инстанса может быть уже нами изменена
            try
            {
                var gameCap = GetPrefabCapacity(info);
                if (gameCap > 0 && _config.Weapons.TryGetValue(shortname, out var we) && we.BaseGame != gameCap)
                {
                    // если Default совпадал со старым BaseGame — синхронизируем вместе
                    if (we.Default == we.BaseGame)
                        we.Default = gameCap;

                    we.BaseGame = gameCap;
                    SaveConfig();
                }
            }
            catch
            {
                return;
            }

            var targetCap = GetCapacityFor(player, shortname);
            if (targetCap < 0) return;

            var mag = proj.primaryMagazine;
            if (mag.capacity != targetCap)
            {
                try
                {
                    mag.capacity = targetCap;
                    if (mag.contents > targetCap) mag.contents = targetCap;

                    // Мягкая отправка (без Immediate), чтобы не попадать в окно сборки вьюмодели/аттачей (ViewmodelScope)
                    proj.SendNetworkUpdate(); // безопаснее, чем Immediate (см. DelayedModsChanged в базовой логике)
                }
                catch
                {
                }
            }
        }

        // OctVIP сменил уровень — сразу переопределить магазин в руках (не ждать таймер)
        private void OnVipLevelChanged(BasePlayer player, int oldLevel, int newLevel)
        {
            if (player == null)
                return;
            try { ApplyForActiveItem(player); }
            catch { }
        }

        private void ApplyForActiveItem(BasePlayer player)
        {
            if (player == null || player.inventory == null || _config == null || _config.Weapons == null)
                return;

            if (IsNpc(player))
                return;

            try
            {
                var active = player.GetActiveItem();
                if (active == null) return;
                ApplyForItem(player, active);
            }
            catch
            {
            }
        }

        private void StartReapplyTimer()
        {
            timer.Every(Mathf.Max(1f, _config.ReapplyInterval), () =>
            {
                var players = BasePlayer.activePlayerList;
                if (players == null) return;

                foreach (var bp in players)
                {
                    if (bp == null) continue;
                    ApplyForActiveItem(bp);
                }
            });
        }

        // Hooks
        void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem) // корректный хук после полного переключения активного (см. UpdateActiveItem)
        {
            if (player == null) return;
            ApplyForItem(player, newItem);
        }

        void OnEntitySpawned(BaseNetworkable ent)
        {
            var proj = ent as BaseProjectile;
            if (proj == null) return;

            var owner = proj.OwnerID;
            if (owner == 0) return;

            // Дадим клиенту время полностью собрать вьюмодель и VM-аттачи (скоуп), чтобы избежать NRE в ViewmodelScope
            timer.Once(0.5f, () =>
            {
                try
                {
                    var player = BasePlayer.FindByID(owner) ?? BasePlayer.FindSleeping(owner);
                    if (player == null || player.IsDestroyed || IsNpc(player))
                        return;

                    var item = proj.GetItem();
                    if (item == null)
                        return;

                    // применяем только если предмет уже активен
                    try
                    {
                        if (player.GetActiveItem() != item)
                            return;
                    }
                    catch
                    {
                        return;
                    }

                    ApplyForItem(player, item);
                }
                catch
                {
                }
            });
        }

        void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            timer.Once(0.3f, () =>
            {
                try { ApplyForActiveItem(player); }
                catch { }
            });
        }

        void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null) return;
            timer.Once(0.3f, () =>
            {
                try { ApplyForActiveItem(player); }
                catch { }
            });
        }

        // ВАЖНО: правильная сигнатура (ядро вызывает OnItemCraftFinished(task, item, this))
        // см. ItemCrafter.FinishCrafting → Interface.CallHook("OnItemCraftFinished", task, item, this)
        object OnItemCraftFinished(ItemCraftTask task, Item item, ItemCrafter crafter)
        {
            if (item == null || task == null || crafter == null) return null;

            try
            {
                var player = item.GetOwnerPlayer();
                if (player != null)
                {
                    timer.Once(0.1f, () =>
                    {
                        try { ApplyForItem(player, item); }
                        catch { }
                    });
                }
            }
            catch
            {
            }

            return null;
        }
        #endregion
    }
}
