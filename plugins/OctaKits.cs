using System;
using System.Collections.Generic;
using System.Linq;
using Facepunch;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using UnityEngine;
using Oxide.Game.Rust.Cui;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Network;
using System.Reflection;

namespace Oxide.Plugins
{
    [Info("OctaKits", "Octavian", "2.0.0")]
    [Description("Самостоятельная система наборов Octavian: данные, выдача, редактор и API — без зависимости от плагина Kits")]
    public class OctaKits : RustPlugin
    {   
        [PluginReference] Plugin ImageLibrary, Economics, ServerRewards, CopyPaste, PlayerDLCAPI, OctVIP;

        // Фирменная палитра Octavian (золото на стекле, эталон OctaBoss)
        private const string GoldBright = "0.95 0.75 0.30 1";
        private const string Gold       = "0.82 0.61 0.20 1";
        private const string GoldDark   = "0.48 0.32 0.10 1";
        private const string GoldGlass  = "0.82 0.61 0.20 0.18";
        private const string GlassBg    = "0.02 0.018 0.015 0.92";
        private const string GlassLt    = "0.05 0.045 0.035 0.92";
        private const string GlassRow   = "0.08 0.07 0.05 0.85";
        private const string GlassHov   = "0.13 0.105 0.06 0.95";
        private const string TMain      = "0.93 0.88 0.75 1";
        private const string TMuted     = "0.58 0.53 0.43 1";
        private const string TWhite     = "1 0.98 0.90 1";
        private const string CGreen     = "0.20 0.52 0.27 0.92";
        private const string CRed       = "0.58 0.13 0.10 0.92";

        private Dictionary<string, KitCache> kitCache = new Dictionary<string, KitCache>();
        private readonly Dictionary<ulong, Timer> _cdTimers = new Dictionary<ulong, Timer>();
        private readonly Dictionary<ulong, bool> _editorOverwrite = new Dictionary<ulong, bool>();
        private readonly Dictionary<ulong, string> _editorToast = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, (string kit, DateTime until)> _pendingDelete = new Dictionary<ulong, (string, DateTime)>();
        private bool isInitialized = false;
        private bool _initScheduled = false;
        private int _initRetries = 0;

        private void Unload()
        {
            if (!Interface.Oxide.IsShuttingDown)
                SavePlayerData();

            foreach (var t in _cdTimers.Values) t?.Destroy();
            _cdTimers.Clear();
            foreach (var p in BasePlayer.activePlayerList)
            {
                CloseKits_API(p);
                CuiHelper.DestroyUi(p, "octakits_background");
                CuiHelper.DestroyUi(p, "octakits_root");
            }
        }

        private class KitCache
        {
            public string ImageUrl { get; set; }
            public string Description { get; set; }
            public int MaxUses { get; set; }
            public float Cooldown { get; set; }
            public bool HasPermission { get; set; }
            public float Price { get; set; }
        }

        // Интеграция с WelcomePanel через API хуки удалена - используем только API методы

        private void Loaded()
        {
            _canUseSkin = CanUseSkin;
            _getRedirectedIfNotOwned = GetRedirectedShortnameIfNotOwned;

            LoadKitData();
            LoadPlayerData();

            permission.RegisterPermission(ADMIN_PERMISSION, this);
            kitData?.RegisterPermissions(permission, this);

            _costType = ParseType<CostType>(config?.backend?.currency ?? "Scrap");
        }

        void OnServerInitialized()
        {
            LoadData();

            // Время создания карты — база для вайп-кулдаунов наборов
            LastWipeTime = SaveRestore.SaveCreatedTime.Subtract(Epoch).TotalSeconds;

            CheckForShortnameUpdates();

            // Картинки наборов регистрируем с задержкой — ImageLibrary может подняться позже
            timer.Once(2f, () => kitData?.RegisterImages(ImageLibrary));

            if (ImageLibrary != null)
                ImageLibrary.Call("AddImage","https://rustplugins.net/products/welcomepanel/new/file-find.png", "https://rustplugins.net/products/welcomepanel/new/file-find.png");

            if (addonsData == null)
            {
                addonsData = new Dictionary<string, AddonsData>();
            }

            if (!addonsData.ContainsKey("addons"))
            {
                addonsData.Add("addons", new AddonsData());
                addonsData["addons"].RustKits_claimCommand = "kit";
                addonsData["addons"].RustKits_currencyName = "$";
                SaveData();
            }

            // Одноразовый сид стандартных наборов x5 (до инициализации — чтобы попали в кэш и картинки)
            EnsureSeedKits();

            InitializeKits();
        }

