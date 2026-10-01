// OctaESP — админский ESP в домовом стиле OctavianRSM.
// Переработанный AdminESP (FineINK 0.3.01): исправлены наследование компонента,
// потерянный хук дисконнекта, дёргание флага IsAdmin на каждую отрисовку,
// плюс добавлены подсветка цели, смотрящей на админа, и отображение оружия.
// v2.0.0 — интерфейс переведён на Chaos UIFramework (требуется Oxide.Ext.Chaos).
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;
using Oxide.Ext.Chaos;
using Oxide.Ext.Chaos.Data;
using Oxide.Ext.Chaos.UIFramework;
using Chaos = Oxide.Ext.Chaos;
using UIColor = Oxide.Ext.Chaos.UIFramework.Color;
using Font = Oxide.Ext.Chaos.UIFramework.Font;
using UIAnchor = Oxide.Ext.Chaos.UIFramework.Anchor;
using Offset = Oxide.Ext.Chaos.UIFramework.Offset;
using Style = Oxide.Ext.Chaos.UIFramework.Style;

namespace Oxide.Plugins
{
    [Info("OctaESP", "Octavian", "2.0.0")]
    [Description("Админский ESP: имена/здоровье/оружие, боксы, линия взгляда, спящие, NPC. Chaos-меню, бинд через octaesp.toggle")]
    public class OctaESP : ChaosPlugin
    {
        [Chaos.Permission] private const string PermUse = "octaesp.use";

        private const string UiMenu   = "OCTAESP_UI";
        private const string UiButton = "OCTAESP_BUTTON";

        private CommandCallbackHandler m_Callbacks;
        private Datafile<EspData> m_Data;

        private static OctaESP _i; // компонент отрисовки читает конфиг через инстанс

        // ── Конфиг (Chaos ConfigurationFile) ────────────────────────────────

        private ConfigData Configuration => ConfigurationData as ConfigData;

        protected override ConfigurationFile OnLoadConfig(ref ConfigurationFile configurationFile) =>
            configurationFile = new ConfigurationFile<ConfigData>(Config);

        protected class ConfigData : BaseConfigData
        {
            [JsonProperty("Показывать HUD-кнопку ◆ ESP слева внизу")]
            public bool HudButton = true;

            [JsonProperty("Подсветка цели, смотрящей на админа (красная линия + ⚠)")]
            public bool AimWarning = true;

            [JsonProperty("Интервал обновления по умолчанию (сек)")]
            public float DefaultInterval = 0.25f;

            [JsonProperty("Дистанция по умолчанию (м)")]
            public int DefaultDistance = 250;

            [JsonProperty("Длина линии взгляда по умолчанию (м)")]
            public int DefaultEyeLength = 60;

            [JsonProperty("Минимальный интервал обновления (сек)")]
            public float MinInterval = 0.1f;

            [JsonProperty("Максимальная дистанция ESP (м)")]
            public int MaxDistance = 1000;

            [JsonProperty("Размер шрифта меток ESP")]
            public int LabelSize = 12;

            [JsonProperty("Префикс сообщений в чате")]
            public string ChatPrefix = "<color=#E8B84B>◆ OctaESP</color>";
        }

        // индивидуальные настройки каждого админа — data/OctaESP.json через Chaos Datafile
        protected class EspData
        {
            public Dictionary<ulong, EspSettings> Players = new();
        }

        protected class EspSettings
        {
            public bool Enabled;
            public float Interval;
            public int Distance;
            public bool ShowAdmins = true;
            public bool Names = true;
            public bool Boxes;
            public bool EyeLine = true;
            public int EyeLength;
            public bool Sleepers = true;
            public bool Weapon = true;
            public bool Npcs;
        }

        private EspSettings Get(BasePlayer p) => m_Data.Data.Players.TryGetValue(p.userID, out var s)
            ? s
            : m_Data.Data.Players[p.userID] = new EspSettings
            {
                Interval = Configuration.DefaultInterval,
                Distance = Configuration.DefaultDistance,
                EyeLength = Configuration.DefaultEyeLength
            };

