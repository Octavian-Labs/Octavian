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
    // OctaHub 2.0 — игровой портал: динамические разделы вместо фиксированных вкладок.
    // Разделы: Главная / Наборы / События / Профиль / Сервисы / Инфо / Настройки.
    // Данные — живые, из провайдеров: OctaKits (киты), OctaBoss (события),
    // OctVIP (статус), OctaTP (дома), OctaFeed (настройки ленты), OctaWC (вайп).
    // Обратная совместимость: octahub_open <tab> <page>, кастомные чат-команды,
    // PageData.json, пермишены octawelcome.tabN (инфо-страницы).
    [Info("OctaHub", "Octavian", "2.2.0")]
    [Description("Игровой портал Octavian: разделы, живые данные провайдеров, OctaUI")]
    public class OctaHub : RustPlugin
    {

        #region [Dependencies]

        [PluginReference] Plugin ImageLibrary, OctaUI, OctaKits, OctaBoss, OctVIP, OctaTP, OctaFeed, OctaWC, OctaRel;
        [PluginReference] Plugin Economics, ServerRewards, WipeCountdown;

        private void DependencyWarnings()
        {
            timer.Once(1f, () =>
            {
                if (OctaUI == null)
                    Puts("(!) Фреймворк OctaUI не найден — интерфейс не будет отрисовываться (c.reload OctaUI).");
                if (ImageLibrary == null)
                    Puts("(!) ImageLibrary не найден — картинки будут грузиться медленнее.");
                if (OctaKits == null) Puts("(!) OctaKits не найден — раздел «Наборы» покажет заглушку.");
                if (OctaBoss == null) Puts("(!) OctaBoss не найден — раздел «События» покажет заглушку.");
                if (OctaWC == null) Puts("(!) OctaWC не найден — виджет вайпа будет читать WipeCountdown (если есть).");
            });
        }

        #endregion

        #region [Sections — реестр разделов]

        // Разделы портала по строковым ид — порядок в рейле = порядок массива.
        // Старые номерные вкладки мапятся сюда: 1→home, 3→kits, 10→events,
        // остальные (2,4,5,6,7,8,9,11) → инфо-страницы внутри раздела info.
        private static readonly string[] SectionIds =
            { "home", "kits", "events", "profile", "services", "info", "settings" };

        // Старые вкладки, ставшие инфо-страницами (порядок карточек в разделе Инфо)
        private static readonly int[] InfoTabs = { 2, 4, 5, 6, 7, 8, 9, 11 };

        private Configuration.SectionCfg SectionCfg(string id)
        {
            var s = config?.sections;
            if (s == null) return new Configuration.SectionCfg();
            switch (id)
            {
                case "kits": return s.kits ?? new Configuration.SectionCfg();
                case "events": return s.events ?? new Configuration.SectionCfg();
                case "profile": return s.profile ?? new Configuration.SectionCfg();
                case "services": return s.services ?? new Configuration.SectionCfg();
                case "info": return s.info ?? new Configuration.SectionCfg();
                case "settings": return s.settings ?? new Configuration.SectionCfg();
                default: return s.home ?? new Configuration.SectionCfg();
            }
        }

        // Маппинг старого номера вкладки -> раздел (обратная совместимость команд)
        private static string SectionOfTab(int tab)
        {
            switch (tab)
            {
                case 0:                          // 0 в конфиге = «открывать на главной»
                case 1: return "home";
                case 3: return "kits";
                case 10: return "events";
                default: return "info";
            }
        }

        private static bool IsInfoTab(int tab)
        {
            foreach (var t in InfoTabs) if (t == tab) return true;
            return false;
        }

        private bool SectionAllowed(BasePlayer player, string id)
        {
            if (config?.generalSettings?.permsEnabled != true) return true;
            // инфо-страницы проверяются по старым пермишенам вкладок — см. CheckPerms
            return permission.UserHasPermission(player.UserIDString, $"octahub.{id}");
        }

        #endregion

        #region [Fields & State]

        // Именованные части интерфейса — частичная перерисовка, как в админке OctaBoss.
        private const string UiRoot = "oh_ui";
        private const string UiHdr  = "oh_hdr";
        private const string UiNav  = "oh_nav";
        private const string UiCnt  = "oh_cnt";
        private const string UiBot  = "oh_bot";
        private const string Mat    = "assets/icons/iconmaterial.mat";

        private class UiState
        {
            public string Section = "home";    // активный раздел
            public int InfoTab = 0;            // открытая инфо-страница (старый номер вкладки), 0 = список
            public string InfoFrom = "list";   // откуда пришли на инфо-страницу: list | home
            public int Page = 1;               // страница внутри инфо-страницы
            public int KitPage = 0;
            public string KitCat = "";         // фильтр наборов: "" | fav | free | paid | vip
            public string EvtKey = null;       // открытая карточка события (ключ босса или we#)
            public int EvtPage = 0;            // страница сетки событий
        }
        private readonly Dictionary<ulong, UiState> _ui = new Dictionary<ulong, UiState>();

        private UiState St(BasePlayer player)
        {
            UiState s;
            if (!_ui.TryGetValue(player.userID, out s))
                s = _ui[player.userID] = new UiState();
            return s;
        }

        // «Показано один раз за вайп» — персистится в data/OctaHub/Seen.json
        private List<ulong> _seen = new List<ulong>();

        // избранные наборы: uid -> имена китов (data/OctaHub/Fav.json)
        private Dictionary<ulong, List<string>> _fav = new Dictionary<ulong, List<string>>();

        // персональная настройка автооткрытия: uid -> bool (data/OctaHub/Prefs.json)
        private Dictionary<ulong, bool> _autoOpen = new Dictionary<ulong, bool>();

        // время игры на сервере: uid -> секунды (data/OctaHub/Playtime.json)
        private class PlayData { public long TotalSec; public long WipeSec; }
        private Dictionary<ulong, PlayData> _play = new Dictionary<ulong, PlayData>();

        // мировые события — одна карточка на КАТЕГОРИЮ (не на сущность):
        // туши техники лежат в serverEntities до деспавна, считаем только живых
        private class WorldEvtCat
        {
            public string Key, Title, Desc;
            public string Image = "";       // url иконки (те же, что у OctaPanel)
            public int Count;               // живых сущностей категории
            public Vector3 Pos;             // квадрат первой найденной
            public string Note;             // «респавн ожидается», «выключено в конфиге»
        }
        // Иконки — фирменные тайлы (Gemini-спрайт 4x3, нарезка в images/events/tiles),
        // хостятся в ассет-репозитории: raw.githubusercontent.com/Octavian-Labs/qk7f2m9x
        private const string EvtImg = "https://raw.githubusercontent.com/Octavian-Labs/qk7f2m9x/main/w4k/";
        private readonly List<WorldEvtCat> _worldCats = new List<WorldEvtCat>
        {
            new WorldEvtCat { Key = "bradley", Title = "БРЭДЛИ", Image = EvtImg + "bradley.png",
                Desc = "Боевой БТР патрулирует монумент: броня, пулемёт, ракеты. Добыча — элитные ящики и запчасти. Взрывчатка решает." },
            new WorldEvtCat { Key = "heli", Title = "ПАТРУЛЬНЫЙ ВЕРТОЛЁТ", Image = EvtImg + "heli.png",
                Desc = "Летающий кошмар с ракетами и пулемётом. Нападает на вооружённых. Сбитый — оставляет ящики с топ-лутом." },
            new WorldEvtCat { Key = "chinook", Title = "ЧИНУК", Image = EvtImg + "chinook.png",
                Desc = "Транспортник сбрасывает защищённый ящик на монумент. Догони точку высадки и дождись расшифровки." },
            new WorldEvtCat { Key = "cargo", Title = "ГРУЗОВОЕ СУДНО", Image = EvtImg + "cargo.png",
                Desc = "Корабль с охраной-учёными и тремя защищёнными ящиками. Подплывай на лодке — лестница с кормы." },
            new WorldEvtCat { Key = "crate", Title = "ЗАЩИЩЁННЫЙ ЯЩИК", Image = EvtImg + "crate.png",
                Desc = "Ящик с таймером расшифровки — процесс шумный и долгий. Лут уровня заправки или нефтевышки." },
            new WorldEvtCat { Key = "airdrop", Title = "ГРУЗОВОЙ САМОЛЁТ", Image = EvtImg + "airdrop.png",
                Desc = "Самолёт сбрасывает контейнер — по сигнальной гранате или по расписанию. Жди точку падения." },
            new WorldEvtCat { Key = "ghost", Title = "ПРИЗРАЧНЫЙ КОРАБЛЬ", Image = EvtImg + "ghost.png",
                Desc = "Судно-призрак из глубоководья — появляется в открытом море, на борту лут и опасность." },
            new WorldEvtCat { Key = "city", Title = "ПЛАВУЧИЙ ГОРОД", Image = EvtImg + "city.png",
                Desc = "Плавучий город в открытом море: торговля, терминалы, мини-игры. Туда — только на лодке." },
        };
        private string _worldSig = "";
        private Timer _scanTimer, _playTimer, _saveTimer;

        // параметры платного пинга врага — из OctaRel.GetPingInfo
        private int _pingRp, _pingCoins, _pingSec;
        private bool _pingRpOn, _pingCoinsOn, _pingTest;

        #endregion

        #region [Hooks]

        private void Loaded()
        {
            CUIClass.Ui = OctaUI;
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
            LoadFav();
            LoadPrefs();
            LoadPlay();
            DependencyWarnings();
            RegisterCommands();
            RegisterPerms();
            DownloadImages();

            // трекер мировых событий — для раздела События
            _scanTimer = timer.Every(20f, ScanWorldEvents);
            // счётчик времени на сервере — минута в минуту
            _playTimer = timer.Every(60f, PlayTick);
            _saveTimer = timer.Every(300f, SavePlay);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (!_play.ContainsKey(player.userID))
                _play[player.userID] = new PlayData();

            if (config?.generalSettings == null || !config.generalSettings.openOnConnect) return;

            // персональная настройка игрока важнее глобальной
            if (_autoOpen.TryGetValue(player.userID, out var pref) && !pref) return;

            if (config.generalSettings.openOnce)
            {
                if (_seen.Contains(player.userID)) return;
                _seen.Add(player.userID);
                SaveSeen();
            }

            timer.Once(1f, () =>
            {
                if (player != null && player.IsConnected)
                    UiOpen(player, SectionOfTab(config.generalSettings.openTab), 0, 1);
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
            foreach (var kv in _play) kv.Value.WipeSec = 0;
            SavePlay();
        }

        private void Unload()
        {
            _scanTimer?.Destroy();
            _playTimer?.Destroy();
            _saveTimer?.Destroy();
            SavePlay();
            foreach (var p in BasePlayer.activePlayerList)
            {
                UiClose(p);
                KitsTab_Destroy(p);
            }
            _ui.Clear();
        }

        // провайдеры, от которых зависят разделы — перерисовываем только на них
        private static readonly HashSet<string> ProviderNames = new HashSet<string>
        {
            "OctaKits", "OctaBoss", "OctVIP", "OctaTP", "OctaFeed", "OctaWC", "WipeCountdown"
        };

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin?.Name == "OctaUI")
                CUIClass.Ui = plugin;
            if (plugin != null && ProviderNames.Contains(plugin.Name))
                RedrawOpen();
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin?.Name == "OctaUI")
                CUIClass.Ui = null;
            if (plugin != null && ProviderNames.Contains(plugin.Name))
                RedrawOpen();
        }

        private void RedrawOpen()
        {
            foreach (var p in BasePlayer.activePlayerList)
                if (_ui.ContainsKey(p.userID))
                    UiRefresh(p, UiCnt);
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

        private void SaveSeen() => Interface.Oxide.DataFileSystem.WriteObject($"{Name}/Seen", _seen);

        private void LoadFav()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile($"{Name}/Fav"))
                _fav = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, List<string>>>($"{Name}/Fav")
                       ?? new Dictionary<ulong, List<string>>();
        }

        private void SaveFav() => Interface.Oxide.DataFileSystem.WriteObject($"{Name}/Fav", _fav);

        private void LoadPrefs()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile($"{Name}/Prefs"))
                _autoOpen = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, bool>>($"{Name}/Prefs")
                            ?? new Dictionary<ulong, bool>();
        }

        private void SavePrefs() => Interface.Oxide.DataFileSystem.WriteObject($"{Name}/Prefs", _autoOpen);

        private void LoadPlay()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile($"{Name}/Playtime"))
                _play = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayData>>($"{Name}/Playtime")
                        ?? new Dictionary<ulong, PlayData>();
        }

        private void SavePlay() => Interface.Oxide.DataFileSystem.WriteObject($"{Name}/Playtime", _play);

        #endregion

        #region [Image Handling]

        private void DownloadImages()
        {
            if (ImageLibrary == null) return;
            for (int i = 1; i <= 11; i++)
            {
                var url = TabCfg(i).tabImageUrl;
                if (url != null && (url.StartsWith("http") || url.StartsWith("www")))
                    ImageLibrary.Call("AddImage", url, $"tab{i}bg");
            }
            foreach (var id in SectionIds)
            {
                var url = SectionCfg(id).icon;
                if (url != null && (url.StartsWith("http") || url.StartsWith("www")))
                    ImageLibrary.Call("AddImage", url, url);
            }
            // иконки категорий событий — фирменные тайлы с ассет-репозитория
            foreach (var c in _worldCats)
                if (c.Image.StartsWith("http"))
                    ImageLibrary.Call("AddImage", c.Image, c.Image);
            ImageLibrary.Call("AddImage", EvtImg + "bosses.png", EvtImg + "bosses.png");
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
        {
            if (ImageLibrary != null)
            {
                if (!(bool)ImageLibrary.Call("HasImage", url))
                    return url;
                else
                    return (string)ImageLibrary?.Call("GetImage", url);
            }
            else return url;
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
                UiOpen(player, SectionOfTab(tab), tab, pageNumber);
            }
            return null;
        }

        [ChatCommand("octahub")]
        private void CmdOctaHub(BasePlayer player)
        {
            if (player == null) return;
            if (_ui.ContainsKey(player.userID)) UiClose(player);
            else UiOpen(player, SectionOfTab(config?.generalSettings?.openTab ?? 1), 0, 1);
        }

        // Алиас на старую привычную команду — открывает панель
        [ChatCommand("octawelcome")]
        private void CmdOctaWelcome(BasePlayer player) => CmdOctaHub(player);

        // Обратная совместимость: старые кнопки/бинды octahub_open <tab> <page>.
        // tab — старый номер вкладки; мапится на раздел (1→home, 3→kits, 10→events, остальные→info).
        [ConsoleCommand("octahub_open")]
        private void CmdOpen(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 1) return;

            int tab;
            if (!int.TryParse(args[0], out tab)) return;
            int page = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 1;

            string sec = SectionOfTab(tab);
            if (!_ui.ContainsKey(player.userID)) { UiOpen(player, sec, tab, page); return; }

            var st = St(player);
            st.Section = sec;
            st.EvtKey = null;
            st.Page = page;
            if (sec == "info") st.InfoTab = tab;
            UiRefresh(player, UiHdr, UiNav, UiCnt, UiBot);
        }

        // Прямой переход в раздел по строковому ид (кнопки рейла, виджеты главной)
        [ConsoleCommand("octahub_sec")]
        private void CmdSection(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 1) return;

            string sec = args[0];
            if (Array.IndexOf(SectionIds, sec) < 0) return;
            if (!SectionAllowed(player, sec)) return;

            if (!_ui.ContainsKey(player.userID)) { UiOpen(player, sec, 0, 1); return; }

            var st = St(player);
            st.Section = sec;
            st.EvtKey = null;
            if (sec == "info") st.InfoTab = 0; // список страниц
            st.Page = 1;
            UiRefresh(player, UiHdr, UiNav, UiCnt, UiBot);
        }

        // Открыть инфо-страницу из списка раздела Инфо
        [ConsoleCommand("octahub_infotab")]
        private void CmdInfoTab(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !_ui.ContainsKey(player.userID)) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 1) return;

            var st = St(player);
            if (args[0] == "back")
            {
                // возврат на предыдущий уровень: из списка или с главной
                if (st.InfoFrom == "home") { st.Section = "home"; st.InfoTab = 0; st.Page = 1; }
                else { st.InfoTab = 0; st.Page = 1; }
                UiRefresh(player, UiHdr, UiNav, UiCnt, UiBot);
                return;
            }
            int tab;
            if (!int.TryParse(args[0], out tab) || !IsInfoTab(tab)) return;
            st.Section = "info";
            st.InfoTab = tab;
            st.InfoFrom = args.Length > 2 ? args[2] : "list";
            st.Page = args.Length > 1 && int.TryParse(args[1], out var pg) ? pg : 1;
            UiRefresh(player, UiHdr, UiNav, UiCnt, UiBot);
        }

        [ConsoleCommand("octahub_close")]
        private void CmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            UiClose(player);
        }

        // Клик по сервисной строке → выполняет чат-команду от имени игрока
        [ConsoleCommand("octahub_svc")]
        private void CmdSvc(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 1) return;
            int i;
            if (!int.TryParse(args[0], out i)) return;
            var rows = config?.services?.rows;
            if (rows == null || i < 0 || i >= rows.Count) return;
            var chat = rows[i].chatCommand;
            if (string.IsNullOrEmpty(chat)) return;

            // спец-префикс sec: — переход в раздел панели вместо чат-команды
            if (chat.StartsWith("sec:", StringComparison.OrdinalIgnoreCase))
            {
                var st = St(player);
                var sec = chat.Substring(4).Trim();
                if (Array.IndexOf(SectionIds, sec) >= 0 && SectionAllowed(player, sec))
                {
                    st.Section = sec; st.EvtKey = null;
                    if (sec == "info") st.InfoTab = 0;
                    st.Page = 1;
                    UiRefresh(player, UiHdr, UiNav, UiCnt, UiBot);
                }
                return;
            }
            player.SendConsoleCommand("chat.say", "/" + chat.TrimStart('/'));
        }

        // Переключатели раздела Настройки
        [ConsoleCommand("octahub_set")]
        private void CmdSet(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !_ui.ContainsKey(player.userID)) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 1) return;

            switch (args[0])
            {
                case "autoopen":
                    bool cur = !_autoOpen.TryGetValue(player.userID, out var v) || v;
                    _autoOpen[player.userID] = !cur;
                    SavePrefs();
                    break;
                case "feedmsg":
                    FeedSet(player, "hideMessages", !FeedGet(player, "hideMessages"));
                    break;
                case "feedname":
                    FeedSet(player, "hideMyName", !FeedGet(player, "hideMyName"));
                    break;
                case "relnotify":
                    if (OctaRel != null)
                        RelNotifySet(player, !RelNotifyGet(player));
                    break;
            }
            UiRefresh(player, UiCnt);
        }

        // ── Провайдер-шимы: v1-конверт {ok,code,data} с фолбэком на legacy ──
        // (Docs/OctaAPI-v1.md). res==null означает «метод не найден» (старый
        // провайдер) → зовём старый API; res!=null с ok=false — честный ответ.

        private bool FeedGet(BasePlayer player, string key)
        {
            try
            {
                var res = OctaFeed?.Call("Feed_GetSettings", (ulong)player.userID) as JObject;
                var o = res?["data"] as JObject;
                if (o != null) return o[key]?.Value<bool>() ?? false;
                if (res == null)
                {
                    var old = OctaFeed?.Call("API_GetSettings", (ulong)player.userID) as JObject;
                    return old?[key]?.Value<bool>() ?? false;
                }
            }
            catch { }
            return false;
        }

        private void FeedSet(BasePlayer player, string key, bool value)
        {
            try
            {
                var res = OctaFeed?.Call("Feed_SetSetting", (ulong)player.userID, key, value) as JObject;
                if (res != null) return;
            }
            catch { }
            try { OctaFeed?.Call("API_SetSetting", (ulong)player.userID, key, value); } catch { }
        }

        private bool RelNotifyGet(BasePlayer player)
        {
            try
            {
                var res = OctaRel?.Call("Rel_GetNotify", (ulong)player.userID) as JObject;
                var v = res?["data"]?["value"];
                if (v != null) return v.Value<bool>();
                if (res == null)
                    return Convert.ToBoolean(OctaRel.Call("GetNotify", (ulong)player.userID));
            }
            catch { }
            return false;
        }

        private void RelNotifySet(BasePlayer player, bool value)
        {
            try
            {
                var res = OctaRel?.Call("Rel_SetNotify", (ulong)player.userID, value) as JObject;
                if (res != null) return;
            }
            catch { }
            try { OctaRel?.Call("SetNotify", (ulong)player.userID, value); } catch { }
        }

        // Фильтр категории наборов
        [ConsoleCommand("octahub_kit_cat")]
        private void CmdKitCat(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !_ui.ContainsKey(player.userID)) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            var st = St(player);
            st.KitCat = args.Length > 0 ? args[0] : "";
            st.KitPage = 0;
            UiRefresh(player, UiCnt, UiBot);
        }

        // Избранный набор: звезда на карточке
        [ConsoleCommand("octahub_kit_fav")]
        private void CmdKitFav(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !_ui.ContainsKey(player.userID)) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 1) return;

            string kitname = args[0].Replace("%", " ");
            List<string> fav;
            if (!_fav.TryGetValue(player.userID, out fav))
                fav = _fav[player.userID] = new List<string>();
            if (fav.Contains(kitname)) fav.Remove(kitname); else fav.Add(kitname);
            SaveFav();
            UiRefresh(player, UiCnt);
        }

        // Карточка события: детали босса
        [ConsoleCommand("octahub_evt")]
        private void CmdEvt(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !_ui.ContainsKey(player.userID)) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            var st = St(player);
            st.EvtKey = args.Length > 0 && args[0] != "back" ? args[0] : null;
            st.EvtPage = 0;
            UiRefresh(player, UiCnt);
        }

        [ConsoleCommand("octahub_evt_page")]
        private void CmdEvtPage(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !_ui.ContainsKey(player.userID)) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            if (args.Length < 1) return;
            int page;
            if (int.TryParse(args[0], out page))
            {
                St(player).EvtPage = Math.Max(0, page);
                UiRefresh(player, UiCnt);
            }
        }

        // Управление списками OctaRel из профиля
        [ConsoleCommand("octahub_rel")]
        private void CmdRel(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || OctaRel == null || !_ui.ContainsKey(player.userID)) return;
            var args = arg.Args != null ? arg.Args.Select(x => x.ToString()).ToArray() : new string[0];
            ulong target;
            // octahub_rel ping rp|coins <uid> — платный ESP-пинг врага (логика и списание в OctaRel)
            if (args.Length >= 4 && args[0] == "ping")
            {
                if (!ulong.TryParse(args[3], out target)) return;
                string err = null;
                try
                {
                    var res = OctaRel.Call("Rel_ScanEnemy", player, target, args[1]) as JObject;
                    if (res != null)
                        err = res["ok"]?.Value<bool>() == true ? null
                              : (res["msg"]?.ToString() ?? "Ошибка пинга.");
                    else
                        err = OctaRel.Call("ScanEnemy", player, target, args[1]) as string;
                }
                catch { err = "сервис недоступен"; }
                if (!string.IsNullOrEmpty(err))
                    player.ChatMessage($"<color=#cd412b>◆ ПИНГ:</color> {err}");
                return;
            }
            if (args.Length < 3 || args[0] != "del") return;
            if (!ulong.TryParse(args[2], out target)) return;
            try
            {
                var res = OctaRel.Call("Rel_Remove", (ulong)player.userID, args[1], target) as JObject;
                if (res == null)
                    OctaRel.Call("RemoveRelation", (ulong)player.userID, args[1], target);
            }
            catch { }
            UiRefresh(player, UiCnt);
        }

        [ConsoleCommand("octahub_refresh")]
        private void CmdRefresh(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !_ui.ContainsKey(player.userID)) return;
            UiRefresh(player, UiCnt);
        }

        #endregion

        #region [Functions & Methods]

        private void RegisterPerms()
        {
            if (config?.generalSettings?.permsEnabled != true) return;
            foreach (var id in SectionIds)
                permission.RegisterPermission($"octahub.{id}", this);
            for (int i = 1; i <= 11; i++)
                permission.RegisterPermission($"octawelcome.tab{i}", this); // инфо-страницы, старые выдачи работают
        }

        private bool CheckPerms(BasePlayer player, int tabNumber)
        {
            if (config?.generalSettings?.permsEnabled != true) return true;
            return permission.UserHasPermission(player.UserIDString, $"octawelcome.tab{tabNumber}");
        }

        private void WritePageEntry()
        {
            for (int i = 1; i <= 11; i++)
            {
                var key = $"Tab{i}";
                if (!pageData.ContainsKey(key))
                    pageData.Add(key, new PageData());
            }
            SavePageData();
        }

        private Configuration.TabSettings TabCfg(int n)
        {
            var t = TabCfgRaw(n);
            if (t == null)
            {
                t = new Configuration.TabSettings();
                TabCfgSet(n, t);
            }
            return t;
        }

        private Configuration.TabSettings TabCfgRaw(int n)
        {
            switch (n)
            {
                case 2: return config?.tab2Settings;
                case 3: return config?.tab3Settings;
                case 4: return config?.tab4Settings;
                case 5: return config?.tab5Settings;
                case 6: return config?.tab6Settings;
                case 7: return config?.tab7Settings;
                case 8: return config?.tab8Settings;
                case 9: return config?.tab9Settings;
                case 10: return config?.tab10Settings;
                case 11: return config?.tab11Settings;
                default: return config?.tab1Settings;
            }
        }

        private void TabCfgSet(int n, Configuration.TabSettings t)
        {
            switch (n)
            {
                case 2: config.tab2Settings = t; break;
                case 3: config.tab3Settings = t; break;
                case 4: config.tab4Settings = t; break;
                case 5: config.tab5Settings = t; break;
                case 6: config.tab6Settings = t; break;
                case 7: config.tab7Settings = t; break;
                case 8: config.tab8Settings = t; break;
                case 9: config.tab9Settings = t; break;
                case 10: config.tab10Settings = t; break;
                case 11: config.tab11Settings = t; break;
                default: config.tab1Settings = t; break;
            }
        }

        // Подстановка токенов в текст: {wipecountdown} идёт через OctaWC,
        // со старым WipeCountdown — запасной путь.
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
            if (text.Contains("{wipecountdown}"))
                text = text.Replace("{wipecountdown}", WipeText());
            if (Economics != null && text.Contains("{economics}"))
                text = text.Replace("{economics}", $"{Economics.Call<double>("Balance", player.UserIDString)}");
            if (ServerRewards != null && text.Contains("{rp}"))
                text = text.Replace("{rp}", $"{ServerRewards?.Call<int>("CheckPoints", (ulong)player.userID)}");
            return text;
        }

        private string WipeText()
        {
            try
            {
                if (OctaWC != null)
                {
                    var res = OctaWC.Call("WC_GetInfo") as JObject;
                    var fmt = res?["data"]?["formatted"]?.ToString();
                    if (fmt != null) return fmt;
                    return OctaWC.Call("GetCountdownFormated_API") as string ?? "—";
                }
                if (WipeCountdown != null)
                    return Convert.ToString(WipeCountdown.CallHook("GetCountdownFormated_API"));
            }
            catch { }
            return "—";
        }

        bool IsDigitsOnly(string str)
        {
            foreach (char c in str)
                if (c < '0' || c > '9') return false;
            return true;
        }

        // Строки дополнительной страницы инфо-страницы (page2..page10 из PageData.json)
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

        private static readonly Regex RxSize = new Regex("</?size[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static string StripSize(string s) => string.IsNullOrEmpty(s) ? s : RxSize.Replace(s, "");

        private static string GridOf(Vector3 pos)
        {
            try { return MapHelper.PositionToString(pos); }
            catch { return $"{pos.x:0}, {pos.z:0}"; }
        }

        private static string FmtLeft(long seconds)
        {
            if (seconds <= 0) return "скоро";
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}д {ts.Hours}ч";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}ч {ts.Minutes}м";
            return $"{(int)ts.TotalMinutes}м";
        }

        // ── Счётчик времени на сервере ───────────────────────────────────────

        private void PlayTick()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                PlayData d;
                if (!_play.TryGetValue(p.userID, out d))
                    d = _play[p.userID] = new PlayData();
                d.TotalSec += 60;
                d.WipeSec += 60;
            }
        }

        private static string FmtHours(long seconds)
        {
            var h = seconds / 3600;
            var m = (seconds % 3600) / 60;
            return h > 0 ? $"{h} ч {m} мин" : $"{m} мин";
        }

        // ── Трекер мировых событий ───────────────────────────────────────────

        private WorldEvtCat Cat(string key)
        {
            for (int i = 0; i < _worldCats.Count; i++)
                if (_worldCats[i].Key == key) return _worldCats[i];
            return null;
        }

        // ghostship/floatingcity — internal-классы, ловим по префиксу префаба (как OctaPanel)
        private static bool MatchesPrefab(BaseNetworkable e, string prefix)
        {
            var spn = e?.ShortPrefabName;
            return spn != null && spn.StartsWith(prefix, StringComparison.Ordinal)
                && !spn.Contains("mapmarker");
        }

        // Один проход по serverEntities — счёт живых сущностей по категориям.
        // Подпись сравниваем, чтобы перерисовывать раздел только при изменениях.
        private void ScanWorldEvents()
        {
            foreach (var c in _worldCats) { c.Count = 0; c.Note = null; }

            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent == null || ent.IsDestroyed) continue;
                var ce = ent as BaseCombatEntity;
                if (ce != null && ce.IsDead()) continue; // туша техники — не событие

                WorldEvtCat c = null;
                if (ent is BradleyAPC) c = Cat("bradley");
                else if (ent is PatrolHelicopter) c = Cat("heli");
                else if (ent is CH47Helicopter) c = Cat("chinook");
                else if (ent is CargoShip) c = Cat("cargo");
                else if (ent is CargoPlane) c = Cat("airdrop");
                else if (ent is HackableLockedCrate hc)
                {
                    // ящик интересен, когда приземлился и ещё не вскрыт
                    if (hc.hasLanded && !hc.hasBeenOpened) c = Cat("crate");
                }
                else if (MatchesPrefab(ent, "ghostship")) c = Cat("ghost");
                else if (MatchesPrefab(ent, "floatingcity")) c = Cat("city");

                if (c == null) continue;
                if (c.Count == 0) c.Pos = ent.transform.position;
                c.Count++;
            }

            // живые подсказки из спавнеров/флагов
            var bs = BradleySpawner.singleton;
            if (bs != null && bs.spawned == null && bs.pendingRespawn)
                Cat("bradley").Note = "респавн ожидается";
            if (!CargoShip.event_enabled)
                Cat("cargo").Note = "выключено в конфиге";

            // сигнатура: категория + количество + квадрат
            var sig = string.Join("|", _worldCats.Select(c =>
                c.Count > 0 ? $"{c.Key}x{c.Count}@{GridOf(c.Pos)}" : c.Key + ":" + (c.Note ?? "")));
            if (sig == _worldSig) return;
            _worldSig = sig;
            foreach (var p in BasePlayer.activePlayerList)
            {
                UiState s;
                if (_ui.TryGetValue(p.userID, out s) && s.Section == "events")
                    UiRefresh(p, UiCnt);
            }
        }

        #endregion

        #region [UI — каркас в стиле админки OctaBoss]

        private void UiOpen(BasePlayer player, string section, int infoTab, int page)
        {
            if (CUIClass.Ui == null) return;
            UiClose(player);
            _ui[player.userID] = new UiState
            {
                Section = section,
                InfoTab = section == "info" ? infoTab : 0,
                Page = page
            };

            var c = CUIClass.CreateOverlay(UiRoot, "bg", "0.17 0.08", "0.83 0.92", true, 0f,
                "assets/content/ui/uibackgroundblur.mat");
            CUIClass.CreatePanel(ref c, "oh_edge_t", UiRoot, "accent.dark", "0 0.995", "1 1", false, 0f, Mat);
            CUIClass.CreatePanel(ref c, "oh_edge_b", UiRoot, "accent.dark", "0 0", "1 0.005", false, 0f, Mat);
            CuiHelper.AddUi(player, c);

            UiRefresh(player, UiHdr, UiNav, UiCnt, UiBot);
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

        // Частичная перерисовка: сносим названные части и добавляем заново одним батчом
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
            var sec = SectionCfg(st.Section);

            CUIClass.CreatePanel(ref ui, UiHdr, UiRoot, "panel", "0 0.918", "1 0.995", false, 0f, Mat);
            CUIClass.CreatePanel(ref ui, "oh_hdr_line", UiHdr, "accent.dark", "0 0", "1 0.045", false, 0f, Mat);

            CUIClass.CreatePanel(ref ui, "oh_hdr_chip", UiHdr, "accent.dark", "0.011 0.14", "0.056 0.90", false, 0f, Mat);
            CUIClass.CreateText(ref ui, "oh_hdr_chip_t", "oh_hdr_chip", "accent", "◆", 16, "0 0", "1 1",
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // титул сервера + имя активного раздела
            string title = StripSize(R(player, config?.titleSettings?.titleText ?? "OCTAVIAN"));
            string secTitle = StripSize(sec?.title ?? "");
            CUIClass.CreateText(ref ui, "oh_hdr_title", UiHdr, "text",
                $"<b>{title}</b>  <color=@muted>—</color>  <color=@accent>{secTitle}</color>",
                15, "0.062 0", "0.72 1", TextAnchor.MiddleLeft, config?.titleSettings?.titleFontStyle ?? "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            CUIClass.CreateText(ref ui, "oh_hdr_meta", UiHdr, "muted",
                $"online {BasePlayer.activePlayerList.Count()}/{ConVar.Server.maxplayers}", 11,
                "0.72 0", "0.945 1", TextAnchor.MiddleRight, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            string closeTxt = StripSize(config?.buttonSettings?.closeBtnText ?? "✕");
            CUIClass.CreateButton(ref ui, "oh_hdr_close", UiHdr, "red", $"<b>{closeTxt}</b>", 16,
                "0.955 0.14", "0.992 0.90", "octahub_close", UiRoot, "white", 0f, TextAnchor.MiddleCenter,
                config?.buttonSettings?.btnFontStyle ?? "robotocondensed-bold.ttf");
        }

        private void DrawNav(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);
            CUIClass.CreatePanel(ref ui, UiNav, UiRoot, "panel", "0.008 0.014", "0.178 0.908", false, 0f, Mat);

            var enabled = SectionIds.Where(id => SectionCfg(id).enabled).ToList();
            if (enabled.Count == 0) return;
            float pitch = 0.965f / Math.Max(enabled.Count, 7), h = pitch * 0.89f;
            const float top = 0.985f;

            for (int pos = 0; pos < enabled.Count; pos++)
            {
                string id = enabled[pos];
                var sc = SectionCfg(id);

                float y1 = top - (pos + 1) * pitch, y2 = y1 + h;
                string aMin = $"0.03 {y1.ToString("0.####", CultureInfo.InvariantCulture)}";
                string aMax = $"0.97 {y2.ToString("0.####", CultureInfo.InvariantCulture)}";

                bool active = id == st.Section;
                bool allowed = SectionAllowed(player, id);
                string bg = active ? "accent.glass" : "row";
                string label = StripSize(sc.title ?? id.ToUpper());
                string labelCol = !allowed ? "muted" : (active ? "accent" : "text");
                string labelTxt = active ? $"<b>{label}</b>" : label;

                CUIClass.CreateButton(ref ui, $"oh_nav_{id}", UiNav, bg, "", 12, aMin, aMax,
                    $"octahub_sec {id}", "", "white", 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

                if (active)
                    CUIClass.CreatePanel(ref ui, $"oh_nav_e_{id}", $"oh_nav_{id}", "accent", "0 0.08", "0.02 0.92", false, 0f, Mat);

                bool hasIcon = !string.IsNullOrEmpty(sc.icon) && sc.icon.StartsWith("http");
                if (hasIcon)
                    CUIClass.CreateImage(ref ui, $"oh_nav_{id}", Img3(sc.icon), "0.07 0.24", "0.24 0.78", 0.15f);

                CUIClass.CreateText(ref ui, $"oh_nav_t_{id}", $"oh_nav_{id}", labelCol, labelTxt, 12,
                    hasIcon ? "0.27 0" : "0.08 0", "0.98 1", TextAnchor.MiddleLeft,
                    config?.buttonSettings?.btnFontStyle ?? "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                if (!allowed)
                    CUIClass.CreateText(ref ui, $"oh_nav_l_{id}", $"oh_nav_{id}", "muted", "🔒", 10,
                        "0.86 0", "0.98 1", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            }
        }

        // Диспетчер разделов: контент-панель + рендерер раздела
        private void DrawContent(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);
            CUIClass.CreatePanel(ref ui, UiCnt, UiRoot, "panel", "0.186 0.075", "0.992 0.908", false, 0f, Mat);

            if (!SectionAllowed(player, st.Section))
            {
                CUIClass.CreateText(ref ui, "oh_cnt_text", UiCnt, "text",
                    "<size=44>✘</size>\n<color=@muted>Нет доступа к этой странице.</color>", 18,
                    "0.02 0.02", "0.98 0.98", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                return;
            }

            switch (st.Section)
            {
                case "kits": DrawKitsSection(ref ui, player); break;
                case "events": DrawEventsSection(ref ui, player); break;
                case "profile": DrawProfileSection(ref ui, player); break;
                case "services": DrawServicesSection(ref ui, player); break;
                case "info":
                    if (st.InfoTab == 0) DrawInfoList(ref ui, player);
                    else DrawInfoPage(ref ui, player, st.InfoTab, st.Page);
                    break;
                case "settings": DrawSettingsSection(ref ui, player); break;
                default: DrawHomeSection(ref ui, player); break;
            }
        }

        private void DrawFooter(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);
            CUIClass.CreatePanel(ref ui, UiBot, UiRoot, "panel", "0.186 0.014", "0.992 0.065", false, 0f, Mat);

            CUIClass.CreateText(ref ui, "oh_bot_hint", UiBot, "muted",
                "<color=@accent>OCTAVIAN</color> · закрыть: ✕ или /octahub", 10,
                "0.012 0", "0.5 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            int cur = 1, max = 1;
            if (st.Section == "kits")
            {
                cur = st.KitPage + 1;
                max = (int)Math.Ceiling(KitsTab_List(player, st.KitCat).Count / 9.0);
            }
            else if (st.Section == "info" && st.InfoTab > 0)
            {
                cur = st.Page; max = MaxPage(st.InfoTab);
            }
            else if (st.Section == "home")
            {
                cur = st.Page; max = MaxPage(1);
            }

            if (max > 1)
                CUIClass.CreateText(ref ui, "oh_bot_pg", UiBot, "muted",
                    $"стр. <color=@accent>{cur}</color>/{max}", 11,
                    "0.5 0", "0.988 1", TextAnchor.MiddleRight, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
        }

        #endregion

        #region [Section: Главная]

        // Главная: живые виджеты сверху + приветственный текст (tab1 + PageData-страницы).
        private void DrawHomeSection(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);

            if (st.Page == 1)
            {
                // ── компактная полоса виджетов сверху ───────────────────────
                var ev = EventCardText();
                var (kitsOk, kitsAll) = KitsCounts(player);
                DrawHomeCard(ref ui, "0.015 0.865-0.325 0.985".Split('-'), "accent", "explosive.timed",
                    "ДО ВАЙПА", WipeText(), "", "octahub_infotab 4 1 home");
                DrawHomeCard(ref ui, "0.345 0.865-0.655 0.985".Split('-'), ev.Spec, "rocket.launcher",
                    "СОБЫТИЕ", ev.Value, ev.Sub, "octahub_sec events");
                DrawHomeCard(ref ui, "0.675 0.865-0.985 0.985".Split('-'), "green", "supply.signal",
                    "НАБОРЫ", $"{kitsOk} из {kitsAll}", "открыть", "octahub_sec kits");

                // ── приветствие + статистика ────────────────────────────────
                CUIClass.CreateText(ref ui, "oh_cnt_hello", UiCnt, "text",
                    $"<b>Добро пожаловать, <color=@accent>{player.displayName}</color>!</b>", 14,
                    "0.02 0.785", "0.985 0.85", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreateText(ref ui, "oh_cnt_stats", UiCnt, "muted",
                    HomeStatsText(player), 11,
                    "0.02 0.745", "0.985 0.785", TextAnchor.MiddleLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");

                // ── шок-витрина: баннер + тизеры (выключается пустым списком) ─
                if (config?.homePromos != null && config.homePromos.Count > 0)
                    DrawHomePromo(ref ui, ev);
            }

            // ── текст: первая страница — hero из конфига, 2+ — PageData ─────
            var tab = TabCfg(1);
            var lines = st.Page >= 2 ? PageLines(1, st.Page) : tab.textLines;
            var ls = lines ?? new List<string>();

            string headTitle = null, headSub = null;
            var body = new List<string>(); var foot = new List<string>();
            foreach (var l in ls)
            {
                if (l.StartsWith("[H]")) headTitle = l.Substring(3);
                else if (l.StartsWith("[h]")) headSub = l.Substring(3);
                else if (l.StartsWith("[F]")) foot.Add(l.Substring(3));
                else body.Add(l);
            }

            bool promo = st.Page == 1 && config?.homePromos != null && config.homePromos.Count > 0;
            if (!promo)
            {
                if (headTitle != null)
                    CUIClass.CreateText(ref ui, "oh_cnt_title", UiCnt, "text", StripSize(R(player, headTitle)), 22,
                        "0.035 0.655", "0.965 0.725", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                if (headSub != null)
                    CUIClass.CreateText(ref ui, "oh_cnt_sub", UiCnt, "muted", StripSize(R(player, headSub)), 12,
                        "0.035 0.615", "0.965 0.655", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreatePanel(ref ui, "oh_cnt_rule", UiCnt, "accent.dark", "0.035 0.605", "0.965 0.61", false, 0f, Mat);
            }

            CUIClass.CreateText(ref ui, "oh_cnt_text", UiCnt, tab.mainFontColor, R(player, string.Join("\n", body)),
                tab.mainFontSize, "0.035 0.06", promo ? "0.965 0.37" : "0.965 0.60", tab.mainAlign,
                tab.mainFontStyle, tab.fontOutlineColor, $"{tab.fontOutlineThick} {tab.fontOutlineThick}");

            if (foot.Count > 0)
                CUIClass.CreateText(ref ui, "oh_cnt_foot", UiCnt, "muted", R(player, string.Join("\n", foot)), 11,
                    "0.035 0.01", promo ? "0.875 0.055" : "0.875 0.09", TextAnchor.MiddleLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");

            DrawPageArrows(ref ui, 1, st.Page);
        }

        // Персональная строка: время на сервере / за вайп / валюта
        private string HomeStatsText(BasePlayer player)
        {
            PlayData pd;
            _play.TryGetValue(player.userID, out pd);
            string s = $"на сервере <color=@accent>{FmtHours(pd?.TotalSec ?? 0)}</color>" +
                       $" · за вайп <color=@accent>{FmtHours(pd?.WipeSec ?? 0)}</color>";
            try
            {
                if (ServerRewards != null)
                    s += $" · RP <color=@accent>{ServerRewards.Call<int>("CheckPoints", (ulong)player.userID)}</color>";
                else if (Economics != null)
                    s += $" · баланс <color=@accent>{Economics.Call<double>("Balance", player.UserIDString):0}</color>";
            }
            catch { }
            return s;
        }

        // Стильная карточка главной: стекло + акцентная полоса + иконка предмета
        // + подпись + крупное значение + подстрока. Вся карточка — кнопка.
        // spec — токен цвета OctaUI ("accent" | "red" | "green" | "blue").
        private void DrawHomeCard(ref CuiElementContainer ui, string[] a, string spec,
            string itemIcon, string label, string value, string sub, string cmd)
        {
            int i = label.GetHashCode() & 0xffff;
            string n = $"oh_c_{i}";
            CUIClass.CreateButton(ref ui, n, UiCnt, "panel", "", 10, a[0], a[1], cmd, "", "white", 0f,
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            CUIClass.CreatePanel(ref ui, n + "_bar", n, spec, "0 0", "0.012 1", false, 0f, Mat);
            CUIClass.CreateItemIcon(ref ui, n + "_ic", n, itemIcon, 0UL, 0, "0.05 0.15", "0.17 0.85", 0.15f);
            CUIClass.CreateText(ref ui, n + "_t", n, "muted", label, 10, "0.21 0.60", "0.97 0.92",
                TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            var val = string.IsNullOrEmpty(sub) ? $"<b>{value}</b>"
                                              : $"<b>{value}</b> <size=9>· {sub}</size>";
            CUIClass.CreateText(ref ui, n + "_v", n, spec, val, 16, "0.21 0.08", "0.97 0.58",
                TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
        }

        // Шок-витрина главной: «горящий» баннер (живое событие > слоган из конфига)
        // + ряд тизеров-зазывалок в разделы меню.
        private void DrawHomePromo(ref CuiElementContainer ui, (string Spec, string Value, string Sub) ev)
        {
            // ── баннер ─────────────────────────────────────────────────────
            string bt, bs = "", bSpec = "accent", bCmd = "octahub_sec events";
            if (ev.Spec == "red")
            {
                bt = $"{ev.Value} — ЖИВ ПРЯМО СЕЙЧАС"; bs = "успей первым — жми, откроются события"; bSpec = "red";
            }
            else if (ev.Spec == "accent")
            {
                bt = $"{ev.Value} — {ev.Sub}"; bs = "успей подготовиться — жми, откроются события";
            }
            else
            {
                var sl = config?.homeSlogans;
                bt = (sl != null && sl.Count > 0)
                    ? sl[UnityEngine.Random.Range(0, sl.Count)]
                    : "Кликай по разделам слева — тут всё самое интересное";
                bCmd = "octahub_sec info";
            }
            CUIClass.CreateButton(ref ui, "oh_pr_b", UiCnt, "row", "", 10, "0.035 0.625", "0.965 0.735",
                bCmd, "", "white", 0f);
            CUIClass.CreatePanel(ref ui, "oh_pr_bar", "oh_pr_b", bSpec, "0 0", "0.010 1", false, 0f, Mat);
            CUIClass.CreateText(ref ui, "oh_pr_bt", "oh_pr_b", bSpec, $"<b>{bt}</b>", 15,
                "0.03 0.52", "0.90 0.95", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            if (!string.IsNullOrEmpty(bs))
                CUIClass.CreateText(ref ui, "oh_pr_bs", "oh_pr_b", "muted", bs, 10,
                    "0.03 0.10", "0.90 0.50", TextAnchor.MiddleLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
            CUIClass.CreateText(ref ui, "oh_pr_arr", "oh_pr_b", bSpec, "»", 22,
                "0.92 0.1", "0.99 0.9", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // ── тизеры: сетка 2 ряда × до 4 колонок, компактная высота ───────
            var pr = config.homePromos.Where(p => p != null && !string.IsNullOrEmpty(p.title)).Take(8).ToList();
            int n = pr.Count;
            if (n == 0) return;
            int cols = Math.Min(4, n);
            const float x0 = 0.035f, x1 = 0.965f, yTop = 0.615f, gap = 0.015f, h = 0.105f;
            float w = (x1 - x0 - gap * (cols - 1)) / cols;
            for (int i = 0; i < n; i++)
            {
                var p = pr[i];
                int row = i / cols, col = i % cols;
                float ax = x0 + col * (w + gap);
                float ay1 = yTop - row * (h + gap);        // верх карточки
                float ay0 = ay1 - h;                        // низ карточки
                string sec = SectionIds.Contains(p.section) ? p.section : "info";
                string t = $"oh_pr_{i}";
                CUIClass.CreateButton(ref ui, t, UiCnt, "panel", "", 10,
                    $"{ax:0.###} {ay0:0.###}", $"{ax + w:0.###} {ay1:0.###}",
                    $"octahub_sec {sec}", "", "white", 0f);
                CUIClass.CreatePanel(ref ui, t + "_bar", t, p.spec, "0 0", "0.015 1", false, 0f, Mat);
                if (!string.IsNullOrEmpty(p.icon))
                    CUIClass.CreateItemIcon(ref ui, t + "_ic", t, p.icon, 0UL, 0, "0.04 0.18", "0.15 0.82", 0.15f);
                CUIClass.CreateText(ref ui, t + "_t", t, p.spec, $"<b>{p.title}</b>", 12,
                    "0.18 0.55", "0.97 0.95", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreateText(ref ui, t + "_s", t, "muted", p.sub ?? "", 10,
                    "0.18 0.05", "0.97 0.50", TextAnchor.MiddleLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
            }
        }

        // Данные карточки события: цвет по состоянию (красный = активно),
        // крупно — имя, мелко — статус/таймер.
        private (string Spec, string Value, string Sub) EventCardText()
        {
            // живое мировое событие важнее расписания боссов
            var act = _worldCats.FirstOrDefault(c => c.Count > 0);
            if (act != null) return ("red", act.Title, "АКТИВНО СЕЙЧАС");
            try
            {
                var arr = BossStates();
                if (arr != null && arr.Count > 0)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    JObject live = null; JObject next = null; long nextAt = long.MaxValue;
                    foreach (var o in arr.OfType<JObject>())
                    {
                        if (o["enabled"]?.Value<bool>() != true) continue;
                        if (o["alive"]?.Value<bool>() == true) { live = o; break; }
                        long ns = o["nextSpawnUnix"]?.Value<long>() ?? 0;
                        if (ns > 0 && ns < nextAt) { nextAt = ns; next = o; }
                    }
                    if (live != null) return ("red", Convert.ToString(live["name"]), "ЖИВ — на карте");
                    if (next != null) return ("accent", Convert.ToString(next["name"]), $"через {FmtLeft(nextAt - now)}");
                    return ("blue", "все повержены", "");
                }
            }
            catch { }
            return ("blue", "нет данных", OctaBoss == null ? "OctaBoss не найден" : "");
        }

        private (int ok, int total) KitsCounts(BasePlayer player)
        {
            try
            {
                var kits = KitsTab_List(player, "");
                int ok = 0;
                foreach (var k in kits)
                {
                    // не на кулдауне = доступен к получению (availability из каталога)
                    if ((k["avail"] as JObject)?["code"]?.ToString() != "COOLDOWN") ok++;
                }
                return (ok, kits.Count);
            }
            catch { return (0, 0); }
        }

        // Стрелки пагинации инфо-страниц — общие для Главной и Инфо.
        // Команда octahub_open <tab> <page> сама маршрутизирует в нужный раздел.
        private void DrawPageArrows(ref CuiElementContainer ui, int tab, int page)
        {
            string cmdPrev = $"octahub_open {tab} {page - 1}";
            string cmdNext = $"octahub_open {tab} {page + 1}";
            if (page > 1)
                CUIClass.CreateButton(ref ui, "oh_cnt_prev", UiCnt, "accent.glass", "<color=@accent>‹</color>", 16,
                    "0.885 0.015", "0.93 0.08", cmdPrev, "", "white", 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            if (CheckPage(tab, page + 1))
                CUIClass.CreateButton(ref ui, "oh_cnt_next", UiCnt, "accent.glass", "<color=@accent>›</color>", 16,
                    "0.935 0.015", "0.985 0.08", cmdNext, "", "white", 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
        }

        #endregion

        #region [Section: События — живые боссы через OctaBoss.GetBossStates]

        // Общая сетка карточек 3x2 для раздела События
        private static readonly string[] EvtGrid =
        {
            "0.015 0.545-0.325 0.895", "0.345 0.545-0.655 0.895", "0.675 0.545-0.985 0.895",
            "0.015 0.135-0.325 0.485", "0.345 0.135-0.655 0.485", "0.675 0.135-0.985 0.485",
        };

        private void DrawEventsSection(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);

            // вложенные виды: сетка боссов / деталь мирового / деталь босса
            if (st.EvtKey != null) { DrawEventDetail(ref ui, player, st.EvtKey); return; }

            // заголовок раздела + кнопка обновления
            CUIClass.CreateText(ref ui, "oh_evt_h", UiCnt, "text",
                "<b>◆ СОБЫТИЯ ОСТРОВА</b>  <color=@muted>— что сейчас живо на карте</color>", 15,
                "0.015 0.925", "0.90 0.99", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreateButton(ref ui, "oh_evt_rf", UiCnt, "accent.glass", "<color=@accent>⟳</color>", 14,
                "0.945 0.925", "0.99 0.99", "octahub_refresh", "", "white", 0f,
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            // карточки-категории: сначала провайдеры из реестра (порядок конфига),
            // дальше мировые категории в фиксированном порядке
            var provCats = ProviderCats(player);
            int cardCount = provCats.Count + _worldCats.Count;
            int totalPages = (int)Math.Ceiling(cardCount / 6.0);
            st.EvtPage = Math.Max(0, Math.Min(st.EvtPage, totalPages - 1));
            int start = st.EvtPage * 6;

            for (int idx = 0; idx < 6 && start + idx < cardCount; idx++)
            {
                int ci = start + idx;
                string[] a = EvtGrid[idx].Split('-');
                string card = $"oh_evt_{idx}";

                string name, status, img = "", cmd;
                bool active;
                if (ci < provCats.Count)
                {
                    var c = provCats[ci];
                    name = c["title"]?.ToString() ?? "СОБЫТИЕ";
                    img = c["image"]?.ToString() ?? "";
                    // тайл по соглашению: images/events/tiles/<id>.png в ассет-репо
                    if (string.IsNullOrEmpty(img))
                        img = EvtImg + (c["id"]?.ToString() ?? "event") + ".png";
                    active = c["active"]?.Value<bool>() ?? false;
                    status = CatStatusHtml(c);
                    var detail = c["detail"]?.ToString();
                    cmd = string.IsNullOrEmpty(detail)
                        ? $"octahub_evt pv:{c["_pv"]}:{c["id"]}"
                        : $"octahub_evt {detail}";
                }
                else
                {
                    var c = _worldCats[ci - provCats.Count];
                    name = c.Title;
                    img = c.Image;
                    active = c.Count > 0;
                    if (c.Count > 0)
                        status = $"<color=@accent>АКТИВНО{(c.Count > 1 ? " ×" + c.Count : "")}</color>" +
                                 $"  <color=@muted>[{GridOf(c.Pos)}]</color>";
                    else if (!string.IsNullOrEmpty(c.Note))
                        status = $"<color=@muted>{c.Note}</color>";
                    else
                        status = "<color=@muted>нет на карте</color>";
                    cmd = $"octahub_evt we:{c.Key}";
                }

                CUIClass.CreatePanel(ref ui, card, UiCnt, "bg", a[0], a[1], false, 0f, Mat);
                CUIClass.CreatePanel(ref ui, card + "_g", card, active ? "accent" : "accent.dark",
                    "0 0.97", "1 1", false, 0f, Mat);
                // квадратный якорь: карточка ≈ 1.2:1 по пикселям — 30% ширины ≈ 36% высоты;
                // http → Img3-хендл, иначе считаем уже PNG-хендлом ImageLibrary
                if (!string.IsNullOrEmpty(img))
                    CUIClass.CreateImage(ref ui, card,
                        img.StartsWith("http") ? Img3(img) : img, "0.05 0.32", "0.35 0.68", 0.15f);

                CUIClass.CreateText(ref ui, card + "_n", card, "text", $"<b>{name}</b>", 14,
                    "0.38 0.74", "0.97 0.95", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreateText(ref ui, card + "_s", card, "muted", status, 11,
                    "0.38 0.52", "0.97 0.76", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreateButton(ref ui, card + "_b", card, "hover", "<color=@accent>ПОДРОБНЕЕ</color>", 10,
                    "0.38 0.10", "0.97 0.28", cmd, "", "white", 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }

            // пагинация категорий
            if (totalPages > 1)
            {
                if (st.EvtPage > 0)
                    CUIClass.CreateButton(ref ui, "oh_evt_prev", UiCnt, "accent.glass", "<color=@accent>‹</color>", 16,
                        "0.885 0.015", "0.93 0.08", $"octahub_evt_page {st.EvtPage - 1}", "", "white", 0f,
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                if (st.EvtPage + 1 < totalPages)
                    CUIClass.CreateButton(ref ui, "oh_evt_next", UiCnt, "accent.glass", "<color=@accent>›</color>", 16,
                        "0.935 0.015", "0.985 0.08", $"octahub_evt_page {st.EvtPage + 1}", "", "white", 0f,
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }
        }

        // Сводный статус карточки «БОССЫ»: живые / ближайший по таймеру / выключены
        private string BossAggStatus(out bool active, out string img)
        {
            active = false; img = "";
            JArray states = BossStates();
            if (states == null || states.Count == 0)
                return OctaBoss == null ? "<color=@muted>OctaBoss не загружен</color>"
                                        : "<color=@muted>нет видов боссов</color>";

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int aliveN = 0;
            long nextAt = long.MaxValue; string nextName = null;
            bool anyEnabled = false;
            foreach (var o in states.OfType<JObject>())
            {
                if (string.IsNullOrEmpty(img))
                    img = o["image"]?.ToString() ?? "";
                if (o["enabled"]?.Value<bool>() != true) continue;
                anyEnabled = true;
                if (o["alive"]?.Value<bool>() == true) { aliveN++; continue; }
                long ns = o["nextSpawnUnix"]?.Value<long>() ?? 0;
                if (ns > 0 && ns < nextAt) { nextAt = ns; nextName = o["name"]?.ToString(); }
            }

            if (aliveN > 0) { active = true; return $"<color=@red>ЖИВЫХ СЕЙЧАС: {aliveN}</color>"; }
            if (nextName != null)
                return $"<color=@muted>ближайший:</color> <color=@accent>{nextName} · {FmtLeft(nextAt - now)}</color>";
            return anyEnabled ? "<color=@muted>ожидают расписания</color>" : "<color=@muted>все выключены</color>";
        }

        // Сетка боссов внутри карточки «БОССЫ» — живые карточки из GetBossStates
        private void DrawBossGrid(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);

            CUIClass.CreatePanel(ref ui, "oh_evd", UiCnt, "row", "0.015 0.90", "0.985 0.985", false, 0f, Mat);
            CUIClass.CreateText(ref ui, "oh_evd_s", "oh_evd", "text",
                "<b>БОССЫ ОСТРОВА</b>  <color=@muted>— статусы живут по расписанию OctaBoss</color>", 12,
                "0.02 0", "0.85 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreateButton(ref ui, "oh_evd_b", "oh_evd", "accent.glass", "<color=@accent>← НАЗАД</color>", 10,
                "0.86 0.15", "0.99 0.85", "octahub_evt back", "", "white", 0f,
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            JArray states = BossStates();
            var list = states?.OfType<JObject>()
                .OrderByDescending(o => o["alive"]?.Value<bool>() == true)
                .ThenBy(o => o["nextSpawnUnix"]?.Value<long>() ?? long.MaxValue)
                .ToList() ?? new List<JObject>();

            if (list.Count == 0)
            {
                CUIClass.CreateText(ref ui, "oh_evd_t", UiCnt, "muted",
                    OctaBoss == null ? "OctaBoss не загружен." : "Видов боссов нет — настрой их в OctaBoss.json.", 14,
                    "0 0", "1 0.85", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int totalPages = (int)Math.Ceiling(list.Count / 6.0);
            st.EvtPage = Math.Max(0, Math.Min(st.EvtPage, totalPages - 1));
            int start = st.EvtPage * 6;

            for (int idx = 0; idx < 6 && start + idx < list.Count; idx++)
            {
                var o = list[start + idx];
                string[] a = EvtGrid[idx].Split('-');
                string key = o["key"]?.ToString() ?? $"b{idx}";
                string name = o["name"]?.ToString() ?? key.ToUpper();
                bool enabled = o["enabled"]?.Value<bool>() ?? true;
                bool alive = o["alive"]?.Value<bool>() ?? false;
                string card = $"oh_boss_{idx}";

                CUIClass.CreatePanel(ref ui, card, UiCnt, "bg", a[0], a[1], false, 0f, Mat);
                CUIClass.CreatePanel(ref ui, card + "_g", card, alive ? "red" : "accent.dark",
                    "0 0.97", "1 1", false, 0f, Mat);

                string img = o["image"]?.ToString() ?? "";
                // GetBossStates отдаёт уже PNG-хендл ImageLibrary (не URL) — передаём как есть;
                // http-вариант (если провайдер отдаёт ссылку) прогоняем через Img3
                if (!string.IsNullOrEmpty(img))
                    CUIClass.CreateImage(ref ui, card, img.StartsWith("http") ? Img3(img) : img,
                        "0.05 0.32", "0.35 0.68", 0.15f);

                string status;
                if (!enabled)
                    status = "<color=@muted>ВЫКЛЮЧЕН</color>";
                else if (alive)
                {
                    string g = o["grid"]?.ToString();
                    status = $"<color=@red>ЖИВ · {o["hp"]}/{o["hpMax"]} HP</color>" +
                             (g != null ? $"  <color=@muted>[{g}]</color>" : "");
                }
                else
                {
                    long ns = o["nextSpawnUnix"]?.Value<long>() ?? 0;
                    status = ns > 0 ? $"<color=@muted>спавн через</color> <color=@accent>{FmtLeft(ns - now)}</color>"
                                    : "<color=@muted>ожидает расписания</color>";
                }

                CUIClass.CreateText(ref ui, card + "_n", card, "text", $"<b>{name}</b>", 14,
                    "0.38 0.74", "0.97 0.95", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreateText(ref ui, card + "_s", card, "muted", status, 11,
                    "0.38 0.52", "0.97 0.76", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                if (alive)
                {
                    int hp = o["hp"]?.Value<int>() ?? 0, hpMax = Math.Max(1, o["hpMax"]?.Value<int>() ?? 1);
                    float frac = Math.Max(0f, Math.Min(1f, (float)hp / hpMax));
                    CUIClass.CreatePanel(ref ui, card + "_hpb", card, "row", "0.38 0.40", "0.97 0.46", false, 0f, Mat);
                    CUIClass.CreatePanel(ref ui, card + "_hpf", card + "_hpb", "red",
                        "0 0", $"{frac.ToString("0.###", CultureInfo.InvariantCulture)} 1", false, 0f, Mat);
                }

                CUIClass.CreateButton(ref ui, card + "_b", card, "hover", "<color=@accent>ПОДРОБНЕЕ</color>", 10,
                    "0.38 0.10", "0.97 0.28", $"octahub_evt {key}", "", "white", 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }

            if (totalPages > 1)
            {
                if (st.EvtPage > 0)
                    CUIClass.CreateButton(ref ui, "oh_boss_prev", UiCnt, "accent.glass", "<color=@accent>‹</color>", 16,
                        "0.885 0.015", "0.93 0.08", $"octahub_evt_page {st.EvtPage - 1}", "", "white", 0f,
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                if (st.EvtPage + 1 < totalPages)
                    CUIClass.CreateButton(ref ui, "oh_boss_next", UiCnt, "accent.glass", "<color=@accent>›</color>", 16,
                        "0.935 0.015", "0.985 0.08", $"octahub_evt_page {st.EvtPage + 1}", "", "white", 0f,
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }
        }

        // Детальный вид: «bosses» → сетка боссов, «we:<кат>» → мировое событие,
        // «pv:<plugin>:<cat>» → генерический вид провайдера, иначе → лор босса
        private void DrawEventDetail(ref CuiElementContainer ui, BasePlayer player, string key)
        {
            if (key == "bosses") { DrawBossGrid(ref ui, player); return; }

            if (key.StartsWith("pv:"))
            {
                var pp = key.Substring(3).Split(':');
                if (pp.Length >= 2)
                    DrawProviderDetail(ref ui, player, pp[0], pp[1]);
                return;
            }

            // мировое событие — категория из трекера
            if (key.StartsWith("we:"))
            {
                var c = Cat(key.Substring(3));
                string status = "<color=@muted>нет на карте</color>";
                if (c != null && c.Count > 0)
                    status = $"<color=@accent>АКТИВНО{(c.Count > 1 ? " ×" + c.Count : "")}</color>" +
                             $" · квадрат <color=@accent>{GridOf(c.Pos)}</color>";
                else if (c != null && !string.IsNullOrEmpty(c.Note))
                    status = $"<color=@muted>{c.Note}</color>";

                CUIClass.CreatePanel(ref ui, "oh_evd", UiCnt, "row", "0.015 0.90", "0.985 0.985", false, 0f, Mat);
                CUIClass.CreateText(ref ui, "oh_evd_s", "oh_evd", "text", status, 12,
                    "0.02 0", "0.85 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                // крупный тайл события слева — квадратный якорь (контент ~1.42:1 по пикселям)
                string weImg = c?.Image ?? "";
                string tx = "0.03";
                if (weImg.StartsWith("http"))
                {
                    CUIClass.CreateImage(ref ui, UiCnt, Img3(weImg), "0.03 0.42", "0.31 0.82", 0.15f);
                    tx = "0.34";
                }
                CUIClass.CreateText(ref ui, "oh_evd_t", UiCnt, "text",
                    c != null
                        ? $"<size=30><color=#E8B84B>{c.Title}</color></size>\n\n<size=16>{c.Desc}</size>\n\n" +
                          "<size=13><color=#83b8c7>СОВЕТ:</color> иди группой — точка шумная, сбегутся и свои, и чужие.</size>"
                        : "Неизвестное событие.",
                    14, tx + " 0.08", "0.97 0.88", TextAnchor.UpperLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
                // кнопка последней — иначе её перекроет текст
                CUIClass.CreateButton(ref ui, "oh_evd_b", "oh_evd", "accent.glass", "<color=@accent>← НАЗАД</color>", 10,
                    "0.86 0.15", "0.99 0.85", "octahub_evt back", "", "white", 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                return;
            }

            JObject live = BossStates()?.OfType<JObject>()
                .FirstOrDefault(o => o["key"]?.ToString() == key);

            // лор-страница из каталога OctaBoss (та же генерация, что давала PageData)
            JObject spec = BossSpecInfo(key);
            var lines = spec != null ? BossPageLines(spec) : new List<string> { key.ToUpper() };

            // вверху — живой статус одной строкой
            string status2;
            if (live == null) status2 = "<color=@muted>нет данных</color>";
            else if (live["enabled"]?.Value<bool>() != true) status2 = "<color=@muted>ВЫКЛЮЧЕН</color>";
            else if (live["alive"]?.Value<bool>() == true)
            {
                string g = live["grid"]?.ToString();
                status2 = $"<color=@red>ЖИВ</color> · {live["hp"]}/{live["hpMax"]} HP" +
                         (g != null ? $" · квадрат <color=@accent>{g}</color>" : "") +
                         $" · фаза {live["phase"]}";
            }
            else
            {
                long ns = live["nextSpawnUnix"]?.Value<long>() ?? 0;
                status2 = ns > 0 ? $"спавн через <color=@accent>{FmtLeft(ns - DateTimeOffset.UtcNow.ToUnixTimeSeconds())}</color>"
                                : "<color=@muted>ожидает расписания</color>";
            }

            CUIClass.CreatePanel(ref ui, "oh_evd", UiCnt, "row", "0.015 0.90", "0.985 0.985", false, 0f, Mat);
            CUIClass.CreateText(ref ui, "oh_evd_s", "oh_evd", "text", status2, 12,
                "0.02 0", "0.85 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            // назад — в сетку боссов, откуда пришли
            CUIClass.CreateButton(ref ui, "oh_evd_b", "oh_evd", "accent.glass", "<color=@accent>← НАЗАД</color>", 10,
                "0.86 0.15", "0.99 0.85", "octahub_evt bosses", "", "white", 0f,
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            // крупный портрет босса слева — image из GetBossStates это PNG-хендл ImageLibrary
            // квадратный якорь: контент-зона ~1.42:1 по пикселям → 0.28w ≈ 0.40h
            string bossImg = live?["image"]?.ToString() ?? "";
            string tx2 = "0.03";
            if (!string.IsNullOrEmpty(bossImg))
            {
                CUIClass.CreateImage(ref ui, UiCnt,
                    bossImg.StartsWith("http") ? Img3(bossImg) : bossImg,
                    "0.03 0.42", "0.31 0.82", 0.15f);
                tx2 = "0.34";
            }

            CUIClass.CreateText(ref ui, "oh_evd_t", UiCnt, "text", string.Join("\n", lines), 13,
                tx2 + " 0.03", "0.97 0.88", TextAnchor.UpperLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
        }

        // Генерический детал-вид категории провайдера: свежий снапшот на каждую
        // перерисовку (провайдер — источник истины), сетка items[] 3x2.
        private void DrawProviderDetail(ref CuiElementContainer ui, BasePlayer player,
            string pvName, string catId)
        {
            var st = St(player);
            JObject cat = null;
            var plug = EvtProvider(pvName);
            if (plug != null)
            {
                try
                {
                    var res = plug.Call("Events_GetSnapshot", player) as JObject;
                    cat = (res?["data"]?["categories"] as JArray)?.OfType<JObject>()
                        .FirstOrDefault(o => string.Equals(o["id"]?.ToString(),
                            catId, StringComparison.OrdinalIgnoreCase));
                }
                catch { }
            }

            // шапка: статус категории + назад в обзор
            CUIClass.CreatePanel(ref ui, "oh_evd", UiCnt, "row", "0.015 0.90", "0.985 0.985", false, 0f, Mat);
            CUIClass.CreateText(ref ui, "oh_evd_s", "oh_evd", "text",
                cat != null
                    ? $"<b>{cat["title"]}</b>  <color=@muted>·</color>  {CatStatusHtml(cat)}"
                    : "<color=@muted>провайдер недоступен</color>", 12,
                "0.02 0", "0.85 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreateButton(ref ui, "oh_evd_b", "oh_evd", "accent.glass", "<color=@accent>← НАЗАД</color>", 10,
                "0.86 0.15", "0.99 0.85", "octahub_evt back", "", "white", 0f,
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            var items = (cat?["items"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (cat == null || items.Count == 0)
            {
                CUIClass.CreateText(ref ui, "oh_evd_t", UiCnt, "muted",
                    cat == null ? $"Модуль {pvName} не загружен." : "Записей нет.", 14,
                    "0 0", "1 0.85", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                return;
            }

            int totalPages = (int)Math.Ceiling(items.Count / 6.0);
            st.EvtPage = Math.Max(0, Math.Min(st.EvtPage, totalPages - 1));
            int start = st.EvtPage * 6;

            for (int idx = 0; idx < 6 && start + idx < items.Count; idx++)
            {
                var o = items[start + idx];
                string[] a = EvtGrid[idx].Split('-');
                string card = $"oh_pv_{idx}";
                bool active = o["active"]?.Value<bool>() ?? false;

                CUIClass.CreatePanel(ref ui, card, UiCnt, "bg", a[0], a[1], false, 0f, Mat);
                CUIClass.CreatePanel(ref ui, card + "_g", card, active ? "accent" : "accent.dark",
                    "0 0.97", "1 1", false, 0f, Mat);

                string img = o["image"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(img))
                    CUIClass.CreateImage(ref ui, card,
                        img.StartsWith("http") ? Img3(img) : img, "0.05 0.32", "0.35 0.68", 0.15f);

                // квадрат к имени, если провайдер его отдал
                string nameTxt = $"<b>{o["name"]}</b>";
                string grid = o["grid"]?.ToString();
                if (!string.IsNullOrEmpty(grid)) nameTxt += $"  <color=@muted>[{grid}]</color>";

                CUIClass.CreateText(ref ui, card + "_n", card, "text", nameTxt, 14,
                    "0.38 0.74", "0.97 0.95", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreateText(ref ui, card + "_s", card, "muted", CatStatusHtml(o), 11,
                    "0.38 0.52", "0.97 0.76", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                // hp-бар, если item отдал hp/hpMax
                var hp = o["hp"]?.Value<int>();
                var hpMax = o["hpMax"]?.Value<int>();
                if (hp != null && hpMax != null && hpMax > 0)
                {
                    float frac = Math.Max(0f, Math.Min(1f, (float)hp.Value / hpMax.Value));
                    CUIClass.CreatePanel(ref ui, card + "_hpb", card, "row", "0.38 0.40", "0.97 0.46", false, 0f, Mat);
                    CUIClass.CreatePanel(ref ui, card + "_hpf", card + "_hpb", "red",
                        "0 0", $"{frac.ToString("0.###", CultureInfo.InvariantCulture)} 1", false, 0f, Mat);
                }

                // item может задать свой ключ деталя (octahub_evt <cmd>)
                var icmd = o["cmd"]?.ToString();
                if (!string.IsNullOrEmpty(icmd))
                    CUIClass.CreateButton(ref ui, card + "_b", card, "hover", "<color=@accent>ПОДРОБНЕЕ</color>", 10,
                        "0.38 0.10", "0.97 0.28", $"octahub_evt {icmd}", "", "white", 0f,
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }

            if (totalPages > 1)
            {
                if (st.EvtPage > 0)
                    CUIClass.CreateButton(ref ui, "oh_pv_prev", UiCnt, "accent.glass", "<color=@accent>‹</color>", 16,
                        "0.885 0.015", "0.93 0.08", $"octahub_evt_page {st.EvtPage - 1}", "", "white", 0f,
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                if (st.EvtPage + 1 < totalPages)
                    CUIClass.CreateButton(ref ui, "oh_pv_next", UiCnt, "accent.glass", "<color=@accent>›</color>", 16,
                        "0.935 0.015", "0.985 0.08", $"octahub_evt_page {st.EvtPage + 1}", "", "white", 0f,
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }
        }

        // Провайдер сообщил об изменении состояния — перечитываем снапшоты
        // у всех, у кого открыт раздел СОБЫТИЯ (в т.ч. детал-виды: данные
        // подтягиваются свежие на каждую перерисовку)
        private void OnOctaEventChanged(string provider, string categoryId)
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                UiState s;
                if (_ui.TryGetValue(p.userID, out s) && s.Section == "events")
                    UiRefresh(p, UiCnt);
            }
        }

        // ── Реестр провайдеров событий (Docs/OctaAPI-v1.md, фаза 5) ─────────
        // Провайдер — любой плагин с API Events_GetSnapshot(player) →
        // {categories:[{id,title,image,active,status,tone,detail,items[]}]}.
        // Карточки категорий идут в сетку СОБЫТИЙ перед мировыми категориями.
        // detail: "bosses"/"we:*" — встроенные виды; "pv:<plugin>:<catId>" —
        // генерический детал по items[]; пусто — генерический по умолчанию.
        // Живой поиск по имени (PITFALLS §11) — PluginReference залипает на reload.
        private Plugin EvtProvider(string name)
        {
            try
            {
                foreach (var pl in Interface.Oxide.RootPluginManager.GetPlugins())
                    if (string.Equals(pl.Name, name, StringComparison.OrdinalIgnoreCase) && pl.IsLoaded)
                        return pl;
            }
            catch { }
            return null;
        }

        // Категории от всех сконфигурированных провайдеров, в порядке конфига.
        // Провайдер недоступен → карточка-заглушка (не молчаливый пропуск).
        private List<JObject> ProviderCats(BasePlayer player)
        {
            var list = new List<JObject>();
            var names = config?.generalSettings?.eventProviders;
            if (names == null) return list;
            foreach (var pn in names)
            {
                var plug = EvtProvider(pn);
                if (plug == null)
                {
                    list.Add(new JObject
                    {
                        ["_pv"] = pn, ["id"] = pn.ToLowerInvariant(),
                        ["title"] = pn.ToUpperInvariant(),
                        ["status"] = "модуль недоступен", ["tone"] = "muted",
                        ["active"] = false
                    });
                    continue;
                }
                JObject res = null;
                try { res = plug.Call("Events_GetSnapshot", player) as JObject; } catch { }
                if (res == null)
                {
                    // провайдер старой версии без контракта — legacy-мост
                    // для OctaBoss: карточка собирается из GetBossStates
                    if (string.Equals(pn, "OctaBoss", StringComparison.OrdinalIgnoreCase))
                        list.Add(new JObject
                        {
                            ["_pv"] = pn, ["id"] = "bosses",
                            ["title"] = "БОССЫ ОСТРОВА", ["image"] = "",
                            ["status"] = BossAggStatus(out var act2, out _),
                            ["tone"] = "raw", ["active"] = act2,
                            ["detail"] = "bosses"
                        });
                    continue;
                }
                if (res["ok"]?.Value<bool>() != true) continue;
                foreach (var c in (res["data"]?["categories"] as JArray)
                     ?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var cc = (JObject)c.DeepClone();
                    cc["_pv"] = pn;
                    list.Add(cc);
                }
            }
            return list;
        }

        // status + tone → html: tone "raw" — провайдер сам отформатировал
        private static string CatStatusHtml(JObject c)
        {
            var s = c?["status"]?.ToString() ?? "";
            var t = c?["tone"]?.ToString() ?? "muted";
            if (string.IsNullOrEmpty(s) || t == "raw") return s;
            return $"<color=@{t}>{s}</color>";
        }

        // Снапшот боссов: v1-конверт Boss_GetActiveInstances (data.bosses —
        // слитный вид на вид босса), фолбэк — legacy GetBossStates для старого
        // OctaBoss без контракта.
        private JArray BossStates()
        {
            try
            {
                var res = OctaBoss?.Call("Boss_GetActiveInstances") as JObject;
                var arr = res?["data"]?["bosses"] as JArray;
                if (arr != null) return arr;
            }
            catch { }
            try { return OctaBoss?.Call("GetBossStates") as JArray; } catch { return null; }
        }

        // Данные вида босса — через контракт v1 (Boss_GetCatalog); фолбэк —
        // чтение OctaBoss.json + нормализация для старого OctaBoss без контракта.
        private JObject BossSpecInfo(string key)
        {
            try
            {
                var res = OctaBoss?.Call("Boss_GetCatalog") as JObject;
                if (res?["ok"]?.Value<bool>() == true)
                {
                    var hit = (res["data"]?["bosses"] as JArray)?.OfType<JObject>()
                        .FirstOrDefault(k => string.Equals(k["key"]?.ToString(), key,
                            StringComparison.OrdinalIgnoreCase));
                    if (hit != null) return hit;
                }
            }
            catch { }
            var legacy = FindBossSpec(key);
            return legacy == null ? null : NormalizeLegacyBossSpec(legacy);
        }

        // старый конфиг OctaBoss.json (русские JsonProperty) → единые ключи
        // каталога v1 — BossPageLines работает с одним набором полей
        private JObject NormalizeLegacyBossSpec(JObject b)
        {
            var esc = b["Свита"] as JObject;
            var sp = b["Спавн"] as JObject;
            var rw = b["Награды"] as JObject;
            var nativeBiome = JBool(sp, "Спавн только в родном биоме");
            return new JObject
            {
                ["key"] = JStr(b, "Ключ (латиницей, для команд)", "?"),
                ["name"] = JStr(b, "Имя босса", "?"),
                ["enabled"] = JBool(b, "Включён", true),
                ["escortCount"] = esc?["Количество"]?.ToObject<int?>() ?? 0,
                ["monuments"] = JStr(sp, "Монументы-якоря (через запятую, частичное совпадение; пусто = любая точка)"),
                ["nativeBiome"] = nativeBiome,
                ["nativeBiomes"] = nativeBiome
                    ? JStr(sp, "Родные биомы (arid,temperate,tundra,arctic,jungle через запятую)")
                    : "любой",
                ["needsWater"] = JBool(sp, "Требовать воду рядом (водные звери; крокодил иначе умирает сойдя с навмеша)"),
                ["rewardCurrency"] = rw?["Валюта за убийство (ServerRewards/Economics)"]?.ToObject<int?>() ?? 0,
                ["rewardParticipation"] = rw?["Валюта участникам (нанёс >= порога урона)"]?.ToObject<int?>() ?? 0
            };
        }

        // LEGACY-фолбэк: ищем вид босса в OctaBoss.json — используется только
        // когда OctaBoss не отдаёт Boss_GetCatalog (старая версия)
        private JObject FindBossSpec(string key)
        {
            try
            {
                var path = Path.Combine(Interface.Oxide.ConfigDirectory, "OctaBoss.json");
                if (!File.Exists(path)) return null;
                var specs = (JObject.Parse(File.ReadAllText(path))["Виды боссов"] as JArray)?.OfType<JObject>();
                return specs?.FirstOrDefault(b =>
                    string.Equals(b["Ключ (латиницей, для команд)"]?.ToString(), key, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        #endregion

        #region [Section: Профиль — OctVIP + OctaTP + OctaFeed]

        private void DrawProfileSection(ref CuiElementContainer ui, BasePlayer player)
        {
            // левая колонка — паспорт игрока
            CUIClass.CreatePanel(ref ui, "oh_pr_l", UiCnt, "row", "0.015 0.40", "0.36 0.97", false, 0f, Mat);
            CUIClass.CreatePanel(ref ui, "oh_pr_lg", "oh_pr_l", "accent.dark", "0 0.965", "1 1", false, 0f, Mat);
            CUIClass.CreateText(ref ui, "oh_pr_name", "oh_pr_l", "text",
                $"<b>{player.displayName}</b>", 17, "0.06 0.86", "0.94 0.95",
                TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // VIP-бейдж из OctVIP
            string vipLine = "<color=@muted>Обычный игрок</color>";
            try
            {
                if (OctVIP != null)
                {
                    // v1: статус одним вызовом; фолбэк — четыре legacy-геттера
                    int lvl = 0; string vname = null, vcol = null; long exp = 0;
                    var res = OctVIP.Call("Vip_GetPlayerStatus", (ulong)player.userID) as JObject;
                    var d = res?["data"] as JObject;
                    if (d != null)
                    {
                        lvl = d["level"]?.Value<int>() ?? 0;
                        vname = d["name"]?.ToString();
                        vcol = d["color"]?.ToString();
                        exp = d["expiresAt"]?.Value<long>() ?? 0;
                    }
                    else
                    {
                        lvl = Convert.ToInt32(OctVIP.Call("GetVipLevel", (ulong)player.userID));
                        if (lvl > 0)
                        {
                            vname = OctVIP.Call("GetVipLevelName", (ulong)player.userID) as string;
                            vcol = OctVIP.Call("GetVipColor", (ulong)player.userID) as string;
                            exp = Convert.ToInt64(OctVIP.Call("GetVipExpiry", (ulong)player.userID));
                        }
                    }
                    if (lvl > 0)
                    {
                        string expTxt = exp > 0
                            ? $" · до {DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime:dd.MM.yyyy}"
                            : " · бессрочно";
                        vipLine = $"<color={vcol ?? "#E8B84B"}><b>{vname ?? $"VIP {lvl}"}</b></color><color=@muted>{expTxt}</color>";
                    }
                }
            }
            catch { }
            CUIClass.CreateText(ref ui, "oh_pr_vip", "oh_pr_l", "muted", vipLine, 12,
                "0.06 0.72", "0.94 0.84", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // быстрая сводка: избранные наборы
            List<string> fav;
            string favTxt = _fav.TryGetValue(player.userID, out fav) && fav.Count > 0
                ? string.Join(", ", fav.Take(6))
                : "нет — отмечай ★ во вкладке НАБОРЫ";
            CUIClass.CreateText(ref ui, "oh_pr_fav", "oh_pr_l", "muted",
                $"<color=@accent>◆ ИЗБРАННЫЕ НАБОРЫ</color>\n<size=11>{favTxt}</size>", 12,
                "0.06 0.42", "0.94 0.68", TextAnchor.UpperLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // лента убийств — текущее состояние из OctaFeed
            string feedState = "—";
            if (OctaFeed != null)
            {
                bool hm = FeedGet(player, "hideMessages"), hn = FeedGet(player, "hideMyName");
                feedState = (hm ? "лента скрыта" : "лента видна") + (hn ? ", ник скрыт" : "");
            }
            CUIClass.CreateText(ref ui, "oh_pr_feed", "oh_pr_l", "muted",
                $"<color=@accent>◆ ЛЕНТА УБИЙСТВ</color>\n<size=11>{feedState} — меняется в НАСТРОЙКАХ</size>", 12,
                "0.06 0.18", "0.94 0.40", TextAnchor.UpperLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // правая колонка, верх — дома из OctaTP (компактно: лимит домов мал)
            CUIClass.CreatePanel(ref ui, "oh_pr_r", UiCnt, "row", "0.375 0.68", "0.985 0.97", false, 0f, Mat);
            CUIClass.CreatePanel(ref ui, "oh_pr_rg", "oh_pr_r", "accent.dark", "0 0.965", "1 1", false, 0f, Mat);
            CUIClass.CreateText(ref ui, "oh_pr_hh", "oh_pr_r", "accent", "◆ <b>МОИ ДОМА</b>", 12,
                "0.04 0.83", "0.96 0.95", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            if (OctaTP == null)
            {
                CUIClass.CreateText(ref ui, "oh_pr_he", "oh_pr_r", "muted",
                    "OctaTP не загружен.", 11, "0 0", "1 0.78", TextAnchor.MiddleCenter,
                    "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            }
            else
            {
                // v1: TP_GetHomes отдаёт JSON-конверт; фолбэк — legacy Dictionary
                List<KeyValuePair<string, Vector3>> homes = null;
                try
                {
                    var res = OctaTP.Call("TP_GetHomes", player) as JObject;
                    var arr = res?["data"]?["homes"] as JArray;
                    if (arr != null)
                        homes = arr.OfType<JObject>().Select(h => new KeyValuePair<string, Vector3>(
                            h["name"]?.ToString() ?? "?",
                            new Vector3(
                                h["pos"]?[0]?.Value<float>() ?? 0,
                                h["pos"]?[1]?.Value<float>() ?? 0,
                                h["pos"]?[2]?.Value<float>() ?? 0))).ToList();
                    else if (res == null)
                        homes = (OctaTP.Call("API_GetHomes", player) as Dictionary<string, Vector3>)?.ToList();
                }
                catch { }
                if (homes == null || homes.Count == 0)
                {
                    CUIClass.CreateText(ref ui, "oh_pr_he", "oh_pr_r", "muted",
                        "Домов нет — поставь точку: /sethome имя", 11, "0 0", "1 0.78",
                        TextAnchor.MiddleCenter, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
                }
                else
                {
                    int hi = 0;
                    foreach (var kv in homes.Take(4))
                    {
                        float y1 = 0.72f - hi * 0.19f, y2 = y1 + 0.17f;
                        string aMin = $"0.04 {y1.ToString("0.###", CultureInfo.InvariantCulture)}";
                        string aMax = $"0.96 {y2.ToString("0.###", CultureInfo.InvariantCulture)}";
                        CUIClass.CreatePanel(ref ui, $"oh_pr_h{hi}", "oh_pr_r", "bg", aMin, aMax, false, 0f, Mat);
                        CUIClass.CreateText(ref ui, $"oh_pr_h{hi}_t", $"oh_pr_h{hi}", "text",
                            $"<color=@accent>⌂</color> {kv.Key}  <color=@muted>[{GridOf(kv.Value)}]</color>", 11,
                            "0.04 0", "0.96 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                        hi++;
                    }
                }
            }

            // правая колонка, низ — друзья и враги (OctaRel)
            CUIClass.CreatePanel(ref ui, "oh_pr_rel", UiCnt, "row", "0.375 0.40", "0.985 0.66", false, 0f, Mat);
            CUIClass.CreatePanel(ref ui, "oh_pr_rel_g", "oh_pr_rel", "accent.dark", "0 0.965", "1 1", false, 0f, Mat);
            CUIClass.CreateText(ref ui, "oh_pr_rel_h", "oh_pr_rel", "accent", "◆ <b>СВОИ / ВРАГИ</b>", 12,
                "0.04 0.86", "0.96 0.95", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            if (OctaRel == null)
            {
                CUIClass.CreateText(ref ui, "oh_pr_rel_e", "oh_pr_rel", "muted",
                    "Модуль связей (OctaRel) не загружен.", 11, "0 0", "1 0.8",
                    TextAnchor.MiddleCenter, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
            }
            else
            {
                JObject rel = null;
                try
                {
                    var res = OctaRel.Call("Rel_GetRelations", (ulong)player.userID) as JObject;
                    rel = res?["data"] as JObject;
                    if (rel == null && res == null)
                        rel = OctaRel.Call("GetRelations", (ulong)player.userID) as JObject;
                }
                catch { }
                // параметры пинга — цены и доступность валют из OctaRel
                _pingRp = _pingCoins = _pingSec = 0; _pingRpOn = _pingCoinsOn = _pingTest = false;
                try
                {
                    var pres = OctaRel.Call("Rel_GetPingInfo") as JObject;
                    var pi = pres?["data"] as JObject;
                    if (pi == null && pres == null)
                        pi = OctaRel.Call("GetPingInfo") as JObject;
                    if (pi != null)
                    {
                        _pingRp = pi["rp"]?.Value<int>() ?? 0;
                        _pingCoins = pi["coins"]?.Value<int>() ?? 0;
                        _pingSec = pi["sec"]?.Value<int>() ?? 10;
                        _pingRpOn = pi["rpOn"]?.Value<bool>() == true;
                        _pingCoinsOn = pi["coinsOn"]?.Value<bool>() == true;
                        _pingTest = pi["testOn"]?.Value<bool>() == true;
                    }
                }
                catch { }
                var friends = rel?["friends"] as JArray ?? new JArray();
                var enemies = rel?["enemies"] as JArray ?? new JArray();

                CUIClass.CreateText(ref ui, "oh_pr_rel_f", "oh_pr_rel", "muted",
                    $"<color=#8dd06f>ДРУЗЬЯ ({friends.Count})</color>   <color=#cd412b>ВРАГИ ({enemies.Count})</color>",
                    10, "0.04 0.72", "0.96 0.85", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                int ri = 0;
                foreach (var f in friends.Take(4)) DrawRelRow(ref ui, ri++, f, "friend", true);
                int ei = 0;
                foreach (var e in enemies.Take(4)) DrawRelRow(ref ui, 4 + ei++, e, "enemy", false);

                CUIClass.CreateText(ref ui, "oh_pr_rel_hint", "oh_pr_rel", "muted",
                    "/enemy ping ник — платная метка врага на экране · радар предупредит сам", 9,
                    "0.04 0.01", "0.96 0.11", TextAnchor.MiddleLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
            }

            // нижняя полоса — быстрые переходы
            CUIClass.CreateButton(ref ui, "oh_pr_go1", UiCnt, "accent.glass", "<color=@accent>НАБОРЫ →</color>", 11,
                "0.015 0.28", "0.20 0.36", "octahub_sec kits", "", "white", 0f,
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            CUIClass.CreateButton(ref ui, "oh_pr_go2", UiCnt, "accent.glass", "<color=@accent>СЕРВИСЫ →</color>", 11,
                "0.21 0.28", "0.36 0.36", "octahub_sec services", "", "white", 0f,
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            CUIClass.CreateText(ref ui, "oh_pr_note", UiCnt, "muted",
                "Данные живые: OctVIP · OctaTP · OctaFeed · OctaRel", 10,
                "0.375 0.28", "0.985 0.36", TextAnchor.MiddleLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
        }

        // строка контакта в карточке СВОИ/ВРАГИ: i 0..3 — друзья слева, 4..7 — враги справа
        private void DrawRelRow(ref CuiElementContainer ui, int i, JToken who, string type, bool isFriend)
        {
            string name = who?["name"]?.ToString() ?? "?";
            ulong id = who?["id"]?.Value<ulong>() ?? 0UL;
            bool online = id != 0 && BasePlayer.FindByID(id) != null;

            float x1 = isFriend ? 0.04f : 0.52f, x2 = isFriend ? 0.50f : 0.96f;
            int row = isFriend ? i : i - 4;
            float y1 = 0.62f - row * 0.155f, y2 = y1 + 0.13f;
            string aMin = $"{x1.ToString("0.##", CultureInfo.InvariantCulture)} {y1.ToString("0.###", CultureInfo.InvariantCulture)}";
            string aMax = $"{x2.ToString("0.##", CultureInfo.InvariantCulture)} {y2.ToString("0.###", CultureInfo.InvariantCulture)}";
            string n = $"oh_rel_{i}";
            bool ping = !isFriend && online && (_pingRpOn || _pingCoinsOn || _pingTest);

            CUIClass.CreatePanel(ref ui, n, "oh_pr_rel", "bg", aMin, aMax, false, 0f, Mat);
            CUIClass.CreateText(ref ui, n + "_t", n, "text",
                $"{(online ? "<color=#8dd06f>●</color>" : "<color=#666>●</color>")} {name}", 10,
                "0.02 0", ping ? "0.44 1" : "0.82 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            // кнопки-ценники платного ESP-пинга — только живым онлайн-врагам
            if (ping)
            {
                // тест-режим без валют — золотая кнопка ESP вместо ценника
                if (_pingTest && !_pingRpOn && !_pingCoinsOn)
                    CUIClass.CreateButton(ref ui, n + "_pt", n, "accent.glass",
                        "<color=@accent>ESP</color>", 9,
                        "0.45 0.10", "0.72 0.90", $"octahub_rel ping test {id}", "", "white", 0f,
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                if (_pingRpOn)
                    CUIClass.CreateButton(ref ui, n + "_pr", n, "accent.glass",
                        $"<color=#83b8c7>{_pingRp}RP</color>", 9,
                        "0.45 0.10", "0.58 0.90", $"octahub_rel ping rp {id}", "", "white", 0f,
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                if (_pingCoinsOn)
                    CUIClass.CreateButton(ref ui, n + "_pe", n, "accent.glass",
                        $"<color=#8dd06f>${(_pingCoins >= 1000 ? (_pingCoins / 1000f).ToString("0.#", CultureInfo.InvariantCulture) + "К" : _pingCoins.ToString())}</color>", 9,
                        "0.59 0.10", "0.72 0.90", $"octahub_rel ping coins {id}", "", "white", 0f,
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }
            CUIClass.CreateButton(ref ui, n + "_x", n, "0 0 0 0", "<color=#cd412b>✕</color>", 9,
                "0.85 0", "0.98 1", $"octahub_rel del {type} {id}", "", "white", 0f,
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
        }

        #endregion

        #region [Section: Сервисы — настраиваемые строки действий]

        private void DrawServicesSection(ref CuiElementContainer ui, BasePlayer player)
        {
            var rows = config?.services?.rows ?? new List<Configuration.ServiceRow>();
            CUIClass.CreateText(ref ui, "oh_sv_h", UiCnt, "text",
                "<b>◆ СЕРВИСЫ СЕРВЕРА</b>  <color=@muted>— клик по строке выполняет команду</color>", 15,
                "0.015 0.925", "0.985 0.99", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            if (rows.Count == 0)
            {
                CUIClass.CreateText(ref ui, "oh_sv_e", UiCnt, "muted",
                    "Сервисы не настроены (config → Services → rows).", 13, "0 0", "1 0.9",
                    TextAnchor.MiddleCenter, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
                return;
            }

            int i = 0;
            foreach (var r in rows.Take(9))
            {
                if (!r.enabled) { i++; continue; }
                float y1 = 0.90f - i * 0.10f, y2 = y1 + 0.085f;
                string aMin = $"0.015 {y1.ToString("0.###", CultureInfo.InvariantCulture)}";
                string aMax = $"0.985 {y2.ToString("0.###", CultureInfo.InvariantCulture)}";
                string n = $"oh_sv_{i}";
                bool hasCmd = !string.IsNullOrEmpty(r.chatCommand);

                CUIClass.CreateButton(ref ui, n, UiCnt, "row", "", 11, aMin, aMax,
                    hasCmd ? $"octahub_svc {i}" : "", "", "white", 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                if (!string.IsNullOrEmpty(r.icon) && r.icon.StartsWith("http"))
                    CUIClass.CreateImage(ref ui, n, Img3(r.icon), "0.02 0.15", "0.07 0.85", 0.15f);
                else
                    CUIClass.CreateText(ref ui, n + "_i", n, "accent", "◆", 14, "0.02 0", "0.07 1",
                        TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                CUIClass.CreateText(ref ui, n + "_t", n, "text", $"<b>{r.title}</b>", 12,
                    "0.085 0.45", "0.85 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreateText(ref ui, n + "_d", n, "muted", r.desc ?? "", 10,
                    "0.085 0.05", "0.85 0.50", TextAnchor.MiddleLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
                if (hasCmd)
                    CUIClass.CreateText(ref ui, n + "_c", n, "muted", "/" + r.chatCommand.TrimStart('/'), 10,
                        "0.86 0", "0.985 1", TextAnchor.MiddleRight, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
                i++;
            }
        }

        #endregion

        #region [Section: Инфо — список страниц + текстовый рендер]

        // Список инфо-страниц: карточки-строки из старых вкладок 2,4,5,6,7,8,9,11
        private void DrawInfoList(ref CuiElementContainer ui, BasePlayer player)
        {
            CUIClass.CreateText(ref ui, "oh_il_h", UiCnt, "text",
                "<b>◆ ИНФОРМАЦИЯ</b>", 18, "0.015 0.925", "0.985 0.99",
                TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            int i = 0;
            foreach (var tab in InfoTabs)
            {
                var t = TabCfg(tab);
                if (t == null || !t.btnEnabled) continue;
                bool allowed = CheckPerms(player, tab);

                float y1 = 0.89f - i * 0.115f, y2 = y1 + 0.10f;
                string aMin = $"0.015 {y1.ToString("0.###", CultureInfo.InvariantCulture)}";
                string aMax = $"0.985 {y2.ToString("0.###", CultureInfo.InvariantCulture)}";
                string n = $"oh_il_{i}";

                CUIClass.CreateButton(ref ui, n, UiCnt, "row", "", 11, aMin, aMax,
                    allowed ? $"octahub_infotab {tab}" : "", "", "white", 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                CUIClass.CreateText(ref ui, n + "_i", n, allowed ? "accent" : "muted",
                    allowed ? "◆" : "🔒", 13, "0.02 0", "0.07 1",
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreateText(ref ui, n + "_t", n, allowed ? "text" : "muted",
                    $"<b>{StripSize(t.btnTitle ?? $"СТРАНИЦА {tab}").Trim()}</b>", 13,
                    "0.08 0", "0.85 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreateText(ref ui, n + "_a", n, "muted", allowed ? "→" : "", 14,
                    "0.93 0", "0.985 1", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                i++;
            }
        }

        // Текстовая инфо-страница — прежний рендер вкладок (маркеры [H]/[h]/[F],
        // фоновая картинка, постраничная навигация по PageData).
        private void DrawInfoPage(ref CuiElementContainer ui, BasePlayer player, int tabNumber, int page)
        {
            var tab = TabCfg(tabNumber);

            string bgImage = tab.tabImageUrl;
            if (bgImage != null && (bgImage.StartsWith("http") || bgImage.StartsWith("www")) &&
                tab.tabImageAnchor != null && tab.tabImageAnchor.Count >= 2)
                CUIClass.CreateImage(ref ui, UiCnt, Img2(tabNumber), tab.tabImageAnchor[0], tab.tabImageAnchor[1], 0f);

            var st = St(player);

            if (!CheckPerms(player, tabNumber))
            {
                CUIClass.CreateText(ref ui, "oh_cnt_text", UiCnt, "text",
                    "<size=44>✘</size>\n<color=@muted>Нет доступа к этой странице.</color>", 18,
                    "0.02 0.02", "0.98 0.98", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                return;
            }

            var lines = page >= 2 ? PageLines(tabNumber, page) : tab.textLines;
            var ls = lines ?? new List<string>();

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
                if (headTitle != null)
                    CUIClass.CreateText(ref ui, "oh_cnt_title", UiCnt, "text", StripSize(R(player, headTitle)), 26,
                        "0.035 0.865", "0.965 0.985", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                if (headSub != null)
                    CUIClass.CreateText(ref ui, "oh_cnt_sub", UiCnt, "muted", StripSize(R(player, headSub)), 13,
                        "0.035 0.80", "0.965 0.875", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                CUIClass.CreatePanel(ref ui, "oh_cnt_rule", UiCnt, "accent.dark", "0.035 0.792", "0.965 0.795", false, 0f, Mat);

                CUIClass.CreateText(ref ui, "oh_cnt_text", UiCnt, tab.mainFontColor, R(player, string.Join("\n", body)),
                    tab.mainFontSize, "0.035 0.115", "0.965 0.785", tab.mainAlign,
                    tab.mainFontStyle, tab.fontOutlineColor, $"{tab.fontOutlineThick} {tab.fontOutlineThick}");

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

            // Кнопка «Назад» — ПОСЛЕДНЯЯ: в CUI поздние элементы поверх,
            // созданная до текста кнопка перекрывается и не кликается.
            if (st.Section == "info")
                CUIClass.CreateButton(ref ui, "oh_ip_back", UiCnt, "accent.glass", "<color=@accent>← НАЗАД</color>", 10,
                    "0.015 0.015", "0.10 0.07", "octahub_infotab back", "", "white", 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            DrawPageArrows(ref ui, tabNumber, page);
        }

        #endregion

        #region [Section: Настройки — живые переключатели]

        private void DrawSettingsSection(ref CuiElementContainer ui, BasePlayer player)
        {
            CUIClass.CreateText(ref ui, "oh_st_h", UiCnt, "text",
                "<b>◆ НАСТРОЙКИ</b>", 18, "0.015 0.925", "0.985 0.99",
                TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

            int i = 0;

            // автооткрытие панели при входе — персональная настройка
            bool auto = !_autoOpen.TryGetValue(player.userID, out var av) || av;
            DrawSettingRow(ref ui, i++, "ОТКРЫВАТЬ ПАНЕЛЬ ПРИ ВХОДЕ",
                "показывать OctaHub после подключения к серверу",
                auto, "octahub_set autoopen", true);

            // переключатели OctaFeed — живые, через API
            bool feedOn = OctaFeed != null;
            DrawSettingRow(ref ui, i++, "СКРЫВАТЬ ЛЕНТУ УБИЙСТВ",
                feedOn ? "не показывать deathfeed в левом верхнем углу" : "модуль OctaFeed не загружен",
                feedOn && FeedGet(player, "hideMessages"), "octahub_set feedmsg", feedOn);

            DrawSettingRow(ref ui, i++, "СКРЫВАТЬ МОЙ НИК В ЛЕНТЕ",
                feedOn ? "другие видят Anonymous вместо твоего ника" : "модуль OctaFeed не загружен",
                feedOn && FeedGet(player, "hideMyName"), "octahub_set feedname", feedOn);

            // уведомления OctaRel — живые, через API
            bool relOn = OctaRel != null;
            bool relNotify = relOn && RelNotifyGet(player);
            DrawSettingRow(ref ui, i++, "УВЕДОМЛЕНИЯ О ДРУЗЬЯХ И ВРАГАХ",
                relOn ? "чат: вход/выход из списков + радар врага рядом" : "модуль OctaRel не загружен",
                relOn && relNotify, "octahub_set relnotify", relOn);

            CUIClass.CreateText(ref ui, "oh_st_hint", UiCnt, "muted",
                "настройки сохраняются автоматически и переживают перезаходы", 10,
                "0.015 0.03", "0.985 0.09", TextAnchor.MiddleLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");
        }

        // Строка настройки — ВСЯ кликабельная (узкая кнопка-бегунок промахивается)
        private void DrawSettingRow(ref CuiElementContainer ui, int i, string title, string desc,
            bool on, string cmd, bool enabled)
        {
            float y1 = 0.89f - i * 0.125f, y2 = y1 + 0.11f;
            string aMin = $"0.015 {y1.ToString("0.###", CultureInfo.InvariantCulture)}";
            string aMax = $"0.985 {y2.ToString("0.###", CultureInfo.InvariantCulture)}";
            string n = $"oh_st_{i}";

            // строка = кнопка целиком
            CUIClass.CreateButton(ref ui, n, UiCnt, "row", "", 11, aMin, aMax,
                enabled ? cmd : "", "", "white", 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            CUIClass.CreateText(ref ui, n + "_t", n, enabled ? "text" : "muted", $"<b>{title}</b>", 13,
                "0.02 0.45", "0.80 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreateText(ref ui, n + "_d", n, "muted", desc, 10,
                "0.02 0.05", "0.80 0.50", TextAnchor.MiddleLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");

            // дорожка переключателя — декоративная часть той же кнопки
            CUIClass.CreatePanel(ref ui, n + "_sw", n, enabled ? (on ? "green" : "panel") : "panel",
                "0.86 0.28", "0.935 0.72", false, 0f, Mat);
            if (enabled)
            {
                string kMin = on ? "0.52 0.15" : "0.06 0.15";
                string kMax = on ? "0.94 0.85" : "0.48 0.85";
                CUIClass.CreatePanel(ref ui, n + "_kn", n + "_sw", on ? "accent" : "muted", kMin, kMax, false, 0f, Mat);
            }
            CUIClass.CreateText(ref ui, n + "_s", n, on ? "green" : "muted",
                enabled ? (on ? "ВКЛ" : "ВЫКЛ") : "—", 9, "0.94 0", "0.99 1",
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
        }

        #endregion

        #region [Kits Tab] — собственный раздел наборов (данные напрямую из OctaKits)

        private const string KGold     = "accent.dim";
        private const string KGoldDark = "accent.dark";
        private const string KGlassBg  = "bg";
        private const string KGlassRow = "row";
        private const string KGlassHov = "hover";
        private const string KGlassLt  = "panel";
        private const string KTWhite   = "white";
        private const string KGreen    = "green";
        private const string KRed      = "red";

        // Сетка 3x3 под полосой фильтров (фильтры заняли верхний ряд)
        private static readonly string[] _kitCardAnchors =
        {
            "0.01 0.635-0.32 0.865", "0.34 0.635-0.65 0.865", "0.67 0.635-0.98 0.865",
            "0.01 0.335-0.32 0.565", "0.34 0.335-0.65 0.565", "0.67 0.335-0.98 0.565",
            "0.01 0.035-0.32 0.265", "0.34 0.035-0.65 0.265", "0.67 0.035-0.98 0.265"
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

        // Фильтры категорий — без правок OctaKits: считаем по полям кита
        private static readonly (string Id, string Label)[] KitCats =
        {
            ("", "ВСЕ"), ("fav", "★ ИЗБРАННОЕ"), ("free", "БЕСПЛАТНЫЕ"),
            ("paid", "ПЛАТНЫЕ"), ("vip", "VIP")
        };

        // Список наборов для игрока — Octa API v1: один вызов Kits_GetCatalog
        // отдаёт видимые наборы с availability-снапшотом (фильтр hidden/auth —
        // на стороне владельца, Hub только сортирует по категории/избранному)
        private List<JObject> KitsTab_List(BasePlayer player, string cat)
        {
            var list = new List<JObject>();
            if (OctaKits == null) return list;

            JObject resp = null;
            try { resp = OctaKits.Call("Kits_GetCatalog", player) as JObject; } catch { }
            var arr = resp?["data"]?["kits"] as JArray;
            if (arr == null) return list;

            List<string> fav;
            bool hasFav = _fav.TryGetValue(player.userID, out fav) && fav.Count > 0;

            foreach (var t in arr)
            {
                var kit = t as JObject;
                if (kit == null) continue;
                string name = kit["name"]?.ToString() ?? "";

                if (!string.IsNullOrEmpty(cat))
                {
                    bool isFav = hasFav && fav.Contains(name);
                    int cost = kit["cost"]?.Value<int>() ?? 0;
                    string perm = kit["perm"]?.ToString() ?? "";
                    bool isVip = perm.IndexOf("vip", StringComparison.OrdinalIgnoreCase) >= 0;
                    switch (cat)
                    {
                        case "fav": if (!isFav) continue; break;
                        case "free": if (cost > 0) continue; break;
                        case "paid": if (cost <= 0) continue; break;
                        case "vip": if (!isVip) continue; break;
                    }
                }
                list.Add(kit);
            }

            // избранные — в начало списка
            if (hasFav && cat != "fav")
                list = list.OrderByDescending(k => fav.Contains(k["name"]?.ToString())).ToList();
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

        // Текст и цвет кнопки набора из availability-снапшота (Octa API v1): "ТЕКСТ%ЦВЕТ"
        private string KitsTab_BtnState(JObject kit)
        {
            var av = kit?["avail"] as JObject;
            if (av == null) return $"НЕДОСТУПЕН%{KGlassLt}";

            switch (av["code"]?.ToString())
            {
                case "COOLDOWN":
                    return $"⏱ {KitFmtTime(av["cooldownLeft"]?.Value<double>() ?? 0)}%{KRed}";
                case "NO_PERMISSION":       return $"НУЖЕН ДОСТУП%{KGlassLt}";
                case "LIMIT_REACHED":       return $"ЛИМИТ ИСЧЕРПАН%{KGlassLt}";
                case "NO_SPACE":            return $"НЕТ МЕСТА%{KGlassLt}";
                case "PROVIDER_UNAVAILABLE":
                case "UNAVAILABLE":         return $"НЕДОСТУПЕН%{KGlassLt}";
            }

            int cost = kit["cost"]?.Value<int>() ?? 0;
            if (cost <= 0) return $"ЗАБРАТЬ%{KGreen}";
            string curLabel = kit["currencyLabel"]?.ToString() ?? "$";
            return $"ЗАБРАТЬ • {cost} {curLabel}%{KGreen}";
        }

        // Раздел «Наборы»: полоса фильтров сверху + сетка карточек 3x3.
        // Рисуем В ОБЩИЙ контейнер — отдельный AddUi до панели oh_cnt
        // накрывал карточки фоном (в CUI последний элемент поверх).
        private void DrawKitsSection(ref CuiElementContainer ui, BasePlayer player)
        {
            var st = St(player);
            KitsTab_Destroy(player); // сносим возможные старые элементы набора

            if (OctaKits == null)
            {
                CUIClass.CreateText(ref ui, "owkit_err", UiCnt, "muted",
                    "Система наборов недоступна — OctaKits не загружен.", 15, "0 0", "1 1",
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                return;
            }

            // полоса фильтров категорий
            float chipW = 0.19f;
            for (int c = 0; c < KitCats.Length; c++)
            {
                float x1 = 0.01f + c * (chipW + 0.008f), x2 = x1 + chipW;
                string aMin = $"{x1.ToString("0.###", CultureInfo.InvariantCulture)} 0.895";
                string aMax = $"{x2.ToString("0.###", CultureInfo.InvariantCulture)} 0.985";
                bool act = st.KitCat == KitCats[c].Id || (KitCats[c].Id == "" && st.KitCat == "");
                CUIClass.CreateButton(ref ui, $"owkit_cat{c}", UiCnt, act ? "accent.glass" : "row",
                    act ? $"<color=@accent><b>{KitCats[c].Label}</b></color>" : $"<color=@muted>{KitCats[c].Label}</color>",
                    10, aMin, aMax, $"octahub_kit_cat {KitCats[c].Id}", "", KTWhite, 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }

            var kits = KitsTab_List(player, st.KitCat);
            if (kits.Count == 0)
            {
                CUIClass.CreateText(ref ui, "owkit_err", UiCnt, "muted",
                    st.KitCat == "fav" ? "Нет избранных наборов — отмечай ★ на карточках."
                                       : "Здесь нет доступных наборов.", 14,
                    "0 0", "1 0.85", TextAnchor.MiddleCenter, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
                return;
            }

            List<string> fav;
            bool hasFav = _fav.TryGetValue(player.userID, out fav) && fav.Count > 0;

            int start = st.KitPage * 9;
            for (int i = 0; i < 9; i++)
            {
                if (start + i >= kits.Count) break;
                JObject kit = kits[start + i];
                string kitname = kit["name"]?.ToString() ?? "";
                string[] a = _kitCardAnchors[i].Split('-');
                if (a.Length < 2) continue;

                string descr = kit["desc"]?.ToString() ?? "";
                if (descr.Length > 60) descr = descr.Substring(0, 60) + "...";

                string image = kit["image"]?.ToString() ?? "";

                int cooldownSec = kit["cooldown"]?.Value<int>() ?? 0;
                int maxUses = kit["maxUses"]?.Value<int>() ?? 0;
                int usesLeft = kit["avail"]?["usesLeft"]?.Value<int>() ?? maxUses;

                string cdText = cooldownSec > 0 ? KitFmtTime(cooldownSec) : "НЕТ";
                string usesText = maxUses > 0 ? Math.Max(0, usesLeft).ToString() : "БЕЗ ЛИМИТА";

                CUIClass.CreatePanel(ref ui, $"owkit_card_{i}", UiCnt, KGlassBg, a[0], a[1], false, 0f, Mat);
                CUIClass.CreatePanel(ref ui, $"owkit_gold_{i}", $"owkit_card_{i}", KGold, "0 0.955", "1 1", false, 0f, Mat);

                CUIClass.CreatePanel(ref ui, $"owkit_icon_{i}", $"owkit_card_{i}", KGlassRow, "0.04 0.93", "0.04 0.93", false, 0f, Mat, "", "0 -58", "58 0");
                if (!string.IsNullOrEmpty(image))
                    CUIClass.CreateImage(ref ui, $"owkit_icon_{i}", Img3(image), "0.08 0.08", "0.92 0.92", 0.2f);

                // звезда избранного — правый верхний угол карточки
                bool isFav = hasFav && fav.Contains(kitname);
                CUIClass.CreateButton(ref ui, $"owkit_fav_{i}", $"owkit_card_{i}", "0 0 0 0",
                    isFav ? "<color=@accent>★</color>" : "<color=@muted>☆</color>", 14,
                    "0.90 0.90", "0.99 0.99", $"octahub_kit_fav {kitname.Replace(" ", "%")}", "", KTWhite, 0f,
                    TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

                CUIClass.CreatePanel(ref ui, $"owkit_tp_{i}", $"owkit_card_{i}", "0 0 0 0", "0 0.70", "0.90 0.92", false, 0f, Mat, "", "72 0", "0 0");
                CUIClass.CreateText(ref ui, $"owkit_title_{i}", $"owkit_tp_{i}", KTWhite,
                    $"<color=@accent><b>{kitname.ToUpper()}</b></color>", 12, "0 0", "1 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                CUIClass.CreatePanel(ref ui, $"owkit_dp_{i}", $"owkit_card_{i}", "0 0 0 0", "0 0.44", "0.985 0.68", false, 0f, Mat, "", "72 0", "0 0");
                CUIClass.CreateText(ref ui, $"owkit_descr_{i}", $"owkit_dp_{i}", KTWhite,
                    $"<size=9><color=@muted>{descr}</color></size>", 9, "0 0", "1 1", TextAnchor.UpperLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");

                CUIClass.CreatePanel(ref ui, $"owkit_mp_{i}", $"owkit_card_{i}", "0 0 0 0", "0 0.26", "0.985 0.42", false, 0f, Mat, "", "72 0", "0 0");
                CUIClass.CreateText(ref ui, $"owkit_meta_{i}", $"owkit_mp_{i}", KTWhite,
                    $"<size=9><color=@muted>КД:</color> <color=@accent>{cdText}</color>   <color=@muted>Лимит:</color> <color=@accent>{usesText}</color></size>",
                    9, "0 0", "1 1", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");

                CUIClass.CreateButton(ref ui, $"owkit_view_{i}", $"owkit_card_{i}", KGlassHov, "<color=@accent>ОБЗОР</color>", 10, "0.035 0.05", "0.31 0.22",
                    $"octahub_kit_view {kitname.Replace(" ", "%")} {st.KitPage}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

                string[] btn = KitsTab_BtnState(kit).Split('%');
                CUIClass.CreateButton(ref ui, $"owkit_claim_{i}", $"owkit_card_{i}", btn[1], $"<b>{btn[0]}</b>", 10, "0.33 0.05", "0.985 0.22",
                    $"octahub_kit_claim {kitname.Replace(" ", "%")} {st.KitPage} 0", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }

            int totalPages = (int)Math.Ceiling(kits.Count / 9.0);
            if (totalPages > 1)
            {
                if (st.KitPage > 0)
                    CUIClass.CreateButton(ref ui, "owkit_prev", UiCnt, "accent.glass", "<color=@accent>‹</color>", 16, "0.885 0.006", "0.93 0.054",
                        $"octahub_kit_page {st.KitPage - 1}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
                if (st.KitPage + 1 < totalPages)
                    CUIClass.CreateButton(ref ui, "owkit_next", UiCnt, "accent.glass", "<color=@accent>›</color>", 16, "0.935 0.006", "0.985 0.054",
                        $"octahub_kit_page {st.KitPage + 1}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");
            }
        }

        private void KitsTab_ItemCells(ref CuiElementContainer ui, string parent, string[] cellAnchors, JToken items, string prefix)
        {
            for (int i = 0; i < cellAnchors.Length; i++)
            {
                string[] a = cellAnchors[i].Split('-');
                CUIClass.CreatePanel(ref ui, $"{prefix}{i}", parent, KGlassRow, a[0], a[1], false, 0.2f, Mat);

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

                    CUIClass.CreateItemIcon(ref ui, $"{prefix}{i}_img", $"{prefix}{i}", shortname, skin, amount, "0.10 0.10", "0.90 0.90", 0.2f);
                }
                catch { }
            }
        }

        private void KitsTab_ViewKit(BasePlayer player, string kitname, int page)
        {
            var ui = new CuiElementContainer();

            // Octa API v1: один вызов отдаёт поля, предметы и availability
            JObject kit = null;
            try { kit = (OctaKits?.Call("Kits_GetKit", player, kitname) as JObject)?["data"] as JObject; } catch { }
            if (kit == null) { UiRefresh(player, UiCnt, UiBot); return; }

            var belt = kit.SelectToken("belt");
            var main = kit.SelectToken("main");
            var wear = kit.SelectToken("wear");

            CUIClass.CreatePanel(ref ui, "owkit_view_inv", UiCnt, KGlassBg, "0.37 0.04", "0.99 0.96", false, 0f, Mat);
            CUIClass.CreatePanel(ref ui, "owkit_inv_gold", "owkit_view_inv", KGold, "0 0.985", "1 1", false, 0f, Mat);

            CUIClass.CreateText(ref ui, "owkit_sec_main", "owkit_view_inv", KGold, "◆ <b>ОСНОВНОЙ ИНВЕНТАРЬ</b>", 11, "0.03 0.93", "0.97 0.97", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_div_main", "owkit_view_inv", KGoldDark, "0.03 0.925", "0.97 0.928", false, 0f, Mat);
            CUIClass.CreatePanel(ref ui, "owkit_inv_main", "owkit_view_inv", "0 0 0 0", "0.02 0.44", "0.98 0.91", false, 0f, Mat);
            KitsTab_ItemCells(ref ui, "owkit_inv_main", _kitMainA, main, "owkit_mitem_");

            CUIClass.CreateText(ref ui, "owkit_sec_belt", "owkit_view_inv", KGold, "◆ <b>БЫСТРЫЙ ДОСТУП</b>", 11, "0.03 0.38", "0.97 0.42", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_div_belt", "owkit_view_inv", KGoldDark, "0.03 0.375", "0.97 0.378", false, 0f, Mat);
            CUIClass.CreatePanel(ref ui, "owkit_inv_belt", "owkit_view_inv", "0 0 0 0", "0.02 0.24", "0.98 0.36", false, 0f, Mat);
            KitsTab_ItemCells(ref ui, "owkit_inv_belt", _kitBeltA, belt, "owkit_bitem_");

            CUIClass.CreateText(ref ui, "owkit_sec_wear", "owkit_view_inv", KGold, "◆ <b>ЭКИПИРОВКА</b>", 11, "0.03 0.19", "0.97 0.23", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_div_wear", "owkit_view_inv", KGoldDark, "0.03 0.185", "0.97 0.188", false, 0f, Mat);
            CUIClass.CreatePanel(ref ui, "owkit_inv_wear", "owkit_view_inv", "0 0 0 0", "0.02 0.05", "0.98 0.17", false, 0f, Mat);
            KitsTab_ItemCells(ref ui, "owkit_inv_wear", _kitClothA, wear, "owkit_witem_");

            CUIClass.CreatePanel(ref ui, "owkit_view_info", UiCnt, KGlassBg, "0.01 0.04", "0.35 0.96", false, 0f, Mat);
            CUIClass.CreatePanel(ref ui, "owkit_v_gold", "owkit_view_info", KGold, "0 0.985", "1 1", false, 0f, Mat);
            CUIClass.CreateText(ref ui, "owkit_v_title", "owkit_view_info", "accent", $"◆ <b>{kitname.ToUpper()}</b>", 18, "0.06 0.91", "0.90 0.97", TextAnchor.MiddleLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_v_div1", "owkit_view_info", KGoldDark, "0.06 0.895", "0.94 0.898", false, 0f, Mat);

            List<string> fv;
            bool isFv = _fav.TryGetValue(player.userID, out fv) && fv.Contains(kitname);
            CUIClass.CreateButton(ref ui, "owkit_v_fav", "owkit_view_info", "0 0 0 0",
                isFv ? "<color=@accent>★</color>" : "<color=@muted>☆</color>", 16,
                "0.90 0.90", "0.98 0.98", $"octahub_kit_fav {kitname.Replace(" ", "%")}", "", KTWhite, 0f,
                TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            int cooldownSec = kit["cooldown"]?.Value<int>() ?? 0;
            int maxUses = kit["maxUses"]?.Value<int>() ?? 0;
            int usesLeft = kit["avail"]?["usesLeft"]?.Value<int>() ?? maxUses;

            string cdText = cooldownSec > 0 ? KitFmtTime(cooldownSec) : "НЕТ";
            string usesText = maxUses > 0 ? Math.Max(0, usesLeft).ToString() : "БЕЗ ЛИМИТА";
            int cost = kit["cost"]?.Value<int>() ?? 0;
            string curLbl = kit["currencyLabel"]?.ToString() ?? "$";
            string costText = cost > 0 ? $"{cost} {curLbl}" : "БЕСПЛАТНО";
            bool hasPerm = (kit["avail"] as JObject)?["code"]?.ToString() != "NO_PERMISSION";
            string permStatus = hasPerm
                ? "<color=@green>Разрешён</color>" : "<color=@red>Требуется привилегия</color>";

            string info = $"<color=@muted>• Кулдаун:</color>  <color=@accent>{cdText}</color>\n" +
                          $"<color=@muted>• Доступно:</color>  <color=@accent>{usesText}</color>\n" +
                          $"<color=@muted>• Стоимость:</color> <color=@accent>{costText}</color>\n" +
                          $"<color=@muted>• Статус:</color>    {permStatus}";
            CUIClass.CreateText(ref ui, "owkit_v_info", "owkit_view_info", KTWhite, info, 12, "0.06 0.65", "0.94 0.88", TextAnchor.UpperLeft, "robotocondensed-bold.ttf", "0 0 0 0", "0 0");
            CUIClass.CreatePanel(ref ui, "owkit_v_div2", "owkit_view_info", KGoldDark, "0.06 0.635", "0.94 0.638", false, 0f, Mat);

            string descr = kit["desc"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(descr)) descr = "Описание набора не указано.";
            CUIClass.CreateText(ref ui, "owkit_v_descr", "owkit_view_info", "muted", descr, 11, "0.06 0.20", "0.94 0.62", TextAnchor.UpperLeft, "robotocondensed-regular.ttf", "0 0 0 0", "0 0");

            CUIClass.CreateButton(ref ui, "owkit_v_back", "owkit_view_info", KGlassRow, "<color=@accent>← НАЗАД</color>", 11, "0.06 0.06", "0.45 0.16",
                $"octahub_kit_page {page}", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            string[] btn = KitsTab_BtnState(kit).Split('%');
            CUIClass.CreateButton(ref ui, "owkit_v_claim", "owkit_view_info", btn[1], $"<b>{btn[0]}</b>", 11, "0.49 0.06", "0.94 0.16",
                $"octahub_kit_claim {kitname.Replace(" ", "%")} {page} 1", "", KTWhite, 0f, TextAnchor.MiddleCenter, "robotocondensed-bold.ttf");

            KitsTab_Destroy(player);
            CuiHelper.AddUi(player, ui);
        }

        private void KitsTab_Destroy(BasePlayer player)
        {
            for (int i = 0; i < 9; i++)
                CuiHelper.DestroyUi(player, $"owkit_card_{i}");
            for (int c = 0; c < KitCats.Length; c++)
                CuiHelper.DestroyUi(player, $"owkit_cat{c}");

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

            var st = St(player);
            int page = 0;
            int.TryParse(args[0], out page);
            st.KitPage = Math.Max(0, page);
            UiRefresh(player, UiCnt, UiBot);
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

            // Octa API v1: единый обработчик + requestId (идемпотентность при даблклике)
            JObject res = null;
            try
            {
                res = OctaKits.Call("Kits_Redeem", player, kitname,
                                    Guid.NewGuid().ToString("N")) as JObject;
            }
            catch { }
            if (res != null && res["ok"]?.Value<bool>() != true)
            {
                var msg = res["msg"]?.ToString();
                if (!string.IsNullOrEmpty(msg))
                    player.ChatMessage($"<color=#cd412b>◆ НАБОР:</color> {msg}");
            }
            // обновляем в любом случае — кулдаун/лимит могли измениться
            if (inView) KitsTab_ViewKit(player, kitname, page);
            else UiRefresh(player, UiCnt, UiBot);
        }

        #endregion

        #region [Boss lore — атмосферные описания из OctaBoss.json]

        // Вкладка «VIP»: стр.2..5 — по уровню OctVIP. Сидим только в пустые страницы.
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
                "<size=16><color=#CD7F32>◆</color> VIP-наборы уровня BRONZE — раздел НАБОРЫ</size>",
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
        private static bool JBool(JObject o, string key, bool def = false) =>
            o?[key]?.ToObject<bool?>() ?? def;

        // короткое имя зверя из пути префаба
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
            var key = JStr(b, "key", "?");
            var name = JStr(b, "name", key.ToUpper());
            var enabled = JBool(b, "enabled", true);

            var escCount = b["escortCount"]?.ToObject<int?>() ?? 0;
            var escLine = escCount > 0
                ? $"<size=15><color=#83b8c7>ОН НЕ ОДИН:</color> с ним свора из {escCount} зверей — билет продаётся только пакетом.</size>"
                : "<size=15><color=#83b8c7>ОН ОДИН:</color> приходит без свиты. Одного достаточно.</size>";

            var mons = JStr(b, "monuments");
            var biomes = JBool(b, "nativeBiome") ? JStr(b, "nativeBiomes", "любой") : "любой";
            var water = JBool(b, "needsWater");
            var where = BiomeRu(biomes)
                + (mons.Length > 0 ? $", кружит около {mons}" : "")
                + (water ? ", у воды" : "");

            var cur = b["rewardCurrency"]?.ToObject<int?>() ?? 0;
            var part = b["rewardParticipation"]?.ToObject<int?>() ?? 0;

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
            // лор генерируется на лету — просто перерисовываем открытые панели
            foreach (var p in BasePlayer.activePlayerList)
                if (_ui.ContainsKey(p.userID)) UiRefresh(p, UiCnt);
        }

        #endregion

        #region [CUI Framework]

        // Вся отрисовка идёт через фреймворк OctaUI — тонкий шим со стабильными сигнатурами.
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
            if (config.sections == null) config.sections = new Configuration.SectionSettings();
            if (config.services == null) config.services = new Configuration.ServicesSettings();
            // Newtonsoft ДОБАВЛЯЕТ элементы из файла в уже инициализированные списки —
            // без дедупликации списки раздуваются при каждой загрузке (PITFALLS).
            if (config.homePromos != null)
                config.homePromos = config.homePromos.GroupBy(p => p?.title).Select(g => g.First()).ToList();
            if (config.homeSlogans != null)
                config.homeSlogans = config.homeSlogans.Distinct().ToList();
            if (config.generalSettings?.eventProviders != null)
                config.generalSettings.eventProviders = config.generalSettings.eventProviders.Distinct().ToList();
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

                [JsonProperty("Open on tab number (0/1=home, 3=kits, 10=events, остальные=info-страницы).")]
                public int openTab { get; set; }

                [JsonProperty("Open only once per wipe.")]
                public bool openOnce { get; set; }

                [JsonProperty("Require permission for each section/tab.")]
                public bool permsEnabled { get; set; }

                [JsonProperty("Custom chat commands (don't delete from list, just rename).")]
                public List<string> customCommands { get; set; }

                [JsonProperty("Event providers (plugin names exposing Events_GetSnapshot API).")]
                public List<string> eventProviders { get; set; } = new List<string> { "OctaBoss" };
            }

            [JsonProperty(PropertyName = "Sections")]
            public SectionSettings sections { get; set; }

            public class SectionSettings
            {
                [JsonProperty("Главная")]
                public SectionCfg home { get; set; } = new SectionCfg { title = "ГЛАВНАЯ" };
                [JsonProperty("Наборы")]
                public SectionCfg kits { get; set; } = new SectionCfg { title = "НАБОРЫ" };
                [JsonProperty("События")]
                public SectionCfg events { get; set; } = new SectionCfg { title = "СОБЫТИЯ" };
                [JsonProperty("Профиль")]
                public SectionCfg profile { get; set; } = new SectionCfg { title = "ПРОФИЛЬ" };
                [JsonProperty("Сервисы")]
                public SectionCfg services { get; set; } = new SectionCfg { title = "СЕРВИСЫ" };
                [JsonProperty("Инфо")]
                public SectionCfg info { get; set; } = new SectionCfg { title = "ИНФО" };
                [JsonProperty("Настройки")]
                public SectionCfg settings { get; set; } = new SectionCfg { title = "НАСТРОЙКИ" };
            }

            public class SectionCfg
            {
                [JsonProperty("Enabled")]
                public bool enabled { get; set; } = true;
                [JsonProperty("Title")]
                public string title { get; set; } = "";
                [JsonProperty("Icon URL (пусто = без иконки)")]
                public string icon { get; set; } = "";
            }

            // Витрина главной: баннер + тизеры-зазывалки в разделы.
            // Пустой список homePromos = витрина выключена, hero-текст на всю высоту.
            [JsonProperty("Home promos (тизеры главной, до 8 — сетка 2×4)")]
            public List<PromoCfg> homePromos { get; set; } = new List<PromoCfg>
            {
                new PromoCfg { title = "ЗАБЕРИ НАБОР",   sub = "бесплатно — просто открой и забери",      section = "kits",    icon = "supply.signal",  spec = "green" },
                new PromoCfg { title = "БОССЫ И СОБЫТИЯ", sub = "кто жив и когда спавн — не пропусти лут", section = "events",  icon = "rocket.launcher", spec = "red" },
                new PromoCfg { title = "ТВОЙ ПРОФИЛЬ",  sub = "наигранное время, статистика, статус",   section = "profile", icon = "hoodie",         spec = "accent" },
                new PromoCfg { title = "ИНФО И ПРАВИЛА", sub = "гайды, команды, правила сервера",        section = "info",    icon = "note",           spec = "blue" },
            };

            [JsonProperty("Home slogans (слоганы баннера, когда нет живых событий)")]
            public List<string> homeSlogans { get; set; } = new List<string>
            {
                "Главная — только начало: кликай по разделам слева",
                "Тут живут наборы, боссы, телепорты и твоя статистика",
                "Скучно? Открой СОБЫТИЯ — там всегда что-то горит",
            };

            public class PromoCfg
            {
                [JsonProperty("Title")]   public string title { get; set; } = "";
                [JsonProperty("Sub")]     public string sub { get; set; } = "";
                [JsonProperty("Section (kits/events/profile/services/info/settings)")]
                                          public string section { get; set; } = "info";
                [JsonProperty("Icon (item shortname, пусто = без иконки)")]
                                          public string icon { get; set; } = "";
                [JsonProperty("Color (accent/red/green/blue)")]
                                          public string spec { get; set; } = "accent";
            }

            [JsonProperty(PropertyName = "Services Rows")]
            public ServicesSettings services { get; set; }

            public class ServicesSettings
            {
                [JsonProperty("Строки сервисов (клик выполняет чат-команду)")]
                public List<ServiceRow> rows { get; set; } = new List<ServiceRow>();
            }

            public class ServiceRow
            {
                [JsonProperty("Enabled")] public bool enabled { get; set; } = true;
                [JsonProperty("Title")] public string title { get; set; } = "";
                [JsonProperty("Desc")] public string desc { get; set; } = "";
                [JsonProperty("Icon URL (пусто = ромб)")] public string icon { get; set; } = "";
                [JsonProperty("Chat command (без /)")] public string chatCommand { get; set; } = "";
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

            // Настройки старых вкладок — теперь это инфо-страницы разделов Инфо и Главной.
            // tab1 = главная (hero-текст), tab2..11 = инфо-страницы.
            [JsonProperty(PropertyName = "Tab #1 Settings")]
            public TabSettings tab1Settings { get; set; }
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

            public static Configuration CreateConfig()
            {
                return new Configuration
                {
                    generalSettings = new GeneralSettings
                    {
                        openOnConnect = true,
                        openTab = 1,
                        openOnce = false,
                        permsEnabled = false,
                        customCommands = new List<string>
                            {
                                "info", "rules", "kits", "wipe", "vipinfo",
                                "discord", "shop", "admins", "news", "bosses", "commands"
                            }
                    },

                    sections = new SectionSettings(),

                    services = new ServicesSettings
                    {
                        rows = new List<ServiceRow>
                        {
                            new ServiceRow { title = "ЭКИПИРОВКИ", desc = "личные пресеты инвентаря при респавне — /loadout", chatCommand = "loadout" },
                            new ServiceRow { title = "АПГРЕЙД БАЗЫ", desc = "улучшить все постройки рядом — /upall", chatCommand = "upall" },
                            new ServiceRow { title = "РЕМУВЕР", desc = "снять свою постройку — /remove", chatCommand = "remove" },
                            new ServiceRow { title = "РЮКЗАК", desc = "дополнительный инвентарь — /backpackgui", chatCommand = "backpackgui" },
                            new ServiceRow { title = "VIP СТАТУС", desc = "твоя привилегия и её срок — /vip", chatCommand = "vip" },
                            new ServiceRow { title = "РАСПИСАНИЕ БОССОВ", desc = "кто и когда появится — в чат", chatCommand = "bosstimer" },
                            new ServiceRow { title = "ТЕЛЕПОРТЫ", desc = "справка по командам телепортации — /tphelp", chatCommand = "tphelp" },
                        }
                    },

                    buttonSettings = new ButtonSettings
                    {
                        btnFontStyle = "robotocondensed-bold.ttf",
                        closeBtnText = "✕",
                        mainAlign = TextAnchor.MiddleCenter,
                        btnIconAnchor = new List<string> { "0 0", "1 1" }
                    },

                    titleSettings = new TitleSettings
                    {
                        titleText = "<size=55><color=#E8B84B>OCTAVIAN</color></size>",
                        titleColor = "1 1 1 1",
                        titleFontStyle = "robotocondensed-bold.ttf",
                        titleOutlineColor = "0 0 0 1",
                        titleOutlineThick = "1.5",
                    },

                    tab1Settings = new TabSettings
                    {
                        btnEnabled = true,
                        btnTitle = " ГЛАВНАЯ",
                        btnIcon = "",
                        mainFontSize = 12,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-bold.ttf",
                        mainAlign = TextAnchor.UpperLeft,
                        textLines = new List<string>
                        {
                            "[H]ДОБРО ПОЖАЛОВАТЬ НА <color=#E8B84B>OCTAVIAN</color>",
                            "[h]рейты x5 · вайпы карт еженедельно · онлайн {online}/{maxplayers} · до вайпа {wipecountdown}",
                            "<size=18><color=#E8B84B>ПОЧЕМУ У НАС ИНТЕРЕСНО</color></size>",
                            "<size=15><color=#E8B84B>◆ БОССЫ-ЗВЕРИ</color> — наш флагман: гигантские звери с AI-управлением.</size>",
                            "<size=15>   Маркер на карте покажет логово — награда вносит больше урона.</size>",
                            "<size=15><color=#E8B84B>◆ AI-АНТИЧИТ</color> — система анализа поведения отлавливает читеров.</size>",
                            "<size=15><color=#E8B84B>◆ СВОИ ПЛАГИНЫ</color> — наборы, телепорты, апгрейд баз, VIP — под баланс сервера.</size>",
                            "",
                            "<size=18><color=#E8B84B>ЧТО ГДЕ ЛЕЖИТ</color></size>",
                            "<size=14><color=#83b8c7>НАБОРЫ</color> — киты · <color=#83b8c7>СОБЫТИЯ</color> — живые боссы · <color=#83b8c7>ПРОФИЛЬ</color> — статус и дома</size>",
                            "<size=14><color=#83b8c7>СЕРВИСЫ</color> — телепорты и команды · <color=#83b8c7>ИНФО</color> — правила, VIP, дискорд · <color=#83b8c7>НАСТРОЙКИ</color> — под себя</size>",
                            "[F]первые шаги: /kit — набор · /sethome — точка дома · /tpr ник — телепорт к другу"
                        },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string> { "0 0", "1 1" }
                    },

                    tab2Settings = new TabSettings
                    {
                        btnEnabled = true,
                        btnTitle = " ПРАВИЛА",
                        btnIcon = "",
                        mainFontSize = 12,
                        mainFontColor = "1 1 1 1",
                        fontOutlineColor = "0 0 0 1",
                        fontOutlineThick = "0.5",
                        mainFontStyle = "robotocondensed-regular.ttf",
                        mainAlign = TextAnchor.UpperLeft,
                        textLines = new List<string>
                        {
                            "[H]ПРАВИЛА СЕРВЕРА",
                            "[h]незнание правил не освобождает от ответственности",
                            "<size=17><color=#83b8c7>1.</color> Читы, макросы, багоюз — перманентный бан без предупреждений.</size>",
                            "<size=17><color=#83b8c7>2.</color> Уважай игроков и администрацию — токсичность карается.</size>",
                            "<size=17><color=#83b8c7>3.</color> Инсайдерство и кража у своей команды запрещены.</size>",
                            "<size=17><color=#83b8c7>4.</color> Оффлайн-рейды разрешены — это Rust.</size>",
                            "<size=17><color=#83b8c7>5.</color> Реклама сторонних серверов — бан.</size>",
                            "",
                            "<size=14>Споры решает администрация — вкладка АДМИНЫ или ДИСКОРД.</size>"
                        },
                        tabImageUrl = "",
                        tabImageAnchor = new List<string> { "0 0", "1 1" }
                    },

                    tab3Settings = new TabSettings { btnEnabled = true, btnTitle = " НАБОРЫ", btnIcon = "", mainFontSize = 15, mainFontColor = "1 1 1 1", fontOutlineColor = "0 0 0 1", fontOutlineThick = "0", mainFontStyle = "robotocondensed-bold.ttf", mainAlign = TextAnchor.UpperLeft, textLines = new List<string> { "<size=40><color=#E8B84B>НАБОРЫ</color></size>" }, tabImageUrl = "", tabImageAnchor = new List<string> { "0 0", "1 1" } },
                    tab4Settings = new TabSettings { btnEnabled = true, btnTitle = " ВАЙПЫ", btnIcon = "", mainFontSize = 14, mainFontColor = "1 1 1 1", fontOutlineColor = "0 0 0 1", fontOutlineThick = "0.5", mainFontStyle = "robotocondensed-regular.ttf", mainAlign = TextAnchor.UpperLeft, textLines = new List<string> { "[H]РАСПИСАНИЕ ВАЙПОВ", "[h]карта вайпается еженедельно · до вайпа {wipecountdown}", "<size=17>Вайп карты — каждый четверг в 18:00 МСК.</size>", "<size=17>Чертежи (BP) не вайпаются — прогресс сохраняется.</size>", "", "<size=14>Точное время до вайпа — виджет на ГЛАВНОЙ.</size>" }, tabImageUrl = "", tabImageAnchor = new List<string> { "0 0", "1 1" } },
                    tab5Settings = new TabSettings { btnEnabled = true, btnTitle = " VIP", btnIcon = "", mainFontSize = 14, mainFontColor = "1 1 1 1", fontOutlineColor = "0 0 0 1", fontOutlineThick = "0.5", mainFontStyle = "robotocondensed-regular.ttf", mainAlign = TextAnchor.UpperLeft, textLines = new List<string> { "[H]VIP-ПРИВИЛЕГИИ", "[h]стрелки внизу — перелистывай уровни", "<size=17>На сервере 4 уровня привилегий: BRONZE, SILVER, GOLD, BRILLIANT.</size>", "<size=17>Текущий статус — в разделе ПРОФИЛЬ.</size>", "", "<size=14>Страницы уровней генерируются автоматически — листай стрелками.</size>" }, tabImageUrl = "", tabImageAnchor = new List<string> { "0 0", "1 1" } },
                    tab6Settings = new TabSettings { btnEnabled = true, btnTitle = " ДИСКОРД", btnIcon = "", mainFontSize = 14, mainFontColor = "1 1 1 1", fontOutlineColor = "0 0 0 1", fontOutlineThick = "0.5", mainFontStyle = "robotocondensed-regular.ttf", mainAlign = TextAnchor.MiddleCenter, textLines = new List<string> { "<size=40><color=#E8B84B>ДИСКОРД</color></size>", "", "<size=17>Ссылка на сервер — в чате командой <color=#83b8c7>/discord</color></size>" }, tabImageUrl = "", tabImageAnchor = new List<string> { "0 0", "1 1" } },
                    tab7Settings = new TabSettings { btnEnabled = true, btnTitle = " МАГАЗИН", btnIcon = "", mainFontSize = 14, mainFontColor = "1 1 1 1", fontOutlineColor = "0 0 0 1", fontOutlineThick = "0.5", mainFontStyle = "robotocondensed-regular.ttf", mainAlign = TextAnchor.MiddleCenter, textLines = new List<string> { "<size=40><color=#E8B84B>МАГАЗИН</color></size>", "", "<size=17>Донат-привилегии — в разделе VIP.</size>" }, tabImageUrl = "", tabImageAnchor = new List<string> { "0 0", "1 1" } },
                    tab8Settings = new TabSettings { btnEnabled = true, btnTitle = " АДМИНЫ", btnIcon = "", mainFontSize = 14, mainFontColor = "1 1 1 1", fontOutlineColor = "0 0 0 1", fontOutlineThick = "0.5", mainFontStyle = "robotocondensed-regular.ttf", mainAlign = TextAnchor.UpperLeft, textLines = new List<string> { "[H]АДМИНИСТРАЦИЯ", "<size=17>Админы онлайн: следи за чатом — они отмечены цветом.</size>", "<size=17>Жалобы — командой <color=#83b8c7>/report</color> или в ДИСКОРД.</size>" }, tabImageUrl = "", tabImageAnchor = new List<string> { "0 0", "1 1" } },
                    tab9Settings = new TabSettings { btnEnabled = true, btnTitle = " НОВОСТИ", btnIcon = "", mainFontSize = 14, mainFontColor = "1 1 1 1", fontOutlineColor = "0 0 0 1", fontOutlineThick = "0.5", mainFontStyle = "robotocondensed-regular.ttf", mainAlign = TextAnchor.UpperLeft, textLines = new List<string> { "[H]НОВОСТИ ОСТРОВА", "<size=17>Свежие обновления — в ДИСКОРД и на ГЛАВНОЙ.</size>" }, tabImageUrl = "", tabImageAnchor = new List<string> { "0 0", "1 1" } },
                    tab10Settings = new TabSettings { btnEnabled = true, btnTitle = " СОБЫТИЯ", btnIcon = "", mainFontSize = 14, mainFontColor = "1 1 1 1", fontOutlineColor = "0 0 0 1", fontOutlineThick = "0.5", mainFontStyle = "robotocondensed-regular.ttf", mainAlign = TextAnchor.UpperLeft, textLines = new List<string> { "[H]СОБЫТИЯ", "<size=17>Живые данные по боссам — в разделе СОБЫТИЯ.</size>" }, tabImageUrl = "", tabImageAnchor = new List<string> { "0 0", "1 1" } },
                    tab11Settings = new TabSettings { btnEnabled = true, btnTitle = " КОМАНДЫ", btnIcon = "", mainFontSize = 14, mainFontColor = "1 1 1 1", fontOutlineColor = "0 0 0 1", fontOutlineThick = "0.5", mainFontStyle = "robotocondensed-regular.ttf", mainAlign = TextAnchor.UpperLeft, textLines = new List<string> { "[H]КОМАНДЫ СЕРВЕРА", "<size=16><color=#83b8c7>/octahub</color> — эта панель · <color=#83b8c7>/kit</color> — наборы</size>", "<size=16><color=#83b8c7>/sethome имя</color> · <color=#83b8c7>/home имя</color> · <color=#83b8c7>/tpr ник</color> · <color=#83b8c7>/tpa</color></size>", "<size=16><color=#83b8c7>/bosstimer</color> · <color=#83b8c7>/bossinfo</color> · <color=#83b8c7>/report</color> · <color=#83b8c7>/discord</color></size>" }, tabImageUrl = "", tabImageAnchor = new List<string> { "0 0", "1 1" } },
                };
            }
        }

        #endregion
    }
}
