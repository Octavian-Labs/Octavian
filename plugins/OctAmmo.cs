using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctAmmo", "Octavian", "1.0.0")]
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
        [Command("at")]
        private void CmdAt(IPlayer player, string cmd, string[] args)
        {
            if (player == null || !player.IsConnected) return;
            var bp = player.Object as BasePlayer;
            if (bp == null) return;

            // Подсказка
            if (args.Length == 0)
            {
                player.Reply(
                    "OctAmmo (без UI)\n" +
                    "Команды:\n" +
                    "  /at list [filter] [page] — список оружия\n" +
                    "  /at get <shortname> — показать ёмкости\n" +
                    "  /at set <shortname> <Default|Senior|Middle|High|Имя_уровня> <value> — задать ёмкость\n" +
                    "  /at save — сохранить конфиг\n" +
                    "  /at reload — перечитать конфиг\n" +
                    $"Права уровней выдаются через permissions (например, oxide.grant group default {PermPrefix}.high)."
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
                    player.Reply("✅ Конфиг сохранён.");
                    break;

                case "reload":
                    if (!HasAdmin(bp, player)) return;
                    LoadConfig();
                    player.Reply("🔄 Конфиг перезагружен.");
                    break;

                default:
                    player.Reply("❓ Неизвестная команда. Используйте: /at, /at list, /at get, /at set, /at save, /at reload");
                    break;
            }
        }

        private bool HasAdmin(BasePlayer bp, IPlayer p)
        {
            if (p.HasPermission(PermAdmin) || (bp != null && bp.IsAdmin)) return true;
            p.Reply($"Недостаточно прав. Требуется {PermAdmin} или статус администратора.");
            return false;
        }

        private void HandleList(IPlayer player, string[] args)
        {
            if (_config?.Weapons == null)
            {
                player.Reply("Конфигурация не загружена.");
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
            var lines = slice.Select(w => $"{w.DisplayName} ({w.Shortname}) — Base:{w.BaseGame} Def:{w.Default}");

            player.Reply($"Оружие ({all.Count}) стр. {page}/{totalPages}:\n" + string.Join("\n", lines));
        }

        private void HandleGet(IPlayer player, string[] args)
        {
            if (_config?.Weapons == null)
            {
                player.Reply("Конфигурация не загружена.");
                return;
            }

            if (args.Length < 1) { player.Reply("Использование: /at get <shortname>"); return; }
            var sn = args[0];
            if (!_config.Weapons.TryGetValue(sn, out var w))
            {
                player.Reply($"Оружие '{sn}' не найдено.");
                return;
            }

            var groups = _config.Groups?.Select(g =>
            {
                var has = w.Levels?.TryGetValue(g.Name, out var v) == true ? v.ToString() : "-";
                return $"{g.Name}:{has}";
            }) ?? new[] { "none" };

            player.Reply(
                $"{w.DisplayName} ({w.Shortname})\n" +
                $"BaseGame: {w.BaseGame}\n" +
                $"Default: {w.Default}\n" +
                string.Join(", ", groups)
            );
        }

        private void HandleSet(IPlayer player, string[] args)
        {
            if (_config?.Weapons == null)
            {
                player.Reply("Конфигурация не загружена.");
                return;
            }

            if (args.Length < 3)
            {
                player.Reply("Использование: /at set <shortname> <Default|Senior|Middle|High|Имя_уровня> <value>");
                return;
            }

            var sn = args[0];
            var tier = args[1];
            var valStr = args[2];

            if (!_config.Weapons.TryGetValue(sn, out var w))
            {
                player.Reply($"Оружие '{sn}' не найдено.");
                return;
            }

            if (!int.TryParse(valStr, out var val))
            {
                player.Reply("Значение должно быть целым числом.");
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

            player.Reply($"✅ OK: {w.DisplayName} ({w.Shortname}) [{tier}] = {val}");
        }
        #endregion

        #region Discovery
        private void TryAutofillWeaponsFromGame()
        {
            if (ItemManager.itemList == null || _config?.Weapons == null) return;

            foreach (var def in ItemManager.itemList)
            {
                if (def == null) continue;
                if (def.category != ItemCategory.Weapon) continue;

                if (!_config.Weapons.ContainsKey(def.shortname))
                {
                    var guess = GuessDefaultCapacity(def.shortname);

                    _config.Weapons[def.shortname] = new WeaponEntry
                    {
                        Shortname = def.shortname,
                        DisplayName = def.displayName?.english ?? def.shortname,
                        BaseGame = guess,
                        Default = guess,
                        Levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    };
                }
                else
                {
                    // Подстрахуем заполнение BaseGame для старых конфигов
                    var w = _config.Weapons[def.shortname];
                    if (w.BaseGame < 0) w.BaseGame = (w.Default > 0 ? w.Default : GuessDefaultCapacity(def.shortname));
                }
            }
            SaveConfig();
        }

        private int GuessDefaultCapacity(string shortname)
        {
            var sn = shortname.ToLowerInvariant();
            if (sn.Contains("shotgun")) return 6;
            if (sn.Contains("spas")) return 6;
            if (sn.Contains("smg")) return 24;
            if (sn.Contains("thompson")) return 20;
            if (sn.Contains("pistol")) return 10;
            if (sn.Contains("python")) return 6;
            if (sn.Contains("revolver")) return 8;
            return 30;
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

            return Mathf.Clamp(we.Default, _config.MinCapacity, _config.MaxCapacity);
        }

        private void ApplyForItem(BasePlayer player, Item item)
        {
            if (player == null || item == null || _config == null || _config.Weapons == null)
                return;

            if (IsNpc(player))
                return;

            var shortname = item.info?.shortname;
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

            // Обновление BaseGame при первом «живом» чтении из игры
            try
            {
                var liveCap = proj.primaryMagazine.capacity;
                if (_config.Weapons.TryGetValue(shortname, out var we))
                {
                    var changed = false;

                    if (we.BaseGame != liveCap)
                    {
                        // если Default совпадал со старым BaseGame — синхронизируем вместе
                        if (we.Default == we.BaseGame)
                            we.Default = liveCap;

                        we.BaseGame = liveCap;
                        changed = true;
                    }

                    if (changed)
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
