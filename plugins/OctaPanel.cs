using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins
{
    // Ребрендинг ZenPanelPlus 2.5.0 (автор оригинала: FineINK)
    // Переименованы идентификаторы, пермишены, команды и дефолтные тексты — логика сохранена.
    [Info("OctaPanel", "Octavian", "1.1.0")]
    [Description("Инфопанель Octavian: часы, онлайн, события, советы, экономика, PvP (Kills/Deaths/KD)")]
    public class OctaPanel : RustPlugin
    {
        #region Dependencies

        [PluginReference] private Plugin ImageLibrary, ServerRewards, Economics;

        #endregion

        #region Fields

        private TOD_Sky _sky;

        private readonly Dictionary<string, string> _staticIcons = new Dictionary<string, string>
        {
            { "OctaPanelIconTime", "https://i.imgur.com/CBKTj8B.png" },
            { "OctaPanelIconOnline", "https://i.imgur.com/lWoUIw2.png" },
            { "OctaPanelBckgr", "https://i.imgur.com/eAUEICV.png" }
        };

        private class EventIcon
        {
            public string Url;
            public string Name;
            public Type Type;
            public int Count;
            public Func<BaseNetworkable, bool> Test;
            // встроенный PNG (base64) — для иконок без внешней ссылки; грузится через AddImageData
            public string PngB64;
            // если задан — считаем по префиксу ShortPrefabName (классы deep sea internal, typeof недоступен)
            public string PrefabPrefix;

            public EventIcon() => Count = 0;
        }

        private class UIDrawInfo
        {
            public CuiElementContainer UI;
            public string Name;

            public UIDrawInfo(CuiElementContainer ui, string name)
            {
                UI = ui;
                Name = name;
            }
        }

        private readonly List<EventIcon> _eventIcons = new List<EventIcon>
        {
            new EventIcon
            {
                Name = "OctaPanelIconCargoPlane",
                Url = "https://i.imgur.com/CL41EJS.png",
                Type = typeof(CargoPlane),
                Test = e => e is CargoPlane
            },
            new EventIcon
            {
                Name = "OctaPanelIconPatrolHelicopter",
                Url = "https://i.imgur.com/HtAifod.png",
                Type = typeof(BaseHelicopter),
                Test = e => e is BaseHelicopter
            },
            new EventIcon
            {
                Name = "OctaPanelIconTank",
                Url = "https://i.imgur.com/j5cMDpt.png",
                Type = typeof(BradleyAPC),
                Test = e => e is BradleyAPC
            },
            new EventIcon
            {
                Name = "OctaPanelIconCH47",
                Url = "https://i.imgur.com/jRIF5y8.png",
                Type = typeof(CH47Helicopter),
                Test = e => e is CH47Helicopter
            },
            new EventIcon
            {
                Name = "OctaPanelIconCargoShip",
                Url = "https://i.imgur.com/xU8IUWO.png",
                Type = typeof(CargoShip),
                Test = e => e is CargoShip
            },
            // Deep Sea (Livestock Update): призрачные корабли и плавучий город.
            // Классы GhostShip/FloatingCity — internal, поэтому считаем по префиксу префаба.
            new EventIcon
            {
                Name = "OctaPanelIconGhostShip",
                PngB64 = IconGhostShipPng,
                PrefabPrefix = "ghostship",
                Test = e => MatchesPrefab(e, "ghostship")
            },
            new EventIcon
            {
                Name = "OctaPanelIconFloatingCity",
                PngB64 = IconFloatingCityPng,
                PrefabPrefix = "floatingcity",
                Test = e => MatchesPrefab(e, "floatingcity")
            }
        };

        // shortname вида ghostship_c — карты-маркеры (*mapmarker) пропускаем
        private static bool MatchesPrefab(BaseNetworkable e, string prefix)
        {
            var spn = e?.ShortPrefabName;
            return spn != null && spn.StartsWith(prefix, StringComparison.Ordinal)
                && !spn.Contains("mapmarker");
        }

        // силуэты 128x128, белые на прозрачном — в стиле остальных иконок панели
        private const string IconGhostShipPng =
            "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAADFElEQVR4nO2cWW7DMAwFraCXyf0Pk9yGRT8CdHO8iRLJN/PXpEAScvQsycuyAAAAAAAAAAAAAAAAAAAAAABAHdpSHDOz36+11sr/7r3cdv8nlOSmNPK/83g83r6vQlkBYB+lBVhLAUa/iACwDQKIIyfA8/mc/RVCIScA/AQBxJESgPgXFwD+ggDiyAhA/P/Px8rrIHIWUiYBQFiAkfFvG2cht94fjYQAo7Bgzd0DAoiLUl6AUfFvgZp6hPICRG2aBRFGWoBqzTzDTT3+TTwFSgug0sSUO4GRdsrMzBQ/u3QCjJj9W/LRP0WAqNfrm+hcYEoCVBg5VuA3hD0EXE2BjNf92yShQgoQvQlWZPSHFiDjKL7KDLHCCjBLHEt2Ore0AKSAuADRmmDFRn85ATwTwwY2f+RnhRdAcWNoJKEEiFp0C/q9yglwJQU8ksIEtodTCDATKzz6UwnAklBcgHcoyiEpgGKjzTkFUgkA/UkvgEIqmGMKpBNAoeEjSSeAKuaUAikFeKUAaSAqgCrmkAJpBWD0iwuginVOAQQQJ/1Dou73e5u1j94K3E5GAoiTXoC1yeDXCFkbJa0Ty0R6JVx6AeAaJQTItCRsHenxfUoIAIkEeGfvFaszpUAkSABxWvVHxMx6FI0FegSO60bQkeVIxAKowyFAHAQQ5iu9EUCU1/mE9CeD4NqcjQQQbf5rQo4AIqyt1jgEFGdrP4IEKMyePRoSQKzxvzfjDu/MVb9fvjL/7cQePgSwnVsL5gDFOHqq/ZQApEBMzvSFBBAX47QApEAszvbjUgIgQQz+LO2+/b3VIw4B4lwWgBSYy7v67+kNCVCUvQOziwCkwBx61L1bAiDBWLgzCJZwApACY+hZZxJAnO4CkAK+9K6vSwIggQ8edXURgItGfPCoa1ejaLzwMpDmj4VnBEEXWhQbFSeOFqBuJIA4tyjHIrU5mgAAAAAWTz4BieOcXf5l1mgAAAAASUVORK5CYII=";

        private const string IconFloatingCityPng =
            "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAACB0lEQVR4nO3dQW7bMBRAQUvo/a/MblqgyCKtJVWm+GaWBgI7/A+MzcjQ6wUAAAAArG97LWqMMb4+tm3bsr/vUfvhn2QJAogTQJwA4vbKG8DvHi9bLoC/DVkECwfwr8MVwYIBvDtUESwUwNFhDhE8P4CzQxzxCB4fAOcIIE4AcQKIE0CcAOIEECeAOAHECSBOAHECiBNA3I+7n/DMf99c1389O0CcAOIEECeAOAHECSBOAHECiBNAnADiBBAngDgBxAkgTgBxAogTQJwA4gQQJ4A4AcQJIE4AcQKIE0CcAOIEECeAOAHECSDusu+e1e/A+QlXfHfQDhAngDgBxAkgTgBxAogTQJwA4gQQJ4A4AcQJIG6r3DDyU88/Jr9Rph0gTgBxAogTQJwA4gQQJ4A4AcQJIE4AcQKIE0CcAOIEECeAOAHEXX7nUN8S/v9reeWFIpcEYOj3+nO9z8ZwKgCD/7zfMzgawuH3AIY/l6PzOBSA4c/pyFzeDsDw5/bufN4KwPCf4Z05bbMFcMe18J82JlpHB0Fx+2zb/+p/ZsZk62gHiBNAnADiBBC3z/bxbPWPgdtk62gHiBNA3D7T9rX69j/jOu6zvPjK8Gdbx32GF18b/uuXGdbxJ3asoMnpbzqlAAAAAElFTkSuQmCC";

        private readonly Dictionary<string, Timer> _timers = new Dictionary<string, Timer>
        {
            {"clockTimer", null},
            {"iconTimer", null},
            {"onlineTimer", null},
            {"launchTimer", null},
            {"panelTimer", null},
            {"adviceTimer", null},
            {"economicsTimer", null},
            {"statsUiTimer", null}
        };

        private enum PanelOffsetType
        {
            Timer,
            Online,
            Icons
        }

        private class PanelOffsets
        {
            private readonly Dictionary<BasePlayer, Dictionary<PanelOffsetType, float>> _widths =
                new Dictionary<BasePlayer, Dictionary<PanelOffsetType, float>>();

            public void SetWidth(BasePlayer player, PanelOffsetType type, float width = 0f)
            {
                if (!_widths.ContainsKey(player))
                    _widths.Add(player, new Dictionary<PanelOffsetType, float>());

                if (!_widths[player].ContainsKey(type))
                    _widths[player].Add(type, 0f);

                _widths[player][type] = width;
            }

            public float GetWidth(BasePlayer player, PanelOffsetType type)
            {
                if (!_widths.ContainsKey(player)) return 0f;
                return !_widths[player].ContainsKey(type) ? 0f : _widths[player][type];
            }
        }

        private readonly PanelOffsets _offsets = new PanelOffsets();

        private bool _panelReady;
        private string _currTime;
        private string _currOnline;
        private string _currAdvice = "";
        private int _currAdviceIdx;

        #endregion

        #region Configuration

        public class Configuration
        {
            [JsonProperty(PropertyName = "Версия конфига (не менять)")]
            public int version;

            [JsonProperty(PropertyName = "Название сервера (верхняя строка)")]
            public string serverName;

            [JsonProperty(PropertyName = "Команда настройки")]
            public string cmdOptions;

            [JsonProperty(PropertyName = "Советы")]
            public ConfigurationAdvice advice;

            [JsonProperty(PropertyName = "Логотип")]
            public ConfigurationLogo logo;

            [JsonProperty(PropertyName = "Скрытие элементов")]
            public ConfigurationHide hide;

            [JsonProperty(PropertyName = "Частота обновления")]
            public ConfigurationTimers timers;

            [JsonProperty(PropertyName = "Экономика")]
            public ConfigurationEconomic economic;

            [JsonProperty(PropertyName = "PvP панель")]
            public ConfigurationPvP pvp;
        }

        public class ConfigurationPvP
        {
            [JsonProperty(PropertyName = "Показывать")]
            public bool allow;

            [JsonProperty(PropertyName = "AnchorMin")]
            public string anchorMin;

            [JsonProperty(PropertyName = "AnchorMax")]
            public string anchorMax;

            [JsonProperty(PropertyName = "Фоновый цвет панели")]
            public string panelColor;
        }

        public class ConfigurationEconomic
        {
            [JsonProperty(PropertyName = "Ссылка на иконку (256x256)")]
            public string url;

            [JsonProperty(PropertyName = "Отображать")]
            public bool allow;

            [JsonProperty(PropertyName = "ServerRewards")]
            public bool serverRewards;

            [JsonProperty(PropertyName = "Economics")]
            public bool economics;
        }

        public class ConfigurationAdvice
        {
            [JsonProperty(PropertyName = "Показывать")]
            public bool allow;

            [JsonProperty(PropertyName = "Список")]
            public List<string> list;
        }

        public class ConfigurationLogo
        {
            [JsonProperty(PropertyName = "Включен")]
            public bool allow;

            [JsonProperty(PropertyName = "Ссылка")]
            public string url;

            [JsonProperty(PropertyName = "Размер X")]
            public int x;

            [JsonProperty(PropertyName = "Размер Y")]
            public int y;

            [JsonProperty(PropertyName = "Отступ верх-право X")]
            public int offsetX;

            [JsonProperty(PropertyName = "Отступ верх-право Y")]
            public int offsetY;
        }

        public class ConfigurationHide
        {
            [JsonProperty(PropertyName = "Разрешить")]
            public bool allow;

            [JsonProperty(PropertyName = "Только по пермишену octapanel.canhide")]
            public bool usePermision;

            [JsonProperty(PropertyName = "Разрешить скрывать логотип")]
            public bool canHideLogo;
        }

        public class ConfigurationTimers
        {
            [JsonProperty(PropertyName = "Часы")] public float clock;

            [JsonProperty(PropertyName = "Онлайн")]
            public float online;

            [JsonProperty(PropertyName = "Советы")]
            public float advice;

            [JsonProperty(PropertyName = "Экономика")]
            public float economics;

            [JsonProperty(PropertyName = "Обновление UI")]
            public float main;
        }

        private Configuration _config;

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration
            {
                version = 1,
                serverName = "◆ OCTAVIAN",
                cmdOptions = "panel",
                advice = new ConfigurationAdvice
                {
                    allow = true,
                    list = new List<string>
                    {
                        "<color=#E8B84B>Добро пожаловать на OCTAVIAN!</color>",
                        "Построй базу мечты — рейды начинаются с тебя!",
                        "PvP статистика учитывается в реальном времени.",
                        "Телепорты — <color=#F2CE7E>/octatp</color>, меню — <color=#F2CE7E>/admin</color>"
                    }
                },
                logo = new ConfigurationLogo
                {
                    // логотип выключен по умолчанию — подставь свою картинку в конфиге
                    allow = false,
                    url = "https://i.imgur.com/c2e8gYD.png",
                    x = 150,
                    y = 29,
                    offsetX = 10,
                    offsetY = 5
                },
                hide = new ConfigurationHide
                {
                    allow = true,
                    canHideLogo = false,
                    usePermision = false
                },
                timers = new ConfigurationTimers
                {
                    online = 3f,
                    clock = 1f,
                    advice = 10f,
                    economics = 2f,
                    main = 60f
                },
                economic = new ConfigurationEconomic
                {
                    url = "https://i.imgur.com/AmVZH1s.png",
                    allow = true,
                    economics = true,
                    serverRewards = true
                },
                pvp = new ConfigurationPvP
                {
                    allow = true,
                    // ЭТО МЕСТО ТЕПЕРЬ ИСПОЛЬЗУЕТСЯ ДЛЯ СОВЕТОВ (мы поменяли панели местами)
                    anchorMin = "0.015 0.83",
                    anchorMax = "0.19 0.955",
                    panelColor = "0 0 0 0.55"
                }
            };
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Data (Per-Player UI + PvP Stats)

        private readonly List<string> _layersToHide = new List<string>
        {
            "IconsAll",
            "OctaPanelIconCargoPlane",
            "OctaPanelIconPatrolHelicopter",
            "OctaPanelIconTank",
            "OctaPanelIconCH47",
            "OctaPanelIconCargoShip",
            "OctaPanelIconGhostShip",
            "OctaPanelIconFloatingCity",
            "Clock",
            "Online",
            "Advice",
            "Logo",
            "Money",
            "PvP",
            "All"
        };

        public class PlayerUIOptions
        {
            public int clockType = 0; // 0 - big, 1 - small
        }

        public class PlayerUIStateData
        {
            public class Row
            {
                public List<string> hide;
                public PlayerUIOptions options;

                public Row()
                {
                    hide = new List<string>();
                    options = new PlayerUIOptions();
                }
            }

            public Dictionary<ulong, Row> data = new Dictionary<ulong, Row>();

            public bool Has(BasePlayer player) => data.ContainsKey(player.userID);

            private void Ensure(BasePlayer player)
            {
                if (!data.ContainsKey(player.userID))
                    data.Add(player.userID, new Row());
            }

            public List<string> GetHide(BasePlayer player)
            {
                Ensure(player);
                return data[player.userID].hide;
            }

            public PlayerUIOptions GetOptions(BasePlayer player)
            {
                Ensure(player);
                return data[player.userID].options;
            }
        }

        public PlayerUIStateData playerUI;

        private void LoadPlayerUIData()
        {
            try
            {
                playerUI = Interface.Oxide.DataFileSystem.ReadObject<PlayerUIStateData>($"{Name}.Players");
            }
            catch
            {
                // ignored
            }

            if (playerUI == null)
            {
                playerUI = new PlayerUIStateData();
                SavePlayerUIData();
            }
        }

        private void SavePlayerUIData() =>
            Interface.Oxide.DataFileSystem.WriteObject($"{Name}.Players", playerUI);

        // ==== PvP Stats ====

        public class PlayerStats
        {
            public int Kills;
            public int Deaths;

            [JsonIgnore] public float KDRatio => Deaths == 0 ? Kills : (float)Math.Round((float)Kills / Deaths, 2);
        }

        private Dictionary<ulong, PlayerStats> _playerStats;

        private void LoadStats()
        {
            try
            {
                _playerStats = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerStats>>($"{Name}.Stats");
            }
            catch
            {
                // ignored
            }

            if (_playerStats == null)
                _playerStats = new Dictionary<ulong, PlayerStats>();
        }

        private void SaveStats() =>
            Interface.Oxide.DataFileSystem.WriteObject($"{Name}.Stats", _playerStats);

        private PlayerStats GetStats(ulong userId)
        {
            if (!_playerStats.ContainsKey(userId))
                _playerStats[userId] = new PlayerStats();
            return _playerStats[userId];
        }

        #endregion

        #region Helpers

        private string GetImg(string name)
        {
            try
            {
                return (string)ImageLibrary?.Call("GetImage", name) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static int CountComponentsOf(Type name) =>
            UnityEngine.Object.FindObjectsOfType(name).ToList().Count;

        private static List<BasePlayer> GetActivePlayersSafe()
        {
            var ret = new List<BasePlayer>();
            foreach (var p in BasePlayer.activePlayerList)
                if (p.IsValid() && p.net?.connection != null)
                    ret.Add(p);
            return ret;
        }

        private static void DestroyAllUIFor(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, "OctaPanelUIMain");
            CuiHelper.DestroyUi(player, "OctaPanelUITimerText");
            CuiHelper.DestroyUi(player, "OctaPanelUIOnlineText");
            CuiHelper.DestroyUi(player, "OctaPanelUIIcons");
            CuiHelper.DestroyUi(player, "OctaPanelUIAdvice");
            CuiHelper.DestroyUi(player, "OctaPanelUILogo");
            CuiHelper.DestroyUi(player, "OctaPanelUIEconomics");
            CuiHelper.DestroyUi(player, "OctaPanelUIPvP");
            CuiHelper.DestroyUi(player, "OctaPanelSettings");
        }

        private static void DestroyAllUI(BasePlayer specific = null)
        {
            if (specific == null)
            {
                foreach (var p in GetActivePlayersSafe())
                    DestroyAllUIFor(p);
            }
            else DestroyAllUIFor(specific);
        }

        private static void DestroyTimers(IEnumerable<Timer> dtimers)
        {
            foreach (var t in dtimers)
            {
                if (t == null || t.Destroyed) continue;
                t.Destroy();
            }
        }

        private static CuiPanel BasePanel390x40TopLeft() => new CuiPanel
        {
            Image = { Color = "0 0 0 0" },
            RectTransform =
            {
                AnchorMin = "0 1",
                AnchorMax = "0 1",
                OffsetMin = "0 -40",
                OffsetMax = "390 0"
            }
        };

        private bool CanHide(BasePlayer player) =>
            _config.hide.allow && (!_config.hide.usePermision || permission.UserHasPermission(player.UserIDString, $"{Name}.canhide"));

        private bool IsHidden(BasePlayer player, string layer) =>
            CanHide(player) && playerUI.GetHide(player).Contains(layer);

        #endregion

        #region Drawing (UI)

        private void DrawUIForPlayer(Func<BasePlayer, UIDrawInfo> builder, BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            if (player.IsReceivingSnapshot)
            {
                timer.Once(1f, () => DrawUIForPlayer(builder, player));
                return;
            }

            var info = builder(player);
            if (string.IsNullOrEmpty(info.Name)) return;

            var json = info.UI?.ToJson();
            CuiHelper.DestroyUi(player, info.Name);
            if (json == null) return;
            CuiHelper.AddUi(player, json);
        }

        private IEnumerator DrawAllCoroutine(Func<BasePlayer, UIDrawInfo> builder)
        {
            var list = BasePlayer.activePlayerList.ToArray().ToList();
            foreach (var p in list)
            {
                DrawUIForPlayer(builder, p);
                yield return null;
            }
            yield return null;
        }

        private void RedrawUIForAllPlayers(Func<BasePlayer, UIDrawInfo> builder, BasePlayer only = null)
        {
            if (only != null)
            {
                DrawUIForPlayer(builder, only);
                return;
            }

            Rust.Global.Runner.StartCoroutine(DrawAllCoroutine(builder));
        }

        private void RedrawAllUIComponents(BasePlayer player = null)
        {
            RedrawUIForAllPlayers(DrawMainPanelUI, player);
            RedrawUIForAllPlayers(DrawClockUI, player);
            RedrawUIForAllPlayers(DrawOnlineUI, player);
            RedrawUIForAllPlayers(DrawIconsUI, player);
            if (_config.economic.allow) RedrawUIForAllPlayers(DrawEconomicsUI, player);

            // ВНИЗУ — теперь СТАТИСТИКА (раньше тут были советы)
            if (_config.pvp.allow) RedrawUIForAllPlayers(DrawPvPStatsUI, player);

            // СОВЕТЫ — наверху слева на месте старой PvP-панели
            if (_config.advice.allow) RedrawUIForAllPlayers(DrawAdviceUI, player);

            if (_config.logo.allow) RedrawUIForAllPlayers(DrawLogoUI, player);
        }

        private UIDrawInfo DrawMainPanelUI(BasePlayer player)
        {
            const string uiName = "OctaPanelUIMain";
            if (IsHidden(player, "All")) return new UIDrawInfo(null, uiName);

            var ui = new CuiElementContainer
            {
                { BasePanel390x40TopLeft(), "Hud", uiName },
                new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Color = "1 1 1 1",
                            Png = GetImg("OctaPanelBckgr")
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 1.1",
                            AnchorMax = "0 1.1"
                        }
                    }
                }
            };

            // Server name title (left, above panel slightly)
            ui.Add(new CuiElement
            {
                Parent = uiName,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = _config.serverName,
                        Color = "1 1 1 0.85",
                        FontSize = 12,
                        Align = TextAnchor.UpperLeft
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "4 2",
                        OffsetMax = "300 24"
                    }
                }
            });

            if (!IsHidden(player, "Clock"))
            {
                var opt = playerUI.GetOptions(player);
                ui.Add(new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiRawImageComponent { Png = GetImg("OctaPanelIconTime") },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = opt.clockType == 0 ? "0.02 0.2" : "0.0275 0.32",
                            AnchorMax = opt.clockType == 0 ? "0.085 0.8" : "0.07 0.72"
                        }
                    }
                });
            }

            return new UIDrawInfo(ui, uiName);
        }

        private UIDrawInfo DrawClockUI(BasePlayer player)
        {
            const string uiName = "OctaPanelUITimerText";

            if (IsHidden(player, "Clock"))
            {
                _offsets.SetWidth(player, PanelOffsetType.Timer, 0.2275f);
                return new UIDrawInfo(null, uiName);
            }

            var opt = playerUI.GetOptions(player);
            _offsets.SetWidth(player, PanelOffsetType.Timer, opt.clockType == 0 ? 0f : 0.06f);

            var ui = new CuiElementContainer
            {
                { BasePanel390x40TopLeft(), "Hud", uiName },
                new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Color = "1 1 1 1",
                            Text = _currTime ?? "",
                            FontSize = opt.clockType == 0 ? 24 : 16,
                            Align = TextAnchor.MiddleLeft
                        },
                        new CuiOutlineComponent
                        {
                            Color = "0.2 0.2 0.2 0.3",
                            Distance = "1 -1"
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = opt.clockType == 0 ? "0.0975 0.05" : "0.09 0.05",
                            AnchorMax = "1 1"
                        }
                    }
                }
            };
            return new UIDrawInfo(ui, uiName);
        }

        private UIDrawInfo DrawOnlineUI(BasePlayer player)
        {
            const string uiName = "OctaPanelUIOnlineText";

            if (IsHidden(player, "Online"))
            {
                _offsets.SetWidth(player, PanelOffsetType.Online, 0.2f);
                return new UIDrawInfo(null, uiName);
            }

            _offsets.SetWidth(player, PanelOffsetType.Online);
            var onlineOffset = 0.045f;

            if (!string.IsNullOrEmpty(_currOnline) && _currOnline.Length > 3)
                onlineOffset -= (_currOnline.Length - 3) * 0.0125f;

            onlineOffset -= _offsets.GetWidth(player, PanelOffsetType.Timer);

            var ui = new CuiElementContainer
            {
                { BasePanel390x40TopLeft(), "Hud", uiName },
                new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = _currOnline ?? "",
                            FontSize = 16,
                            Align = TextAnchor.MiddleLeft
                        },
                        new CuiOutlineComponent
                        {
                            Color = "0.22 0.22 0.22 0.3",
                            Distance = "1 -1"
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = $"{0.325f+onlineOffset} 0.08",
                            AnchorMax = "1 1",
                            OffsetMin = "0 0",
                            OffsetMax = "0 0"
                        }
                    }
                },
                new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiRawImageComponent { Png = GetImg("OctaPanelIconOnline") },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = $"{0.27f+onlineOffset} 0.32",
                            AnchorMax = $"{0.31f+onlineOffset} 0.72"
                        }
                    }
                }
            };

            return new UIDrawInfo(ui, uiName);
        }

        private UIDrawInfo DrawEconomicsUI(BasePlayer player)
        {
            const string uiName = "OctaPanelUIEconomics";
            if (!_config.economic.allow || IsHidden(player, "Money")) return new UIDrawInfo(null, uiName);

            var ui = new CuiElementContainer
            {
                {
                    new CuiPanel
                    {
                        Image = { Color = "0 0 0 0" },
                        RectTransform =
                        {
                            AnchorMin = "0 1",
                            AnchorMax = "0 1",
                            OffsetMin = "0 -80",
                            OffsetMax = "390 -40"
                        }
                    },
                    "Hud", uiName
                },
                new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiRawImageComponent { Png = GetImg("OctaPanelMoney") },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.0275 0.5",
                            AnchorMax = "0.07 0.9"
                        }
                    }
                },
                new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Color = "1 1 1 1",
                            Text = GetEconomicsText(player),
                            FontSize = 16,
                            Align = TextAnchor.MiddleLeft
                        },
                        new CuiOutlineComponent
                        {
                            Color = "0.2 0.2 0.2 0.3",
                            Distance = "1 -1"
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.09 0.4",
                            AnchorMax = "1 1"
                        }
                    }
                }
            };

            return new UIDrawInfo(ui, uiName);
        }

        private UIDrawInfo DrawIconsUI(BasePlayer player)
        {
            const string uiName = "OctaPanelUIIcons";
            if (IsHidden(player, "IconsAll")) return new UIDrawInfo(null, uiName);

            var min = 0.49f - _offsets.GetWidth(player, PanelOffsetType.Timer) - _offsets.GetWidth(player, PanelOffsetType.Online);
            var max = 0.57f - _offsets.GetWidth(player, PanelOffsetType.Timer) - _offsets.GetWidth(player, PanelOffsetType.Online);

            var cnt = 0;
            var ui = new CuiElementContainer
            {
                { BasePanel390x40TopLeft(), "Hud", uiName }
            };

            foreach (var ic in _eventIcons)
            {
                if (ic.Count == 0) continue;
                if (IsHidden(player, ic.Name)) continue;

                ui.Add(new CuiElement
                {
                    Components =
                    {
                        new CuiImageComponent { Png = GetImg(ic.Name) },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = $"{min + 0.1f*cnt} 0.34",
                            AnchorMax = $"{max + 0.1f*cnt} 0.73",
                            OffsetMin = "0 0",
                            OffsetMax = "0 0"
                        }
                    },
                    Parent = uiName
                });
                cnt++;
            }

            return new UIDrawInfo(ui, uiName);
        }

        private UIDrawInfo DrawLogoUI(BasePlayer player)
        {
            const string uiName = "OctaPanelUILogo";
            if (IsHidden(player, "Logo") && _config.hide.canHideLogo) return new UIDrawInfo(null, uiName);

            var ui = new CuiElementContainer
            {
                {
                    new CuiPanel
                    {
                        Image = { Color = "0 0 0 0" },
                        RectTransform =
                        {
                            AnchorMin = "1 1",
                            AnchorMax = "1 1",
                            OffsetMin = $"{(_config.logo.x + _config.logo.offsetX) * -1} {(_config.logo.y + _config.logo.offsetY) * -1}",
                            OffsetMax = $"{_config.logo.offsetX * -1} {_config.logo.offsetY * -1}"
                        }
                    },
                    "Hud", uiName
                },
                new CuiElement
                {
                    Parent = uiName,
                    Name = uiName + "_img",
                    Components =
                    {
                        new CuiRawImageComponent { Png = GetImg("OctaPanelLogo") },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                            OffsetMin = "0 0",
                            OffsetMax = "0 0"
                        }
                    }
                }
            };
            return new UIDrawInfo(ui, uiName);
        }

        // СОВЕТЫ — теперь вверху слева (на месте старой мини-статы)
        private UIDrawInfo DrawAdviceUI(BasePlayer player)
        {
            const string uiName = "OctaPanelUIAdvice";
            if (IsHidden(player, "Advice")) return new UIDrawInfo(null, uiName);

            var ui = new CuiElementContainer
            {
                {
                    new CuiPanel
                    {
                        Image = { Color = _config.pvp.panelColor },
                        RectTransform =
                        {
                            AnchorMin = _config.pvp.anchorMin,
                            AnchorMax = _config.pvp.anchorMax
                        }
                    },
                    "Hud", uiName
                },
                new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = _currAdvice ?? "",
                            FontSize = 14,
                            Align = TextAnchor.UpperLeft,
                            Color = "1 1 1 1"
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                            OffsetMin = "6 4",
                            OffsetMax = "-6 -4"
                        }
                    }
                }
            };

            return new UIDrawInfo(ui, uiName);
        }

        // PvP СТАТИСТИКА — теперь ВНИЗУ (бывшая полоса "Советы"), ОДНА строка по центру
        private UIDrawInfo DrawPvPStatsUI(BasePlayer player)
        {
            const string uiName = "OctaPanelUIPvP";
            if (!_config.pvp.allow || IsHidden(player, "PvP")) return new UIDrawInfo(null, uiName);

            var s = GetStats(player.userID);

            var ui = new CuiElementContainer
            {
                {
                    new CuiPanel
                    {
                        Image = { Color = "1 1 1 0" },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0",
                            AnchorMax = "0.5 0",
                            OffsetMin = "-420 0",
                            OffsetMax = "400 18"
                        }
                    },
                    "Overlay", uiName
                },
                new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = $"Убийства: {s.Kills} | Смерти: {s.Deaths} | K/D: {s.KDRatio:0.00}",
                            FontSize = 14,
                            Align = TextAnchor.MiddleCenter,
                            Color = "1 1 1 1"
                        },
                        new CuiOutlineComponent
                        {
                            Color = "0.4 0.4 0.4 0.6",
                            Distance = "1 -1",
                            UseGraphicAlpha = true
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1"
                        }
                    }
                }
            };

            return new UIDrawInfo(ui, uiName);
        }

        #endregion

        #region Data Refreshing

        private void RefreshClockData()
        {
            var time = _sky?.Cycle?.DateTime.ToString("HH:mm");
            if (time != null) _currTime = time;
        }

        private void RefreshOnlineData()
        {
            var max = ConVar.Server.maxplayers;
            _currOnline = $"{GetActivePlayersSafe().Count}/{max}";
        }

        private void RefreshIconsData()
        {
            foreach (var ic in _eventIcons)
                ic.Count = ic.PrefabPrefix != null
                    ? CountByPrefab(ic.PrefabPrefix)
                    : CountComponentsOf(ic.Type);
        }

        // deep sea сущности: ghostship/ghostship_c/... — без карты-маркеров
        private static int CountByPrefab(string prefix)
        {
            var n = 0;
            foreach (var e in BaseNetworkable.serverEntities)
                if (MatchesPrefab(e, prefix))
                    n++;
            return n;
        }

        private void RefreshAdviceData()
        {
            if (_config.advice.list == null || _config.advice.list.Count == 0) return;

            _currAdvice = _config.advice.list[_currAdviceIdx];
            _currAdviceIdx = _currAdviceIdx >= _config.advice.list.Count - 1 ? 0 : _currAdviceIdx + 1;
        }

        private void RefreshAllData()
        {
            RefreshClockData();
            RefreshOnlineData();
            RefreshIconsData();
            if (_config.advice.allow) RefreshAdviceData();
        }

        #endregion

        #region Timers

        private void StartServiceTimers()
        {
            RedrawAllUIComponents();

            _timers["clockTimer"] = timer.Every(_config.timers.clock, () =>
            {
                RefreshClockData();
                RedrawUIForAllPlayers(DrawClockUI);
            });

            _timers["onlineTimer"] = timer.Every(_config.timers.online, () =>
            {
                RefreshOnlineData();
                RedrawUIForAllPlayers(DrawOnlineUI);
            });

            _timers["iconTimer"] = timer.Every(1f, () => RedrawUIForAllPlayers(DrawIconsUI));

            if (_config.advice.allow)
            {
                _timers["adviceTimer"] = timer.Every(_config.timers.advice, () =>
                {
                    RefreshAdviceData();
                    RedrawUIForAllPlayers(DrawAdviceUI);
                });
            }

            if (_config.economic.allow)
            {
                _timers["economicsTimer"] = timer.Every(_config.timers.economics,
                    () => RedrawUIForAllPlayers(DrawEconomicsUI));
            }

            if (_config.pvp.allow)
            {
                // Периодическое обновление PvP-панели
                _timers["statsUiTimer"] = timer.Every(3f, () => RedrawUIForAllPlayers(DrawPvPStatsUI));
            }

            _timers["panelTimer"] = timer.Every(_config.timers.main, () =>
            {
                RefreshAllData();
                RedrawAllUIComponents();
            });
        }

        #endregion

        #region Commands (Settings UI)

        private static double GetAMin(int cnt)
        {
            cnt--;
            return 0.8f - cnt * 0.08 - 0.01 * cnt;
        }

        private static string GetAMax(int cnt)
        {
            cnt--;
            return (0.88f - cnt * 0.08 - 0.01 * cnt) + "";
        }

        private void DrawSettingsUI(BasePlayer player)
        {
            const string uiName = "OctaPanelSettings";
            const string uiGreen = "0.61 0.75 0.24 0.4";
            const string uiRed = "0.80 0.29 0.25 0.4";
            const string uiGray = "0.4 0.4 0.4 0.4";

            var hide = playerUI.GetHide(player);
            var options = playerUI.GetOptions(player);

            var iconsAllEnabled = !hide.Contains("IconsAll");
            var ui = new CuiElementContainer
            {
                {
                    new CuiPanel
                    {
                        Image = { Color = "0 0 0 0" },
                        RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-200 -220", OffsetMax = "200 220" }
                    },
                    "Hud", uiName
                },
                new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiImageComponent { Color = "0 0 0 0.6" },
                        new CuiRectTransformComponent { AnchorMin = "0 0.9", AnchorMax = "1 1" }
                    }
                },
                new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiTextComponent { Color = "1 1 1 1", Text = getMsg(player, "uiOptTitle"), FontSize = 18, Align = TextAnchor.UpperCenter },
                        new CuiRectTransformComponent { AnchorMin = "0 0.9", AnchorMax = "1 0.975" }
                    }
                },
                {
                    new CuiButton
                    {
                        Text = { Text = "X", FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter },
                        Button = { Color = "0.79 0.29 0.25 1.00", Command = "OctaPanelHideOptions" },
                        RectTransform = { AnchorMin = "0.915 0.915", AnchorMax = "0.9825 0.9825" }
                    }, uiName
                }
            };

            var i = 0;
            var idx = 0;

            if (CanHide(player))
            {
                idx++;
                ui.Add(new CuiButton
                {
                    Text = { Text = getMsg(player, "uiOptEvents"), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    Button = { Color = iconsAllEnabled ? uiGreen : uiRed, Command = $"OctaPanelSetHide IconsAll {(iconsAllEnabled ? "0" : "1")}" },
                    RectTransform = { AnchorMin = "0.015 0.8", AnchorMax = $"{(iconsAllEnabled ? 0.5175 : 0.985)} 0.88" }
                }, uiName);

                if (iconsAllEnabled)
                {
                    foreach (var stateIcon in _eventIcons)
                    {
                        ui.Add(new CuiButton
                        {
                            Text = { Text = "", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                            Button = { ImageType = Image.Type.Simple, Color = !hide.Contains(stateIcon.Name) ? uiGreen : uiRed, Command = $"OctaPanelSetHide {stateIcon.Name} {(!hide.Contains(stateIcon.Name) ? "0" : "1")}" },
                            RectTransform = { AnchorMin = $"{0.525 + i * 0.0935} 0.8", AnchorMax = $"{0.61 + i * 0.0935} 0.88" }
                        }, uiName, uiName + stateIcon.Name);

                        ui.Add(new CuiElement
                        {
                            Parent = uiName + stateIcon.Name,
                            Components =
                            {
                                new CuiImageComponent { Png = GetImg(stateIcon.Name) },
                                new CuiRectTransformComponent { AnchorMin = "0.1 0.3", AnchorMax = "0.9 0.7" }
                            }
                        });
                        i++;
                    }
                }
            }

            idx++;
            ui.Add(new CuiButton
            {
                Text = { Text = getMsg(player, "uiOptClock"), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                Button =
                {
                    ImageType = Image.Type.Simple,
                    Color = CanHide(player) ? (!hide.Contains("Clock") ? uiGreen : uiRed) : "0 0 0 0",
                    Command = CanHide(player) ? $"OctaPanelSetHide Clock {(!hide.Contains("Clock") ? "0" : "1")}" : ""
                },
                RectTransform = { AnchorMin = $"0.015 {GetAMin(idx)}", AnchorMax = $"{(CanHide(player) && hide.Contains("Clock") ? 0.985 : 0.5175)} {GetAMax(idx)}" }
            }, uiName);

            if (!CanHide(player) || !hide.Contains("Clock"))
            {
                ui.Add(new CuiButton
                {
                    Text = { Text = getMsg(player, "uiOptClockBig"), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    Button = { ImageType = Image.Type.Simple, Color = options.clockType == 0 ? uiGreen : uiGray, Command = "OctaPanelSetOptions Clock 0" },
                    RectTransform = { AnchorMin = $"0.525 {GetAMin(idx)}", AnchorMax = $"0.75125 {GetAMax(idx)}" }
                }, uiName);

                ui.Add(new CuiButton
                {
                    Text = { Text = getMsg(player, "uiOptClockSmall"), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    Button = { ImageType = Image.Type.Simple, Color = options.clockType == 1 ? uiGreen : uiGray, Command = "OctaPanelSetOptions Clock 1" },
                    RectTransform = { AnchorMin = $"0.75875 {GetAMin(idx)}", AnchorMax = $"0.985 {GetAMax(idx)}" }
                }, uiName);
            }

            if (CanHide(player))
            {
                idx++;
                ui.Add(new CuiButton
                {
                    Text = { Text = getMsg(player, "uiOptOnline"), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    Button = { ImageType = Image.Type.Simple, Color = !hide.Contains("Online") ? uiGreen : uiRed, Command = $"OctaPanelSetHide Online {(!hide.Contains("Online") ? "0" : "1")}" },
                    RectTransform = { AnchorMin = $"0.015 {GetAMin(idx)}", AnchorMax = $"0.985 {GetAMax(idx)}" }
                }, uiName);

                idx++;
                ui.Add(new CuiButton
                {
                    Text = { Text = getMsg(player, "uiOptAdvice"), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    Button = { ImageType = Image.Type.Simple, Color = !hide.Contains("Advice") ? uiGreen : uiRed, Command = $"OctaPanelSetHide Advice {(!hide.Contains("Advice") ? "0" : "1")}" },
                    RectTransform = { AnchorMin = $"0.015 {GetAMin(idx)}", AnchorMax = $"0.985 {GetAMax(idx)}" }
                }, uiName);

                if (_config.economic.allow)
                {
                    idx++;
                    ui.Add(new CuiButton
                    {
                        Text = { Text = getMsg(player, "uiOptMoney"), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                        Button = { ImageType = Image.Type.Simple, Color = !hide.Contains("Money") ? uiGreen : uiRed, Command = $"OctaPanelSetHide Money {(!hide.Contains("Money") ? "0" : "1")}" },
                        RectTransform = { AnchorMin = $"0.015 {GetAMin(idx)}", AnchorMax = $"0.985 {GetAMax(idx)}" }
                    }, uiName);
                }

                // PvP
                idx++;
                ui.Add(new CuiButton
                {
                    Text = { Text = "PvP", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    Button = { ImageType = Image.Type.Simple, Color = !hide.Contains("PvP") ? uiGreen : uiRed, Command = $"OctaPanelSetHide PvP {(!hide.Contains("PvP") ? "0" : "1")}" },
                    RectTransform = { AnchorMin = $"0.015 {GetAMin(idx)}", AnchorMax = $"0.985 {GetAMax(idx)}" }
                }, uiName);

                if (_config.hide.canHideLogo)
                {
                    idx++;
                    ui.Add(new CuiButton
                    {
                        Text = { Text = getMsg(player, "uiOptLogo"), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                        Button = { ImageType = Image.Type.Simple, Color = !hide.Contains("Logo") ? uiGreen : uiRed, Command = $"OctaPanelSetHide Logo {(!hide.Contains("Logo") ? "0" : "1")}" },
                        RectTransform = { AnchorMin = $"0.015 {GetAMin(idx)}", AnchorMax = $"0.985 {GetAMax(idx)}" }
                    }, uiName);
                }

                idx++;
                ui.Add(new CuiButton
                {
                    Text = { Text = getMsg(player, "uiOptAll"), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    Button = { ImageType = Image.Type.Simple, Color = !hide.Contains("All") ? uiGreen : uiRed, Command = $"OctaPanelSetHide All {(!hide.Contains("All") ? "0" : "1")}" },
                    RectTransform = { AnchorMin = $"0.015 {GetAMin(idx)}", AnchorMax = $"0.985 {GetAMax(idx)}" }
                }, uiName);
            }

            ui.Insert(1, new CuiElement
            {
                Parent = uiName,
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0.4" },
                    new CuiRectTransformComponent { AnchorMin = $"0 {GetAMin(idx)-0.0175f}", AnchorMax = "1 1" },
                    new CuiNeedsCursorComponent()
                }
            });

            CuiHelper.DestroyUi(player, uiName);
            CuiHelper.AddUi(player, ui.ToJson());
        }

        [ConsoleCommand("OctaPanelHideOptions")]
        private void CmdHideOptions(ConsoleSystem.Arg args)
        {
            var player = args.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, "OctaPanelSettings");
        }

        [ConsoleCommand("OctaPanelSetOptions")]
        private void CmdSetOptions(ConsoleSystem.Arg args)
        {
            var player = args.Player();
            if (player == null || !args.HasArgs(2)) return;

            var options = playerUI.GetOptions(player);

            if (args.Args[0] == "Clock")
            {
                int val;
                int.TryParse(args.Args[1], out val);
                options.clockType = val;
            }

            SavePlayerUIData();
            RedrawAllUIComponents(player);
            DrawSettingsUI(player);
        }

        [ConsoleCommand("OctaPanelSetHide")]
        private void CmdSetHide(ConsoleSystem.Arg args)
        {
            var player = args.Player();
            if (player == null || !args.HasArgs(2)) return;

            var hideData = playerUI.GetHide(player);

            var layerName = _layersToHide.Find(l => l == args.Args[0]);
            if (string.IsNullOrEmpty(layerName)) return;

            var state = args.Args[1] != "1";

            if (state)
            {
                if (!hideData.Contains(layerName)) hideData.Add(layerName);
            }
            else
            {
                if (hideData.Contains(layerName)) hideData.Remove(layerName);
            }

            if (args.Args[0] == "All")
            {
                foreach (var layer in _layersToHide)
                {
                    if (state)
                    {
                        if (!hideData.Contains(layer)) hideData.Add(layer);
                    }
                    else
                    {
                        if (hideData.Contains(layer)) hideData.Remove(layer);
                    }
                }
            }

            SavePlayerUIData();
            RedrawAllUIComponents(player);
            DrawSettingsUI(player);
        }

        private void CmdShowOptions(IPlayer iplayer, string command, string[] args)
        {
            var player = BasePlayer.Find(iplayer.Id);
            if (player == null) return;
            DrawSettingsUI(player);
        }

        #endregion

        #region Hooks

        private void Loaded()
        {
            _config = Config.ReadObject<Configuration>();

            if (_config.version != 1)
            {
                PrintError("Неверная версия конфигурационного файла! Плагин выгружен!");
                Interface.Oxide.UnloadPlugin(Name);
                return;
            }

            LoadPlayerUIData();
            LoadStats();

            if (_config.hide.allow && _config.hide.usePermision)
                permission.RegisterPermission($"{Name}.canhide", this);
        }

        private void OnServerInitialized()
        {
            _sky = TOD_Sky.Instance;

            if (ImageLibrary == null)
            {
                PrintError("[ImageLibrary] not found, unloading!");
                Interface.Oxide.UnloadPlugin(Name);
                return;
            }

            if (Economics == null && _config.economic.economics)
            {
                PrintWarning("[Economics] not found, disabled!");
                _config.economic.economics = false;
            }

            if (ServerRewards == null && _config.economic.serverRewards)
            {
                PrintWarning("[ServerRewards] not found, disabled!");
                _config.economic.serverRewards = false;
            }

            if (!_config.economic.economics && !_config.economic.serverRewards)
                _config.economic.allow = false;

            // preload images
            if (_config.logo.allow)
                _staticIcons["OctaPanelLogo_src"] = _config.logo.url;

            foreach (var icon in _staticIcons)
            {
                try { ImageLibrary.Call("AddImage", icon.Value, icon.Key, 0UL); }
                catch { }
            }
            foreach (var ic in _eventIcons)
            {
                try
                {
                    if (ic.PngB64 != null)
                        ImageLibrary.Call("AddImageData", ic.Name, Convert.FromBase64String(ic.PngB64), 0UL);
                    else
                        ImageLibrary.Call("AddImage", ic.Url, ic.Name, 0UL);
                }
                catch { }
            }
            if (_config.logo.allow)
            {
                try { ImageLibrary.Call("AddImage", _config.logo.url, "OctaPanelLogo", 0UL); }
                catch { }
            }
            if (_config.economic.allow)
            {
                try { ImageLibrary.Call("AddImage", _config.economic.url, "OctaPanelMoney", 0UL); }
                catch { }
            }

            AddCovalenceCommand(_config.cmdOptions, nameof(CmdShowOptions));

            RefreshAllData();

            _timers["launchTimer"] = timer.Every(0.5f, () =>
            {
                if (!_panelReady)
                {
                    bool ready = false;
                    try { ready = (bool)ImageLibrary.Call("IsReady"); } catch { ready = false; }
                    if (!ready) return;
                }

                _panelReady = true;
                _timers["launchTimer"]?.Destroy();
                StartServiceTimers();
            });
        }

        private void OnPlayerInit(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            if (player.IsReceivingSnapshot)
            {
                timer.Once(1f, () => OnPlayerInit(player));
                return;
            }

            if (!playerUI.Has(player))
            {
                playerUI.GetHide(player);
                playerUI.GetOptions(player);
                SavePlayerUIData();
            }

            RedrawAllUIComponents(player);
        }

        private void Unload()
        {
            SavePlayerUIData();
            SaveStats();
            DestroyTimers(_timers.Values.ToList());
            DestroyAllUI();
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (entity == null) return;
            foreach (var ic in _eventIcons)
            {
                if (ic.Test == null || !ic.Test(entity)) continue;
                ic.Count++;
            }
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity == null) return;
            foreach (var ic in _eventIcons)
            {
                if (ic.Test == null || !ic.Test(entity)) continue;
                if (ic.Count > 0) ic.Count--;
            }
        }

        // PvP: Считаем только убийства игроков
        // Deaths++ если умер живой игрок (не NPC)
        // Kills++ если инициатор - другой живой игрок (не NPC)
        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null) return;

            if (!player.IsNpc)
            {
                var victimStats = GetStats(player.userID);
                victimStats.Deaths++;
            }

            var killer = info?.InitiatorPlayer;
            if (killer != null && killer != player && !killer.IsNpc)
            {
                var killerStats = GetStats(killer.userID);
                killerStats.Kills++;
            }

            SaveStats();
        }

        #endregion

        #region Localization

        private string getMsg(BasePlayer player, string key, params object[] args) =>
            string.Format(lang.GetMessage(key, this, player.UserIDString), args);

        private new void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["uiOptTitle"] = "◆ OctaPanel settings",
                ["uiOptEvents"] = "Events",
                ["uiOptClock"] = "Clock",
                ["uiOptClockBig"] = "Big",
                ["uiOptClockSmall"] = "Small",
                ["uiOptOnline"] = "Online",
                ["uiOptAdvice"] = "Advices",
                ["uiOptMoney"] = "Balance",
                ["uiOptLogo"] = "Logo",
                ["uiOptAll"] = "Switch all"
            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["uiOptTitle"] = "◆ OctaPanel — настройка инфопанели",
                ["uiOptEvents"] = "События",
                ["uiOptClock"] = "Часы",
                ["uiOptClockBig"] = "Большие",
                ["uiOptClockSmall"] = "Маленькие",
                ["uiOptOnline"] = "Онлайн",
                ["uiOptAdvice"] = "Советы",
                ["uiOptMoney"] = "Баланс",
                ["uiOptLogo"] = "Логотип",
                ["uiOptAll"] = "Переключить все"
            }, this, "ru");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["uiOptTitle"] = "Configuración del Panel",
                ["uiOptEvents"] = "Eventos",
                ["uiOptClock"] = "Reloj",
                ["uiOptClockBig"] = "Grande",
                ["uiOptClockSmall"] = "Chico",
                ["uiOptOnline"] = "En Línea",
                ["uiOptAdvice"] = "Mensajes",
                ["uiOptMoney"] = "Dinero",
                ["uiOptLogo"] = "Logo",
                ["uiOptAll"] = "Cambiar ON/OFF"
            }, this, "es-ES");
        }

        #endregion

        #region Internal

        private string GetEconomicsText(BasePlayer player)
        {
            try
            {
                if (_config.economic.serverRewards)
                {
                    var ret = ServerRewards?.Call<object>("CheckPoints", player.userID);
                    if (ret == null) return "0";
                    return ((int)ret) + "";
                }

                if (_config.economic.economics)
                {
                    var ret = Economics?.Call<double>("Balance", player.userID);
                    if (ret == null) return "0";
                    return ((double)ret) + "";
                }
            }
            catch
            {
                // ignored
            }
            return "";
        }

        #endregion
    }
}