        // живые NPC — ведём сами через хуки, чтобы не сканить serverEntities каждый тик
        private readonly HashSet<NPCPlayer> _npcs = new();
        private readonly HashSet<ulong> _menuOpen = new();

        private void Msg(BasePlayer p, string text) =>
            p.ChatMessage($"{Configuration.ChatPrefix} <color=#9AA4B2>—</color> {text}");

        // ChaosPlugin требует словарь фраз — сообщения шлём напрямую, словарь минимальный
        protected override void PopulatePhrases() => m_Messages = new Dictionary<string, string>();

        // ── Жизненный цикл ──────────────────────────────────────────────────

        private void Init()
        {
            _i = this;
            m_Callbacks = new CommandCallbackHandler(this);
            // путь отличается от старого data/OctaESP.json — там был плоский словарь
            m_Data = new Datafile<EspData>("OctaESP/settings");
            cmd.AddChatCommand("esp", this, CmdEsp);
        }

        private void OnServerInitialized()
        {
            // при загрузке на живом сервере вернуть кнопку/ESP тем, у кого было включено
            foreach (var p in BasePlayer.activePlayerList) timer.Once(2f, () => OnPlayerReady(p));
            // боты, заспавненные до загрузки плагина
            foreach (var e in BaseNetworkable.serverEntities)
                if (e is NPCPlayer npc) _npcs.Add(npc);
        }

        private void OnEntitySpawned(BaseNetworkable ent)
        {
            if (ent is NPCPlayer npc) _npcs.Add(npc);
        }

        private void OnEntityKill(BaseNetworkable ent)
        {
            if (ent is NPCPlayer npc) _npcs.Remove(npc);
        }

