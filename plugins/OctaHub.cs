using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Globalization;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaHub", "Octavian", "1.1.0")]
    [Description("Приветственная панель Octavian с нуля — каркас админки OctaBoss поверх OctaUI: экранные якоря вместо пиксельного бокса, рейл вкладок, частичная перерисовка, наборы, многостраничный контент, страницы боссов")]

    public class OctaHub : RustPlugin
    {

        #region [Dependencies]

        [PluginReference] Plugin ImageLibrary, OctaKits, OctaUI;
        [PluginReference] Plugin Economics, ServerRewards, WipeCountdown;

        private void DependencyWarnings()
        {
            timer.Once(1f, () =>
            {
                if (OctaUI == null)
                    Puts("(!) Фреймворк OctaUI не найден — интерфейс не будет отрисовываться (c.reload OctaUI).");
                if (ImageLibrary == null)
                    Puts("(!) ImageLibrary не найден — картинки будут грузиться медленнее.");
                if (OctaKits == null) Puts("(!) Плагин OctaKits не найден — вкладка наборов будет пустой.");
            });
        }

        #region API Calls

        private void OpenAddOn_API(BasePlayer player, string _apiName)
        {
            // Вкладка наборов — собственный рендер, данные берутся напрямую из OctaKits.
            if (_apiName == "OctaKits") { KitsTab_Show(player, 0); return; }

            // Прочие аддоны (если появятся) — через CallHook.
            var addon = plugins.Find(_apiName);
            if (addon != null) addon.CallHook("ShowKits_Page1_API", player);
        }

        private void CloseAddOn_API(BasePlayer player)
        {
            // Своих внешних аддонов нет — добиваем элементы вкладки наборов.
            KitsTab_Destroy(player);
        }

        #endregion

        #endregion

        #region [Fields & State]

        // Именованные части интерфейса — частичная перерисовка, как в админке OctaBoss.
        // Корень крепится к Overlay долями экрана: уехать за края невозможно на любом разрешении.
        private const string UiRoot = "oh_ui";
        private const string UiHdr  = "oh_hdr";
        private const string UiNav  = "oh_nav";
        private const string UiCnt  = "oh_cnt";
        private const string UiBot  = "oh_bot";
        private const string Mat    = "assets/icons/iconmaterial.mat";

        private class UiState { public int Tab = 1; public int Page = 1; public int KitPage = 0; }
        private readonly Dictionary<ulong, UiState> _ui = new Dictionary<ulong, UiState>();

        // Страховка от «неприкаянного» рендера: состояние создаётся по требованию
        private UiState St(BasePlayer player)
        {
            UiState s;
            if (!_ui.TryGetValue(player.userID, out s))
                s = _ui[player.userID] = new UiState();
            return s;
        }

        // Количество вкладок — единая константа для рейла, пермишенов и клампов
        private const int TabCount = 11;

        // Порядок вкладок в рейле по полезности: позиция -> номер вкладки.
        // Нумерация вкладок (контент, аддоны, PageData, пермишены) не меняется.
        private static readonly int[] NavOrder = { 1, 11, 3, 10, 5, 7, 9, 4, 8, 6, 2 };

        private List<string> tabList = new List<string>() { "Tab1", "Tab2", "Tab3", "Tab4", "Tab5", "Tab6", "Tab7", "Tab8", "Tab9", "Tab10", "Tab11" };

        // «Показано один раз за вайп» — персистится в data/OctaHub/Seen.json
        private List<ulong> _seen = new List<ulong>();

        #endregion

        #region [Hooks]

        private void Loaded()
        {
            CUIClass.Ui = OctaUI; // подключаем фреймворк, если загружен
            if (OctaUI == null)
                PrintWarning("OctaUI не загружен — интерфейс OctaHub не отрисуется (c.reload OctaUI)");
        }

        private void OnServerInitialized()
        {
            CUIClass.Ui = OctaUI;
            LoadSeen();
            LoadPageData();
            WritePageEntry();
            SeedVipPages();
            PopulateBossPages();
            DependencyWarnings();
            RegisterCommands();
            RegisterPerms();
            DownloadImages();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (config?.generalSettings == null || !config.generalSettings.openOnConnect) return;

            if (config.generalSettings.openOnce)
            {
                if (_seen.Contains(player.userID)) return;
                _seen.Add(player.userID);
                SaveSeen();
            }

            // Даём клиенту секунду на инициализацию, потом открываем на заданной вкладке
            timer.Once(1f, () =>
            {
                if (player != null && player.IsConnected)
                    UiOpen(player, ClampTab(config.generalSettings.openTab), 1);
            });
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player != null) _ui.Remove(player.userID);
        }

        private void OnNewSave()
        {
            _seen.Clear();
            SaveSeen();
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                UiClose(p);
                CloseAddOn_API(p);
            }
            _ui.Clear();
        }

        // Хук загрузки плагинов: OctaUI поднялся после нас — подцепляем ссылку
        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin?.Name == "OctaUI")
                CUIClass.Ui = plugin;
        }

        // OctaUI выгрузили/перезагрузили — сбрасываем ссылку, чтобы не звать в мёртвую сборку
        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin?.Name == "OctaUI")
                CUIClass.Ui = null;
        }

        #endregion

        #region [Config & Data Migration]

        // Первый запуск OctaHub: подтягиваем конфиг и данные старого OctaWelcome,
        // чтобы контент вкладок, страницы и список «уже видели» не потерялись.
        private void MigrateLegacyData()
        {
            try
            {
                var cfgDir = Interface.Oxide.ConfigDirectory;
                var cfgDst = Path.Combine(cfgDir, "OctaHub.json");
                var cfgSrc = Path.Combine(cfgDir, "OctaWelcome.json");
                if (!File.Exists(cfgDst) && File.Exists(cfgSrc))
                {
                    File.Copy(cfgSrc, cfgDst);
                    Puts("Конфиг перенесён: OctaWelcome.json → OctaHub.json");
                }

                var dataDir = Interface.Oxide.DataDirectory;
                var legacyDir = Path.Combine(dataDir, "OctaWelcome");
                var hubDir = Path.Combine(dataDir, "OctaHub");

                var lp = Path.Combine(legacyDir, "PageData.json");
                var np = Path.Combine(hubDir, "PageData.json");
                if (!File.Exists(np) && File.Exists(lp))
                {
                    Directory.CreateDirectory(hubDir);
                    File.Copy(lp, np);
                    Puts("Страницы вкладок перенесены: OctaWelcome/PageData.json → OctaHub/PageData.json");
                }

                var ls = Path.Combine(legacyDir, "PresetData.json");
                var ns = Path.Combine(hubDir, "Seen.json");
                if (!File.Exists(ns) && File.Exists(ls))
                {
                    var seen = JObject.Parse(File.ReadAllText(ls))["OctaWelcome"]?["seen"]?.ToObject<List<ulong>>()
                               ?? new List<ulong>();
                    Directory.CreateDirectory(hubDir);
                    File.WriteAllText(ns, JsonConvert.SerializeObject(seen));
                    Puts($"Перенесён список просмотров once-per-wipe: {seen.Count} игроков.");
                }
            }
            catch (Exception ex) { Puts($"(!) Миграция данных OctaWelcome: {ex.Message}"); }
        }

        private void LoadSeen()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile($"{Name}/Seen"))
                _seen = Interface.Oxide.DataFileSystem.ReadObject<List<ulong>>($"{Name}/Seen") ?? new List<ulong>();
            else
                _seen = new List<ulong>();
        }

        private void SaveSeen()
        {
            Interface.Oxide.DataFileSystem.WriteObject($"{Name}/Seen", _seen);
        }

        #endregion

        #region [Image Handling]

        private void DownloadImages()
        {
            if (ImageLibrary == null) return;
            for (int i = 1; i <= TabCount; i++)
            {
                var url = TabCfg(i).tabImageUrl;
                if (url != null && (url.StartsWith("http") || url.StartsWith("www")))
                    ImageLibrary.Call("AddImage", url, $"tab{i}bg");
            }
        }

        private string Img2(int tabNumber)
        {
            string url = TabCfg(tabNumber).tabImageUrl;
            if (ImageLibrary != null)
            {
                if (!(bool)ImageLibrary.Call("HasImage", $"tab{tabNumber}bg"))
                    return url;
                else
                    return (string)ImageLibrary?.Call("GetImage", $"tab{tabNumber}bg");
            }
            else return url;
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

        #endregion

        #region [Commands]

        private void RegisterCommands()
        {
            if (config?.generalSettings?.customCommands == null) return;
            foreach (var command in config.generalSettings.customCommands)
                cmd.AddChatCommand(command, this, "Wp_CustomCommands");
        }

        private object Wp_CustomCommands(BasePlayer player, string command, string[] args)
        {
            int pageNumber = 1;
            if (args.Length > 0 && IsDigitsOnly(args[0])) pageNumber = Convert.ToInt32(args[0]);

            if (config?.generalSettings?.customCommands?.Contains(command) == true)
            {
                int tab = config.generalSettings.customCommands.IndexOf(command) + 1;
                UiOpen(player, ClampTab(tab), pageNumber);
            }
            return null;
        }

        [ChatCommand("octahub")]
        private void CmdOctaHub(BasePlayer player)
        {
            if (player == null) return;
            if (_ui.ContainsKey(player.userID)) UiClose(player);
            else UiOpen(player, ClampTab(config?.generalSettings?.openTab ?? 1), 1);
        }

        // Алиас на старую привычную команду — открывает панель
        [ChatCommand("octawelcome")]
        private void CmdOctaWelcome(BasePlayer player) => CmdOctaHub(player);

        [ConsoleCommand("octahub_open")]
        private void CmdOpen(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 1) return;

            int tab = ClampTab(Convert.ToInt32(args[0]));
            int page = args.Length > 1 ? Convert.ToInt32(args[1]) : 1;

            if (!_ui.ContainsKey(player.userID)) { UiOpen(player, tab, page); return; }

            var st = St(player);
            st.Tab = tab; st.Page = page;
            UiRefresh(player, UiHdr, UiNav, UiCnt, UiBot);

            var addon = TabAddon(tab);
            if (!string.IsNullOrEmpty(addon) && addon != "null") OpenAddOn_API(player, addon);
            else CloseAddOn_API(player);
        }

        [ConsoleCommand("octahub_close")]
        private void CmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            UiClose(player);
        }

        [ChatCommand("octahub_populatepages")]
        private void CmdPopulatePages(BasePlayer player)
        {
            if (player == null || !player.IsAdmin) return;
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

        private static int ClampTab(int n) => n < 1 ? 1 : (n > TabCount ? TabCount : n);

        private void RegisterPerms()
        {
            if (config?.generalSettings?.permsEnabled != true) return;
            for (int i = 1; i <= TabCount; i++)
                permission.RegisterPermission($"octawelcome.tab{i}", this); // имя оставлено — старые выдачи работают
        }

        private bool CheckPerms(BasePlayer player, int tabNumber)
        {
            if (config?.generalSettings?.permsEnabled != true) return true;
            return permission.UserHasPermission(player.UserIDString, $"octawelcome.tab{tabNumber}");
        }

        private void WritePageEntry()
        {
            foreach (string tab in tabList)
            {
                if (!pageData.ContainsKey(tab))
                    pageData.Add(tab, new PageData());
            }
            SavePageData();
        }

        private Configuration.TabSettings TabCfg(int n)
        {
            switch (n)
            {
                case 2: return config.tab2Settings;
                case 3: return config.tab3Settings;
                case 4: return config.tab4Settings;
                case 5: return config.tab5Settings;
                case 6: return config.tab6Settings;
                case 7: return config.tab7Settings;
                case 8: return config.tab8Settings;
                case 9: return config.tab9Settings;
                case 10: return config.tab10Settings;
                case 11: return config.tab11Settings;
                default: return config.tab1Settings;
            }
        }

        private string TabAddon(int n)
        {
            switch (n)
            {
                case 1: return config.extension?.tab1;
                case 2: return config.extension?.tab2;
                case 3: return config.extension?.tab3;
                case 4: return config.extension?.tab4;
                case 5: return config.extension?.tab5;
                case 6: return config.extension?.tab6;
                case 7: return config.extension?.tab7;
                case 8: return config.extension?.tab8;
                case 9: return config.extension?.tab9;
                case 10: return config.extension?.tab10;
                default: return null;
            }
        }

        //Replace Text
        private string R(BasePlayer player, string _text)
        {
            string text = _text;
            if (text.Contains("{playername}")) text = text.Replace("{playername}", player.displayName);
            if (text.Contains("{pvp/pve}")) text = text.Replace("{pvp/pve}", ConVar.Server.pve ? "PVE" : "PVP");
            if (text.Contains("{maxplayers}")) text = text.Replace("{maxplayers}", $"{(int)ConVar.Server.maxplayers}");
            if (text.Contains("{online}")) text = text.Replace("{online}", $"{(int)BasePlayer.activePlayerList.Count()}");
            if (text.Contains("{sleeping}")) text = text.Replace("{sleeping}", $"{(int)BasePlayer.sleepingPlayerList.Count()}");
            if (text.Contains("{joining}")) text = text.Replace("{joining}", $"{(int)ServerMgr.Instance.connectionQueue.Joining}");
            if (text.Contains("{queued}")) text = text.Replace("{queued}", $"{(int)ServerMgr.Instance.connectionQueue.Queued}");
            if (text.Contains("{worldsize}")) text = text.Replace("{worldsize}", $"{(int)ConVar.Server.worldsize}");
            if (text.Contains("{hostname}")) text = text.Replace("{hostname}", ConVar.Server.hostname);
            if (WipeCountdown != null && text.Contains("{wipecountdown}"))
                text = text.Replace("{wipecountdown}", Convert.ToString(WipeCountdown.CallHook("GetCountdownFormated_API")));
            if (Economics != null && text.Contains("{economics}"))
                text = text.Replace("{economics}", $"{Economics.Call<double>("Balance", player.UserIDString)}");
            if (ServerRewards != null && text.Contains("{rp}"))
                text = text.Replace("{rp}", $"{ServerRewards?.Call<int>("CheckPoints", (ulong)player.userID)}");
            return text;
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

        // Строки дополнительной страницы вкладки (page2..page10 из PageData.json)
        private List<string> PageLines(int tabNumber, int pageNumber)
        {
            var d = pageData[$"Tab{tabNumber}"];
            switch (pageNumber)
            {
                case 3: return d.page3;
                case 4: return d.page4;
                case 5: return d.page5;
                case 6: return d.page6;
                case 7: return d.page7;
                case 8: return d.page8;
                case 9: return d.page9;
                case 10: return d.page10;
                default: return d.page2;
            }
        }

        private bool CheckPage(int tabNumber, int pageNumber)
        {
            // Страница 1 «многостранична», если заполнена page2; остальные — по своему списку
            var lines = PageLines(tabNumber, pageNumber < 2 ? 2 : pageNumber);
            return lines != null && lines.Count > 0;
        }

        private int MaxPage(int tabNumber)
        {
            int max = 1;
            for (int p = 2; p <= 10; p++)
                if (CheckPage(tabNumber, p)) max = p;
            return max;
        }

        // Снимаем <size> из заголовков кнопок/титула — в рейле и шапке размер единый
        private static readonly Regex RxSize = new Regex("</?size[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static string StripSize(string s) => string.IsNullOrEmpty(s) ? s : RxSize.Replace(s, "");

        #endregion

        #region [UI — каркас в стиле админки OctaBoss]

        // Корень — доли экрана (0.17..0.83 x 0.08..0.92), курсор включён.
        // Все дети — доли корня: никаких пиксельных боксов, уехать за экран невозможно.
        private void UiOpen(BasePlayer player, int tab, int page)
        {
            if (CUIClass.Ui == null) return;
            UiClose(player);
            _ui[player.userID] = new UiState { Tab = ClampTab(tab), Page = page };

            var c = CUIClass.CreateOverlay(UiRoot, "bg", "0.17 0.08", "0.83 0.92", true, 0f,
                "assets/content/ui/uibackgroundblur.mat");
            // золотые кромки сверху и снизу — фирменная рамка
            CUIClass.CreatePanel(ref c, "oh_edge_t", UiRoot, "accent.dark", "0 0.995", "1 1", false, 0f, Mat);
            CUIClass.CreatePanel(ref c, "oh_edge_b", UiRoot, "accent.dark", "0 0", "1 0.005", false, 0f, Mat);
            CuiHelper.AddUi(player, c);

            UiRefresh(player, UiHdr, UiNav, UiCnt, UiBot);

            var addon = TabAddon(St(player).Tab);
            if (!string.IsNullOrEmpty(addon) && addon != "null") OpenAddOn_API(player, addon);
        }

        private void UiClose(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.DestroyUi(player, UiHdr);
            CuiHelper.DestroyUi(player, UiNav);
            CuiHelper.DestroyUi(player, UiCnt);
            CuiHelper.DestroyUi(player, UiBot);
            KitsTab_Destroy(player);
            _ui.Remove(player.userID);
        }

        // Частичная перерисовка: сносим названные части и добавляем их заново одним батчом
        private void UiRefresh(BasePlayer player, params string[] parts)
        {
            foreach (var p in parts) CuiHelper.DestroyUi(player, p);
            var ui = new CuiElementContainer();
            foreach (var p in parts)
            {
                switch (p)
                {
                    case UiHdr: DrawHeader(ref ui, player); break;
                    case UiNav: DrawNav(ref ui, player); break;
                    case UiCnt: DrawContent(ref ui, player); break;
                    case UiBot: DrawFooter(ref ui, player); break;
                }
            }
            CuiHelper.AddUi(player, ui);
        }

        private void DrawHeader(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);
            var tab = TabCfg(st.Tab);

            CUIClass.CreatePanel(ref ui, UiHdr, UiRoot, "panel", "0 0.918", "1 0.995", false, 0f, Mat);
            CUIClass.CreatePanel(ref ui, "oh_hdr_line", UiHdr, "accent.dark", "0 0", "1 0.045", false, 0f, Mat);

            // эмблема-фишка
            CUIClass.CreatePanel(ref ui, "oh_hdr_chip", UiHdr, "accent.dark", "0.011 0.14", "0.056 0.90", false, 0f, Mat);
            CUIClass.CreateText(ref ui, "oh_hdr_chip_t", "oh_hdr_chip", "accent", "◆", 16, "0 0", "1 1",
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // титул сервера + имя активной вкладки
            string title = StripSize(R(player, config?.titleSettings?.titleText ?? "OCTAVIAN"));
            string tabTitle = StripSize(tab?.btnTitle ?? "");
            CUIClass.CreateText(ref ui, "oh_hdr_title", UiHdr, "text",
                $"<b>{title}</b>  <color=@muted>—</color>  <color=@accent>{tabTitle}</color>",
                15, "0.062 0", "0.72 1", TextAnchor.MiddleLeft, config?.titleSettings?.titleFontStyle ?? "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // справа: онлайн-справка + крест закрытия
            CUIClass.CreateText(ref ui, "oh_hdr_meta", UiHdr, "muted",
                $"online {BasePlayer.activePlayerList.Count()}/{ConVar.Server.maxplayers}", 11,
                "0.72 0", "0.945 1", TextAnchor.MiddleRight, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // квадратная кнопка-крест: не клипает текст на любых настройках ширины
            string closeTxt = StripSize(config?.buttonSettings?.closeBtnText ?? "✕");
            CUIClass.CreateButton(ref ui, "oh_hdr_close", UiHdr, "red", $"<b>{closeTxt}</b>", 16,
                "0.955 0.14", "0.992 0.90", "octahub_close", UiRoot, "white", 0f, TextAnchor.MiddleCenter,
                config?.buttonSettings?.btnFontStyle ?? "robotocondensed-bold.ttf");
        }

        private void DrawNav(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);
            CUIClass.CreatePanel(ref ui, UiNav, UiRoot, "panel", "0.008 0.014", "0.178 0.908", false, 0f, Mat);

            // Шаг и высота кнопок подгоняются под число вкладок — рейл всегда заполнен равномерно
            float pitch = 0.965f / TabCount, h = pitch * 0.89f;
            const float top = 0.985f;
            for (int pos = 1; pos <= TabCount; pos++)
            {
                int i = NavOrder[pos - 1]; // позиция в рейле -> реальная вкладка
                var t = TabCfg(i);
                if (t == null || !t.btnEnabled) continue;

                float y1 = top - pos * pitch, y2 = y1 + h;
                string aMin = $"0.03 {y1.ToString("0.####", CultureInfo.InvariantCulture)}";
                string aMax = $"0.97 {y2.ToString("0.####", CultureInfo.InvariantCulture)}";

                bool active = i == st.Tab;
                bool allowed = CheckPerms(player, i);
                string bg = active ? "accent.glass" : "row";
                string label = StripSize(t.btnTitle ?? "");
                string labelCol = !allowed ? "muted" : (active ? "accent" : "text");
                string labelTxt = active ? $"<b>{label}</b>" : label;

                CUIClass.CreateButton(ref ui, $"oh_nav_b{i}", UiNav, bg, "", 12, aMin, aMax,
                    $"octahub_open {i} 1", "", "white", 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

                // золотая кромка слева у активной вкладки
                if (active)
                    CUIClass.CreatePanel(ref ui, $"oh_nav_e{i}", $"oh_nav_b{i}", "accent", "0 0.08", "0.02 0.92", false, 0f, Mat);

                // иконка вкладки (если задана)
                bool hasIcon = !string.IsNullOrEmpty(t.btnIcon);
                if (hasIcon)
                    CUIClass.CreateImage(ref ui, $"oh_nav_b{i}", Img3(t.btnIcon), "0.07 0.24", "0.24 0.78", 0.15f);

                CUIClass.CreateText(ref ui, $"oh_nav_t{i}", $"oh_nav_b{i}", labelCol, labelTxt, 12,
                    hasIcon ? "0.27 0" : "0.08 0", "0.98 1", TextAnchor.MiddleLeft,
                    config?.buttonSettings?.btnFontStyle ?? "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                if (!allowed)
                    CUIClass.CreateText(ref ui, $"oh_nav_l{i}", $"oh_nav_b{i}", "muted", "🔒", 10,
                        "0.86 0", "0.98 1", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            }
        }

        private void DrawContent(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);
            var tab = TabCfg(st.Tab);
            string addon = TabAddon(st.Tab);

            // контент-панель — стекло; аддоны рисуют в неё свои элементы (owkit_* и т.п.)
            CUIClass.CreatePanel(ref ui, UiCnt, UiRoot, "panel", "0.186 0.075", "0.992 0.908", false, 0f, Mat);

            if (addon != null && addon != "null") return; // содержимое придёт из аддона после AddUi

            // фоновая картинка вкладки (внутренние якоря из конфига)
            string bgImage = tab.tabImageUrl;
            if (bgImage != null && (bgImage.StartsWith("http") || bgImage.StartsWith("www")) &&
                tab.tabImageAnchor != null && tab.tabImageAnchor.Count >= 2)
                CUIClass.CreateImage(ref ui, UiCnt, Img2(st.Tab), tab.tabImageAnchor[0], tab.tabImageAnchor[1], 0f);

            if (!CheckPerms(player, st.Tab))
            {
                CUIClass.CreateText(ref ui, "oh_cnt_text", UiCnt, "text",
                    "<size=44>✘</size>\n<color=@muted>Нет доступа к этой странице.</color>", 18,
                    "0.02 0.02", "0.98 0.98", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                return;
            }

            var lines = st.Page >= 2 ? PageLines(st.Tab, st.Page) : tab.textLines;
            var ls = lines ?? new List<string>();

            // Структурная разметка страницы: [H]заголовок / [h]подзаголовок / [F]строка-футер.
            // Остальные строки — тело. Страницы без маркеров рисуются как раньше, одним блоком.
            string headTitle = null, headSub = null;
            var body = new List<string>(); var foot = new List<string>();
            foreach (var l in ls)
            {
                if (l.StartsWith("[H]")) headTitle = l.Substring(3);
                else if (l.StartsWith("[h]")) headSub = l.Substring(3);
                else if (l.StartsWith("[F]")) foot.Add(l.Substring(3));
                else body.Add(l);
            }

            if (headTitle != null || headSub != null)
            {
                // хеадер: большой заголовок + среднее уточнение + золотая линейка
                if (headTitle != null)
                    CUIClass.CreateText(ref ui, "oh_cnt_title", UiCnt, "text", StripSize(headTitle), 26,
                        "0.035 0.865", "0.965 0.985", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                if (headSub != null)
                    CUIClass.CreateText(ref ui, "oh_cnt_sub", UiCnt, "muted", StripSize(headSub), 13,
                        "0.035 0.80", "0.965 0.875", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreatePanel(ref ui, "oh_cnt_rule", UiCnt, "accent.dark", "0.035 0.792", "0.965 0.795", false, 0f, Mat);

                // боди: наглядный текст — центрируется по align вкладки
                CUIClass.CreateText(ref ui, "oh_cnt_text", UiCnt, tab.mainFontColor, R(player, string.Join("\n", body)),
                    tab.mainFontSize, "0.035 0.115", "0.965 0.785", tab.mainAlign,
                    tab.mainFontStyle, tab.fontOutlineColor, $"{tab.fontOutlineThick} {tab.fontOutlineThick}");

                // футер страницы: мелкая строка-подсказка над зоной стрелок
                if (foot.Count > 0)
                    CUIClass.CreateText(ref ui, "oh_cnt_foot", UiCnt, "muted", R(player, string.Join("\n", foot)), 11,
                        "0.035 0.055", "0.875 0.11", TextAnchor.MiddleLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
            }
            else
            {
                string textContent = R(player, string.Join("\n", ls));
                CUIClass.CreateText(ref ui, "oh_cnt_text", UiCnt, tab.mainFontColor, textContent,
                    tab.mainFontSize, "0.015 0.02", "0.985 0.985", tab.mainAlign,
                    tab.mainFontStyle, tab.fontOutlineColor, $"{tab.fontOutlineThick} {tab.fontOutlineThick}");
            }

            // пагинация страниц — нижний правый угол контента, доли панели CNT
            if (st.Page > 1)
                CUIClass.CreateButton(ref ui, "oh_cnt_prev", UiCnt, "accent.glass", "<color=@accent>‹</color>", 16,
                    "0.885 0.015", "0.93 0.08", $"octahub_open {st.Tab} {st.Page - 1}", "", "white", 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            if (CheckPage(st.Tab, st.Page + 1))
                CUIClass.CreateButton(ref ui, "oh_cnt_next", UiCnt, "accent.glass", "<color=@accent>›</color>", 16,
                    "0.935 0.015", "0.985 0.08", $"octahub_open {st.Tab} {st.Page + 1}", "", "white", 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
        }

        private void DrawFooter(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);
            CUIClass.CreatePanel(ref ui, UiBot, UiRoot, "panel", "0.186 0.014", "0.992 0.065", false, 0f, Mat);

            CUIClass.CreateText(ref ui, "oh_bot_hint", UiBot, "muted",
                "<color=@accent>OCTAVIAN</color> · закрыть: ✕ или /octahub", 10,
                "0.012 0", "0.5 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // счётчик страниц: у вкладки наборов — страницы китов, у остальных — PageData
            int cur, max;
            if (TabAddon(st.Tab) == "OctaKits")
            {
                cur = st.KitPage + 1;
                max = (int)Math.Ceiling(KitsTab_List(player).Count / 9.0);
            }
            else { cur = st.Page; max = MaxPage(st.Tab); }

            if (max > 1)
                CUIClass.CreateText(ref ui, "oh_bot_pg", UiBot, "muted",
                    $"стр. <color=@accent>{cur}</color>/{max}", 11,
                    "0.5 0", "0.988 1", TextAnchor.MiddleRight, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
        }

        #endregion

#region [Kits Tab] — собственная вкладка наборов (данные напрямую из плагина Kits)

        // Палитра — токены темы OctaUI (золото на стекле, эталон OctaBoss).
        // Значения резолвятся фреймворком; тема выбирается биндом ThemeBind("OctaHub", ...).
        private const string KGold     = "accent.dim";
        private const string KGoldDark = "accent.dark";
        private const string KGlassBg  = "bg";
        private const string KGlassRow = "row";
        private const string KGlassHov = "hover";
        private const string KGlassLt  = "panel";
        private const string KTWhite   = "white";
        private const string KGreen    = "green";
        private const string KRed      = "red";

        // Сетка 3x3 внутри контент-панели oh_cnt
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
            if (OctaKits == null) return list;

            string[] all = null;
            try { all = OctaKits.Call("GetAllKits") as string[]; } catch { }
            if (all == null) return list;

            bool isAdmin = player.IsAdmin;
            foreach (string name in all)
            {
                JObject kit = null;
                try { kit = OctaKits.Call("GetKitObject", name) as JObject; } catch { }
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
            try { kit = OctaKits?.Call("GetKitObject", kitname) as JObject; } catch { }
            if (kit == null) return $"НЕДОСТУПЕН%{KGlassLt}";

            double cdLeft = 0;
            try { cdLeft = Convert.ToDouble(OctaKits.Call("GetPlayerKitCooldown", (ulong)player.userID, kitname)); } catch { }
            if (cdLeft > 0) return $"⏱ {KitFmtTime(cdLeft)}%{KRed}";

            string perm = kit.SelectToken("RequiredPermission")?.ToString() ?? "";
            if (!string.IsNullOrEmpty(perm))
            {
                // доступ решает OctaKits (обычные пермишены + уровни OctVIP)
                bool hasAccess = false;
                try { hasAccess = Convert.ToBoolean(OctaKits.Call("HasKitPermission", player, perm)); } catch { }
                if (!hasAccess) return $"НЕТ ДОСТУПА%{KGlassLt}";
            }

            int maxUses = kit.SelectToken("MaximumUses")?.Value<int>() ?? 0;
            if (maxUses > 0)
            {
                int used = 0;
                try { used = Convert.ToInt32(OctaKits.Call("GetPlayerKitUses", (ulong)player.userID, kitname)); } catch { }
                if (maxUses - used < 1) return $"ЛИМИТ ИСЧЕРПАН%{KGlassLt}";
            }

            int cost = kit.SelectToken("Cost")?.Value<int>() ?? 0;
            if (cost <= 0) return $"ЗАБРАТЬ%{KGreen}";

            string curLabel = "$";
            try { curLabel = OctaKits.Call("GetCurrencyLabel") as string ?? "$"; } catch { }
            return $"ЗАБРАТЬ • {cost} {curLabel}%{KGreen}";
        }

        private void KitsTab_Show(BasePlayer player, int page = 0)
        {
            var ui = new CuiElementContainer();

            if (OctaKits == null)
            {
                CUIClass.CreateText(ref ui, "owkit_err", "oh_cnt", "1 1 1 0.6",
                    "Система наборов сейчас недоступна.", 16, "0 0", "1 1", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                KitsTab_Destroy(player);
                CuiHelper.AddUi(player, ui);
                return;
            }

            var kits = KitsTab_List(player);
            if (kits.Count == 0)
            {
                CUIClass.CreateText(ref ui, "owkit_err", "oh_cnt", "1 1 1 0.6",
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
                try { descr = OctaKits.Call("GetKitDescription", kitname) as string ?? ""; } catch { }
                if (descr.Length > 60) descr = descr.Substring(0, 60) + "...";

                string image = "";
                try { image = OctaKits.Call("GetKitImage", kitname) as string ?? ""; } catch { }

                int cooldownSec = 0, maxUses = 0, used = 0;
                try { cooldownSec = Convert.ToInt32(OctaKits.Call("GetKitCooldown", kitname)); } catch { }
                try { maxUses = Convert.ToInt32(OctaKits.Call("GetKitMaxUses", kitname)); } catch { }
                try { used = Convert.ToInt32(OctaKits.Call("GetPlayerKitUses", (ulong)player.userID, kitname)); } catch { }

                string cdText = cooldownSec > 0 ? KitFmtTime(cooldownSec) : "НЕТ";
                string usesText = maxUses > 0 ? Math.Max(0, maxUses - used).ToString() : "БЕЗ ЛИМИТА";

                // карточка набора (~202x95 px): тёмное стекло + золотая полоска сверху
                CUIClass.CreatePanel(ref ui, $"owkit_card_{i}", "oh_cnt", KGlassBg, a[0], a[1], false, 0f, "assets/icons/iconmaterial.mat");
                CUIClass.CreatePanel(ref ui, $"owkit_gold_{i}", $"owkit_card_{i}", KGold, "0 0.955", "1 1", false, 0f, "assets/icons/iconmaterial.mat");

                // иконка — жёсткий квадрат 60px у левого верхнего угла (панель задаёт px-геометрию)
                CUIClass.CreatePanel(ref ui, $"owkit_icon_{i}", $"owkit_card_{i}", KGlassRow, "0.04 0.93", "0.04 0.93", false, 0f, "assets/icons/iconmaterial.mat", "", "0 -58", "58 0");
                if (!string.IsNullOrEmpty(image))
                    CUIClass.CreateImage(ref ui, $"owkit_icon_{i}", Img3(image), "0.08 0.08", "0.92 0.92", 0.2f);

                // текст — правая колонка: CreateText оффсетов не принимает,
                // поэтому левый край в px (72px, правее 58px иконки) задают прозрачные панели-обёртки
                CUIClass.CreatePanel(ref ui, $"owkit_tp_{i}", $"owkit_card_{i}", "0 0 0 0", "0 0.70", "0.985 0.92", false, 0f, "assets/icons/iconmaterial.mat", "", "72 0", "0 0");
                CUIClass.CreateText(ref ui, $"owkit_title_{i}", $"owkit_tp_{i}", KTWhite,
                    $"<color=@accent><b>{kitname.ToUpper()}</b></color>", 12, "0 0", "1 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                CUIClass.CreatePanel(ref ui, $"owkit_dp_{i}", $"owkit_card_{i}", "0 0 0 0", "0 0.44", "0.985 0.68", false, 0f, "assets/icons/iconmaterial.mat", "", "72 0", "0 0");
                CUIClass.CreateText(ref ui, $"owkit_descr_{i}", $"owkit_dp_{i}", KTWhite,
                    $"<size=9><color=@muted>{descr}</color></size>", 9, "0 0", "1 1", TextAnchor.UpperLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");

                CUIClass.CreatePanel(ref ui, $"owkit_mp_{i}", $"owkit_card_{i}", "0 0 0 0", "0 0.26", "0.985 0.42", false, 0f, "assets/icons/iconmaterial.mat", "", "72 0", "0 0");
                CUIClass.CreateText(ref ui, $"owkit_meta_{i}", $"owkit_mp_{i}", KTWhite,
                    $"<size=9><color=@muted>КД:</color> <color=@accent>{cdText}</color>   <color=@muted>Лимит:</color> <color=@accent>{usesText}</color></size>",
                    9, "0 0", "1 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                // нижний ряд кнопок: ОБЗОР — под иконкой, клейм — на оставшуюся ширину
                CUIClass.CreateButton(ref ui, $"owkit_view_{i}", $"owkit_card_{i}", KGlassHov, "<color=@accent>ОБЗОР</color>", 10, "0.035 0.05", "0.31 0.22",
                    $"octahub_kit_view {kitname.Replace(" ", "%")} {page}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

                string[] btn = KitsTab_BtnState(player, kitname).Split('%');
                CUIClass.CreateButton(ref ui, $"owkit_claim_{i}", $"owkit_card_{i}", btn[1], $"<b>{btn[0]}</b>", 10, "0.33 0.05", "0.985 0.22",
                    $"octahub_kit_claim {kitname.Replace(" ", "%")} {page} 0", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }

            // пагинация — правый нижний угол, эталон с текстовых вкладок;
            // счётчик страниц живёт в футере (единый стиль панели)
            int totalPages = (int)Math.Ceiling(kits.Count / 9.0);
            if (totalPages > 1)
            {
                if (page > 0)
                    CUIClass.CreateButton(ref ui, "owkit_prev", "oh_cnt", "accent.glass", "<color=@accent>‹</color>", 16, "0.885 0.006", "0.93 0.054",
                        $"octahub_kit_page {page - 1}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                if (page + 1 < totalPages)
                    CUIClass.CreateButton(ref ui, "owkit_next", "oh_cnt", "accent.glass", "<color=@accent>›</color>", 16, "0.935 0.006", "0.985 0.054",
                        $"octahub_kit_page {page + 1}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }

            KitsTab_Destroy(player);
            CuiHelper.AddUi(player, ui);

            // футер показывает номер страницы наборов — обновляем заодно
            UiState ks;
            if (_ui.TryGetValue(player.userID, out ks)) { ks.KitPage = page; UiRefresh(player, UiBot); }
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
                    if (string.IsNullOrEmpty(shortname) || ItemManager.FindItemDefinition(shortname) == null) continue;

                    ulong skin = 0;
                    try { skin = item.SelectToken("SkinID")?.Value<ulong>() ?? 0UL; } catch { }
                    int amount = 0;
                    try { amount = item.SelectToken("Amount")?.Value<int>() ?? 0; } catch { }

                    // ItemIcon фреймворка: иконка предмета + подпись xN в углу
                    CUIClass.CreateItemIcon(ref ui, $"{prefix}{i}_img", $"{prefix}{i}", shortname, skin, amount, "0.10 0.10", "0.90 0.90", 0.2f);
                }
                catch { }
            }
        }

        private void KitsTab_ViewKit(BasePlayer player, string kitname, int page)
        {
            var ui = new CuiElementContainer();

            JObject kit = null;
            try { kit = OctaKits?.Call("GetKitObject", kitname) as JObject; } catch { }
            if (kit == null) { KitsTab_Show(player, page); return; }

            var belt = kit.SelectToken("BeltItems");
            var main = kit.SelectToken("MainItems");
            var wear = kit.SelectToken("WearItems");

            // правая часть — содержимое набора
            CUIClass.CreatePanel(ref ui, "owkit_view_inv", "oh_cnt", KGlassBg, "0.37 0.04", "0.99 0.96", false, 0f, "assets/icons/iconmaterial.mat");
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
            CUIClass.CreatePanel(ref ui, "owkit_view_info", "oh_cnt", KGlassBg, "0.01 0.04", "0.35 0.96", false, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreatePanel(ref ui, "owkit_v_gold", "owkit_view_info", KGold, "0 0.985", "1 1", false, 0f, "assets/icons/iconmaterial.mat");
            CUIClass.CreateText(ref ui, "owkit_v_title", "owkit_view_info", "accent", $"◆ <b>{kitname.ToUpper()}</b>", 18, "0.06 0.91", "0.94 0.97", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_v_div1", "owkit_view_info", KGoldDark, "0.06 0.895", "0.94 0.898", false, 0f, "assets/icons/iconmaterial.mat");

            int cooldownSec = 0, maxUses = 0, used = 0;
            try { cooldownSec = Convert.ToInt32(OctaKits.Call("GetKitCooldown", kitname)); } catch { }
            try { maxUses = Convert.ToInt32(OctaKits.Call("GetKitMaxUses", kitname)); } catch { }
            try { used = Convert.ToInt32(OctaKits.Call("GetPlayerKitUses", (ulong)player.userID, kitname)); } catch { }

            string cdText = cooldownSec > 0 ? KitFmtTime(cooldownSec) : "НЕТ";
            string usesText = maxUses > 0 ? Math.Max(0, maxUses - used).ToString() : "БЕЗ ЛИМИТА";
            int cost = kit.SelectToken("Cost")?.Value<int>() ?? 0;
            string curLbl = "$";
            try { curLbl = OctaKits.Call("GetCurrencyLabel") as string ?? "$"; } catch { }
            string costText = cost > 0 ? $"{cost} {curLbl}" : "БЕСПЛАТНО";
            string perm = kit.SelectToken("RequiredPermission")?.ToString() ?? "";
            bool hasPerm = string.IsNullOrEmpty(perm);
            if (!hasPerm)
            {
                try { hasPerm = Convert.ToBoolean(OctaKits.Call("HasKitPermission", player, perm)); } catch { }
            }
            string permStatus = hasPerm
                ? "<color=@green>Разрешён</color>" : "<color=@red>Требуется привилегия</color>";

            string info = $"<color=@muted>• Кулдаун:</color>  <color=@accent>{cdText}</color>\n" +
                          $"<color=@muted>• Доступно:</color>  <color=@accent>{usesText}</color>\n" +
                          $"<color=@muted>• Стоимость:</color> <color=@accent>{costText}</color>\n" +
                          $"<color=@muted>• Статус:</color>    {permStatus}";
            CUIClass.CreateText(ref ui, "owkit_v_info", "owkit_view_info", KTWhite, info, 12, "0.06 0.65", "0.94 0.88", TextAnchor.UpperLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_v_div2", "owkit_view_info", KGoldDark, "0.06 0.635", "0.94 0.638", false, 0f, "assets/icons/iconmaterial.mat");

            string descr = "";
            try { descr = OctaKits.Call("GetKitDescription", kitname) as string ?? ""; } catch { }
            if (string.IsNullOrEmpty(descr)) descr = "Описание набора не указано.";
            CUIClass.CreateText(ref ui, "owkit_v_descr", "owkit_view_info", "muted", descr, 11, "0.06 0.20", "0.94 0.62", TextAnchor.UpperLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");

            CUIClass.CreateButton(ref ui, "owkit_v_back", "owkit_view_info", KGlassRow, "<color=@accent>← НАЗАД</color>", 11, "0.06 0.06", "0.45 0.16",
                $"octahub_kit_page {page}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            string[] btn = KitsTab_BtnState(player, kitname).Split('%');
            CUIClass.CreateButton(ref ui, "owkit_v_claim", "owkit_view_info", btn[1], $"<b>{btn[0]}</b>", 11, "0.49 0.06", "0.94 0.16",
                $"octahub_kit_claim {kitname.Replace(" ", "%")} {page} 1", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

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

        [ConsoleCommand("octahub_kit_page")]
        private void octahub_kit_page(ConsoleSystem.Arg arg)
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

        [ConsoleCommand("octahub_kit_view")]
        private void octahub_kit_view(ConsoleSystem.Arg arg)
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

        [ConsoleCommand("octahub_kit_claim")]
        private void octahub_kit_claim(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || OctaKits == null) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 3) return;

            string kitname = args[0].Replace("%", " ");
            int page = 0;
            int.TryParse(args[1], out page);
            bool inView = args[2] == "1";

            object ok = null;
            try { ok = OctaKits.Call("TryClaimKit", player, kitname, false); } catch { }
            if (ok is bool && (bool)ok)
            {
                // перерисовываем — кнопка уходит в кулдаун/лимит
                if (inView) KitsTab_ViewKit(player, kitname, page);
                else KitsTab_Show(player, page);
            }
        }

        #endregion

#region [Boss pages — генерация из OctaBoss.json]

        // вкладка «БОССЫ»: стр.2..10 — по виду из конфига OctaBoss.
        // Заполняем только пустые страницы (ручные правки админа не трогаем),
        // принудительная регенерация — octahub_bosses_refresh.
        private const int BossTabIndex = 10;

        // Вкладка «VIP»: стр.2..5 — по уровню OctVIP (Bronze/Silver/Gold/Brilliant).
        // Сидим только в пустые страницы — правки админа в PageData.json сохраняются.
        private const int VipTabIndex = 5;

        private void SeedVipPages()
        {
            if (pageData == null) return;
            var tab = $"Tab{VipTabIndex}";
            if (!pageData.ContainsKey(tab)) pageData[tab] = new PageData();
            var d = pageData[tab];
            if (d.page2 != null && d.page2.Count > 0) return;

            d.page2 = new List<string>
            {
                "[H]<color=#CD7F32>VIP BRONZE</color>",
                "[h]первый уровень привилегий — базовый набор бонусов",
                "<size=18><color=#E8B84B>ЧТО ТЫ ПОЛУЧАЕШЬ</color></size>",
                "",
                "<size=16><color=#CD7F32>◆</color> Иконка статуса <color=#CD7F32>BRONZE</color> перед ником в чате</size>",
                "<size=16><color=#CD7F32>◆</color> Приветствие при входе — статус виден сразу</size>",
                "<size=16><color=#CD7F32>◆</color> VIP-наборы уровня BRONZE — вкладка НАБОРЫ</size>",
                "<size=16><color=#CD7F32>◆</color> Приоритет в очереди при заходе на сервер</size>",
                "",
                "[F]следующий уровень → SILVER · оформление — в ДИСКОРД или у администрации"
            };
            d.page3 = new List<string>
            {
                "[H]<color=#C0C0C0>VIP SILVER</color>",
                "[h]всё из BRONZE — и больше инфы при входе",
                "<size=18><color=#E8B84B>ЧТО ТЫ ПОЛУЧАЕШЬ</color></size>",
                "",
                "<size=16><color=#C0C0C0>◆</color> Всё из статуса BRONZE</size>",
                "<size=16><color=#C0C0C0>◆</color> Серебряная иконка <color=#C0C0C0>SILVER</color> в чате</size>",
                "<size=16><color=#C0C0C0>◆</color> При входе видишь свой клан и сколько соратников онлайн</size>",
                "<size=16><color=#C0C0C0>◆</color> Расширенные наборы уровня SILVER</size>",
                "",
                "[F]следующий уровень → GOLD · оформление — в ДИСКОРД"
            };
            d.page4 = new List<string>
            {
                "[H]<color=#FFD700>VIP GOLD</color>",
                "[h]всё из SILVER — плюс заметность и контроль базы",
                "<size=18><color=#E8B84B>ЧТО ТЫ ПОЛУЧАЕШЬ</color></size>",
                "",
                "<size=16><color=#FFD700>◆</color> Всё из статуса SILVER</size>",
                "<size=16><color=#FFD700>◆</color> Золотая иконка <color=#FFD700>GOLD</color> — заметна в любом чате</size>",
                "<size=16><color=#FFD700>◆</color> При входе видишь количество своих шкафов на карте</size>",
                "<size=16><color=#FFD700>◆</color> Твой заход на сервер анонсируется в общий чат</size>",
                "<size=16><color=#FFD700>◆</color> Наборы уровня GOLD</size>",
                "",
                "[F]максимум → BRILLIANT · оформление — в ДИСКОРД"
            };
            d.page5 = new List<string>
            {
                "[H]<color=#4FC3F7>VIP BRILLIANT</color>",
                "[h]максимальный игровой статус — вершина линейки",
                "<size=18><color=#E8B84B>ЧТО ТЫ ПОЛУЧАЕШЬ</color></size>",
                "",
                "<size=16><color=#4FC3F7>◆</color> Всё из статуса GOLD</size>",
                "<size=16><color=#4FC3F7>◆</color> Уникальная бриллиантовая иконка <color=#4FC3F7>BRILLIANT</color></size>",
                "<size=16><color=#4FC3F7>◆</color> Максимальный приоритет в очереди</size>",
                "<size=16><color=#4FC3F7>◆</color> Топовые наборы уровня BRILLIANT</size>",
                "<size=16><color=#4FC3F7>◆</color> Высший игровой статус — дальше только персонал</size>",
                "",
                "[F]оформление — в ДИСКОРД или у администрации"
            };
            SavePageData();
            Puts($"Страницы VIP-уровней сгенерированы → Tab{VipTabIndex} (page2..5).");
        }

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

        [ConsoleCommand("octahub_bosses_refresh")]
        private void octahub_bosses_refresh(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            PopulateBossPages(force: true);
        }

        #endregion

#region [CUI Framework]

        // Вся отрисовка идёт через фреймворк OctaUI — класс оставлен как тонкий шим
        // со старыми сигнатурами, чтобы тело плагина не переписывать.
        public class CUIClass
        {
            internal static Plugin Ui;

            private static string AlignStr(TextAnchor a) => a.ToString().ToLowerInvariant();
            private static string Off(string oMin, string oMax) =>
                string.IsNullOrEmpty(oMin) && string.IsNullOrEmpty(oMax) ? null : (oMin + " " + oMax).Trim();

            public static CuiElementContainer CreateOverlay(string _name, string _color, string _anchorMin, string _anchorMax, bool _cursorOn = false, float _fade = 0f, string _mat = "assets/content/ui/uibackgroundblur.mat")
            {
                var c = new CuiElementContainer();
                Ui?.Call("Panel", c, "Overlay", _name, new JObject
                {
                    ["plugin"] = "OctaHub",
                    ["rect"] = _anchorMin + " " + _anchorMax, ["color"] = _color,
                    ["cursor"] = _cursorOn, ["fadeIn"] = _fade, ["mat"] = _mat
                });
                return c;
            }

            public static void CreatePanel(ref CuiElementContainer _container, string _name, string _parent, string _color, string _anchorMin, string _anchorMax, bool _cursorOn = false, float _fade = 0f, string _mat2 = "assets/content/ui/uibackgroundblur.mat", string _sprite = "", string _OffsetMin = "", string _OffsetMax = "")
            {
                Ui?.Call("Panel", _container, _parent, _name, new JObject
                {
                    ["plugin"] = "OctaHub",
                    ["rect"] = _anchorMin + " " + _anchorMax, ["offset"] = Off(_OffsetMin, _OffsetMax),
                    ["color"] = _color, ["cursor"] = _cursorOn, ["fadeIn"] = _fade, ["mat"] = _mat2
                });
            }

            public static void CreateImage(ref CuiElementContainer _container, string _parent, string _image, string _anchorMin, string _anchorMax, float _fade = 0f)
            {
                Ui?.Call("Image", _container, _parent, null, new JObject
                {
                    ["plugin"] = "OctaHub",
                    ["rect"] = _anchorMin + " " + _anchorMax,
                    ["src"] = _image, ["fadeIn"] = _fade
                });
            }

            // ItemIcon: иконка предмета + опциональная подпись xN (заменяет пару RawImage+Text)
            public static void CreateItemIcon(ref CuiElementContainer _container, string _name, string _parent, string _shortname, ulong _skin = 0UL, int _amount = 0, string _anchorMin = "0.10 0.10", string _anchorMax = "0.90 0.90", float _fadeIn = 0.2f)
            {
                Ui?.Call("ItemIcon", _container, _parent, _name, new JObject
                {
                    ["plugin"] = "OctaHub",
                    ["rect"] = _anchorMin + " " + _anchorMax, ["item"] = _shortname,
                    ["skin"] = _skin, ["amount"] = _amount, ["fadeIn"] = _fadeIn
                });
            }

            public static void CreateText(ref CuiElementContainer _container, string _name, string _parent, string _color, string _text, int _size, string _anchorMin, string _anchorMax, TextAnchor _align = TextAnchor.MiddleCenter, string _font = "robotocondensed-bold.ttf", string _outlineColor = "", string _outlineScale = "")
            {
                Ui?.Call("Text", _container, _parent, _name, new JObject
                {
                    ["plugin"] = "OctaHub",
                    ["rect"] = _anchorMin + " " + _anchorMax, ["color"] = _color,
                    ["text"] = _text, ["size"] = _size, ["align"] = AlignStr(_align),
                    ["font"] = _font, ["outline"] = _outlineColor, ["outlineScale"] = _outlineScale
                });
            }

            public static void CreateButton(ref CuiElementContainer _container, string _name, string _parent, string _color, string _text, int _size, string _anchorMin, string _anchorMax, string _command = "", string _close = "", string _textColor = "", float _fade = 1f, TextAnchor _align = TextAnchor.MiddleCenter, string _font = "", string _mat = "assets/content/ui/uibackgroundblur.mat")
            {
                Ui?.Call("Button", _container, _parent, _name, new JObject
                {
                    ["plugin"] = "OctaHub",
                    ["rect"] = _anchorMin + " " + _anchorMax, ["color"] = _color,
                    ["text"] = _text, ["size"] = _size, ["align"] = AlignStr(_align),
                    ["font"] = _font, ["cmd"] = _command, ["close"] = _close,
                    ["textColor"] = _textColor, ["fadeIn"] = _fade, ["mat"] = _mat
                });
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

        #endregion

#region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["_noPerm"] = "<color=#E8B84B>[OctaHub]</color> У вас нет разрешения на это.",
                ["_dataWiped"] = "<color=#E8B84B>[OctaHub]</color> Layout игроков очищен. Перезагрузите плагин.",
                ["_typeKdrshow"] = "<color=#E8B84B>[OctaHub]</color> Наберите /kdrshow, чтобы открыть панель KDR.",
                ["_typeKdrhide"] = "<color=#E8B84B>[OctaHub]</color> Наберите /kdrhide, чтобы закрыть панель KDR.",
                ["_hidden"] = "<color=#E8B84B>[OctaHub]</color> Скрыто.",
                ["_displayed"] = "<color=#E8B84B>[OctaHub]</color> Показано.",

            }, this);
        }

        private string GetLang(string _message) => lang.GetMessage(_message, this);

        #endregion

#region [Config]

        private Configuration config;
        protected override void LoadConfig()
        {
            MigrateLegacyData();
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
            public TabSettings tab1Settings { get; set; }

            public class TabSettings
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
            public TabSettings tab2Settings { get; set; }

            
            [JsonProperty(PropertyName = "Tab #3 Settings")]
            public TabSettings tab3Settings { get; set; }

            
            [JsonProperty(PropertyName = "Tab #4 Settings")]
            public TabSettings tab4Settings { get; set; }

            
            [JsonProperty(PropertyName = "Tab #5 Settings")]
            public TabSettings tab5Settings { get; set; }

            
            [JsonProperty(PropertyName = "Tab #6 Settings")]
            public TabSettings tab6Settings { get; set; }

            
            [JsonProperty(PropertyName = "Tab #7 Settings")]
            public TabSettings tab7Settings { get; set; }

            
            [JsonProperty(PropertyName = "Tab #8 Settings")]
            public TabSettings tab8Settings { get; set; }

            
            [JsonProperty(PropertyName = "Tab #9 Settings")]
            public TabSettings tab9Settings { get; set; }

            
            [JsonProperty(PropertyName = "Tab #10 Settings")]
            public TabSettings tab10Settings { get; set; }

            [JsonProperty(PropertyName = "Tab #11 Settings")]
            public TabSettings tab11Settings { get; set; }

            
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

                [JsonProperty("Tab 11")]
                public string tab11 { get; set; }
            }


            public static Configuration CreateConfig()
            {
                return new Configuration
                {

                    generalSettings = new OctaHub.Configuration.GeneralSettings
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
                                "bosses",
                                "commands"
                            }

                    },

                    buttonSettings = new OctaHub.Configuration.ButtonSettings
                    {

                        btnFontStyle = "robotocondensed-bold.ttf",
                        closeBtnText = "✕",
                        mainAlign = TextAnchor.MiddleCenter,
                        btnIconAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }

                        //"0.13 0.33",
                        //"0.20 0.65"
                    },

                    titleSettings = new OctaHub.Configuration.TitleSettings
                    {
                        titleText = "<size=55><color=#E8B84B>OCTAVIAN</color></size>",
                        titleColor = "1 1 1 1",
                        titleFontStyle = "robotocondensed-bold.ttf",
                        titleOutlineColor = "0 0 0 1",
                        titleOutlineThick = "1.5",

                    },

                    tab1Settings = new OctaHub.Configuration.TabSettings
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
                                "[H]ДОБРО ПОЖАЛОВАТЬ НА <color=#E8B84B>OCTAVIAN</color>",
                                "[h]рейты x5 · вайпы карт еженедельно · EU · карта 4200 · онлайн {online}/{maxplayers}",
                                "<size=18><color=#E8B84B>ПОЧЕМУ У НАС ИНТЕРЕСНО</color></size>",
                                "<size=15><color=#E8B84B>◆ БОССЫ-ЗВЕРИ</color> — наш флагман: гигантские звери с AI-управлением.</size>",
                                "<size=15>   Маркер на карте покажет логово — награда вносит больше урона.</size>",
                                "<size=15><color=#E8B84B>◆ AI-АНТИЧИТ</color> — система анализа поведения отлавливает читеров.</size>",
                                "<size=15><color=#E8B84B>◆ СВОИ ПЛАГИНЫ</color> — наборы, телепорты, апгрейд баз, VIP — под баланс сервера.</size>",
                                "",
                                "<size=18><color=#E8B84B>ЧТО ГДЕ ЛЕЖИТ</color></size>",
                                "<size=14><color=#83b8c7>ПРАВИЛА</color> — законы острова · <color=#83b8c7>НАБОРЫ</color> — киты · <color=#83b8c7>ВАЙПЫ</color> — расписание · <color=#83b8c7>VIP</color> — привилегии</size>",
                                "<size=14><color=#83b8c7>КОМАНДЫ</color> — команды чата · <color=#83b8c7>БОССЫ</color> — кто гуляет по острову · <color=#83b8c7>ДИСКОРД</color> — связь и новости</size>",
                                "[F]первые шаги: /kit — набор · /sethome — точка дома · /tpr ник — телепорт к другу"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }

                    },

                    tab2Settings = new OctaHub.Configuration.TabSettings
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

                    tab3Settings = new OctaHub.Configuration.TabSettings
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

                    tab4Settings = new OctaHub.Configuration.TabSettings
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

                    tab5Settings = new OctaHub.Configuration.TabSettings
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
                                    "[H]VIP СТАТУСЫ",
                                    "[h]4 уровня привилегий — у каждого своя страница, листай стрелками",
                                    "<size=20><color=#CD7F32>BRONZE</color> → <color=#C0C0C0>SILVER</color> → <color=#FFD700>GOLD</color> → <color=#4FC3F7>BRILLIANT</color></size>",
                                    "",
                                    "<size=16><color=#83b8c7>◆</color> Иконка статуса в чате и приветствие при входе</size>",
                                    "<size=16><color=#83b8c7>◆</color> Приоритет в очереди и VIP-наборы на свой уровень</size>",
                                    "<size=16><color=#83b8c7>◆</color> Чем выше статус — тем больше полезного видишь при заходе</size>",
                                    "",
                                    "<size=14>Стрелки внизу справа — каждый статус на своей странице.</size>",
                                    "[F]оформление — в ДИСКОРД или у администрации"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    tab6Settings = new OctaHub.Configuration.TabSettings
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

                    tab7Settings = new OctaHub.Configuration.TabSettings
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

                    tab8Settings = new OctaHub.Configuration.TabSettings
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

                    tab9Settings = new OctaHub.Configuration.TabSettings
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

                    tab10Settings = new OctaHub.Configuration.TabSettings
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

                    tab11Settings = new OctaHub.Configuration.TabSettings
                    {
                        btnEnabled = true,
                        btnTitle = "<size=22>КОМАНДЫ</size>",
                        btnIcon = "you can change icons in data/cuiData.json",
                        mainFontSize = 15,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-regular.ttf",
                        mainAlign = TextAnchor.MiddleCenter,
                        textLines = new List<string>
                                {
                                    "[H]КОМАНДЫ СЕРВЕРА",
                                    "[h]всё, что можно писать в чат — и что это делает",
                                    "<size=17><color=#E8B84B>ОБЩЕЕ</color></size>",
                                    "<size=14>/octahub — эта панель · /kit — наборы · /vip — мой статус · /gather — рейты · /dm — лента убийств</size>",
                                    "",
                                    "<size=17><color=#E8B84B>ТЕЛЕПОРТ</color></size>",
                                    "<size=14>/tpr ник — запрос к игроку · /tpa — принять · /home имя — домой</size>",
                                    "<size=14>/sethome имя — сохранить дом · /listhomes — список · /removehome имя — удалить</size>",
                                    "",
                                    "<size=17><color=#E8B84B>СТРОЙКА</color></size>",
                                    "<size=14>/up /down — блок перед лицом · /upall /downall — вся база · /remove — вернуть свой объект</size>",
                                    "",
                                    "<size=17><color=#E8B84B>БОССЫ</color></size>",
                                    "<size=14>/bosstimer — расписание · /bossinfo — кто жив · /bosslast — киллеры</size>",
                                    "[F]шорткаты вкладок: /info /rules /kits /wipe /vipinfo /discord /shop /admins /news /bosses"
                                },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string>
                                {
                                "0 0",
                                "1 1"
                                }
                    },

                    extension = new OctaHub.Configuration.Extension
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
                        tab11 = "null",
                    },


                };
            }
        }

        #endregion

    }
}
