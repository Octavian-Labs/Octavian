// OctVIP — система VIP-уровней OctavianRSM.
// 7 уровней: Base, VIP Bronze, VIP Silver, VIP Gold, VIP Brilliant, VIP Moderator, VIP Admin.
// Уровень = запись в data/OctVIP/players.json (с опциональным сроком) ИЛИ пермишен octavip.<level>.
// Экспортирует API для других плагинов: GetVipLevel / GetVipLevelName / GetVipColor /
// HasVipLevel / GetVipExpiry / SetVipLevel. Вещает хук OnVipLevelChanged(player, old, new).
// UI на Chaos UIFramework (требуется Oxide.Ext.Chaos).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
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
    [Info("OctVIP", "Octavian", "1.3.2")]
    [Description("Система VIP-уровней: 7 уровней, временная выдача, Chaos-панель, приветствие с инфой, API для плагинов")]
    public class OctVIP : ChaosPlugin
    {
        [Chaos.Permission] private const string PermManage = "octavip.manage";

        private const string UiPanel = "OCTAVIP_UI";

        private CommandCallbackHandler m_Callbacks;
        private Datafile<VipData> m_Data;

        [PluginReference] private Plugin Clans;
        [PluginReference] private Plugin ImageLibrary;

        // имя эмодзи (без двоеточий) → PNG-ключ ImageLibrary / регистрация в процессе
        private readonly Dictionary<string, string> _imgKeys = new();
        private readonly HashSet<string> _imgPending = new();

        // ── Уровни ──────────────────────────────────────────────────────────

        // порядок = приоритет, индекс массива = уровень
        private static readonly string[] LevelPerm = { "", "octavip.bronze", "octavip.silver", "octavip.gold", "octavip.brilliant", "octavip.moderator", "octavip.admin" };

        private class LevelDef
        {
            public string Name;
            public string Color;   // hex для чата/UI
            public string Group;   // oxide-группа для синхронизации ("" — не синкать)
        }

        private static readonly LevelDef[] Levels =
        {
            new LevelDef { Name = "Base",          Color = "#8A8F98", Group = "" },
            new LevelDef { Name = "VIP Bronze",    Color = "#CD7F32", Group = "vip_bronze" },
            new LevelDef { Name = "VIP Silver",    Color = "#C0C0C0", Group = "vip_silver" },
            new LevelDef { Name = "VIP Gold",      Color = "#FFD700", Group = "vip_gold" },
            new LevelDef { Name = "VIP Brilliant", Color = "#4FC3F7", Group = "vip_brilliant" },
            new LevelDef { Name = "VIP Moderator", Color = "#59C073", Group = "vip_moderator" },
            new LevelDef { Name = "VIP Admin",     Color = "#E06666", Group = "vip_admin" },
        };

        // ключи наборов иконок в конфиге по индексу уровня
        private static readonly string[] IconKeys = { "Base", "Bronze", "Silver", "Gold", "Brilliant", "Moderator", "Admin" };

        private List<string> IconSet(int level) =>
            Configuration.ChatIcons.TryGetValue(IconKeys[level], out var s) && s != null
                ? s : new List<string>();

        /// <summary>Иконка чата игрока: выбранная (валидная для уровня) или дефолтная; "" = без иконки.</summary>
        private string GetIcon(ulong userId, int level)
        {
            var set = IconSet(level);
            if (set.Count == 0) return "";
            if (m_Data.Data.Icons.TryGetValue(userId, out var icon) && set.Contains(icon))
                return icon;
            return set[0];
        }

        /// <summary>Эмодзи-код вида ":vip_gold_0:" — рисуется клиентом как картинка из serveremoji.</summary>
        private static bool IsEmoji(string s) =>
            !string.IsNullOrEmpty(s) && s.Length > 2 && s[0] == ':' && s[s.Length - 1] == ':';

        /// <summary>Иконка для строки чата: эмодзи — как есть (цвет в картинке), глиф — красим цветом уровня.</summary>
        private string ChatIcon(string icon, int level) =>
            IsEmoji(icon) ? icon : $"<color={Levels[level].Color}>{icon}</color>";

        private string LevelTag(int level)
        {
            var set = IconSet(level);
            var ic = set.Count > 0 ? set[0] : "";
            return string.IsNullOrEmpty(ic) || IsEmoji(ic)
                ? $"<color={Levels[level].Color}>{Levels[level].Name}</color>"
                : $"<color={Levels[level].Color}>{ic} {Levels[level].Name}</color>";
        }

        // ── Конфиг ──────────────────────────────────────────────────────────

        private ConfigData Configuration => ConfigurationData as ConfigData;

        protected override ConfigurationFile OnLoadConfig(ref ConfigurationFile configurationFile) =>
            configurationFile = new ConfigurationFile<ConfigData>(Config);

        // При горячей загрузке Carbon не инициализирует Config, если файла
        // конфига ещё нет — Chaos затем падает с NRE в LoadConfig.
        // Сеттер internal, поэтому присваиваем DynamicConfigFile рефлексией.
        protected override void LoadConfig()
        {
            if (Config == null)
            {
                var path = Path.Combine(Manager.ConfigPath, Name + ".json");
                var cfg = new DynamicConfigFile(path);
                if (!cfg.Exists(null)) cfg.WriteObject(new ConfigData());
                typeof(Plugin).GetProperty("Config").SetValue(this, cfg);
            }
            base.LoadConfig();
        }

        protected class ConfigData : BaseConfigData
        {
            [JsonProperty("Префикс сообщений в чате")]
            public string ChatPrefix = "<color=#E8B84B>◆ OctVIP</color>";

            [JsonProperty("Синхронизировать уровни с oxide-группами (vip_bronze и т.д.)")]
            public bool MirrorGroups = true;

            [JsonProperty("Интервал проверки истёкших VIP (сек)")]
            public float ExpireCheckInterval = 3600f;

            [JsonProperty("Приветствие при входе: минимальный уровень")]
            public int WelcomeMinLevel = 1;

            [JsonProperty("Приветствие: показывать клан с уровня")]
            public int ShowClanMinLevel = 1;

            [JsonProperty("Приветствие: показывать онлайн клана с уровня")]
            public int ShowClanOnlineMinLevel = 2;

            [JsonProperty("Приветствие: показывать кол-во шкафов с уровня")]
            public int ShowBasesMinLevel = 3;

            [JsonProperty("Анонсировать вход VIP в общий чат с уровня")]
            public int BroadcastJoinMinLevel = 3;

            [JsonProperty("Анонсировать выдачу VIP в общий чат")]
            public bool BroadcastGrant = true;

            [JsonProperty("Выдача временных VIP обладателям уровней: макс. выдаваемый уровень для Moderator")]
            public int ModeratorGrantMaxLevel = 1;

            [JsonProperty("Выдача временных VIP: макс. уровень для Admin")]
            public int AdminGrantMaxLevel = 3;

            [JsonProperty("Выдача временных VIP: макс. срок в днях")]
            public int TempGrantMaxDays = 7;

            [JsonProperty("Чат: звезда и цвет имени по уровню")]
            public bool FormatChat = true;

            [JsonProperty("Чат: показывать клан-тег перед именем (Clans)")]
            public bool ChatClanTag = true;

            [JsonProperty("Чат: звёздочка у игроков без VIP (уровень 0)")]
            public bool ChatStarForBase = true;

            [JsonProperty("Панель: вкладка «Мой статус» (выбор иконки чата)")]
            public bool TabProfile = true;

            [JsonProperty("Панель: вкладка «Игроки» (управление, octavip.manage)")]
            public bool TabPlayers = true;

            // Наборы иконок чата. Два типа значений:
            //  - ":vip_gold_0:" — серверный эмодзи (PNG/JPG в serveremoji рядом с
            //    cfg identity; чат рисует картинку, цвет уже в файле). Генератор:
            //    gen-vip-badges.ps1. Клиенты видят после переподключения.
            //  - "★" — Unicode-глиф: работает без файлов, но чатовый шрифт Rust
            //    рисует лишь базовый набор (★ ☆ ● ○ ◆ ◇ ♦ ■ ▲ ► ♥ ♠ ♣),
            //    экзотика показывается как "?".
            [JsonProperty("Иконки чата по уровням: набор вариантов для каждого статуса (глифы или :эмодзи:)")]
            public Dictionary<string, List<string>> ChatIcons = new()
            {
                ["Base"]      = new List<string>(),                    // без иконки
                ["Bronze"]    = new List<string> { ":vip_bronze_0:" }, // 🌟
                ["Silver"]    = new List<string> { ":vip_silver_0:" }, // 🔘
                ["Gold"]      = new List<string> { ":vip_gold_0:" },   // 🔥
                ["Brilliant"] = new List<string> { ":vip_brilliant_0:" }, // 💎
                ["Moderator"] = new List<string> { ":vip_moderator_0:" }, // 🛡
                ["Admin"]     = new List<string> { ":vip_admin_0:" },  // ⚡
            };


        }

        // ── Данные ──────────────────────────────────────────────────────────

        protected class VipEntry
        {
            public int Level;                 // 1-6
            public string GrantedBy = "";     // кто выдал
            public long GrantedAt;            // unix
            public long ExpiresAt;            // unix, 0 = навсегда
        }

        protected class VipData
        {
            public Dictionary<ulong, VipEntry> Players = new();
            public Dictionary<ulong, string> Icons = new();   // выбранная иконка чата
        }

        // ── Жизненный цикл ──────────────────────────────────────────────────

        protected override void PopulatePhrases() => m_Messages = new Dictionary<string, string>();

        private void Init()
        {
            m_Callbacks = new CommandCallbackHandler(this);
            m_Data = new Datafile<VipData>("OctVIP/players");
            cmd.AddChatCommand("vip", this, CmdVip);

            foreach (var p in LevelPerm)
                if (!string.IsNullOrEmpty(p)) permission.RegisterPermission(p, this);

            if (Configuration.MirrorGroups)
                foreach (var l in Levels.Skip(1))
                    if (!string.IsNullOrEmpty(l.Group) && !permission.GroupExists(l.Group))
                        permission.CreateGroup(l.Group, l.Name, l.Name == "VIP Admin" ? 60 : Array.IndexOf(Levels, l) * 10);

            timer.Every(Mathf.Max(60f, Configuration.ExpireCheckInterval), CheckExpirations);
        }

        private void OnServerInitialized()
        {
            // заставляем сервер пересканировать serveremoji — файлы могли
            // добавиться между рестартами; клиенты увидят после перезахода
            try { ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.resetserveremoji"); }
            catch { }

            EnsureEmojiImages();

            // отложенная сверка — плагин мог загрузиться после подключения игроков
            timer.Once(3f, () =>
            {
                foreach (var p in BasePlayer.activePlayerList) WelcomePlayer(p);
                CheckExpirations();
            });
        }

        /// <summary>
        /// Скармливаем PNG эмодзи из serveremoji в ImageLibrary — тогда панель
        /// может показать ту же картинку, что видна в чате. Идемпотентно.
        /// </summary>
        private void EnsureEmojiImages()
        {
            if (ImageLibrary == null || !ImageLibrary.IsLoaded) return;
            try
            {
                var dir = Path.Combine(Carbon.Core.Defines.GetRustIdentityFolder(), "serveremoji");
                foreach (var kv in Configuration.ChatIcons)
                foreach (var ic in kv.Value)
                {
                    if (!IsEmoji(ic)) continue;
                    var name = ic.Substring(1, ic.Length - 2);
                    if (_imgKeys.ContainsKey(name) || !_imgPending.Add(name)) continue;

                    var file = Path.Combine(dir, name + ".png");
                    if (!File.Exists(file)) file = Path.Combine(dir, name + ".jpg");
                    if (!File.Exists(file)) { _imgPending.Remove(name); continue; }

                    var bytes = File.ReadAllBytes(file);
                    ImageLibrary.Call("AddImageData", $"octvip.{name}", bytes, 0UL, (Action)(() =>
                        _imgKeys[name] = ImageLibrary.Call("GetImage", $"octvip.{name}", 0UL) as string));
                }
            }
            catch { }
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList) ChaosUI.Destroy(p, UiPanel);
            m_Data.Save();
        }

        private void OnServerSave() => m_Data.Save();

        // ── Ядро уровней ────────────────────────────────────────────────────

        private static long NowUnix => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private bool IsExpired(VipEntry e) => e.ExpiresAt > 0 && e.ExpiresAt <= NowUnix;

        /// <summary>Текущий уровень игрока: максимум из записи (живой) и пермишенов.</summary>
        private int ResolveLevel(ulong userId)
        {
            var lvl = 0;
            if (m_Data.Data.Players.TryGetValue(userId, out var e) && e != null && !IsExpired(e) && e.Level > lvl)
                lvl = e.Level;

            var idStr = userId.ToString();
            for (var i = LevelPerm.Length - 1; i >= 1; i--)
                if (permission.UserHasPermission(idStr, LevelPerm[i])) { lvl = Math.Max(lvl, i); break; }

            return lvl;
        }

        /// <summary>Выдать/изменить уровень. durationSec 0 — навсегда. Возвращает старый уровень.</summary>
        private int SetLevel(ulong userId, int level, long durationSec, string grantedBy)
        {
            var old = ResolveLevel(userId);

            if (level <= 0)
                m_Data.Data.Players.Remove(userId);
            else
                m_Data.Data.Players[userId] = new VipEntry
                {
                    Level = level,
                    GrantedBy = grantedBy,
                    GrantedAt = NowUnix,
                    ExpiresAt = durationSec > 0 ? NowUnix + durationSec : 0
                };
            m_Data.Save();

            var now = ResolveLevel(userId);
            SyncGroups(userId, now);

            var player = BasePlayer.FindByID(userId) ?? BasePlayer.FindSleeping(userId);
            if (player != null && old != now)
                Interface.Oxide.CallHook("OnVipLevelChanged", player, old, now);

            return old;
        }

        /// <summary>Снять/поставить oxide-группу по уровню.</summary>
        private void SyncGroups(ulong userId, int level)
        {
            if (!Configuration.MirrorGroups) return;
            var id = userId.ToString();
            for (var i = 1; i < Levels.Length; i++)
            {
                var g = Levels[i].Group;
                if (string.IsNullOrEmpty(g) || !permission.GroupExists(g)) continue;
                var has = permission.UserHasGroup(id, g);
                if (i == level && !has) permission.AddUserGroup(id, g);
                else if (i != level && has) permission.RemoveUserGroup(id, g);
            }
        }

        /// <summary>Почасовая проверка истёкших VIP.</summary>
        private void CheckExpirations()
        {
            var expired = m_Data.Data.Players
                .Where(kv => kv.Value != null && IsExpired(kv.Value))
                .Select(kv => kv.Key).ToList();
            foreach (var id in expired)
            {
                var e = m_Data.Data.Players[id];
                m_Data.Data.Players.Remove(id);
                SyncGroups(id, ResolveLevel(id));

                var player = BasePlayer.FindByID(id);
                if (player != null)
                {
                    Msg(player, $"Срок {LevelTag(e.Level)} истёк — спасибо, что были с нами!");
                    Interface.Oxide.CallHook("OnVipExpired", player, e.Level);
                    if (e.Level != ResolveLevel(id))
                        Interface.Oxide.CallHook("OnVipLevelChanged", player, e.Level, ResolveLevel(id));
                }
            }
            if (expired.Count > 0) m_Data.Save();
        }

        // ── Парсинг срока ───────────────────────────────────────────────────

        /// <summary>"30d" / "12h" / "45m" / "perm"/"" — в секунды. 0 = навсегда.</summary>
        private static bool TryParseDuration(string s, out long seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(s)) return true;
            s = s.Trim().ToLowerInvariant();
            if (s is "perm" or "permament" or "forever" or "навсегда" or "0") return true;

            long mult = 86400; // по умолчанию — дни
            if (s.EndsWith("d")) { s = s[..^1]; }
            else if (s.EndsWith("h")) { s = s[..^1]; mult = 3600; }
            else if (s.EndsWith("m")) { s = s[..^1]; mult = 60; }
            else if (s.EndsWith("w")) { s = s[..^1]; mult = 604800; }

            if (!long.TryParse(s, out var n) || n <= 0) return false;
            seconds = n * mult;
            return true;
        }

        private static string FormatDuration(long expiresAt)
        {
            if (expiresAt <= 0) return "навсегда";
            var left = expiresAt - NowUnix;
            if (left <= 0) return "истёк";
            if (left >= 86400) return $"{left / 86400}д {(left % 86400) / 3600}ч";
            if (left >= 3600) return $"{left / 3600}ч {(left % 3600) / 60}м";
            return $"{left / 60}м";
        }

        // ── Чат ─────────────────────────────────────────────────────────────

        private void Msg(BasePlayer player, string text) =>
            player.ChatMessage($"{Configuration.ChatPrefix} <color=#9AA4B2>—</color> {text}");

        private static string Val(object v) => $"<color=#FFD35C>{v}</color>";

        private void Broadcast(string text) =>
            PrintToChat($"{Configuration.ChatPrefix} <color=#9AA4B2>—</color> {text}");

        // ── Форматирование игрового чата ─────────────────────────────────
        // Звезда цвета уровня + имя цветом уровня + клан-тег. Берём на себя
        // вывод сообщения (chat.add) и глушим стандартный — как делает Clans.

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (!Configuration.FormatChat || player == null) return null;
            if (channel != ConVar.Chat.ChatChannel.Global && channel != ConVar.Chat.ChatChannel.Team) return null;

            var lvl = ResolveLevel(player.userID);
            if (lvl == 0 && !Configuration.ChatStarForBase) return null;

            var color = Levels[lvl].Color;

            var tag = "";
            if (Configuration.ChatClanTag && Clans != null)
            {
                try
                {
                    var t = Clans.Call("GetClanOf", player.userID) as string;
                    if (!string.IsNullOrEmpty(t)) tag = $"<color=#8A8F98>[{t}]</color> ";
                }
                catch { }
            }

            var icon = GetIcon(player.userID, lvl);
            var iconPart = string.IsNullOrEmpty(icon) ? "" : ChatIcon(icon, lvl) + " ";
            var line = $"{tag}{iconPart}<color={color}>{player.displayName}</color>" +
                       $"<color=#8A8F98>:</color> {message}";

            return SendChatLine(player, line, channel);
        }

        /// <summary>Разослать форматированную строку чата адресатам канала.</summary>
        private static bool SendChatLine(BasePlayer player, string line, ConVar.Chat.ChatChannel channel)
        {
            if (channel == ConVar.Chat.ChatChannel.Global)
            {
                foreach (var p in BasePlayer.activePlayerList)
                    if (p != null && p.IsConnected)
                        p.SendConsoleCommand("chat.add", (int)channel, player.UserIDString, line);
                return true;
            }

            var team = player.Team;
            if (team == null) return false;
            foreach (var p in BasePlayer.activePlayerList)
                if (p != null && p.IsConnected && p.Team != null && p.Team.teamID == team.teamID)
                    p.SendConsoleCommand("chat.add", (int)channel, player.UserIDString, line);
            return true;
        }

        // ── Приветствие при входе ───────────────────────────────────────────

        private void OnPlayerConnected(BasePlayer player) => WelcomePlayer(player);

        private void WelcomePlayer(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.Once(2f, () => WelcomePlayer(player));
                return;
            }

            var lvl = ResolveLevel(player.userID);
            if (lvl < Configuration.WelcomeMinLevel) return;

            // анонс в общий чат для высоких уровней
            if (lvl >= Configuration.BroadcastJoinMinLevel)
                Broadcast($"{LevelTag(lvl)} {player.displayName} зашёл на сервер!");

            var parts = new List<string>();
            if (m_Data.Data.Players.TryGetValue(player.userID, out var e) && e.ExpiresAt > 0)
                parts.Add($"осталось: {Val(FormatDuration(e.ExpiresAt))}");

            // клан
            if (lvl >= Configuration.ShowClanMinLevel && Clans != null)
            {
                try
                {
                    var tag = Clans.Call("GetClanOf", player.userID) as string;
                    if (!string.IsNullOrEmpty(tag))
                    {
                        if (lvl >= Configuration.ShowClanOnlineMinLevel)
                        {
                            var members = Clans.Call("GetClanMembers", player.userID) as List<string>;
                            if (members != null)
                            {
                                var online = members.Count(id =>
                                    BasePlayer.activePlayerList.Any(p => p.UserIDString == id));
                                parts.Add($"клан: {Val(tag)} ({online}/{members.Count} онлайн)");
                            }
                            else parts.Add($"клан: {Val(tag)}");
                        }
                        else parts.Add($"клан: {Val(tag)}");
                    }
                }
                catch { }
            }

            // шкафы, в которых авторизован
            if (lvl >= Configuration.ShowBasesMinLevel)
            {
                var tc = 0;
                foreach (var ent in BaseNetworkable.serverEntities)
                    if (ent is BuildingPrivlidge b && b.authorizedPlayers != null &&
                        b.authorizedPlayers.Any(x => x == player.userID)) tc++;
                if (tc > 0) parts.Add($"твои базы: {Val(tc)}");
            }

            var tail = parts.Count > 0 ? "\n<color=#9AA4B2>" + string.Join("  ·  ", parts) + "</color>" : "";
            Msg(player, $"С возвращением! Твой статус: {LevelTag(lvl)}{tail}");
        }

        // ── Чат-команда /vip ────────────────────────────────────────────────

        private void CmdVip(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var myLevel = ResolveLevel(player.userID);
            var canManage = permission.UserHasPermission(player.UserIDString, PermManage) || player.IsAdmin;

            if (args.Length == 0)
            {
                // свой статус
                var line = $"Твой статус: {LevelTag(myLevel)}";
                if (m_Data.Data.Players.TryGetValue(player.userID, out var e) && !IsExpired(e))
                    line += $"\nВыдан: {Val(DateTimeOffset.FromUnixTimeSeconds(e.GrantedAt).LocalDateTime.ToString("dd.MM.yyyy"))}" +
                            $" · до: {Val(e.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.ExpiresAt).LocalDateTime.ToString("dd.MM.yyyy HH:mm") : "навсегда")}" +
                            (string.IsNullOrEmpty(e.GrantedBy) ? "" : $" · от: {Val(e.GrantedBy)}");
                if (canManage || GrantCap(myLevel) > 0)
                    line += $"\n{Val("/vip give")} <игрок> <уровень> [срок] — выдать временный VIP";
                line += $"\n{Val("/vip panel")} — панель: статус и иконка чата";
                Msg(player, line);
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "panel":
                case "admin":
                case "ui":
                    TogglePanel(player);
                    return;

                case "give":
                {
                    var cap = GrantCap(myLevel);
                    if (!canManage && cap <= 0)
                    {
                        Msg(player, "Выдача VIP доступна только уровням Moderator/Admin или по octavip.manage");
                        return;
                    }
                    if (args.Length < 3)
                    {
                        Msg(player, $"Использование: {Val("/vip give")} <игрок> <уровень 1-6> [срок: 7d/24h, пусто = навсегда]");
                        return;
                    }

                    var target = FindPlayer(args[1]);
                    if (target == null) { Msg(player, $"Игрок {Val(args[1])} не найден."); return; }

                    if (!int.TryParse(args[2], out var lvl) || lvl < 1 || lvl > 6)
                    {
                        Msg(player, $"Уровень должен быть {Val("1-6")}: 1=Bronze, 2=Silver, 3=Gold, 4=Brilliant, 5=Moderator, 6=Admin");
                        return;
                    }

                    long dur = 0;
                    if (args.Length > 3 && !TryParseDuration(args[3], out dur))
                    {
                        Msg(player, $"Срок не распознан: {Val(args[3])} — примеры: 7d, 24h, 90m, perm");
                        return;
                    }

                    // ограничения для не-админов
                    if (!canManage)
                    {
                        if (lvl > cap)
                        {
                            Msg(player, $"Твой максимум для выдачи — {LevelTag(cap)}");
                            return;
                        }
                        var maxSec = (long)Configuration.TempGrantMaxDays * 86400;
                        if (dur <= 0 || dur > maxSec)
                        {
                            Msg(player, $"Ты можешь выдавать только временный VIP до {Val(Configuration.TempGrantMaxDays)} дней");
                            return;
                        }
                    }

                    Grant(player, target.userID, lvl, dur, target.displayName.ToString());
                    return;
                }

                default:
                    Msg(player, $"{Val("/vip")} — статус · {Val("/vip give")} — выдать · {Val("/vip panel")} — управление");
                    return;
            }
        }

        /// <summary>Макс. уровень, который может выдавать обладатель уровня lvl.</summary>
        private int GrantCap(int lvl) => lvl switch
        {
            5 => Configuration.ModeratorGrantMaxLevel,
            6 => Configuration.AdminGrantMaxLevel,
            _ => 0
        };

        private void Grant(BasePlayer granter, ulong targetId, int level, long dur, string targetName)
        {
            var old = SetLevel(targetId, level, dur, granter?.displayName.ToString() ?? "console");
            var durText = dur > 0 ? $" на {Val(FormatDuration(NowUnix + dur))}" : "";

            if (Configuration.BroadcastGrant && granter != null)
                Broadcast($"{LevelTag(level)} получает {targetName}{durText} — выдал {granter.displayName}");

            Msg(granter, $"{targetName}: {LevelTag(old)} → {LevelTag(level)}{durText}");
        }

        private static BasePlayer FindPlayer(string nameOrId)
        {
            if (ulong.TryParse(nameOrId, out var id))
            {
                var p = BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id);
                if (p != null) return p;
            }
            return BasePlayer.activePlayerList
                .Concat(BasePlayer.sleepingPlayerList)
                .FirstOrDefault(p => p.displayName.ToString().Equals(nameOrId, StringComparison.OrdinalIgnoreCase)
                                  || p.displayName.ToString().IndexOf(nameOrId, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ── Консольные команды ──────────────────────────────────────────────

        [ConsoleCommand("octavip.set")]
        private void CmdSet(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 1) return;
            if (arg.Args == null || arg.Args.Length < 2)
            {
                Puts("octavip.set <steamid|имя> <уровень 0-6> [срок: 30d/24h/perm]");
                return;
            }
            var targetName = arg.Args[0].ToString();
            var target = FindPlayer(targetName);
            ulong id;
            string name;
            if (target != null) { id = target.userID; name = target.displayName.ToString(); }
            else if (ulong.TryParse(targetName, out id)) name = targetName;
            else { Puts($"Игрок '{targetName}' не найден (для офлайн укажи steamid)"); return; }

            if (!int.TryParse(arg.Args[1].ToString(), out var lvl) || lvl < 0 || lvl > 6) { Puts("Уровень 0-6"); return; }

            long dur = 0;
            if (arg.Args.Length > 2 && !TryParseDuration(arg.Args[2].ToString(), out dur)) { Puts("Срок не распознан"); return; }

            var old = SetLevel(id, lvl, dur, "console");
            Puts($"{name} ({id}): {Levels[old].Name} -> {Levels[ResolveLevel(id)].Name}" + (dur > 0 ? $" на {FormatDuration(NowUnix + dur)}" : ""));
        }

        [ConsoleCommand("octavip.remove")]
        private void CmdRemove(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 1) return;
            if (arg.Args == null || arg.Args.Length < 1) { Puts("octavip.remove <steamid|имя>"); return; }
            var target = FindPlayer(arg.Args[0].ToString());
            ulong id = target?.userID ?? (ulong.TryParse(arg.Args[0].ToString(), out var x) ? x : 0);
            if (id == 0) { Puts("Игрок не найден"); return; }
            var old = SetLevel(id, 0, 0, "console");
            Puts($"Снят VIP у {id} (был {Levels[old].Name})");
        }

        [ConsoleCommand("octavip.list")]
        private void CmdList(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 1) return;
            foreach (var kv in m_Data.Data.Players)
            {
                var e = kv.Value;
                var p = BasePlayer.FindByID(kv.Key) ?? BasePlayer.FindSleeping(kv.Key);
                Puts($"{kv.Key} {(p?.displayName.ToString() ?? "?")}: {Levels[ResolveLevel(kv.Key)].Name}" +
                     $" [запись: {e.Level}, выдал {e.GrantedBy}, до {(e.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.ExpiresAt).LocalDateTime.ToString("dd.MM HH:mm") : "perm")}]" +
                     (IsExpired(e) ? " ИСТЁК" : ""));
            }
        }

        // ── API для других плагинов ─────────────────────────────────────────

        [HookMethod("GetVipLevel")]     public int API_GetLevel(ulong userId) => ResolveLevel(userId);
        [HookMethod("GetVipLevelName")] public string API_GetLevelName(ulong userId) => Levels[ResolveLevel(userId)].Name;
        [HookMethod("GetVipColor")]     public string API_GetColor(ulong userId) => Levels[ResolveLevel(userId)].Color;
        [HookMethod("HasVipLevel")]     public bool API_HasLevel(ulong userId, int level) => ResolveLevel(userId) >= level;
        [HookMethod("GetVipExpiry")]    public long API_GetExpiry(ulong userId) =>
            m_Data.Data.Players.TryGetValue(userId, out var e) && !IsExpired(e) ? e.ExpiresAt : 0;
        [HookMethod("SetVipLevel")]
        public bool API_SetLevel(ulong userId, int level, string duration = null)
        {
            if (level < 0 || level > 6) return false;
            long dur = 0;
            if (!string.IsNullOrEmpty(duration) && !TryParseDuration(duration, out dur)) return false;
            SetLevel(userId, level, dur, "api");
            return true;
        }

        // ── Chaos-панель управления ─────────────────────────────────────────

        private class PanelState
        {
            public string Search = "";
            public ulong Selected;
            public string Duration = "30d";
            public int Page;
            public int Tab;
        }

        private readonly Dictionary<ulong, PanelState> _panel = new();

        private void TogglePanel(BasePlayer player)
        {
            if (_panel.Remove(player.userID)) { ChaosUI.Destroy(player, UiPanel); return; }
            _panel[player.userID] = new PanelState();
            BuildPanel(player);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player != null) _panel.Remove(player.userID);
        }

        // кандидаты: онлайн + спящие + все с VIP-записью
        private List<BasePlayer> PanelPlayers(PanelState st)
        {
            var list = BasePlayer.activePlayerList
                .Concat(BasePlayer.sleepingPlayerList)
                .Where(p => string.IsNullOrEmpty(st.Search)
                         || p.displayName.ToString().IndexOf(st.Search, StringComparison.OrdinalIgnoreCase) >= 0
                         || p.UserIDString.Contains(st.Search))
                .ToList();

            // офлайн-игроки с VIP-записями — показываем псевдо-строками через id
            var extra = m_Data.Data.Players.Keys
                .Where(id => list.All(p => p.userID != id)
                          && (string.IsNullOrEmpty(st.Search) || id.ToString().Contains(st.Search)))
                .ToList();
            // вернём список (online) + офлайн-записи отдельно обработаем при рендере
            _offlineIds = extra;
            return list.OrderByDescending(p => ResolveLevel(p.userID)).ThenBy(p => p.displayName.ToString()).ToList();
        }

        private List<ulong> _offlineIds = new();

        private const int Rows = 11;
        private const int RowH = 30;

        private void BuildPanel(BasePlayer player)
        {
            if (!_panel.TryGetValue(player.userID, out var st)) st = _panel[player.userID] = new PanelState();

            var root = ChaosPrefab.Background(UiPanel, Layer.HudMenu, UIAnchor.Center,
                new Offset(-280, -260, 280, 260), m_PanelStyle);

            root.WithChildren(body =>
            {
                ChaosPrefab.Title(body, UIAnchor.TopStretch, new Offset(8, -34, -44, -8), "◆ OCTAVIAN VIP", m_TitleStyle);
                ChaosPrefab.CloseButton(body, UIAnchor.TopRight, new Offset(-36, -34, -8, -6), ChaosStyle.BlackOutline)
                    .WithCallback(m_Callbacks, arg => TogglePanel(player), $"{player.userID}.close");

                // ── вкладки ──
                var canManage = permission.UserHasPermission(player.UserIDString, PermManage) || player.IsAdmin;
                var tabs = new List<(int id, string name)>();
                if (Configuration.TabProfile) tabs.Add((0, "МОЙ СТАТУС"));
                if (Configuration.TabPlayers && canManage) tabs.Add((1, "ИГРОКИ"));
                if (tabs.Count == 0)
                {
                    TextContainer.Create(body, "<color=#8A8F98>нет доступных вкладок</color>", m_LabelStyle,
                        UIAnchor.Center, new Offset(-120, -10, 120, 10));
                    return;
                }
                if (tabs.All(t => t.id != st.Tab)) st.Tab = tabs[0].id;

                for (var ti = 0; ti < tabs.Count; ti++)
                {
                    var (tid, tname) = tabs[ti];
                    var active = st.Tab == tid;
                    var ts = new Style(ChaosStyle.Button)
                    {
                        FontSize = 12,
                        ImageColor = active ? new UIColor(0.91f, 0.72f, 0.29f, 0.22f) : new UIColor(0.14f, 0.16f, 0.19f, 0.95f),
                        FontColor = active ? new UIColor(0.91f, 0.72f, 0.29f, 1f) : new UIColor(0.6f, 0.63f, 0.68f, 1f)
                    };
                    if (active) ts.Font = Font.RobotoCondensedBold;
                    ChaosPrefab.TextButton(body, UIAnchor.TopLeft, new Offset(8 + ti * 128, -70, 8 + ti * 128 + 122, -44),
                            tname, ts, active ? ChaosStyle.GreenOutline : ChaosStyle.BlackOutline)
                        .WithCallback(m_Callbacks, arg => { st.Tab = tid; BuildPanel(player); }, $"{player.userID}.tab.{tid}");
                }

                if (st.Tab == 0)
                {
                    // ── вкладка «Мой статус»: уровень + выбор иконки чата ──
                    var myLvl = ResolveLevel(player.userID);
                    TextContainer.Create(body, $"Твой статус: {LevelTag(myLvl)}", m_LabelStyle,
                        UIAnchor.TopLeft, new Offset(16, -104, 536, -80));

                    if (m_Data.Data.Players.TryGetValue(player.userID, out var me) && !IsExpired(me))
                        TextContainer.Create(body,
                            $"Выдан: {DateTimeOffset.FromUnixTimeSeconds(me.GrantedAt).LocalDateTime:dd.MM.yyyy} · " +
                            $"до: {(me.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(me.ExpiresAt).LocalDateTime.ToString("dd.MM.yyyy HH:mm") : "навсегда")} · " +
                            $"от: {me.GrantedBy}",
                            m_SmallStyle, UIAnchor.TopLeft, new Offset(16, -128, 536, -108));

                    TextContainer.Create(body, "Иконка в чате:", m_LabelStyle,
                        UIAnchor.TopLeft, new Offset(16, -164, 200, -140));

                    var set = IconSet(myLvl);
                    var cur = GetIcon(player.userID, myLvl);
                    EnsureEmojiImages(); // ленивая регистрация — ImageLibrary мог подгрузиться позже

                    if (set.Count == 0)
                    {
                        TextContainer.Create(body, "<color=#8A8F98>— у твоего уровня нет иконки —</color>",
                            m_SmallStyle, UIAnchor.TopLeft, new Offset(16, -190, 536, -170));
                    }
                    for (var i = 0; i < set.Count; i++)
                    {
                        var ic = set[i];
                        var on = ic == cur;
                        var isEmoji = IsEmoji(ic);
                        var ist = new Style(ChaosStyle.Button)
                        {
                            ImageColor = new UIColor(0.16f, 0.18f, 0.22f, 0.95f),
                            FontColor = on ? HexUi(Levels[myLvl].Color) : new UIColor(0.72f, 0.75f, 0.8f, 1f),
                            FontSize = 18
                        };
                        if (on) ist.Font = Font.RobotoCondensedBold;
                        ChaosPrefab.TextButton(body, UIAnchor.TopLeft,
                                new Offset(16 + i * 52, -210, 16 + i * 52 + 46, -170), isEmoji ? "" : ic, ist,
                                on ? ChaosStyle.GreenOutline : ChaosStyle.BlackOutline)
                            .WithCallback(m_Callbacks, arg =>
                            {
                                m_Data.Data.Icons[player.userID] = ic;
                                m_Data.Save();
                                BuildPanel(player);
                            }, $"{player.userID}.icon.{i}");

                        // картинка эмодзи поверх кнопки — через зарегистрированный PNG
                        if (isEmoji && _imgKeys.TryGetValue(ic.Substring(1, ic.Length - 2), out var imgKey))
                            RawImageContainer.Create(body, UIAnchor.TopLeft,
                                    new Offset(16 + i * 52 + 7, -203, 16 + i * 52 + 39, -177))
                                .WithPNG(imgKey);
                    }

                    // превью: для эмодзи показываем картинку, для глифа — символ
                    if (!string.IsNullOrEmpty(cur))
                    {
                        TextContainer.Create(body, "Как выглядит:", m_SmallStyle,
                            UIAnchor.TopLeft, new Offset(16, -246, 96, -222));

                        string prevKey = null;
                        var emojiPrev = IsEmoji(cur) &&
                                        _imgKeys.TryGetValue(cur.Substring(1, cur.Length - 2), out prevKey);
                        if (emojiPrev)
                            RawImageContainer.Create(body, UIAnchor.TopLeft, new Offset(96, -244, 116, -224))
                                .WithPNG(prevKey);

                        TextContainer.Create(body,
                            $"{(emojiPrev ? "" : $"<color={Levels[myLvl].Color}>{cur}</color> ")}<color={Levels[myLvl].Color}>{player.displayName}</color><color=#8A8F98>:</color> привет, мир!",
                            m_SmallStyle, UIAnchor.TopLeft, new Offset(emojiPrev ? 120 : 96, -246, 540, -222));
                    }
                }
                else
                {

                // поиск
                ChaosPrefab.Input(body, UIAnchor.TopStretch, new Offset(8, -102, -8, -76), st.Search)
                    .WithCallback(m_Callbacks, arg =>
                    {
                        st.Search = arg.Args.Length > 1 ? string.Join(" ", arg.Args.Skip(1)) : "";
                        st.Page = 0;
                        BuildPanel(player);
                    }, $"{player.userID}.search");

                // ── список игроков (левая колонка) ──
                var players = PanelPlayers(st);
                var totalRows = players.Count + _offlineIds.Count;
                var maxPage = Math.Max(0, (totalRows - 1) / Rows);
                st.Page = Math.Clamp(st.Page, 0, maxPage);

                var y = 110;
                var rowIdx = 0;
                var skip = st.Page * Rows;
                var shown = 0;

                foreach (var p in players.Concat(_offlineIds.Select(id => (BasePlayer)null)))
                {
                    if (shown >= Rows) break;

                    string line; ulong id; int lvl;
                    if (p != null) { id = p.userID; lvl = ResolveLevel(id); line = p.displayName.ToString(); }
                    else
                    {
                        id = _offlineIds[rowIdx - players.Count];
                        lvl = ResolveLevel(id);
                        var e = m_Data.Data.Players[id];
                        line = $"{id} <color=#8A8F98>(оффлайн)</color>";
                        _ = e;
                    }
                    if (rowIdx++ < skip) continue;
                    shown++;

                    var yy = y;
                    var sel = st.Selected == id;
                    ChaosPrefab.TextButton(body, UIAnchor.TopStretch, new Offset(8, -(yy + 26), -292, -yy),
                            "", sel ? m_SelRowStyle : m_RowStyle, null);

                    TextContainer.Create(body, $"{line}  {LevelTag(lvl)}", m_LabelStyle,
                        UIAnchor.TopStretch, new Offset(16, -(yy + 23), -290, -yy - 3));

                    // прозрачная кнопка поверх всей строки — иначе текст съедает клики
                    ButtonContainer.Create(body, UIAnchor.TopStretch, new Offset(8, -(yy + 26), -292, -yy))
                        .WithColor(UIColor.Clear)
                        .WithCallback(m_Callbacks, arg => { st.Selected = id; BuildPanel(player); }, $"{player.userID}.sel.{id}");
                    y += RowH;
                }

                if (totalRows == 0)
                    TextContainer.Create(body, "<color=#8A8F98>— никого не найдено —</color>", m_LabelStyle,
                        UIAnchor.TopStretch, new Offset(16, -144, -16, -118));

                // пейджинг
                if (maxPage > 0)
                {
                    ChaosPrefab.TextButton(body, UIAnchor.BottomLeft, new Offset(8, 8, 60, 30), "◀", m_LabelStyle, ChaosStyle.BlackOutline)
                        .WithCallback(m_Callbacks, arg => { st.Page--; BuildPanel(player); }, $"{player.userID}.prev");
                    TextContainer.Create(body, $"{st.Page + 1}/{maxPage + 1}", m_ValueStyle,
                        UIAnchor.BottomLeft, new Offset(60, 10, 120, 30));
                    ChaosPrefab.TextButton(body, UIAnchor.BottomLeft, new Offset(124, 8, 176, 30), "▶", m_LabelStyle, ChaosStyle.BlackOutline)
                        .WithCallback(m_Callbacks, arg => { st.Page++; BuildPanel(player); }, $"{player.userID}.next");
                }

                // ── правая колонка: управление выбранным ──
                var rx = 288; // левый край правой колонки
                if (st.Selected != 0)
                {
                    var selP = BasePlayer.FindByID(st.Selected) ?? BasePlayer.FindSleeping(st.Selected);
                    var selName = selP?.displayName.ToString() ?? st.Selected.ToString();
                    var selLvl = ResolveLevel(st.Selected);

                    TextContainer.Create(body, $"Игрок: {selName}", m_LabelStyle,
                        UIAnchor.TopLeft, new Offset(rx, -122, 536, -98));
                    TextContainer.Create(body, $"Статус: {LevelTag(selLvl)}", m_LabelStyle,
                        UIAnchor.TopLeft, new Offset(rx, -146, 536, -122));

                    if (m_Data.Data.Players.TryGetValue(st.Selected, out var e) && !IsExpired(e))
                        TextContainer.Create(body,
                            $"Выдан: {DateTimeOffset.FromUnixTimeSeconds(e.GrantedAt).LocalDateTime:dd.MM.yyyy} · " +
                            $"до: {(e.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.ExpiresAt).LocalDateTime.ToString("dd.MM.yyyy") : "perm")} · " +
                            $"от: {e.GrantedBy}",
                            m_SmallStyle, UIAnchor.TopLeft, new Offset(rx, -166, 536, -146));

                    // кнопки уровней — сетка 2 ряда
                    for (var i = 0; i <= 6; i++)
                    {
                        var lvl = i;
                        var col = i % 4;
                        var rowI = i / 4;
                        var bx = rx + col * 62;
                        var by = 184 + rowI * 34;
                        var label = lvl == 0 ? "Снять" : Levels[lvl].Name.Replace("VIP ", "");
                        var isCurrent = selLvl == lvl;
                        var style = new Style(ChaosStyle.Button)
                        {
                            ImageColor = new UIColor(0.16f, 0.18f, 0.22f, 0.95f),
                            FontColor = isCurrent ? HexUi(Levels[lvl].Color) : new UIColor(0.72f, 0.75f, 0.8f, 1f),
                            FontSize = 11
                        };
                        if (isCurrent)
                        {
                            style.Font = Font.RobotoCondensedBold;
                            label = $"✓ {label}";
                        }
                        var outline = isCurrent ? ChaosStyle.GreenOutline : ChaosStyle.BlackOutline;
                        ChaosPrefab.TextButton(body, UIAnchor.TopLeft, new Offset(bx, -(by + 28), bx + 58, -by),
                                label, style, outline)
                            .WithCallback(m_Callbacks, arg =>
                            {
                                var old = SetLevel(st.Selected, lvl, 0, player.displayName.ToString());
                                var p2 = BasePlayer.FindByID(st.Selected) ?? BasePlayer.FindSleeping(st.Selected);
                                if (Configuration.BroadcastGrant && lvl > old)
                                    Broadcast($"{LevelTag(lvl)} получает {(p2?.displayName.ToString() ?? st.Selected.ToString())} — выдал {player.displayName}");
                                BuildPanel(player);
                            }, $"{player.userID}.lvl.{i}");
                    }

                    // срок + кнопки
                    TextContainer.Create(body, "Срок:", m_LabelStyle, UIAnchor.TopLeft, new Offset(rx, -276, rx + 44, -254));
                    ChaosPrefab.Input(body, UIAnchor.TopLeft, new Offset(rx + 44, -274, rx + 120, -252), st.Duration)
                        .WithCallback(m_Callbacks, arg =>
                        {
                            st.Duration = arg.Args.Length > 1 ? arg.Args[1].ToString() : "";
                        }, $"{player.userID}.dur");

                    var di = 0;
                    foreach (var d in new[] { "7d", "30d", "perm" })
                    {
                        var dd = d;
                        ChaosPrefab.TextButton(body, UIAnchor.TopLeft, new Offset(rx + 126 + di * 44, -274, rx + 164 + di * 44, -252),
                                dd, m_LabelStyle, ChaosStyle.BlackOutline)
                            .WithCallback(m_Callbacks, arg =>
                            {
                                st.Duration = dd == "perm" ? "" : dd;
                                BuildPanel(player);
                            }, $"{player.userID}.dur.{dd}");
                        di++;
                    }

                    // применить срок
                    ChaosPrefab.TextButton(body, UIAnchor.TopLeft, new Offset(rx, -318, rx + 200, -286),
                            "ВЫДАТЬ СРОК + УРОВЕНЬ", m_MasterOnStyle, ChaosStyle.BlackOutline)
                        .WithCallback(m_Callbacks, arg =>
                        {
                            if (!TryParseDuration(st.Duration, out var dur))
                            {
                                Msg(player, $"Срок {Val(st.Duration)} не распознан (7d, 24h, perm)");
                                return;
                            }
                            var lvl2 = ResolveLevel(st.Selected);
                            if (lvl2 <= 0) { Msg(player, "Сначала выбери уровень кнопками выше"); return; }
                            SetLevel(st.Selected, lvl2, dur, player.displayName.ToString());
                            BuildPanel(player);
                        }, $"{player.userID}.apply");
                }
                else
                {
                    TextContainer.Create(body, "<color=#8A8F98>Выбери игрока слева</color>", m_LabelStyle,
                        UIAnchor.TopLeft, new Offset(rx, -174, 540, -134));
                }
                }
            })
            .NeedsCursor()
            .DestroyExisting();

            ChaosUI.Show(player, root);
        }

        private static UIColor HexUi(string hex)
        {
            if (ColorUtility.TryParseHtmlString(hex, out var c))
                return new UIColor(c.r, c.g, c.b, 1f);
            return new UIColor(1, 1, 1, 1);
        }

        // ── Стили ───────────────────────────────────────────────────────────

        private static readonly Style m_PanelStyle = new Style(ChaosStyle.Panel)
        {
            ImageColor = new UIColor(0.055f, 0.065f, 0.085f, 0.96f)
        };

        private static readonly Style m_TitleStyle = new Style(ChaosStyle.Title)
        {
            Font = Font.RobotoCondensedBold,
            FontSize = 16,
            FontColor = new UIColor(0.91f, 0.72f, 0.29f, 1f),
            Alignment = TextAnchor.MiddleCenter
        };

        private static readonly Style m_LabelStyle = new Style(ChaosStyle.Button)
        {
            FontSize = 12,
            FontColor = new UIColor(0.92f, 0.94f, 0.97f, 1f),
            Alignment = TextAnchor.MiddleLeft
        };

        private static readonly Style m_ValueStyle = new Style(ChaosStyle.Button)
        {
            FontSize = 12,
            FontColor = new UIColor(0.91f, 0.72f, 0.29f, 1f),
            Alignment = TextAnchor.MiddleCenter
        };

        private static readonly Style m_SmallStyle = new Style(ChaosStyle.Button)
        {
            FontSize = 10,
            FontColor = new UIColor(0.6f, 0.63f, 0.68f, 1f),
            Alignment = TextAnchor.MiddleLeft
        };

        private static readonly Style m_RowStyle = new Style(ChaosStyle.Panel)
        {
            ImageColor = new UIColor(0.13f, 0.15f, 0.18f, 0.85f)
        };

        private static readonly Style m_SelRowStyle = new Style(ChaosStyle.Panel)
        {
            ImageColor = new UIColor(0.91f, 0.72f, 0.29f, 0.3f)
        };

        private static readonly Style m_MasterOnStyle = new Style(ChaosStyle.Button)
        {
            ImageColor = new UIColor(0.35f, 0.75f, 0.45f, 0.9f)
        };
    }
}
