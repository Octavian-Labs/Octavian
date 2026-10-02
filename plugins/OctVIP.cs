// OctVIP вЂ” СЃРёСЃС‚РµРјР° VIP-СѓСЂРѕРІРЅРµР№ OctavianRSM.
// 7 СѓСЂРѕРІРЅРµР№: Base, VIP Bronze, VIP Silver, VIP Gold, VIP Brilliant, VIP Moderator, VIP Admin.
// РЈСЂРѕРІРµРЅСЊ = Р·Р°РїРёСЃСЊ РІ data/OctVIP/players.json (СЃ РѕРїС†РёРѕРЅР°Р»СЊРЅС‹Рј СЃСЂРѕРєРѕРј) РР›Р РїРµСЂРјРёС€РµРЅ octavip.<level>.
// Р­РєСЃРїРѕСЂС‚РёСЂСѓРµС‚ API РґР»СЏ РґСЂСѓРіРёС… РїР»Р°РіРёРЅРѕРІ: GetVipLevel / GetVipLevelName / GetVipColor /
// HasVipLevel / GetVipExpiry / SetVipLevel. Р’РµС‰Р°РµС‚ С…СѓРє OnVipLevelChanged(player, old, new).
// UI РЅР° Chaos UIFramework (С‚СЂРµР±СѓРµС‚СЃСЏ Oxide.Ext.Chaos).
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
    [Info("OctVIP", "Octavian", "1.1.0")]
    [Description("РЎРёСЃС‚РµРјР° VIP-СѓСЂРѕРІРЅРµР№: 7 СѓСЂРѕРІРЅРµР№, РІСЂРµРјРµРЅРЅР°СЏ РІС‹РґР°С‡Р°, Chaos-РїР°РЅРµР»СЊ, РїСЂРёРІРµС‚СЃС‚РІРёРµ СЃ РёРЅС„РѕР№, API РґР»СЏ РїР»Р°РіРёРЅРѕРІ")]
    public class OctVIP : ChaosPlugin
    {
        [Chaos.Permission] private const string PermManage = "octavip.manage";

        private const string UiPanel = "OCTAVIP_UI";

        private CommandCallbackHandler m_Callbacks;
        private Datafile<VipData> m_Data;

        [PluginReference] private Plugin Clans;

        // в”Ђв”Ђ РЈСЂРѕРІРЅРё в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

        // РїРѕСЂСЏРґРѕРє = РїСЂРёРѕСЂРёС‚РµС‚, РёРЅРґРµРєСЃ РјР°СЃСЃРёРІР° = СѓСЂРѕРІРµРЅСЊ
        private static readonly string[] LevelPerm = { "", "octavip.bronze", "octavip.silver", "octavip.gold", "octavip.brilliant", "octavip.moderator", "octavip.admin" };

        private class LevelDef
        {
            public string Name;
            public string Color;   // hex РґР»СЏ С‡Р°С‚Р°/UI
            public string Group;   // oxide-РіСЂСѓРїРїР° РґР»СЏ СЃРёРЅС…СЂРѕРЅРёР·Р°С†РёРё ("" вЂ” РЅРµ СЃРёРЅРєР°С‚СЊ)
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

        private static string LevelTag(int level) =>
            $"<color={Levels[level].Color}>в… {Levels[level].Name}</color>";

        // в”Ђв”Ђ РљРѕРЅС„РёРі в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

        private ConfigData Configuration => ConfigurationData as ConfigData;

        protected override ConfigurationFile OnLoadConfig(ref ConfigurationFile configurationFile) =>
            configurationFile = new ConfigurationFile<ConfigData>(Config);

        // РџСЂРё РіРѕСЂСЏС‡РµР№ Р·Р°РіСЂСѓР·РєРµ Carbon РЅРµ РёРЅРёС†РёР°Р»РёР·РёСЂСѓРµС‚ Config, РµСЃР»Рё С„Р°Р№Р»Р°
        // РєРѕРЅС„РёРіР° РµС‰С‘ РЅРµС‚ вЂ” Chaos Р·Р°С‚РµРј РїР°РґР°РµС‚ СЃ NRE РІ LoadConfig.
        // РЎРµС‚С‚РµСЂ internal, РїРѕСЌС‚РѕРјСѓ РїСЂРёСЃРІР°РёРІР°РµРј DynamicConfigFile СЂРµС„Р»РµРєСЃРёРµР№.
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
            [JsonProperty("РџСЂРµС„РёРєСЃ СЃРѕРѕР±С‰РµРЅРёР№ РІ С‡Р°С‚Рµ")]
            public string ChatPrefix = "<color=#E8B84B>в—† OctVIP</color>";

            [JsonProperty("РЎРёРЅС…СЂРѕРЅРёР·РёСЂРѕРІР°С‚СЊ СѓСЂРѕРІРЅРё СЃ oxide-РіСЂСѓРїРїР°РјРё (vip_bronze Рё С‚.Рґ.)")]
            public bool MirrorGroups = true;

            [JsonProperty("РРЅС‚РµСЂРІР°Р» РїСЂРѕРІРµСЂРєРё РёСЃС‚С‘РєС€РёС… VIP (СЃРµРє)")]
            public float ExpireCheckInterval = 3600f;

            [JsonProperty("РџСЂРёРІРµС‚СЃС‚РІРёРµ РїСЂРё РІС…РѕРґРµ: РјРёРЅРёРјР°Р»СЊРЅС‹Р№ СѓСЂРѕРІРµРЅСЊ")]
            public int WelcomeMinLevel = 1;

            [JsonProperty("РџСЂРёРІРµС‚СЃС‚РІРёРµ: РїРѕРєР°Р·С‹РІР°С‚СЊ РєР»Р°РЅ СЃ СѓСЂРѕРІРЅСЏ")]
            public int ShowClanMinLevel = 1;

            [JsonProperty("РџСЂРёРІРµС‚СЃС‚РІРёРµ: РїРѕРєР°Р·С‹РІР°С‚СЊ РѕРЅР»Р°Р№РЅ РєР»Р°РЅР° СЃ СѓСЂРѕРІРЅСЏ")]
            public int ShowClanOnlineMinLevel = 2;

            [JsonProperty("РџСЂРёРІРµС‚СЃС‚РІРёРµ: РїРѕРєР°Р·С‹РІР°С‚СЊ РєРѕР»-РІРѕ С€РєР°С„РѕРІ СЃ СѓСЂРѕРІРЅСЏ")]
            public int ShowBasesMinLevel = 3;

            [JsonProperty("РђРЅРѕРЅСЃРёСЂРѕРІР°С‚СЊ РІС…РѕРґ VIP РІ РѕР±С‰РёР№ С‡Р°С‚ СЃ СѓСЂРѕРІРЅСЏ")]
            public int BroadcastJoinMinLevel = 3;

            [JsonProperty("РђРЅРѕРЅСЃРёСЂРѕРІР°С‚СЊ РІС‹РґР°С‡Сѓ VIP РІ РѕР±С‰РёР№ С‡Р°С‚")]
            public bool BroadcastGrant = true;

            [JsonProperty("Р’С‹РґР°С‡Р° РІСЂРµРјРµРЅРЅС‹С… VIP РѕР±Р»Р°РґР°С‚РµР»СЏРј СѓСЂРѕРІРЅРµР№: РјР°РєСЃ. РІС‹РґР°РІР°РµРјС‹Р№ СѓСЂРѕРІРµРЅСЊ РґР»СЏ Moderator")]
            public int ModeratorGrantMaxLevel = 1;

            [JsonProperty("Р’С‹РґР°С‡Р° РІСЂРµРјРµРЅРЅС‹С… VIP: РјР°РєСЃ. СѓСЂРѕРІРµРЅСЊ РґР»СЏ Admin")]
            public int AdminGrantMaxLevel = 3;

            [JsonProperty("Р’С‹РґР°С‡Р° РІСЂРµРјРµРЅРЅС‹С… VIP: РјР°РєСЃ. СЃСЂРѕРє РІ РґРЅСЏС…")]
            public int TempGrantMaxDays = 7;

            [JsonProperty("Р§Р°С‚: Р·РІРµР·РґР° Рё С†РІРµС‚ РёРјРµРЅРё РїРѕ СѓСЂРѕРІРЅСЋ")]
            public bool FormatChat = true;

            [JsonProperty("Р§Р°С‚: РїРѕРєР°Р·С‹РІР°С‚СЊ РєР»Р°РЅ-С‚РµРі РїРµСЂРµРґ РёРјРµРЅРµРј (Clans)")]
            public bool ChatClanTag = true;

            [JsonProperty("Р§Р°С‚: Р·РІС‘Р·РґРѕС‡РєР° Сѓ РёРіСЂРѕРєРѕРІ Р±РµР· VIP (СѓСЂРѕРІРµРЅСЊ 0)")]
            public bool ChatStarForBase = true;
        }

        // в”Ђв”Ђ Р”Р°РЅРЅС‹Рµ в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

        protected class VipEntry
        {
            public int Level;                 // 1-6
            public string GrantedBy = "";     // РєС‚Рѕ РІС‹РґР°Р»
            public long GrantedAt;            // unix
            public long ExpiresAt;            // unix, 0 = РЅР°РІСЃРµРіРґР°
        }

        protected class VipData
        {
            public Dictionary<ulong, VipEntry> Players = new();
        }

        // в”Ђв”Ђ Р–РёР·РЅРµРЅРЅС‹Р№ С†РёРєР» в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

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
            // РѕС‚Р»РѕР¶РµРЅРЅР°СЏ СЃРІРµСЂРєР° вЂ” РїР»Р°РіРёРЅ РјРѕРі Р·Р°РіСЂСѓР·РёС‚СЊСЃСЏ РїРѕСЃР»Рµ РїРѕРґРєР»СЋС‡РµРЅРёСЏ РёРіСЂРѕРєРѕРІ
            timer.Once(3f, () =>
            {
                foreach (var p in BasePlayer.activePlayerList) WelcomePlayer(p);
                CheckExpirations();
            });
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList) ChaosUI.Destroy(p, UiPanel);
            m_Data.Save();
        }

        private void OnServerSave() => m_Data.Save();

        // в”Ђв”Ђ РЇРґСЂРѕ СѓСЂРѕРІРЅРµР№ в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

        private static long NowUnix => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private bool IsExpired(VipEntry e) => e.ExpiresAt > 0 && e.ExpiresAt <= NowUnix;

        /// <summary>РўРµРєСѓС‰РёР№ СѓСЂРѕРІРµРЅСЊ РёРіСЂРѕРєР°: РјР°РєСЃРёРјСѓРј РёР· Р·Р°РїРёСЃРё (Р¶РёРІРѕР№) Рё РїРµСЂРјРёС€РµРЅРѕРІ.</summary>
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

        /// <summary>Р’С‹РґР°С‚СЊ/РёР·РјРµРЅРёС‚СЊ СѓСЂРѕРІРµРЅСЊ. durationSec 0 вЂ” РЅР°РІСЃРµРіРґР°. Р’РѕР·РІСЂР°С‰Р°РµС‚ СЃС‚Р°СЂС‹Р№ СѓСЂРѕРІРµРЅСЊ.</summary>
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

        /// <summary>РЎРЅСЏС‚СЊ/РїРѕСЃС‚Р°РІРёС‚СЊ oxide-РіСЂСѓРїРїСѓ РїРѕ СѓСЂРѕРІРЅСЋ.</summary>
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

        /// <summary>РџРѕС‡Р°СЃРѕРІР°СЏ РїСЂРѕРІРµСЂРєР° РёСЃС‚С‘РєС€РёС… VIP.</summary>
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
                    Msg(player, $"РЎСЂРѕРє {LevelTag(e.Level)} РёСЃС‚С‘Рє вЂ” СЃРїР°СЃРёР±Рѕ, С‡С‚Рѕ Р±С‹Р»Рё СЃ РЅР°РјРё!");
                    Interface.Oxide.CallHook("OnVipExpired", player, e.Level);
                    if (e.Level != ResolveLevel(id))
                        Interface.Oxide.CallHook("OnVipLevelChanged", player, e.Level, ResolveLevel(id));
                }
            }
            if (expired.Count > 0) m_Data.Save();
        }

        // в”Ђв”Ђ РџР°СЂСЃРёРЅРі СЃСЂРѕРєР° в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

        /// <summary>"30d" / "12h" / "45m" / "perm"/"" вЂ” РІ СЃРµРєСѓРЅРґС‹. 0 = РЅР°РІСЃРµРіРґР°.</summary>
        private static bool TryParseDuration(string s, out long seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(s)) return true;
            s = s.Trim().ToLowerInvariant();
            if (s is "perm" or "permament" or "forever" or "РЅР°РІСЃРµРіРґР°" or "0") return true;

            long mult = 86400; // РїРѕ СѓРјРѕР»С‡Р°РЅРёСЋ вЂ” РґРЅРё
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
            if (expiresAt <= 0) return "РЅР°РІСЃРµРіРґР°";
            var left = expiresAt - NowUnix;
            if (left <= 0) return "РёСЃС‚С‘Рє";
            if (left >= 86400) return $"{left / 86400}Рґ {(left % 86400) / 3600}С‡";
            if (left >= 3600) return $"{left / 3600}С‡ {(left % 3600) / 60}Рј";
            return $"{left / 60}Рј";
        }

        // в”Ђв”Ђ Р§Р°С‚ в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

        private void Msg(BasePlayer player, string text) =>
            player.ChatMessage($"{Configuration.ChatPrefix} <color=#9AA4B2>вЂ”</color> {text}");

        private static string Val(object v) => $"<color=#FFD35C>{v}</color>";

        private void Broadcast(string text) =>
            PrintToChat($"{Configuration.ChatPrefix} <color=#9AA4B2>вЂ”</color> {text}");

        // в”Ђв”Ђ Р¤РѕСЂРјР°С‚РёСЂРѕРІР°РЅРёРµ РёРіСЂРѕРІРѕРіРѕ С‡Р°С‚Р° в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
        // Р—РІРµР·РґР° С†РІРµС‚Р° СѓСЂРѕРІРЅСЏ + РёРјСЏ С†РІРµС‚РѕРј СѓСЂРѕРІРЅСЏ + РєР»Р°РЅ-С‚РµРі. Р‘РµСЂС‘Рј РЅР° СЃРµР±СЏ
        // РІС‹РІРѕРґ СЃРѕРѕР±С‰РµРЅРёСЏ (chat.add) Рё РіР»СѓС€РёРј СЃС‚Р°РЅРґР°СЂС‚РЅС‹Р№ вЂ” РєР°Рє РґРµР»Р°РµС‚ Clans.

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

            var line = $"{tag}<color={color}>в…</color> <color={color}>{player.displayName}</color>" +
                       $"<color=#8A8F98>:</color> {message}";

            if (channel == ConVar.Chat.ChatChannel.Global)
            {
                foreach (var p in BasePlayer.activePlayerList)
                    if (p != null && p.IsConnected)
                        p.SendConsoleCommand("chat.add", (int)channel, player.UserIDString, line);
            }
            else
            {
                var team = player.Team;
                if (team == null) return null;
                foreach (var p in BasePlayer.activePlayerList)
                    if (p != null && p.IsConnected && p.Team != null && p.Team.teamID == team.teamID)
                        p.SendConsoleCommand("chat.add", (int)channel, player.UserIDString, line);
            }

            return true;
        }

        // в”Ђв”Ђ РџСЂРёРІРµС‚СЃС‚РІРёРµ РїСЂРё РІС…РѕРґРµ в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

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

            // Р°РЅРѕРЅСЃ РІ РѕР±С‰РёР№ С‡Р°С‚ РґР»СЏ РІС‹СЃРѕРєРёС… СѓСЂРѕРІРЅРµР№
            if (lvl >= Configuration.BroadcastJoinMinLevel)
                Broadcast($"{LevelTag(lvl)} {player.displayName} Р·Р°С€С‘Р» РЅР° СЃРµСЂРІРµСЂ!");

            var parts = new List<string>();
            if (m_Data.Data.Players.TryGetValue(player.userID, out var e) && e.ExpiresAt > 0)
                parts.Add($"РѕСЃС‚Р°Р»РѕСЃСЊ: {Val(FormatDuration(e.ExpiresAt))}");

            // РєР»Р°РЅ
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
                                parts.Add($"РєР»Р°РЅ: {Val(tag)} ({online}/{members.Count} РѕРЅР»Р°Р№РЅ)");
                            }
                            else parts.Add($"РєР»Р°РЅ: {Val(tag)}");
                        }
                        else parts.Add($"РєР»Р°РЅ: {Val(tag)}");
                    }
                }
                catch { }
            }

            // С€РєР°С„С‹, РІ РєРѕС‚РѕСЂС‹С… Р°РІС‚РѕСЂРёР·РѕРІР°РЅ
            if (lvl >= Configuration.ShowBasesMinLevel)
            {
                var tc = 0;
                foreach (var ent in BaseNetworkable.serverEntities)
                    if (ent is BuildingPrivlidge b && b.authorizedPlayers != null &&
                        b.authorizedPlayers.Any(x => x == player.userID)) tc++;
                if (tc > 0) parts.Add($"С‚РІРѕРё Р±Р°Р·С‹: {Val(tc)}");
            }

            var tail = parts.Count > 0 ? "\n<color=#9AA4B2>" + string.Join("  В·  ", parts) + "</color>" : "";
            Msg(player, $"РЎ РІРѕР·РІСЂР°С‰РµРЅРёРµРј! РўРІРѕР№ СЃС‚Р°С‚СѓСЃ: {LevelTag(lvl)}{tail}");
        }

        // в”Ђв”Ђ Р§Р°С‚-РєРѕРјР°РЅРґР° /vip в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

        private void CmdVip(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var myLevel = ResolveLevel(player.userID);
            var canManage = permission.UserHasPermission(player.UserIDString, PermManage) || player.IsAdmin;

            if (args.Length == 0)
            {
                // СЃРІРѕР№ СЃС‚Р°С‚СѓСЃ
                var line = $"РўРІРѕР№ СЃС‚Р°С‚СѓСЃ: {LevelTag(myLevel)}";
                if (m_Data.Data.Players.TryGetValue(player.userID, out var e) && !IsExpired(e))
                    line += $"\nР’С‹РґР°РЅ: {Val(DateTimeOffset.FromUnixTimeSeconds(e.GrantedAt).LocalDateTime.ToString("dd.MM.yyyy"))}" +
                            $" В· РґРѕ: {Val(e.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.ExpiresAt).LocalDateTime.ToString("dd.MM.yyyy HH:mm") : "РЅР°РІСЃРµРіРґР°")}" +
                            (string.IsNullOrEmpty(e.GrantedBy) ? "" : $" В· РѕС‚: {Val(e.GrantedBy)}");
                if (canManage || GrantCap(myLevel) > 0)
                    line += $"\n{Val("/vip give")} <РёРіСЂРѕРє> <СѓСЂРѕРІРµРЅСЊ> [СЃСЂРѕРє] вЂ” РІС‹РґР°С‚СЊ РІСЂРµРјРµРЅРЅС‹Р№ VIP\n{Val("/vip panel")} вЂ” РїР°РЅРµР»СЊ СѓРїСЂР°РІР»РµРЅРёСЏ";
                Msg(player, line);
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "panel":
                case "admin":
                case "ui":
                    if (!canManage) { Msg(player, $"РќРµС‚ РґРѕСЃС‚СѓРїР° ({Val(PermManage)})"); return; }
                    TogglePanel(player);
                    return;

                case "give":
                {
                    var cap = GrantCap(myLevel);
                    if (!canManage && cap <= 0)
                    {
                        Msg(player, "Р’С‹РґР°С‡Р° VIP РґРѕСЃС‚СѓРїРЅР° С‚РѕР»СЊРєРѕ СѓСЂРѕРІРЅСЏРј Moderator/Admin РёР»Рё РїРѕ octavip.manage");
                        return;
                    }
                    if (args.Length < 3)
                    {
                        Msg(player, $"РСЃРїРѕР»СЊР·РѕРІР°РЅРёРµ: {Val("/vip give")} <РёРіСЂРѕРє> <СѓСЂРѕРІРµРЅСЊ 1-6> [СЃСЂРѕРє: 7d/24h, РїСѓСЃС‚Рѕ = РЅР°РІСЃРµРіРґР°]");
                        return;
                    }

                    var target = FindPlayer(args[1]);
                    if (target == null) { Msg(player, $"РРіСЂРѕРє {Val(args[1])} РЅРµ РЅР°Р№РґРµРЅ."); return; }

                    if (!int.TryParse(args[2], out var lvl) || lvl < 1 || lvl > 6)
                    {
                        Msg(player, $"РЈСЂРѕРІРµРЅСЊ РґРѕР»Р¶РµРЅ Р±С‹С‚СЊ {Val("1-6")}: 1=Bronze, 2=Silver, 3=Gold, 4=Brilliant, 5=Moderator, 6=Admin");
                        return;
                    }

                    long dur = 0;
                    if (args.Length > 3 && !TryParseDuration(args[3], out dur))
                    {
                        Msg(player, $"РЎСЂРѕРє РЅРµ СЂР°СЃРїРѕР·РЅР°РЅ: {Val(args[3])} вЂ” РїСЂРёРјРµСЂС‹: 7d, 24h, 90m, perm");
                        return;
                    }

                    // РѕРіСЂР°РЅРёС‡РµРЅРёСЏ РґР»СЏ РЅРµ-Р°РґРјРёРЅРѕРІ
                    if (!canManage)
                    {
                        if (lvl > cap)
                        {
                            Msg(player, $"РўРІРѕР№ РјР°РєСЃРёРјСѓРј РґР»СЏ РІС‹РґР°С‡Рё вЂ” {LevelTag(cap)}");
                            return;
                        }
                        var maxSec = (long)Configuration.TempGrantMaxDays * 86400;
                        if (dur <= 0 || dur > maxSec)
                        {
                            Msg(player, $"РўС‹ РјРѕР¶РµС€СЊ РІС‹РґР°РІР°С‚СЊ С‚РѕР»СЊРєРѕ РІСЂРµРјРµРЅРЅС‹Р№ VIP РґРѕ {Val(Configuration.TempGrantMaxDays)} РґРЅРµР№");
                            return;
                        }
                    }

                    Grant(player, target.userID, lvl, dur, target.displayName.ToString());
                    return;
                }

                default:
                    Msg(player, $"{Val("/vip")} вЂ” СЃС‚Р°С‚СѓСЃ В· {Val("/vip give")} вЂ” РІС‹РґР°С‚СЊ В· {Val("/vip panel")} вЂ” СѓРїСЂР°РІР»РµРЅРёРµ");
                    return;
            }
        }

        /// <summary>РњР°РєСЃ. СѓСЂРѕРІРµРЅСЊ, РєРѕС‚РѕСЂС‹Р№ РјРѕР¶РµС‚ РІС‹РґР°РІР°С‚СЊ РѕР±Р»Р°РґР°С‚РµР»СЊ СѓСЂРѕРІРЅСЏ lvl.</summary>
        private int GrantCap(int lvl) => lvl switch
        {
            5 => Configuration.ModeratorGrantMaxLevel,
            6 => Configuration.AdminGrantMaxLevel,
            _ => 0
        };

        private void Grant(BasePlayer granter, ulong targetId, int level, long dur, string targetName)
        {
            var old = SetLevel(targetId, level, dur, granter?.displayName.ToString() ?? "console");
            var durText = dur > 0 ? $" РЅР° {Val(FormatDuration(NowUnix + dur))}" : "";

            if (Configuration.BroadcastGrant && granter != null)
                Broadcast($"{LevelTag(level)} РїРѕР»СѓС‡Р°РµС‚ {targetName}{durText} вЂ” РІС‹РґР°Р» {granter.displayName}");

            Msg(granter, $"{targetName}: {LevelTag(old)} в†’ {LevelTag(level)}{durText}");
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

        // в”Ђв”Ђ РљРѕРЅСЃРѕР»СЊРЅС‹Рµ РєРѕРјР°РЅРґС‹ в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

        [ConsoleCommand("octavip.set")]
        private void CmdSet(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 1) return;
            if (arg.Args == null || arg.Args.Length < 2)
            {
                Puts("octavip.set <steamid|РёРјСЏ> <СѓСЂРѕРІРµРЅСЊ 0-6> [СЃСЂРѕРє: 30d/24h/perm]");
                return;
            }
            var targetName = arg.Args[0].ToString();
            var target = FindPlayer(targetName);
            ulong id;
            string name;
            if (target != null) { id = target.userID; name = target.displayName.ToString(); }
            else if (ulong.TryParse(targetName, out id)) name = targetName;
            else { Puts($"РРіСЂРѕРє '{targetName}' РЅРµ РЅР°Р№РґРµРЅ (РґР»СЏ РѕС„Р»Р°Р№РЅ СѓРєР°Р¶Рё steamid)"); return; }

            if (!int.TryParse(arg.Args[1].ToString(), out var lvl) || lvl < 0 || lvl > 6) { Puts("РЈСЂРѕРІРµРЅСЊ 0-6"); return; }

            long dur = 0;
            if (arg.Args.Length > 2 && !TryParseDuration(arg.Args[2].ToString(), out dur)) { Puts("РЎСЂРѕРє РЅРµ СЂР°СЃРїРѕР·РЅР°РЅ"); return; }

            var old = SetLevel(id, lvl, dur, "console");
            Puts($"{name} ({id}): {Levels[old].Name} -> {Levels[ResolveLevel(id)].Name}" + (dur > 0 ? $" РЅР° {FormatDuration(NowUnix + dur)}" : ""));
        }

        [ConsoleCommand("octavip.remove")]
        private void CmdRemove(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 1) return;
            if (arg.Args == null || arg.Args.Length < 1) { Puts("octavip.remove <steamid|РёРјСЏ>"); return; }
            var target = FindPlayer(arg.Args[0].ToString());
            ulong id = target?.userID ?? (ulong.TryParse(arg.Args[0].ToString(), out var x) ? x : 0);
            if (id == 0) { Puts("РРіСЂРѕРє РЅРµ РЅР°Р№РґРµРЅ"); return; }
            var old = SetLevel(id, 0, 0, "console");
            Puts($"РЎРЅСЏС‚ VIP Сѓ {id} (Р±С‹Р» {Levels[old].Name})");
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
                     $" [Р·Р°РїРёСЃСЊ: {e.Level}, РІС‹РґР°Р» {e.GrantedBy}, РґРѕ {(e.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.ExpiresAt).LocalDateTime.ToString("dd.MM HH:mm") : "perm")}]" +
                     (IsExpired(e) ? " РРЎРўРЃРљ" : ""));
            }
        }

        // в”Ђв”Ђ API РґР»СЏ РґСЂСѓРіРёС… РїР»Р°РіРёРЅРѕРІ в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

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

        // в”Ђв”Ђ Chaos-РїР°РЅРµР»СЊ СѓРїСЂР°РІР»РµРЅРёСЏ в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

        private class PanelState
        {
            public string Search = "";
            public ulong Selected;
            public string Duration = "30d";
            public int Page;
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

        // РєР°РЅРґРёРґР°С‚С‹: РѕРЅР»Р°Р№РЅ + СЃРїСЏС‰РёРµ + РІСЃРµ СЃ VIP-Р·Р°РїРёСЃСЊСЋ
        private List<BasePlayer> PanelPlayers(PanelState st)
        {
            var list = BasePlayer.activePlayerList
                .Concat(BasePlayer.sleepingPlayerList)
                .Where(p => string.IsNullOrEmpty(st.Search)
                         || p.displayName.ToString().IndexOf(st.Search, StringComparison.OrdinalIgnoreCase) >= 0
                         || p.UserIDString.Contains(st.Search))
                .ToList();

            // РѕС„Р»Р°Р№РЅ-РёРіСЂРѕРєРё СЃ VIP-Р·Р°РїРёСЃСЏРјРё вЂ” РїРѕРєР°Р·С‹РІР°РµРј РїСЃРµРІРґРѕ-СЃС‚СЂРѕРєР°РјРё С‡РµСЂРµР· id
            var extra = m_Data.Data.Players.Keys
                .Where(id => list.All(p => p.userID != id)
                          && (string.IsNullOrEmpty(st.Search) || id.ToString().Contains(st.Search)))
                .ToList();
            // РІРµСЂРЅС‘Рј СЃРїРёСЃРѕРє (online) + РѕС„Р»Р°Р№РЅ-Р·Р°РїРёСЃРё РѕС‚РґРµР»СЊРЅРѕ РѕР±СЂР°Р±РѕС‚Р°РµРј РїСЂРё СЂРµРЅРґРµСЂРµ
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
                ChaosPrefab.Title(body, UIAnchor.TopStretch, new Offset(8, -34, -44, -8), "в—† OCTAVIAN VIP", m_TitleStyle);
                ChaosPrefab.CloseButton(body, UIAnchor.TopRight, new Offset(-36, -34, -8, -6), ChaosStyle.BlackOutline)
                    .WithCallback(m_Callbacks, arg => TogglePanel(player), $"{player.userID}.close");

                // РїРѕРёСЃРє
                ChaosPrefab.Input(body, UIAnchor.TopStretch, new Offset(8, -68, -8, -44), st.Search)
                    .WithCallback(m_Callbacks, arg =>
                    {
                        st.Search = arg.Args.Length > 1 ? string.Join(" ", arg.Args.Skip(1)) : "";
                        st.Page = 0;
                        BuildPanel(player);
                    }, $"{player.userID}.search");

                // в”Ђв”Ђ СЃРїРёСЃРѕРє РёРіСЂРѕРєРѕРІ (Р»РµРІР°СЏ РєРѕР»РѕРЅРєР°) в”Ђв”Ђ
                var players = PanelPlayers(st);
                var totalRows = players.Count + _offlineIds.Count;
                var maxPage = Math.Max(0, (totalRows - 1) / Rows);
                st.Page = Math.Clamp(st.Page, 0, maxPage);

                var y = 76;
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
                        line = $"{id} <color=#8A8F98>(РѕС„С„Р»Р°Р№РЅ)</color>";
                        _ = e;
                    }
                    if (rowIdx++ < skip) continue;
                    shown++;

                    var yy = y;
                    var sel = st.Selected == id;
                    ChaosPrefab.TextButton(body, UIAnchor.TopStretch, new Offset(8, -(yy + 26), -292, -yy),
                            "", sel ? m_SelRowStyle : m_RowStyle, null)
                        .WithCallback(m_Callbacks, arg => { st.Selected = id; BuildPanel(player); }, $"{player.userID}.sel.{id}");

                    TextContainer.Create(body, $"{line}  {LevelTag(lvl)}", m_LabelStyle,
                        UIAnchor.TopStretch, new Offset(16, -(yy + 23), -290, -yy - 3));
                    y += RowH;
                }

                if (totalRows == 0)
                    TextContainer.Create(body, "<color=#8A8F98>вЂ” РЅРёРєРѕРіРѕ РЅРµ РЅР°Р№РґРµРЅРѕ вЂ”</color>", m_LabelStyle,
                        UIAnchor.TopStretch, new Offset(16, -110, -16, -84));

                // РїРµР№РґР¶РёРЅРі
                if (maxPage > 0)
                {
                    ChaosPrefab.TextButton(body, UIAnchor.BottomLeft, new Offset(8, 8, 60, 30), "в—Ђ", m_LabelStyle, ChaosStyle.BlackOutline)
                        .WithCallback(m_Callbacks, arg => { st.Page--; BuildPanel(player); }, $"{player.userID}.prev");
                    TextContainer.Create(body, $"{st.Page + 1}/{maxPage + 1}", m_ValueStyle,
                        UIAnchor.BottomLeft, new Offset(60, 10, 120, 30));
                    ChaosPrefab.TextButton(body, UIAnchor.BottomLeft, new Offset(124, 8, 176, 30), "в–¶", m_LabelStyle, ChaosStyle.BlackOutline)
                        .WithCallback(m_Callbacks, arg => { st.Page++; BuildPanel(player); }, $"{player.userID}.next");
                }

                // в”Ђв”Ђ РїСЂР°РІР°СЏ РєРѕР»РѕРЅРєР°: СѓРїСЂР°РІР»РµРЅРёРµ РІС‹Р±СЂР°РЅРЅС‹Рј в”Ђв”Ђ
                var rx = 288; // Р»РµРІС‹Р№ РєСЂР°Р№ РїСЂР°РІРѕР№ РєРѕР»РѕРЅРєРё
                if (st.Selected != 0)
                {
                    var selP = BasePlayer.FindByID(st.Selected) ?? BasePlayer.FindSleeping(st.Selected);
                    var selName = selP?.displayName.ToString() ?? st.Selected.ToString();
                    var selLvl = ResolveLevel(st.Selected);

                    TextContainer.Create(body, $"РРіСЂРѕРє: {selName}", m_LabelStyle,
                        UIAnchor.TopLeft, new Offset(rx, -88, 536, -64));
                    TextContainer.Create(body, $"РЎС‚Р°С‚СѓСЃ: {LevelTag(selLvl)}", m_LabelStyle,
                        UIAnchor.TopLeft, new Offset(rx, -112, 536, -88));

                    if (m_Data.Data.Players.TryGetValue(st.Selected, out var e) && !IsExpired(e))
                        TextContainer.Create(body,
                            $"Р’С‹РґР°РЅ: {DateTimeOffset.FromUnixTimeSeconds(e.GrantedAt).LocalDateTime:dd.MM.yyyy} В· " +
                            $"РґРѕ: {(e.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.ExpiresAt).LocalDateTime.ToString("dd.MM.yyyy") : "perm")} В· " +
                            $"РѕС‚: {e.GrantedBy}",
                            m_SmallStyle, UIAnchor.TopLeft, new Offset(rx, -132, 536, -112));

                    // РєРЅРѕРїРєРё СѓСЂРѕРІРЅРµР№ вЂ” СЃРµС‚РєР° 2 СЂСЏРґР°
                    for (var i = 0; i <= 6; i++)
                    {
                        var lvl = i;
                        var col = i % 4;
                        var rowI = i / 4;
                        var bx = rx + col * 62;
                        var by = 150 + rowI * 34;
                        var label = lvl == 0 ? "РЎРЅСЏС‚СЊ" : Levels[lvl].Name.Replace("VIP ", "");
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
                            label = $"вњ“ {label}";
                        }
                        var outline = isCurrent ? ChaosStyle.GreenOutline : ChaosStyle.BlackOutline;
                        ChaosPrefab.TextButton(body, UIAnchor.TopLeft, new Offset(bx, -(by + 28), bx + 58, -by),
                                label, style, outline)
                            .WithCallback(m_Callbacks, arg =>
                            {
                                var old = SetLevel(st.Selected, lvl, 0, player.displayName.ToString());
                                var p2 = BasePlayer.FindByID(st.Selected) ?? BasePlayer.FindSleeping(st.Selected);
                                if (Configuration.BroadcastGrant && lvl > old)
                                    Broadcast($"{LevelTag(lvl)} РїРѕР»СѓС‡Р°РµС‚ {(p2?.displayName.ToString() ?? st.Selected.ToString())} вЂ” РІС‹РґР°Р» {player.displayName}");
                                BuildPanel(player);
                            }, $"{player.userID}.lvl.{i}");
                    }

                    // СЃСЂРѕРє + РєРЅРѕРїРєРё
                    TextContainer.Create(body, "РЎСЂРѕРє:", m_LabelStyle, UIAnchor.TopLeft, new Offset(rx, -242, rx + 44, -220));
                    ChaosPrefab.Input(body, UIAnchor.TopLeft, new Offset(rx + 44, -240, rx + 120, -218), st.Duration)
                        .WithCallback(m_Callbacks, arg =>
                        {
                            st.Duration = arg.Args.Length > 1 ? arg.Args[1].ToString() : "";
                        }, $"{player.userID}.dur");

                    var di = 0;
                    foreach (var d in new[] { "7d", "30d", "perm" })
                    {
                        var dd = d;
                        ChaosPrefab.TextButton(body, UIAnchor.TopLeft, new Offset(rx + 126 + di * 44, -240, rx + 164 + di * 44, -218),
                                dd, m_LabelStyle, ChaosStyle.BlackOutline)
                            .WithCallback(m_Callbacks, arg =>
                            {
                                st.Duration = dd == "perm" ? "" : dd;
                                BuildPanel(player);
                            }, $"{player.userID}.dur.{dd}");
                        di++;
                    }

                    // РїСЂРёРјРµРЅРёС‚СЊ СЃСЂРѕРє
                    ChaosPrefab.TextButton(body, UIAnchor.TopLeft, new Offset(rx, -284, rx + 200, -252),
                            "Р’Р«Р”РђРўР¬ РЎР РћРљ + РЈР РћР’Р•РќР¬", m_MasterOnStyle, ChaosStyle.BlackOutline)
                        .WithCallback(m_Callbacks, arg =>
                        {
                            if (!TryParseDuration(st.Duration, out var dur))
                            {
                                Msg(player, $"РЎСЂРѕРє {Val(st.Duration)} РЅРµ СЂР°СЃРїРѕР·РЅР°РЅ (7d, 24h, perm)");
                                return;
                            }
                            var lvl2 = ResolveLevel(st.Selected);
                            if (lvl2 <= 0) { Msg(player, "РЎРЅР°С‡Р°Р»Р° РІС‹Р±РµСЂРё СѓСЂРѕРІРµРЅСЊ РєРЅРѕРїРєР°РјРё РІС‹С€Рµ"); return; }
                            SetLevel(st.Selected, lvl2, dur, player.displayName.ToString());
                            BuildPanel(player);
                        }, $"{player.userID}.apply");
                }
                else
                {
                    TextContainer.Create(body, "<color=#8A8F98>Р’С‹Р±РµСЂРё РёРіСЂРѕРєР° СЃР»РµРІР°</color>", m_LabelStyle,
                        UIAnchor.TopLeft, new Offset(rx, -140, 540, -100));
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

        // в”Ђв”Ђ РЎС‚РёР»Рё в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

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