        // Хук загрузки плагинов: когда ImageLibrary поднимется после нас — догружаем картинки
        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin?.Name == "ImageLibrary")
                timer.Once(2f, () => kitData?.RegisterImages(ImageLibrary));
        }

        private void OnServerSave() => SavePlayerData();

        private void OnNewSave(string filename)
        {
            // Вайп карты: чистим статистику использований/кулдаунов, если включено в конфиге
            if (config?.backend?.wipeData == true)
                playerData?.Wipe();

            LastWipeTime = SaveRestore.SaveCreatedTime.Subtract(Epoch).TotalSeconds;
        }

        // Автовыдача наборов при спавне (список в конфиге, по приоритету)
        private void OnPlayerRespawned(BasePlayer player)
        {
            var autoKits = config?.backend?.autoKits;
            if (!player || autoKits == null || autoKits.Count == 0)
                return;

            if (Interface.Oxide.CallHook("CanRedeemKit", player) != null)
                return;

            if (Interface.Oxide.CallHook("CanRedeemAutoKit", player) != null)
                return;

            for (int i = 0; i < autoKits.Count; i++)
            {
                if (!kitData.Find(autoKits[i], out KitData.Kit kit))
                    continue;

                if (CanClaimKit(player, kit, true) != null)
                    continue;

                player.inventory.Strip();

                if (GiveKit(player, kit) is string)
                    continue;

                OnKitReceived(player, kit);
                return;
            }
        }

        private void ScheduleInitRetry()
        {
            if (_initScheduled || _initRetries >= 24) return; // максимум ~2 минуты попыток
            _initScheduled = true;
            _initRetries++;
            timer.Once(5f, () =>
            {
                _initScheduled = false;
                if (!isInitialized) InitializeKits();
            });
        }

        private void InitializeKits()
        {
            if (config?.debugMode == true) PrintWarning("[OctaKits DEBUG] Starting InitializeKits");

            if (kitData == null)
            {
                PrintWarning("Данные наборов не загружены — повторим инициализацию позже");
                ScheduleInitRetry();
                return;
            }

            if (ImageLibrary == null)
            {
                PrintWarning("ImageLibrary plugin not found, kit images will not load");
                if (config?.debugMode == true) PrintWarning("[OctaKits DEBUG] ImageLibrary plugin reference is null");
            }

            try
            {
                // Предзагрузка данных всех китов
                string[] allKits = GetAllKits();
                if (allKits != null && allKits.Length > 0)
                {
                    PrintWarning($"Инициализация {allKits.Length} наборов для OctaKits");
                    if (config?.debugMode == true)
                    {
                        PrintWarning($"[OctaKits DEBUG] Found kits: {string.Join(", ", allKits)}");
                    }
                    LoadKitCache(allKits);

                    // Предзагрузка изображений китов
                    timer.Once(2f, () => PreloadKitImages(allKits));
                }
                else
                {
                    PrintWarning("Наборы для инициализации OctaKits не найдены");
                    if (config?.debugMode == true) PrintWarning("[OctaKits DEBUG] GetAllKits returned null or empty array");
                }

                isInitialized = true;
                PrintWarning("OctaKits успешно инициализирован");
                if (config?.debugMode == true) PrintWarning("[OctaKits DEBUG] InitializeKits completed, isInitialized set to true");
            }
            catch (Exception ex)
            {
                PrintError($"Error initializing OctaKits: {ex.Message}");
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Exception in InitializeKits: {ex.StackTrace}");
                PrintWarning("OctaKits продолжит работу с ограниченным функционалом");
                isInitialized = false; // Разрешаем работу даже при ошибках инициализации
                ScheduleInitRetry();
            }
        }

        private void LoadKitCache(string[] kitNames)
        {
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Starting LoadKitCache for {kitNames.Length} kits");

            foreach (string kitName in kitNames)
            {
                try
                {
                    KitCache cache = new KitCache();

                    // Данные кита теперь локальные — бэкенд внутри этого плагина
                    cache.ImageUrl = GetKitImage(kitName) ?? "";
                    cache.Description = GetKitDescription(kitName) ?? "";
                    cache.MaxUses = GetKitMaxUses(kitName);
                    cache.Cooldown = GetKitCooldown(kitName);

                    cache.HasPermission = true; // Будет проверяться при использовании
                    cache.Price = 0f; // Будет заполняться при необходимости

                    kitCache[kitName] = cache;

                    PrintWarning($"Loaded cache for kit '{kitName}': Image='{cache.ImageUrl}', Uses={cache.MaxUses}, Cooldown={cache.Cooldown}");
                    if (config?.debugMode == true)
                    {
                        PrintWarning($"[OctaKits DEBUG] Kit '{kitName}' - Description: '{cache.Description}', HasPermission: {cache.HasPermission}, Price: {cache.Price}");
                    }
                }
                catch (Exception ex)
                {
                    PrintWarning($"Error loading cache for kit '{kitName}': {ex.Message}");
                    if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Exception loading kit '{kitName}': {ex.StackTrace}");
                }
            }

            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] LoadKitCache completed for {kitNames.Length} kits");
        }

        private void PreloadKitImages(string[] kitNames)
        {
            if (ImageLibrary == null) return;

            foreach (string kitName in kitNames)
            {
                try
                {
                    string imageUrl = kitCache.ContainsKey(kitName) ? kitCache[kitName].ImageUrl : "";
                    if (!string.IsNullOrEmpty(imageUrl))
                    {
                        // Проверяем, загружено ли изображение
                        bool hasImage = (bool)ImageLibrary.Call("HasImage", imageUrl);
                        if (!hasImage)
                        {
                            ImageLibrary.Call("AddImage", imageUrl, imageUrl);
                        }
                    }
                }
                catch (Exception ex)
                {
                    PrintWarning($"Error preloading image for kit '{kitName}': {ex.Message}");
                }
            }

            PrintWarning("OctaKits image preloading completed");
        }

        private string Img(string url)
        {
            if (ImageLibrary != null) 
            {   
                if (!(bool) ImageLibrary.Call("HasImage", url))
                    return url;
                else
                    return (string) ImageLibrary?.Call("GetImage", url);
            }
            else return url;
        }

        private void SetImageList (string imagedata)
        {   
            if (ImageLibrary == null) 
            {
                Puts("ImageLibrary не найден!");
                return;
            }

            if (kitData != null) 
            {
                foreach (string kit in GetAllKits())
                {   
                    string url = GetKitImage(kit);
                    bool saved = (bool) ImageLibrary.Call("HasImage", url);
                    if (!saved)
                        ImageLibrary.Call("AddImage", url, url);
                }
                Puts("Загрузка изображений наборов...");
            }
        }

        private Dictionary<string, string> octakits_colors = new Dictionary<string, string>()
        {   
            //buttons
            {"claim", "0.91 0.72 0.29 0.85"},
            {"cooldown", "0.80 0.25 0.16 0.85"},
            {"noPerms", "0.14 0.14 0.14 0.9"},
            {"noUses", "0.14 0.14 0.14 0.6"},
            {"back", "0 0 0 0.6"},
            //panels
            {"main_panel", "0 0 0 0.5"},
            {"secondary_panel", "0 0 0 0.5"},
            {"inventory", "0 0 0 0.6"}
        };

        //kits import grid
        string[] anchors = {
            "0 1", "0 0.8", "0 0.6", "0 0.4", "0 0.2", 
            "0.33 1", "0.33 0.8", "0.33 0.6", "0.33 0.4", "0.33 0.2",
            "0.66 1", "0.66 0.8", "0.66 0.6", "0.66 0.4", "0.66 0.2",
        };

        private string[] anchors2 = {

            // Верхний ряд
            "0.01 0.70-0.32 0.93",   // Кит 1 (верхний левый)
            "0.34 0.70-0.65 0.93",   // Кит 2 (верхний средний)
            "0.67 0.70-0.98 0.93",   // Кит 3 (верхний правый)

            // Средний ряд
            "0.01 0.375-0.32 0.605",   // Кит 4 (средний левый)
            "0.34 0.375-0.65 0.605",   // Кит 5 (средний средний)
            "0.67 0.375-0.98 0.605",   // Кит 6 (средний правый)

            // Нижний ряд
            "0.01 0.05-0.32 0.28",   // Кит 7 (нижний левый)
            "0.34 0.05-0.65 0.28",   // Кит 8 (нижний средний)
            "0.67 0.05-0.98 0.28",   // Кит 9 (нижний правый)
        };

        string[] mainA = {
    
            "0.01 0.75-0.165 0.98", "0.175 0.75-0.330 0.98", "0.340 0.75-0.495 0.98", "0.505 0.75-0.660 0.98", "0.670 0.75-0.825 0.98", "0.835 0.75-0.99 0.98",
            "0.01 0.505-0.165 0.735", "0.175 0.505-0.330 0.735", "0.340 0.505-0.495 0.735", "0.505 0.505-0.660 0.735", "0.670 0.505-0.825 0.735", "0.835 0.505-0.99 0.735",
            "0.01 0.265-0.165 0.495", "0.175 0.265-0.330 0.495", "0.340 0.265-0.495 0.495", "0.505 0.265-0.660 0.495", "0.670 0.265-0.825 0.495", "0.835 0.265-0.99 0.495",
            "0.01 0.02-0.165 0.250", "0.175 0.02-0.330 0.250", "0.340 0.02-0.495 0.250", "0.505 0.02-0.660 0.250", "0.670 0.02-0.825 0.250", "0.835 0.02-0.99 0.250",
        };

        string[] clothA = {
    
            "0.01 0.04-0.14 0.96", "0.15 0.04-0.28 0.96", "0.29 0.04-0.42 0.96", "0.43 0.04-0.56 0.96", "0.57 0.04-0.70 0.96", "0.71 0.04-0.845 0.96", "0.855 0.04-0.99 0.96"
        };

        string[] beltA = {
    
            "0.01 0.04-0.165 0.94", "0.175 0.04-0.330 0.94", "0.340 0.04-0.495 0.94", "0.505 0.04-0.660 0.94", "0.670 0.04-0.825 0.94", "0.835 0.04-0.99 0.94",
        };


        // Список наборов, которые можно показывать игроку (скрытые и auth-only отфильтровываем)
        private List<string> GetVisibleKits(BasePlayer player)
        {
            var list = new List<string>();
            if (kitData == null) return list;

            string[] all = GetAllKits();

            bool isAdmin = IsKitsAdmin(player);
            foreach (string name in all)
            {
                JObject kit = GetKitObject(name);
                if (kit == null) continue;

                if (!isAdmin && kit.SelectToken("IsHidden")?.Value<bool>() == true) continue;
                int reqAuth = kit.SelectToken("RequiredAuth")?.Value<int>() ?? 0;
                if (player.net.connection.authLevel < reqAuth) continue;

                list.Add(name);
            }
            return list;
        }

        // Самостоятельное окно OctaKits: корневой оверлей + стеклянная панель с шапкой
        private void CreateStandaloneRoot(ref CuiElementContainer ui, string title = "◆ <b>НАБОРЫ</b>", string subtitle = "выбери набор — обзор покажет содержимое", string closeCmd = "octakits_close", bool keyboard = false)
        {
            CUIClass.CreatePanel(ref ui, "octakits_root", "Overlay", "0 0 0 0.85", "0 0", "1 1", true, 0f, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreatePanel(ref ui, "octakits_content", "octakits_root", GlassBg, "0.18 0.08", "0.82 0.94", false, 0.2f, 0f, "assets/icons/iconmaterial.mat", "", "", keyboard);
            // золотая шапка
            CUIClass.CreatePanel(ref ui, "octakits_header", "octakits_content", "0 0 0 0", "0 0.95", "1 1.0", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "octakits_headline", "octakits_content", Gold, "0 0.945", "1 0.948", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreateText(ref ui, "octakits_title", "octakits_header", GoldBright, title, 16, "0.02 0.1", "0.5 0.9", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf");
            CUIClass.CreateText(ref ui, "octakits_sub", "octakits_header", TMuted, subtitle, 10, "0.5 0.1", "0.76 0.9", TextAnchor.MiddleRight, "robotocondensed-regular.ttf");
            CUIClass.CreateButton(ref ui, "octakits_close", "octakits_header", CRed, "<b>✕</b>", 13, "0.945 0.15", "0.99 0.85", closeCmd, "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
        }

        private void ShowKits(BasePlayer player, int page = 0)
        {
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] ShowKits called for player {player.displayName} (ID: {player.UserIDString}), page: {page}");

            // Ленивая инициализация: если кэш ещё не готов — пробуем собрать прямо сейчас
            if (!isInitialized)
                InitializeKits();

            if (kitData == null)
            {
                PrintWarning("Данные наборов недоступны");
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] ShowKits called but kitData is null");
                player.ChatMessage("<color=#E8B84B>OctaKits</color>: система наборов сейчас недоступна");
                return;
            }

            var ui =  new CuiElementContainer();
            CreateStandaloneRoot(ref ui);

            // Админам — кнопка создания набора в шапке (открывает наш редактор)
            bool isAdmin = IsKitsAdmin(player);
            if (isAdmin)
                CUIClass.CreateButton(ref ui, "octakits_adm_create", "octakits_header", GlassRow, "<color=#FFD35C><b>+ НОВЫЙ</b></color>", 11, "0.13 0.15", "0.245 0.85", "octakits_admin_create", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");

            var kitList = GetVisibleKits(player);
            if (config?.debugMode == true)
            {
                PrintWarning($"[OctaKits DEBUG] Visible kits for {player.displayName}: {kitList.Count}");
            }

            if (kitList.Count == 0)
            {
                CUIClass.CreateText(ref ui, "errortext", "octakits_content", "1 1 1 0.6", "На сервере пока нет доступных наборов.", 18, "0.0 0.0", "1 0.94", TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", 0.0f);
                CuiHelper.DestroyUi(player, "octakits_root");
                CuiHelper.AddUi(player, ui);
                return;
            }

            int startingIndex = page * 9;
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Showing page {page}, starting index {startingIndex}, total kits to show: 9");

            for (var i = 0; i < 9; i++)
            {
                if (startingIndex + i >= kitList.Count) break;

                string kitname = kitList[startingIndex + i];
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Processing kit {i}: '{kitname}'");

                // Проверяем кит из кеша или загружаем данные
                if (!kitCache.TryGetValue(kitname, out KitCache kitInfo))
                {
                    if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit '{kitname}' not in cache, loading dynamically");
                    // Если кита нет в кеше, пытаемся загрузить данные
                    try
                    {
                        kitInfo = new KitCache();

                        // Данные кита — локальный бэкенд
                        kitInfo.ImageUrl = GetKitImage(kitname) ?? "";
                        kitInfo.Description = GetKitDescription(kitname) ?? "";
                        kitInfo.MaxUses = GetKitMaxUses(kitname);
                        kitInfo.Cooldown = GetKitCooldown(kitname);

                        kitInfo.HasPermission = true; // Будет проверяться при использовании
                        kitInfo.Price = 0f;

                        kitCache[kitname] = kitInfo;

                        PrintWarning($"Dynamic loaded kit '{kitname}' into cache");
                        if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Dynamically loaded kit '{kitname}' into cache");
                    }
                    catch (Exception ex)
                    {
                        PrintWarning($"Error loading kit data for '{kitname}': {ex.Message}");
                        if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Exception loading kit '{kitname}': {ex.StackTrace}");
                        continue;
                    }
                }
                else
                {
                    if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit '{kitname}' found in cache");
                    // Обновляем только динамические данные
                    try
                    {
                        var ko = GetKitObject(kitname);
                        var reqPerm = ko?.SelectToken("RequiredPermission")?.ToString() ?? "";
                        kitInfo.HasPermission = HasKitPermission(player, reqPerm);
                    }
                    catch (Exception ex)
                    {
                        PrintWarning($"Error updating permissions for kit '{kitname}': {ex.Message}");
                        kitInfo.HasPermission = false;
                    }
                }

                // Проверка существования набора — локальный бэкенд
                bool isKitExists = IsKit(kitname);

                if (!isKitExists)
                {
                    if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit '{kitname}' does not exist");
                    CUIClass.CreateText(ref ui, $"kitpanel_{i}", "octakits_content", "1 1 1 0.6", $"Набор <b>'{kitname}'</b> не существует.", 10, "0.0 0.00", "1 1", TextAnchor.MiddleCenter, $"robotocondensed-regular.ttf", 0.0f);
                    continue;
                }

                string kitDescr = kitInfo.Description;
                if (kitDescr.Length > 60) kitDescr = kitDescr.Substring(0, 60) + "...";

                string usesleft = gl("uses_title_unlimited");
                if (kitInfo.MaxUses != 0)
                {
                    int usedCount = GetPlayerKitUses((ulong)player.userID, kitname);
                    usesleft = Math.Max(0, kitInfo.MaxUses - usedCount).ToString();
                }

                string cooldown = gl("cooldown_title_none");
                if (kitInfo.Cooldown != 0)
                    cooldown = $"{TimeFormated(kitInfo.Cooldown)}";

                string[] _anchors = anchors2[i].Split('-');
                if (_anchors.Length < 2)
                {
                    PrintWarning($"Invalid anchor format for kit {i}: {anchors2[i]}");
                    continue;
                }

                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Creating UI for kit '{kitname}' - Uses: {usesleft}, Cooldown: {cooldown}, HasPermission: {kitInfo.HasPermission}");

                CUIClass.CreatePanel(ref ui, $"kitpanel_{i}", "octakits_content", config?.ui?.colors?["Дополнительная панель"] ?? GlassBg, _anchors[0], _anchors[1], false, 0.0f, 0f, "assets/icons/iconmaterial.mat", "0 0", "0 0");
                    // Верхняя золотая полоска карточки
                    CUIClass.CreatePanel(ref ui, $"kit_goldline_{i}", $"kitpanel_{i}", Gold, "0 0.97", "1 1.0", false, 0f, 0f, "assets/icons/iconmaterial.mat");

                    // Иконка — жёсткий квадрат 96px в левом верхнем углу (анкоры+оффсеты: не тянется ни по одной оси)
                    CUIClass.CreatePanel(ref ui, $"kit_icon_panel_{i}", $"kitpanel_{i}", config?.ui?.colors?["Основная панель"] ?? GlassRow, "0.035 0.94", "0.035 0.94", false, 0.0f, 0f, "assets/icons/iconmaterial.mat", "0 -96", "96 0");
                        string imageUrl = Img(kitInfo?.ImageUrl ?? "");
                        if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit '{kitname}' image URL: '{kitInfo?.ImageUrl ?? "NULL"}'");
                        if (!string.IsNullOrEmpty(imageUrl))
                            CUIClass.CreateImage(ref ui, "img", $"kit_icon_panel_{i}", imageUrl, "0.07 0.07", "0.93 0.93", 0.2f);

                    // Админ-кнопки — квадраты 26px у правого верхнего угла (панель задаёт px-геометрию, кнопка внутри 0..1)
                    if (isAdmin)
                    {
                        CUIClass.CreatePanel(ref ui, $"kit_editp_{i}", $"kitpanel_{i}", "0 0 0 0", "1 0.94", "1 0.94", false, 0f, 0f, "assets/icons/iconmaterial.mat", "-58 -28", "-32 0");
                        CUIClass.CreateButton(ref ui, $"kit_editbtn_{i}", $"kit_editp_{i}", GlassRow, "", 10, "0 0", "1 1", $"octakits_admin_edit {kitname.Replace(" ", "%")}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                        CUIClass.PullFromAssets(ref ui, $"kit_editicon_{i}", $"kit_editbtn_{i}", GoldBright, "assets/icons/gear.png", 0f, 0f, "0.20 0.20", "0.80 0.80");

                        CUIClass.CreatePanel(ref ui, $"kit_delp_{i}", $"kitpanel_{i}", "0 0 0 0", "1 0.94", "1 0.94", false, 0f, 0f, "assets/icons/iconmaterial.mat", "-30 -28", "-4 0");
                        CUIClass.CreateButton(ref ui, $"kit_delbtn_{i}", $"kit_delp_{i}", CRed, "", 10, "0 0", "1 1", $"octakits_admin_delete {kitname.Replace(" ", "%")}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                        CUIClass.PullFromAssets(ref ui, $"kit_delicon_{i}", $"kit_delbtn_{i}", "1 1 1 0.9", "assets/icons/trash.png", 0f, 0f, "0.22 0.22", "0.78 0.78");
                    }

                    // Правая колонка текста: левый край задан в px (108px) — гарантированно правее 96px иконки
                    // title
                    CUIClass.CreatePanel(ref ui, $"kit_tp_{i}", $"kitpanel_{i}", "0 0 0 0", "0 0.68", "0.76 0.92", false, 0f, 0f, "assets/icons/iconmaterial.mat", "108 0", "0 0");
                        CUIClass.CreateText(ref ui, "kitname", $"kit_tp_{i}", TWhite, $"<color=#FFD35C><b>{kitname.ToUpper()}</b></color>", 13, "0 0", "1 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", 0.0f);
                    // description
                    CUIClass.CreatePanel(ref ui, $"kit_dp_{i}", $"kitpanel_{i}", "0 0 0 0", "0 0.44", "0.985 0.66", false, 0f, 0f, "assets/icons/iconmaterial.mat", "108 0", "0 0");
                        CUIClass.CreateText(ref ui, "kitdesc", $"kit_dp_{i}", TWhite, $"<size=10><color=#9E9482>{kitDescr}</color></size>", 10, "0 0", "1 1", TextAnchor.UpperLeft, "robotocondensed-regular.ttf", 0.0f);
                    // meta: кулдаун / лимит
                    CUIClass.CreatePanel(ref ui, $"kit_mp_{i}", $"kitpanel_{i}", "0 0 0 0", "0 0.27", "0.985 0.42", false, 0f, 0f, "assets/icons/iconmaterial.mat", "108 0", "0 0");
                        CUIClass.CreateText(ref ui, "kitmeta", $"kit_mp_{i}", TWhite,
                            $"<size=10><color=#8E8777>КД:</color> <color=#FFD35C>{cooldown}</color>   <color=#8E8777>Лимит:</color> <color=#FFD35C>{usesleft}</color></size>",
                            10, "0 0", "1 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", 0.0f);

                    // Нижний ряд кнопок: ОБЗОР под иконкой, ПОЛУЧИТЬ — на оставшуюся ширину
                    CUIClass.CreateButton(ref ui, $"kit_viewbtn_{i}", $"kitpanel_{i}", GlassHov, "<color=#FFD35C>ОБЗОР</color>", 11, "0.035 0.05", "0.29 0.24", $"octakits_viewkit {kitname.Replace(" ", "%")} {page}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");

                    string[] btn_props = Button_Cooldown_Text(player, kitname).Split('%');
                    if (btn_props.Length >= 2)
                    {
                        CUIClass.CreateButton(ref ui, $"kit_claimbtn_{i}", $"kitpanel_{i}", btn_props[1], $"<b>{btn_props[0]}</b>", 11, "0.31 0.05", "0.985 0.24", $"octakits_try_claim {kitname.Replace(" ", "%")} 0 {i}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                    }
                    else
                    {
                        PrintWarning($"Invalid button properties for kit '{kitname}': {Button_Cooldown_Text(player, kitname)}");
                    }
                }
            
            // Сносим всё старое окно целиком (корень уносит и карточки, и детальный просмотр)
            CuiHelper.DestroyUi(player, "octakits_root");

            // Пагинация — в шапке, не перекрывает карточки
            int totalPages = (int)Math.Ceiling(kitList.Count / 9.0);
            if (totalPages > 1)
            {
                if (page > 0)
                    CUIClass.CreateButton(ref ui, "octakits_pg_back", "octakits_header", GlassRow, "<size=14><color=#FFD35C>‹</color></size>", 12, "0.775 0.15", "0.825 0.85", $"octakits_page {page - 1}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                CUIClass.CreateText(ref ui, "octakits_pg_label", "octakits_header", TMuted, $"{page + 1}/{totalPages}", 11, "0.825 0.15", "0.885 0.85", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                if (page + 1 < totalPages)
                    CUIClass.CreateButton(ref ui, "octakits_pg_next", "octakits_header", GlassRow, "<size=14><color=#FFD35C>›</color></size>", 12, "0.885 0.15", "0.935 0.85", $"octakits_page {page + 1}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
            }

            CuiHelper.AddUi(player, ui);
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] ShowKits completed for player {player.displayName}, page {page}");

            // Запускаем таймер для обновления кнопок каждые 5 секунд
            if (config?.ui?.updateCooldownButtons == true)
            {
                if (_cdTimers.TryGetValue(player.userID, out var _old) && _old != null) _old.Destroy();
                _cdTimers[player.userID] = timer.Repeat(5f, 0, () =>
                {
                    if (player == null || !player.IsConnected)
                    {
                        if (_cdTimers.TryGetValue(player.userID, out var _t) && _t != null) { _t.Destroy(); _cdTimers.Remove(player.userID); }
                        return;
                    }
                    UpdateCooldownButtons(player, kitList, page);
                });
            }
        }

        private void UpdateCooldownButtons(BasePlayer player, List<string> kitList, int page)
        {
            if (player == null || !player.IsConnected) return;

            // Проверяем, показан ли интерфейс OctaKits игроку
            if (!player.IsConnected) return;

            int startingIndex = page * 9;
            var ui = new CuiElementContainer();

            for (var i = 0; i < 9; i++)
            {
                if (startingIndex + i >= kitList.Count) break;

                string kitname = kitList[startingIndex + i];

                string[] btn_props = Button_Cooldown_Text(player, kitname).Split('%');
                if (btn_props.Length >= 2)
                {
                    // Обновляем кнопку получения кита
                    CUIClass.CreateButton(ref ui, $"kit_claimbtn_{i}", $"kitpanel_{i}", btn_props[1], $"<b>{btn_props[0]}</b>", 11, "0.31 0.05", "0.985 0.24", $"octakits_try_claim {kitname.Replace(" ", "%")} 0 {i}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                    CuiHelper.DestroyUi(player, $"kit_claimbtn_{i}");
                }
            }

            if (ui.Any())
            {
                CuiHelper.AddUi(player, ui);
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Updated cooldown buttons for player {player.displayName}");
            }
        }

        private void ViewKit(BasePlayer player, string kitname, int page = 0)
        {
            var ui =  new CuiElementContainer();
            CreateStandaloneRoot(ref ui);

            JObject kitObj = GetKitObject(kitname);
            if (kitObj == null)
            {
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit object is null for kit '{kitname}'");
                CUIClass.CreateText(ref ui, "errortext", "octakits_content", "1 1 1 0.6", $"Набор '{kitname}' не существует.", 18, "0.0 0.00", "1 0.94", TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", 0.0f);
                CuiHelper.DestroyUi(player, "octakits_root");
                CuiHelper.AddUi(player, ui);
                return;
            }

            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit object loaded successfully for kit '{kitname}'");

            var belt = kitObj.SelectToken("BeltItems");
            var main = kitObj.SelectToken("MainItems");
            var wear = kitObj.SelectToken("WearItems");

            if (config?.debugMode == true)
            {
                PrintWarning($"[OctaKits DEBUG] Kit items loaded - Main: {main?.Count() ?? 0}, Belt: {belt?.Count() ?? 0}, Wear: {wear?.Count() ?? 0}");
            }

            CUIClass.CreatePanel(ref ui, "octakits_viewkit_inventory", "octakits_content", GlassBg, "0.37 0.03", "0.99 0.94", false, 0.0f, 0f, "assets/icons/iconmaterial.mat");
                // Золотая полоска сверху
                CUIClass.CreatePanel(ref ui, "octakits_inv_goldline", "octakits_viewkit_inventory", Gold, "0 0.985", "1 1.0", false, 0f, 0f, "assets/icons/iconmaterial.mat");
                // Заголовок секции: основной инвентарь
                CUIClass.CreateText(ref ui, "octakits_sec_main", "octakits_viewkit_inventory", Gold, "◆ <b>ОСНОВНОЙ ИНВЕНТАРЬ</b>", 11, "0.03 0.93", "0.97 0.97", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf");
                CUIClass.CreatePanel(ref ui, "octakits_div_main", "octakits_viewkit_inventory", GoldDark, "0.03 0.925", "0.97 0.928", false, 0f, 0f, "assets/icons/iconmaterial.mat");

                //main
                CUIClass.CreatePanel(ref ui, $"octakits_viewkit_inventory_main", "octakits_viewkit_inventory", "0 0 0 0.0", "0.02 0.44", "0.98 0.91", false, 0.0f, 0f, "assets/icons/iconmaterial.mat");
                for (int i = 0; i < mainA.Count(); i++)
                {   
                    if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Processing main item {i} for kit '{kitname}'");
                    string[] splitA = mainA[i].Split('-');
                    CUIClass.CreatePanel(ref ui, $"octakits_imain_item{i}", "octakits_viewkit_inventory_main", GlassRow, splitA[0], splitA[1], false, 0.2f, 0f, "assets/icons/iconmaterial.mat"); 
                    
                    try{
                        if (main != null && i < main.Count())
                        {
                            var item = main[i];
                            if (item != null)
                            {
                                string shortname = $"{item.SelectToken("Shortname")}";
                                var iDef = ItemManager.FindItemDefinition(shortname);

                                ui.Add(new CuiElement
                                {
                                    Parent = $"octakits_imain_item{i}",
                                    Name = $"{shortname}_{i}",
                                    Components ={ new CuiImageComponent { ItemId = iDef.itemid, SkinId = (ulong) item.SelectToken("SkinID"), FadeIn = 0.2f}, new CuiRectTransformComponent {AnchorMin = "0.10 0.10", AnchorMax = "0.90 0.90"}},
                                    FadeOut = 0f
                                });
                                CUIClass.CreateText(ref ui, "amount", $"octakits_imain_item{i}", GoldBright, $"x{item.SelectToken("Amount")}", 10, "0.05 0.00", "0.95 0.95", TextAnchor.LowerRight, $"robotocondensed-bold.ttf", 0.0f);  
                            }
                        }
                    
                    }catch (Exception ex){
                        if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Error processing main item {i}: {ex.Message}");
                    }
                }
            
                // Заголовок секции: быстрый доступ
                CUIClass.CreateText(ref ui, "octakits_sec_belt", "octakits_viewkit_inventory", Gold, "◆ <b>БЫСТРЫЙ ДОСТУП</b>", 11, "0.03 0.38", "0.97 0.42", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf");
                CUIClass.CreatePanel(ref ui, "octakits_div_belt", "octakits_viewkit_inventory", GoldDark, "0.03 0.375", "0.97 0.378", false, 0f, 0f, "assets/icons/iconmaterial.mat");

                CUIClass.CreatePanel(ref ui, "octakits_viewkit_inventory_belt", "octakits_viewkit_inventory", "0 0 0 0.0", "0.02 0.24", "0.98 0.36", false, 0.0f, 0f, "assets/icons/iconmaterial.mat");
                for (int i = 0; i < beltA.Count(); i++)
                {   
                    if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Processing belt item {i} for kit '{kitname}'");
                    string[] splitA = beltA[i].Split('-');
                    CUIClass.CreatePanel(ref ui, $"octakits_imain_item{i}", "octakits_viewkit_inventory_belt", GlassRow, splitA[0], splitA[1], false, 0.2f, 0f, "assets/icons/iconmaterial.mat"); 
                    
                    try{
                        if (belt != null && i < belt.Count())
                        {
                            var item = belt[i];
                            if (item != null)
                            {
                                string shortname = $"{item.SelectToken("Shortname")}";
                                var iDef = ItemManager.FindItemDefinition(shortname);

                                ui.Add(new CuiElement
                                {
                                    Parent = $"octakits_imain_item{i}",
                                    Name = $"{shortname}_{i}",
                                    Components ={ new CuiImageComponent { ItemId = iDef.itemid, SkinId = (ulong) item.SelectToken("SkinID"), FadeIn = 0.2f}, new CuiRectTransformComponent {AnchorMin = "0.10 0.10", AnchorMax = "0.90 0.90"}},
                                    FadeOut = 0f
                                });
                                CUIClass.CreateText(ref ui, "amount", $"octakits_imain_item{i}", GoldBright, $"x{item.SelectToken("Amount")}", 10, "0.05 0.00", "0.95 0.95", TextAnchor.LowerRight, $"robotocondensed-bold.ttf", 0.0f);  
                            }
                        }
                    
                    }catch (Exception ex){
                        if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Error processing belt item {i}: {ex.Message}");
                    }
                }

                // Заголовок секции: экипировка
                CUIClass.CreateText(ref ui, "octakits_sec_wear", "octakits_viewkit_inventory", Gold, "◆ <b>ЭКИПИРОВКА</b>", 11, "0.03 0.19", "0.97 0.23", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf");
                CUIClass.CreatePanel(ref ui, "octakits_div_wear", "octakits_viewkit_inventory", GoldDark, "0.03 0.185", "0.97 0.188", false, 0f, 0f, "assets/icons/iconmaterial.mat");

                CUIClass.CreatePanel(ref ui, $"octakits_viewkit_inventory_wear", "octakits_viewkit_inventory", "0 0 0 0.0", "0.02 0.05", "0.98 0.17", false, 0.0f, 0f, "assets/icons/iconmaterial.mat");
                for (int i = 0; i < clothA.Count(); i++)
                {   
                    if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Processing wear item {i} for kit '{kitname}'");
                    string[] splitA = clothA[i].Split('-');
                    CUIClass.CreatePanel(ref ui, $"octakits_imain_item{i}", "octakits_viewkit_inventory_wear", GlassRow, splitA[0], splitA[1], false, 0.2f, 0f, "assets/icons/iconmaterial.mat"); 
                    
                    try{
                        if (wear != null && i < wear.Count())
                        {
                            var item = wear[i];
                            if (item != null)
                            {
                                string shortname = $"{item.SelectToken("Shortname")}";
                                var iDef = ItemManager.FindItemDefinition(shortname);

                                ui.Add(new CuiElement
                                {
                                    Parent = $"octakits_imain_item{i}",
                                    Name = $"{shortname}_{i}",
                                    Components ={ new CuiImageComponent { ItemId = iDef.itemid, SkinId = (ulong) item.SelectToken("SkinID"), FadeIn = 0.2f}, new CuiRectTransformComponent {AnchorMin = "0.10 0.10", AnchorMax = "0.90 0.90"}},
                                    FadeOut = 0f
                                });
                                CUIClass.CreateText(ref ui, "amount", $"octakits_imain_item{i}", GoldBright, $"x{item.SelectToken("Amount")}", 10, "0.05 0.00", "0.95 0.95", TextAnchor.LowerRight, $"robotocondensed-bold.ttf", 0.0f);  
                            }
                        }
                    
                    }catch (Exception ex){
                        if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Error processing wear item {i}: {ex.Message}");
                    }
                }
            
            
            CUIClass.CreatePanel(ref ui, "octakits_viewkit_info", "octakits_content", GlassBg, "0.01 0.03", "0.35 0.94", false, 0.0f, 0f, "assets/icons/iconmaterial.mat");
                // Верхняя золотая полоска
                CUIClass.CreatePanel(ref ui, "octakits_v_goldline", "octakits_viewkit_info", Gold, "0 0.985", "1 1.0", false, 0f, 0f, "assets/icons/iconmaterial.mat");
                CUIClass.CreateText(ref ui, "kit_title", "octakits_viewkit_info", GoldBright, $"◆ <b>{kitname.ToUpper()}</b>", 18, "0.06 0.91", "0.94 0.97", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", 0.0f);  
                CUIClass.CreatePanel(ref ui, "octakits_v_div1", "octakits_viewkit_info", GoldDark, "0.06 0.895", "0.94 0.898", false, 0f, 0f, "assets/icons/iconmaterial.mat");
                
                string cooldown = gl("cooldown_title_none");
                if (GetKitCooldown(kitname) != 0)
                    cooldown = $"{TimeFormated(GetKitCooldown(kitname))}";
                
                string usesleft = gl("uses_title_unlimited");
                if (GetKitMaxUses(kitname) != 0)
                    usesleft = $"{GetKitMaxUses(kitname) - GetPlayerKitUses((ulong)player.userID, kitname)}";
                
                string cost = gl("price_title_free");
                if(Convert.ToInt32(kitObj.SelectToken("Cost")) != 0)
                    cost = $"{kitObj.SelectToken("Cost")} {CurrencyLabel}";

                string permReq = (string)kitObj.SelectToken("RequiredPermission");
                string permStatus = string.IsNullOrEmpty(permReq) ? "<color=#A3E06C>Разрешён</color>" :
                                    (HasKitPermission(player, permReq) ? "<color=#A3E06C>Разрешён</color>" : "<color=#E05A47>Требуется привилегия</color>");

                string infoText = $"<color=#8E8777>• Кулдаун:</color>  <color=#FFD35C>{cooldown}</color>\n" +
                                  $"<color=#8E8777>• Доступно:</color>  <color=#FFD35C>{usesleft}</color>\n" +
                                  $"<color=#8E8777>• Стоимость:</color> <color=#FFD35C>{cost}</color>\n" +
                                  $"<color=#8E8777>• Статус:</color>    {permStatus}";

                CUIClass.CreateText(ref ui, "kit_info_block", "octakits_viewkit_info", TWhite, infoText, 12, "0.06 0.65", "0.94 0.88", TextAnchor.UpperLeft, "robotocondensed-bold.ttf", 0.0f);
                CUIClass.CreatePanel(ref ui, "octakits_v_div2", "octakits_viewkit_info", GoldDark, "0.06 0.635", "0.94 0.638", false, 0f, 0f, "assets/icons/iconmaterial.mat");

                string descText = GetKitDescription(kitname);
                if (string.IsNullOrEmpty(descText)) descText = "Описание набора не указано.";
                CUIClass.CreateText(ref ui, "kit_description", "octakits_viewkit_info", TMuted, descText, 11, "0.06 0.20", "0.94 0.62", TextAnchor.UpperLeft, "robotocondensed-regular.ttf", 0.0f);  
                
                CUIClass.CreateButton(ref ui, "octakits_btn_back", "octakits_viewkit_info", GlassRow, "<color=#FFD35C>← НАЗАД</color>", 11, "0.06 0.06", "0.45 0.16", $"octakits_page {page}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                
                string[] btn_props = Button_Cooldown_Text(player, kitname).Split('%');
                if (btn_props.Length < 2)
                {
                    PrintWarning($"Button_Cooldown_Text returned invalid format for kit '{kitname}' in ViewKit");
                    return;
                }

                CUIClass.CreateButton(ref ui, "octakits_btn_claim", "octakits_viewkit_info", btn_props[1], $"<b>{btn_props[0]}</b>", 11, "0.49 0.06", "0.94 0.16", $"octakits_try_claim {kitname.Replace(" ", "%")} 1 0", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");

            // Админам — кнопки редактирования и удаления в шапке (место пагинации на экране просмотра свободно)
            if (IsKitsAdmin(player))
            {
                CUIClass.CreateButton(ref ui, "octakits_adm_edit", "octakits_header", GlassRow, "", 11, "0.62 0.15", "0.775 0.85", $"octakits_admin_edit {kitname.Replace(" ", "%")}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                CUIClass.PullFromAssets(ref ui, "octakits_adm_edit_ic", "octakits_adm_edit", GoldBright, "assets/icons/gear.png", 0f, 0f, "0.04 0.22", "0.17 0.78");
                CUIClass.CreateText(ref ui, "octakits_adm_edit_tx", "octakits_adm_edit", GoldBright, "<b>ИЗМЕНИТЬ</b>", 11, "0.18 0", "1 1", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

                CUIClass.CreateButton(ref ui, "octakits_adm_del", "octakits_header", CRed, "", 11, "0.785 0.15", "0.845 0.85", $"octakits_admin_delete {kitname.Replace(" ", "%")}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                CUIClass.PullFromAssets(ref ui, "octakits_adm_del_ic", "octakits_adm_del", "1 1 1 0.9", "assets/icons/trash.png", 0f, 0f, "0.24 0.20", "0.76 0.80");
            }

            // Сносим старое окно целиком — ViewKit отрисовал новый корень
            CuiHelper.DestroyUi(player, "octakits_root");
            CuiHelper.AddUi(player, ui);
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] ViewKit completed successfully for kit '{kitname}' by player {player.displayName}");
        }

        private void KitsImportCui(BasePlayer player, int page = 0)
        {   
            string[] allKits = GetAllKits();
            int windowHeight = 0;
            string text = "";
            foreach (var item in allKits)
            {
                windowHeight += 18;
                text += item + "\n";
            }
            var ui =  new CuiElementContainer();


            CUIClass.CreatePanel(ref ui, "octakits_background", "Overlay", "0 0 0 0.8", "0 0", "1 1", true, 0.0f, 0f, "assets/content/ui/uibackgroundblur.mat", "0 0", "0 0");
            CUIClass.PullFromAssets(ref ui, "overlay", "octakits_background", "0 0 0 1", "assets/content/ui/ui.background.transparent.radial.psd", 0.0f, 0f, "0 0", "1 1");
            
            CUIClass.CreatePanel(ref ui, "octakits_main", "octakits_background", "0.11 0.11 0.11 1", "0.5 0.5", "0.5 0.5", false, 0.1f, 0f, "assets/content/ui/uibackgroundblur.mat", $"-300 -170", "300 170");
            CUIClass.CreatePanel(ref ui, "octakits_title", "octakits_main", "0.07 0.07 0.07 1", "0 1", "1 1.1", false, 0.1f, 0f, "assets/content/ui/uibackgroundblur.mat");
                CUIClass.CreateText(ref ui, "titletext", "octakits_title", "1 1 1 0.6", "ИМПОРТ НАБОРОВ", 15, "0.02 0.00", "1 1", TextAnchor.MiddleLeft, $"robotocondensed-bold.ttf", 0.0f);  
                CUIClass.CreateButton(ref ui, "octakits_closeimportbtn", "octakits_title", "0.80 0.25 0.16 0.85", "", 11, "0.945 0.16", $"0.992 0.82", $"octakits_closeImportMenu", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                CUIClass.PullFromAssets(ref ui, "octakits_closeimportbtn_icon", "octakits_closeimportbtn", "1 1 1 0.8", "assets/icons/vote_down.png", 0.1f, 0f, "0 0", "1 1");
            
            
            if (page > 0) 
                CUIClass.CreateButton(ref ui, "octakits_main_btn_back", "octakits_main", "0.11 0.11 0.11 1", "< НАЗАД", 11, "0 -0.1", $"0.15 -0.02", $"octakits_importpage {page - 1}", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
            

            if (page < allKits.Length / 14) 
                CUIClass.CreateButton(ref ui, "octakits_main_btn_back", "octakits_main", "0.11 0.11 0.11 1", "ДАЛЕЕ >", 11, "0.85 -0.1", $"1 -0.02", $"octakits_importpage {page + 1}", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
            

            CuiHelper.DestroyUi(player, "octakits_background"); 
            
            CuiHelper.AddUi(player, ui); 

            int startingindex = page * 14;
            
            for (int i = 0; i < 15; i++)
            {   
                if (startingindex + i > allKits.Length - 1) break;

                Kit_FI(player, allKits[startingindex + i], i, addonsData["addons"].importedKits.Contains(allKits[startingindex + i]));
            }
        }

        private void Kit_FI(BasePlayer player, string kitname, int index, bool alreadyImported = false)
        {   
            var ui2 =  new CuiElementContainer();

            string topOffset = "0";
            string leftOffset = "0";
            string btnColor = "0.91 0.72 0.29 0.85";
            string btnText = "ИМПОРТ";

            if (index == 0 || index == 5 || index == 10) 
                topOffset = "-3";

            if (index < 5) 
                leftOffset = "3";

            if (alreadyImported)
            {
                btnColor = "0.80 0.25 0.16 0.85";
                btnText = "<size=10>ИМПОРТИРОВАНО</size>";
            }
            
            CUIClass.CreatePanel(ref ui2, $"octakits_kit_FI{index}", "octakits_main", "0 0 0 0.8", anchors[index], anchors[index], false, 0.0f, 0f, "assets/icons/iconmaterial.mat", $"{leftOffset} -65", $"195 {topOffset}");
                CUIClass.CreateImage(ref ui2, "img", $"octakits_kit_FI{index}", $"{Img(GetKitImage(kitname))}", "0.02 0.12", "0.27 0.95", 0.5f);
                CUIClass.CreateText(ref ui2, "kitname", $"octakits_kit_FI{index}", "1 1 1 0.6", kitname, 15, "0.30 0.5", "1 1", TextAnchor.MiddleLeft, $"robotocondensed-bold.ttf", 0.0f);  
                CUIClass.CreateButton(ref ui2, $"octakits_btnimport_{index}", $"octakits_kit_FI{index}", btnColor, btnText, 11, "0.29 0.08", "0.775 0.45", $"octakits_import {kitname.Replace(" ", "%")} {index}", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                CUIClass.CreateButton(ref ui2, $"octakits_btn_viewkit", $"octakits_kit_FI{index}", "0.11 0.11 0.11 1", "", 11, "0.80 0.08", "0.97 0.45", $"octakits_lookupkit {kitname.Replace(" ", "%")}", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                    CUIClass.CreateImage(ref ui2, "img", "octakits_btn_viewkit", Img("https://rustplugins.net/products/welcomepanel/new/file-find.png"), "0.20 0.20", "0.80 0.80", 0.0f);

            CuiHelper.DestroyUi(player, $"octakits_kit_FI{index}");
            CuiHelper.AddUi(player, ui2); 
        }

        private void PlayFx(BasePlayer player, string fx = "assets/prefabs/deployable/locker/sound/equip_zipper.prefab")
        {
            if (player == null) return;
            if (fx == null) return;

            var EffectInstance = new Effect();
            EffectInstance.Init(Effect.Type.Generic, player, 0, Vector3.up, Vector3.zero);
            EffectInstance.pooledstringid = StringPool.Get(fx);
            NetWrite netWrite = Net.sv.StartWrite();
			netWrite.PacketID(Message.Type.Effect);
			EffectInstance.WriteToStream(netWrite);
			netWrite.Send(new SendInfo(player.net.connection));
			EffectInstance.Clear();
        }

        private string TimeFormated(double cooldown)
        {
            var ts = TimeSpan.FromSeconds(cooldown);
            if (ts.TotalDays >= 1) return $"{ts.Days} ДН {ts.Hours} Ч";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} Ч {ts.Minutes} МИН";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes} МИН {ts.Seconds} СЕК";
            return $"{ts.Seconds} СЕК";
        }

        private string Button_Cooldown_Text(BasePlayer player, string kitname)
        {
            try
            {
                var kit = GetKitObject(kitname);
                if (kit == null)
                {
                    PrintWarning($"Failed to get kit object for '{kitname}'");
                    return gl("no_perms_button") + $"%{config?.ui?.colors?["Кнопка без доступа"] ?? "0.14 0.14 0.14 0.9"}";
                }

                object costToken = kit.SelectToken("Cost");
                int cost = costToken != null ? Convert.ToInt32(costToken) : 0;

                object permToken = kit.SelectToken("RequiredPermission");
                string perm = permToken as string ?? "";

                int kitUseLimit = GetKitMaxUses(kitname);
                int kitTimesUsed = GetPlayerKitUses((ulong)player.userID, kitname);

                if (IsKitOnCd(player, kitname))
                {
                    return gl("on_cooldown_button").Replace("{kit_cooldown}", $"{GetCurrentKitCd(player, kitname)}") + $"%{config?.ui?.colors?["Кнопка перезарядки"] ?? CRed}";
                }
                else
                {
                    if (!HasKitPermission(player, perm))
                        return gl("no_perms_button") + $"%{config?.ui?.colors?["Кнопка без доступа"] ?? GlassLt}";

                    if (kitUseLimit != 0)
                    {
                        if (kitUseLimit - kitTimesUsed < 1)
                            return gl("no_uses_button") + $"%{config?.ui?.colors?["Кнопка без использований"] ?? GlassLt}";
                    }

                    if (cost != 0)
                        return $"{cost} {CurrencyLabel}" + $"%{config?.ui?.colors?["Кнопка забрать"] ?? CGreen}";
                    else
                        return gl("claim_button") + $"%{config?.ui?.colors?["Кнопка забрать"] ?? CGreen}";
                }
            }
            catch (Exception ex)
            {
                PrintWarning($"Error in Button_Cooldown_Text for kit '{kitname}': {ex.Message}");
                return gl("no_perms_button") + $"%{config?.ui?.colors?["Кнопка без доступа"] ?? "0.14 0.14 0.14 0.9"}";
            }
        }

        private string KitUsesLeft(BasePlayer player, string _kitName) 
        {   
            int _kitMaxUses = GetKitMaxUses(_kitName);
            int _kitTimesUsed = GetPlayerKitUses((ulong)player.userID, _kitName);
            string _kitUsesLeft = Convert.ToString(_kitMaxUses - _kitTimesUsed);
            return _kitUsesLeft;
        }

        private bool IsKitOnCd(BasePlayer player, string _kitName) 
        {
            return GetPlayerKitCooldown((ulong)player.userID, _kitName) > 0;
        }

        private string GetKitCd(string _kitName) 
        {
            int _kitCdInt = GetKitCooldown(_kitName); 
        
            TimeSpan cooldownTS = TimeSpan.FromSeconds(_kitCdInt); 
            string cooldownFormated = string.Format("{0:D1}D {1:D2}:{2:D2}:{3:D2}", cooldownTS.Days, cooldownTS.Hours, cooldownTS.Minutes, cooldownTS.Seconds);
            if (_kitCdInt < 86400) cooldownFormated = string.Format("{0:D2}:{1:D2}:{2:D2}", cooldownTS.Hours, cooldownTS.Minutes, cooldownTS.Seconds);
            
            string _kitCd = $"{cooldownFormated}";
            return _kitCd; 
        }

        private string GetCurrentKitCd(BasePlayer player, string _kitName) 
        { 
            int _currentCdInt = Convert.ToInt32(GetPlayerKitCooldown((ulong)player.userID, _kitName));
            
            TimeSpan cooldownTS = TimeSpan.FromSeconds(_currentCdInt); 
            string cooldownFormated = string.Format("{0:D1}D {1:D2}:{2:D2}:{3:D2}", cooldownTS.Days, cooldownTS.Hours, cooldownTS.Minutes, cooldownTS.Seconds);
            if (_currentCdInt < 86400) cooldownFormated = string.Format("{0:D2}:{1:D2}:{2:D2}", cooldownTS.Hours, cooldownTS.Minutes, cooldownTS.Seconds);
                        
            string _currentCd = $"{cooldownFormated}";
            return _currentCd; 
        }

        [ConsoleCommand("octakits_try_claim")]
        private void octakits_try_claim(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;

            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (player == null) return;
            if (args == null || args.Length != 3) return;

            if (string.IsNullOrEmpty(args[0])) return;

            string kitname = args[0].Replace("%", " ");

            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] octakits_try_claim called for kit '{kitname}' by player {player.displayName}");

            bool claimResult = TryClaimKit(player, kitname, false);
            if (claimResult)
            {   
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit '{kitname}' successfully claimed by {player.displayName}");

                string[] btn_props = Button_Cooldown_Text(player, kitname).Split('%');
                if (btn_props.Length < 2)
                {
                    PrintWarning($"Button_Cooldown_Text returned invalid format for kit '{kitname}'");
                    return;
                }

                var ui =  new CuiElementContainer();

                if (args[1] == "0") {
                    CUIClass.CreateButton(ref ui, $"kit_claimbtn_{args[2]}", $"kitpanel_{args[2]}",  btn_props[1], $"<b>{btn_props[0]}</b>", 11, "0.31 0.05", $"0.985 0.24", $"octakits_try_claim {kitname.Replace(" ", "%")} 0 {args[2]}", "", TWhite, 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                    CuiHelper.DestroyUi(player, $"kit_claimbtn_{args[2]}");
                }
                else {
                    CUIClass.CreateButton(ref ui, "octakits_btn_claim", $"octakits_viewkit_info", btn_props[1],  $"<b>{btn_props[0]}</b>", 11, "0.49 0.06", "0.94 0.16", $"octakits_try_claim {kitname.Replace(" ", "%")} 1 0", "", TWhite, 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                    CuiHelper.DestroyUi(player, "octakits_btn_claim");
                }
                CuiHelper.AddUi(player, ui); 

                if (config?.ui?.closeAfterClaim == true) {
                    CuiHelper.DestroyUi(player, "octakits_root");
                }

                /* if (Notifications != null)
                    Notifications.Call("Run",
                        player,
                        5,
                        gl("notification_kitclaim_success").Replace("{kitname}", kitname),
                        gl("notification_success_stripe_color"),
                        Img(GetKitImage(kitname)),
                        true
                    ); */

                PlayFx(player, "assets/prefabs/deployable/locker/sound/equip_zipper.prefab");
                return;
            }

            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit '{kitname}' claim failed for player {player.displayName}");
            PlayFx(player, "assets/bundled/prefabs/fx/notice/item.select.fx.prefab");
        }

        [ConsoleCommand("octakits_viewkit")]
        private void octakits_viewkit(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (player == null) return;
            if (args == null || args.Length != 2) return;

            if (string.IsNullOrEmpty(args[0])) return;

            string kitname = args[0].Replace("%", " ");
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] octakits_viewkit called for kit '{kitname}' by player {player.displayName}, page: {args[1]}");
            ViewKit(player, kitname, Convert.ToInt32(args[1]));
        }

        [ConsoleCommand("octakits_page")]
        private void octakits_page(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (player == null) return;
            if (args == null || args.Length != 1) {
                Puts("Ошибка команды импорта: неверные аргументы");
                return;
            }

            int pg = 0;
            int.TryParse(args[0], out pg);
            pg = Math.Max(0, pg);
            CuiHelper.DestroyUi(player, "octakits_viewkit_inventory");
            CuiHelper.DestroyUi(player, "octakits_viewkit_info");
            ShowKits(player, pg);

        }

        [ConsoleCommand("octakits_importpage")]
        private void octakits_importpage(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (player == null) return;
            if (!player.IsAdmin) return;
            if (args == null || args.Length != 1) {
                Puts("Ошибка команды импорта: неверные аргументы");
                return;
            }

            if (kitData == null)
            {
                Puts("Данные наборов не загружены");
                return;
            }

            try
            {
                KitsImportCui(player, Convert.ToInt32(args[0]));
            }
            catch (Exception ex)
            {
                PrintError($"Error in octakits_importpage: {ex.Message}");
            }
        }

        [ConsoleCommand("octakits_lookupkit")]
        private void octakits_lookupkit(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (player == null) return;
            if (!player.IsAdmin) return;
            if (args == null || args.Length != 1) {
                Puts("Ошибка команды импорта: неверные аргументы");
                return;
            }

            if (string.IsNullOrEmpty(args[0])) {
                Puts("Ошибка просмотра: имя набора пустое");
                return;
            }

            string kitname = args[0].Replace("%", " ");
            CuiHelper.DestroyUi(player, "octakits_background");
            StartKitEditor(player, kitname);

        }

        [ConsoleCommand("octakits_closeImportMenu")]
        private void octakits_closeImportMenu(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, "octakits_background"); 

        }

        [ConsoleCommand("octakits_import")]
        private void octakits_import(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (player == null) return;
            if (!player.IsAdmin) return;
            if (args == null || args.Length != 2) {
                Puts("Ошибка команды импорта: неверные аргументы");
                return;
            }

            if (string.IsNullOrEmpty(args[0])) {
                Puts("Ошибка импорта: имя набора пустое");
                return;
            }

            string kitname = args[0].Replace("%", " ");

            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] octakits_import called for kit '{kitname}' by admin {player.displayName}");

            // Проверка и инициализация addonsData["addons"]
            if (!addonsData.ContainsKey("addons"))
            {
                addonsData.Add("addons", new AddonsData());
            }

            if (addonsData["addons"].importedKits == null)
            {
                addonsData["addons"].importedKits = new List<string>();
                Puts("Список importedKits инициализирован.");
            }

            if (addonsData["addons"].importedKits.Contains(kitname)) {
                Puts($"Набор {kitname} убран из списка импорта.");
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Removing kit '{kitname}' from imported list");
                addonsData["addons"].importedKits.Remove(kitname);
                Kit_FI(player, kitname, Convert.ToInt32(args[1]), false);

            }
            else
            {
                Puts($"Набор {kitname} добавлен в данные аддона.");
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Adding kit '{kitname}' to imported list");
                addonsData["addons"].importedKits.Add(kitname);
                Kit_FI(player, kitname, Convert.ToInt32(args[1]), true);
            }
            SaveData();
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Data saved, imported kits count: {addonsData["addons"].importedKits.Count}");
            Puts("Готово, данные сохранены. Перезагрузите плагин, чтобы применить изменения.");

        }

        [ChatCommand("octa_importkits")]
        private void octa_importkits(BasePlayer player)
        {
            if (player == null) return;
            if (!player.IsAdmin) return;
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] octa_importkits chat command called by admin {player.displayName}");
            KitsImportCui(player);
        }

        public class CUIClass
        {
            public static CuiElementContainer CreateOverlay(string _name, string _color, string _anchorMin, string _anchorMax, bool _cursorOn = false, float _fade = 0f, string _mat = "")
            {
                var _element = new CuiElementContainer()
                {
                    {
                        new CuiPanel
                        {
                            Image = { Color = _color, Material = _mat, FadeIn = _fade},
                            RectTransform = { AnchorMin = _anchorMin, AnchorMax = _anchorMax },
                            CursorEnabled = _cursorOn
                        },
                        new CuiElement().Parent = "Overlay",
                        _name
                    }
                };
                return _element;
            }

            public static void CreatePanel(ref CuiElementContainer _container, string _name, string _parent, string _color, string _anchorMin, string _anchorMax, bool _cursorOn = false, float _fadeIn = 0f, float _fadeOut = 0f, string _mat2 = "", string _OffsetMin = "", string _OffsetMax = "", bool keyboard = false)
            {
                _container.Add(new CuiPanel
                {
                    Image = { Color = _color, Material = _mat2, FadeIn = _fadeIn },
                    RectTransform = { AnchorMin = _anchorMin, AnchorMax = _anchorMax, OffsetMin = _OffsetMin, OffsetMax = _OffsetMax },
                    FadeOut = _fadeOut,
                    CursorEnabled = _cursorOn,
                    KeyboardEnabled = keyboard
                },
                _parent,
                _name);
            }

            public static void CreateImage(ref CuiElementContainer _container, string _name, string _parent, string _image, string _anchorMin, string _anchorMax, float _fadeIn = 0f, float _fadeOut = 0f, string _OffsetMin = "", string _OffsetMax = "")
            {
                if (_image.StartsWith("http") || _image.StartsWith("www"))
                {
                    _container.Add(new CuiElement
                    {
                        Name = _name,
                        Parent = _parent,
                        FadeOut = _fadeOut,
                        Components =
                        {
                            new CuiRawImageComponent { Url = _image, Sprite = "assets/content/textures/generic/fulltransparent.tga", FadeIn = _fadeIn},
                            new CuiRectTransformComponent { AnchorMin = _anchorMin, AnchorMax = _anchorMax, OffsetMin = _OffsetMin, OffsetMax = _OffsetMax }
                        }

                    });
                }
                else
                {
                    _container.Add(new CuiElement
                    {
                        Parent = _parent,
                        Components =
                        {
                            new CuiRawImageComponent { Png = _image, Sprite = "assets/content/textures/generic/fulltransparent.tga", FadeIn = _fadeIn},
                            new CuiRectTransformComponent { AnchorMin = _anchorMin, AnchorMax = _anchorMax }
                        }
                    });
                }
            }

            public static void PullFromAssets(ref CuiElementContainer _container, string _name, string _parent, string _color, string _sprite, float _fadeIn = 0f, float _fadeOut = 0f, string _anchorMin = "0 0", string _anchorMax = "1 1", string _material = "assets/icons/iconmaterial.mat")
            {
                //assets/content/textures/generic/fulltransparent.tga MAT
                _container.Add(new CuiElement
                {
                    Parent = _parent,
                    Name = _name,
                    Components =
                            {
                                new CuiImageComponent { Material = _material, Sprite = _sprite, Color = _color, FadeIn = _fadeIn},
                                new CuiRectTransformComponent {AnchorMin = _anchorMin, AnchorMax = _anchorMax}
                            },
                    FadeOut = _fadeOut
                });
            }

            public static void CreateInput(ref CuiElementContainer _container, string _name, string _parent, string _color, int _size, string _anchorMin, string _anchorMax, string _defaultText, string _font = "permanentmarker.ttf", string _command = "command.processinput", TextAnchor _align = TextAnchor.MiddleCenter, int _charsLimit = 200, bool _readOnly = false)
            {
                _container.Add(new CuiElement
                {
                    Parent = _parent,
                    Name = _name,

                    Components =
                    {
                        new CuiInputFieldComponent
                        {

                            Text = _defaultText,
                            CharsLimit = _charsLimit,
                            Color = _color,
                            IsPassword = false,
                            Command = _command,
                            Font = _font,
                            FontSize = _size,
                            Align = _align,
                            ReadOnly = _readOnly,
                        },

                        new CuiRectTransformComponent
                        {
                            AnchorMin = _anchorMin,
                            AnchorMax = _anchorMax

                        }

                    },
                });
            }

            public static void CreateText(ref CuiElementContainer _container, string _name, string _parent, string _color, string _text, int _size, string _anchorMin, string _anchorMax, TextAnchor _align = TextAnchor.MiddleCenter, string _font = "robotocondensed-bold.ttf", float _fadeIn = 0f, float _fadeOut = 0f, string _outlineColor = "0 0 0 0", string _outlineScale = "0 0")
            {
                _container.Add(new CuiElement
                {
                    Parent = _parent,
                    Name = _name,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = _text,
                            FontSize = _size,
                            Font = _font,
                            Align = _align,
                            Color = _color,
                            FadeIn = _fadeIn,
                        },

                        new CuiOutlineComponent
                        {

                            Color = _outlineColor,
                            Distance = _outlineScale

                        },

                        new CuiRectTransformComponent
                        {
                             AnchorMin = _anchorMin,
                             AnchorMax = _anchorMax
                        }
                    },
                    FadeOut = _fadeOut
                });
            }

            public static void CreateButton(ref CuiElementContainer _container, string _name, string _parent, string _color, string _text, int _size, string _anchorMin, string _anchorMax, string _command = "", string _close = "", string _textColor = "0.843 0.816 0.78 1", float _fade = 1f, TextAnchor _align = TextAnchor.MiddleCenter, string _font = "", string _material = "assets/content/ui/uibackgroundblur-ingamemenu.mat")
            {

                _container.Add(new CuiButton
                {
                    Button = { Close = _close, Command = _command, Color = _color, Material = _material, FadeIn = _fade },
                    RectTransform = { AnchorMin = _anchorMin, AnchorMax = _anchorMax },
                    Text = { Text = _text, FontSize = _size, Align = _align, Color = _textColor, Font = _font, FadeIn = _fade }
                },
                _parent,
                _name);
            }
        }
        private void SaveData()
        {
            if (addonsData != null)
            Interface.Oxide.DataFileSystem.WriteObject($"OctaKitsData", addonsData);
        }
        
        private Dictionary<string, AddonsData> addonsData;
        
        private class AddonsData
        {    
            public string RustKits_claimCommand;
            public string RustKits_currencyName;
            public List<string> importedKits = new List<string>{};
            public bool seededDefaults = false;
        }

        private void LoadData()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile($"OctaKitsData"))
            {
                addonsData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, AddonsData>>($"OctaKitsData");
            }
            else
            {
                addonsData = new Dictionary<string, AddonsData>();            
                SaveData();
            }
        }
        
        private Configuration config;
        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<Configuration>();
            if (config?.backend == null)
            {
                if (config == null) config = Configuration.CreateConfig();
                else config.backend = new Configuration.Backend();
            }
            SaveConfig();
        }
        
        protected override void LoadDefaultConfig() => config = Configuration.CreateConfig();
        
        protected override void SaveConfig() => Config.WriteObject(config);     
        
        class Configuration
        {   
            [JsonProperty(PropertyName = "Дополнительные настройки UI")]
            public Ui ui { get; set; }

            [JsonProperty("Режим отладки")]
            public bool debugMode { get; set; }

            [JsonProperty("Бэкенд наборов")]
            public Backend backend { get; set; }

            public class Backend
            {
                [JsonProperty("Валюта платных наборов (Scrap, ServerRewards, Economics)")]
                public string currency { get; set; } = "Scrap";

                [JsonProperty("Авто-наборы при спавне (по приоритету)")]
                public List<string> autoKits { get; set; } = new List<string>();

                [JsonProperty("Сбрасывать данные игроков при вайпе")]
                public bool wipeData { get; set; } = false;

                [JsonProperty("Админы игнорируют ограничения (кулдаун/лимит/цена)")]
                public bool adminIgnoreRestrictions { get; set; } = false;

                [JsonProperty("Логировать выдачу наборов в файл")]
                public bool logKitsGiven { get; set; } = false;

                [JsonProperty("Заполнить стандартными наборами x5 (один раз, не перезаписывает свои)")]
                public bool seedDefaultKits { get; set; } = true;

                [JsonProperty("Выдавать только скины, которыми владеет игрок (нужен PlayerDLCAPI)")]
                public bool ownedSkins { get; set; } = true;

                [JsonProperty("Разрешить NPC использовать скины из наборов")]
                public bool npcSkins { get; set; } = true;

                [JsonProperty("Кулдаун набора от момента вайпа (название | секунды)")]
                public Dictionary<string, int> wipeCooldowns { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                [JsonProperty("Параметры CopyPaste при выдаче постройки")]
                public string[] copyPasteParams { get; set; } = new[] { "deployables", "true", "inventories", "true" };
            }

            public class Ui
            {   
                [JsonProperty("Закрывать панель после получения набора")]
                public bool closeAfterClaim { get; set; }

                [JsonProperty("Цвета интерфейса")]
                public Dictionary<string, string> colors { get; set; }

                [JsonProperty("Обновлять кнопки перезарядки")]
                public bool updateCooldownButtons { get; set; }

                [JsonProperty("Кнопка предыдущей страницы")]
                public PageBtn prevPage { get; set; }

                [JsonProperty("Кнопка следующей страницы")]
                public PageBtn nextPage { get; set; }

                public class PageBtn 
                {
                    [JsonProperty("Цвет кнопки")]
                    public string color { get; set; }

                    [JsonProperty("Текст кнопки")]
                    public string text { get; set; }

                    [JsonProperty("Якорь Min")]
                    public string anchorMin { get; set; }

                    [JsonProperty("Якорь Max")]
                    public string anchorMax { get; set; }

                    [JsonProperty("Картинка")]
                    public string image { get; set; }
                }
            } 
        
            public static Configuration CreateConfig()
            {
                return new Configuration
                {   
                    ui = new OctaKits.Configuration.Ui 
                    {   
                        closeAfterClaim = false,
                        updateCooldownButtons = true,
                        colors = new Dictionary<string, string>()
                        {   
                            {"Кнопка забрать", CGreen},
                            {"Кнопка перезарядки", CRed},
                            {"Кнопка без доступа", GlassLt},
                            {"Кнопка без использований", GlassLt},
                            {"Кнопка назад", GlassRow},
                            {"Основная панель", GlassRow},
                            {"Дополнительная панель", GlassBg},
                            {"Ячейка предмета", GlassRow}
                        },
                        prevPage = new OctaKits.Configuration.Ui.PageBtn
                        {
                            color = "0 0 0 0",
                            text = "<size=60>‹</size>",
                            anchorMin = "0.02 0.45",
                            anchorMax = "0.08 0.55",
                            image = ""
                        },
                        nextPage = new OctaKits.Configuration.Ui.PageBtn
                        {
                            color = "0 0 0 0",
                            text = "<size=60>›</size>",
                            anchorMin = "0.92 0.45",
                            anchorMax = "0.98 0.55",
                            image = ""
                        }
                    },
                    debugMode = false,
                    backend = new Backend()
                };
            }
        }
    
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {   
                //buttons
                ["claim_button"] = "ЗАБРАТЬ",
                ["claim_button_2"] = "ЗАБРАТЬ",
                ["view_button"] = "ОБЗОР",
                ["back_button"] = "НАЗАД",
                ["no_perms_button"] = "НЕТ ДОСТУПА",
                ["no_uses_button"] = "ЛИМИТ ИСЧЕРПАН",
                ["on_cooldown_button"] = "{kit_cooldown}",
                ["paid_kit_button"] = "{kit_price} {kit_currency}",
                //first title
                ["kit_title_format"] = "<b><size=16>{kit_title}</size></b>\n{kit_description}",
                //second title
                ["kit_title_format_view"] = "{kit_title}",
                //cooldown 
                ["cooldown_title"] = "ПЕРЕЗАРЯДКА <color=#ffffffcd>{kit_cooldown}</color>",
                ["cooldown_title_none"] = "<color=#ffffffcd>НЕТ</color>",
                //uses
                ["uses_title"] = "ОСТАЛОСЬ <color=#ffffffcd>{kit_uses}</color>",
                ["uses_title_unlimited"] = "<color=#ffffffcd>БЕЗ ЛИМИТА</color>",
                //price
                ["price_title"] = "ЦЕНА <color=#ffffffcd>{kit_price} {kit_currency}</color>",
                ["price_title_free"] =  $"ЦЕНА <color=#ffffffcd>БЕСПЛАТНО</color>",
                //perms
                ["perms_title_yes"] = "НУЖЕН VIP <color=#ffffffcd>ДА</color>",
                ["perms_title_no"] = "НУЖЕН VIP <color=#ffffffcd>НЕТ</color>",
                //notifications
                ["notification_kitclaim_success"] = "<size=14>Вы успешно получили набор <b>{kitname}</b>.</size>",
                ["notification_success_stripe_color"] = "0.482 0.675 0.251 1.00",
            }, this);
        }

        private string gl(string message) => lang.GetMessage(message, this);

        // API методы для интеграции с WelcomePanel
        object ShowKits_Page1_API(BasePlayer player)
        {
            // На всякий случай очищаем прежний UI этого аддона
            CloseKits_API(player);

            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] ShowKits_Page1_API called for player {player.displayName} (ID: {player.UserIDString})");
            Puts($"[OctaKits] ShowKits_Page1_API -> {player.displayName}");
            ShowKits(player, 0); // показываем первую страницу китов
            return null;
        }

        object CloseKits_API(BasePlayer player)
        {
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] CloseKits_API called for player {player.displayName} (ID: {player.UserIDString})");

            // Корень самостоятельного окна — уносит за собой карточки, просмотр и шапку
            CuiHelper.DestroyUi(player, "octakits_root");
            // Панель импорта живёт отдельным корнем
            CuiHelper.DestroyUi(player, "octakits_background");

            if (_cdTimers.TryGetValue(player.userID, out var _t) && _t != null)
            {
                _t.Destroy();
                _cdTimers.Remove(player.userID);
            }
            return null;
        }

        [ConsoleCommand("octakits_close")]
        private void octakits_close(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            CloseKits_API(player);
        }

        // Админ-контролы: прокси к родному редактору Kits (там вся логика создания/редактирования)
        private bool IsKitsAdmin(BasePlayer player)
            => player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, "kits.admin"));

        /// <summary>
        /// Проверка права доступа к набору. Обычные пермишены — через oxide.
        /// Пермишены вида "octavip.<уровень>" — через API OctVIP (HasVipLevel),
        /// т.к. уровень VIP может быть задан записью в data/OctVIP без oxide-пермишена.
        /// Уровни выше требуемого проходят автоматически (brilliant видит наборы gold и т.д.).
        /// </summary>
        private bool HasKitPermission(BasePlayer player, string requiredPermission)
        {
            if (player == null || string.IsNullOrEmpty(requiredPermission))
                return true;

            if (requiredPermission.StartsWith("octavip.", StringComparison.OrdinalIgnoreCase))
            {
                if (permission.UserHasPermission(player.UserIDString, requiredPermission))
                    return true;

                int level = VipLevelFromPerm(requiredPermission);
                if (level > 0 && OctVIP != null)
                {
                    try { return Convert.ToBoolean(OctVIP.Call("HasVipLevel", (ulong)player.userID, level)); }
                    catch { }
                }
                return false;
            }

            return permission.UserHasPermission(player.UserIDString, requiredPermission);
        }

        private static int VipLevelFromPerm(string perm)
        {
            switch (perm.ToLowerInvariant())
            {
                case "octavip.bronze":    return 1;
                case "octavip.silver":    return 2;
                case "octavip.gold":      return 3;
                case "octavip.brilliant": return 4;
                case "octavip.moderator": return 5;
                case "octavip.admin":     return 6;
                default:                  return 0;
            }
        }

        /// <summary>Метка валюты для кнопок/текстов (скрап / RP / $)</summary>
        private string CurrencyLabel
        {
            get
            {
                switch (_costType)
                {
                    case CostType.Scrap:         return "скрап";
                    case CostType.ServerRewards: return "RP";
                    default:                     return "$";
                }
            }
        }

        // API для внешних плагинов (OctaWelcome): та же логика доступа к наборам
        [HookMethod("HasKitPermission")]
        public bool API_HasKitPermission(BasePlayer player, string requiredPermission) => HasKitPermission(player, requiredPermission);

        [HookMethod("GetCurrencyLabel")]
        public string API_GetCurrencyLabel() => CurrencyLabel;

        #region Редактор наборов (наш дизайн поверх headless-API Kits)

        private void StartKitEditor(BasePlayer player, string kitName)
        {
            if (kitData == null)
            {
                player.ChatMessage("<color=#E8B84B>OctaKits</color>: система наборов недоступна");
                return;
            }

            object result = KitEditorStart(player, kitName ?? "");

            if (!(result is JObject draft))
            {
                player.ChatMessage("<color=#E8B84B>OctaKits</color>: не удалось открыть редактор — проверь право kits.admin");
                return;
            }

            if (_cdTimers.TryGetValue(player.userID, out var t) && t != null) { t.Destroy(); _cdTimers.Remove(player.userID); }

            // при редактировании существующего набора перезапись включена по умолчанию
            _editorOverwrite[(ulong)player.userID] = !string.IsNullOrEmpty(kitName);
            _editorToast[(ulong)player.userID] = null;

            DrawKitEditor(player, draft);
        }

        private void DrawKitEditor(BasePlayer player, JObject draft)
        {
            var ui = new CuiElementContainer();
            string draftName = draft?.SelectToken("Name")?.ToString() ?? "";

            CreateStandaloneRoot(ref ui, "◆ <b>РЕДАКТОР НАБОРА</b>", string.IsNullOrEmpty(draftName) ? "новый набор" : draftName, "octakits_editor_cancel", true);

            // Тост с результатом последнего действия
            ulong uid = (ulong)player.userID;
            string toast = _editorToast.TryGetValue(uid, out string t) ? t : null;
            if (!string.IsNullOrEmpty(toast))
                CUIClass.CreateText(ref ui, "octaedit_toast", "octakits_content", "0.98 0.80 0.35 1", toast, 11, "0.01 0.905", "0.99 0.945", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", 0f);

            // ── Левая колонка: параметры набора ──
            CUIClass.CreatePanel(ref ui, "octaedit_fields", "octakits_content", GlassBg, "0.01 0.03", "0.345 0.90", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "octaedit_fields_gold", "octaedit_fields", Gold, "0 0.985", "1 1.0", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreateText(ref ui, "octaedit_fields_title", "octaedit_fields", Gold, "◆ <b>ПАРАМЕТРЫ</b>", 11, "0.04 0.945", "0.96 0.98", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf");

            DrawEditorField(ref ui, 0, "НАЗВАНИЕ", "name", draftName);
            DrawEditorField(ref ui, 1, "ОПИСАНИЕ", "description", draft?.SelectToken("Description")?.ToString() ?? "");
            DrawEditorField(ref ui, 2, "ИКОНКА (URL)", "image", draft?.SelectToken("KitImage")?.ToString() ?? "");
            DrawEditorField(ref ui, 3, "ПРАВО ДОСТУПА (kits.*)", "permission", draft?.SelectToken("RequiredPermission")?.ToString() ?? "");
            DrawEditorField(ref ui, 4, "УРОВЕНЬ АВТОРИЗАЦИИ (0-2)", "authLevel", $"{draft?.SelectToken("RequiredAuth")}");
            DrawEditorField(ref ui, 5, "МАКС. ИСПОЛЬЗОВАНИЙ (0 = без лимита)", "maximumUses", $"{draft?.SelectToken("MaximumUses")}");
            DrawEditorField(ref ui, 6, "ПЕРЕЗАРЯДКА (СЕК)", "cooldown", $"{draft?.SelectToken("Cooldown")}");
            DrawEditorField(ref ui, 7, "ЦЕНА (0 = бесплатно)", "cost", $"{draft?.SelectToken("Cost")}");
            DrawEditorField(ref ui, 8, "COPYPASTE ФАЙЛ", "copyPaste", draft?.SelectToken("CopyPasteFile")?.ToString() ?? "");

            // Тоггл «скрытый набор»
            bool isHidden = draft?.SelectToken("IsHidden")?.ToObject<bool>() ?? false;
            float tyTop = 0.865f - 9 * 0.068f;
            CUIClass.CreateText(ref ui, "octaedit_lbl_hidden", "octaedit_fields", TMuted, "СКРЫТЫЙ НАБОР", 9, $"0.04 {f(tyTop - 0.040)}", $"0.70 {f(tyTop - 0.005)}", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf");
            CUIClass.CreateButton(ref ui, "octaedit_tgl_hidden", "octaedit_fields", isHidden ? CGreen : GlassRow,
                isHidden ? "<b>✓</b>" : "", 12, $"0.80 {f(tyTop - 0.052)}", $"0.96 {f(tyTop - 0.008)}",
                "octakits_editor_toggle isHidden", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");

            // Кнопки действий
            bool ow = _editorOverwrite.TryGetValue(uid, out bool o) && o;
            CUIClass.CreateButton(ref ui, "octaedit_save", "octaedit_fields", CGreen, "<b>✓ СОХРАНИТЬ</b>", 11, "0.04 0.068", "0.63 0.115", "octakits_editor_save", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
            CUIClass.CreateButton(ref ui, "octaedit_ow", "octaedit_fields", ow ? Gold : GlassRow, ow ? "<b>◉</b> ПЕРЕЗАПИСАТЬ" : "◯ ПЕРЕЗАПИСАТЬ", 10, "0.645 0.068", "0.96 0.115", "octakits_editor_overwrite", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
            CUIClass.CreateButton(ref ui, "octaedit_copyinv", "octaedit_fields", GoldDark, "⬇ КОПИРОВАТЬ ИНВЕНТАРЬ", 10, "0.04 0.012", "0.48 0.058", "octakits_editor_copyinv", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
            CUIClass.CreateButton(ref ui, "octaedit_clear", "octaedit_fields", CRed, "ОЧИСТИТЬ", 10, "0.50 0.012", "0.96 0.058", "octakits_editor_clearitems", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");

            // ── Правая колонка: предметы набора ──
            CUIClass.CreatePanel(ref ui, "octaedit_items", "octakits_content", GlassBg, "0.355 0.03", "0.99 0.90", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "octaedit_items_gold", "octaedit_items", Gold, "0 0.985", "1 1.0", false, 0f, 0f, "assets/icons/iconmaterial.mat");

            var main = draft?.SelectToken("MainItems") as JArray;
            var belt = draft?.SelectToken("BeltItems") as JArray;
            var wear = draft?.SelectToken("WearItems") as JArray;

            CUIClass.CreateText(ref ui, "octaedit_sec_main", "octaedit_items", Gold, $"◆ <b>ОСНОВНОЙ ИНВЕНТАРЬ</b>  <size=9><color=#8a7f66>{main?.Count ?? 0} шт.</color></size>", 11, "0.03 0.93", "0.97 0.97", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf");
            CUIClass.CreatePanel(ref ui, "octaedit_div_main", "octaedit_items", GoldDark, "0.03 0.925", "0.97 0.928", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "octaedit_grid_main", "octaedit_items", "0 0 0 0.0", "0.02 0.44", "0.98 0.91", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            for (int i = 0; i < mainA.Length; i++)
            {
                string[] splitA = mainA[i].Split('-');
                CUIClass.CreatePanel(ref ui, $"octaedit_mitem{i}", "octaedit_grid_main", GlassRow, splitA[0], splitA[1], false, 0.1f, 0f, "assets/icons/iconmaterial.mat");
                DrawEditorItem(ref ui, $"octaedit_mitem{i}", main, i);
            }

            CUIClass.CreateText(ref ui, "octaedit_sec_belt", "octaedit_items", Gold, $"◆ <b>БЫСТРЫЙ ДОСТУП</b>  <size=9><color=#8a7f66>{belt?.Count ?? 0} шт.</color></size>", 11, "0.03 0.38", "0.97 0.42", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf");
            CUIClass.CreatePanel(ref ui, "octaedit_div_belt", "octaedit_items", GoldDark, "0.03 0.375", "0.97 0.378", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "octaedit_grid_belt", "octaedit_items", "0 0 0 0.0", "0.02 0.24", "0.98 0.36", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            for (int i = 0; i < beltA.Length; i++)
            {
                string[] splitA = beltA[i].Split('-');
                CUIClass.CreatePanel(ref ui, $"octaedit_bitem{i}", "octaedit_grid_belt", GlassRow, splitA[0], splitA[1], false, 0.1f, 0f, "assets/icons/iconmaterial.mat");
                DrawEditorItem(ref ui, $"octaedit_bitem{i}", belt, i);
            }

            CUIClass.CreateText(ref ui, "octaedit_sec_wear", "octaedit_items", Gold, $"◆ <b>ЭКИПИРОВКА</b>  <size=9><color=#8a7f66>{wear?.Count ?? 0} шт.</color></size>", 11, "0.03 0.19", "0.97 0.23", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf");
            CUIClass.CreatePanel(ref ui, "octaedit_div_wear", "octaedit_items", GoldDark, "0.03 0.185", "0.97 0.188", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "octaedit_grid_wear", "octaedit_items", "0 0 0 0.0", "0.02 0.05", "0.98 0.17", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            for (int i = 0; i < clothA.Length; i++)
            {
                string[] splitA = clothA[i].Split('-');
                CUIClass.CreatePanel(ref ui, $"octaedit_witem{i}", "octaedit_grid_wear", GlassRow, splitA[0], splitA[1], false, 0.1f, 0f, "assets/icons/iconmaterial.mat");
                DrawEditorItem(ref ui, $"octaedit_witem{i}", wear, i);
            }

            CuiHelper.DestroyUi(player, "octakits_root");
            CuiHelper.AddUi(player, ui);
        }

        private static string f(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private void DrawEditorField(ref CuiElementContainer ui, int index, string label, string field, string value)
        {
            float yTop = 0.865f - index * 0.068f;
            CUIClass.CreateText(ref ui, $"octaedit_lbl_{field}", "octaedit_fields", TMuted, label, 9, $"0.04 {f(yTop - 0.022)}", $"0.96 {f(yTop)}", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf");
            CUIClass.CreatePanel(ref ui, $"octaedit_in_bg_{field}", "octaedit_fields", GlassRow, $"0.04 {f(yTop - 0.062)}", $"0.96 {f(yTop - 0.024)}", false, 0f, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreateInput(ref ui, $"octaedit_in_{field}", $"octaedit_in_bg_{field}", TWhite, 11, "0 0", "1 1", value ?? "", "robotocondensed-regular.ttf", $"octakits_editor_set {field}", TextAnchor.MiddleLeft, 128);
        }

        private void DrawEditorItem(ref CuiElementContainer ui, string cellName, JArray items, int index)
        {
            try
            {
                if (items == null || index >= items.Count) return;
                var item = items[index];
                if (item == null) return;

                string shortname = $"{item.SelectToken("Shortname")}";
                var iDef = ItemManager.FindItemDefinition(shortname);
                if (iDef == null) return;

                ulong skin = 0;
                var skinTok = item.SelectToken("SkinID");
                if (skinTok != null) skin = skinTok.ToObject<ulong>();

                ui.Add(new CuiElement
                {
                    Parent = cellName,
                    Name = $"{shortname}_{index}",
                    Components = { new CuiImageComponent { ItemId = iDef.itemid, SkinId = skin, FadeIn = 0.1f }, new CuiRectTransformComponent { AnchorMin = "0.10 0.10", AnchorMax = "0.90 0.90" } },
                    FadeOut = 0f
                });
                CUIClass.CreateText(ref ui, "amount", cellName, GoldBright, $"x{item.SelectToken("Amount")}", 10, "0.05 0.00", "0.95 0.95", TextAnchor.LowerRight, "robotocondensed-bold.ttf", 0f);
            }
            catch { }
        }

        private void EditorToast(BasePlayer player, string text)
        {
            _editorToast[(ulong)player.userID] = text;
            if (KitEditorGet(player) is JObject draft)
                DrawKitEditor(player, draft);
        }

        private void RefreshKitCache()
        {
            string[] all = GetAllKits();
            if (all == null) return;
            LoadKitCache(all);
            PreloadKitImages(all);
        }

        private string EditorErrorText(string code)
        {
            switch (code)
            {
                case "no-admin": return "Нет доступа к редактору наборов";
                case "no-session": return "Сессия редактирования потеряна — открой редактор заново";
                case "no-name": return "Укажите название набора";
                case "no-contents": return "Набор пуст — скопируйте инвентарь или укажите CopyPaste-файл";
                case "exists": return "Набор с таким именем уже существует — включите «ПЕРЕЗАПИСАТЬ»";
                default: return code ?? "Ошибка редактора";
            }
        }

        [ConsoleCommand("octakits_admin_create")]
        private void octakits_admin_create(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsKitsAdmin(player)) return;
            StartKitEditor(player, "");
        }

        [ConsoleCommand("octakits_admin_edit")]
        private void octakits_admin_edit(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsKitsAdmin(player)) return;

            string name = (arg?.FullString.ToString() ?? "").Replace("%", " ").Trim();
            if (string.IsNullOrEmpty(name)) return;

            StartKitEditor(player, name);
        }

        // Удаление набора: первый клик по 🗑 — предупреждение, второй в течение 5 сек — удаляет
        [ConsoleCommand("octakits_admin_delete")]
        private void octakits_admin_delete(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsKitsAdmin(player)) return;

            string name = (arg?.FullString.ToString() ?? "").Replace("%", " ").Trim();
            if (string.IsNullOrEmpty(name)) return;

            ulong uid = (ulong)player.userID;
            if (!_pendingDelete.TryGetValue(uid, out var pd) || pd.kit != name || DateTime.UtcNow > pd.until)
            {
                _pendingDelete[uid] = (name, DateTime.UtcNow.AddSeconds(5));
                player.ChatMessage($"<color=#E8B84B>OctaKits</color>: нажми <color=#E05A4E>🗑</color> ещё раз, чтобы удалить набор «{name}»");
                return;
            }

            _pendingDelete.Remove(uid);

            object result = KitDelete(player, name);

            if ("ok".Equals(result as string))
            {
                player.ChatMessage($"<color=#E8B84B>OctaKits</color>: набор «{name}» удалён");
                RefreshKitCache();
                ShowKits(player);
            }
            else player.ChatMessage("<color=#E8B84B>OctaKits</color>: не удалось удалить набор");
        }

        [ConsoleCommand("octakits_editor_set")]
        private void octakits_editor_set(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsKitsAdmin(player)) return;
            if (arg == null || arg.Args == null || arg.Args.Length < 1) return;

            string field = arg.GetString(0);
            string value = arg.Args.Length > 1 ? string.Join(" ", arg.Args.Skip(1).Select(x => x.ToString())) : "";

            object result = KitEditorSetField(player, field, value);

            if (result is JObject draft)
                DrawKitEditor(player, draft);
            else
                EditorToast(player, EditorErrorText(result as string));
        }

        [ConsoleCommand("octakits_editor_toggle")]
        private void octakits_editor_toggle(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsKitsAdmin(player)) return;

            if (!(KitEditorGet(player) is JObject draft)) return;

            string field = arg?.GetString(0) ?? "isHidden";
            bool cur = draft.SelectToken("IsHidden")?.ToObject<bool>() ?? false;

            object result = KitEditorSetField(player, field, (!cur).ToString());
            if (result is JObject snap)
                DrawKitEditor(player, snap);
            else
                EditorToast(player, EditorErrorText(result as string));
        }

        [ConsoleCommand("octakits_editor_overwrite")]
        private void octakits_editor_overwrite(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsKitsAdmin(player)) return;
            ulong uid = (ulong)player.userID;
            _editorOverwrite[uid] = !(_editorOverwrite.TryGetValue(uid, out bool o) && o);

            if (KitEditorGet(player) is JObject draft2)
                DrawKitEditor(player, draft2);
        }

        [ConsoleCommand("octakits_editor_copyinv")]
        private void octakits_editor_copyinv(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsKitsAdmin(player)) return;

            object result = KitEditorCopyInventory(player);
            if (result is JObject draft)
            {
                _editorToast[(ulong)player.userID] = "Инвентарь скопирован в набор";
                DrawKitEditor(player, draft);
            }
            else EditorToast(player, "Сессия редактирования потеряна");
        }

        [ConsoleCommand("octakits_editor_clearitems")]
        private void octakits_editor_clearitems(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsKitsAdmin(player)) return;

            object result = KitEditorClearItems(player);
            if (result is JObject draft)
            {
                _editorToast[(ulong)player.userID] = "Предметы набора очищены";
                DrawKitEditor(player, draft);
            }
            else EditorToast(player, "Сессия редактирования потеряна");
        }

        [ConsoleCommand("octakits_editor_save")]
        private void octakits_editor_save(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsKitsAdmin(player)) return;

            ulong uid = (ulong)player.userID;
            bool overwrite = _editorOverwrite.TryGetValue(uid, out bool o) && o;

            object result = KitEditorSave(player, overwrite);

            if ("ok".Equals(result as string))
            {
                _editorOverwrite.Remove(uid);
                _editorToast.Remove(uid);
                RefreshKitCache();
                player.ChatMessage("<color=#E8B84B>OctaKits</color>: набор сохранён");
                ShowKits(player);
            }
            else EditorToast(player, EditorErrorText(result as string));
        }

        [ConsoleCommand("octakits_editor_cancel")]
        private void octakits_editor_cancel(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;

            KitEditorCancel(player);
            _editorOverwrite.Remove((ulong)player.userID);
            _editorToast.Remove((ulong)player.userID);
            ShowKits(player);
        }

        #endregion

        #region Команды игрока

        // /kit — открыть витрину; /kit <имя> — забрать набор; /kit edit <имя> — редактор (админ)
        [ChatCommand("kit")]
        private void cmdKit(BasePlayer player, string command, string[] args)
        {
            if (args != null && args.Length > 0 && !string.IsNullOrEmpty(args[0]))
            {
                if (args[0].Equals("edit", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
                {
                    if (IsKitsAdmin(player))
                        StartKitEditor(player, string.Join(" ", args.Skip(1)));
                    else
                        player.ChatMessage(KitMsg("Error.CanClaim.Permission", player));
                    return;
                }

                TryClaimKit(player, string.Join(" ", args), false);
                return;
            }

            ShowKits(player);
        }

        [ConsoleCommand("kit")]
        private void ccmdKit(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            ShowKits(player);
        }

        #endregion

        #region Получение набора (автономный бэкенд)

        private const string ADMIN_PERMISSION = "kits.admin";
        private const string BLUEPRINT_BASE = "blueprintbase";
        private const int SCRAP_ITEM_ID = -932201673;

        private static Func<BasePlayer, ulong, bool> _canUseSkin;
        private static Func<BasePlayer, int, int> _getRedirectedIfNotOwned;

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0);
        private static double LastWipeTime;
        private static double CurrentTime => DateTime.UtcNow.Subtract(Epoch).TotalSeconds;

        private CostType _costType;
        private enum CostType { Scrap, ServerRewards, Economics }

        // Локальные сообщения бэкенда (вместо lang-файла Kits)
        private string KitMsg(string key, BasePlayer player = null)
        {
            switch (key)
            {
                case "Error.EmptyKitName": return "Название набора не указано";
                case "Error.InvalidKitName": return "Такого набора не существует";
                case "Error.CantClaimNow": return "Набор сейчас недоступен";
                case "Error.CanClaim.Auth": return "Недостаточный уровень авторизации";
                case "Error.CanClaim.Permission": return "Нет доступа к этому набору";
                case "Error.CanClaim.WipeCooldown": return "Набор станет доступен через {0}";
                case "Error.CanClaim.Cooldown": return "Перезарядка — подожди ещё {0}";
                case "Error.CanClaim.MaxUses": return "Лимит использований набора исчерпан";
                case "Error.CanClaim.InventorySpace": return "Недостаточно места в инвентаре";
                case "Error.CanClaim.InsufficientFunds": return "Недостаточно средств — нужно {0} {1}";
                case "Cost.Scrap": return "металлолом";
                case "Cost.ServerRewards": return "RP";
                case "Cost.Economics": return "$";
                case "EditKit.PermissionPrefix": return "Право доступа должно начинаться с «kits.»";
                case "EditKit.Number": return "Нужно целое число";
                case "EditKit.Bool": return "Нужно true или false";
                default: return key;
            }
        }

        private void KitReply(BasePlayer player, string message)
        {
            player.ChatMessage($"<color=#E8B84B>OctaKits</color>: {message}");
        }

        [HookMethod("TryClaimKit")]
        public bool TryClaimKit(BasePlayer player, string name, bool usingUI)
        {
            if (string.IsNullOrEmpty(name))
            {
                KitReply(player, KitMsg("Error.EmptyKitName", player));
                return false;
            }

            if (!kitData.Find(name, out KitData.Kit kit))
            {
                KitReply(player, KitMsg("Error.InvalidKitName", player));
                return false;
            }

            object success = CanClaimKit(player, kit) ?? GiveKit(player, kit);
            if (success is string s)
            {
                KitReply(player, s);
                return false;
            }

            OnKitReceived(player, kit);
            return true;
        }

        private object CanClaimKit(BasePlayer player, KitData.Kit kit, bool ignoreAuthCost = false)
        {
            object success = Interface.Oxide.CallHook("CanRedeemKit", player);
            if (success != null)
            {
                if (success is string s)
                    return s;
                return KitMsg("Error.CantClaimNow", player);
            }

            if (!ignoreAuthCost && kit.RequiredAuth > 0 && player.net.connection.authLevel < kit.RequiredAuth)
                return KitMsg("Error.CanClaim.Auth", player);

            if (config?.backend?.adminIgnoreRestrictions == true && IsKitsAdmin(player))
            {
                if (!kit.HasSpaceForItems(player))
                    return KitMsg("Error.CanClaim.InventorySpace", player);

                return null;
            }

            if (!HasKitPermission(player, kit.RequiredPermission))
                return KitMsg("Error.CanClaim.Permission", player);

            if (config?.backend?.wipeCooldowns != null && config.backend.wipeCooldowns.TryGetValue(kit.Name, out int wipeCooldownTime))
            {
                if (kitData.IsOnWipeCooldown(wipeCooldownTime, out wipeCooldownTime))
                    return string.Format(KitMsg("Error.CanClaim.WipeCooldown", player), TimeFormated(wipeCooldownTime));
            }

            if (playerData.Find((ulong)player.userID, out PlayerData.PlayerUsageData playerUsageData))
            {
                if (kit.Cooldown > 0)
                {
                    double cooldownRemaining = playerUsageData.GetCooldownRemaining(kit.Name);
                    if (cooldownRemaining > 0)
                        return string.Format(KitMsg("Error.CanClaim.Cooldown", player), TimeFormated(cooldownRemaining));
                }

                if (kit.MaximumUses > 0)
                {
                    int currentUses = playerUsageData.GetKitUses(kit.Name);
                    if (currentUses >= kit.MaximumUses)
                        return KitMsg("Error.CanClaim.MaxUses", player);
                }
            }

            if (!kit.HasSpaceForItems(player))
                return KitMsg("Error.CanClaim.InventorySpace", player);

            if (!ignoreAuthCost && kit.Cost > 0)
            {
                if (!ChargePlayer(player, kit.Cost))
                    return string.Format(KitMsg("Error.CanClaim.InsufficientFunds", player), kit.Cost, KitMsg($"Cost.{_costType}", player));
            }

            return null;
        }

        private object GiveKit(BasePlayer player, KitData.Kit kit)
        {
            if (!string.IsNullOrEmpty(kit.CopyPasteFile))
            {
                object success = CopyPaste?.CallHook("TryPasteFromSteamId", player.userID, kit.CopyPasteFile, config?.backend?.copyPasteParams, null);
                if (success != null)
                    return success;
            }

            kit.GiveItemsTo(player);
            return true;
        }

        private void OnKitReceived(BasePlayer player, KitData.Kit kit)
        {
            playerData[(ulong)player.userID].OnKitClaimed(kit);
            SavePlayerData();

            Interface.CallHook("OnKitRedeemed", player, kit.Name);

            if (config?.backend?.logKitsGiven == true)
                LogToFile("Kits_Received", $"{player.displayName} ({player.userID}) — получил набор {kit.Name}", this);
        }

        private bool ChargePlayer(BasePlayer player, int amount)
        {
            if (amount == 0)
                return true;

            switch (_costType)
            {
                case CostType.Scrap:
                    if (amount <= player.inventory.GetAmount(SCRAP_ITEM_ID))
                    {
                        player.inventory.Take(null, SCRAP_ITEM_ID, amount);
                        return true;
                    }
                    return false;
                case CostType.ServerRewards:
                    {
                        if ((int)(ServerRewards?.Call("CheckPoints", (ulong)player.userID) ?? 0) < amount)
                            return false;

                        return ServerRewards?.Call("TakePoints", (ulong)player.userID, amount) is bool ok && ok;
                    }
                case CostType.Economics:
                    return Economics?.Call("Withdraw", (ulong)player.userID, (double)amount) is bool ok2 && ok2;
            }
            return false;
        }

        private bool CanUseSkin(BasePlayer player, ulong skinID)
        {
            if (skinID == 0UL)
                return true;

            if (config?.backend?.npcSkins == true && (player.IsNpc || !player.userID.IsSteamId()))
                return true;

            if (PlayerDLCAPI != null && config?.backend?.ownedSkins == true)
                return PlayerDLCAPI.Call<bool>("IsOwnedOrFreeSkin", player, skinID);

            return true;
        }

        private int GetRedirectedShortnameIfNotOwned(BasePlayer player, int itemId)
        {
            if (config?.backend?.npcSkins == true && (player.IsNpc || !player.userID.IsSteamId()))
                return itemId;

            if (PlayerDLCAPI != null && config?.backend?.ownedSkins == true)
                return PlayerDLCAPI.Call<int>("GetRedirectedItemIdIfNotOwned", player, itemId);

            return itemId;
        }

        private T ParseType<T>(string type)
        {
            try
            {
                return (T)Enum.Parse(typeof(T), type, true);
            }
            catch
            {
                return default(T);
            }
        }

        #endregion

        #region Бэкенд редактора наборов

        private Hash<ulong, KitData.Kit> _kitCreators = new Hash<ulong, KitData.Kit>();

        [HookMethod("KitEditorStart")]
        public object KitEditorStart(BasePlayer player, string kitName)
        {
            if (player == null || !IsKitsAdmin(player))
                return null;

            if (string.IsNullOrEmpty(kitName))
                _kitCreators[(ulong)player.userID] = new KitData.Kit();
            else
            {
                if (!kitData.Find(kitName, out KitData.Kit editKit))
                    return null;

                _kitCreators[(ulong)player.userID] = KitData.Kit.CloneOf(editKit);
            }

            return EditorSnapshot(_kitCreators[(ulong)player.userID]);
        }

        [HookMethod("KitEditorGet")]
        public object KitEditorGet(BasePlayer player)
            => player != null && _kitCreators.TryGetValue((ulong)player.userID, out KitData.Kit kit) ? EditorSnapshot(kit) : (object)null;

        [HookMethod("KitEditorSetField")]
        public object KitEditorSetField(BasePlayer player, string field, string value)
        {
            if (player == null || !IsKitsAdmin(player))
                return "no-admin";

            if (!_kitCreators.TryGetValue((ulong)player.userID, out KitData.Kit kit))
                return "no-session";

            if (string.IsNullOrEmpty(value))
                ClearField(kit, field);
            else
            {
                string error = TrySetParameter(player, kit, field, value);
                if (error != null)
                    return error;
            }

            return EditorSnapshot(kit);
        }

        [HookMethod("KitEditorCopyInventory")]
        public object KitEditorCopyInventory(BasePlayer player)
        {
            if (player == null || !IsKitsAdmin(player) || !_kitCreators.TryGetValue((ulong)player.userID, out KitData.Kit kit))
                return null;

            kit.CopyItemsFrom(player);
            return EditorSnapshot(kit);
        }

        [HookMethod("KitEditorClearItems")]
        public object KitEditorClearItems(BasePlayer player)
        {
            if (player == null || !IsKitsAdmin(player) || !_kitCreators.TryGetValue((ulong)player.userID, out KitData.Kit kit))
                return null;

            kit.ClearItems();
            return EditorSnapshot(kit);
        }

        [HookMethod("KitEditorSave")]
        public object KitEditorSave(BasePlayer player, bool overwrite)
        {
            if (player == null || !IsKitsAdmin(player))
                return "no-admin";

            if (!_kitCreators.TryGetValue((ulong)player.userID, out KitData.Kit kit))
                return "no-session";

            if (string.IsNullOrEmpty(kit.Name))
                return "no-name";

            if (kit.ItemCount == 0 && string.IsNullOrEmpty(kit.CopyPasteFile))
                return "no-contents";

            if (kitData.Exists(kit.Name) && !overwrite)
                return "exists";

            kitData[kit.Name] = kit;
            SaveKitData();

            _kitCreators.Remove((ulong)player.userID);

            if (!string.IsNullOrEmpty(kit.RequiredPermission) && !permission.PermissionExists(kit.RequiredPermission))
                permission.RegisterPermission(kit.RequiredPermission, this);

            if (!string.IsNullOrEmpty(kit.KitImage))
                RegisterKitImage(kit.Name, kit.KitImage);

            return "ok";
        }

        [HookMethod("KitEditorCancel")]
        public void KitEditorCancel(BasePlayer player)
        {
            if (player != null)
                _kitCreators.Remove((ulong)player.userID);
        }

        [HookMethod("KitDelete")]
        public object KitDelete(BasePlayer player, string kitName)
        {
            if (player == null || !IsKitsAdmin(player))
                return "no-admin";

            if (string.IsNullOrEmpty(kitName) || !kitData.Find(kitName, out KitData.Kit deleteKit))
                return "not-found";

            kitData.Remove(deleteKit);
            SaveKitData();
            return "ok";
        }

        // Свежий снапшот черновика: Kit.ToJObject кэширует JObject и после мутаций отдавал бы устаревшие данные
        private static JObject EditorSnapshot(KitData.Kit kit)
        {
            JObject obj = new JObject
            {
                ["Name"] = kit.Name,
                ["Description"] = kit.Description,
                ["RequiredPermission"] = kit.RequiredPermission,
                ["MaximumUses"] = kit.MaximumUses,
                ["RequiredAuth"] = kit.RequiredAuth,
                ["Cooldown"] = kit.Cooldown,
                ["Cost"] = kit.Cost,
                ["IsHidden"] = kit.IsHidden,
                ["CopyPasteFile"] = kit.CopyPasteFile,
                ["KitImage"] = kit.KitImage,
                ["MainItems"] = new JArray(),
                ["WearItems"] = new JArray(),
                ["BeltItems"] = new JArray()
            };

            for (int i = 0; i < kit.MainItems.Length; i++)
                ((JArray)obj["MainItems"]).Add(kit.MainItems[i].ToJObject);
            for (int i = 0; i < kit.WearItems.Length; i++)
                ((JArray)obj["WearItems"]).Add(kit.WearItems[i].ToJObject);
            for (int i = 0; i < kit.BeltItems.Length; i++)
                ((JArray)obj["BeltItems"]).Add(kit.BeltItems[i].ToJObject);

            return obj;
        }

        private void ClearField(KitData.Kit kit, string fieldName)
        {
            switch (fieldName)
            {
                case "name": kit.Name = string.Empty; break;
                case "description": kit.Description = string.Empty; break;
                case "copyPaste": kit.CopyPasteFile = string.Empty; break;
                case "permission": kit.RequiredPermission = string.Empty; break;
                case "image": kit.KitImage = string.Empty; break;
                case "cost": kit.Cost = 0; break;
                case "cooldown": kit.Cooldown = 0; break;
                case "maximumUses": kit.MaximumUses = 0; break;
                case "authLevel": kit.RequiredAuth = 0; break;
                case "isHidden": kit.IsHidden = false; break;
            }
        }

        // Возвращает текст ошибки или null
        private string TrySetParameter(BasePlayer player, KitData.Kit kit, string fieldName, object value)
        {
            if (value == null)
                return null;

            switch (fieldName)
            {
                case "name":
                    kit.Name = (string)value;
                    return null;
                case "description":
                    kit.Description = (string)value;
                    return null;
                case "copyPaste":
                    kit.CopyPasteFile = (string)value;
                    return null;
                case "permission":
                    if (!((string)value).StartsWith("kits."))
                        return KitMsg("EditKit.PermissionPrefix", player);
                    kit.RequiredPermission = (string)value;
                    return null;
                case "image":
                    kit.KitImage = (string)value;
                    return null;
                case "cost":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            return KitMsg("EditKit.Number", player);
                        kit.Cost = intValue;
                        return null;
                    }
                case "cooldown":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            return KitMsg("EditKit.Number", player);
                        kit.Cooldown = intValue;
                        return null;
                    }
                case "maximumUses":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            return KitMsg("EditKit.Number", player);
                        kit.MaximumUses = intValue;
                        return null;
                    }
                case "authLevel":
                    {
                        if (!TryConvertValue(value, out int intValue))
                            return KitMsg("EditKit.Number", player);
                        kit.RequiredAuth = Mathf.Clamp(intValue, 0, 2);
                        return null;
                    }
                case "isHidden":
                    {
                        if (!TryConvertValue(value, out bool boolValue))
                            return KitMsg("EditKit.Bool", player);
                        kit.IsHidden = boolValue;
                        return null;
                    }
                default:
                    return null;
            }
        }

        private bool TryConvertValue<T>(object value, out T result)
        {
            try
            {
                result = (T)Convert.ChangeType(value, typeof(T));
                return true;
            }
            catch
            {
                result = default(T);
                return false;
            }
        }

        #endregion

        #region Публичное API (совместимо с хуками Kits)

        [HookMethod("isKit")]
        public bool isKit(string name) => IsKit(name);

        [HookMethod("IsKit")]
        public bool IsKit(string name) => !string.IsNullOrEmpty(name) && kitData != null && kitData.Exists(name);

        [HookMethod("GetAllKits")]
        public string[] GetAllKits() => kitData?.Keys.ToArray() ?? new string[0];

        [HookMethod("GetKitNames")]
        public void GetKitNames(List<string> list) { if (kitData != null) list.AddRange(kitData.Keys); }

        [HookMethod("GetKitImage")]
        public string GetKitImage(string name) => kitData?[name]?.KitImage ?? string.Empty;

        [HookMethod("GetKitDescription")]
        public string GetKitDescription(string name) => kitData?[name]?.Description ?? string.Empty;

        [HookMethod("GetKitMaxUses")]
        public int GetKitMaxUses(string name) => kitData?[name]?.MaximumUses ?? 0;

        [HookMethod("GetKitCooldown")]
        public int GetKitCooldown(string name) => kitData?[name]?.Cooldown ?? 0;

        [HookMethod("GetPlayerKitUses")]
        public int GetPlayerKitUses(ulong playerId, string name) => playerData != null && playerData.Exists(playerId) ? playerData[playerId].GetKitUses(name) : 0;

        [HookMethod("SetPlayerKitUses")]
        public void SetPlayerKitUses(ulong playerId, string name, int amount)
        {
            if (playerData != null && playerData.Exists(playerId))
                playerData[playerId].SetKitUses(name, amount);
        }

        [HookMethod("GetPlayerKitCooldown")]
        public double GetPlayerKitCooldown(ulong playerId, string name) => playerData != null && playerData.Exists(playerId) ? playerData[playerId].GetCooldownRemaining(name) : 0;

        [HookMethod("SetPlayerKitCooldown")]
        public void SetPlayerCooldown(ulong playerId, string name, double seconds)
        {
            if (playerData != null && playerData.Exists(playerId))
                playerData[playerId].SetCooldownRemaining(name, seconds);
        }

        [HookMethod("GetKitObject")]
        public JObject GetKitObject(string name)
        {
            if (kitData == null || !kitData.Find(name, out KitData.Kit kit))
                return null;

            return kit.ToJObject;
        }

        [HookMethod("GiveKit")]
        public object GiveKit(BasePlayer player, string name)
        {
            if (!player)
                return null;

            if (string.IsNullOrEmpty(name))
                return KitMsg("Error.EmptyKitName", player);

            if (!kitData.Find(name, out KitData.Kit kit))
                return KitMsg("Error.InvalidKitName", player);

            return GiveKit(player, kit);
        }

        [HookMethod("CreateKitItems")]
        public IEnumerable<Item> CreateKitItems(string name)
        {
            if (kitData == null || !kitData.Find(name, out KitData.Kit kit))
                yield break;

            foreach (Item item in kit.CreateItems())
                yield return item;
        }

        // Алиасы для совместимости со старыми вызовами
        [HookMethod("KitImage")]
        public string KitImage(string name) => GetKitImage(name);

        [HookMethod("KitDescription")]
        public string KitDescription(string name) => GetKitDescription(name);

        [HookMethod("KitMax")]
        public int KitMax(string name) => GetKitMaxUses(name);

        [HookMethod("KitCooldown")]
        public double KitCooldown(string name) => GetKitCooldown(name);

        [HookMethod("PlayerKitMax")]
        public int PlayerKitMax(ulong playerId, string name) => GetPlayerKitUses(playerId, name);

        [HookMethod("PlayerKitCooldown")]
        public double PlayerKitCooldown(ulong playerId, string name) => GetPlayerKitCooldown(playerId, name);

        [HookMethod("GetKitContents")]
        public string[] GetKitContents(string name)
        {
            if (kitData == null || !kitData.Find(name, out KitData.Kit kit))
                return null;

            List<string> items = Pool.Get<List<string>>();

            void Append(List<string> list, ItemData[] itemData)
            {
                for (int i = 0; i < itemData.Length; i++)
                {
                    ItemData item = itemData[i];
                    string itemstring = $"{item.ItemID}_{item.Amount}";

                    if (item.Contents is { Length: > 0 })
                    {
                        for (int j = 0; j < item.Contents.Length; j++)
                            itemstring += $"_{item.Contents[j].ItemID}";
                    }

                    if (item.Container != null && item.Container.contents.Count > 0)
                    {
                        for (int j = 0; j < item.Container.contents.Count; j++)
                            itemstring += $"_{item.Container.contents[j].ItemID}";
                    }

                    list.Add(itemstring);
                }
            }

            Append(items, kit.BeltItems);
            Append(items, kit.MainItems);
            Append(items, kit.WearItems);

            string[] array = items.ToArray();
            Pool.FreeUnmanaged(ref items);
            return array;
        }

        [HookMethod("GetKitInfo")]
        public object GetKitInfo(string name)
        {
            if (kitData == null || !kitData.Find(name, out KitData.Kit kit))
                return null;

            JObject obj = new JObject
            {
                ["name"] = kit.Name,
                ["permission"] = kit.RequiredPermission,
                ["max"] = kit.MaximumUses,
                ["image"] = kit.KitImage,
                ["hide"] = kit.IsHidden,
                ["description"] = kit.Description,
                ["cooldown"] = kit.Cooldown,
                ["building"] = kit.CopyPasteFile,
                ["authlevel"] = kit.RequiredAuth
            };

            JArray array = new JArray();
            GetItemObject_Old(ref array, kit.BeltItems, "belt");
            GetItemObject_Old(ref array, kit.MainItems, "main");
            GetItemObject_Old(ref array, kit.WearItems, "wear");

            obj["items"] = array;
            return obj;
        }

        private void GetItemObject_Old(ref JArray array, ItemData[] items, string container)
        {
            for (int i = 0; i < items.Length; i++)
            {
                ItemData itemData = items[i];
                JObject item = new JObject
                {
                    ["amount"] = itemData.Amount,
                    ["container"] = container,
                    ["itemid"] = itemData.ItemID,
                    ["skinid"] = itemData.Skin,
                    ["weapon"] = !string.IsNullOrEmpty(itemData.Ammotype),
                    ["blueprint"] = itemData.BlueprintItemID,
                    ["mods"] = new JArray()
                };

                if (itemData.Contents is { Length: > 0 })
                {
                    for (int i1 = 0; i1 < itemData.Contents.Length; i1++)
                        (item["mods"] as JArray).Add(itemData.Contents[i1].ItemID);
                }

                if (itemData.Container != null && itemData.Container.contents.Count > 0)
                {
                    for (int i3 = 0; i3 < itemData.Container.contents.Count; i3++)
                        (item["mods"] as JArray).Add(itemData.Container.contents[i3].ItemID);
                }

                array.Add(item);
            }
        }

        #endregion

        #region Хранение данных и миграция из Kits

        private KitData kitData;
        private PlayerData playerData;

        private DynamicConfigFile kitsFile;
        private DynamicConfigFile playersFile;

        private void SaveKitData() => kitsFile?.WriteObject(kitData);
        private void SavePlayerData() => playersFile?.WriteObject(playerData);

        private void LoadKitData()
        {
            kitsFile = Interface.Oxide.DataFileSystem.GetFile("OctaKits/kits_data");

            // ReadObject по отсутствующему файлу возвращает пустой, но ВАЛИДНЫЙ объект —
            // поэтому наличие собственного файла проверяем явно, иначе миграция не срабатывала
            kitData = Interface.Oxide.DataFileSystem.ExistsDatafile("OctaKits/kits_data")
                ? kitsFile.ReadObject<KitData>()
                : null;

            if (!kitData?.IsValid ?? true)
                kitData = null;

            if (kitData == null && Interface.Oxide.DataFileSystem.ExistsDatafile("Kits/kits_data"))
            {
                // Миграция: подхватываем наборы из данных старого плагина Kits (схема совместима)
                try
                {
                    kitData = Interface.Oxide.DataFileSystem.GetFile("Kits/kits_data").ReadObject<KitData>();
                    if (kitData?.IsValid == true)
                    {
                        Puts($"Миграция: перенесено {kitData.Count} наборов из Kits/kits_data.json");
                        SaveKitData();
                    }
                    else
                    {
                        kitData = null;
                    }
                }
                catch (Exception ex)
                {
                    PrintWarning($"Ошибка миграции наборов из Kits: {ex.Message}");
                    kitData = null;
                }
            }

            if (kitData == null)
                kitData = new KitData();
        }

        private void LoadPlayerData()
        {
            playersFile = Interface.Oxide.DataFileSystem.GetFile("OctaKits/player_data");

            playerData = Interface.Oxide.DataFileSystem.ExistsDatafile("OctaKits/player_data")
                ? playersFile.ReadObject<PlayerData>()
                : null;

            if (!playerData?.IsValid ?? true)
                playerData = null;

            if (playerData == null && Interface.Oxide.DataFileSystem.ExistsDatafile("Kits/player_data"))
            {
                // Миграция статистики использований/кулдаунов из старого Kits
                try
                {
                    playerData = Interface.Oxide.DataFileSystem.GetFile("Kits/player_data").ReadObject<PlayerData>();
                    if (playerData?.IsValid == true)
                    {
                        Puts("Миграция: перенесены данные игроков из Kits/player_data.json");
                        SavePlayerData();
                    }
                    else
                    {
                        playerData = null;
                    }
                }
                catch (Exception ex)
                {
                    PrintWarning($"Ошибка миграции данных игроков из Kits: {ex.Message}");
                    playerData = null;
                }
            }

            if (playerData == null)
                playerData = new PlayerData();
        }

        #region Сид стандартных наборов x5

        // Одноразовое заполнение каталога: добавляет отсутствующие стандартные наборы,
        // никогда не перезаписывает существующие (имена сравниваются без регистра).
        // Повторный запуск вручную — консольная команда octakits.reseed.
        private void EnsureSeedKits(bool force = false)
        {
            if (kitData == null) return;
            if (!force && config?.backend?.seedDefaultKits != true) return;
            if (!force && addonsData != null && addonsData.TryGetValue("addons", out var ad) && ad.seededDefaults) return;

            int added = 0;
            foreach (KitData.Kit kit in BuildSeedKits())
            {
                if (kitData.Exists(kit.Name)) continue; // свой набор с таким именем — не трогаем
                kitData[kit.Name] = kit;
                added++;
            }

            if (added > 0)
            {
                SaveKitData();
                kitData.RegisterPermissions(permission, this);
                Puts($"Сид наборов x5: добавлено {added} стандартных наборов");
            }
            else
            {
                Puts("Сид наборов x5: все стандартные наборы уже существуют — ничего не добавлено");
            }

            if (addonsData != null && addonsData.TryGetValue("addons", out var addons))
            {
                addons.seededDefaults = true;
                SaveData();
            }
        }

        [ConsoleCommand("octakits.reseed")]
        private void ccmdReseed(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg?.Player();
            if (player != null && !IsKitsAdmin(player)) return;
            EnsureSeedKits(force: true);
            if (player != null)
                player.ChatMessage("<color=#E8B84B>OctaKits</color>: ресид выполнен — детали в консоли сервера");
        }

        private static ItemData SeedItem(string shortname, int amount = 1, int position = 0, string ammoType = null, int ammo = 0)
            => new ItemData { Shortname = shortname, Amount = amount, Position = position, Ammotype = ammoType, Ammo = ammo };

        private const string SeedImgBase = "https://wiki.rustclash.com/img/items180/";

        // Каталог x5: прогрессия по уровням OctVIP (default → bronze → silver → gold → brilliant)
        private static List<KitData.Kit> BuildSeedKits()
        {
            const string P_BRONZE = "octavip.bronze";
            const string P_SILVER = "octavip.silver";
            const string P_GOLD = "octavip.gold";
            const string P_BRILLIANT = "octavip.brilliant";

            return new List<KitData.Kit>
            {
                // ── DEFAULT: два простых набора ─────────────────────────────
                new KitData.Kit
                {
                    Name = "Стартовый",
                    Description = "Базовый комплект выживания: инструменты, еда и стартовые ресурсы. Один раз за вайп.",
                    MaximumUses = 1, Cooldown = 0,
                    KitImage = SeedImgBase + "rock.png",
                    MainItems = new[]
                    {
                        SeedItem("rock"), SeedItem("stonehatchet"), SeedItem("stone.pickaxe"), SeedItem("torch"),
                        SeedItem("sleepingbag"), SeedItem("building.planner"), SeedItem("hammer"),
                        SeedItem("bandage", 10), SeedItem("can.tuna", 3), SeedItem("apple", 4),
                        SeedItem("wood", 20000), SeedItem("stones", 12000), SeedItem("cloth", 300)
                    }
                },
                new KitData.Kit
                {
                    Name = "Рабочий",
                    Description = "Полный инструментарий и материалы на первую базу.",
                    Cooldown = 21600,
                    KitImage = SeedImgBase + "hatchet.png",
                    MainItems = new[]
                    {
                        SeedItem("hatchet"), SeedItem("pickaxe"), SeedItem("hammer"),
                        SeedItem("wood", 30000), SeedItem("stones", 25000), SeedItem("metal.fragments", 4000),
                        SeedItem("rope", 10), SeedItem("tarp", 5), SeedItem("can.beans", 3)
                    }
                },

                // ── BRONZE ──────────────────────────────────────────────────
                new KitData.Kit
                {
                    Name = "Охотник",
                    Description = "Арбалет, револьвер и лёгкая экипировка для охоты и самообороны.",
                    RequiredPermission = P_BRONZE, Cooldown = 21600,
                    KitImage = SeedImgBase + "crossbow.png",
                    MainItems = new[]
                    {
                        SeedItem("crossbow"), SeedItem("arrow.bone", 40), SeedItem("arrow.wooden", 20),
                        SeedItem("pistol.revolver"), SeedItem("ammo.pistol", 32), SeedItem("knife.bone"),
                        SeedItem("bandage", 10), SeedItem("can.tuna", 3)
                    },
                    WearItems = new[]
                    {
                        SeedItem("hoodie", 1, 0), SeedItem("pants", 1, 1), SeedItem("burlap.shoes", 1, 2), SeedItem("hat.beenie", 1, 3)
                    }
                },
                new KitData.Kit
                {
                    Name = "Медик",
                    Description = "Полевой комплект медика: медкиты, шприцы и защитный костюм.",
                    RequiredPermission = P_BRONZE, Cooldown = 21600,
                    KitImage = SeedImgBase + "syringe.medical.png",
                    MainItems = new[]
                    {
                        SeedItem("largemedkit", 2), SeedItem("syringe.medical", 10), SeedItem("bandage", 15),
                        SeedItem("antiradpills", 10), SeedItem("can.tuna", 2)
                    },
                    WearItems = new[] { SeedItem("hazmatsuit", 1, 0) }
                },
                new KitData.Kit
                {
                    Name = "Дайвер",
                    Description = "Гарпун и полный комплект для подводных вылазок.",
                    RequiredPermission = P_BRONZE, Cooldown = 21600,
                    KitImage = SeedImgBase + "diving.tank.png",
                    MainItems = new[]
                    {
                        SeedItem("speargun"), SeedItem("speargun.spear", 12), SeedItem("flare", 5),
                        SeedItem("knife.bone"), SeedItem("bandage", 8)
                    },
                    WearItems = new[]
                    {
                        SeedItem("diving.wetsuit", 1, 0), SeedItem("diving.mask", 1, 1),
                        SeedItem("diving.tank", 1, 2), SeedItem("diving.fins", 1, 3)
                    }
                },

                // ── SILVER ──────────────────────────────────────────────────
                new KitData.Kit
                {
                    Name = "Строитель",
                    Description = "Материалы, верстак и фурнитура для полноценной базы.",
                    RequiredPermission = P_SILVER, Cooldown = 86400,
                    KitImage = SeedImgBase + "building.planner.png",
                    MainItems = new[]
                    {
                        SeedItem("wood", 80000), SeedItem("stones", 60000), SeedItem("metal.fragments", 15000),
                        SeedItem("metal.refined", 400), SeedItem("cupboard.tool"), SeedItem("box.wooden.large", 4),
                        SeedItem("lock.code", 3), SeedItem("lock.key", 2), SeedItem("workbench1"),
                        SeedItem("door.hinged.metal", 4), SeedItem("building.planner"), SeedItem("hammer"), SeedItem("furnace", 2)
                    }
                },
                new KitData.Kit
                {
                    Name = "Фермер PRO",
                    Description = "Отбойный молоток, бензопила и стартовая руда для промышленного фарма.",
                    RequiredPermission = P_SILVER, Cooldown = 43200,
                    KitImage = SeedImgBase + "jackhammer.png",
                    MainItems = new[]
                    {
                        SeedItem("jackhammer"), SeedItem("chainsaw"), SeedItem("lowgradefuel", 400),
                        SeedItem("metal.ore", 6000), SeedItem("sulfur.ore", 5000), SeedItem("hq.metal.ore", 400),
                        SeedItem("wood", 30000)
                    }
                },
                new KitData.Kit
                {
                    Name = "Боец",
                    Description = "Томпсон и кевларовая экипировка — боевой комплект среднего класса.",
                    RequiredPermission = P_SILVER, Cooldown = 21600,
                    KitImage = SeedImgBase + "smg.thompson.png",
                    MainItems = new[]
                    {
                        SeedItem("smg.thompson"), SeedItem("ammo.pistol.hv", 128),
                        SeedItem("bandage", 10), SeedItem("syringe.medical", 5)
                    },
                    WearItems = new[]
                    {
                        SeedItem("coffeecan.helmet", 1, 0), SeedItem("roadsign.jacket", 1, 1), SeedItem("roadsign.kilt", 1, 2),
                        SeedItem("tactical.gloves", 1, 3), SeedItem("boots.frog", 1, 4)
                    }
                },

                // ── GOLD ────────────────────────────────────────────────────
                new KitData.Kit
                {
                    Name = "Подрывник",
                    Description = "Комплект подрывника: калаш с разрывными, сачели и молотовы.",
                    RequiredPermission = P_GOLD, Cooldown = 86400,
                    KitImage = SeedImgBase + "explosive.satchel.png",
                    MainItems = new[]
                    {
                        SeedItem("rifle.ak", 1, 0, "ammo.rifle.explosive", 30),
                        SeedItem("ammo.rifle.explosive", 180), SeedItem("explosive.satchel", 4),
                        SeedItem("grenade.molotov", 3), SeedItem("syringe.medical", 4)
                    }
                },
                new KitData.Kit
                {
                    Name = "Снайпер",
                    Description = "Болтовая снайперка, ПНВ и дымы для дальнобойной работы.",
                    RequiredPermission = P_GOLD, Cooldown = 86400,
                    KitImage = SeedImgBase + "rifle.l96.png",
                    MainItems = new[]
                    {
                        SeedItem("rifle.l96"), SeedItem("ammo.rifle.hv", 64), SeedItem("nightvisiongoggles"),
                        SeedItem("tool.binoculars"), SeedItem("grenade.smoke", 4), SeedItem("syringe.medical", 4)
                    },
                    WearItems = new[]
                    {
                        SeedItem("hoodie", 1, 0), SeedItem("pants", 1, 1), SeedItem("boots.frog", 1, 2), SeedItem("tactical.gloves", 1, 3)
                    }
                },
                new KitData.Kit
                {
                    Name = "Гренадёр",
                    Description = "Револьвер-гранатомёт и запас гранат для огневой поддержки.",
                    RequiredPermission = P_GOLD, Cooldown = 86400,
                    KitImage = SeedImgBase + "grenade.f1.png",
                    MainItems = new[]
                    {
                        SeedItem("multiplegrenadelauncher"), SeedItem("ammo.grenadelauncher.he", 12),
                        SeedItem("ammo.grenadelauncher.buckshot", 6), SeedItem("grenade.f1", 6),
                        SeedItem("grenade.beancan", 8), SeedItem("syringe.medical", 4)
                    },
                    WearItems = new[]
                    {
                        SeedItem("coffeecan.helmet", 1, 0), SeedItem("roadsign.jacket", 1, 1), SeedItem("roadsign.kilt", 1, 2)
                    }
                },

                // ── BRILLIANT ───────────────────────────────────────────────
                new KitData.Kit
                {
                    Name = "Танк",
                    Description = "Тяжёлая броня и M249 — ходячая крепость.",
                    RequiredPermission = P_BRILLIANT, Cooldown = 172800,
                    KitImage = SeedImgBase + "heavy.plate.jacket.png",
                    MainItems = new[]
                    {
                        SeedItem("lmg.m249"), SeedItem("ammo.rifle", 200), SeedItem("largemedkit", 3), SeedItem("syringe.medical", 6)
                    },
                    WearItems = new[]
                    {
                        SeedItem("heavy.plate.helmet", 1, 0), SeedItem("heavy.plate.jacket", 1, 1), SeedItem("heavy.plate.pants", 1, 2),
                        SeedItem("tactical.gloves", 1, 3), SeedItem("boots.frog", 1, 4)
                    }
                },
                new KitData.Kit
                {
                    Name = "Легенда",
                    Description = "Полный топовый комплект: LR-300, металл-сет и сигналы снабжения.",
                    RequiredPermission = P_BRILLIANT, Cooldown = 172800,
                    KitImage = SeedImgBase + "rifle.lr300.png",
                    MainItems = new[]
                    {
                        SeedItem("rifle.lr300"), SeedItem("ammo.rifle.hv", 150), SeedItem("largemedkit", 5),
                        SeedItem("syringe.medical", 8), SeedItem("supply.signal", 2), SeedItem("grenade.smoke", 4)
                    },
                    WearItems = new[]
                    {
                        SeedItem("metal.facemask", 1, 0), SeedItem("metal.plate.torso", 1, 1), SeedItem("pants", 1, 2),
                        SeedItem("tactical.gloves", 1, 3), SeedItem("boots.frog", 1, 4)
                    }
                },
                // Платный рейдерский набор: топ-ранг + списание валюты — двойной барьер самого мощного комплекта.
                // 22 ракеты (12 обычных + 6 скоростных + 4 зажигательных) — хватает на бронированную базу «на один дом».
                new KitData.Kit
                {
                    Name = "Рейдер",
                    Description = "Платный рейдерский комплект: ракетница и 22 ракеты — хватит на целую базу.",
                    RequiredPermission = P_BRILLIANT, Cooldown = 172800, Cost = 1500,
                    KitImage = SeedImgBase + "rocket.launcher.png",
                    MainItems = new[]
                    {
                        SeedItem("rocket.launcher"), SeedItem("ammo.rocket.basic", 12), SeedItem("ammo.rocket.hv", 6),
                        SeedItem("ammo.rocket.fire", 4), SeedItem("syringe.medical", 4), SeedItem("grenade.smoke", 2)
                    }
                }
            };
        }

        #endregion

        private void RegisterKitImage(string name, string url) => ImageLibrary?.Call("AddImage", url, name.Replace(" ", ""), 0UL, null);

        // Поправки устаревших shortname в данных наборов — чтобы старые наборы не ломались после обновлений Rust
        private void CheckForShortnameUpdates()
        {
            bool hasChanges = false;

            kitData.ForEach(kit =>
            {
                Action<ItemData[]> action = (items =>
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        ItemData kitItem = items[i];

                        if (!string.IsNullOrEmpty(kitItem.Shortname) && _itemShortnameReplacements.TryGetValue(kitItem.Shortname, out string replacement))
                        {
                            kitItem.Shortname = replacement;
                            hasChanges = true;
                        }
                    }
                });

                action(kit.BeltItems);
                action(kit.WearItems);
                action(kit.MainItems);
            });

            if (hasChanges)
                SaveKitData();
        }

        private readonly Dictionary<string, string> _itemShortnameReplacements = new Dictionary<string, string>
        {
            ["chocholate"] = "chocolate",
            ["medsyringe"] = "syringe.medical",
            ["key.lock"] = "lock.key"
        };

        #endregion

        #region Модель данных (порт из Kits — схема JSON совместима)

        private class KitData
        {
            [JsonProperty]
            private Dictionary<string, Kit> _kits = new Dictionary<string, Kit>(StringComparer.OrdinalIgnoreCase);

            internal Kit this[string key]
            {
                get
                {
                    if (_kits.TryGetValue(key, out Kit tValue))
                        return tValue;

                    return null;
                }
                set
                {
                    if (value == null)
                    {
                        _kits.Remove(key);
                        return;
                    }
                    _kits[key] = value;
                }
            }

            internal int Count => _kits.Count;

            internal bool Find(string name, out Kit kit) => _kits.TryGetValue(name, out kit);

            internal bool Exists(string name) => _kits.ContainsKey(name);

            internal ICollection<string> Keys => _kits.Keys;

            internal ICollection<Kit> Values => _kits.Values;

            internal void ForEach(Action<Kit> action)
            {
                foreach (Kit kit in Values)
                {
                    action.Invoke(kit);
                }
            }

            internal void RegisterPermissions(Permission permission, Plugin plugin)
            {
                foreach (Kit kit in _kits.Values)
                {
                    if (!string.IsNullOrEmpty(kit.RequiredPermission))
                    {
                        if (!permission.PermissionExists(kit.RequiredPermission, plugin))
                            permission.RegisterPermission(kit.RequiredPermission, plugin);
                    }
                }
            }

            internal void RegisterImages(Plugin plugin)
            {
                Dictionary<string, string> loadOrder = new Dictionary<string, string>();

                foreach (Kit kit in _kits.Values)
                {
                    if (!string.IsNullOrEmpty(kit.KitImage))
                        loadOrder[kit.Name.Replace(" ", "")] = kit.KitImage;
                }

                if (loadOrder.Count > 0)
                    plugin?.CallHook("ImportImageList", "OctaKits", loadOrder, 0UL, true, null);
            }

            internal bool IsOnWipeCooldown(int seconds, out int remaining)
            {
                double currentTime = CurrentTime;
                double nextUseTime = LastWipeTime + seconds;

                if (currentTime < nextUseTime)
                {
                    remaining = Mathf.RoundToInt((float)nextUseTime - (float)currentTime);
                    return true;
                }

                remaining = 0;
                return false;
            }

            internal void Remove(Kit kit) => _kits.Remove(kit.Name);

            internal bool IsValid => _kits != null;

            public class Kit
            {
                public string Name { get; set; } = string.Empty;
                public string Description { get; set; } = string.Empty;
                public string RequiredPermission { get; set; } = string.Empty;

                public int MaximumUses { get; set; }
                public int RequiredAuth { get; set; }
                public int Cooldown { get; set; }
                public int Cost { get; set; }

                public bool IsHidden { get; set; }

                public string CopyPasteFile { get; set; } = string.Empty;
                public string KitImage { get; set; } = string.Empty;

                public ItemData[] MainItems { get; set; } = new ItemData[0];
                public ItemData[] WearItems { get; set; } = new ItemData[0];
                public ItemData[] BeltItems { get; set; } = new ItemData[0];

                [JsonIgnore]
                internal int ItemCount => MainItems.Length + WearItems.Length + BeltItems.Length;

                [JsonIgnore]
                private JObject _jObject;

                [JsonIgnore]
                internal JObject ToJObject
                {
                    get
                    {
                        if (_jObject == null)
                        {
                            _jObject = new JObject
                            {
                                ["Name"] = Name,
                                ["Description"] = Description,
                                ["RequiredPermission"] = RequiredPermission,
                                ["MaximumUses"] = MaximumUses,
                                ["RequiredAuth"] = RequiredAuth,
                                ["Cooldown"] = Cooldown,
                                ["Cost"] = Cost,
                                ["IsHidden"] = IsHidden,
                                ["CopyPasteFile"] = CopyPasteFile,
                                ["KitImage"] = KitImage,
                                ["MainItems"] = new JArray(),
                                ["WearItems"] = new JArray(),
                                ["BeltItems"] = new JArray()
                            };

                            for (int i = 0; i < MainItems.Length; i++)
                                (_jObject["MainItems"] as JArray).Add(MainItems[i].ToJObject);

                            for (int i = 0; i < WearItems.Length; i++)
                                (_jObject["WearItems"] as JArray).Add(WearItems[i].ToJObject);

                            for (int i = 0; i < BeltItems.Length; i++)
                                (_jObject["BeltItems"] as JArray).Add(BeltItems[i].ToJObject);
                        }

                        return _jObject;
                    }
                }

                internal static Kit CloneOf(Kit other)
                {
                    Kit kit = new Kit();

                    kit.Name = other.Name;
                    kit.Description = other.Description;
                    kit.RequiredPermission = other.RequiredPermission;

                    kit.MaximumUses = other.MaximumUses;
                    kit.RequiredAuth = other.RequiredAuth;
                    kit.Cooldown = other.Cooldown;
                    kit.Cost = other.Cost;

                    kit.IsHidden = other.IsHidden;

                    kit.CopyPasteFile = other.CopyPasteFile;
                    kit.KitImage = other.KitImage;

                    kit.MainItems = new ItemData[other.MainItems.Length];
                    Array.Copy(other.MainItems, kit.MainItems, other.MainItems.Length);

                    kit.WearItems = new ItemData[other.WearItems.Length];
                    Array.Copy(other.WearItems, kit.WearItems, other.WearItems.Length);

                    kit.BeltItems = new ItemData[other.BeltItems.Length];
                    Array.Copy(other.BeltItems, kit.BeltItems, other.BeltItems.Length);

                    return kit;
                }

                internal bool HasSpaceForItems(BasePlayer player)
                {
                    int wearSpacesFree = 8 - player.inventory.containerWear.itemList.Count;
                    int mainSpacesFree = 24 - player.inventory.containerMain.itemList.Count;
                    int beltSpacesFree = 6 - player.inventory.containerBelt.itemList.Count;

                    return (wearSpacesFree >= WearItems.Length &&
                            beltSpacesFree >= BeltItems.Length &&
                            mainSpacesFree >= MainItems.Length) || ItemCount <= mainSpacesFree + beltSpacesFree;
                }

                internal void GiveItemsTo(BasePlayer player)
                {
                    List<ItemData> list = Pool.Get<List<ItemData>>();

                    GiveItems(player, MainItems, player.inventory.containerMain, ref list);
                    GiveItems(player, WearItems, player.inventory.containerWear, ref list, true);
                    GiveItems(player, BeltItems, player.inventory.containerBelt, ref list);

                    for (int i = 0; i < list.Count; i++)
                    {
                        Item item = CreateItem(list[i], player);
                        if (item == null) continue;

                        if (!MoveToIdealContainer(player.inventory, item) && !item.MoveToContainer(player.inventory.containerMain) && !item.MoveToContainer(player.inventory.containerBelt))
                            item.Drop(player.GetDropPosition(), player.GetDropVelocity());
                    }

                    Pool.FreeUnmanaged(ref list);
                }

                private void GiveItems(BasePlayer player, ItemData[] items, ItemContainer container, ref List<ItemData> leftOverItems, bool isWearContainer = false)
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        ItemData itemData = items[i];
                        if (itemData.Amount < 1)
                            continue;

                        if (container.GetSlot(itemData.Position) != null)
                            leftOverItems.Add(itemData);
                        else
                        {
                            Item item = CreateItem(itemData, player);
                            if (item == null) continue;
                            if (!isWearContainer || (item.info.isWearable && CanWearItem(container, item)))
                            {
                                item.position = itemData.Position;
                                item.SetParent(container);
                            }
                            else
                            {
                                leftOverItems.Add(itemData);
                                item.Remove();
                            }
                        }
                    }
                }

                internal IEnumerable<Item> CreateItems()
                {
                    for (int i = 0; i < MainItems.Length; i++)
                    {
                        ItemData itemData = MainItems[i];
                        if (itemData.Amount > 0)
                        {
                            Item item = CreateItem(itemData);
                            if (item != null) yield return item;
                        }
                    }

                    for (int i = 0; i < WearItems.Length; i++)
                    {
                        ItemData itemData = WearItems[i];
                        if (itemData.Amount > 0)
                        {
                            Item item = CreateItem(itemData);
                            if (item != null) yield return item;
                        }
                    }

                    for (int i = 0; i < BeltItems.Length; i++)
                    {
                        ItemData itemData = BeltItems[i];
                        if (itemData.Amount > 0)
                        {
                            Item item = CreateItem(itemData);
                            if (item != null) yield return item;
                        }
                    }
                }

                private bool MoveToIdealContainer(PlayerInventory playerInventory, Item item)
                {
                    if (item.info.isWearable && CanWearItem(playerInventory.containerWear, item))
                        return item.MoveToContainer(playerInventory.containerWear, -1, false);

                    if (item.info.stackable > 1)
                    {
                        if (playerInventory.containerBelt?.FindItemByItemID(item.info.itemid) != null)
                            return item.MoveToContainer(playerInventory.containerBelt);

                        if (playerInventory.containerMain?.FindItemByItemID(item.info.itemid) != null)
                            return item.MoveToContainer(playerInventory.containerMain);
                    }
                    if (item.info.HasFlag(ItemDefinition.Flag.NotStraightToBelt) || !item.info.isUsable)
                        return item.MoveToContainer(playerInventory.containerMain);

                    return item.MoveToContainer(playerInventory.containerBelt, -1, false);
                }

                private bool CanWearItem(ItemContainer containerWear, Item item)
                {
                    ItemModWearable itemModWearable = item.info.GetComponent<ItemModWearable>();
                    if (!itemModWearable)
                        return false;

                    for (int i = 0; i < containerWear.itemList.Count; i++)
                    {
                        Item otherItem = containerWear.itemList[i];
                        if (otherItem != null)
                        {
                            ItemModWearable otherModWearable = otherItem.info.GetComponent<ItemModWearable>();
                            if (otherModWearable && !itemModWearable.CanExistWith(otherModWearable))
                                return false;
                        }
                    }

                    return true;
                }

                internal void CopyItemsFrom(BasePlayer player)
                {
                    ItemData[] array = MainItems;
                    CopyItems(ref array, player.inventory.containerMain, 24);
                    MainItems = array;

                    array = WearItems;
                    CopyItems(ref array, player.inventory.containerWear, 8);
                    WearItems = array;

                    array = BeltItems;
                    CopyItems(ref array, player.inventory.containerBelt, 6);
                    BeltItems = array;
                }

                private void CopyItems(ref ItemData[] array, ItemContainer container, int limit)
                {
                    limit = Mathf.Min(container.itemList.Count, limit);

                    array = new ItemData[limit];

                    for (int i = 0; i < limit; i++)
                        array[i] = new ItemData(container.itemList[i]);
                }

                internal void ClearItems()
                {
                    MainItems = Array.Empty<ItemData>();
                    WearItems = Array.Empty<ItemData>();
                    BeltItems = Array.Empty<ItemData>();
                }
            }
        }

        private class PlayerData
        {
            [JsonProperty]
            private Dictionary<ulong, PlayerUsageData> _players = new Dictionary<ulong, PlayerUsageData>();

            internal bool Find(ulong playerId, out PlayerUsageData playerUsageData) => _players.TryGetValue(playerId, out playerUsageData);

            internal bool Exists(ulong playerId) => _players.ContainsKey(playerId);

            internal PlayerUsageData this[ulong key]
            {
                get
                {
                    if (_players.TryGetValue(key, out PlayerUsageData tValue))
                        return tValue;

                    tValue = (PlayerUsageData)Activator.CreateInstance(typeof(PlayerUsageData));
                    _players.Add(key, tValue);
                    return tValue;
                }
                set
                {
                    if (value == null)
                    {
                        _players.Remove(key);
                        return;
                    }
                    _players[key] = value;
                }
            }

            internal void OnKitClaimed(ulong playerId, KitData.Kit kit)
            {
                if (kit.MaximumUses == 0 && kit.Cooldown == 0)
                    return;

                if (!_players.TryGetValue(playerId, out PlayerUsageData playerUsageData))
                    playerUsageData = _players[playerId] = new PlayerUsageData();

                playerUsageData.OnKitClaimed(kit);
            }

            internal bool ClearKitUses(ulong playerId, KitData.Kit kit)
            {
                if (!_players.TryGetValue(playerId, out PlayerUsageData playerUsageData))
                    return false;

                playerUsageData.ClearKitUses(kit.Name);
                return true;
            }

            internal void Wipe() => _players.Clear();

            internal bool IsValid => _players != null;

            public class PlayerUsageData
            {
                [JsonProperty]
                private Hash<string, KitUsageData> _usageData = new Hash<string, KitUsageData>();

                public bool ClaimAutoKits { get; set; } = true;

                internal double GetCooldownRemaining(string name)
                {
                    if (!_usageData.TryGetValue(name, out KitUsageData kitUsageData))
                        return 0;

                    double currentTime = CurrentTime;

                    return currentTime > kitUsageData.NextUseTime ? 0 : kitUsageData.NextUseTime - CurrentTime;
                }

                internal void SetCooldownRemaining(string name, double seconds)
                {
                    if (!_usageData.TryGetValue(name, out KitUsageData kitUsageData))
                        return;

                    kitUsageData.NextUseTime = CurrentTime + seconds;
                }

                internal int GetKitUses(string name)
                {
                    if (!_usageData.TryGetValue(name, out KitUsageData kitUsageData))
                        return 0;

                    return kitUsageData.TotalUses;
                }

                internal void SetKitUses(string name, int amount)
                {
                    if (!_usageData.TryGetValue(name, out KitUsageData kitUsageData))
                        return;

                    kitUsageData.TotalUses = amount;
                }

                internal void ClearKitUses(string name)
                {
                    if (!_usageData.TryGetValue(name, out KitUsageData kitUsageData))
                        return;

                    kitUsageData.TotalUses = 0;
                    kitUsageData.NextUseTime = 0;
                }

                internal void OnKitClaimed(KitData.Kit kit)
                {
                    if (!_usageData.TryGetValue(kit.Name, out KitUsageData kitUsageData))
                        kitUsageData = _usageData[kit.Name] = new KitUsageData();

                    kitUsageData.OnKitClaimed(kit.Cooldown);
                }

                public class KitUsageData
                {
                    public int TotalUses { get; set; }

                    public double NextUseTime { get; set; }

                    internal void OnKitClaimed(int cooldownSeconds)
                    {
                        TotalUses += 1;
                        NextUseTime = CurrentTime + cooldownSeconds;
                    }
                }
            }
        }

        #endregion

        #region Сериализация предметов (порт из Kits)

        private static Item CreateItem(ItemData itemData, BasePlayer player = null)
        {
            int itemId = itemData.ItemID;
            if (itemId <= 0)
            {
                Debug.LogWarning($"[OctaKits] Предмет '{itemData.Shortname}' не найден в ItemDictionary — пропускаем");
                return null;
            }

            ulong skin = itemData.Skin;

            if (player)
            {
                itemId = _getRedirectedIfNotOwned(player, itemData.ItemID);
                skin = _canUseSkin(player, itemData.Skin) ? itemData.Skin : 0UL;
            }

            Item item = ItemManager.CreateByItemID(itemId, itemData.Amount, skin);

            if (!string.IsNullOrEmpty(itemData.DisplayName))
                item.name = itemData.DisplayName;

            if (!string.IsNullOrEmpty(itemData.Text))
                item.text = itemData.Text;

            // Прочность применяем только если она задана в данных набора (MaxCondition > 0);
            // иначе непрописанные в редакторе значения 0/0 выдавали бы сломанные предметы
            if (itemData.MaxCondition > 0f && item.info.condition.enabled)
            {
                item.maxCondition = itemData.MaxCondition;
                item.condition = itemData.Condition > 0f ? itemData.Condition : itemData.MaxCondition;
            }

            if (itemData.Frequency > 0)
            {
                ItemModRFListener rfListener = item.info.GetComponentInChildren<ItemModRFListener>();
                if (rfListener)
                    (BaseNetworkable.serverEntities.Find(item.instanceData.subEntity) as PagerEntity)?.ChangeFrequency(itemData.Frequency);
            }

            if (itemData.BlueprintItemID > 0)
            {
                if (item.instanceData == null)
                    item.instanceData = new ProtoBuf.Item.InstanceData();

                item.instanceData.ShouldPool = false;

                item.instanceData.blueprintAmount = 1;
                item.instanceData.blueprintTarget = itemData.BlueprintItemID;

                item.MarkDirty();
            }

            FlameThrower flameThrower = item.GetHeldEntity() as FlameThrower;
            if (flameThrower)
                flameThrower.ammo = itemData.Ammo;

            if (itemData.Contents != null)
            {
                foreach (ItemData contentData in itemData.Contents)
                {
                    Item newContent = CreateItem(contentData, player);
                    if (newContent != null)
                    {
                        if (!newContent.MoveToContainer(item.contents))
                            newContent.Remove();
                    }
                }
            }

            if (itemData.Container != null)
            {
                if (item.contents == null)
                {
                    ItemModContainerArmorSlot armorSlot = FindItemMod<ItemModContainerArmorSlot>(item);
                    if (armorSlot)
                        armorSlot.CreateAtCapacity(itemData.Container.slots, item);
                    else
                    {
                        item.contents = Pool.Get<ItemContainer>();
                        item.contents.ServerInitialize(item, itemData.Container.slots);
                        item.contents.GiveUID();
                    }
                }
                itemData.Container.Load(item.contents);
            }

            BaseProjectile weapon = item.GetHeldEntity() as BaseProjectile;
            if (weapon)
            {
                weapon.DelayedModsChanged();

                if (!string.IsNullOrEmpty(itemData.Ammotype))
                    weapon.primaryMagazine.ammoType = ItemManager.FindItemDefinition(itemData.Ammotype);
                weapon.primaryMagazine.contents = itemData.Ammo;
            }

            item.MarkDirty();

            return item;
        }

        private static T FindItemMod<T>(Item item) where T : ItemMod
        {
            foreach (ItemMod itemMod in item.info.itemMods)
            {
                if (itemMod is T mod)
                    return mod;
            }

            return null;
        }

        // availableSlots — приватное поле ItemContainer в стоковых сборках; Carbon его
        // паблицит, но чтобы плагин собирался и без паблицинга — обходим рефлексией
        private static readonly FieldInfo _availableSlotsField =
            typeof(global::ItemContainer).GetField("availableSlots", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

        private static System.Collections.IList GetAvailableSlots(global::ItemContainer container)
            => _availableSlotsField?.GetValue(container) as System.Collections.IList;

        public class ItemData
        {
            public string Shortname { get; set; }

            public string DisplayName { get; set; }

            public ulong Skin { get; set; }

            public int Amount { get; set; }

            public float Condition { get; set; }

            public float MaxCondition { get; set; }

            public int Ammo { get; set; }

            public string Ammotype { get; set; }

            public int Position { get; set; }

            public int Frequency { get; set; }

            public string BlueprintShortname { get; set; }

            public string Text { get; set; }

            public ItemData[] Contents { get; set; }

            public ItemContainer Container { get; set; }


            [JsonIgnore]
            private int _itemId;

            [JsonIgnore]
            private int _blueprintItemId;

            [JsonIgnore]
            private JObject _jObject;


            [JsonIgnore]
            internal int ItemID
            {
                get
                {
                    if (_itemId == 0)
                    {
                        // устаревший/переименованный shortname не должен ронять выдачу всего набора
                        ItemDefinition def = null;
                        if (!string.IsNullOrEmpty(Shortname))
                            ItemManager.itemDictionaryByName.TryGetValue(Shortname, out def);
                        _itemId = def != null ? def.itemid : -1;
                    }
                    return _itemId;
                }
            }

            [JsonIgnore]
            internal bool IsBlueprint => !string.IsNullOrEmpty(Shortname) && Shortname.Equals(BLUEPRINT_BASE);

            [JsonIgnore]
            internal int BlueprintItemID
            {
                get
                {
                    if (_blueprintItemId == 0 && !string.IsNullOrEmpty(BlueprintShortname))
                    {
                        ItemDefinition def = null;
                        ItemManager.itemDictionaryByName.TryGetValue(BlueprintShortname, out def);
                        _blueprintItemId = def != null ? def.itemid : -1;
                    }
                    return _blueprintItemId;
                }
            }

            [JsonIgnore]
            internal JObject ToJObject
            {
                get
                {
                    if (_jObject == null)
                    {
                        _jObject = new JObject
                        {
                            ["Shortname"] = Shortname,
                            ["DisplayName"] = DisplayName,
                            ["SkinID"] = Skin,
                            ["Amount"] = Amount,
                            ["Condition"] = Condition,
                            ["MaxCondition"] = MaxCondition,
                            ["IsBlueprint"] = BlueprintItemID != 0,
                            ["Ammo"] = Ammo,
                            ["AmmoType"] = Ammotype,
                            ["Text"] = Text,
                            ["Contents"] = new JArray()
                        };

                        for (int i = 0; i < Contents?.Length; i++)
                            (_jObject["Contents"] as JArray).Add(Contents[i].ToJObject);

                        if (Container != null && Container.contents != null && Container.contents.Count > 0)
                        {
                            for (int i = 0; i < Container.contents.Count; i++)
                                (_jObject["Contents"] as JArray).Add(Container.contents[i].ToJObject);
                        }
                    }

                    return _jObject;
                }
            }

            internal ItemData()
            {
            }

            internal ItemData(Item item)
            {
                Shortname = item.info.shortname;
                Amount = item.amount;
                DisplayName = item.name;
                Text = item.text;

                BaseEntity heldEntity = item.GetHeldEntity();
                if (heldEntity)
                {
                    Ammotype = heldEntity is BaseProjectile projectile ? projectile.primaryMagazine.ammoType.shortname : null;
                    Ammo = heldEntity is BaseProjectile baseProjectile ? baseProjectile.primaryMagazine.contents :
                        heldEntity is FlameThrower thrower ? thrower.ammo : 0;
                }

                Position = item.position;
                Skin = item.skin;

                Condition = item.condition;
                MaxCondition = item.maxCondition;

                Frequency = ItemModAssociatedEntity<PagerEntity>.GetAssociatedEntity(item)?.GetFrequency() ?? -1;

                if (item.instanceData != null && item.instanceData.blueprintTarget != 0)
                    BlueprintShortname = ItemManager.FindItemDefinition(item.instanceData.blueprintTarget).shortname;

                if (item.contents != null)
                    Container = new ItemContainer(item.contents);
            }

            public class ItemContainer
            {
                public int slots;
                public float temperature;
                public int flags;
                public int allowedContents;
                public int maxStackSize;
                public List<int> allowedItems;
                public List<int> availableSlots;
                public int volume;
                public List<ItemData> contents;

                public ItemContainer() { }

                public ItemContainer(global::ItemContainer container)
                {
                    contents = new List<ItemData>();
                    slots = container.capacity;
                    temperature = container.temperature;
                    allowedContents = (int)container.allowedContents;

                    if (container.HasLimitedAllowedItems)
                    {
                        allowedItems = new List<int>();
                        for (int i = 0; i < container.onlyAllowedItems.Length; i++)
                        {
                            if (container.onlyAllowedItems[i])
                            {
                                allowedItems.Add(container.onlyAllowedItems[i].itemid);
                            }
                        }
                    }

                    flags = (int)container.flags;
                    maxStackSize = container.maxStackSize;
                    volume = container.containerVolume;

                    var srcSlots = GetAvailableSlots(container);
                    if (srcSlots is { Count: > 0 })
                    {
                        availableSlots = new List<int>();
                        for (int j = 0; j < srcSlots.Count; j++)
                        {
                            availableSlots.Add((int)srcSlots[j]);
                        }
                    }

                    for (int k = 0; k < container.itemList.Count; k++)
                    {
                        Item item = container.itemList[k];
                        if (item.IsValid())
                        {
                            contents.Add(new ItemData(item));
                        }
                    }
                }

                public void Load(global::ItemContainer itemContainer)
                {
                    itemContainer.capacity = slots;
                    itemContainer.itemList = Pool.Get<List<Item>>();
                    itemContainer.temperature = temperature;
                    itemContainer.flags = (global::ItemContainer.Flag)flags;
                    itemContainer.allowedContents = (global::ItemContainer.ContentsType)((allowedContents == 0) ? 1 : allowedContents);

                    if (allowedItems is { Count: > 0 })
                    {
                        itemContainer.onlyAllowedItems = new ItemDefinition[allowedItems.Count];
                        for (int i = 0; i < allowedItems.Count; i++)
                        {
                            itemContainer.onlyAllowedItems[i] = ItemManager.FindItemDefinition(allowedItems[i]);
                        }
                    }
                    else
                    {
                        itemContainer.onlyAllowedItems = null;
                    }

                    itemContainer.maxStackSize = maxStackSize;
                    itemContainer.containerVolume = volume;

                    var dstSlots = GetAvailableSlots(itemContainer);
                    if (dstSlots != null)
                    {
                        dstSlots.Clear();

                        if (availableSlots != null)
                        {
                            for (int j = 0; j < availableSlots.Count; j++)
                            {
                                dstSlots.Add((ItemSlot)availableSlots[j]);
                            }
                        }
                    }

                    foreach (ItemData itemData in contents)
                    {
                        Item item = CreateItem(itemData);
                        if (item == null)
                            continue;

                        if (!item.MoveToContainer(itemContainer, itemData.Position) && !item.MoveToContainer(itemContainer))
                            item.Remove();
                    }

                    itemContainer.MarkDirty();
                }
            }
        }

        #endregion
    }
}