        private void OnServerSave() => m_Data.Save();

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                DestroyUi(p);
                var comp = p.GetComponent<EspDrawer>();
                if (comp != null) UnityEngine.Object.Destroy(comp);
                AdminFlag(p, false);
            }
            _menuOpen.Clear();
            _npcs.Clear();
            m_Data.Save();
            _i = null;
        }

        private void OnPlayerConnected(BasePlayer player) => OnPlayerReady(player);

        // кнопку нельзя слать, пока игрок грузит снапшот — ждём
        private void OnPlayerReady(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.Once(1f, () => OnPlayerReady(player));
                return;
            }
            if (!permission.UserHasPermission(player.UserIDString, PermUse))
            {
                // право отобрали — забываем настройки, чтобы не таскать хвост
                if (m_Data.Data.Players.Remove(player.userID)) m_Data.Save();
                return;
            }
            if (Configuration.HudButton) BuildButton(player);
            if (m_Data.Data.Players.TryGetValue(player.userID, out var s) && s.Enabled) SetEnabled(player, true, silent: true);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null) return;
            _menuOpen.Remove(player.userID);
            var comp = player.GetComponent<EspDrawer>();
            if (comp != null) UnityEngine.Object.Destroy(comp);
            AdminFlag(player, false);
        }

        // ddraw.* рендерится только на клиенте с флагом IsAdmin. Ставим флаг на
        // время работы ESP одним сетевым обновлением. authLevel сервера не трогаем.
        private static void AdminFlag(BasePlayer player, bool on)
        {
            if (player == null || player.Connection == null) return;
            if (player.Connection.authLevel >= 2) return; // настоящим админам флаг и так есть
            player.playerFlags = on
                ? player.playerFlags | BasePlayer.PlayerFlags.IsAdmin
                : player.playerFlags & ~BasePlayer.PlayerFlags.IsAdmin;
            player.SendNetworkUpdate();
        }

        // ── Включение/выключение ────────────────────────────────────────────

        private void SetEnabled(BasePlayer player, bool on, bool silent = false)
        {
            var s = Get(player);
            s.Enabled = on;
            var comp = player.GetComponent<EspDrawer>();
            if (on)
            {
                if (comp == null) player.gameObject.AddComponent<EspDrawer>().Setup(player, s);
                else comp.Setup(player, s);
                AdminFlag(player, true);
            }
            else
            {
                if (comp != null) UnityEngine.Object.Destroy(comp);
                AdminFlag(player, false);
            }
            m_Data.Save();
            if (!silent) Msg(player, on ? "ESP <color=#59C073>включён</color>" : "ESP <color=#E06666>выключен</color>");
        }

        // ── Команды ─────────────────────────────────────────────────────────

        private void CmdEsp(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermUse))
            {
                Msg(player, "Нет доступа (<color=#E06666>octaesp.use</color>)");
                return;
            }
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "":
                    SetEnabled(player, !Get(player).Enabled);
                    break;
                case "menu":
                case "ui":
                    ToggleMenu(player);
                    break;
                default:
                    Msg(player,
                        "<color=#E8B84B>/esp</color> — вкл/выкл · " +
                        "<color=#E8B84B>/esp menu</color> — настройки · " +
                        "бинд: <color=#E8B84B>bind x octaesp.toggle</color>");
                    break;
            }
        }

        [ConsoleCommand("octaesp.toggle")]
        private void CmdToggle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, PermUse)) return;
            SetEnabled(player, !Get(player).Enabled);
        }

        // ── Компонент отрисовки (без изменений) ─────────────────────────────

        private sealed class EspDrawer : MonoBehaviour
        {
            private BasePlayer _self;
            private EspSettings _s;
            private float _acc;
            private readonly List<BasePlayer> _npcBuf = new();
            private readonly List<NPCPlayer> _stale = new();

            public void Setup(BasePlayer self, EspSettings s) { _self = self; _s = s; }

            private void Update()
            {
                if (_self == null || !_self.IsConnected || _s == null) { Destroy(this); return; }
                _acc += Time.deltaTime;
                if (_acc < _s.Interval) return;
                _acc = 0f;
                // ttl чуть длиннее интервала — подсветка не мерцает между тиками
                var ttl = _s.Interval + 0.08f;
                DrawList(BasePlayer.activePlayerList, ttl);
                if (_s.Sleepers) DrawList(BasePlayer.sleepingPlayerList, ttl);
                if (_s.Npcs) DrawNpcs(ttl);
            }

            // NPCPlayer живут в общем сете плагина; мёртвых/деспавнувших чистим по ходу
            private void DrawNpcs(float ttl)
            {
                _npcBuf.Clear();
                _stale.Clear();
                foreach (var npc in _i._npcs)
                {
                    if (npc == null || npc.IsDestroyed || npc.IsDead()) { _stale.Add(npc); continue; }
                    _npcBuf.Add(npc);
                }
                foreach (var n in _stale) _i._npcs.Remove(n);
                if (_npcBuf.Count > 0) DrawList(_npcBuf, ttl, npcs: true);
            }

            private void DrawList(IEnumerable<BasePlayer> list, float ttl, bool npcs = false)
            {
                var cfg = _i.Configuration;
                foreach (var t in list)
                {
                    if (t == null || t == _self || t.transform == null) continue;
                    var dist = Vector3.Distance(t.transform.position, _self.transform.position);
                    if (dist < 2f || dist > _s.Distance) continue;
                    if (t.IsAdmin && !_s.ShowAdmins) continue;

                    var sleeping = t.IsSleeping();
                    var color = npcs ? new UnityEngine.Color(1f, 0.6f, 0.15f) : sleeping ? UnityEngine.Color.white : UnityEngine.Color.cyan;
                    var aimed = false;

                    // цель смотрит на админа: маленький угол между взглядом и направлением на нас
                    if (cfg.AimWarning && !sleeping && t.eyes != null)
                    {
                        var toMe = _self.eyes.position - t.eyes.position;
                        if (toMe.magnitude <= _s.EyeLength &&
                            Vector3.Angle(t.eyes.HeadRay().direction, toMe) < 6f)
                        {
                            aimed = true;
                            color = UnityEngine.Color.red;
                        }
                    }

                    if (_s.Names)
                    {
                        var name = string.IsNullOrEmpty(t.displayName) ? t.ShortPrefabName : t.displayName;
                        var sb = new System.Text.StringBuilder(96);
                        sb.Append("<size=").Append(cfg.LabelSize).Append('>');
                        if (aimed) sb.Append("⚠ ");
                        sb.Append(name).Append(" · ").Append(dist.ToString("0")).Append(" м");
                        if (sleeping) sb.Append(" · СПИТ");
                        if (_s.Weapon && !sleeping)
                        {
                            var held = t.GetActiveItem();
                            var wname = held?.info?.displayName?.english ?? held?.info?.shortname;
                            if (!string.IsNullOrEmpty(wname)) sb.Append(" · ").Append(wname);
                        }
                        sb.Append('\n').Append(HealthBar(t)).Append("</size>");
                        DDraw("ddraw.text", ttl, color, t.eyes.position + new Vector3(0, 0.45f, 0), sb.ToString());
                    }
                    if (_s.Boxes)
                        _self.SendConsoleCommand("ddraw.box", ttl, color,
                            t.transform.position + new Vector3(0, 1f, 0),
                            BasePlayer.GetHeight(t.modelState.ducked));
                    if (_s.EyeLine && !sleeping && t.eyes != null)
                        _self.SendConsoleCommand("ddraw.line", ttl, aimed ? UnityEngine.Color.red : new UnityEngine.Color(1f, 0.8f, 0.2f),
                            t.eyes.position, t.eyes.position + t.eyes.HeadRay().direction * _s.EyeLength);
                }
            }

            // HP-полоска блочными символами; цвет зелёный → жёлтый → красный
            private static string HealthBar(BasePlayer t)
            {
                var frac = Mathf.Clamp01(t.health / Math.Max(1f, t.MaxHealth()));
                var full = Mathf.RoundToInt(frac * 8f);
                var col = frac > 0.6f ? "#59C073" : frac > 0.3f ? "#E8B84B" : "#E06666";
                return $"<color={col}>{new string('█', full)}{new string('░', 8 - full)}</color> {t.health:0}";
            }

            private void DDraw(string cmd, float ttl, UnityEngine.Color color, Vector3 pos, string text) =>
                _self.SendConsoleCommand(cmd, ttl, color, pos, text);
        }

        // ── Chaos UI ────────────────────────────────────────────────────────

        // Все стили клонируются из пресетов Chaos — "голый" new Style без спрайта/материала не рендерится
        private static readonly Style m_PanelStyle = new Style(ChaosStyle.Panel)
        {
            ImageColor = new UIColor(0.05f, 0.07f, 0.10f, 0.94f),
            Material = Materials.BackgroundBlur
        };

        private static readonly Style m_RowStyle = new Style(ChaosStyle.Panel)
        {
            ImageColor = new UIColor(0.10f, 0.13f, 0.18f, 0.85f)
        };

        private static readonly Style m_TitleStyle = new Style(ChaosStyle.Title)
        {
            Font = Font.RobotoCondensedBold, // пресет Title тащит рукописный PermanentMarker — заменяем
            FontSize = 16,
            FontColor = new UIColor(0.91f, 0.72f, 0.29f, 1f), // золото Octavian
            Alignment = TextAnchor.MiddleCenter
        };

        private static readonly Style m_LabelStyle = new Style(ChaosStyle.Button)
        {
            FontSize = 13,
            FontColor = new UIColor(0.92f, 0.94f, 0.97f, 1f),
            Alignment = TextAnchor.MiddleLeft
        };

        private static readonly Style m_ValueStyle = new Style(ChaosStyle.Button)
        {
            FontSize = 13,
            FontColor = new UIColor(0.91f, 0.72f, 0.29f, 1f),
            Alignment = TextAnchor.MiddleCenter
        };

        private static readonly Style m_MasterOnStyle = new Style(ChaosStyle.Button)
        {
            ImageColor = new UIColor(0.35f, 0.75f, 0.45f, 0.9f)
        };

        private static readonly Style m_MasterOffStyle = new Style(ChaosStyle.Button)
        {
            ImageColor = new UIColor(0.75f, 0.30f, 0.30f, 0.9f)
        };

        // маленькая кнопка ◆ ESP в левом нижнем углу
        private void BuildButton(BasePlayer player)
        {
            var root = BaseContainer.Create(UiButton, Layer.HudMenu, UIAnchor.BottomLeft, new Offset(10, 12, 82, 34));
            ChaosPrefab.TextButton(root, UIAnchor.FullStretch, Offset.zero, "◆ ESP", m_LabelStyle, ChaosStyle.BlackOutline)
                .WithCallback(m_Callbacks, arg => ToggleMenu(player), $"{player.userID}.button");
            ChaosUI.Show(player, root);
        }

        private void ToggleMenu(BasePlayer player)
        {
            if (_menuOpen.Remove(player.userID))
            {
                ChaosUI.Destroy(player, UiMenu);
                return;
            }
            _menuOpen.Add(player.userID);
            BuildMenu(player);
        }

        private const int RowH = 26;   // высота строки настроек
        private const int RowGap = 4;
        private const int HeadH = 84;  // шапка + мастер-кнопка

        // строка с чекбоксом: [x] Подпись
        // ВАЖНО: у stretch-якорей Chaos xMin/xMax — абсолютные пиксели от краёв,
        // поэтому для текста используем CenterLeft/CenterRight с явной шириной
        private void AddToggleRow(BaseContainer body, int i, BasePlayer player, string label, Func<EspSettings, bool> get, Action<EspSettings, bool> set, string key)
        {
            var y = HeadH + i * (RowH + RowGap);
            ChaosPrefab.Panel(body, UIAnchor.TopStretch, new Offset(8, -(y + RowH), -8, -y), m_RowStyle)
                .WithChildren(row =>
                {
                    var s = Get(player);
                    ChaosPrefab.Toggle(row, UIAnchor.CenterLeft, new Offset(8, -10, 28, 10), get(s))
                        ?.WithCallback(m_Callbacks, arg =>
                        {
                            var cur = Get(player);
                            set(cur, !get(cur));
                            m_Data.Save();
                            BuildMenu(player);
                        }, $"{player.userID}.{key}");

                    TextContainer.Create(row, label, m_LabelStyle, UIAnchor.CenterLeft, new Offset(36, -10, 260, 10));
                });
        }

        // строка со значением: Подпись  [−] val [+]
        private void AddValueRow(BaseContainer body, int i, BasePlayer player, string label, string value, Action<EspSettings> minus, Action<EspSettings> plus, string key)
        {
            var y = HeadH + i * (RowH + RowGap);
            ChaosPrefab.Panel(body, UIAnchor.TopStretch, new Offset(8, -(y + RowH), -8, -y), m_RowStyle)
                .WithChildren(row =>
                {
                    TextContainer.Create(row, label, m_LabelStyle, UIAnchor.CenterLeft, new Offset(10, -10, 180, 10));

                    ChaosPrefab.TextButton(row, UIAnchor.CenterRight, new Offset(-96, -11, -70, 11), "−", m_LabelStyle, ChaosStyle.BlackOutline)
                        .WithCallback(m_Callbacks, arg => { minus(Get(player)); m_Data.Save(); BuildMenu(player); }, $"{player.userID}.{key}.minus");

                    TextContainer.Create(row, value, m_ValueStyle, UIAnchor.CenterRight, new Offset(-66, -10, -36, 10));

                    ChaosPrefab.TextButton(row, UIAnchor.CenterRight, new Offset(-32, -11, -6, 11), "+", m_LabelStyle, ChaosStyle.BlackOutline)
                        .WithCallback(m_Callbacks, arg => { plus(Get(player)); m_Data.Save(); BuildMenu(player); }, $"{player.userID}.{key}.plus");
                });
        }

        private void BuildMenu(BasePlayer player)
        {
            var s = Get(player);

            var root = ChaosPrefab.Background(UiMenu, Layer.HudMenu, UIAnchor.Center,
                new Offset(-170, -250, 170, 250), m_PanelStyle);

            root.WithChildren(body =>
            {
                ChaosPrefab.Title(body, UIAnchor.TopStretch, new Offset(8, -34, -44, -8), "◆ OCTAVIAN ESP", m_TitleStyle);

                ChaosPrefab.CloseButton(body, UIAnchor.TopRight, new Offset(-36, -34, -8, -6), ChaosStyle.BlackOutline)
                    .WithCallback(m_Callbacks, arg => ToggleMenu(player), $"{player.userID}.close");

                // мастер-выключатель
                ChaosPrefab.TextButton(body, UIAnchor.TopStretch, new Offset(8, -74, -8, -44),
                        s.Enabled ? "ВЫКЛЮЧИТЬ ESP" : "ВКЛЮЧИТЬ ESP",
                        s.Enabled ? m_MasterOffStyle : m_MasterOnStyle, ChaosStyle.BlackOutline)
                    .WithCallback(m_Callbacks, arg =>
                    {
                        SetEnabled(player, !Get(player).Enabled);
                        BuildMenu(player);
                    }, $"{player.userID}.master");

                var i = 0;
                AddToggleRow(body, i++, player, "Имена и здоровье",  s => s.Names,    (s, v) => s.Names = v,    "names");
                AddToggleRow(body, i++, player, "Боксы",            s => s.Boxes,    (s, v) => s.Boxes = v,    "boxes");
                AddToggleRow(body, i++, player, "Линия взгляда",    s => s.EyeLine,  (s, v) => s.EyeLine = v,  "eyeline");
                AddToggleRow(body, i++, player, "Спящие игроки",    s => s.Sleepers, (s, v) => s.Sleepers = v, "sleepers");
                AddToggleRow(body, i++, player, "Показывать админов", s => s.ShowAdmins, (s, v) => s.ShowAdmins = v, "admins");
                AddToggleRow(body, i++, player, "Оружие в руках",   s => s.Weapon,   (s, v) => s.Weapon = v,   "weapon");
                AddToggleRow(body, i++, player, "Боты (NPC)",       s => s.Npcs,     (s, v) => s.Npcs = v,     "npcs");
                i++; // зазор перед числовыми строками
                AddValueRow(body, i++, player, "Дистанция, м", s.Distance.ToString(),
                    x => x.Distance = Math.Clamp(x.Distance - 50, 50, Configuration.MaxDistance),
                    x => x.Distance = Math.Clamp(x.Distance + 50, 50, Configuration.MaxDistance), "distance");
                AddValueRow(body, i++, player, "Длина линии, м", s.EyeLength.ToString(),
                    x => x.EyeLength = Math.Clamp(x.EyeLength - 10, 10, 300),
                    x => x.EyeLength = Math.Clamp(x.EyeLength + 10, 10, 300), "eyelen");
                AddValueRow(body, i++, player, "Интервал, сек", s.Interval.ToString("0.00", CultureInfo.InvariantCulture),
                    x => x.Interval = Math.Clamp(x.Interval - 0.05f, Configuration.MinInterval, 2f),
                    x => x.Interval = Math.Clamp(x.Interval + 0.05f, Configuration.MinInterval, 2f), "interval");
            })
            .NeedsCursor()
            .DestroyExisting();

            ChaosUI.Show(player, root);
        }

        private void DestroyUi(BasePlayer player)
        {
            ChaosUI.Destroy(player, UiButton);
            ChaosUI.Destroy(player, UiMenu);
        }
    }
}
