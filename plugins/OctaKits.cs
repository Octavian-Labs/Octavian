using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using UnityEngine;
using Oxide.Game.Rust.Cui;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Network;

namespace Oxide.Plugins
{
    [Info("OctaKits", "Octavian", "1.1.0")]
    [Description("Интерфейс наборов Octavian поверх плагина Kits — стиль золото на стекле")]
    public class OctaKits : RustPlugin
    {   
        [PluginReference] Plugin ImageLibrary, Kits;

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
        private bool isInitialized = false;

        private void Unload()
        {
            foreach (var t in _cdTimers.Values) t?.Destroy();
            _cdTimers.Clear();
            foreach (var p in BasePlayer.activePlayerList)
            {
                CloseKits_API(p);
                CuiHelper.DestroyUi(p, "octakits_background");
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

        void OnServerInitialized()
        {
            LoadData();

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

            // Асинхронная инициализация китов с задержкой
            timer.Once(3f, () => InitializeKits());
        }

        private void InitializeKits()
        {
            if (config?.debugMode == true) PrintWarning("[OctaKits DEBUG] Starting InitializeKits");

            if (Kits == null)
            {
                PrintWarning("Плагин Kits не найден — OctaKits не будет работать корректно");
                if (config?.debugMode == true) PrintWarning("[OctaKits DEBUG] Kits plugin reference is null");
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
                string[] allKits = (string[])Kits.Call("GetAllKits");
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

                    // Безопасное получение данных кита с обработкой типов
                    object imageResult = Kits.Call("GetKitImage", kitName);
                    cache.ImageUrl = imageResult is string ? (string)imageResult : "";

                    object descResult = Kits.Call("GetKitDescription", kitName);
                    cache.Description = descResult is string ? (string)descResult : "";

                    object maxUsesResult = Kits.Call("GetKitMaxUses", kitName);
                    cache.MaxUses = maxUsesResult is int ? (int)maxUsesResult : 0;

                    object cooldownResult = Kits.Call("GetKitCooldown", kitName);
                    cache.Cooldown = cooldownResult != null ? Convert.ToSingle(cooldownResult) : 0f;

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

            if(Kits != null) 
            {
                string[] allKits = (string[]) Kits.Call("GetAllKits");
                foreach (string kit in allKits)
                {   
                    string url = (string) Kits.Call("GetKitImage", kit);
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
            "0.01 0.72-0.32 0.95",   // Кит 1 (верхний левый)
            "0.34 0.72-0.65 0.95",   // Кит 2 (верхний средний)
            "0.67 0.72-0.98 0.95",   // Кит 3 (верхний правый)

            // Средний ряд
            "0.01 0.39-0.32 0.62",   // Кит 4 (средний левый)
            "0.34 0.39-0.65 0.62",   // Кит 5 (средний средний)
            "0.67 0.39-0.98 0.62",   // Кит 6 (средний правый)

            // Нижний ряд
            "0.01 0.06-0.32 0.29",   // Кит 7 (нижний левый)
            "0.34 0.06-0.65 0.29",   // Кит 8 (нижний средний)
            "0.67 0.06-0.98 0.29",   // Кит 9 (нижний правый)
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


        private void ShowKits(BasePlayer player, int page = 0)
        {
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] ShowKits called for player {player.displayName} (ID: {player.UserIDString}), page: {page}");

            if (!isInitialized)
            {
                PrintWarning("OctaKits ещё не инициализирован, подождите...");
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] ShowKits called but isInitialized is false");
                player.ChatMessage("<color=#E8B84B>OctaKits</color>: интерфейс ещё загружается, попробуйте через мгновение...");
                return;
            }

            if (Kits == null)
            {
                PrintWarning("Плагин Kits недоступен");
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] ShowKits called but Kits plugin is null");
                player.ChatMessage("<color=#E8B84B>OctaKits</color>: система наборов сейчас недоступна");
                return;
            }

            var ui =  new CuiElementContainer();
            var kitList = addonsData.ContainsKey("addons") ? addonsData["addons"].importedKits : null;
            if (config?.debugMode == true)
            {
                PrintWarning($"[OctaKits DEBUG] Kit list from addonsData: {kitList?.Count ?? 0} kits");
                if (kitList != null) PrintWarning($"[OctaKits DEBUG] Imported kits: {string.Join(", ", kitList)}");
            }

            if (kitList == null || kitList.Count == 0)
            {
                PrintWarning($"Kit list is empty or null. addonsData contains 'addons': {addonsData.ContainsKey("addons")}");
                if (config?.debugMode == true)
                {
                    PrintWarning($"[OctaKits DEBUG] Kit list is empty, showing error message");
                    PrintWarning($"[OctaKits DEBUG] addonsData keys: {string.Join(", ", addonsData.Keys)}");
                    if (addonsData.ContainsKey("addons"))
                    {
                        PrintWarning($"[OctaKits DEBUG] Imported kits count: {addonsData["addons"].importedKits?.Count ?? 0}");
                    }
                }
                CuiHelper.DestroyUi(player, "errortext");
                CUIClass.CreateText(ref ui, "errortext", "imagePanel", "1 1 1 0.6", "Список импортированных наборов пуст. \nДобавьте наборы командой <color=#ffffff>/octa_importkits</color>.", 18, "0.0 0.00", "1 1", TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", 0.0f);
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

                        // Безопасное получение данных кита
                        try
                        {
                            object imageResult = Kits?.Call("GetKitImage", kitname);
                            kitInfo.ImageUrl = imageResult is string ? (string)imageResult : "";
                        }
                        catch (Exception ex)
                        {
                            PrintWarning($"Error getting kit image for '{kitname}': {ex.Message}");
                            kitInfo.ImageUrl = "";
                        }

                        try
                        {
                            object descResult = Kits?.Call("GetKitDescription", kitname);
                            kitInfo.Description = descResult is string ? (string)descResult : "";
                        }
                        catch (Exception ex)
                        {
                            PrintWarning($"Error getting kit description for '{kitname}': {ex.Message}");
                            kitInfo.Description = "";
                        }

                        try
                        {
                            object maxUsesResult = Kits?.Call("GetKitMaxUses", kitname);
                            kitInfo.MaxUses = maxUsesResult is int ? (int)maxUsesResult : 0;
                        }
                        catch (Exception ex)
                        {
                            PrintWarning($"Error getting kit max uses for '{kitname}': {ex.Message}");
                            kitInfo.MaxUses = 0;
                        }

                        try
                        {
                            object cooldownResult = Kits?.Call("GetKitCooldown", kitname);
                            kitInfo.Cooldown = cooldownResult != null ? Convert.ToSingle(cooldownResult) : 0f;
                        }
                        catch (Exception ex)
                        {
                            PrintWarning($"Error getting kit cooldown for '{kitname}': {ex.Message}");
                            kitInfo.Cooldown = 0f;
                        }

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
                        var ko = Kits?.Call("GetKitObject", kitname) as JObject;
                        var reqPerm = ko?.SelectToken("RequiredPermission")?.ToString() ?? "";
                        kitInfo.HasPermission = string.IsNullOrEmpty(reqPerm) || permission.UserHasPermission(player.UserIDString, reqPerm);
                    }
                    catch (Exception ex)
                    {
                        PrintWarning($"Error updating permissions for kit '{kitname}': {ex.Message}");
                        kitInfo.HasPermission = false;
                    }
                }

                // Безопасная проверка существования кита
                bool isKitExists = false;
                try
                {
                    object isKitResult = Kits?.Call("IsKit", kitname);
                    isKitExists = isKitResult is bool ? (bool)isKitResult : false;
                }
                catch (Exception ex)
                {
                    PrintWarning($"Error checking if kit exists '{kitname}': {ex.Message}");
                    isKitExists = false;
                }

                if (!isKitExists)
                {
                    if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit '{kitname}' does not exist");
                    CUIClass.CreateText(ref ui, $"kitpanel_{i}", "imagePanel", "1 1 1 0.6", $"Набор <b>'{kitname}'</b> не существует. Удалите его из списка в 'carbon/data/OctaKitsData.json'.", 10, "0.0 0.00", "1 1", TextAnchor.MiddleCenter, $"robotocondensed-regular.ttf", 0.0f);
                    continue;
                }

                string kitDescr = kitInfo.Description;
                if (kitDescr.Length > 60) kitDescr = kitDescr.Substring(0, 60) + "...";

                string usesleft = gl("uses_title_unlimited");
                if (kitInfo.MaxUses != 0)
                {
                    try
                    {
                        // Безопасный вызов метода получения использований кита
                        object usesResult = Kits.Call("GetPlayerKitUses", player.UserIDString, kitname);
                        int usedCount = usesResult is int ? (int)usesResult : 0;
                        int remaining = Math.Max(0, kitInfo.MaxUses - usedCount);
                        usesleft = remaining.ToString();
                    }
                    catch (Exception ex)
                    {
                        PrintWarning($"Error getting kit uses for '{kitname}' player '{player.UserIDString}': {ex.Message}");
                        usesleft = kitInfo.MaxUses.ToString();
                    }
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

                CUIClass.CreatePanel(ref ui, $"kitpanel_{i}", "imagePanel", config?.ui?.colors?["Дополнительная панель"] ?? GlassBg, _anchors[0], _anchors[1], false, 0.0f, 0f, "assets/icons/iconmaterial.mat", "0 0", "0 0");
                    // Верхняя золотая полоска карточки
                    CUIClass.CreatePanel(ref ui, $"kit_goldline_{i}", $"kitpanel_{i}", Gold, "0 0.97", "1 1.0", false, 0f, 0f, "assets/icons/iconmaterial.mat");
                    //logo
                    CUIClass.CreatePanel(ref ui, $"kit_icon_panel_{i}", $"kitpanel_{i}", config?.ui?.colors?["Основная панель"] ?? GlassRow, "0.03 0.33", "0.32 0.92", false, 0.0f, 0f, "assets/icons/iconmaterial.mat");
                        string imageUrl = Img(kitInfo?.ImageUrl ?? "");
                        if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit '{kitname}' image URL: '{kitInfo?.ImageUrl ?? "NULL"}'");
                        if (!string.IsNullOrEmpty(imageUrl))
                            CUIClass.CreateImage(ref ui, "img", $"kit_icon_panel_{i}", imageUrl, "0.06 0.06", "0.94 0.94", 0.2f);
                    //title
                    CUIClass.CreatePanel(ref ui, $"kit_title_panel_{i}", $"kitpanel_{i}", "0 0 0 0", "0.35 0.33", "0.97 0.92", false, 0.0f, 0f, "assets/icons/iconmaterial.mat");
                        string titleFormatted = $"<color=#FFD35C><b>{kitname.ToUpper()}</b></color>\n" +
                                                $"<size=10><color=#9E9482>{kitDescr}</color></size>\n\n" +
                                                $"<size=10><color=#8E8777>КД:</color> <color=#FFD35C>{cooldown}</color>   <color=#8E8777>Лимит:</color> <color=#FFD35C>{usesleft}</color></size>";
                        CUIClass.CreateText(ref ui, "kitname", $"kit_title_panel_{i}", TWhite, titleFormatted, 13, "0.0 0.0", "1 1", TextAnchor.UpperLeft, "robotocondensed-bold.ttf", 0.0f);

                    //view button
                    CUIClass.CreateButton(ref ui, $"kit_viewbtn_{i}", $"kitpanel_{i}", GlassHov, "<color=#FFD35C>ОБЗОР</color>", 11, "0.03 0.05", "0.32 0.26", $"octakits_viewkit {kitname.Replace(" ", "%")} {page}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");

                    string[] btn_props = Button_Cooldown_Text(player, kitname).Split('%');
                    if (btn_props.Length >= 2)
                    {
                        CUIClass.CreateButton(ref ui, $"kit_claimbtn_{i}", $"kitpanel_{i}", btn_props[1], $"<b>{btn_props[0]}</b>", 11, "0.35 0.05", "0.97 0.26", $"octakits_try_claim {kitname.Replace(" ", "%")} 0 {i}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                    }
                    else
                    {
                        PrintWarning($"Invalid button properties for kit '{kitname}': {Button_Cooldown_Text(player, kitname)}");
                    }
                }
            
            for (var i = 0; i < 9; i++)
            {
                CuiHelper.DestroyUi(player, $"kitpanel_{i}");
                CuiHelper.DestroyUi(player, $"kit_goldline_{i}");
            }

            CuiHelper.DestroyUi(player, "octakits_btn_next");
            CuiHelper.DestroyUi(player, "octakits_btn_back");

            if (page > 0) {
                CUIClass.CreateButton(ref ui, "octakits_btn_back", "imagePanel", GlassRow, "<size=22><color=#FFD35C>‹</color></size>", 14, "0.02 0.45", "0.06 0.55", $"octakits_page {page - 1}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
            }

            if ((double) page + 1 < ((double) kitList.Count / 9)) {
                CUIClass.CreateButton(ref ui, "octakits_btn_next", "imagePanel", GlassRow, "<size=22><color=#FFD35C>›</color></size>", 14, "0.94 0.45", "0.98 0.55", $"octakits_page {page + 1}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
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
                    CUIClass.CreateButton(ref ui, $"kit_claimbtn_{i}", $"kitpanel_{i}", btn_props[1], $"<b>{btn_props[0]}</b>", 11, "0.35 0.05", "0.97 0.26", $"octakits_try_claim {kitname.Replace(" ", "%")} 0 {i}", "", TWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
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

            JObject kitObj = (JObject) Kits.Call("GetKitObject", $"{kitname}");
            if (kitObj == null)
            {
                if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] Kit object is null for kit '{kitname}'");
                CUIClass.CreateText(ref ui, "errortext", "kits_content", "1 1 1 0.6", $"Набор '{kitname}' не существует.", 18, "0.0 0.00", "1 1", TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", 0.0f);  
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

            CUIClass.CreatePanel(ref ui, "octakits_viewkit_inventory", "imagePanel", GlassBg, "0.37 0.04", "0.99 0.96", false, 0.0f, 0f, "assets/icons/iconmaterial.mat");
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
            
            
            CUIClass.CreatePanel(ref ui, "octakits_viewkit_info", "imagePanel", GlassBg, "0.01 0.04", "0.35 0.96", false, 0.0f, 0f, "assets/icons/iconmaterial.mat");
                // Верхняя золотая полоска
                CUIClass.CreatePanel(ref ui, "octakits_v_goldline", "octakits_viewkit_info", Gold, "0 0.985", "1 1.0", false, 0f, 0f, "assets/icons/iconmaterial.mat");
                CUIClass.CreateText(ref ui, "kit_title", "octakits_viewkit_info", GoldBright, $"◆ <b>{kitname.ToUpper()}</b>", 18, "0.06 0.91", "0.94 0.97", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", 0.0f);  
                CUIClass.CreatePanel(ref ui, "octakits_v_div1", "octakits_viewkit_info", GoldDark, "0.06 0.895", "0.94 0.898", false, 0f, 0f, "assets/icons/iconmaterial.mat");
                
                string cooldown = gl("cooldown_title_none");
                if ((int) Kits.Call<int>("GetKitCooldown", kitname) != 0)
                    cooldown = $"{TimeFormated(Convert.ToDouble(Kits.Call<int>("GetKitCooldown", kitname)))}";
                
                string usesleft = gl("uses_title_unlimited");
                if ((int) Kits.Call<int>("GetKitMaxUses", kitname) != 0)
                    usesleft = $"{(int) Kits.Call("GetKitMaxUses", kitname) - (int) Kits.Call("GetPlayerKitUses", player.userID, kitname)}";
                
                string cost = gl("price_title_free");
                if(Convert.ToInt32(kitObj.SelectToken("Cost")) != 0)
                    cost = $"{kitObj.SelectToken("Cost")} {addonsData?["addons"]?.RustKits_currencyName ?? "$"}";
                
                string permReq = (string)kitObj.SelectToken("RequiredPermission");
                string permStatus = string.IsNullOrEmpty(permReq) ? "<color=#A3E06C>Разрешён</color>" : 
                                    (permission.UserHasPermission(player.UserIDString, permReq) ? "<color=#A3E06C>Разрешён</color>" : "<color=#E05A47>Требуется привилегия</color>");

                string infoText = $"<color=#8E8777>• Кулдаун:</color>  <color=#FFD35C>{cooldown}</color>\n" +
                                  $"<color=#8E8777>• Доступно:</color>  <color=#FFD35C>{usesleft}</color>\n" +
                                  $"<color=#8E8777>• Стоимость:</color> <color=#FFD35C>{cost}</color>\n" +
                                  $"<color=#8E8777>• Статус:</color>    {permStatus}";

                CUIClass.CreateText(ref ui, "kit_info_block", "octakits_viewkit_info", TWhite, infoText, 12, "0.06 0.65", "0.94 0.88", TextAnchor.UpperLeft, "robotocondensed-bold.ttf", 0.0f);
                CUIClass.CreatePanel(ref ui, "octakits_v_div2", "octakits_viewkit_info", GoldDark, "0.06 0.635", "0.94 0.638", false, 0f, 0f, "assets/icons/iconmaterial.mat");

                string descText = (string)Kits.Call("GetKitDescription", kitname);
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

            for (var i = 0; i < 9; i++)
                CuiHelper.DestroyUi(player, $"kitpanel_{i}");
            CuiHelper.DestroyUi(player, "octakits_viewkit_inventory"); 
            CuiHelper.DestroyUi(player, "octakits_viewkit_info"); 
            CuiHelper.AddUi(player, ui); 
            if (config?.debugMode == true) PrintWarning($"[OctaKits DEBUG] ViewKit completed successfully for kit '{kitname}' by player {player.displayName}");
        }

        private void KitsImportCui(BasePlayer player, int page = 0)
        {   
            string[] allKits = (string[]) Kits.Call("GetAllKits");
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
                CUIClass.CreateImage(ref ui2, "img", $"octakits_kit_FI{index}", $"{Img((string)Kits.Call("GetKitImage", kitname))}", "0.02 0.12", "0.27 0.95", 0.5f);
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
            if (Kits == null)
            {
                return gl("no_perms_button") + $"%{config?.ui?.colors?["Кнопка без доступа"] ?? "0.14 0.14 0.14 0.9"}";
            }

            try
            {
                var kit = Kits.Call("GetKitObject", kitname) as JObject;
                if (kit == null)
                {
                    PrintWarning($"Failed to get kit object for '{kitname}'");
                    return gl("no_perms_button") + $"%{config?.ui?.colors?["Кнопка без доступа"] ?? "0.14 0.14 0.14 0.9"}";
                }

                object costToken = kit.SelectToken("Cost");
                int cost = costToken != null ? Convert.ToInt32(costToken) : 0;

                object permToken = kit.SelectToken("RequiredPermission");
                string perm = permToken as string ?? "";

                object maxUsesResult = Kits?.Call("GetKitMaxUses", kitname);
                int kitUseLimit = maxUsesResult is int ? (int)maxUsesResult : 0;

                object timesUsedResult = Kits?.Call("GetPlayerKitUses", player.userID, kitname);
                int kitTimesUsed = timesUsedResult is int ? (int)timesUsedResult : 0;

                if (IsKitOnCd(player, kitname))
                {
                    return gl("on_cooldown_button").Replace("{kit_cooldown}", $"{GetCurrentKitCd(player, kitname)}") + $"%{config?.ui?.colors?["Кнопка перезарядки"] ?? CRed}";
                }
                else
                {
                    if (!string.IsNullOrEmpty(perm))
                    {
                        if (!permission.UserHasPermission(player.UserIDString, perm))
                            return gl("no_perms_button") + $"%{config?.ui?.colors?["Кнопка без доступа"] ?? GlassLt}";
                    }

                    if (kitUseLimit != 0)
                    {
                        if (kitUseLimit - kitTimesUsed < 1)
                            return gl("no_uses_button") + $"%{config?.ui?.colors?["Кнопка без использований"] ?? GlassLt}";
                    }

                    if (cost != 0)
                        return $"{cost} {addonsData?["addons"]?.RustKits_currencyName ?? "$"}" + $"%{config?.ui?.colors?["Кнопка забрать"] ?? CGreen}";
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
            int _kitMaxUses = Convert.ToInt32(Kits?.CallHook("GetKitMaxUses", _kitName));
            int _kitTimesUsed = Convert.ToInt32(Kits?.CallHook("GetPlayerKitUses", player.userID, _kitName));
            string _kitUsesLeft = Convert.ToString(_kitMaxUses - _kitTimesUsed);
            return _kitUsesLeft;
        }

        private bool IsKitOnCd(BasePlayer player, string _kitName) 
        {
            double _currentCd = Convert.ToDouble(Kits?.CallHook("GetPlayerKitCooldown", player.userID, _kitName));
            if ( _currentCd == 0)
            {
                return false;
            }
            return true;
        }

        private string GetKitCd(string _kitName) 
        {
            int _kitCdInt = Convert.ToInt32(Kits?.Call<int>("GetKitCooldown", _kitName)); 
        
            TimeSpan cooldownTS = TimeSpan.FromSeconds(_kitCdInt); 
            string cooldownFormated = string.Format("{0:D1}D {1:D2}:{2:D2}:{3:D2}", cooldownTS.Days, cooldownTS.Hours, cooldownTS.Minutes, cooldownTS.Seconds);
            if (_kitCdInt < 86400) cooldownFormated = string.Format("{0:D2}:{1:D2}:{2:D2}", cooldownTS.Hours, cooldownTS.Minutes, cooldownTS.Seconds);
            
            string _kitCd = $"{cooldownFormated}";
            return _kitCd; 
        }

        private string GetCurrentKitCd(BasePlayer player, string _kitName) 
        { 
            int _currentCdInt = Convert.ToInt32(Kits?.CallHook("GetPlayerKitCooldown", player.userID, _kitName));
            
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

            if ((bool) Kits.Call("TryClaimKit", player, kitname, false))
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
                    CUIClass.CreateButton(ref ui, $"kit_claimbtn_{args[2]}", $"kitpanel_{args[2]}",  btn_props[1], btn_props[0], 12, "0.34 0.06", $"0.96 0.28", $"octakits_try_claim {kitname} {args[2]}", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                    CuiHelper.DestroyUi(player, $"kit_claimbtn_{args[2]}"); 
                }
                else {
                    CUIClass.CreateButton(ref ui, "octakits_btn_claim", $"octakits_viewkit_info", btn_props[1],  btn_props[0], 11, "0.45 0.15", "0.85 0.225", $"octakits_try_claim {kitname} {args[2]}", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");
                    CuiHelper.DestroyUi(player, "octakits_btn_claim"); 
                }
                CuiHelper.AddUi(player, ui); 

                if (config?.ui?.closeAfterClaim == true) {
                    CuiHelper.DestroyUi(player, "background"); 
                    CuiHelper.DestroyUi(player, "background"); 
                }

                /* if (Notifications != null)
                    Notifications.Call("Run",
                        player,
                        5,
                        gl("notification_kitclaim_success").Replace("{kitname}", kitname),
                        gl("notification_success_stripe_color"),
                        Img((string)Kits.Call("GetKitImage", kitname)),
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

            
            CuiHelper.DestroyUi(player, "octakits_viewkit_inventory"); 
            CuiHelper.DestroyUi(player, "octakits_viewkit_info"); 
            ShowKits(player, Convert.ToInt32(args[0]));

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

            // Проверка готовности плагинов
            if (Kits == null)
            {
                Puts("Плагин Kits не найден");
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
            string[] _args = {"edit", kitname};
            Kits.Call("cmdKit", player, addonsData["addons"].RustKits_claimCommand, _args);

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

            public static void CreateInput(ref CuiElementContainer _container, string _name, string _parent, string _color, int _size, string _anchorMin, string _anchorMax, string _defaultText, string _font = "permanentmarker.ttf", string _command = "command.processinput", TextAnchor _align = TextAnchor.MiddleCenter, int _charsLimit = 200)
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
                            ReadOnly = true,
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
                    debugMode = false
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
            Puts($"[OctaKits] CloseKits_API -> {player.displayName}");

            // Уничтожаем элементы UI, которые мог создать OctaKits
            CuiHelper.DestroyUi(player, "errortext");
            for (int i = 0; i < 6; i++)
            {
                CuiHelper.DestroyUi(player, $"kitpanel_{i}");
                CuiHelper.DestroyUi(player, $"kit_image_{i}");
                CuiHelper.DestroyUi(player, $"kit_name_{i}");
                CuiHelper.DestroyUi(player, $"kit_desc_{i}");
                CuiHelper.DestroyUi(player, $"kit_claimbtn_{i}");
                CuiHelper.DestroyUi(player, $"kit_cd_{i}");
            }
            // кнопки пагинации
            CuiHelper.DestroyUi(player, "octakits_btn_next");
            CuiHelper.DestroyUi(player, "octakits_btn_back");
            // элементы детального просмотра кита
            CuiHelper.DestroyUi(player, "octakits_viewkit_inventory");
            CuiHelper.DestroyUi(player, "octakits_viewkit_info");
            return null;
        }


    }
}