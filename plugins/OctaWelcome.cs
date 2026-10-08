using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaWelcome", "Octavian", "1.1.0")]
    [Description("Приветственная панель Octavian: 10 вкладок, редактор, автогенерация страниц боссов из OctaBoss")]

    public class OctaWelcome : RustPlugin
    {

        #region [Dependencies]

        [PluginReference] Plugin ImageLibrary, Kits;

        private void DependencyWarnings()
        {
            timer.Once(1f, () =>
            {
                if (ImageLibrary == null)
                    Puts("(!) ImageLibrary не найден — картинки будут грузиться медленнее.");
                if (Kits == null) Puts("(!) Плагин Kits не найден — вкладка наборов будет пустой.");
            });
        }

        #region API Calls

        private void OpenAddOn_API(BasePlayer player, string _apiName)
        {
            // Вкладка наборов — собственный рендер OctaWelcome, данные берутся напрямую из Kits.
            if (_apiName == "OctaKits") { KitsTab_Show(player, 0); return; }

            // Прочие аддоны (если появятся) — по-прежнему через CallHook.
            var addon = plugins.Find(_apiName);
            if (addon != null) addon.CallHook("ShowKits_Page1_API", player);
        }

        private void CloseAddOn_API(BasePlayer player)
        {
            // Своих аддонов нет — просто добиваем элементы вкладки наборов.
            KitsTab_Destroy(player);
        }

        #endregion

        #endregion

        #region [Fields]

        private List<string> cuiList = new List<string>() { "background", "background2", "offsetContainer", "mainPanel", "sidePanel",
        "titlePanel", "logoPanel", "closeButton", "contentPanel", "highlightBtn", "menuBtn1", "menuBtn2", "menuBtn3", "menuBtn4", "menuBtn5", "menuBtn6",
        "menuBtn7", "menuBtn8", "menuBtn9", "menuBtn10", "nextButton", "previousButton"};

        private List<string> tabList = new List<string>() { "Tab1", "Tab2", "Tab3", "Tab4", "Tab5", "Tab6", "Tab7", "Tab8", "Tab9", "Tab10" };

        private Dictionary<ulong, bool> isPlayerEditing = new Dictionary<ulong, bool>();
        private string panelNameEdit = null;
        private int anchor = 0;
        private float moveValue = 0.01f;
        #endregion

        #region [Hooks]

        private void OnServerInitialized()
        {
            LoadData();
            LoadPresetData();
            LoadPageData();
            LoadConfig();
            WriteCuiEntry();
            WritePageEntry();
            PopulateBossPages();
            DependencyWarnings();
            RegisterCommands();
            RegisterPerms();
            // Первый запуск — применяем базовый шаблон, чтобы панель работала из коробки
            if (!presetData[$"{Name}"].presetInit)
            {
                SetPresetValues(1);
                presetData[$"{Name}"].presetInit = true;
                presetData[$"{Name}"].presetNumber = 1;
                SavePresetData();
            }

            DownloadImages();

            foreach (var _player in BasePlayer.activePlayerList)
            {
                if (!_player.IsAdmin) continue;
                WriteAdminEntry(_player);
            }
            //{CreateCui(_player); CreateTab(_player, 1, 1);}
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            WriteAdminEntry(player);

            if (config.generalSettings.openOnConnect && presetData[$"{Name}"].presetInit)
            {
                if (config.generalSettings.openOnce)
                {
                    if (!presetData[$"{Name}"].seen.Contains(player.userID))
                    {
                        presetData[$"{Name}"].seen.Add(player.userID);
                        SavePresetData();
                        CreateCui(player, true);
                        CreateTab(player, config.generalSettings.openTab, 1);
                        return;
                    }
                    else return;
                }
                CreateCui(player, true);
                CreateTab(player, config.generalSettings.openTab, 1);
            }
        }

        void OnPlayerDisconnected(BasePlayer player) => BlockInput(player, false);

        private void OnNewSave()
        {
            if (presetData != null && presetData.ContainsKey($"{Name}"))
            {
                presetData[$"{Name}"].seen.Clear();
                SavePresetData();
            }
        }

        private void Unload()
        {
            foreach (var _player in BasePlayer.activePlayerList)
            {
                DestroyCui(_player);
                DestroyEditPanelUi(_player);
                CuiHelper.DestroyUi(_player, "l_offsetContainer");
                CuiHelper.DestroyUi(_player, "l_background");
                CloseAddOn_API(_player);
                DestroyPopUi(_player);
            }
        }

        #endregion

        #region [Image Handling]

        private void DownloadImages()
        {
            if (ImageLibrary == null) return;
            //image from data
            foreach (string element in cuiData.Keys)
            { if (CI(element)) ImageLibrary.Call("AddImage", cuiData[element].image, element); }
            //images from config    
            //📝"Could have been solved better but customers got used to old config file so I didnt want to change it."
            if (config.tab1Settings.tabImageUrl.StartsWith("http") || config.tab1Settings.tabImageUrl.StartsWith("www"))
                ImageLibrary.Call("AddImage", config.tab1Settings.tabImageUrl, "tab1bg");
            if (config.tab2Settings.tabImageUrl.StartsWith("http") || config.tab2Settings.tabImageUrl.StartsWith("www"))
                ImageLibrary.Call("AddImage", config.tab2Settings.tabImageUrl, "tab2bg");
            if (config.tab3Settings.tabImageUrl.StartsWith("http") || config.tab3Settings.tabImageUrl.StartsWith("www"))
                ImageLibrary.Call("AddImage", config.tab3Settings.tabImageUrl, "tab3bg");
            if (config.tab4Settings.tabImageUrl.StartsWith("http") || config.tab4Settings.tabImageUrl.StartsWith("www"))
                ImageLibrary.Call("AddImage", config.tab4Settings.tabImageUrl, "tab4bg");
            if (config.tab5Settings.tabImageUrl.StartsWith("http") || config.tab5Settings.tabImageUrl.StartsWith("www"))
                ImageLibrary.Call("AddImage", config.tab5Settings.tabImageUrl, "tab5bg");
            if (config.tab6Settings.tabImageUrl.StartsWith("http") || config.tab6Settings.tabImageUrl.StartsWith("www"))
                ImageLibrary.Call("AddImage", config.tab6Settings.tabImageUrl, "tab6bg");
            if (config.tab7Settings.tabImageUrl.StartsWith("http") || config.tab7Settings.tabImageUrl.StartsWith("www"))
                ImageLibrary.Call("AddImage", config.tab7Settings.tabImageUrl, "tab7bg");
            if (config.tab8Settings.tabImageUrl.StartsWith("http") || config.tab8Settings.tabImageUrl.StartsWith("www"))
                ImageLibrary.Call("AddImage", config.tab8Settings.tabImageUrl, "tab8bg");
            if (config.tab9Settings.tabImageUrl.StartsWith("http") || config.tab9Settings.tabImageUrl.StartsWith("www"))
                ImageLibrary.Call("AddImage", config.tab9Settings.tabImageUrl, "tab9bg");
            if (config.tab10Settings.tabImageUrl.StartsWith("http") || config.tab10Settings.tabImageUrl.StartsWith("www"))
                ImageLibrary.Call("AddImage", config.tab10Settings.tabImageUrl, "tab10bg");

            //my images
            ImageLibrary.Call("AddImage", "https://rustplugins.net/products/welcomepanel/keyicons.png", "wasd_panelsText3");
            ImageLibrary.Call("AddImage", "https://rustplugins.net/products/welcomepanel/mouseicon.png", "mouse_panelsText3");
            ImageLibrary.Call("AddImage", "https://rustplugins.net/products/welcomepanel/Rkeyicon.png", "back_panelsText3");
            //wp layouts
            ImageLibrary.Call("AddImage", "https://rustplugins.net/products/welcomepanel/empty_template.png", "https://rustplugins.net/products/welcomepanel/empty_template.png");
            ImageLibrary.Call("AddImage", "https://rustplugins.net/products/welcomepanel/5/template5.jpg", "https://rustplugins.net/products/welcomepanel/5/template5.jpg");
            ImageLibrary.Call("AddImage", "https://rustplugins.net/products/welcomepanel/4/template_4.png", "https://rustplugins.net/products/welcomepanel/4/template_4.png");
            ImageLibrary.Call("AddImage", "https://rustplugins.net/products/welcomepanel/3/template3.png", "https://rustplugins.net/products/welcomepanel/3/template3.png");
            ImageLibrary.Call("AddImage", "https://rustplugins.net/products/welcomepanel/2/template2.png", "https://rustplugins.net/products/welcomepanel/2/template2.png");
            ImageLibrary.Call("AddImage", "https://codefling.com/uploads/monthly_2021_07/2.PNG.393e12443a8a8186523b2dcef616ee0c.PNG", "https://codefling.com/uploads/monthly_2021_07/2.PNG.393e12443a8a8186523b2dcef616ee0c.PNG");
        }

        private string Img(string name)
        {
            if (ImageLibrary != null)
            {
                if (!(bool)ImageLibrary.Call("HasImage", name))
                    return cuiData[name].image;
                else
                    return (string)ImageLibrary?.Call("GetImage", name);
            }
            else return cuiData[name].image;
        }

        private string Img2(int tabNumber)
        {
            string[] images = {"empty", config.tab1Settings.tabImageUrl, config.tab2Settings.tabImageUrl, config.tab3Settings.tabImageUrl,
            config.tab4Settings.tabImageUrl, config.tab5Settings.tabImageUrl, config.tab6Settings.tabImageUrl, config.tab7Settings.tabImageUrl,
            config.tab8Settings.tabImageUrl, config.tab9Settings.tabImageUrl, config.tab10Settings.tabImageUrl};


            if (ImageLibrary != null)
            {
                if (!(bool)ImageLibrary.Call("HasImage", $"tab{tabNumber}bg"))
                    return images[tabNumber];
                else
                    return (string)ImageLibrary?.Call("GetImage", $"tab{tabNumber}bg");
            }
            else return images[tabNumber];
        }

        private string Img3(string url)
        {   //img url been used as image names
            if (ImageLibrary != null)
            {
                if (!(bool)ImageLibrary.Call("HasImage", url))
                    return url;
                else
                    return (string)ImageLibrary?.Call("GetImage", url);
            }
            else
                return url;
        }

        private bool CI(string name)
        {
            if (cuiData[name].image == null)
            {
                return false;
            }
            else
            {
                if (cuiData[name].image == "") return false;
                return true;
            }
        }


        #endregion

        #region [Commands]

        private void RegisterCommands()
        {
            if (config?.generalSettings?.customCommands == null)
                return;

            int count = config.generalSettings.customCommands.Count();
            for (int i = 0; i < count; i++)
            {
                cmd.AddChatCommand(config.generalSettings.customCommands[i], this, "Wp_CustomCommands");
            }
        }

        private object Wp_CustomCommands(BasePlayer player, string command, string[] args)
        {
            if (!presetData[$"{Name}"].presetInit)
            {
                SendReply(player, "<size=15>Сначала выберите шаблон: <color=#cd412b>/octa_template</color>.</size> \n\nСмена пресета перезапишет файл CuiData вместе с названиями кнопок и заголовком в конфиге! \nВсегда делайте резервные копии!");
                return null;
            }
            int pageNumber = 1;
            if (args.Length > 0)
            {
                if (IsDigitsOnly(args[0])) pageNumber = Convert.ToInt32(args[0]);
            }
            if (config?.generalSettings?.customCommands?.Contains(command) == true)
            {
                int index = config.generalSettings.customCommands.IndexOf(command);
                int tab = index + 1;
                CreateCui(player, true); CreateTab(player, tab, pageNumber);
            }
            return null;
        }

        [ConsoleCommand("octawelcome_open")]
        private void octawelcome_open(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (arg.Player() == null) return;
            if (args.Length < 2) return;

            int tab = Convert.ToInt32(args[0]);
            int page = Convert.ToInt32(args[1]);
            DestroyPage(player);
            CreateTab(player, tab, page);
        }

        [ConsoleCommand("octawelcome_close")]
        private void octawelcome_close(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (arg.Player() == null) return;

            DestroyPage(player);
            DestroyCui(player);
            CuiHelper.DestroyUi(player, "l_offsetContainer");
            CuiHelper.DestroyUi(player, "l_background");
            DestroyPopUi(player);
            DestroyEditPanelUi(player);
            CloseAddOn_API(player);
        }

        [ConsoleCommand("octawelcome_editpanel")]
        private void octawelcome_editpanel(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (arg.Player() == null) return;
            if (args.Length < 1) return;
            if (!player.IsAdmin) return;


            isPlayerEditing[player.userID] = true;
            panelNameEdit = args[0];
            DestroyEditPanelUi(player);
            CreateCui(player, false);
            CreateTab(player, 1, 1);
            //Puts($"{isPlayerEditing[player.userID]}");
        }

        [ConsoleCommand("octa_color")]
        private void octa_color(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (arg.Player() == null) return;
            if (!player.IsAdmin) return;
            if (args == null)
            {
                player.ConsoleMessage($"Неверный формат. Пример: octa_color \"0.16 0.34 0.49 1.0\" ");
                return;
            }
            if (args.Length > 1)
            {
                player.ConsoleMessage($"Неверный формат. Пример: octa_color \"0.16 0.34 0.49 1.0\" ");
                return;
            }
            if (panelNameEdit == null)
            {
                player.ConsoleMessage($"Панель для редактирования не выбрана. Наберите /octa_edit");
                return;
            }

            cuiData[panelNameEdit].color = args[0];
            SaveData();
            player.ConsoleMessage($"{panelNameEdit}: цвет изменён на {cuiData[panelNameEdit].color}");
        }

        [ConsoleCommand("octa_movevalue")]
        private void octa_movevalue(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (arg.Player() == null) return;
            if (args.Length < 1) return;
            if (!player.IsAdmin) return;

            moveValue = Convert.ToSingle(args[0]);
            if (moveValue > 0.3) player.ConsoleMessage($"(!) Большой шаг — панели будут двигаться значительно.");
            player.ConsoleMessage($"Шаг перемещения установлен: {moveValue}");
        }

        [ChatCommand("octa_edit")]
        private void octa_edit(BasePlayer player)
        {
            if (player == null) return;
            if (!player.IsAdmin) return;

            CreateEditPanelMenu(player);
        }

        [ChatCommand("octa_template")]
        private void octa_layout(BasePlayer player)
        {
            if (player == null) return;
            if (!player.IsAdmin) return;

            CreateLayoutMenu(player);
        }

        [ChatCommand("octa_populatepages")]
        private void populatepages(BasePlayer player)
        {
            if (player == null) return;
            if (!player.IsAdmin) return;

            foreach (string tab in pageData.Keys)
            {
                pageData[tab].page2.Add($"{tab} Page 2");
                pageData[tab].page3.Add($"{tab} Page 3");
                pageData[tab].page4.Add($"{tab} Page 4");
                pageData[tab].page5.Add($"{tab} Page 5");
            }
            SavePageData();
        }


        #endregion

        #region [Functions & Methods]

        private void RegisterPerms()
        {
            if (config?.generalSettings?.permsEnabled != true) return;
            for (int i = 1; i < 11; i++)
            {
                permission.RegisterPermission($"octawelcome.tab{i}", this);
            }
        }

        private bool CheckPerms(BasePlayer player, int tabNumber)
        {
            if (config?.generalSettings?.permsEnabled != true) return true;
            return permission.UserHasPermission(player.UserIDString, $"octawelcome.tab{tabNumber}");
        }

        private void WriteCuiEntry()
        {
            foreach (string elementName in cuiList)
            {
                if (!cuiData.ContainsKey(elementName))
                {
                    cuiData.Add(elementName, new CuiData());
                }
            }
            SaveData();
        }

        private void WritePageEntry()
        {
            foreach (string tab in tabList)
            {
                if (!pageData.ContainsKey(tab))
                {
                    pageData.Add(tab, new PageData());
                }
            }
            SavePageData();
        }

        private void WriteAdminEntry(BasePlayer player)
        {
            if (!player.IsAdmin) return;
            if (!isPlayerEditing.ContainsKey(player.userID))
            {
                isPlayerEditing.Add(player.userID, false);
            }
            else { isPlayerEditing[player.userID] = false; }
        }

        void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!player.IsAdmin) return;

            if (!isPlayerEditing.ContainsKey(player.userID)) return;

            if (isPlayerEditing[player.userID])
            {
                if (input.WasJustPressed(BUTTON.FIRE_PRIMARY))
                {
                    float anchorValue = 0f;
                    if (anchor == 1)
                    {
                        anchorValue = GetSingleAnchor(cuiData[panelNameEdit].anchorMin, 0);
                        anchorValue = anchorValue + moveValue;
                        cuiData[panelNameEdit].anchorMin = $"{anchorValue} {GetSingleAnchor(cuiData[panelNameEdit].anchorMin, 1)}";
                        SaveData();
                    }
                    if (anchor == 2)
                    {
                        anchorValue = GetSingleAnchor(cuiData[panelNameEdit].anchorMin, 1);
                        anchorValue = anchorValue + moveValue;
                        cuiData[panelNameEdit].anchorMin = $"{GetSingleAnchor(cuiData[panelNameEdit].anchorMin, 0)} {anchorValue}";
                        SaveData();
                    }
                    if (anchor == 3)
                    {
                        anchorValue = GetSingleAnchor(cuiData[panelNameEdit].anchorMax, 0);
                        anchorValue = anchorValue + moveValue;
                        cuiData[panelNameEdit].anchorMax = $"{anchorValue} {GetSingleAnchor(cuiData[panelNameEdit].anchorMax, 1)}";
                        SaveData();
                    }
                    if (anchor == 4)
                    {
                        anchorValue = GetSingleAnchor(cuiData[panelNameEdit].anchorMax, 1);
                        anchorValue = anchorValue + moveValue;
                        cuiData[panelNameEdit].anchorMax = $"{GetSingleAnchor(cuiData[panelNameEdit].anchorMax, 0)} {anchorValue}";
                        SaveData();
                    }
                    CreateCui(player, false, true);
                    CreateTab(player, 1, 2);
                    return;
                }
                if (input.WasJustPressed(BUTTON.FIRE_SECONDARY))
                {
                    float anchorValue = 0f;
                    if (anchor == 1)
                    {
                        anchorValue = GetSingleAnchor(cuiData[panelNameEdit].anchorMin, 0);
                        anchorValue = anchorValue - moveValue;
                        cuiData[panelNameEdit].anchorMin = $"{anchorValue} {GetSingleAnchor(cuiData[panelNameEdit].anchorMin, 1)}";
                        SaveData();
                    }
                    if (anchor == 2)
                    {
                        anchorValue = GetSingleAnchor(cuiData[panelNameEdit].anchorMin, 1);
                        anchorValue = anchorValue - moveValue;
                        cuiData[panelNameEdit].anchorMin = $"{GetSingleAnchor(cuiData[panelNameEdit].anchorMin, 0)} {anchorValue}";
                        SaveData();
                    }
                    if (anchor == 3)
                    {
                        anchorValue = GetSingleAnchor(cuiData[panelNameEdit].anchorMax, 0);
                        anchorValue = anchorValue - moveValue;
                        cuiData[panelNameEdit].anchorMax = $"{anchorValue} {GetSingleAnchor(cuiData[panelNameEdit].anchorMax, 1)}";
                        SaveData();
                    }
                    if (anchor == 4)
                    {
                        anchorValue = GetSingleAnchor(cuiData[panelNameEdit].anchorMax, 1);
                        anchorValue = anchorValue - moveValue;
                        cuiData[panelNameEdit].anchorMax = $"{GetSingleAnchor(cuiData[panelNameEdit].anchorMax, 0)} {anchorValue}";
                        SaveData();
                    }
                    CreateCui(player, false, true);
                    CreateTab(player, 1, 2);
                    return;
                }
                if (input.WasJustPressed(BUTTON.FORWARD))
                {
                    anchor = 4;
                    return;
                }
                if (input.WasJustPressed(BUTTON.BACKWARD))
                {
                    anchor = 2;
                    return;
                }
                if (input.WasJustPressed(BUTTON.LEFT))
                {
                    anchor = 1;
                    return;
                }
                if (input.WasJustPressed(BUTTON.RIGHT))
                {
                    anchor = 3;
                    return;
                }
                if (input.WasJustPressed(BUTTON.RELOAD))
                {
                    DestroyPage(player);
                    DestroyCui(player);
                    CreateEditPanelMenu(player);
                    isPlayerEditing[player.userID] = false;
                    return;
                }

                return;
            }
        }

        private float GetSingleAnchor(string anchor, int index)
        {
            //if (anchor == null) return null;
            string[] splitter = anchor.Split(' ');
            float converted = 0;

            if (splitter[index] != null)
            {
                converted = Convert.ToSingle(splitter[index]);
            }

            return converted;
        }

        #endregion 

        #region [Utilities]

        [PluginReference] Plugin Economics, ServerRewards, WipeCountdown;

        List<ulong> isBlocked = new List<ulong>();
        //Replace Text
        private string R(BasePlayer player, string _text)
        {
            string text = _text;

            if (text.Contains("{playername}"))
            {
                string playerName = player.displayName;
                text = text.Replace("{playername}", playerName);
            }
            if (text.Contains("{pvp/pve}"))
            {
                bool pve = ConVar.Server.pve;
                if (pve)
                    text = text.Replace("{pvp/pve}", "PVE");
                else
                    text = text.Replace("{pvp/pve}", "PVP");
            }
            if (text.Contains("{maxplayers}"))
            {
                string max = $"{(int)ConVar.Server.maxplayers}";
                text = text.Replace("{maxplayers}", max);
            }
            if (text.Contains("{online}"))
            {
                string online = $"{(int)BasePlayer.activePlayerList.Count()}";
                text = text.Replace("{online}", online);
            }
            if (text.Contains("{sleeping}"))
            {
                string sleeping = $"{(int)BasePlayer.sleepingPlayerList.Count()}";
                text = text.Replace("{sleeping}", sleeping);
            }
            if (text.Contains("{joining}"))
            {
                string joining = $"{(int)ServerMgr.Instance.connectionQueue.Joining}";
                text = text.Replace("{joining}", joining);
            }
            if (text.Contains("{queued}"))
            {
                string queued = $"{(int)ServerMgr.Instance.connectionQueue.Queued}";
                text = text.Replace("{queued}", queued);
            }
            if (text.Contains("{worldsize}"))
            {
                string worldsize = $"{(int)ConVar.Server.worldsize}";
                text = text.Replace("{worldsize}", worldsize);
            }
            if (text.Contains("{hostname}"))
            {
                string hostname = ConVar.Server.hostname;
                text = text.Replace("{hostname}", hostname);
            }
            if (WipeCountdown != null)
            {
                if (text.Contains("{wipecountdown}"))
                {
                    string wipe = Convert.ToString(WipeCountdown.CallHook("GetCountdownFormated_API"));
                    text = text.Replace("{wipecountdown}", wipe);
                }
            }
            if (Economics != null)
            {
                if (text.Contains("{economics}"))
                {
                    string playersBalance = $"{Economics.Call<double>("Balance", player.UserIDString)}";
                    text = text.Replace("{economics}", playersBalance);
                }
            }
            if (ServerRewards != null)
            {
                if (text.Contains("{rp}"))
                {
                    string playersRP = $"{ServerRewards?.Call<int>("CheckPoints", (ulong)player.userID)}";
                    text = text.Replace("{rp}", playersRP);
                }
            }
            return text;
        }
        //Editing Mode (disable fade while editing)
        private float Em(bool enabled, float fade)
        {
            if (enabled)
                return 0f;
            else
                return fade;
        }

        private void BlockInput(BasePlayer player, bool state)
        {
            /*
            if (!state) {
                if (isBlocked.Contains(player.userID)) isBlocked.Remove(player.userID);
                player.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, false); }
            else {
                if (!isBlocked.Contains(player.userID)) isBlocked.Add(player.userID);
                player.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, true); }
            */
        }

        object CanSpectateTarget(BasePlayer player)
        {
            if (isBlocked.Contains(player.userID)) return false;

            return null;
        }

        bool IsDigitsOnly(string str)
        {
            foreach (char c in str)
            {
                if (c < '0' || c > '9')
                    return false;
            }
            return true;
        }

        #endregion

        #region [CUI]

        private void CreateCui(BasePlayer player, bool cursor, bool eM = false)
        {

            #region Base
            //background
            var createBase = CUIClass.CreateOverlay("background", $"{cuiData["background"].color}", $"{cuiData["background"].anchorMin}", $"{cuiData["background"].anchorMax}", cursor, Em(eM, cuiData["background"].fade), $"{cuiData["background"].material}");
            if (CI("background")) CUIClass.CreateImage(ref createBase, "background", Img("background"), "0 0", "1 1", Em(eM, cuiData["background"].fade));
            CUIClass.CreatePanel(ref createBase, "background2", "Overlay", $"{cuiData["background2"].color}", $"{cuiData["background2"].anchorMin}", $"{cuiData["background2"].anchorMax}", false, Em(eM, cuiData["background2"].fade), $"{cuiData["background2"].material}");
            if (CI("background2")) CUIClass.CreateImage(ref createBase, "background2", Img("background2"), "0 0", "1 1", Em(eM, cuiData["background2"].fade));
            //container
            CUIClass.CreatePanel(ref createBase, "offsetContainer", "Overlay", $"{cuiData["offsetContainer"].color}", $"{cuiData["offsetContainer"].anchorMin}", $"{cuiData["offsetContainer"].anchorMax}", false, Em(eM, cuiData["offsetContainer"].fade), $"{cuiData["offsetContainer"].material}", "", "-680 -360", "680 360");
            if (CI("offsetContainer")) CUIClass.CreateImage(ref createBase, "offsetContainer", Img("offsetContainer"), "0.02 0", "1 1", Em(eM, cuiData["offsetContainer"].fade));
            //main panel
            CUIClass.CreatePanel(ref createBase, "mainPanel", "offsetContainer", $"{cuiData["mainPanel"].color}", $"{cuiData["mainPanel"].anchorMin}", $"{cuiData["mainPanel"].anchorMax}", false, Em(eM, cuiData["mainPanel"].fade), $"{cuiData["mainPanel"].material}");
            if (CI("mainPanel")) CUIClass.CreateImage(ref createBase, "mainPanel", Img("mainPanel"), "0 0", "1 1", Em(eM, cuiData["mainPanel"].fade));
            //side panel
            CUIClass.CreatePanel(ref createBase, "sidePanel", "offsetContainer", $"{cuiData["sidePanel"].color}", $"{cuiData["sidePanel"].anchorMin}", $"{cuiData["sidePanel"].anchorMax}", false, Em(eM, cuiData["sidePanel"].fade), $"{cuiData["sidePanel"].material}");
            if (CI("sidePanel")) CUIClass.CreateImage(ref createBase, "sidePanel", Img("sidePanel"), "0 0", "1 1", Em(eM, cuiData["sidePanel"].fade));
            //title panel
            CUIClass.CreatePanel(ref createBase, "titlePanel", "offsetContainer", $"{cuiData["titlePanel"].color}", $"{cuiData["titlePanel"].anchorMin}", $"{cuiData["titlePanel"].anchorMax}", false, Em(eM, cuiData["titlePanel"].fade), $"{cuiData["titlePanel"].material}");
            if (CI("titlePanel")) CUIClass.CreateImage(ref createBase, "titlePanel", Img("titlePanel"), "0 0", "1 1", Em(eM, cuiData["titlePanel"].fade));
            CUIClass.CreateText(ref createBase, "titleText", "titlePanel", $"{config.titleSettings.titleColor}", $"{R(player, config.titleSettings.titleText)}", 15, "0.00 0", "1 1", TextAnchor.MiddleLeft, $"{config.titleSettings.titleFontStyle}", $"{config.titleSettings.titleOutlineColor}", $"{config.titleSettings.titleOutlineThick} {config.titleSettings.titleOutlineThick}");
            //logo panel
            CUIClass.CreatePanel(ref createBase, "logoPanel", "offsetContainer", $"{cuiData["logoPanel"].color}", $"{cuiData["logoPanel"].anchorMin}", $"{cuiData["logoPanel"].anchorMax}", false, Em(eM, cuiData["logoPanel"].fade), $"{cuiData["logoPanel"].material}");
            if (CI("logoPanel")) CUIClass.CreateImage(ref createBase, "logoPanel", Img("logoPanel"), "0 0", "1 1", Em(eM, cuiData["logoPanel"].fade));
            //close button
            CUIClass.CreateButton(ref createBase, "closeButton", "offsetContainer", $"{cuiData["closeButton"].color}", $"{config.buttonSettings.closeBtnText}", 11, $"{cuiData["closeButton"].anchorMin}", $"{cuiData["closeButton"].anchorMax}", $"octawelcome_close", "", "1 1 1 0.7", Em(eM, cuiData["closeButton"].fade), TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", $"{cuiData["closeButton"].material}");
            if (CI("closeButton")) CUIClass.CreateImage(ref createBase, "closeButton", Img("closeButton"), "0 0", "1 1", Em(eM, cuiData["closeButton"].fade));
            #endregion
            #region Buttons
            //Button 1 
            if (config.tab1Settings.btnEnabled)
            {
                CUIClass.CreateButton(ref createBase, "menuBtn1", "offsetContainer", $"{cuiData["menuBtn1"].color}", $"{config?.tab1Settings.btnTitle}", 16, $"{cuiData["menuBtn1"].anchorMin}", $"{cuiData["menuBtn1"].anchorMax}", $"octawelcome_open 1 1", "", "1 1 1 0.7", cuiData["menuBtn1"].fade, config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", $"{cuiData["menuBtn1"].material}");
                if (CI("menuBtn1")) CUIClass.CreateImage(ref createBase, "menuBtn1", Img("menuBtn1"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData["menuBtn1"].fade);
            }
            //Button 2
            if (config.tab2Settings.btnEnabled)
            {
                CUIClass.CreateButton(ref createBase, "menuBtn2", "offsetContainer", $"{cuiData["menuBtn2"].color}", $"{config?.tab2Settings.btnTitle}", 16, $"{cuiData["menuBtn2"].anchorMin}", $"{cuiData["menuBtn2"].anchorMax}", $"octawelcome_open 2 1", "", "1 1 1 0.7", cuiData["menuBtn2"].fade, config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", $"{cuiData["menuBtn2"].material}");
                if (CI("menuBtn2")) CUIClass.CreateImage(ref createBase, "menuBtn2", Img("menuBtn2"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData["menuBtn2"].fade);
            }
            //Button 3
            if (config.tab3Settings.btnEnabled)
            {
                CUIClass.CreateButton(ref createBase, "menuBtn3", "offsetContainer", $"{cuiData["menuBtn3"].color}", $"{config?.tab3Settings.btnTitle}", 16, $"{cuiData["menuBtn3"].anchorMin}", $"{cuiData["menuBtn3"].anchorMax}", $"octawelcome_open 3 1", "", "1 1 1 0.7", cuiData["menuBtn3"].fade, config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", $"{cuiData["menuBtn3"].material}");
                if (CI("menuBtn3")) CUIClass.CreateImage(ref createBase, "menuBtn3", Img("menuBtn3"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData["menuBtn3"].fade);
            }
            //Button 4
            if (config.tab4Settings.btnEnabled)
            {
                CUIClass.CreateButton(ref createBase, "menuBtn4", "offsetContainer", $"{cuiData["menuBtn4"].color}", $"{config?.tab4Settings.btnTitle}", 16, $"{cuiData["menuBtn4"].anchorMin}", $"{cuiData["menuBtn4"].anchorMax}", $"octawelcome_open 4 1", "", "1 1 1 0.7", cuiData["menuBtn4"].fade, config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", $"{cuiData["menuBtn4"].material}");
                if (CI("menuBtn4")) CUIClass.CreateImage(ref createBase, "menuBtn4", Img("menuBtn4"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData["menuBtn4"].fade);
            }
            //Button 5
            if (config.tab5Settings.btnEnabled)
            {
                CUIClass.CreateButton(ref createBase, "menuBtn5", "offsetContainer", $"{cuiData["menuBtn5"].color}", $"{config?.tab5Settings.btnTitle}", 16, $"{cuiData["menuBtn5"].anchorMin}", $"{cuiData["menuBtn5"].anchorMax}", $"octawelcome_open 5 1", "", "1 1 1 0.7", cuiData["menuBtn5"].fade, config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", $"{cuiData["menuBtn5"].material}");
                if (CI("menuBtn5")) CUIClass.CreateImage(ref createBase, "menuBtn5", Img("menuBtn5"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData["menuBtn5"].fade);
            }
            //Button 6
            if (config.tab6Settings.btnEnabled)
            {
                CUIClass.CreateButton(ref createBase, "menuBtn6", "offsetContainer", $"{cuiData["menuBtn6"].color}", $"{config?.tab6Settings.btnTitle}", 16, $"{cuiData["menuBtn6"].anchorMin}", $"{cuiData["menuBtn6"].anchorMax}", $"octawelcome_open 6 1", "", "1 1 1 0.7", cuiData["menuBtn6"].fade, config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", $"{cuiData["menuBtn6"].material}");
                if (CI("menuBtn6")) CUIClass.CreateImage(ref createBase, "menuBtn6", Img("menuBtn6"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData["menuBtn6"].fade);
            }
            //Button 7
            if (config.tab7Settings.btnEnabled)
            {
                CUIClass.CreateButton(ref createBase, "menuBtn7", "offsetContainer", $"{cuiData["menuBtn7"].color}", $"{config?.tab7Settings.btnTitle}", 16, $"{cuiData["menuBtn7"].anchorMin}", $"{cuiData["menuBtn7"].anchorMax}", $"octawelcome_open 7 1", "", "1 1 1 0.7", cuiData["menuBtn7"].fade, config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", $"{cuiData["menuBtn7"].material}");
                if (CI("menuBtn7")) CUIClass.CreateImage(ref createBase, "menuBtn7", Img("menuBtn7"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData["menuBtn7"].fade);
            }
            //BUttom 8
            if (config.tab8Settings.btnEnabled)
            {
                CUIClass.CreateButton(ref createBase, "menuBtn8", "offsetContainer", $"{cuiData["menuBtn8"].color}", $"{config?.tab8Settings.btnTitle}", 16, $"{cuiData["menuBtn8"].anchorMin}", $"{cuiData["menuBtn8"].anchorMax}", $"octawelcome_open 8 1", "", "1 1 1 0.7", cuiData["menuBtn8"].fade, config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", $"{cuiData["menuBtn8"].material}");
                if (CI("menuBtn8")) CUIClass.CreateImage(ref createBase, "menuBtn8", Img("menuBtn8"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData["menuBtn8"].fade);
            }
            //Button 9
            if (config.tab9Settings.btnEnabled)
            {
                CUIClass.CreateButton(ref createBase, "menuBtn9", "offsetContainer", $"{cuiData["menuBtn9"].color}", $"{config?.tab9Settings.btnTitle}", 16, $"{cuiData["menuBtn9"].anchorMin}", $"{cuiData["menuBtn9"].anchorMax}", $"octawelcome_open 9 1", "", "1 1 1 0.7", cuiData["menuBtn9"].fade, config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", $"{cuiData["menuBtn9"].material}");
                if (CI("menuBtn9")) CUIClass.CreateImage(ref createBase, "menuBtn9", Img("menuBtn9"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData["menuBtn9"].fade);
            }
            //Button 10
            if (config.tab10Settings.btnEnabled)
            {
                CUIClass.CreateButton(ref createBase, "menuBtn10", "offsetContainer", $"{cuiData["menuBtn10"].color}", $"{config?.tab10Settings.btnTitle}", 16, $"{cuiData["menuBtn10"].anchorMin}", $"{cuiData["menuBtn10"].anchorMax}", $"octawelcome_open 10 1", "", "1 1 1 0.7", cuiData["menuBtn10"].fade, config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", $"{cuiData["menuBtn10"].material}");
                if (CI("menuBtn10")) CUIClass.CreateImage(ref createBase, "menuBtn10", Img("menuBtn10"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData["menuBtn10"].fade);
            }

            #endregion

            DestroyCui(player);
            CuiHelper.AddUi(player, createBase);
            BlockInput(player, true);
        }

        private void DestroyCui(BasePlayer player)
        {
            BlockInput(player, false);
            CuiHelper.DestroyUi(player, "empty");
            CuiHelper.DestroyUi(player, "background");
            CuiHelper.DestroyUi(player, "background2");
            CuiHelper.DestroyUi(player, "offsetContainer");

        }

        private void CreateTab(BasePlayer player, int tabNumber, int pageNumber)
        {
            #region Fields 
            string bgImage = "def";
            string bgImage_Amin = "def";
            string bgImage_Amax = "def";

            string btn_Title = "def";
            string btn_FontStyle = "def";
            string btn_Icon = "def";

            List<string> tabText = new List<string>();
            string fontColor = "def";
            int fontSize = 0;
            string fontStyle = "def";
            string fontOutColor = "def";
            string fontOutThic = "def";
            TextAnchor textAnchor = TextAnchor.MiddleCenter;

            string addonName = "def";
            #endregion
            #region Config Container
            if (tabNumber == 1)
            {
                btn_Title = config.tab1Settings.btnTitle; bgImage = config.tab1Settings.tabImageUrl; bgImage_Amin = config.tab1Settings.tabImageAnchor[0]; bgImage_Amax = config?.tab1Settings.tabImageAnchor[1];
                fontColor = config.tab1Settings.mainFontColor; tabText = config.tab1Settings.textLines; fontSize = config.tab1Settings.mainFontSize; textAnchor = config.tab1Settings.mainAlign;
                fontStyle = config.tab1Settings.mainFontStyle; fontOutColor = config.tab1Settings.fontOutlineColor; fontOutThic = config.tab1Settings.fontOutlineThick;
                btn_Icon = config.tab1Settings.btnIcon; addonName = config.extension.tab1;
            }
            if (tabNumber == 2)
            {
                btn_Title = config.tab2Settings.btnTitle; bgImage = config.tab2Settings.tabImageUrl; bgImage_Amin = config.tab2Settings.tabImageAnchor[0]; bgImage_Amax = config?.tab2Settings.tabImageAnchor[1];
                fontColor = config.tab2Settings.mainFontColor; tabText = config.tab2Settings.textLines; fontSize = config.tab2Settings.mainFontSize; textAnchor = config.tab2Settings.mainAlign;
                fontStyle = config.tab2Settings.mainFontStyle; fontOutColor = config.tab2Settings.fontOutlineColor; fontOutThic = config.tab2Settings.fontOutlineThick;
                btn_Icon = config.tab2Settings.btnIcon; addonName = config.extension.tab2;
            }
            if (tabNumber == 3)
            {
                btn_Title = config.tab3Settings.btnTitle; bgImage = config.tab3Settings.tabImageUrl; bgImage_Amin = config.tab3Settings.tabImageAnchor[0]; bgImage_Amax = config?.tab3Settings.tabImageAnchor[1];
                fontColor = config.tab3Settings.mainFontColor; tabText = config.tab3Settings.textLines; fontSize = config.tab3Settings.mainFontSize; textAnchor = config.tab3Settings.mainAlign;
                fontStyle = config.tab3Settings.mainFontStyle; fontOutColor = config.tab3Settings.fontOutlineColor; fontOutThic = config.tab3Settings.fontOutlineThick;
                btn_Icon = config.tab3Settings.btnIcon; addonName = config.extension.tab3;
            }
            if (tabNumber == 4)
            {
                btn_Title = config.tab4Settings.btnTitle; bgImage = config.tab4Settings.tabImageUrl; bgImage_Amin = config.tab4Settings.tabImageAnchor[0]; bgImage_Amax = config?.tab4Settings.tabImageAnchor[1];
                fontColor = config.tab4Settings.mainFontColor; tabText = config.tab4Settings.textLines; fontSize = config.tab4Settings.mainFontSize; textAnchor = config.tab4Settings.mainAlign;
                fontStyle = config.tab4Settings.mainFontStyle; fontOutColor = config.tab4Settings.fontOutlineColor; fontOutThic = config.tab4Settings.fontOutlineThick;
                btn_Icon = config.tab4Settings.btnIcon; addonName = config.extension.tab4;
            }
            if (tabNumber == 5)
            {
                btn_Title = config.tab5Settings.btnTitle; bgImage = config.tab5Settings.tabImageUrl; bgImage_Amin = config.tab5Settings.tabImageAnchor[0]; bgImage_Amax = config?.tab5Settings.tabImageAnchor[1];
                fontColor = config.tab5Settings.mainFontColor; tabText = config.tab5Settings.textLines; fontSize = config.tab5Settings.mainFontSize; textAnchor = config.tab5Settings.mainAlign;
                fontStyle = config.tab5Settings.mainFontStyle; fontOutColor = config.tab5Settings.fontOutlineColor; fontOutThic = config.tab5Settings.fontOutlineThick;
                btn_Icon = config.tab5Settings.btnIcon; addonName = config.extension.tab5;
            }
            if (tabNumber == 6)
            {
                btn_Title = config.tab6Settings.btnTitle; bgImage = config.tab6Settings.tabImageUrl; bgImage_Amin = config.tab6Settings.tabImageAnchor[0]; bgImage_Amax = config?.tab6Settings.tabImageAnchor[1];
                fontColor = config.tab6Settings.mainFontColor; tabText = config.tab6Settings.textLines; fontSize = config.tab6Settings.mainFontSize; textAnchor = config.tab6Settings.mainAlign;
                fontStyle = config.tab6Settings.mainFontStyle; fontOutColor = config.tab6Settings.fontOutlineColor; fontOutThic = config.tab6Settings.fontOutlineThick;
                btn_Icon = config.tab6Settings.btnIcon; addonName = config.extension.tab6;
            }
            if (tabNumber == 7)
            {
                btn_Title = config.tab7Settings.btnTitle; bgImage = config.tab7Settings.tabImageUrl; bgImage_Amin = config.tab7Settings.tabImageAnchor[0]; bgImage_Amax = config?.tab7Settings.tabImageAnchor[1];
                fontColor = config.tab7Settings.mainFontColor; tabText = config.tab7Settings.textLines; fontSize = config.tab7Settings.mainFontSize; textAnchor = config.tab7Settings.mainAlign;
                fontStyle = config.tab7Settings.mainFontStyle; fontOutColor = config.tab7Settings.fontOutlineColor; fontOutThic = config.tab7Settings.fontOutlineThick;
                btn_Icon = config.tab7Settings.btnIcon; addonName = config.extension.tab7;
            }
            if (tabNumber == 8)
            {
                btn_Title = config.tab8Settings.btnTitle; bgImage = config.tab8Settings.tabImageUrl; bgImage_Amin = config.tab8Settings.tabImageAnchor[0]; bgImage_Amax = config?.tab8Settings.tabImageAnchor[1];
                fontColor = config.tab8Settings.mainFontColor; tabText = config.tab8Settings.textLines; fontSize = config.tab8Settings.mainFontSize; textAnchor = config.tab8Settings.mainAlign;
                fontStyle = config.tab8Settings.mainFontStyle; fontOutColor = config.tab8Settings.fontOutlineColor; fontOutThic = config.tab8Settings.fontOutlineThick;
                btn_Icon = config.tab8Settings.btnIcon; addonName = config.extension.tab8;
            }
            if (tabNumber == 9)
            {
                btn_Title = config.tab9Settings.btnTitle; bgImage = config.tab9Settings.tabImageUrl; bgImage_Amin = config.tab9Settings.tabImageAnchor[0]; bgImage_Amax = config?.tab9Settings.tabImageAnchor[1];
                fontColor = config.tab9Settings.mainFontColor; tabText = config.tab9Settings.textLines; fontSize = config.tab9Settings.mainFontSize; textAnchor = config.tab9Settings.mainAlign;
                fontStyle = config.tab9Settings.mainFontStyle; fontOutColor = config.tab9Settings.fontOutlineColor; fontOutThic = config.tab9Settings.fontOutlineThick;
                btn_Icon = config.tab9Settings.btnIcon; addonName = config.extension.tab9;
            }
            if (tabNumber == 10)
            {
                btn_Title = config.tab10Settings.btnTitle; bgImage = config.tab10Settings.tabImageUrl; bgImage_Amin = config.tab10Settings.tabImageAnchor[0]; bgImage_Amax = config?.tab10Settings.tabImageAnchor[1];
                fontColor = config.tab10Settings.mainFontColor; tabText = config.tab10Settings.textLines; fontSize = config.tab10Settings.mainFontSize; textAnchor = config.tab10Settings.mainAlign;
                fontStyle = config.tab10Settings.mainFontStyle; fontOutColor = config.tab10Settings.fontOutlineColor; fontOutThic = config.tab10Settings.fontOutlineThick;
                btn_Icon = config.tab10Settings.btnIcon; addonName = config.extension.tab10;
            }
            #endregion

            var createPage = CUIClass.CreateOverlay("empty", "0 0 0 0", "0 0", "0 0", false, 0f, "assets/icons/iconmaterial.mat");

            int indexPrev = pageNumber - 1;
            int indexNext = pageNumber + 1;

            CUIClass.CreatePanel(ref createPage, "imagePanel", "offsetContainer", $"{cuiData["contentPanel"].color}", $"{cuiData["contentPanel"].anchorMin}", $"{cuiData["contentPanel"].anchorMax}", false, cuiData["contentPanel"].fade, cuiData["contentPanel"].material);


            if (addonName == "null")
            {
                if (CI("contentPanel")) CUIClass.CreateImage(ref createPage, "imagePanel", Img("contentPanel"), "0 0", "1 1", 0f);
                string textContent = string.Join("\n", tabText);

                if (CheckPerms(player, tabNumber))
                {
                    if (CheckPage(tabNumber, pageNumber))
                    {
                        if (pageNumber == 2) tabText = pageData[$"Tab{tabNumber}"].page2;
                        if (pageNumber == 3) tabText = pageData[$"Tab{tabNumber}"].page3;
                        if (pageNumber == 4) tabText = pageData[$"Tab{tabNumber}"].page4;
                        if (pageNumber == 5) tabText = pageData[$"Tab{tabNumber}"].page5;
                        if (pageNumber == 6) tabText = pageData[$"Tab{tabNumber}"].page6;
                        if (pageNumber == 7) tabText = pageData[$"Tab{tabNumber}"].page7;
                        if (pageNumber == 8) tabText = pageData[$"Tab{tabNumber}"].page8;
                        if (pageNumber == 9) tabText = pageData[$"Tab{tabNumber}"].page9;
                        if (pageNumber == 10) tabText = pageData[$"Tab{tabNumber}"].page10;
                        textContent = string.Join("\n", tabText);

                        if (bgImage.StartsWith("http") || bgImage.StartsWith("www"))
                            CUIClass.CreateImage(ref createPage, "imagePanel", Img2(tabNumber), bgImage_Amin, bgImage_Amax, 0f);

                        CUIClass.CreateText(ref createPage, "contentText", "imagePanel", fontColor, $"{R(player, textContent)}", fontSize, "0.01 0.008", "0.99 0.992", textAnchor, fontStyle, fontOutColor, $"{fontOutThic} {fontOutThic}");

                        if (CheckPage(tabNumber, indexNext))
                        {
                            CUIClass.CreateButton(ref createPage, "nextButton", "imagePanel", cuiData["nextButton"].color, "", 7, cuiData["nextButton"].anchorMin, cuiData["nextButton"].anchorMax, $"octawelcome_open {tabNumber} {indexNext}", "", "1 1 1 1", 0f, TextAnchor.MiddleCenter, $"{config?.buttonSettings.btnFontStyle}", cuiData["nextButton"].material);
                            if (CI("nextButton")) CUIClass.CreateImage(ref createPage, "nextButton", Img("nextButton"), "0 0", "1 1", 0f);
                        }
                        if (pageNumber != 1)
                        {
                            CUIClass.CreateButton(ref createPage, "previousButton", "imagePanel", cuiData["previousButton"].color, "", 7, cuiData["previousButton"].anchorMin, cuiData["previousButton"].anchorMax, $"octawelcome_open {tabNumber} {indexPrev}", "", "1 1 1 1", 0f, TextAnchor.MiddleCenter, $"{config?.buttonSettings.btnFontStyle}", cuiData["previousButton"].material);
                            if (CI("previousButton")) CUIClass.CreateImage(ref createPage, "previousButton", Img("previousButton"), "0 0", "1 1", 0f);
                        }

                    }
                    else
                    {
                        if (bgImage.StartsWith("http") || bgImage.StartsWith("www"))
                            CUIClass.CreateImage(ref createPage, "imagePanel", Img2(tabNumber), bgImage_Amin, bgImage_Amax, 0f);

                        CUIClass.CreateText(ref createPage, "contentText", "imagePanel", fontColor, $"{R(player, textContent)}", fontSize, "0.01 0.008", "0.99 0.992", textAnchor, fontStyle, fontOutColor, $"{fontOutThic} {fontOutThic}");
                    }
                }
                else
                {
                    CUIClass.CreateText(ref createPage, "contentText", "imagePanel", fontColor, $"<size=55>✘</size> \nНет доступа к этой странице.", 22, "0.01 0.008", "0.99 0.992", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", $"{fontOutThic} {fontOutThic}");
                }
            }
            CUIClass.CreateButton(ref createPage, "_btn-highlighted", $"menuBtn{tabNumber}", $"{cuiData["highlightBtn"].color}", "", 16, cuiData["highlightBtn"].anchorMin, cuiData["highlightBtn"].anchorMax, $"", "", "1 1 1 0.95", cuiData["highlightBtn"].fade, TextAnchor.MiddleCenter, $"{config?.buttonSettings.btnFontStyle}");
            if (CI($"highlightBtn"))
            {
                CUIClass.CreateImage(ref createPage, "_btn-highlighted", Img($"highlightBtn"), "0 0", "1 1", cuiData["highlightBtn"].fade);
            }
            else { if (CI($"menuBtn{tabNumber}")) CUIClass.CreateImage(ref createPage, "_btn-highlighted", Img($"menuBtn{tabNumber}"), $"{config.buttonSettings.btnIconAnchor[0]}", $"{config.buttonSettings.btnIconAnchor[1]}", cuiData[$"menuBtn{tabNumber}"].fade); }
            CUIClass.CreateText(ref createPage, "_btn-highlighted_text", "_btn-highlighted", "1 1 1 0.95", $"{btn_Title}", 16, "0 0", "1 1", config.buttonSettings.mainAlign, $"{config?.buttonSettings.btnFontStyle}", "0 0 0 0", $"0 0");

            DestroyPage(player);
            CuiHelper.AddUi(player, createPage);
            if (addonName != "null") OpenAddOn_API(player, addonName);
        }

        private void DestroyPage(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "empty");
            CuiHelper.DestroyUi(player, "_btn-highlighted");
            CuiHelper.DestroyUi(player, "imagePanel");
            CuiHelper.DestroyUi(player, "btn_next");
            CuiHelper.DestroyUi(player, "btn_prev");
            CuiHelper.DestroyUi(player, "contentText");
        }

        private bool CheckPage(int tabNumber, int pageNumber)
        {
            if (pageNumber == 1)
            {
                if (pageData[$"Tab{tabNumber}"].page2.Count() == 0)
                { return false; }
                else { return true; }
            }
            if (pageNumber == 2)
            {
                if (pageData[$"Tab{tabNumber}"].page2.Count() == 0)
                { return false; }
                else { return true; }
            }
            if (pageNumber == 3)
            {
                if (pageData[$"Tab{tabNumber}"].page3.Count() == 0)
                { return false; }
                else { return true; }
            }
            if (pageNumber == 4)
            {
                if (pageData[$"Tab{tabNumber}"].page4.Count() == 0)
                { return false; }
                else { return true; }
            }
            if (pageNumber == 5)
            {
                if (pageData[$"Tab{tabNumber}"].page5.Count() == 0)
                { return false; }
                else { return true; }
            }
            if (pageNumber == 6)
            {
                if (pageData[$"Tab{tabNumber}"].page6.Count() == 0)
                { return false; }
                else { return true; }
            }
            if (pageNumber == 7)
            {
                if (pageData[$"Tab{tabNumber}"].page7.Count() == 0)
                { return false; }
                else { return true; }
            }
            if (pageNumber == 8)
            {
                if (pageData[$"Tab{tabNumber}"].page8.Count() == 0)
                { return false; }
                else { return true; }
            }
            if (pageNumber == 9)
            {
                if (pageData[$"Tab{tabNumber}"].page9.Count() == 0)
                { return false; }
                else { return true; }
            }
            if (pageNumber == 10)
            {
                if (pageData[$"Tab{tabNumber}"].page10.Count() == 0)
                { return false; }
                else { return true; }
            }

            return false;
        }

        private string Hl_Choice(int index)
        {
            if (index == presetData[$"{Name}"].presetNumber) return "0.38 0.51 0.16 0.85";
            else return "0 0 0 0.75";
        }

        private void CreateLayoutMenu(BasePlayer player)
        {
            #region Base
            //background
            var createLayoutMenu = CUIClass.CreateOverlay("l_background", $"0 0 0 0.7", "0 0", "1 1", true, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreatePanel(ref createLayoutMenu, "l_offsetContainer", "Overlay", "0 0 0 0", "0.5 0.5", "0.5 0.5", false, 0f, "assets/icons/iconmaterial.mat", "", "-680 -360", "680 360");
            CUIClass.CreatePanel(ref createLayoutMenu, "l_mainPanel", "l_offsetContainer", "0.25 0.23 0.22 0.95", "0.2 0.1", "0.8 0.75", false, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreatePanel(ref createLayoutMenu, "l_mainTitlePanel", "l_mainPanel", "0.11 0.11 0.11 0.95", "0.0 1", "1 1.08", false, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateText(ref createLayoutMenu, "l_mainTitle", "l_mainTitlePanel", "1 1 1 1", $"Layout Menu", 18, "0.01 0", "1 1", TextAnchor.MiddleLeft, $"robotocondensed-bold.ttf", "0 0 0 1", $"1.5 1.5");
            CUIClass.CreateButton(ref createLayoutMenu, "btn_discord", "l_mainTitlePanel", "0.56 0.20 0.15 1.0", $"✘", 16, "0.955 0.10", $"0.995 0.91", $"octawelcome_close", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf");
            #endregion
            #region Layouts
            CUIClass.CreatePanel(ref createLayoutMenu, "l_lp_container", "l_mainPanel", "0 0 0 0", "0.02 0.05", "0.98 0.95", false, 0f, "assets/icons/iconmaterial.mat");
            //1
            CUIClass.CreatePanel(ref createLayoutMenu, "l_layout_panel_1", "l_lp_container", Hl_Choice(1), "0 0.51", "0.32 1", false, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateImage(ref createLayoutMenu, "l_layout_panel_1", Img3("https://codefling.com/uploads/monthly_2021_07/2.PNG.393e12443a8a8186523b2dcef616ee0c.PNG"), "0.03 0.3", "0.97 0.95", cuiData["logoPanel"].fade);
            CUIClass.CreateButton(ref createLayoutMenu, "btn_choose", "l_layout_panel_1", "0 0 0 0", "\n<b><size=17>DEFAULT #1</size></b> \n\n", 9, "0 0", "1 1", $"octa_changelayout 1", "", "1 1 1 0.7", 0f, TextAnchor.LowerCenter, $"robotocondensed-regular.ttf", "assets/icons/iconmaterial.mat");
            //2
            CUIClass.CreatePanel(ref createLayoutMenu, "l_layout_panel_2", "l_lp_container", Hl_Choice(2), "0.33 0.51", "0.65 1", false, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateImage(ref createLayoutMenu, "l_layout_panel_2", Img3("https://rustplugins.net/products/welcomepanel/2/template2.png"), "0.03 0.3", "0.97 0.95", cuiData["logoPanel"].fade);
            CUIClass.CreateButton(ref createLayoutMenu, "btn_choose", "l_layout_panel_2", "0 0 0 0", "\n<b><size=17>FULLSCREEN #2</size></b> \n\n", 9, "0 0", "1 1", $"octa_changelayout 2", "", "1 1 1 0.7", 0f, TextAnchor.LowerCenter, $"robotocondensed-regular.ttf", "assets/icons/iconmaterial.mat");
            //3
            CUIClass.CreatePanel(ref createLayoutMenu, "l_layout_panel_3", "l_lp_container", Hl_Choice(3), "0.66 0.51", "1 1", false, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateImage(ref createLayoutMenu, "l_layout_panel_3", Img3("https://rustplugins.net/products/welcomepanel/3/template3.png"), "0.03 0.3", "0.97 0.95", cuiData["logoPanel"].fade);
            CUIClass.CreateButton(ref createLayoutMenu, "btn_choose", "l_layout_panel_3", "0 0 0 0", "\n<b><size=17>GRAY #3</size></b> \n\n", 9, "0 0", "1 1", $"octa_changelayout 3", "", "1 1 1 0.7", 0f, TextAnchor.LowerCenter, $"robotocondensed-regular.ttf", "assets/icons/iconmaterial.mat");
            //4
            CUIClass.CreatePanel(ref createLayoutMenu, "l_layout_panel_4", "l_lp_container", Hl_Choice(4), "0 0", "0.32 0.49", false, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateImage(ref createLayoutMenu, "l_layout_panel_4", Img3("https://rustplugins.net/products/welcomepanel/4/template4.png"), "0.03 0.3", "0.97 0.95", cuiData["logoPanel"].fade);
            CUIClass.CreateButton(ref createLayoutMenu, "btn_choose", "l_layout_panel_4", "0 0 0 0", "\n<b><size=17>ROUNDED #4</size></b> \n\n", 9, "0 0", "1 1", $"octa_changelayout 4", "", "1 1 1 0.7", 0f, TextAnchor.LowerCenter, $"robotocondensed-regular.ttf", "assets/icons/iconmaterial.mat");
            //5
            CUIClass.CreatePanel(ref createLayoutMenu, "l_layout_panel_5", "l_lp_container", Hl_Choice(5), "0.33 0", "0.65 0.49", false, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateImage(ref createLayoutMenu, "l_layout_panel_5", Img3("https://rustplugins.net/products/welcomepanel/5/template5.jpg"), "0.03 0.3", "0.97 0.95", cuiData["logoPanel"].fade);
            CUIClass.CreateButton(ref createLayoutMenu, "btn_choose", "l_layout_panel_5", "0 0 0 0", "\n<b><size=17>FULLSCREEN #5</size></b> \n\n", 9, "0 0", "1 1", $"octa_changelayout 5", "", "1 1 1 0.7", 0f, TextAnchor.LowerCenter, $"robotocondensed-regular.ttf", "assets/icons/iconmaterial.mat");
            //6
            CUIClass.CreatePanel(ref createLayoutMenu, "l_layout_panel_6", "l_lp_container", Hl_Choice(6), "0.66 0", "1 0.49", false, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateImage(ref createLayoutMenu, "l_layout_panel_6", Img3("https://rustplugins.net/products/welcomepanel/empty_template.png"), "0.03 0.3", "0.97 0.95", cuiData["logoPanel"].fade);
            CUIClass.CreateButton(ref createLayoutMenu, "btn_choose", "l_layout_panel_6", "0 0 0 0", "\n<b><size=17>COMING IN NEXT UPDATE</size></b> \n\n", 9, "0 0", "1 1", $"", "", "1 1 1 0.7", 0f, TextAnchor.LowerCenter, $"robotocondensed-regular.ttf", "assets/icons/iconmaterial.mat");


            #endregion   
            CuiHelper.DestroyUi(player, "l_offsetContainer");
            CuiHelper.DestroyUi(player, "l_background");
            CuiHelper.AddUi(player, createLayoutMenu);

        }

        [ConsoleCommand("octa_changelayout")]
        private void octa_changelayout(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (arg.Player() == null) return;
            //if (args.Length < 2) return;

            CuiHelper.DestroyUi(player, "l_offsetContainer");
            CuiHelper.DestroyUi(player, "l_background");

            int preset = Convert.ToInt32(args[0]);
            SetPresetValues(preset);
            presetData[$"{Name}"].presetInit = true;
            presetData[$"{Name}"].presetNumber = preset;
            SavePresetData();
            DownloadImages();
            CreateChoicePopUp(player);

        }

        private object GetLayout_API1() => presetData[$"{Name}"].presetNumber;

        private void CreateChoicePopUp(BasePlayer player)
        {

            var createCPopUp = CUIClass.CreateOverlay("c_background", $"0 0 0 0.3", "0 0", "1 1", true, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreatePanel(ref createCPopUp, "c_offsetContainer", "Overlay", "0.11 0.11 0.11 1", "0.5 0.5", "0.5 0.5", false, 0f, "assets/content/ui/uibackgroundblur.mat", "", "-170 -110", "170 70");
            CUIClass.CreateText(ref createCPopUp, "c_mainTitle", "c_offsetContainer", "1 1 1 0.65", $"<size=32><color=#6e952c>✔</color></size> \n\n<size=18><color=#6e952c>Шаблон успешно изменён!</color></size>  \n\nЕсли картинки отображаются неверно — перезагрузите ImageLibrary и OctaWelcome.", 13, "0.03 0", "0.97 1", TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "0 0 0 0", $"0 0");
            CUIClass.CreateButton(ref createCPopUp, "c_btn_close", "Overlay", "0 0 0 0", $"", 13, "0 0", $"1 1", $"octawelcome_close", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/icons/iconmaterial.mat");

            DestroyPopUi(player);
            CuiHelper.AddUi(player, createCPopUp);
        }

        private void DestroyPopUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "c_offsetContainer");
            CuiHelper.DestroyUi(player, "c_background");
            CuiHelper.DestroyUi(player, "c_btn_close");
        }

        private void CreateEditPanelMenu(BasePlayer player)
        {

            #region Base
            //background
            var editPanelMenu = CUIClass.CreateOverlay("empty", $"0 0 0 0.0", "0 0", "0 0", true, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref editPanelMenu, "p_offsetContainer", "Overlay", "0 0 0 0", "0.5 0.5", "0.5 0.5", true, 0f, "assets/icons/iconmaterial.mat", "", "-680 -360", "680 360");
            CUIClass.CreatePanel(ref editPanelMenu, "p_mainPanel", "p_offsetContainer", "0.25 0.23 0.22 0.95", "0.2 0.15", "0.8 0.8", false, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreatePanel(ref editPanelMenu, "p_mainTitlePanel", "p_mainPanel", "0.11 0.11 0.11 0.95", "0.0 1", "1 1.06", false, 0f, "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateText(ref editPanelMenu, "p_mainTitle", "p_mainTitlePanel", "1 1 1 1", $"Position Editor", 14, "0.01 0", "1 1", TextAnchor.MiddleLeft, $"robotocondensed-bold.ttf", "0 0 0 1", $"1.5 1.5");
            CUIClass.CreateButton(ref editPanelMenu, "p_close", "p_mainTitlePanel", "0.56 0.20 0.15 1.0", $"✘", 16, "0.96 0.10", $"0.995 0.91", $"octawelcome_close", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf");
            #endregion
            #region Layouts
            CUIClass.CreatePanel(ref editPanelMenu, "p_container", "p_mainPanel", "0 0 0 0.0", "0.02 0.5", "0.98 0.95", false, 0f, "assets/icons/iconmaterial.mat");
            //ui panels
            CUIClass.CreateText(ref editPanelMenu, "p_panelsText", "p_container", "1 1 1 1", $"CUI Panels", 14, "0.00 0.9", "1 1", TextAnchor.UpperLeft, $"robotocondensed-bold.ttf", "0 0 0 0", $"0 0");
            CUIClass.CreateButton(ref editPanelMenu, "p_mainPanel2", "p_container", "0.11 0.11 0.11 0.95", "MAIN PANEL", 11, "0 0.7", "0.12 0.89", $"octawelcome_editpanel mainPanel", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_sidePanel", "p_container", "0.11 0.11 0.11 0.95", "SIDE PANEL", 11, "0.125 0.7", "0.245 0.89", $"octawelcome_editpanel sidePanel", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_contentPanel", "p_container", "0.11 0.11 0.11 0.95", "CONTENT PANEL", 11, "0.25 0.7", "0.37 0.89", $"octawelcome_editpanel contentPanel", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_titlePanel", "p_container", "0.11 0.11 0.11 0.95", "TITLE PANEL", 11, "0.375 0.7", "0.495 0.89", $"octawelcome_editpanel titlePanel", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_logoPanel", "p_container", "0.11 0.11 0.11 0.95", "LOGO IMAGE", 11, "0.5 0.7", "0.62 0.89", $"octawelcome_editpanel logoPanel", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_closeButton", "p_container", "0.11 0.11 0.11 0.95", "CLOSE BUTTON", 11, "0.625 0.7", "0.745 0.89", $"octawelcome_editpanel closeButton", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_nextButton", "p_container", "0.11 0.11 0.11 0.95", "NEXT PAGE \nBUTTON", 11, "0.75 0.7", "0.87 0.89", $"octawelcome_editpanel nextButton", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_previousButton", "p_container", "0.11 0.11 0.11 0.95", "PREVIOUS PAGE \nBUTTON", 11, "0.875 0.7", "1 0.89", $"octawelcome_editpanel previousButton", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            // buttons 
            CUIClass.CreateText(ref editPanelMenu, "p_panelsText2", "p_container", "1 1 1 1", $"Tab Buttons", 14, "0.00 0.52", "1 0.61", TextAnchor.UpperLeft, $"robotocondensed-bold.ttf", "0 0 0 0", $"0 0");
            CUIClass.CreateButton(ref editPanelMenu, "p_menuBtn1", "p_container", "0.11 0.11 0.11 0.95", "TAB 1", 11, "0 0.3", "0.1 0.49", $"octawelcome_editpanel menuBtn1", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_menuBtn2", "p_container", "0.11 0.11 0.11 0.95", "TAB 2", 11, "0.11 0.3", "0.2 0.49", $"octawelcome_editpanel menuBtn2", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_menuBtn3", "p_container", "0.11 0.11 0.11 0.95", "TAB 3", 11, "0.21 0.3", "0.3 0.49", $"octawelcome_editpanel menuBtn3", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_menuBtn4", "p_container", "0.11 0.11 0.11 0.95", "TAB 4", 11, "0.31 0.3", "0.4 0.49", $"octawelcome_editpanel menuBtn4", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_menuBtn5", "p_container", "0.11 0.11 0.11 0.95", "TAB 5", 11, "0.41 0.3", "0.5 0.49", $"octawelcome_editpanel menuBtn5", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_menuBtn6", "p_container", "0.11 0.11 0.11 0.95", "TAB 6", 11, "0.51 0.3", "0.6 0.49", $"octawelcome_editpanel menuBtn6", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_menuBtn7", "p_container", "0.11 0.11 0.11 0.95", "TAB 7", 11, "0.61 0.3", "0.7 0.49", $"octawelcome_editpanel menuBtn7", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_menuBtn8", "p_container", "0.11 0.11 0.11 0.95", "TAB 8", 11, "0.71 0.3", "0.8 0.49", $"octawelcome_editpanel menuBtn8", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_menuBtn9", "p_container", "0.11 0.11 0.11 0.95", "TAB 9", 11, "0.81 0.3", "0.9 0.49", $"octawelcome_editpanel menuBtn9", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreateButton(ref editPanelMenu, "p_menuBtn10", "p_container", "0.11 0.11 0.11 0.95", "TAB 10", 11, "0.91 0.3", "1 0.49", $"octawelcome_editpanel menuBtn10", "", "1 1 1 0.7", 0f, TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "assets/content/ui/uibackgroundblur.mat");
            // how to use
            CUIClass.CreateText(ref editPanelMenu, "p_panelsText3", "p_mainPanel", "1 1 1 1", $"Usage", 16, "0.02 0.42", "1 0.58", TextAnchor.UpperLeft, $"robotocondensed-bold.ttf", "0 0 0 0", $"0 0");
            //wasd
            CUIClass.CreatePanel(ref editPanelMenu, "wasd_container", "p_mainPanel", "0 0 0 0.6", "0.02 0.04", "0.33 0.53", false, 0f, "assets/icons/iconmaterial.mat");
            if (ImageLibrary != null) CUIClass.CreateImage(ref editPanelMenu, "wasd_container", (string)ImageLibrary?.Call("GetImage", $"wasd_panelsText3"), "0.15 0.40", "0.85 0.90", 2f);
            else CUIClass.CreateImage(ref editPanelMenu, "wasd_container", "https://rustplugins.net/products/welcomepanel/keyicons.png", "0.15 0.40", "0.85 0.90", 2f);
            CUIClass.CreateText(ref editPanelMenu, "wasd_panelsText3", "wasd_container", "1 1 1 0.7", $"When you start editing panel, choose which side of the panel you want edit by pressing your movement keys.", 14, "0.03 0.0", "0.97 0.40", TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "0 0 0 0", $"0 0");

            //mouse
            CUIClass.CreatePanel(ref editPanelMenu, "mouse_container", "p_mainPanel", "0 0 0 0.6", "0.34 0.04", "0.65 0.53", false, 0f, "assets/icons/iconmaterial.mat");
            if (ImageLibrary != null) CUIClass.CreateImage(ref editPanelMenu, "mouse_container", (string)ImageLibrary?.Call("GetImage", $"mouse_panelsText3"), "0.15 0.45", "0.85 0.95", 2f);
            else CUIClass.CreateImage(ref editPanelMenu, "mouse_container", "https://rustplugins.net/products/welcomepanel/mouseicon.png", "0.15 0.40", "0.85 0.90", 2f);
            CUIClass.CreateText(ref editPanelMenu, "mouse_panelsText3", "mouse_container", "1 1 1 0.7", $"After selecting side you can change anchor value by clicking mouse buttons. Left click to increase, Right click to decrease.", 14, "0.03 0.0", "0.97 0.40", TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "0 0 0 0", $"0 0");

            //back
            CUIClass.CreatePanel(ref editPanelMenu, "back_container", "p_mainPanel", "0 0 0 0.6", "0.66 0.04", "0.98 0.53", false, 0f, "assets/icons/iconmaterial.mat");
            if (ImageLibrary != null) CUIClass.CreateImage(ref editPanelMenu, "back_container", (string)ImageLibrary?.Call("GetImage", $"back_panelsText3"), "0.3 0.45", "0.7 0.85", 2f);
            else CUIClass.CreateImage(ref editPanelMenu, "back_container", "https://rustplugins.net/products/welcomepanel/Rkeyicon.png", "0.15 0.40", "0.85 0.90", 2f);
            CUIClass.CreateText(ref editPanelMenu, "back_panelsText3", "back_container", "1 1 1 0.7", $"Press your 'Reload' key if you want return to this menu.", 14, "0.07 0.0", "0.93 0.40", TextAnchor.MiddleCenter, $"robotocondensed-bold.ttf", "0 0 0 0", $"0 0");


            #endregion
            DestroyEditPanelUi(player);
            CuiHelper.AddUi(player, editPanelMenu);

        }

        private void DestroyEditPanelUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "empty");
            CuiHelper.DestroyUi(player, "p_offsetContainer");
        }

        #endregion

        #region [Kits Tab] — собственная вкладка наборов (данные напрямую из плагина Kits)

        // Фирменная палитра Octavian (золото на стекле, эталон OctaBoss)
        private const string KGold     = "0.82 0.61 0.20 1";
        private const string KGoldDark = "0.48 0.32 0.10 1";
        private const string KGlassBg  = "0.02 0.018 0.015 0.92";
        private const string KGlassRow = "0.08 0.07 0.05 0.85";
        private const string KGlassHov = "0.13 0.105 0.06 0.95";
        private const string KGlassLt  = "0.05 0.045 0.035 0.92";
        private const string KTWhite   = "1 0.98 0.90 1";
        private const string KGreen    = "0.20 0.52 0.27 0.92";
        private const string KRed      = "0.58 0.13 0.10 0.92";

        // Сетка 3x3 внутри imagePanel
        private static readonly string[] _kitCardAnchors =
        {
            "0.01 0.72-0.32 0.95", "0.34 0.72-0.65 0.95", "0.67 0.72-0.98 0.95",
            "0.01 0.39-0.32 0.62", "0.34 0.39-0.65 0.62", "0.67 0.39-0.98 0.62",
            "0.01 0.06-0.32 0.29", "0.34 0.06-0.65 0.29", "0.67 0.06-0.98 0.29"
        };

        // Ячейки предметов внутри секций детального просмотра
        private static readonly string[] _kitMainA =
        {
            "0.01 0.75-0.165 0.98", "0.175 0.75-0.330 0.98", "0.340 0.75-0.495 0.98", "0.505 0.75-0.660 0.98", "0.670 0.75-0.825 0.98", "0.835 0.75-0.99 0.98",
            "0.01 0.505-0.165 0.735", "0.175 0.505-0.330 0.735", "0.340 0.505-0.495 0.735", "0.505 0.505-0.660 0.735", "0.670 0.505-0.825 0.735", "0.835 0.505-0.99 0.735",
            "0.01 0.265-0.165 0.495", "0.175 0.265-0.330 0.495", "0.340 0.265-0.495 0.495", "0.505 0.265-0.660 0.495", "0.670 0.265-0.825 0.495", "0.835 0.265-0.99 0.495",
            "0.01 0.02-0.165 0.250", "0.175 0.02-0.330 0.250", "0.340 0.02-0.495 0.250", "0.505 0.02-0.660 0.250", "0.670 0.02-0.825 0.250", "0.835 0.02-0.99 0.250",
        };

        private static readonly string[] _kitBeltA =
        {
            "0.01 0.04-0.165 0.94", "0.175 0.04-0.330 0.94", "0.340 0.04-0.495 0.94", "0.505 0.04-0.660 0.94", "0.670 0.04-0.825 0.94", "0.835 0.04-0.99 0.94",
        };

        private static readonly string[] _kitClothA =
        {
            "0.01 0.04-0.14 0.96", "0.15 0.04-0.28 0.96", "0.29 0.04-0.42 0.96", "0.43 0.04-0.56 0.96", "0.57 0.04-0.70 0.96", "0.71 0.04-0.845 0.96", "0.855 0.04-0.99 0.96"
        };

        // Список наборов, которые можно показывать игроку (скрытые и auth-only отфильтровываем)
        private List<string> KitsTab_List(BasePlayer player)
        {
            var list = new List<string>();
            if (Kits == null) return list;

            string[] all = null;
            try { all = Kits.Call("GetAllKits") as string[]; } catch { }
            if (all == null) return list;

            bool isAdmin = player.IsAdmin;
            foreach (string name in all)
            {
                JObject kit = null;
                try { kit = Kits.Call("GetKitObject", name) as JObject; } catch { }
                if (kit == null) continue;

                if (!isAdmin && kit.SelectToken("IsHidden")?.Value<bool>() == true) continue;
                int reqAuth = kit.SelectToken("RequiredAuth")?.Value<int>() ?? 0;
                if (player.net.connection.authLevel < reqAuth) continue;

                list.Add(name);
            }
            return list;
        }

        private string KitFmtTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalDays >= 1) return $"{ts.Days} ДН {ts.Hours} Ч";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} Ч {ts.Minutes} МИН";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes} МИН {ts.Seconds} СЕК";
            return $"{ts.Seconds} СЕК";
        }

        // Текст и цвет кнопки набора: "ТЕКСТ%ЦВЕТ"
        private string KitsTab_BtnState(BasePlayer player, string kitname)
        {
            JObject kit = null;
            try { kit = Kits?.Call("GetKitObject", kitname) as JObject; } catch { }
            if (kit == null) return $"НЕДОСТУПЕН%{KGlassLt}";

            double cdLeft = 0;
            try { cdLeft = Convert.ToDouble(Kits.Call("GetPlayerKitCooldown", (ulong)player.userID, kitname)); } catch { }
            if (cdLeft > 0) return $"⏱ {KitFmtTime(cdLeft)}%{KRed}";

            string perm = kit.SelectToken("RequiredPermission")?.ToString() ?? "";
            if (!string.IsNullOrEmpty(perm) && !permission.UserHasPermission(player.UserIDString, perm))
                return $"НЕТ ДОСТУПА%{KGlassLt}";

            int maxUses = kit.SelectToken("MaximumUses")?.Value<int>() ?? 0;
            if (maxUses > 0)
            {
                int used = 0;
                try { used = Convert.ToInt32(Kits.Call("GetPlayerKitUses", (ulong)player.userID, kitname)); } catch { }
                if (maxUses - used < 1) return $"ЛИМИТ ИСЧЕРПАН%{KGlassLt}";
            }

            int cost = kit.SelectToken("Cost")?.Value<int>() ?? 0;
            return cost > 0 ? $"ЗАБРАТЬ • {cost}$%{KGreen}" : $"ЗАБРАТЬ%{KGreen}";
        }

        private void KitsTab_Show(BasePlayer player, int page = 0)
        {
            var ui = new CuiElementContainer();

            if (Kits == null)
            {
                CUIClass.CreateText(ref ui, "owkit_err", "imagePanel", "1 1 1 0.6",
                    "Система наборов сейчас недоступна.", 16, "0 0", "1 1", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                KitsTab_Destroy(player);
                CuiHelper.AddUi(player, ui);
                return;
            }

            var kits = KitsTab_List(player);
            if (kits.Count == 0)
            {
                CUIClass.CreateText(ref ui, "owkit_err", "imagePanel", "1 1 1 0.6",
                    "На сервере пока нет доступных наборов.", 16, "0 0", "1 1", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                KitsTab_Destroy(player);
                CuiHelper.AddUi(player, ui);
                return;
            }

            int start = page * 9;
            for (int i = 0; i < 9; i++)
            {
                if (start + i >= kits.Count) break;
                string kitname = kits[start + i];
                string[] a = _kitCardAnchors[i].Split('-');
                if (a.Length < 2) continue;

                string descr = "";
                try { descr = Kits.Call("GetKitDescription", kitname) as string ?? ""; } catch { }
                if (descr.Length > 60) descr = descr.Substring(0, 60) + "...";

                string image = "";
                try { image = Kits.Call("GetKitImage", kitname) as string ?? ""; } catch { }

                int cooldownSec = 0, maxUses = 0, used = 0;
                try { cooldownSec = Convert.ToInt32(Kits.Call("GetKitCooldown", kitname)); } catch { }
                try { maxUses = Convert.ToInt32(Kits.Call("GetKitMaxUses", kitname)); } catch { }
                try { used = Convert.ToInt32(Kits.Call("GetPlayerKitUses", (ulong)player.userID, kitname)); } catch { }

                string cdText = cooldownSec > 0 ? KitFmtTime(cooldownSec) : "НЕТ";
                string usesText = maxUses > 0 ? Math.Max(0, maxUses - used).ToString() : "БЕЗ ЛИМИТА";

                // карточка набора: тёмное стекло + золотая полоска сверху
                CUIClass.CreatePanel(ref ui, $"owkit_card_{i}", "imagePanel", KGlassBg, a[0], a[1], false, 0f, "assets/icons/iconmaterial.mat");
                CUIClass.CreatePanel(ref ui, $"owkit_gold_{i}", $"owkit_card_{i}", KGold, "0 0.97", "1 1", false, 0f, "assets/icons/iconmaterial.mat");

                // иконка — квадрат фиксированной ширины, чтобы картинка не растягивалась
                CUIClass.CreatePanel(ref ui, $"owkit_icon_{i}", $"owkit_card_{i}", KGlassRow, "0.03 0.34", "0.03 0.92", false, 0f, "assets/icons/iconmaterial.mat", "", "0 0", "80 0");
                if (!string.IsNullOrEmpty(image))
                    CUIClass.CreateImage(ref ui, $"owkit_icon_{i}", Img3(image), "0.06 0.06", "0.94 0.94", 0.2f);

                string title = $"<color=#FFD35C><b>{kitname.ToUpper()}</b></color>\n" +
                               $"<size=10><color=#9E9482>{descr}</color></size>\n\n" +
                               $"<size=10><color=#8E8777>КД:</color> <color=#FFD35C>{cdText}</color>   <color=#8E8777>Лимит:</color> <color=#FFD35C>{usesText}</color></size>";
                CUIClass.CreateText(ref ui, $"owkit_title_{i}", $"owkit_card_{i}", KTWhite, title, 13, "0.33 0.33", "0.97 0.92", TextAnchor.UpperLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                CUIClass.CreateButton(ref ui, $"owkit_view_{i}", $"owkit_card_{i}", KGlassHov, "<color=#FFD35C>ОБЗОР</color>", 11, "0.03 0.05", "0.32 0.26",
                    $"octawelcome_kit_view {kitname.Replace(" ", "%")} {page}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

                string[] btn = KitsTab_BtnState(player, kitname).Split('%');
                CUIClass.CreateButton(ref ui, $"owkit_claim_{i}", $"owkit_card_{i}", btn[1], $"<b>{btn[0]}</b>", 11, "0.35 0.05", "0.97 0.26",
                    $"octawelcome_kit_claim {kitname.Replace(" ", "%")} {page} 0", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }

            // пагинация — узкая полоса внизу панели, не перекрывает карточки
            int totalPages = (int)Math.Ceiling(kits.Count / 9.0);
            if (totalPages > 1)
            {
                if (page > 0)
                    CUIClass.CreateButton(ref ui, "owkit_prev", "imagePanel", KGlassRow, "<color=#FFD35C>‹</color>", 13, "0.40 0.004", "0.46 0.048",
                        $"octawelcome_kit_page {page - 1}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                CUIClass.CreateText(ref ui, "owkit_pg", "imagePanel", "0.58 0.53 0.43 1", $"стр. {page + 1}/{totalPages}", 10, "0.46 0.004", "0.54 0.048", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                if (page + 1 < totalPages)
                    CUIClass.CreateButton(ref ui, "owkit_next", "imagePanel", KGlassRow, "<color=#FFD35C>›</color>", 13, "0.54 0.004", "0.60 0.048",
                        $"octawelcome_kit_page {page + 1}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }

            KitsTab_Destroy(player);
            CuiHelper.AddUi(player, ui);
        }

        // Ячейки предметов секции детального просмотра
        private void KitsTab_ItemCells(ref CuiElementContainer ui, string parent, string[] cellAnchors, JToken items, string prefix)
        {
            for (int i = 0; i < cellAnchors.Length; i++)
            {
                string[] a = cellAnchors[i].Split('-');
                CUIClass.CreatePanel(ref ui, $"{prefix}{i}", parent, KGlassRow, a[0], a[1], false, 0.2f, "assets/icons/iconmaterial.mat");

                JToken item = items != null && i < items.Count() ? items[i] : null;
                if (item == null) continue;

                try
                {
                    string shortname = item.SelectToken("Shortname")?.ToString();
                    var def = string.IsNullOrEmpty(shortname) ? null : ItemManager.FindItemDefinition(shortname);
                    if (def == null) continue; // битый shortname — пустая ячейка, без падения

                    ulong skin = 0;
                    try { skin = item.SelectToken("SkinID")?.Value<ulong>() ?? 0UL; } catch { }

                    ui.Add(new CuiElement
                    {
                        Parent = $"{prefix}{i}",
                        Name = $"{prefix}{i}_img",
                        Components =
                        {
                            new CuiImageComponent { ItemId = def.itemid, SkinId = skin, FadeIn = 0.2f },
                            new CuiRectTransformComponent { AnchorMin = "0.10 0.10", AnchorMax = "0.90 0.90" }
                        }
                    });
                    CUIClass.CreateText(ref ui, $"{prefix}{i}_am", $"{prefix}{i}", "0.95 0.75 0.30 1",
                        $"x{item.SelectToken("Amount")}", 10, "0.05 0.00", "0.95 0.95", TextAnchor.LowerRight, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                }
                catch { }
            }
        }

        private void KitsTab_ViewKit(BasePlayer player, string kitname, int page)
        {
            var ui = new CuiElementContainer();

            JObject kit = null;
            try { kit = Kits?.Call("GetKitObject", kitname) as JObject; } catch { }
            if (kit == null) { KitsTab_Show(player, page); return; }

            var belt = kit.SelectToken("BeltItems");
            var main = kit.SelectToken("MainItems");
            var wear = kit.SelectToken("WearItems");

            // правая часть — содержимое набора
            CUIClass.CreatePanel(ref ui, "owkit_view_inv", "imagePanel", KGlassBg, "0.37 0.04", "0.99 0.96", false, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "owkit_inv_gold", "owkit_view_inv", KGold, "0 0.985", "1 1", false, 0f, "assets/icons/iconmaterial.mat");

            CUIClass.CreateText(ref ui, "owkit_sec_main", "owkit_view_inv", KGold, "◆ <b>ОСНОВНОЙ ИНВЕНТАРЬ</b>", 11, "0.03 0.93", "0.97 0.97", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_div_main", "owkit_view_inv", KGoldDark, "0.03 0.925", "0.97 0.928", false, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "owkit_inv_main", "owkit_view_inv", "0 0 0 0", "0.02 0.44", "0.98 0.91", false, 0f, "assets/icons/iconmaterial.mat");
            KitsTab_ItemCells(ref ui, "owkit_inv_main", _kitMainA, main, "owkit_mitem_");

            CUIClass.CreateText(ref ui, "owkit_sec_belt", "owkit_view_inv", KGold, "◆ <b>БЫСТРЫЙ ДОСТУП</b>", 11, "0.03 0.38", "0.97 0.42", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_div_belt", "owkit_view_inv", KGoldDark, "0.03 0.375", "0.97 0.378", false, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "owkit_inv_belt", "owkit_view_inv", "0 0 0 0", "0.02 0.24", "0.98 0.36", false, 0f, "assets/icons/iconmaterial.mat");
            KitsTab_ItemCells(ref ui, "owkit_inv_belt", _kitBeltA, belt, "owkit_bitem_");

            CUIClass.CreateText(ref ui, "owkit_sec_wear", "owkit_view_inv", KGold, "◆ <b>ЭКИПИРОВКА</b>", 11, "0.03 0.19", "0.97 0.23", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_div_wear", "owkit_view_inv", KGoldDark, "0.03 0.185", "0.97 0.188", false, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "owkit_inv_wear", "owkit_view_inv", "0 0 0 0", "0.02 0.05", "0.98 0.17", false, 0f, "assets/icons/iconmaterial.mat");
            KitsTab_ItemCells(ref ui, "owkit_inv_wear", _kitClothA, wear, "owkit_witem_");

            // левая часть — информация о наборе
            CUIClass.CreatePanel(ref ui, "owkit_view_info", "imagePanel", KGlassBg, "0.01 0.04", "0.35 0.96", false, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "owkit_v_gold", "owkit_view_info", KGold, "0 0.985", "1 1", false, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreateText(ref ui, "owkit_v_title", "owkit_view_info", "0.95 0.75 0.30 1", $"◆ <b>{kitname.ToUpper()}</b>", 18, "0.06 0.91", "0.94 0.97", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_v_div1", "owkit_view_info", KGoldDark, "0.06 0.895", "0.94 0.898", false, 0f, "assets/icons/iconmaterial.mat");

            int cooldownSec = 0, maxUses = 0, used = 0;
            try { cooldownSec = Convert.ToInt32(Kits.Call("GetKitCooldown", kitname)); } catch { }
            try { maxUses = Convert.ToInt32(Kits.Call("GetKitMaxUses", kitname)); } catch { }
            try { used = Convert.ToInt32(Kits.Call("GetPlayerKitUses", (ulong)player.userID, kitname)); } catch { }

            string cdText = cooldownSec > 0 ? KitFmtTime(cooldownSec) : "НЕТ";
            string usesText = maxUses > 0 ? Math.Max(0, maxUses - used).ToString() : "БЕЗ ЛИМИТА";
            int cost = kit.SelectToken("Cost")?.Value<int>() ?? 0;
            string costText = cost > 0 ? $"{cost}$" : "БЕСПЛАТНО";
            string perm = kit.SelectToken("RequiredPermission")?.ToString() ?? "";
            string permStatus = string.IsNullOrEmpty(perm) || permission.UserHasPermission(player.UserIDString, perm)
                ? "<color=#A3E06C>Разрешён</color>" : "<color=#E05A47>Требуется привилегия</color>";

            string info = $"<color=#8E8777>• Кулдаун:</color>  <color=#FFD35C>{cdText}</color>\n" +
                          $"<color=#8E8777>• Доступно:</color>  <color=#FFD35C>{usesText}</color>\n" +
                          $"<color=#8E8777>• Стоимость:</color> <color=#FFD35C>{costText}</color>\n" +
                          $"<color=#8E8777>• Статус:</color>    {permStatus}";
            CUIClass.CreateText(ref ui, "owkit_v_info", "owkit_view_info", KTWhite, info, 12, "0.06 0.65", "0.94 0.88", TextAnchor.UpperLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_v_div2", "owkit_view_info", KGoldDark, "0.06 0.635", "0.94 0.638", false, 0f, "assets/icons/iconmaterial.mat");

            string descr = "";
            try { descr = Kits.Call("GetKitDescription", kitname) as string ?? ""; } catch { }
            if (string.IsNullOrEmpty(descr)) descr = "Описание набора не указано.";
            CUIClass.CreateText(ref ui, "owkit_v_descr", "owkit_view_info", "0.58 0.53 0.43 1", descr, 11, "0.06 0.20", "0.94 0.62", TextAnchor.UpperLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");

            CUIClass.CreateButton(ref ui, "owkit_v_back", "owkit_view_info", KGlassRow, "<color=#FFD35C>← НАЗАД</color>", 11, "0.06 0.06", "0.45 0.16",
                $"octawelcome_kit_page {page}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            string[] btn = KitsTab_BtnState(player, kitname).Split('%');
            CUIClass.CreateButton(ref ui, "owkit_v_claim", "owkit_view_info", btn[1], $"<b>{btn[0]}</b>", 11, "0.49 0.06", "0.94 0.16",
                $"octawelcome_kit_claim {kitname.Replace(" ", "%")} {page} 1", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            // добиваем сетку карточек и старые панели, рисуем новое
            KitsTab_Destroy(player);
            CuiHelper.AddUi(player, ui);
        }

        private void KitsTab_Destroy(BasePlayer player)
        {
            // карточки тянут за собой дочерние элементы
            for (int i = 0; i < 9; i++)
                CuiHelper.DestroyUi(player, $"owkit_card_{i}");

            CuiHelper.DestroyUi(player, "owkit_prev");
            CuiHelper.DestroyUi(player, "owkit_next");
            CuiHelper.DestroyUi(player, "owkit_pg");
            CuiHelper.DestroyUi(player, "owkit_err");
            CuiHelper.DestroyUi(player, "owkit_view_inv");
            CuiHelper.DestroyUi(player, "owkit_view_info");
        }

        [ConsoleCommand("octawelcome_kit_page")]
        private void octawelcome_kit_page(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 1) return;

            int page = 0;
            int.TryParse(args[0], out page);
            page = Math.Max(0, page);
            CuiHelper.DestroyUi(player, "owkit_view_inv");
            CuiHelper.DestroyUi(player, "owkit_view_info");
            KitsTab_Show(player, page);
        }

        [ConsoleCommand("octawelcome_kit_view")]
        private void octawelcome_kit_view(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 2) return;

            string kitname = args[0].Replace("%", " ");
            int page = 0;
            int.TryParse(args[1], out page);
            KitsTab_ViewKit(player, kitname, page);
        }

        [ConsoleCommand("octawelcome_kit_claim")]
        private void octawelcome_kit_claim(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || Kits == null) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 3) return;

            string kitname = args[0].Replace("%", " ");
            int page = 0;
            int.TryParse(args[1], out page);
            bool inView = args[2] == "1";

            object ok = null;
            try { ok = Kits.Call("TryClaimKit", player, kitname, false); } catch { }
            if (ok is bool && (bool)ok)
            {
                // перерисовываем — кнопка уходит в кулдаун/лимит
                if (inView) KitsTab_ViewKit(player, kitname, page);
                else KitsTab_Show(player, page);
            }
        }

        #endregion

        #region !Presets

        private void SetPresetValues(int presetNumber)
        {


            if (presetNumber == 1)
            {
                //background
                cuiData["background"].anchorMin = "0 0"; cuiData["background"].anchorMax = "1 1";
                cuiData["background"].color = "0 0 0 0.8"; cuiData["background"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["background"].image = ""; cuiData["background"].fade = 0.5f;
                //background2
                cuiData["background2"].anchorMin = "0 0"; cuiData["background2"].anchorMax = "1 1";
                cuiData["background2"].color = "0 0 0 0.0"; cuiData["background2"].material = "assets/icons/iconmaterial.mat";
                cuiData["background2"].image = ""; cuiData["background2"].fade = 0f;
                //offsetContainer
                cuiData["offsetContainer"].anchorMin = "0.5 0.5"; cuiData["offsetContainer"].anchorMax = "0.5 0.5";
                cuiData["offsetContainer"].color = "0 0 0 0.0"; cuiData["offsetContainer"].material = "assets/icons/iconmaterial.mat";
                cuiData["offsetContainer"].image = ""; cuiData["offsetContainer"].fade = 0f;
                //main panel
                cuiData["mainPanel"].anchorMin = "0.32 0.175"; cuiData["mainPanel"].anchorMax = "0.80 0.748";
                cuiData["mainPanel"].color = "0 0 0 0.75"; cuiData["mainPanel"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["mainPanel"].image = ""; cuiData["mainPanel"].fade = 0.5f;
                //side panel
                cuiData["sidePanel"].anchorMin = "0.185 0.175"; cuiData["sidePanel"].anchorMax = "0.31 0.748";
                cuiData["sidePanel"].color = "0 0 0 0.75"; cuiData["sidePanel"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["sidePanel"].image = ""; cuiData["sidePanel"].fade = 0.5f;
                //title panel
                cuiData["titlePanel"].anchorMin = "0.178 0.745"; cuiData["titlePanel"].anchorMax = "0.9 0.85";
                cuiData["titlePanel"].color = "0 0 0 0"; cuiData["titlePanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["titlePanel"].image = ""; cuiData["titlePanel"].fade = 0.5f;
                //logo panel
                cuiData["logoPanel"].anchorMin = "0.565 0.76"; cuiData["logoPanel"].anchorMax = "0.610 0.833";
                cuiData["logoPanel"].color = "0 0 0 0"; cuiData["logoPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["logoPanel"].image = ""; cuiData["logoPanel"].fade = 0.5f;
                //close button
                cuiData["closeButton"].anchorMin = "0.72 0.115"; cuiData["closeButton"].anchorMax = "0.80 0.16";
                cuiData["closeButton"].color = "0.56 0.20 0.15 0.9"; cuiData["closeButton"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["closeButton"].image = ""; cuiData["closeButton"].fade = 0.5f;
                //contentPanel
                cuiData["contentPanel"].anchorMin = "0.32 0.175"; cuiData["contentPanel"].anchorMax = "0.80 0.748";
                cuiData["contentPanel"].color = "0 0 0 0.0"; cuiData["contentPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["contentPanel"].image = ""; cuiData["contentPanel"].fade = 0.5f;
                //menu buttons
                //hl button
                cuiData["highlightBtn"].anchorMin = "0 0"; cuiData["highlightBtn"].anchorMax = "1 1";
                cuiData["highlightBtn"].color = "0.16 0.34 0.49 1.0"; cuiData["highlightBtn"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["highlightBtn"].image = ""; cuiData["highlightBtn"].fade = 0f;
                //btn 1
                cuiData["menuBtn1"].anchorMin = "0.1845 0.700"; cuiData["menuBtn1"].anchorMax = "0.3095 0.748";
                cuiData["menuBtn1"].color = "0 0 0 0"; cuiData["menuBtn1"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn1"].image = "https://rustplugins.net/products/welcomepanel/1/home.png"; cuiData["menuBtn1"].fade = 0f;
                //btn 2
                cuiData["menuBtn2"].anchorMin = "0.1845 0.650"; cuiData["menuBtn2"].anchorMax = "0.3095 0.697";
                cuiData["menuBtn2"].color = "0 0 0 0"; cuiData["menuBtn2"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn2"].image = "https://rustplugins.net/products/welcomepanel/1/rules.png"; cuiData["menuBtn2"].fade = 0f;
                //btn 3
                cuiData["menuBtn3"].anchorMin = "0.1845 0.600"; cuiData["menuBtn3"].anchorMax = "0.3095 0.647";
                cuiData["menuBtn3"].color = "0 0 0 0"; cuiData["menuBtn3"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn3"].image = "https://rustplugins.net/products/welcomepanel/1/shop.png"; cuiData["menuBtn3"].fade = 0f;
                //btn 4
                cuiData["menuBtn4"].anchorMin = "0.1845 0.550"; cuiData["menuBtn4"].anchorMax = "0.3095 0.597";
                cuiData["menuBtn4"].color = "0 0 0 0"; cuiData["menuBtn4"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn4"].image = "https://rustplugins.net/products/welcomepanel/1/wipe.png"; cuiData["menuBtn4"].fade = 0f;
                //btn 5
                cuiData["menuBtn5"].anchorMin = "0.1845 0.500"; cuiData["menuBtn5"].anchorMax = "0.3095 0.547";
                cuiData["menuBtn5"].color = "0 0 0 0"; cuiData["menuBtn5"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn5"].image = "https://rustplugins.net/products/welcomepanel/1/star.png"; cuiData["menuBtn5"].fade = 0f;
                //btn 6
                cuiData["menuBtn6"].anchorMin = "0.1845 0.450"; cuiData["menuBtn6"].anchorMax = "0.3095 0.497";
                cuiData["menuBtn6"].color = "0 0 0 0"; cuiData["menuBtn6"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn6"].image = "https://rustplugins.net/products/welcomepanel/1/discord.png"; cuiData["menuBtn6"].fade = 0f;
                //btn 7
                cuiData["menuBtn7"].anchorMin = "0.1845 0.400"; cuiData["menuBtn7"].anchorMax = "0.3095 0.447";
                cuiData["menuBtn7"].color = "0 0 0 0"; cuiData["menuBtn7"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn7"].image = "https://rustplugins.net/products/welcomepanel/1/admin.png"; cuiData["menuBtn7"].fade = 0f;
                //btn 8
                cuiData["menuBtn8"].anchorMin = "0.1845 0.350"; cuiData["menuBtn8"].anchorMax = "0.3095 0.397";
                cuiData["menuBtn8"].color = "0 0 0 0"; cuiData["menuBtn8"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn8"].image = "https://rustplugins.net/products/welcomepanel/1/shop.png"; cuiData["menuBtn8"].fade = 0f;
                //btn 9
                cuiData["menuBtn9"].anchorMin = "0.1845 0.300"; cuiData["menuBtn9"].anchorMax = "0.3095 0.347";
                cuiData["menuBtn9"].color = "0 0 0 0"; cuiData["menuBtn9"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn9"].image = "https://rustplugins.net/products/welcomepanel/1/rules.png"; cuiData["menuBtn9"].fade = 0f;
                //btn 10
                cuiData["menuBtn10"].anchorMin = "0.1845 0.250"; cuiData["menuBtn10"].anchorMax = "0.3095 0.297";
                cuiData["menuBtn10"].color = "0 0 0 0"; cuiData["menuBtn10"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn10"].image = "https://rustplugins.net/products/welcomepanel/1/bug.png"; cuiData["menuBtn10"].fade = 0f;
                //page buttons
                //next
                cuiData["nextButton"].anchorMin = "0.95 0.013"; cuiData["nextButton"].anchorMax = "0.99 0.06";
                cuiData["nextButton"].color = "0.16 0.34 0.49 0.6"; cuiData["nextButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["nextButton"].image = "https://rustplugins.net/products/welcomepanel/3/page-next.png"; cuiData["nextButton"].fade = 0f;
                //prev
                cuiData["previousButton"].anchorMin = "0.9 0.013"; cuiData["previousButton"].anchorMax = "0.94 0.06";
                cuiData["previousButton"].color = "0.16 0.34 0.49 0.6"; cuiData["previousButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["previousButton"].image = "https://rustplugins.net/products/welcomepanel/3/page-prev.png"; cuiData["previousButton"].fade = 0f;
                //config
                config.buttonSettings.mainAlign = TextAnchor.MiddleCenter;
                config.tab1Settings.btnTitle = " ГЛАВНАЯ";
                config.tab2Settings.btnTitle = " ПРАВИЛА";
                config.tab3Settings.btnTitle = "   НАБОРЫ";
                config.tab4Settings.btnTitle = "   ВАЙПЫ";
                config.tab5Settings.btnTitle = "  VIP";
                config.tab6Settings.btnTitle = "  ДИСКОРД";
                config.tab7Settings.btnTitle = " МАГАЗИН";
                config.tab8Settings.btnTitle = " АДМИНЫ";
                config.tab9Settings.btnTitle = " НОВОСТИ";
                config.tab10Settings.btnTitle = " БОССЫ";
                config.titleSettings.titleText = "<size=55><color=#E8B84B>OCTAVIAN</color></size>";
            }

            if (presetNumber == 2)
            {
                //background
                cuiData["background"].anchorMin = "0 0"; cuiData["background"].anchorMax = "1 1";
                cuiData["background"].color = "0 0 0 0.85"; cuiData["background"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["background"].image = ""; cuiData["background"].fade = 0.5f;
                //background2
                cuiData["background2"].anchorMin = "0 0"; cuiData["background2"].anchorMax = "1 1";
                cuiData["background2"].color = "0 0 0 0.0"; cuiData["background2"].material = "assets/icons/iconmaterial.mat";
                cuiData["background2"].image = ""; cuiData["background2"].fade = 0f;
                //offsetContainer
                cuiData["offsetContainer"].anchorMin = "0.5 0.5"; cuiData["offsetContainer"].anchorMax = "0.5 0.5";
                cuiData["offsetContainer"].color = "0 0 0 0.0"; cuiData["offsetContainer"].material = "assets/icons/iconmaterial.mat";
                cuiData["offsetContainer"].image = ""; cuiData["offsetContainer"].fade = 0f;
                //main panel
                cuiData["mainPanel"].anchorMin = "0.22 -0.00499999"; cuiData["mainPanel"].anchorMax = "0.97 1.008";
                cuiData["mainPanel"].color = "0 0 0 0.4"; cuiData["mainPanel"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["mainPanel"].image = ""; cuiData["mainPanel"].fade = 0.5f;
                //side panel
                cuiData["sidePanel"].anchorMin = "0.02500001 -0.00499999"; cuiData["sidePanel"].anchorMax = "0.22 1.008";
                cuiData["sidePanel"].color = "0 0 0 0.75"; cuiData["sidePanel"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["sidePanel"].image = ""; cuiData["sidePanel"].fade = 0.5f;
                //title panel
                cuiData["titlePanel"].anchorMin = "0.0725 0.615"; cuiData["titlePanel"].anchorMax = "0.89 0.87";
                cuiData["titlePanel"].color = "0 0 0 0"; cuiData["titlePanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["titlePanel"].image = ""; cuiData["titlePanel"].fade = 0.5f;
                //logo panel
                cuiData["logoPanel"].anchorMin = "0.05500001 0.68"; cuiData["logoPanel"].anchorMax = "0.2 0.963";
                cuiData["logoPanel"].color = "0 0 0 0"; cuiData["logoPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["logoPanel"].image = "https://rustplugins.net/products/welcomepanel/2/templatelogo.png"; cuiData["logoPanel"].fade = 0.5f;
                //close button
                cuiData["closeButton"].anchorMin = "0.08 0.145"; cuiData["closeButton"].anchorMax = "0.17 0.19";
                cuiData["closeButton"].color = "0.56 0.20 0.15 0.9"; cuiData["closeButton"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["closeButton"].image = ""; cuiData["closeButton"].fade = 0.5f;
                //contentPanel
                cuiData["contentPanel"].anchorMin = "0.25 0.04500001"; cuiData["contentPanel"].anchorMax = "0.94 0.82";
                cuiData["contentPanel"].color = "0 0 0 0.0"; cuiData["contentPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["contentPanel"].image = ""; cuiData["contentPanel"].fade = 0.5f;
                //menu buttons
                //hl button
                cuiData["highlightBtn"].anchorMin = "0 0"; cuiData["highlightBtn"].anchorMax = "1 1";
                cuiData["highlightBtn"].color = "0.16 0.34 0.49 1.0"; cuiData["highlightBtn"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["highlightBtn"].image = ""; cuiData["highlightBtn"].fade = 0f;
                //btn 1
                cuiData["menuBtn1"].anchorMin = "0.02450001 0.61"; cuiData["menuBtn1"].anchorMax = "0.2195 0.668";
                cuiData["menuBtn1"].color = "0 0 0 0"; cuiData["menuBtn1"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn1"].image = ""; cuiData["menuBtn1"].fade = 0f;
                //btn 2
                cuiData["menuBtn2"].anchorMin = "0.02450001 0.55"; cuiData["menuBtn2"].anchorMax = "0.2195 0.607";
                cuiData["menuBtn2"].color = "0 0 0 0"; cuiData["menuBtn2"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn2"].image = ""; cuiData["menuBtn2"].fade = 0f;
                //btn 3
                cuiData["menuBtn3"].anchorMin = "0.02450001 0.49"; cuiData["menuBtn3"].anchorMax = "0.2195 0.547";
                cuiData["menuBtn3"].color = "0 0 0 0"; cuiData["menuBtn3"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn3"].image = ""; cuiData["menuBtn3"].fade = 0f;
                //btn 4
                cuiData["menuBtn4"].anchorMin = "0.02450001 0.43"; cuiData["menuBtn4"].anchorMax = "0.2195 0.487";
                cuiData["menuBtn4"].color = "0 0 0 0"; cuiData["menuBtn4"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn4"].image = ""; cuiData["menuBtn4"].fade = 0f;
                //btn 5
                cuiData["menuBtn5"].anchorMin = "0.02450001 0.37"; cuiData["menuBtn5"].anchorMax = "0.2195 0.427";
                cuiData["menuBtn5"].color = "0 0 0 0"; cuiData["menuBtn5"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn5"].image = ""; cuiData["menuBtn5"].fade = 0f;
                //btn 6
                cuiData["menuBtn6"].anchorMin = "0.02450001 0.31"; cuiData["menuBtn6"].anchorMax = "0.2195 0.367";
                cuiData["menuBtn6"].color = "0 0 0 0"; cuiData["menuBtn6"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn6"].image = ""; cuiData["menuBtn6"].fade = 0f;
                //btn 7
                cuiData["menuBtn7"].anchorMin = "0.02450001 0.25"; cuiData["menuBtn7"].anchorMax = "0.2195 0.307";
                cuiData["menuBtn7"].color = "0 0 0 0"; cuiData["menuBtn7"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn7"].image = ""; cuiData["menuBtn7"].fade = 0f;
                //btn 8
                cuiData["menuBtn8"].anchorMin = "0 0"; cuiData["menuBtn8"].anchorMax = "0 0";
                cuiData["menuBtn8"].color = "0 0 0 0"; cuiData["menuBtn8"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn8"].image = ""; cuiData["menuBtn8"].fade = 0f;
                //btn 9
                cuiData["menuBtn9"].anchorMin = "0 0"; cuiData["menuBtn9"].anchorMax = "0 0";
                cuiData["menuBtn9"].color = "0 0 0 0"; cuiData["menuBtn9"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn9"].image = ""; cuiData["menuBtn9"].fade = 0f;
                //btn 10
                cuiData["menuBtn10"].anchorMin = "0 0"; cuiData["menuBtn10"].anchorMax = "0 0";
                cuiData["menuBtn10"].color = "0 0 0 0"; cuiData["menuBtn10"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn10"].image = ""; cuiData["menuBtn10"].fade = 0f;
                //page buttons
                //next
                cuiData["nextButton"].anchorMin = "0.95 0.013"; cuiData["nextButton"].anchorMax = "0.99 0.06";
                cuiData["nextButton"].color = "0 0 0 0"; cuiData["nextButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["nextButton"].image = "https://rustplugins.net/products/welcomepanel/2/btn-nextpage.png"; cuiData["nextButton"].fade = 0f;
                //prev
                cuiData["previousButton"].anchorMin = "0.9 0.013"; cuiData["previousButton"].anchorMax = "0.94 0.06";
                cuiData["previousButton"].color = "0 0 0 0"; cuiData["previousButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["previousButton"].image = "https://rustplugins.net/products/welcomepanel/2/btn-prevpage.png"; cuiData["previousButton"].fade = 0f;

                config.buttonSettings.mainAlign = TextAnchor.MiddleCenter;
                config.tab1Settings.btnTitle = "<size=22>HOME</size>";
                config.tab2Settings.btnTitle = "<size=22>RULES</size>";
                config.tab3Settings.btnTitle = "<size=22>WIPE CYCLE</size>";
                config.tab4Settings.btnTitle = "<size=22>SERVER INFO</size>";
                config.tab5Settings.btnTitle = "<size=22>DISCORD</size>";
                config.tab6Settings.btnTitle = "<size=22>STEAM GROUP</size>";
                config.tab7Settings.btnTitle = "<size=22>ADMIN</size>";
                config.tab8Settings.btnTitle = "TEMPLATE #2 supports only 6 buttons by default";
                config.tab9Settings.btnTitle = "TEMPLATE #2 supports only 6 buttons by default";
                config.tab10Settings.btnTitle = "TEMPLATE #2 supports only 6 buttons by default";
                config.titleSettings.titleText = "<size=15><color=#E8B84B>OCTAVIAN</color></size>";
            }

            if (presetNumber == 3)
            {
                //background
                cuiData["background"].anchorMin = "0 0"; cuiData["background"].anchorMax = "1 1";
                cuiData["background"].color = "0 0 0 0.8"; cuiData["background"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["background"].image = ""; cuiData["background"].fade = 0.5f;
                //background2
                cuiData["background2"].anchorMin = "0 0"; cuiData["background2"].anchorMax = "1 1";
                cuiData["background2"].color = "0 0 0 0.0"; cuiData["background2"].material = "assets/icons/iconmaterial.mat";
                cuiData["background2"].image = ""; cuiData["background2"].fade = 0f;
                //offsetContainer
                cuiData["offsetContainer"].anchorMin = "0.5 0.53"; cuiData["offsetContainer"].anchorMax = "0.5 0.53";
                cuiData["offsetContainer"].color = "0 0 0 0.0"; cuiData["offsetContainer"].material = "assets/icons/iconmaterial.mat";
                cuiData["offsetContainer"].image = ""; cuiData["offsetContainer"].fade = 0f;
                //main panel
                cuiData["mainPanel"].anchorMin = "0.317 0.175"; cuiData["mainPanel"].anchorMax = "0.80 0.77";
                cuiData["mainPanel"].color = "0 0 0 0.0"; cuiData["mainPanel"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["mainPanel"].image = "https://rustplugins.net/products/welcomepanel/3/main.png"; cuiData["mainPanel"].fade = 0.5f;
                //side panel
                cuiData["sidePanel"].anchorMin = "0.185 0.175"; cuiData["sidePanel"].anchorMax = "0.32 0.77";
                cuiData["sidePanel"].color = "0 0 0 0.0"; cuiData["sidePanel"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["sidePanel"].image = "https://rustplugins.net/products/welcomepanel/3/side.png"; cuiData["sidePanel"].fade = 0.5f;
                //title panel
                cuiData["titlePanel"].anchorMin = "0.218 0.725"; cuiData["titlePanel"].anchorMax = "0.9 0.76";
                cuiData["titlePanel"].color = "0 0 0 0"; cuiData["titlePanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["titlePanel"].image = ""; cuiData["titlePanel"].fade = 0.5f;
                //logo panel
                cuiData["logoPanel"].anchorMin = "0.191 0.73"; cuiData["logoPanel"].anchorMax = "0.206 0.757";
                cuiData["logoPanel"].color = "0 0 0 0"; cuiData["logoPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["logoPanel"].image = "https://rustplugins.net/products/welcomepanel/3/rustlogo.png"; cuiData["logoPanel"].fade = 0.5f;
                //close button
                cuiData["closeButton"].anchorMin = "0.72 0.115"; cuiData["closeButton"].anchorMax = "0.80 0.16";
                cuiData["closeButton"].color = "0.80 0.25 0.16 1"; cuiData["closeButton"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["closeButton"].image = ""; cuiData["closeButton"].fade = 0.5f;
                //contentPanel
                cuiData["contentPanel"].anchorMin = "0.32 0.175"; cuiData["contentPanel"].anchorMax = "0.80 0.72";
                cuiData["contentPanel"].color = "0 0 0 0.0"; cuiData["contentPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["contentPanel"].image = "https://rustplugins.net/products/welcomepanel/3/s-green.png"; cuiData["contentPanel"].fade = 0.5f;
                //menu buttons  "anchorMin": "0.1855 0.667", "anchorMax": "0.3135 0.714",
                //hl button
                cuiData["highlightBtn"].anchorMin = "0 0"; cuiData["highlightBtn"].anchorMax = "1 1";
                cuiData["highlightBtn"].color = "0.80 0.25 0.16 1"; cuiData["highlightBtn"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["highlightBtn"].image = ""; cuiData["highlightBtn"].fade = 0f;
                //btn 1
                cuiData["menuBtn1"].anchorMin = "0.1855 0.664"; cuiData["menuBtn1"].anchorMax = "0.3135 0.714";
                cuiData["menuBtn1"].color = "0 0 0 0.35"; cuiData["menuBtn1"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn1"].image = "https://rustplugins.net/products/welcomepanel/3/home-icon.png"; cuiData["menuBtn1"].fade = 0f;
                //btn 2
                cuiData["menuBtn2"].anchorMin = "0.1855 0.614"; cuiData["menuBtn2"].anchorMax = "0.3135 0.664";
                cuiData["menuBtn2"].color = "0 0 0 0.45"; cuiData["menuBtn2"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn2"].image = "https://rustplugins.net/products/welcomepanel/3/rules-icon.png"; cuiData["menuBtn2"].fade = 0f;
                //btn 3
                cuiData["menuBtn3"].anchorMin = "0.1855 0.564"; cuiData["menuBtn3"].anchorMax = "0.3135 0.614";
                cuiData["menuBtn3"].color = "0 0 0 0.35"; cuiData["menuBtn3"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn3"].image = "https://rustplugins.net/products/welcomepanel/3/wipe-icon.png"; cuiData["menuBtn3"].fade = 0f;
                //btn 4
                cuiData["menuBtn4"].anchorMin = "0.1855 0.514"; cuiData["menuBtn4"].anchorMax = "0.3135 0.564";
                cuiData["menuBtn4"].color = "0 0 0 0.45"; cuiData["menuBtn4"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn4"].image = "https://rustplugins.net/products/welcomepanel/3/steam-icon.png"; cuiData["menuBtn4"].fade = 0f;
                //btn 5
                cuiData["menuBtn5"].anchorMin = "0.1855 0.464"; cuiData["menuBtn5"].anchorMax = "0.3135 0.514";
                cuiData["menuBtn5"].color = "0 0 0 0.35"; cuiData["menuBtn5"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn5"].image = "https://rustplugins.net/products/welcomepanel/3/discord-icon.png"; cuiData["menuBtn5"].fade = 0f;
                //btn 6
                cuiData["menuBtn6"].anchorMin = "0.1855 0.414"; cuiData["menuBtn6"].anchorMax = "0.3135 0.464";
                cuiData["menuBtn6"].color = "0 0 0 0.45"; cuiData["menuBtn6"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn6"].image = "https://rustplugins.net/products/welcomepanel/3/bug-icon.png"; cuiData["menuBtn6"].fade = 0f;
                //btn 7
                cuiData["menuBtn7"].anchorMin = "0.1855 0.364"; cuiData["menuBtn7"].anchorMax = "0.3135 0.414";
                cuiData["menuBtn7"].color = "0 0 0 0.35"; cuiData["menuBtn7"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn7"].image = "https://rustplugins.net/products/welcomepanel/3/admin-icon.png"; cuiData["menuBtn7"].fade = 0f;
                //btn 8
                cuiData["menuBtn8"].anchorMin = "0.1855 0.314"; cuiData["menuBtn8"].anchorMax = "0.3135 0.364";
                cuiData["menuBtn8"].color = "0 0 0 0.45"; cuiData["menuBtn8"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn8"].image = "https://rustplugins.net/products/welcomepanel/3/shop-icon.png"; cuiData["menuBtn8"].fade = 0f;
                //btn 9
                cuiData["menuBtn9"].anchorMin = "0.1855 0.264"; cuiData["menuBtn9"].anchorMax = "0.3135 0.314";
                cuiData["menuBtn9"].color = "0 0 0 0.35"; cuiData["menuBtn9"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn9"].image = "https://rustplugins.net/products/welcomepanel/3/vip-icon.png"; cuiData["menuBtn9"].fade = 0f;
                //btn 10
                cuiData["menuBtn10"].anchorMin = "0.1855 0.214"; cuiData["menuBtn10"].anchorMax = "0.3135 0.264";
                cuiData["menuBtn10"].color = "0 0 0 0.45"; cuiData["menuBtn10"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn10"].image = "https://rustplugins.net/products/welcomepanel/3/rules-icon.png"; cuiData["menuBtn10"].fade = 0f;
                //page buttons
                //next
                cuiData["nextButton"].anchorMin = "0.95 0.013"; cuiData["nextButton"].anchorMax = "0.99 0.06";
                cuiData["nextButton"].color = "0 0 0 0.45"; cuiData["nextButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["nextButton"].image = "https://rustplugins.net/products/welcomepanel/3/page-next.png"; cuiData["nextButton"].fade = 0f;
                //prev
                cuiData["previousButton"].anchorMin = "0.893 0.013"; cuiData["previousButton"].anchorMax = "0.94 0.06";
                cuiData["previousButton"].color = "0 0 0 0.45"; cuiData["previousButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["previousButton"].image = "https://rustplugins.net/products/welcomepanel/3/page-prev.png"; cuiData["previousButton"].fade = 0f;


                config.buttonSettings.mainAlign = TextAnchor.MiddleLeft;
                config.tab1Settings.btnTitle = "          Home";
                config.tab2Settings.btnTitle = "          Rules";
                config.tab3Settings.btnTitle = "          Wipe cycle";
                config.tab4Settings.btnTitle = "          Steam group";
                config.tab5Settings.btnTitle = "          Discord";
                config.tab6Settings.btnTitle = "          Bug report";
                config.tab7Settings.btnTitle = "          Admin";
                config.tab8Settings.btnTitle = "          Shop";
                config.tab9Settings.btnTitle = "          Vip";
                config.tab10Settings.btnTitle = "          Updates";
                config.titleSettings.titleText = "<size=13>OCTAVIAN | x5 | ЕЖЕНЕДЕЛЬНЫЕ ВАЙПЫ | АКТИВНЫЕ АДМИНЫ | НАБОРЫ</size>";

            }

            if (presetNumber == 4)
            {
                //background
                cuiData["background"].anchorMin = "0 0"; cuiData["background"].anchorMax = "1 1";
                cuiData["background"].color = "0 0 0 0.6"; cuiData["background"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["background"].image = ""; cuiData["background"].fade = 0.5f;
                //background2
                cuiData["background2"].anchorMin = "0 0"; cuiData["background2"].anchorMax = "1 1";
                cuiData["background2"].color = "0 0 0 0.0"; cuiData["background2"].material = "assets/icons/iconmaterial.mat";
                cuiData["background2"].image = ""; cuiData["background2"].fade = 0f;
                //offsetContainer
                cuiData["offsetContainer"].anchorMin = "0.5 0.5"; cuiData["offsetContainer"].anchorMax = "0.5 0.5";
                cuiData["offsetContainer"].color = "0 0 0 0.0"; cuiData["offsetContainer"].material = "assets/icons/iconmaterial.mat";
                cuiData["offsetContainer"].image = ""; cuiData["offsetContainer"].fade = 0f;
                //main panel
                cuiData["mainPanel"].anchorMin = "0.2 0.155"; cuiData["mainPanel"].anchorMax = "0.8 0.798";
                cuiData["mainPanel"].color = "0 0 0 0.0"; cuiData["mainPanel"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["mainPanel"].image = "https://rustplugins.net/products/welcomepanel/4/background.png"; cuiData["mainPanel"].fade = 0.5f;
                //side panel
                cuiData["sidePanel"].anchorMin = "0 0"; cuiData["sidePanel"].anchorMax = "0 0";
                cuiData["sidePanel"].color = "0 0 0 0.0"; cuiData["sidePanel"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["sidePanel"].image = ""; cuiData["sidePanel"].fade = 0.5f;
                //title panel
                cuiData["titlePanel"].anchorMin = "0 0"; cuiData["titlePanel"].anchorMax = "0 0";
                cuiData["titlePanel"].color = "0 0 0 0"; cuiData["titlePanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["titlePanel"].image = ""; cuiData["titlePanel"].fade = 0.5f;
                //logo panel
                cuiData["logoPanel"].anchorMin = "0.2 0.695"; cuiData["logoPanel"].anchorMax = "0.27 0.783";
                cuiData["logoPanel"].color = "0 0 0 0"; cuiData["logoPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["logoPanel"].image = "https://rustplugins.net/products/welcomepanel/4/logo.png"; cuiData["logoPanel"].fade = 0.5f;
                //close button
                cuiData["closeButton"].anchorMin = "0.76 0.7219999"; cuiData["closeButton"].anchorMax = "0.781 0.7609999";
                cuiData["closeButton"].color = "0 0 0 0"; cuiData["closeButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["closeButton"].image = "https://rustplugins.net/products/welcomepanel/4/btn_close1.png"; cuiData["closeButton"].fade = 0.5f;
                //contentPanel
                cuiData["contentPanel"].anchorMin = "0.215 0.175"; cuiData["contentPanel"].anchorMax = "0.785 0.688";
                cuiData["contentPanel"].color = "0 0 0 0.0"; cuiData["contentPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["contentPanel"].image = ""; cuiData["contentPanel"].fade = 0.5f;
                //menu buttons
                //hl button
                cuiData["highlightBtn"].anchorMin = "0 0"; cuiData["highlightBtn"].anchorMax = "1 1";
                cuiData["highlightBtn"].color = "0 0 0 0"; cuiData["highlightBtn"].material = "assets/icons/iconmaterial.mat";
                cuiData["highlightBtn"].image = "https://rustplugins.net/products/welcomepanel/4/btn_hl.png"; cuiData["highlightBtn"].fade = 0.5f;
                //btn 1
                cuiData["menuBtn1"].anchorMin = "0.2725 0.72"; cuiData["menuBtn1"].anchorMax = "0.3495 0.763";
                cuiData["menuBtn1"].color = "0 0 0 0"; cuiData["menuBtn1"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn1"].image = ""; cuiData["menuBtn1"].fade = 0f;
                //btn 2
                cuiData["menuBtn2"].anchorMin = "0.3515 0.72"; cuiData["menuBtn2"].anchorMax = "0.4295 0.7640001";
                cuiData["menuBtn2"].color = "0 0 0 0"; cuiData["menuBtn2"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn2"].image = ""; cuiData["menuBtn2"].fade = 0f;
                //btn 3
                cuiData["menuBtn3"].anchorMin = "0.4305 0.72"; cuiData["menuBtn3"].anchorMax = "0.5155001 0.763";
                cuiData["menuBtn3"].color = "0 0 0 0"; cuiData["menuBtn3"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn3"].image = ""; cuiData["menuBtn3"].fade = 0f;
                //btn 4
                cuiData["menuBtn4"].anchorMin = "0.5165 0.72"; cuiData["menuBtn4"].anchorMax = "0.6115 0.763";
                cuiData["menuBtn4"].color = "0 0 0 0"; cuiData["menuBtn4"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn4"].image = ""; cuiData["menuBtn4"].fade = 0f;
                //btn 5
                cuiData["menuBtn5"].anchorMin = "0.6125 0.72"; cuiData["menuBtn5"].anchorMax = "0.6914998 0.763";
                cuiData["menuBtn5"].color = "0 0 0 0"; cuiData["menuBtn5"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn5"].image = ""; cuiData["menuBtn5"].fade = 0f;
                //btn 6
                cuiData["menuBtn6"].anchorMin = "0 0"; cuiData["menuBtn6"].anchorMax = "0 0";
                cuiData["menuBtn6"].color = "0 0 0 0"; cuiData["menuBtn6"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn6"].image = ""; cuiData["menuBtn6"].fade = 0f;
                //btn 7
                cuiData["menuBtn7"].anchorMin = "0 0"; cuiData["menuBtn7"].anchorMax = "0 0";
                cuiData["menuBtn7"].color = "0 0 0 0"; cuiData["menuBtn7"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn7"].image = ""; cuiData["menuBtn7"].fade = 0f;
                //btn 8
                cuiData["menuBtn8"].anchorMin = "0 0"; cuiData["menuBtn8"].anchorMax = "0 0";
                cuiData["menuBtn8"].color = "0 0 0 0"; cuiData["menuBtn8"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn8"].image = ""; cuiData["menuBtn8"].fade = 0f;
                //btn 9
                cuiData["menuBtn9"].anchorMin = "0 0"; cuiData["menuBtn9"].anchorMax = "0 0";
                cuiData["menuBtn9"].color = "0 0 0 0"; cuiData["menuBtn9"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn9"].image = ""; cuiData["menuBtn9"].fade = 0f;
                //btn 10
                cuiData["menuBtn10"].anchorMin = "0 0"; cuiData["menuBtn10"].anchorMax = "0 0";
                cuiData["menuBtn10"].color = "0 0 0 0"; cuiData["menuBtn10"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn10"].image = ""; cuiData["menuBtn10"].fade = 0f;
                //page buttons
                //next
                cuiData["nextButton"].anchorMin = "0.95 0.013"; cuiData["nextButton"].anchorMax = "0.978 0.06";
                cuiData["nextButton"].color = "0 0 0 0"; cuiData["nextButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["nextButton"].image = "https://rustplugins.net/products/welcomepanel/4/btn_next.png"; cuiData["nextButton"].fade = 0f;
                //prev
                cuiData["previousButton"].anchorMin = "0.913 0.013"; cuiData["previousButton"].anchorMax = "0.94 0.06";
                cuiData["previousButton"].color = "0 0 0 0"; cuiData["previousButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["previousButton"].image = "https://rustplugins.net/products/welcomepanel/4/btn_prev.png"; cuiData["previousButton"].fade = 0f;



                config.buttonSettings.mainAlign = TextAnchor.MiddleCenter;
                config.tab1Settings.btnTitle = "Home";
                config.tab2Settings.btnTitle = "Rules";
                config.tab3Settings.btnTitle = "Wipe cycle";
                config.tab4Settings.btnTitle = "Steam group";
                config.tab5Settings.btnTitle = "Discord";
                config.tab6Settings.btnTitle = "TEMPLATE #4 supports only 5 buttons by default";
                config.tab7Settings.btnTitle = "TEMPLATE #4 supports only 5 buttons by default";
                config.tab8Settings.btnTitle = "TEMPLATE #4 supports only 5 buttons by default";
                config.tab9Settings.btnTitle = "TEMPLATE #4 supports only 5 buttons by default";
                config.tab10Settings.btnTitle = "TEMPLATE #4 supports only 5 buttons by default";
                config.titleSettings.titleText = "Title panel is hidden, needs adjusting in cuiData.json";

            }

            if (presetNumber == 5)
            {
                //background
                cuiData["background"].anchorMin = "0 0"; cuiData["background"].anchorMax = "1 1";
                cuiData["background"].color = "0 0 0 0.8"; cuiData["background"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["background"].image = ""; cuiData["background"].fade = 0.5f;
                //background2
                cuiData["background2"].anchorMin = "0 0"; cuiData["background2"].anchorMax = "1 1";
                cuiData["background2"].color = "0.12 0.12 0.12 1"; cuiData["background2"].material = "assets/icons/iconmaterial.mat";
                cuiData["background2"].image = ""; cuiData["background2"].fade = 0f;
                //offsetContainer
                cuiData["offsetContainer"].anchorMin = "0.5 0.5"; cuiData["offsetContainer"].anchorMax = "0.5 0.5";
                cuiData["offsetContainer"].color = "0 0 0 0.0"; cuiData["offsetContainer"].material = "assets/icons/iconmaterial.mat";
                cuiData["offsetContainer"].image = "https://rustplugins.net/products/welcomepanel/5/background_55.jpg"; cuiData["offsetContainer"].fade = 0f;
                //main panel
                cuiData["mainPanel"].anchorMin = "0.4 -0.015"; cuiData["mainPanel"].anchorMax = "0.98 1.008";
                cuiData["mainPanel"].color = "0 0 0 0.0"; cuiData["mainPanel"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["mainPanel"].image = ""; cuiData["mainPanel"].fade = 0.5f;
                //side panel
                cuiData["sidePanel"].anchorMin = "0 0"; cuiData["sidePanel"].anchorMax = "0 0";
                cuiData["sidePanel"].color = "0 0 0 0.75"; cuiData["sidePanel"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["sidePanel"].image = ""; cuiData["sidePanel"].fade = 0.5f;
                //title panel
                cuiData["titlePanel"].anchorMin = "0.178 0.745"; cuiData["titlePanel"].anchorMax = "0.9 0.85";
                cuiData["titlePanel"].color = "0 0 0 0"; cuiData["titlePanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["titlePanel"].image = ""; cuiData["titlePanel"].fade = 0.5f;
                //logo panel
                cuiData["logoPanel"].anchorMin = "0 0"; cuiData["logoPanel"].anchorMax = "0 0";
                cuiData["logoPanel"].color = "0 0 0 0"; cuiData["logoPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["logoPanel"].image = ""; cuiData["logoPanel"].fade = 0.5f;
                //close button
                cuiData["closeButton"].anchorMin = "0.54 0.035"; cuiData["closeButton"].anchorMax = "0.62 0.07999998";
                cuiData["closeButton"].color = "0.56 0.20 0.15 0.9"; cuiData["closeButton"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["closeButton"].image = ""; cuiData["closeButton"].fade = 0.5f;
                //contentPanel
                cuiData["contentPanel"].anchorMin = "0.42 0.095"; cuiData["contentPanel"].anchorMax = "0.94 0.748";
                cuiData["contentPanel"].color = "0 0 0 0.0"; cuiData["contentPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["contentPanel"].image = ""; cuiData["contentPanel"].fade = 0.5f;
                //menu buttons
                //hl button
                cuiData["highlightBtn"].anchorMin = "0 0"; cuiData["highlightBtn"].anchorMax = "1 1";
                cuiData["highlightBtn"].color = "0.80 0.25 0.16 1"; cuiData["highlightBtn"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["highlightBtn"].image = ""; cuiData["highlightBtn"].fade = 0f;
                //btn 1
                cuiData["menuBtn1"].anchorMin = "0.4345 0.88"; cuiData["menuBtn1"].anchorMax = "0.5195 0.938";
                cuiData["menuBtn1"].color = "0 0 0 0"; cuiData["menuBtn1"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn1"].image = ""; cuiData["menuBtn1"].fade = 0f;
                //btn 2
                cuiData["menuBtn2"].anchorMin = "0.5195001 0.875"; cuiData["menuBtn2"].anchorMax = "0.6245 0.942";
                cuiData["menuBtn2"].color = "0 0 0 0"; cuiData["menuBtn2"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn2"].image = ""; cuiData["menuBtn2"].fade = 0f;
                //btn 3
                cuiData["menuBtn3"].anchorMin = "0.6245 0.8750001"; cuiData["menuBtn3"].anchorMax = "0.7295 0.942";
                cuiData["menuBtn3"].color = "0 0 0 0"; cuiData["menuBtn3"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn3"].image = ""; cuiData["menuBtn3"].fade = 0f;
                //btn 4
                cuiData["menuBtn4"].anchorMin = "0.7295 0.875"; cuiData["menuBtn4"].anchorMax = "0.8395 0.9419999";
                cuiData["menuBtn4"].color = "0 0 0 0"; cuiData["menuBtn4"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn4"].image = ""; cuiData["menuBtn4"].fade = 0f;
                //btn 5
                cuiData["menuBtn5"].anchorMin = "0.8394999 0.875"; cuiData["menuBtn5"].anchorMax = "0.9394999 0.9419998";
                cuiData["menuBtn5"].color = "0 0 0 0"; cuiData["menuBtn5"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn5"].image = ""; cuiData["menuBtn5"].fade = 0f;
                //btn 6
                cuiData["menuBtn6"].anchorMin = "0.0595 0.1"; cuiData["menuBtn6"].anchorMax = "0.1995 0.182";
                cuiData["menuBtn6"].color = "0 0 0 0"; cuiData["menuBtn6"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn6"].image = "https://rustplugins.net/products/welcomepanel/5/shopbtn.png"; cuiData["menuBtn6"].fade = 0f;
                //btn 7
                cuiData["menuBtn7"].anchorMin = "0.2145 0.09999999"; cuiData["menuBtn7"].anchorMax = "0.3545 0.1819999";
                cuiData["menuBtn7"].color = "0 0 0 0"; cuiData["menuBtn7"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn7"].image = "https://rustplugins.net/products/welcomepanel/5/discordbtn.png"; cuiData["menuBtn7"].fade = 0f;
                //btn 8
                cuiData["menuBtn8"].anchorMin = "0 0"; cuiData["menuBtn8"].anchorMax = "0 0";
                cuiData["menuBtn8"].color = "0 0 0 0"; cuiData["menuBtn8"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn8"].image = ""; cuiData["menuBtn8"].fade = 0f;
                //btn 9
                cuiData["menuBtn9"].anchorMin = "0 0"; cuiData["menuBtn9"].anchorMax = "0 0";
                cuiData["menuBtn9"].color = "0 0 0 0"; cuiData["menuBtn9"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn9"].image = ""; cuiData["menuBtn9"].fade = 0f;
                //btn 10
                cuiData["menuBtn10"].anchorMin = "0 0"; cuiData["menuBtn10"].anchorMax = "0 0";
                cuiData["menuBtn10"].color = "0 0 0 0"; cuiData["menuBtn10"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn10"].image = ""; cuiData["menuBtn10"].fade = 0f;
                //page buttons
                //next
                cuiData["nextButton"].anchorMin = "0.99 0.013"; cuiData["nextButton"].anchorMax = "1.01 0.48";
                cuiData["nextButton"].color = "0.25 0.23 0.22 0.95"; cuiData["nextButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["nextButton"].image = "https://rustplugins.net/products/welcomepanel/5/btn-pagedown.png"; cuiData["nextButton"].fade = 0f;
                //prev
                cuiData["previousButton"].anchorMin = "0.99 0.488"; cuiData["previousButton"].anchorMax = "1.01 0.975";
                cuiData["previousButton"].color = "0.25 0.23 0.22 0.95"; cuiData["previousButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["previousButton"].image = "https://rustplugins.net/products/welcomepanel/5/btn-pageup.png"; cuiData["previousButton"].fade = 0f;

                config.buttonSettings.mainAlign = TextAnchor.MiddleCenter;
                config.tab1Settings.btnTitle = "HOME";
                config.tab2Settings.btnTitle = "RULES";
                config.tab3Settings.btnTitle = "WIPE SCHEDULE";
                config.tab4Settings.btnTitle = "PLUGINS";
                config.tab5Settings.btnTitle = "VIP";
                config.tab6Settings.btnTitle = "      VISIT GAME SHOP";
                config.tab7Settings.btnTitle = "      JOIN OUR DISCORD";
                config.tab8Settings.btnTitle = "TEMPLATE #5 supports only 7 buttons by default";
                config.tab9Settings.btnTitle = "TEMPLATE #5 supports only 7 buttons by default";
                config.tab10Settings.btnTitle = "TEMPLATE #5 supports only 7 buttons by default";
                config.titleSettings.titleText = "This template is partialy made from image \n you have to made your own in order to use this layout.";
            }

            if (presetNumber == 6)
            {
                //background
                cuiData["background"].anchorMin = "0 0"; cuiData["background"].anchorMax = "1 1";
                cuiData["background"].color = "0 0 0 0.4"; cuiData["background"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["background"].image = ""; cuiData["background"].fade = 0.5f;
                //background2
                cuiData["background2"].anchorMin = "0 0"; cuiData["background2"].anchorMax = "1 1";
                cuiData["background2"].color = "0 0 0 0.0"; cuiData["background2"].material = "assets/icons/iconmaterial.mat";
                cuiData["background2"].image = ""; cuiData["background2"].fade = 0f;
                //offsetContainer
                cuiData["offsetContainer"].anchorMin = "0.5 0.5"; cuiData["offsetContainer"].anchorMax = "0.5 0.5";
                cuiData["offsetContainer"].color = "0 0 0 0.0"; cuiData["offsetContainer"].material = "assets/icons/iconmaterial.mat";
                cuiData["offsetContainer"].image = ""; cuiData["offsetContainer"].fade = 0f;
                //main panel
                cuiData["mainPanel"].anchorMin = "0.0 0.0"; cuiData["mainPanel"].anchorMax = "1 1";
                cuiData["mainPanel"].color = "0 0 0 0.0"; cuiData["mainPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["mainPanel"].image = ""; cuiData["mainPanel"].fade = 0.5f;
                //side panel
                cuiData["sidePanel"].anchorMin = "0.02500001 -0.00499999"; cuiData["sidePanel"].anchorMax = "0.22 1.008";
                cuiData["sidePanel"].color = "0 0 0 0.0"; cuiData["sidePanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["sidePanel"].image = ""; cuiData["sidePanel"].fade = 0.5f;
                //title panel
                cuiData["titlePanel"].anchorMin = "0.0725 0.615"; cuiData["titlePanel"].anchorMax = "0.89 0.87";
                cuiData["titlePanel"].color = "0 0 0 0"; cuiData["titlePanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["titlePanel"].image = ""; cuiData["titlePanel"].fade = 0.5f;
                //logo panel
                cuiData["logoPanel"].anchorMin = "0.05500001 0.78"; cuiData["logoPanel"].anchorMax = "0.2 0.963";
                cuiData["logoPanel"].color = "0 0 0 0"; cuiData["logoPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["logoPanel"].image = "https://rustplugins.net/products/welcomepanel/6/logo1.png"; cuiData["logoPanel"].fade = 0.5f;
                //close button
                cuiData["closeButton"].anchorMin = "0.08 0.145"; cuiData["closeButton"].anchorMax = "0.17 0.19";
                cuiData["closeButton"].color = "0.56 0.20 0.15 0.9"; cuiData["closeButton"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["closeButton"].image = ""; cuiData["closeButton"].fade = 0.5f;
                //contentPanel
                cuiData["contentPanel"].anchorMin = "0.25 0.04500001"; cuiData["contentPanel"].anchorMax = "0.94 0.82";
                cuiData["contentPanel"].color = "0 0 0 0.0"; cuiData["contentPanel"].material = "assets/icons/iconmaterial.mat";
                cuiData["contentPanel"].image = ""; cuiData["contentPanel"].fade = 0.5f;
                //menu buttons
                //hl button
                cuiData["highlightBtn"].anchorMin = "0 0"; cuiData["highlightBtn"].anchorMax = "1 1";
                cuiData["highlightBtn"].color = "0.16 0.34 0.49 1.0"; cuiData["highlightBtn"].material = "assets/content/ui/uibackgroundblur.mat";
                cuiData["highlightBtn"].image = ""; cuiData["highlightBtn"].fade = 0f;
                //btn 1
                cuiData["menuBtn1"].anchorMin = "0.02450001 0.61"; cuiData["menuBtn1"].anchorMax = "0.2195 0.668";
                cuiData["menuBtn1"].color = "0 0 0 0"; cuiData["menuBtn1"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn1"].image = ""; cuiData["menuBtn1"].fade = 0f;
                //btn 2
                cuiData["menuBtn2"].anchorMin = "0.02450001 0.55"; cuiData["menuBtn2"].anchorMax = "0.2195 0.607";
                cuiData["menuBtn2"].color = "0 0 0 0"; cuiData["menuBtn2"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn2"].image = ""; cuiData["menuBtn2"].fade = 0f;
                //btn 3
                cuiData["menuBtn3"].anchorMin = "0.02450001 0.49"; cuiData["menuBtn3"].anchorMax = "0.2195 0.547";
                cuiData["menuBtn3"].color = "0 0 0 0"; cuiData["menuBtn3"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn3"].image = ""; cuiData["menuBtn3"].fade = 0f;
                //btn 4
                cuiData["menuBtn4"].anchorMin = "0.02450001 0.43"; cuiData["menuBtn4"].anchorMax = "0.2195 0.487";
                cuiData["menuBtn4"].color = "0 0 0 0"; cuiData["menuBtn4"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn4"].image = ""; cuiData["menuBtn4"].fade = 0f;
                //btn 5
                cuiData["menuBtn5"].anchorMin = "0.02450001 0.37"; cuiData["menuBtn5"].anchorMax = "0.2195 0.427";
                cuiData["menuBtn5"].color = "0 0 0 0"; cuiData["menuBtn5"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn5"].image = ""; cuiData["menuBtn5"].fade = 0f;
                //btn 6
                cuiData["menuBtn6"].anchorMin = "0.02450001 0.31"; cuiData["menuBtn6"].anchorMax = "0.2195 0.367";
                cuiData["menuBtn6"].color = "0 0 0 0"; cuiData["menuBtn6"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn6"].image = ""; cuiData["menuBtn6"].fade = 0f;
                //btn 7
                cuiData["menuBtn7"].anchorMin = "0.02450001 0.25"; cuiData["menuBtn7"].anchorMax = "0.2195 0.307";
                cuiData["menuBtn7"].color = "0 0 0 0"; cuiData["menuBtn7"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn7"].image = ""; cuiData["menuBtn7"].fade = 0f;
                //btn 8
                cuiData["menuBtn8"].anchorMin = "0 0"; cuiData["menuBtn8"].anchorMax = "0 0";
                cuiData["menuBtn8"].color = "0 0 0 0"; cuiData["menuBtn8"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn8"].image = ""; cuiData["menuBtn8"].fade = 0f;
                //btn 9
                cuiData["menuBtn9"].anchorMin = "0 0"; cuiData["menuBtn9"].anchorMax = "0 0";
                cuiData["menuBtn9"].color = "0 0 0 0"; cuiData["menuBtn9"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn9"].image = ""; cuiData["menuBtn9"].fade = 0f;
                //btn 10
                cuiData["menuBtn10"].anchorMin = "0 0"; cuiData["menuBtn10"].anchorMax = "0 0";
                cuiData["menuBtn10"].color = "0 0 0 0"; cuiData["menuBtn10"].material = "assets/icons/iconmaterial.mat";
                cuiData["menuBtn10"].image = ""; cuiData["menuBtn10"].fade = 0f;
                //page buttons
                //next
                cuiData["nextButton"].anchorMin = "0.95 0.013"; cuiData["nextButton"].anchorMax = "0.99 0.06";
                cuiData["nextButton"].color = "0 0 0 0"; cuiData["nextButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["nextButton"].image = "https://rustplugins.net/products/welcomepanel/2/btn-nextpage.png"; cuiData["nextButton"].fade = 0f;
                //prev
                cuiData["previousButton"].anchorMin = "0.9 0.013"; cuiData["previousButton"].anchorMax = "0.94 0.06";
                cuiData["previousButton"].color = "0 0 0 0"; cuiData["previousButton"].material = "assets/icons/iconmaterial.mat";
                cuiData["previousButton"].image = "https://rustplugins.net/products/welcomepanel/2/btn-prevpage.png"; cuiData["previousButton"].fade = 0f;
            }
            SaveConfig();
            SaveData();
        }

        #endregion

        #region [CUI Classes]

        public class CUIClass
        {
            public static CuiElementContainer CreateOverlay(string _name, string _color, string _anchorMin, string _anchorMax, bool _cursorOn = false, float _fade = 0f, string _mat = "assets/content/ui/uibackgroundblur.mat")
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

            public static void CreatePanel(ref CuiElementContainer _container, string _name, string _parent, string _color, string _anchorMin, string _anchorMax, bool _cursorOn = false, float _fade = 0f, string _mat2 = "assets/content/ui/uibackgroundblur.mat", string _sprite = "", string _OffsetMin = "", string _OffsetMax = "")
            {
                _container.Add(new CuiPanel
                {
                    Image = { Color = _color, Material = _mat2, FadeIn = _fade },
                    RectTransform = { AnchorMin = _anchorMin, AnchorMax = _anchorMax, OffsetMin = _OffsetMin, OffsetMax = _OffsetMax },
                    CursorEnabled = _cursorOn
                },
                _parent,
                _name);
            }

            public static void CreateImage(ref CuiElementContainer _container, string _parent, string _image, string _anchorMin, string _anchorMax, float _fade = 0f)
            {
                if (_image.StartsWith("http") || _image.StartsWith("www"))
                {
                    _container.Add(new CuiElement
                    {
                        Parent = _parent,
                        Components =
                        {
                            new CuiRawImageComponent { Url = _image, Sprite = "assets/content/textures/generic/fulltransparent.tga", FadeIn = _fade},
                            new CuiRectTransformComponent { AnchorMin = _anchorMin, AnchorMax = _anchorMax }
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
                            new CuiRawImageComponent { Png = _image, Sprite = "assets/content/textures/generic/fulltransparent.tga", FadeIn = _fade},
                            new CuiRectTransformComponent { AnchorMin = _anchorMin, AnchorMax = _anchorMax }
                        }
                    });
                }
            }

            public static void CreateInput(ref CuiElementContainer _container, string _name, string _parent, string _color, int _size, string _anchorMin, string _anchorMax, string _font = "permanentmarker.ttf", string _command = "command.processinput", TextAnchor _align = TextAnchor.MiddleCenter)
            {
                _container.Add(new CuiElement
                {
                    Parent = _parent,
                    Name = _name,

                    Components =
                    {
                        new CuiInputFieldComponent
                        {

                            Text = "0",
                            CharsLimit = 250,
                            Color = _color,
                            IsPassword = false,
                            Command = _command,
                            Font = _font,
                            FontSize = _size,
                            Align = _align
                        },

                        new CuiRectTransformComponent
                        {
                            AnchorMin = _anchorMin,
                            AnchorMax = _anchorMax

                        }

                    },
                });
            }

            public static void CreateText(ref CuiElementContainer _container, string _name, string _parent, string _color, string _text, int _size, string _anchorMin, string _anchorMax, TextAnchor _align = TextAnchor.MiddleCenter, string _font = "robotocondensed-bold.ttf", string _outlineColor = "", string _outlineScale = "")
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
                            FadeIn = 0f,
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
                });
            }

            public static void CreateButton(ref CuiElementContainer _container, string _name, string _parent, string _color, string _text, int _size, string _anchorMin, string _anchorMax, string _command = "", string _close = "", string _textColor = "", float _fade = 1f, TextAnchor _align = TextAnchor.MiddleCenter, string _font = "", string _mat = "assets/content/ui/uibackgroundblur.mat")
            {

                _container.Add(new CuiButton
                {
                    Button = { Close = _close, Command = _command, Color = _color, Material = _mat, FadeIn = _fade },
                    RectTransform = { AnchorMin = _anchorMin, AnchorMax = _anchorMax },
                    Text = { Text = _text, FontSize = _size, Align = _align, Color = _textColor, Font = _font, FadeIn = _fade }
                },
                _parent,
                _name);
            }

        }
        #endregion

        #region [Cui Data]

        private void SaveData()
        {
            if (cuiData != null)
                Interface.Oxide.DataFileSystem.WriteObject($"{Name}/CuiData", cuiData);
        }

        private Dictionary<string, CuiData> cuiData;

        private class CuiData
        {
            public string anchorMin;
            public string anchorMax;
            public string color;
            public string material;
            public string image;
            public float fade;
        }

        private void LoadData()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile($"{Name}/CuiData"))
            {
                cuiData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, CuiData>>($"{Name}/CuiData");
            }
            else
            {
                cuiData = new Dictionary<string, CuiData>();
                SaveData();
            }
        }

        #endregion

        #region [Page Data]

        private void SavePageData()
        {
            if (pageData != null)
                Interface.Oxide.DataFileSystem.WriteObject($"{Name}/PageData", pageData);
        }

        private Dictionary<string, PageData> pageData;

        private class PageData
        {
            public List<string> page2 = new List<string> { };
            public List<string> page3 = new List<string> { };
            public List<string> page4 = new List<string> { };
            public List<string> page5 = new List<string> { };
            public List<string> page6 = new List<string> { };
            public List<string> page7 = new List<string> { };
            public List<string> page8 = new List<string> { };
            public List<string> page9 = new List<string> { };
            public List<string> page10 = new List<string> { };

        }

        private void LoadPageData()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile($"{Name}/PageData"))
            {
                pageData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, PageData>>($"{Name}/PageData");
            }
            else
            {
                pageData = new Dictionary<string, PageData>();
                SavePageData();
            }
        }

        #region [Boss pages — генерация из OctaBoss.json]

        // вкладка «БОССЫ»: стр.2..10 — по виду из конфига OctaBoss.
        // Заполняем только пустые страницы (ручные правки админа не трогаем),
        // принудительная регенерация — octawelcome_bosses_refresh.
        private const int BossTabIndex = 10;

        private static string JStr(JObject o, string key, string def = "") =>
            o?[key]?.ToString() ?? def;
        private static float JFloat(JObject o, string key, float def = 0f) =>
            o?[key]?.ToObject<float?>() ?? def;
        private static bool JBool(JObject o, string key, bool def = false) =>
            o?[key]?.ToObject<bool?>() ?? def;

        private void PopulateBossPages(bool force = false)
        {
            var tab = $"Tab{BossTabIndex}";
            if (pageData == null || !pageData.ContainsKey(tab)) return;
            if (!force && pageData[tab].page2.Count > 0) return;

            var path = Path.Combine(Interface.Oxide.ConfigDirectory, "OctaBoss.json");
            if (!File.Exists(path))
            {
                Puts("(!) OctaBoss.json не найден — страницы боссов не сгенерированы.");
                return;
            }

            List<JObject> specs;
            try
            {
                specs = (JObject.Parse(File.ReadAllText(path))["Виды боссов"] as JArray)
                        ?.OfType<JObject>().ToList() ?? new List<JObject>();
            }
            catch (Exception ex)
            {
                Puts($"(!) OctaBoss.json не читается: {ex.Message}");
                return;
            }
            if (specs.Count == 0) return;

            var pages = new List<List<string>>();
            foreach (var b in specs) pages.Add(BossPageLines(b));
            for (var i = pages.Count; i < 9; i++) pages.Add(new List<string>()); // чистим хвосты

            pageData[tab].page2 = pages.Count > 0 ? pages[0] : new List<string>();
            pageData[tab].page3 = pages.Count > 1 ? pages[1] : new List<string>();
            pageData[tab].page4 = pages.Count > 2 ? pages[2] : new List<string>();
            pageData[tab].page5 = pages.Count > 3 ? pages[3] : new List<string>();
            pageData[tab].page6 = pages.Count > 4 ? pages[4] : new List<string>();
            pageData[tab].page7 = pages.Count > 5 ? pages[5] : new List<string>();
            pageData[tab].page8 = pages.Count > 6 ? pages[6] : new List<string>();
            pageData[tab].page9 = pages.Count > 7 ? pages[7] : new List<string>();
            pageData[tab].page10 = pages.Count > 8 ? pages[8] : new List<string>();
            SavePageData();
            Puts($"Страницы боссов сгенерированы: {specs.Count} видов → Tab{BossTabIndex}.");
        }

        // короткое имя зверя из пути префаба: ".../boar/boar.prefab" -> "boar"
        private static string PrefabName(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return "?";
            var f = prefab.Replace('\\', '/');
            var slash = f.LastIndexOf('/');
            if (slash >= 0) f = f.Substring(slash + 1);
            return f.Replace(".prefab", "");
        }

        // атмосферные описания по ключу вида — без статов, чтобы не убивать интригу
        private static readonly Dictionary<string, string[]> BossFlavor = new Dictionary<string, string[]>
        {
            ["bull"] = new[]
            {
                "Он не бешеный. Он просто терпеливый.",
                "Тонны мышц, рога как копья и копыта, не знающие тормозов.",
                "Бык-Босс запомнит твоё лицо — и лицо каждого тиммейта.",
                "Красная тряпка ему не нужна: красная тряпка — это ты.",
                "Бери друзей, побольше свинца — и не вставай у него на пути."
            },
            ["boar"] = new[]
            {
                "С ранчо сбежало нечто. Оно вернулось за счётом.",
                "Клыки, проходящие броню как бумагу, и вес полноценной машины.",
                "Калидонский вепрь был мифом. Этот ходит по расписанию.",
                "Охота на него — праздник. Для него.",
                "Береги ноги: он бьёт низко и без предупреждения."
            },
            ["wolf"] = new[]
            {
                "Услышал вой? Поздно — это был дуэт: он и твой страх.",
                "Снег не скроет тебя от его носа. Ночь — от его глаз.",
                "Волк-Босс охотится не ради еды. Ему просто нравится.",
                "Не беги — он обожает погоню. Особенно короткую.",
                "Собери стаю посильнее или стань добычей в одиночку."
            },
            ["tiger"] = new[]
            {
                "В джунглях ты не охотник. Ты — сезонная еда.",
                "Шерхан был сказкой. У сказок кончается последняя страница.",
                "Он прыгнет раньше, чем ты наведёшь прицел.",
                "Полоски на его шкуре — это счёт побед.",
                "Иди группой: на один зевак он не разменивается."
            },
            ["panther"] = new[]
            {
                "Сначала — глаза в темноте. Потом уже ничего.",
                "Ночь принадлежит ей. День тоже — она просто вежливая.",
                "Ты её не увидишь. Увидишь тень — считай, повезло.",
                "Самая тихая смерть на острове носит её имя.",
                "Убить её — честь. Найти её — уже подвиг."
            },
            ["bear"] = new[]
            {
                "Гигант лесов. Хладнокровный убийца, от которого не отпускает страх.",
                "Маша здесь не живёт. Живёт только он — и уже очень давно.",
                "Одна лапа — и твоя кирка покажется зубочисткой.",
                "Убить его — слава и топ-лут. Но не думай, что будет просто.",
                "Возьми друзей. Всех, каких найдёшь."
            },
            ["crocodile"] = new[]
            {
                "Он видел динозавров. Они вымерли. Он — нет.",
                "У воды правила пишет он, а не ты.",
                "Это не Гена. Это тот, кто пришёл за Геной.",
                "Можешь смеяться над крокодилом. У него есть время и терпение. У тебя — нет.",
                "Держись от кромки воды подальше. Если сможешь."
            },
        };

        private static readonly string[] BossFlavorFallback =
        {
            "Остров вырастил нового монстра.",
            "О нём мало что известно — все рассказчики мертвы.",
            "Маркер на карте покажет дорогу. Остальное — твоя забота."
        };

        // биом конфига -> атмосферная локация
        private static string BiomeRu(string biomes)
        {
            if (string.IsNullOrEmpty(biomes) || biomes == "любой") return "где угодно — сам выбирает охотничьи угодья";
            var map = new Dictionary<string, string>
            {
                ["arid"] = "пустыня", ["temperate"] = "леса", ["tundra"] = "тундра",
                ["arctic"] = "снега", ["jungle"] = "джунгли"
            };
            var parts = biomes.Split(',').Select(s => s.Trim().ToLower())
                .Select(s => map.TryGetValue(s, out var ru) ? ru : s).Distinct().ToList();
            return "охотничьи угодья: " + string.Join(" и ", parts);
        }

        private List<string> BossPageLines(JObject b)
        {
            var key = JStr(b, "Ключ (латиницей, для команд)", "?");
            var name = JStr(b, "Имя босса", key.ToUpper());
            var enabled = JBool(b, "Включён", true);

            var esc = b["Свита"] as JObject;
            var escCount = esc?["Количество"]?.ToObject<int?>() ?? 0;
            var escLine = escCount > 0
                ? $"<size=15><color=#83b8c7>ОН НЕ ОДИН:</color> с ним свора из {escCount} зверей — билет продаётся только пакетом.</size>"
                : "<size=15><color=#83b8c7>ОН ОДИН:</color> приходит без свиты. Одного достаточно.</size>";

            var sp = b["Спавн"] as JObject;
            var mons = JStr(sp, "Монументы-якоря (через запятую, частичное совпадение; пусто = любая точка)");
            var biomes = JBool(sp, "Спавн только в родном биоме")
                ? JStr(sp, "Родные биомы (arid,temperate,tundra,arctic,jungle через запятую)") : "любой";
            var water = JBool(sp, "Требовать воду рядом (водные звери; крокодил иначе умирает сойдя с навмеша)");
            var where = BiomeRu(biomes)
                + (mons.Length > 0 ? $", кружит около {mons}" : "")
                + (water ? ", у воды" : "");

            var rw = b["Награды"] as JObject;
            var cur = rw?["Валюта за убийство (ServerRewards/Economics)"]?.ToObject<int?>() ?? 0;
            var part = rw?["Валюта участникам (нанёс >= порога урона)"]?.ToObject<int?>() ?? 0;

            var status = enabled
                ? "<color=#83b8c7>ГУЛЯЕТ ПО ОСТРОВУ</color>"
                : "<color=#cd412b>СПИТ — пока недоступен</color>";
            var lines = new List<string>
            {
                $"<size=32><color=#E8B84B>{name}</color></size>",
                $"<size=13>{status}</size>",
                "",
            };
            lines.AddRange((BossFlavor.TryGetValue(key, out var fl) ? fl : BossFlavorFallback)
                .Select(s => $"<size=16>{s}</size>"));
            lines.Add("");
            lines.Add(escLine);
            lines.Add($"<size=15><color=#83b8c7>ГДЕ ИСКАТЬ:</color> {where} — следи за маркером на карте.</size>");
            lines.Add($"<size=15><color=#83b8c7>НАГРАДА:</color> {cur} RP и топ-лут тому, кто внёс больше всего урона" +
                      $"{(part > 0 ? $" • {part} RP участникам" : "")}.</size>");
            lines.Add("");
            lines.Add("<size=11>/bosstimer — расписание • /bossinfo — кто жив • /bosslast — киллеры</size>");
            return lines;
        }

        [ConsoleCommand("octawelcome_bosses_refresh")]
        private void octawelcome_bosses_refresh(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            PopulateBossPages(force: true);
        }

        #endregion

        #endregion

        #region [Preset Data]

        private void SavePresetData()
        {
            if (presetData != null)
                Interface.Oxide.DataFileSystem.WriteObject($"{Name}/PresetData", presetData);
        }

        private Dictionary<string, PresetData> presetData;

        private class PresetData
        {
            public bool presetInit;
            public int presetNumber;
            public List<ulong> seen = new List<ulong> { };

        }

        private void LoadPresetData()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile($"{Name}/PresetData"))
            {
                presetData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, PresetData>>($"{Name}/PresetData");
            }
            else
            {
                presetData = new Dictionary<string, PresetData>();
                presetData.Add($"{Name}", new PresetData());

                presetData[$"{Name}"].presetInit = false;
                presetData[$"{Name}"].presetNumber = 0;

                SavePresetData();
            }
        }

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["_noPerm"] = "<color=#E8B84B>[OctaWelcome]</color> У вас нет разрешения на это.",
                ["_dataWiped"] = "<color=#E8B84B>[OctaWelcome]</color> CuiData игроков очищена. Перезагрузите плагин.",
                ["_typeKdrshow"] = "<color=#E8B84B>[OctaWelcome]</color> Наберите /kdrshow, чтобы открыть панель KDR.",
                ["_typeKdrhide"] = "<color=#E8B84B>[OctaWelcome]</color> Наберите /kdrhide, чтобы закрыть панель KDR.",
                ["_hidden"] = "<color=#E8B84B>[OctaWelcome]</color> Скрыто.",
                ["_displayed"] = "<color=#E8B84B>[OctaWelcome]</color> Показано.",

            }, this);
        }

        private string GetLang(string _message) => lang.GetMessage(_message, this);

        #endregion

        #region [Config] 

        private Configuration config;
        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<Configuration>();
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            config = Configuration.CreateConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config);



        class Configuration
        {

            [JsonProperty(PropertyName = "General Settings")]
            public GeneralSettings generalSettings { get; set; }

            public class GeneralSettings
            {
                [JsonProperty("Open when player connected.")]
                public bool openOnConnect { get; set; }

                [JsonProperty("Open on tab number.")]
                public int openTab { get; set; }

                [JsonProperty("Open only once per wipe.")]
                public bool openOnce { get; set; }

                [JsonProperty("Require permission for each tab.")]
                public bool permsEnabled { get; set; }

                [JsonProperty("Custom chat commands (don't delete from list, just rename).")]
                public List<string> customCommands { get; set; }
            }

            [JsonProperty(PropertyName = "Button Settings")]
            public ButtonSettings buttonSettings { get; set; }

            public class ButtonSettings
            {
                [JsonProperty("Button Font Style")]
                public string btnFontStyle { get; set; }

                [JsonProperty("Close Button Text")]
                public string closeBtnText { get; set; }

                [JsonProperty("Text Align Inside Button")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Icon Position (parented to button)")]
                public List<string> btnIconAnchor { get; set; }
            }

            [JsonProperty(PropertyName = "Title Settings")]
            public TitleSettings titleSettings { get; set; }

            public class TitleSettings
            {
                [JsonProperty("Title Text")]
                public string titleText { get; set; }

                [JsonProperty("Base Font Color")]
                public string titleColor { get; set; }

                [JsonProperty("Font Style")]
                public string titleFontStyle { get; set; }

                [JsonProperty("Outline Color")]
                public string titleOutlineColor { get; set; }

                [JsonProperty("Outline Thickness")]
                public string titleOutlineThick { get; set; }
            }

            [JsonProperty(PropertyName = "Tab #1 Settings")]
            public Tab1Settings tab1Settings { get; set; }

            public class Tab1Settings
            {
                [JsonProperty("Enabled")]
                public bool btnEnabled { get; set; }

                [JsonProperty("Button Title")]
                public string btnTitle { get; set; }

                [JsonProperty("Button Icon Img")]
                public string btnIcon { get; set; }

                [JsonProperty("Base Text Size")]
                public int mainFontSize { get; set; }

                [JsonProperty("Base Text Color")]
                public string mainFontColor { get; set; }

                [JsonProperty("Text Style")]
                public string mainFontStyle { get; set; }

                [JsonProperty("Text Outline Color")]
                public string fontOutlineColor { get; set; }

                [JsonProperty("Text Outline Thickness")]
                public string fontOutlineThick { get; set; }

                [JsonProperty("Text Align")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Text Lines")]
                public List<string> textLines { get; set; }

                [JsonProperty("Background Image URL")]
                public string tabImageUrl { get; set; }

                [JsonProperty("Background Image Anchoring")]
                public List<string> tabImageAnchor { get; set; }


            }

            [JsonProperty(PropertyName = "Tab #2 Settings")]
            public Tab2Settings tab2Settings { get; set; }

            public class Tab2Settings
            {
                [JsonProperty("Enabled")]
                public bool btnEnabled { get; set; }

                [JsonProperty("Button Title")]
                public string btnTitle { get; set; }

                [JsonProperty("Button Icon Img")]
                public string btnIcon { get; set; }

                [JsonProperty("Base Text Size")]
                public int mainFontSize { get; set; }

                [JsonProperty("Base Text Color")]
                public string mainFontColor { get; set; }

                [JsonProperty("Text Style")]
                public string mainFontStyle { get; set; }

                [JsonProperty("Text Outline Color")]
                public string fontOutlineColor { get; set; }

                [JsonProperty("Text Outline Thickness")]
                public string fontOutlineThick { get; set; }

                [JsonProperty("Text Align")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Text Lines")]
                public List<string> textLines { get; set; }

                [JsonProperty("Background Image URL")]
                public string tabImageUrl { get; set; }

                [JsonProperty("Background Image Anchoring")]
                public List<string> tabImageAnchor { get; set; }
            }

            [JsonProperty(PropertyName = "Tab #3 Settings")]
            public Tab3Settings tab3Settings { get; set; }

            public class Tab3Settings
            {
                [JsonProperty("Enabled")]
                public bool btnEnabled { get; set; }

                [JsonProperty("Button Title")]
                public string btnTitle { get; set; }

                [JsonProperty("Button Icon Img")]
                public string btnIcon { get; set; }

                [JsonProperty("Base Text Size")]
                public int mainFontSize { get; set; }

                [JsonProperty("Base Text Color")]
                public string mainFontColor { get; set; }

                [JsonProperty("Text Style")]
                public string mainFontStyle { get; set; }

                [JsonProperty("Text Outline Color")]
                public string fontOutlineColor { get; set; }

                [JsonProperty("Text Outline Thickness")]
                public string fontOutlineThick { get; set; }

                [JsonProperty("Text Align")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Text Lines")]
                public List<string> textLines { get; set; }

                [JsonProperty("Background Image URL")]
                public string tabImageUrl { get; set; }

                [JsonProperty("Background Image Anchoring")]
                public List<string> tabImageAnchor { get; set; }
            }

            [JsonProperty(PropertyName = "Tab #4 Settings")]
            public Tab4Settings tab4Settings { get; set; }

            public class Tab4Settings
            {
                [JsonProperty("Enabled")]
                public bool btnEnabled { get; set; }

                [JsonProperty("Button Title")]
                public string btnTitle { get; set; }

                [JsonProperty("Button Icon Img")]
                public string btnIcon { get; set; }

                [JsonProperty("Base Text Size")]
                public int mainFontSize { get; set; }

                [JsonProperty("Base Text Color")]
                public string mainFontColor { get; set; }

                [JsonProperty("Text Style")]
                public string mainFontStyle { get; set; }

                [JsonProperty("Text Outline Color")]
                public string fontOutlineColor { get; set; }

                [JsonProperty("Text Outline Thickness")]
                public string fontOutlineThick { get; set; }

                [JsonProperty("Text Align")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Text Lines")]
                public List<string> textLines { get; set; }

                [JsonProperty("Background Image URL")]
                public string tabImageUrl { get; set; }

                [JsonProperty("Background Image Anchoring")]
                public List<string> tabImageAnchor { get; set; }
            }

            [JsonProperty(PropertyName = "Tab #5 Settings")]
            public Tab5Settings tab5Settings { get; set; }

            public class Tab5Settings
            {
                [JsonProperty("Enabled")]
                public bool btnEnabled { get; set; }

                [JsonProperty("Button Title")]
                public string btnTitle { get; set; }

                [JsonProperty("Button Icon Img")]
                public string btnIcon { get; set; }

                [JsonProperty("Base Text Size")]
                public int mainFontSize { get; set; }

                [JsonProperty("Base Text Color")]
                public string mainFontColor { get; set; }

                [JsonProperty("Text Style")]
                public string mainFontStyle { get; set; }

                [JsonProperty("Text Outline Color")]
                public string fontOutlineColor { get; set; }

                [JsonProperty("Text Outline Thickness")]
                public string fontOutlineThick { get; set; }

                [JsonProperty("Text Align")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Text Lines")]
                public List<string> textLines { get; set; }

                [JsonProperty("Background Image URL")]
                public string tabImageUrl { get; set; }

                [JsonProperty("Background Image Anchoring")]
                public List<string> tabImageAnchor { get; set; }
            }

            [JsonProperty(PropertyName = "Tab #6 Settings")]
            public Tab6Settings tab6Settings { get; set; }

            public class Tab6Settings
            {
                [JsonProperty("Enabled")]
                public bool btnEnabled { get; set; }

                [JsonProperty("Button Title")]
                public string btnTitle { get; set; }

                [JsonProperty("Button Icon Img")]
                public string btnIcon { get; set; }

                [JsonProperty("Base Text Size")]
                public int mainFontSize { get; set; }

                [JsonProperty("Base Text Color")]
                public string mainFontColor { get; set; }

                [JsonProperty("Text Style")]
                public string mainFontStyle { get; set; }

                [JsonProperty("Text Outline Color")]
                public string fontOutlineColor { get; set; }

                [JsonProperty("Text Outline Thickness")]
                public string fontOutlineThick { get; set; }

                [JsonProperty("Text Align")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Text Lines")]
                public List<string> textLines { get; set; }

                [JsonProperty("Background Image URL")]
                public string tabImageUrl { get; set; }

                [JsonProperty("Background Image Anchoring")]
                public List<string> tabImageAnchor { get; set; }
            }

            [JsonProperty(PropertyName = "Tab #7 Settings")]
            public Tab7Settings tab7Settings { get; set; }

            public class Tab7Settings
            {
                [JsonProperty("Enabled")]
                public bool btnEnabled { get; set; }

                [JsonProperty("Button Title")]
                public string btnTitle { get; set; }

                [JsonProperty("Button Icon Img")]
                public string btnIcon { get; set; }

                [JsonProperty("Base Text Size")]
                public int mainFontSize { get; set; }

                [JsonProperty("Base Text Color")]
                public string mainFontColor { get; set; }

                [JsonProperty("Text Style")]
                public string mainFontStyle { get; set; }

                [JsonProperty("Text Outline Color")]
                public string fontOutlineColor { get; set; }

                [JsonProperty("Text Outline Thickness")]
                public string fontOutlineThick { get; set; }

                [JsonProperty("Text Align")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Text Lines")]
                public List<string> textLines { get; set; }

                [JsonProperty("Background Image URL")]
                public string tabImageUrl { get; set; }

                [JsonProperty("Background Image Anchoring")]
                public List<string> tabImageAnchor { get; set; }
            }

            [JsonProperty(PropertyName = "Tab #8 Settings")]
            public Tab8Settings tab8Settings { get; set; }

            public class Tab8Settings
            {
                [JsonProperty("Enabled")]
                public bool btnEnabled { get; set; }

                [JsonProperty("Button Title")]
                public string btnTitle { get; set; }

                [JsonProperty("Button Icon Img")]
                public string btnIcon { get; set; }

                [JsonProperty("Base Text Size")]
                public int mainFontSize { get; set; }

                [JsonProperty("Base Text Color")]
                public string mainFontColor { get; set; }

                [JsonProperty("Text Style")]
                public string mainFontStyle { get; set; }

                [JsonProperty("Text Outline Color")]
                public string fontOutlineColor { get; set; }

                [JsonProperty("Text Outline Thickness")]
                public string fontOutlineThick { get; set; }

                [JsonProperty("Text Align")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Text Lines")]
                public List<string> textLines { get; set; }

                [JsonProperty("Background Image URL")]
                public string tabImageUrl { get; set; }

                [JsonProperty("Background Image Anchoring")]
                public List<string> tabImageAnchor { get; set; }
            }

            [JsonProperty(PropertyName = "Tab #9 Settings")]
            public Tab9Settings tab9Settings { get; set; }

            public class Tab9Settings
            {
                [JsonProperty("Enabled")]
                public bool btnEnabled { get; set; }

                [JsonProperty("Button Title")]
                public string btnTitle { get; set; }

                [JsonProperty("Button Icon Img")]
                public string btnIcon { get; set; }

                [JsonProperty("Base Text Size")]
                public int mainFontSize { get; set; }

                [JsonProperty("Base Text Color")]
                public string mainFontColor { get; set; }

                [JsonProperty("Text Style")]
                public string mainFontStyle { get; set; }

                [JsonProperty("Text Outline Color")]
                public string fontOutlineColor { get; set; }

                [JsonProperty("Text Outline Thickness")]
                public string fontOutlineThick { get; set; }

                [JsonProperty("Text Align")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Text Lines")]
                public List<string> textLines { get; set; }

                [JsonProperty("Background Image URL")]
                public string tabImageUrl { get; set; }

                [JsonProperty("Background Image Anchoring")]
                public List<string> tabImageAnchor { get; set; }
            }

            [JsonProperty(PropertyName = "Tab #10 Settings")]
            public Tab10Settings tab10Settings { get; set; }

            public class Tab10Settings
            {
                [JsonProperty("Enabled")]
                public bool btnEnabled { get; set; }

                [JsonProperty("Button Title")]
                public string btnTitle { get; set; }

                [JsonProperty("Button Icon Img")]
                public string btnIcon { get; set; }

                [JsonProperty("Base Text Size")]
                public int mainFontSize { get; set; }

                [JsonProperty("Base Text Color")]
                public string mainFontColor { get; set; }

                [JsonProperty("Text Style")]
                public string mainFontStyle { get; set; }

                [JsonProperty("Text Outline Color")]
                public string fontOutlineColor { get; set; }

                [JsonProperty("Text Outline Thickness")]
                public string fontOutlineThick { get; set; }

                [JsonProperty("Text Align")]
                public TextAnchor mainAlign { get; set; }

                [JsonProperty("Text Lines")]
                public List<string> textLines { get; set; }

                [JsonProperty("Background Image URL")]
                public string tabImageUrl { get; set; }

                [JsonProperty("Background Image Anchoring")]
                public List<string> tabImageAnchor { get; set; }
            }

            [JsonProperty(PropertyName = "Addons")]
            public Extension extension { get; set; }

            public class Extension
            {
                [JsonProperty("Tab 1")]
                public string tab1 { get; set; }

                [JsonProperty("Tab 2")]
                public string tab2 { get; set; }

                [JsonProperty("Tab 3")]
                public string tab3 { get; set; }

                [JsonProperty("Tab 4")]
                public string tab4 { get; set; }

                [JsonProperty("Tab 5")]
                public string tab5 { get; set; }

                [JsonProperty("Tab 6")]
                public string tab6 { get; set; }

                [JsonProperty("Tab 7")]
                public string tab7 { get; set; }

                [JsonProperty("Tab 8")]
                public string tab8 { get; set; }

                [JsonProperty("Tab 9")]
                public string tab9 { get; set; }

                [JsonProperty("Tab 10")]
                public string tab10 { get; set; }
            }


            public static Configuration CreateConfig()
            {
                return new Configuration
                {

                    generalSettings = new OctaWelcome.Configuration.GeneralSettings
                    {
                        openOnConnect = true,
                        openTab = 1,
                        openOnce = false,
                        permsEnabled = false,
                        customCommands = new List<string>
                            {
                                "info",
                                "rules",
                                "kits",
                                "wipe",
                                "vipinfo",
                                "discord",
                                "shop",
                                "admins",
                                "news",
                                "bosses"
                            }

                    },

                    buttonSettings = new OctaWelcome.Configuration.ButtonSettings
                    {

                        btnFontStyle = "robotocondensed-bold.ttf",
                        closeBtnText = "✘ CLOSE",
                        mainAlign = TextAnchor.MiddleCenter,
                        btnIconAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }

                        //"0.13 0.33",
                        //"0.20 0.65"
                    },

                    titleSettings = new OctaWelcome.Configuration.TitleSettings
                    {
                        titleText = "<size=55><color=#E8B84B>OCTAVIAN</color></size>",
                        titleColor = "1 1 1 1",
                        titleFontStyle = "robotocondensed-bold.ttf",
                        titleOutlineColor = "0 0 0 1",
                        titleOutlineThick = "1.5",

                    },

                    tab1Settings = new OctaWelcome.Configuration.Tab1Settings
                    {
                        btnEnabled = true,
                        btnTitle = " HOME",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 12,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-bold.ttf",
                        mainAlign = TextAnchor.UpperLeft,
                        textLines = new List<string>
                                {
                                "<size=45><color=#E8B84B>OCTAVIAN</color></size> ",
                                "<size=25>ВАЙПЫ <color=#83b8c7>ЕЖЕНЕДЕЛЬНЫЕ</color></size>",
                                "<size=25>РЕЙТЫ <color=#83b8c7>x5 ДОБЫЧА</color> | <color=#83b8c7>x5 ЛУТ</color></size> ",
                                "<size=25>КАРТА <color=#83b8c7>4200</color></size> ",
                                "",
                                "<size=15>Сервер в EU. Вайп карт — еженедельно, чертежи — ежемесячно.</size>",
                                "<size=15>Вкладки слева: правила, наборы, VIP, боссы и контакты.</size>",
                                "",
                                "<size=15><color=#83b8c7>Наборы — вкладка НАБОРЫ или /kits • Боссы — вкладка БОССЫ или /bosses</color></size>"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }

                    },

                    tab2Settings = new OctaWelcome.Configuration.Tab2Settings
                    {
                        btnEnabled = true,
                        btnTitle = " RULES",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 12,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-regular.ttf",
                        mainAlign = TextAnchor.UpperLeft,
                        textLines = new List<string>
                                {
                                    "<size=40><color=#E8B84B>ПРАВИЛА СЕРВЕРА</color></size>",
                                    "",
                                    "<size=17><color=#83b8c7>1.</color> Читы, макросы, багоюз — перманентный бан без предупреждений.</size>",
                                    "<size=17><color=#83b8c7>2.</color> Уважай игроков и администрацию — токсичность карается.</size>",
                                    "<size=17><color=#83b8c7>3.</color> Инсайдерство и кража у своей команды запрещены.</size>",
                                    "<size=17><color=#83b8c7>4.</color> Оффлайн-рейды разрешены — это Rust.</size>",
                                    "<size=17><color=#83b8c7>5.</color> Реклама сторонних серверов — бан.</size>",
                                    "",
                                    "<size=14>Незнание правил не освобождает от ответственности.</size>",
                                    "<size=14>Споры решает администрация — вкладка АДМИНЫ или ДИСКОРД.</size>"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    tab3Settings = new OctaWelcome.Configuration.Tab3Settings
                    {
                        btnEnabled = true,
                        btnTitle = "   НАБОРЫ",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 15,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0",
                        mainFontStyle = "robotocondensed-bold.ttf",
                        mainAlign = TextAnchor.UpperLeft,
                        textLines = new List<string>
                                {
                                    "<size=40><color=#E8B84B>НАБОРЫ</color></size>",
                                    "",
                                    "<size=17>Список наборов: <color=#83b8c7>/kits</color></size>",
                                    "",
                                    "<size=17><color=#83b8c7>STARTER</color> — инструменты и еда на первый день.</size>",
                                    "<size=17><color=#83b8c7>BUILDER</color> — материалы для быстрой постройки.</size>",
                                    "<size=17><color=#83b8c7>VIP</color> — расширенные наборы по статусу (вкладка VIP).</size>",
                                    "",
                                    "<size=14>Кулдауны видны прямо в /kits.</size>"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    tab4Settings = new OctaWelcome.Configuration.Tab4Settings
                    {
                        btnEnabled = true,
                        btnTitle = "   ВАЙПЫ",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 12,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "1.5",
                        mainFontStyle = "robotocondensed-bold.ttf",
                        mainAlign = TextAnchor.MiddleCenter,
                        textLines = new List<string>
                                {
                                    "<size=40><color=#E8B84B>РАСПИСАНИЕ ВАЙПОВ</color></size>",
                                    "",
                                    "<size=22>КАРТА <color=#83b8c7>каждый четверг</color></size>",
                                    "<size=22>ЧЕРТЕЖИ <color=#83b8c7>первый четверг месяца</color></size>",
                                    "",
                                    "<size=17>До вайпа: {wipecountdown}</size>",
                                    "",
                                    "<size=14>Вайп карт не трогает чертежи. Точное время — в ДИСКОРД.</size>"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    tab5Settings = new OctaWelcome.Configuration.Tab5Settings
                    {
                        btnEnabled = true,
                        btnTitle = "  VIP",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 18,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-regular.ttf",
                        mainAlign = TextAnchor.MiddleCenter,
                        textLines = new List<string>
                                {
                                    "<size=40><color=#E8B84B>VIP СТАТУСЫ</color></size>",
                                    "",
                                    "<size=20><color=#83b8c7>BRONZE → SILVER → GOLD → BRILLIANT</color></size>",
                                    "",
                                    "<size=16><color=#83b8c7>◆</color> Приоритет входа в очередь</size>",
                                    "<size=16><color=#83b8c7>◆</color> Ускоренный крафт и печи</size>",
                                    "<size=16><color=#83b8c7>◆</color> Расширенные наборы /kits</size>",
                                    "<size=16><color=#83b8c7>◆</color> Увеличенный рюкзак</size>",
                                    "",
                                    "<size=14>Оформить — в ДИСКОРД или у администрации.</size>"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    tab6Settings = new OctaWelcome.Configuration.Tab6Settings
                    {
                        btnEnabled = true,
                        btnTitle = "  ДИСКОРД",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 18,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-bold.ttf",
                        mainAlign = TextAnchor.MiddleCenter,
                        textLines = new List<string>
                                {
                                    "<size=40><color=#E8B84B>ДИСКОРД</color></size>",
                                    "",
                                    "<size=20>Наш дом за пределами острова:</size>",
                                    "<size=22><color=#83b8c7>discord.gg/octavian</color></size>",
                                    "",
                                    "<size=16>Анонсы вайпов, розыгрыши, репорты,</size>",
                                    "<size=16>поиск тиммейтов и прямая связь с админами.</size>",
                                    "",
                                    "<size=14>Нашёл баг? Пиши в #reports — чиним быстро.</size>"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    tab7Settings = new OctaWelcome.Configuration.Tab7Settings
                    {
                        btnEnabled = true,
                        btnTitle = " МАГАЗИН",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 17,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-regular.ttf",
                        mainAlign = TextAnchor.MiddleCenter,
                        textLines = new List<string>
                                {
                                    "<size=40><color=#E8B84B>МАГАЗИН</color></size>",
                                    "",
                                    "<size=17>Внутриигровой магазин: <color=#83b8c7>/shop</color></size>",
                                    "",
                                    "<size=16>RP капает за активность: боссы, ивенты, часы в игре.</size>",
                                    "<size=16>Трать на оружие, ресурсы, скины и привилегии.</size>",
                                    "",
                                    "<size=16>Босс-ивенты — самый жирный источник RP.</size>",
                                    "<size=16>Смотри вкладку БОССЫ и /bosstimer.</size>"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    tab8Settings = new OctaWelcome.Configuration.Tab8Settings
                    {
                        btnEnabled = true,
                        btnTitle = " АДМИНЫ",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 17,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-regular.ttf",
                        mainAlign = TextAnchor.MiddleCenter,
                        textLines = new List<string>
                                {
                                    "<size=40><color=#E8B84B>АДМИНИСТРАЦИЯ</color></size>",
                                    "",
                                    "<size=17>Онлайн-админы видны в списке игроков (TAB).</size>",
                                    "",
                                    "<size=16>Репорт игрока: <color=#83b8c7>/report <ник> <причина></color></size>",
                                    "<size=16>Срочно: тикет в ДИСКОРД #support.</size>",
                                    "",
                                    "<size=14>Админы не играют на равных и не выдают лут.</size>",
                                    "<size=14>Жалоба на админа — создателю в ДИСКОРД.</size>"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    tab9Settings = new OctaWelcome.Configuration.Tab9Settings
                    {
                        btnEnabled = true,
                        btnTitle = " НОВОСТИ",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 17,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-regular.ttf",
                        mainAlign = TextAnchor.MiddleCenter,
                        textLines = new List<string>
                                {
                                    "<size=40><color=#E8B84B>НОВОСТИ</color></size>",
                                    "",
                                    "<size=17><color=#83b8c7>◆</color> Запущена система боссов-зверей — вкладка БОССЫ.</size>",
                                    "<size=17><color=#83b8c7>◆</color> Обновлены наборы и VIP-привилегии.</size>",
                                    "<size=17><color=#83b8c7>◆</color> Рейт x5 на добычу и лут активен.</size>",
                                    "",
                                    "<size=14>Свежее — в ДИСКОРД #announcements.</size>"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    tab10Settings = new OctaWelcome.Configuration.Tab10Settings
                    {
                        btnEnabled = true,
                        btnTitle = " БОССЫ",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 15,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-regular.ttf",
                        mainAlign = TextAnchor.MiddleCenter,
                        textLines = new List<string>
                                {
                                    "<size=40><color=#E8B84B>БОССЫ ОСТРОВА</color></size>",
                                    "",
                                    "<size=16>Гигантские звери с лутом и наградами появляются по расписанию.</size>",
                                    "",
                                    "<size=16><color=#83b8c7>◆</color> Место спавна — красный маркер на карте.</size>",
                                    "<size=16><color=#83b8c7>◆</color> Награда достаётся ТОП-дамагеру, не последнему удару.</size>",
                                    "<size=16><color=#83b8c7>◆</color> Отвлёкся на минуту — босс залечит раны.</size>",
                                    "",
                                    "<size=17>Листай страницы → каждый босс со статами, свитой и лутом.</size>",
                                    "",
                                    "<size=14>/bosstimer — расписание • /bossinfo — кто жив • /bosslast — киллеры</size>"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    extension = new OctaWelcome.Configuration.Extension
                    {
                        tab1 = "null",
                        tab2 = "null",
                        tab3 = "OctaKits",
                        tab4 = "null",
                        tab5 = "null",
                        tab6 = "null",
                        tab7 = "null",
                        tab8 = "null",
                        tab9 = "null",
                        tab10 = "null",
                    },


                };
            }
        }
        #endregion

    }
}