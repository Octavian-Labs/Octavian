using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    // OctaESP — админский ESP в домовом стиле OctavianRSM.
    // Переработанный AdminESP (FineINK 0.3.01): исправлены наследование компонента,
    // потерянный хук дисконнекта, дёргание флага IsAdmin на каждую отрисовку,
    // плюс добавлены подсветка цели, смотрящей на админа, и отображение оружия.
    [Info("OctaESP", "Octavian", "1.0.0")]
    [Description("Админский ESP: имена/здоровье/оружие, боксы, линия взгляда, спящие. HUD-меню, привязка на клавишу через octaesp.toggle")]
    public class OctaESP : RustPlugin
    {
        private const string PermUse  = "octaesp.use";
        private const string UiButton = "OctaESP.Button";
        private const string UiMenu   = "OctaESP.Menu";

        // палитра Octavian: тёмная панель + золотой акцент
        private const string ColPanel  = "0.05 0.07 0.10 0.92";
        private const string ColRow    = "0.10 0.13 0.18 0.80";
        private const string ColAccent = "0.91 0.72 0.29 1";
        private const string ColOn     = "0.35 0.75 0.45 0.9";
        private const string ColOff    = "0.75 0.30 0.30 0.9";
        private const string ColText   = "0.92 0.94 0.97 1";
        private const string ColDim    = "0.55 0.60 0.68 1";

        private static OctaESP _i; // компонент отрисовки читает конфиг через инстанс

        // ── Настройки: глобальный конфиг + per-player ────────────────────────

        private sealed class PluginConfig
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

            [JsonProperty("Префикс сообщений в чате")]
            public string ChatPrefix = "<color=#E8B84B>◆ OctaESP</color>";
        }

        private PluginConfig _cfg;

        protected override void LoadDefaultConfig() => _cfg = new PluginConfig();
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _cfg = Config.ReadObject<PluginConfig>() ?? new PluginConfig(); }
            catch { _cfg = new PluginConfig(); }
            SaveConfig();
        }
        protected override void SaveConfig() => Config.WriteObject(_cfg, true);

        // индивидуальные настройки каждого админа — живут в data/OctaESP.json
        private sealed class EspSettings
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
        }

        private Dictionary<ulong, EspSettings> _sets = new();
        private readonly HashSet<ulong> _menuOpen = new();

        private EspSettings Get(BasePlayer p) => _sets.TryGetValue(p.userID, out var s)
            ? s
            : _sets[p.userID] = new EspSettings
            {
                Interval = _cfg.DefaultInterval,
                Distance = _cfg.DefaultDistance,
                EyeLength = _cfg.DefaultEyeLength
            };

        private void LoadData()
        {
            try { _sets = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, EspSettings>>(Name) ?? new(); }
            catch { _sets = new(); }
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _sets);

        private void Msg(BasePlayer p, string text) =>
            p.ChatMessage($"{_cfg.ChatPrefix} <color=#9AA4B2>—</color> {text}");

        // ── Жизненный цикл ──────────────────────────────────────────────────

        private void Init()
        {
            _i = this;
            LoadData();
            permission.RegisterPermission(PermUse, this);
        }

        private void OnServerInitialized()
        {
            // при загрузке на живом сервере вернуть кнопку/ESP тем, у кого было включено
            foreach (var p in BasePlayer.activePlayerList) timer.Once(2f, () => OnPlayerReady(p));
        }

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
            SaveData();
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
                if (_sets.Remove(player.userID)) SaveData();
                return;
            }
            if (_cfg.HudButton) BuildButton(player);
            if (_sets.TryGetValue(player.userID, out var s) && s.Enabled) SetEnabled(player, true, silent: true);
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
        // время работы ESP одним сетевым обновлением — оригинал дёргал его на
        // каждый вызов отрисовки, что спамило сеть. authLevel сервера не трогаем.
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
            SaveData();
            if (!silent) Msg(player, on ? "ESP <color=#59C073>включён</color>" : "ESP <color=#E06666>выключен</color>");
        }

        // ── Команды ─────────────────────────────────────────────────────────

        [ChatCommand("esp")]
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

        [ConsoleCommand("octaesp.ui")]
        private void CmdUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, PermUse)) return;
            ToggleMenu(player);
        }

        // общий приёмник настроек из меню: octaesp.set <ключ> <значение>
        [ConsoleCommand("octaesp.set")]
        private void CmdSet(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, PermUse)) return;
            if (arg.Args.Length < 2) return;

            // в текущем Rust arg.Args — span, а не string[]: берём строки через GetString
            var s = Get(player);
            var key = arg.GetString(0).ToLowerInvariant();
            var val = arg.GetString(1);
            var on = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);

            switch (key)
            {
                case "enable":   SetEnabled(player, on); break;
                case "names":    s.Names = on; break;
                case "boxes":    s.Boxes = on; break;
                case "eyeline":  s.EyeLine = on; break;
                case "sleepers": s.Sleepers = on; break;
                case "admins":   s.ShowAdmins = on; break;
                case "weapon":   s.Weapon = on; break;
                case "distance":
                    if (int.TryParse(val, out var d)) s.Distance = Math.Clamp(d, 50, _cfg.MaxDistance);
                    break;
                case "eyelen":
                    if (int.TryParse(val, out var l)) s.EyeLength = Math.Clamp(l, 10, 300);
                    break;
                case "interval":
                    if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                        s.Interval = Math.Clamp(f, _cfg.MinInterval, 2f);
                    break;
            }
            SaveData();
            if (_menuOpen.Contains(player.userID)) BuildMenu(player);
        }

        // ── Компонент отрисовки ─────────────────────────────────────────────

        private sealed class EspDrawer : MonoBehaviour
        {
            private BasePlayer _self;
            private EspSettings _s;
            private float _acc;

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
            }

            private void DrawList(IEnumerable<BasePlayer> list, float ttl)
            {
                var cfg = _i._cfg;
                foreach (var t in list)
                {
                    if (t == null || t == _self || t.transform == null) continue;
                    var dist = Vector3.Distance(t.transform.position, _self.transform.position);
                    if (dist < 2f || dist > _s.Distance) continue;
                    if (t.IsAdmin && !_s.ShowAdmins) continue;

                    var sleeping = t.IsSleeping();
                    var color = sleeping ? Color.white : Color.cyan;
                    var aimed = false;

                    // цель смотрит на админа: маленький угол между взглядом и направлением на нас
                    if (cfg.AimWarning && !sleeping && t.eyes != null)
                    {
                        var toMe = _self.eyes.position - t.eyes.position;
                        if (toMe.magnitude <= _s.EyeLength &&
                            Vector3.Angle(t.eyes.HeadRay().direction, toMe) < 6f)
                        {
                            aimed = true;
                            color = Color.red;
                        }
                    }

                    if (_s.Names)
                    {
                        var text = $"{(aimed ? "⚠ " : "")}{t.displayName} ({dist:0} м)\n{(sleeping ? "СПИТ · " : "")}{t.health:0} HP";
                        if (_s.Weapon && !sleeping)
                        {
                            var held = t.GetActiveItem();
                            var wname = held?.info?.displayName?.english ?? held?.info?.shortname;
                            if (!string.IsNullOrEmpty(wname)) text += $" · {wname}";
                        }
                        DDraw("ddraw.text", ttl, color, t.eyes.position + new Vector3(0, 0.45f, 0), text);
                    }
                    if (_s.Boxes)
                        _self.SendConsoleCommand("ddraw.box", ttl, color,
                            t.transform.position + new Vector3(0, 1f, 0),
                            BasePlayer.GetHeight(t.modelState.ducked));
                    if (_s.EyeLine && !sleeping && t.eyes != null)
                        _self.SendConsoleCommand("ddraw.line", ttl, aimed ? Color.red : new Color(1f, 0.8f, 0.2f),
                            t.eyes.position, t.eyes.position + t.eyes.HeadRay().direction * _s.EyeLength);
                }
            }

            private void DDraw(string cmd, float ttl, Color color, Vector3 pos, string text) =>
                _self.SendConsoleCommand(cmd, ttl, color, pos, text);
        }

        // ── HUD: кнопка и меню ──────────────────────────────────────────────

        private void BuildButton(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiButton);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = ColPanel },
                RectTransform = { AnchorMin = "0.008 0.022", AnchorMax = "0.066 0.052" },
                CursorEnabled = false
            }, "Hud.Menu", UiButton);
            ui.Add(new CuiButton
            {
                Button = { Color = "0 0 0 0", Command = "octaesp.ui" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Text = { Text = "◆ ESP", Color = ColAccent, FontSize = 13, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" }
            }, UiButton);
            CuiHelper.AddUi(player, ui);
        }

        private void ToggleMenu(BasePlayer player)
        {
            if (_menuOpen.Remove(player.userID))
            {
                CuiHelper.DestroyUi(player, UiMenu);
                return;
            }
            _menuOpen.Add(player.userID);
            BuildMenu(player);
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // якоря i-й строки сверху вниз; header занимает верхние 12% панели
        private static (string min, string max) RowAnchors(int i, int total)
        {
            const float top = 0.88f;
            var h = top / total;
            var y2 = top - h * i - 0.004f;
            var y1 = y2 - h + 0.008f;
            return ($"0.03 {F(y1)}", $"0.97 {F(y2)}");
        }

        private void AddToggleRow(CuiElementContainer ui, int i, int total, string label, bool on, string key)
        {
            var (min, max) = RowAnchors(i, total);
            var row = $"{UiMenu}.row{i}";
            ui.Add(new CuiPanel { Image = { Color = ColRow }, RectTransform = { AnchorMin = min, AnchorMax = max } }, UiMenu, row);
            ui.Add(new CuiElement
            {
                Parent = row,
                Components =
                {
                    new CuiTextComponent { Text = label, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = ColText },
                    new CuiRectTransformComponent { AnchorMin = "0.04 0", AnchorMax = "0.7 1" }
                }
            });
            ui.Add(new CuiButton
            {
                Button = { Color = on ? ColOn : ColOff, Command = $"octaesp.set {key} {(!on).ToString().ToLowerInvariant()}" },
                RectTransform = { AnchorMin = "0.72 0.12", AnchorMax = "0.97 0.88" },
                Text = { Text = on ? "ON" : "OFF", FontSize = 11, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" }
            }, row);
        }

        private void AddValueRow(CuiElementContainer ui, int i, int total, string label, string value, string key, float minus, float plus)
        {
            var (min, max) = RowAnchors(i, total);
            var row = $"{UiMenu}.row{i}";
            ui.Add(new CuiPanel { Image = { Color = ColRow }, RectTransform = { AnchorMin = min, AnchorMax = max } }, UiMenu, row);
            ui.Add(new CuiElement
            {
                Parent = row,
                Components =
                {
                    new CuiTextComponent { Text = label, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = ColText },
                    new CuiRectTransformComponent { AnchorMin = "0.04 0", AnchorMax = "0.55 1" }
                }
            });
            ui.Add(new CuiButton
            {
                Button = { Color = "1 1 1 0.15", Command = $"octaesp.set {key} {F(minus)}" },
                RectTransform = { AnchorMin = "0.58 0.12", AnchorMax = "0.70 0.88" },
                Text = { Text = "−", FontSize = 15, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" }
            }, row);
            ui.Add(new CuiElement
            {
                Parent = row,
                Components =
                {
                    new CuiTextComponent { Text = value, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColAccent },
                    new CuiRectTransformComponent { AnchorMin = "0.70 0", AnchorMax = "0.85 1" }
                }
            });
            ui.Add(new CuiButton
            {
                Button = { Color = "1 1 1 0.15", Command = $"octaesp.set {key} {F(plus)}" },
                RectTransform = { AnchorMin = "0.85 0.12", AnchorMax = "0.97 0.88" },
                Text = { Text = "+", FontSize = 15, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" }
            }, row);
        }

        private void BuildMenu(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiMenu);
            var s = Get(player);
            const int total = 10; // включить + 6 тоглов + 3 числовых
            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image = { Color = ColPanel },
                RectTransform = { AnchorMin = "0.072 0.02", AnchorMax = "0.30 0.40" },
                CursorEnabled = true
            }, "Hud.Menu", UiMenu);

            ui.Add(new CuiElement
            {
                Parent = UiMenu,
                Components =
                {
                    new CuiTextComponent { Text = "◆ OCTA ESP", FontSize = 14, Align = TextAnchor.MiddleCenter,
                                           Color = ColAccent, Font = "robotocondensed-bold.ttf" },
                    new CuiRectTransformComponent { AnchorMin = "0 0.88", AnchorMax = "1 1" }
                }
            });

            var i = 0;
            // главный выключатель — первой строкой
            var (min, max) = RowAnchors(i++, total);
            ui.Add(new CuiButton
            {
                Button = { Color = s.Enabled ? ColOff : ColOn, Command = $"octaesp.set enable {(!s.Enabled).ToString().ToLowerInvariant()}" },
                RectTransform = { AnchorMin = min, AnchorMax = max },
                Text = { Text = s.Enabled ? "ВЫКЛЮЧИТЬ ESP" : "ВКЛЮЧИТЬ ESP", FontSize = 13,
                         Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" }
            }, UiMenu);

            AddToggleRow(ui, i++, total, "Имена и здоровье", s.Names, "names");
            AddToggleRow(ui, i++, total, "Боксы", s.Boxes, "boxes");
            AddToggleRow(ui, i++, total, "Линия взгляда", s.EyeLine, "eyeline");
            AddToggleRow(ui, i++, total, "Спящие игроки", s.Sleepers, "sleepers");
            AddToggleRow(ui, i++, total, "Показывать админов", s.ShowAdmins, "admins");
            AddToggleRow(ui, i++, total, "Оружие в руках", s.Weapon, "weapon");
            AddValueRow(ui, i++, total, "Дистанция, м", s.Distance.ToString(), "distance", s.Distance - 50, s.Distance + 50);
            AddValueRow(ui, i++, total, "Длина линии, м", s.EyeLength.ToString(), "eyelen", s.EyeLength - 10, s.EyeLength + 10);
            AddValueRow(ui, i++, total, "Интервал, сек", s.Interval.ToString("0.0"), "interval", s.Interval - 0.05f, s.Interval + 0.05f);

            CuiHelper.AddUi(player, ui);
        }

        private void DestroyUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiButton);
            CuiHelper.DestroyUi(player, UiMenu);
        }
    }
}
