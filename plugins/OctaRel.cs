using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    // OctaRel — списки «свои/враги» игроков.
    // Механика:
    //   • при входе друга — уведомление в чат «друг X в сети»;
    //   • при входе врага — предупреждение «враг X в сети»;
    //   • при выходе — симметричные сообщения;
    //   • если враг ближе «Радиус предупреждения» метров — алерт в чат
    //     (не чаще раза в «Кулдаун алерта» на пару).
    // Хранилище — по steamID (переименование не ломает списки).
    // API (HookMethod):
    //   GetRelations(uid) -> JObject {friends:[{id,name}], enemies:[...]}
    //   AddRelation(uid, "friend"|"enemy", targetUid) -> null | "ошибка"
    //   RemoveRelation(uid, "friend"|"enemy", targetUid) -> bool
    //   GetNotify(uid) / SetNotify(uid, bool)
    // Чат-команды: /friend add|del|list ник, /enemy add|del|list ник, /relnotify
    [Info("OctaRel", "Octavian", "1.0.0")]
    [Description("Списки друзей и врагов: уведомления о входе/выходе и радар врага")]
    public class OctaRel : RustPlugin
    {
        private ConfigData Cfg;

        private class ConfigData
        {
            [JsonProperty("Лимит друзей")]
            public int MaxFriends = 10;
            [JsonProperty("Лимит врагов")]
            public int MaxEnemies = 5;
            [JsonProperty("Радиус предупреждения о враге, метров")]
            public float ProximityRadius = 100f;
            [JsonProperty("Проверка радиуса каждые N секунд")]
            public int ProximityCheckSec = 30;
            [JsonProperty("Кулдаун алерта по паре, секунд")]
            public int ProximityCooldownSec = 300;
            [JsonProperty("Уведомлять о входе/выходе")]
            public bool NotifyJoinLeave = true;
            [JsonProperty("Пинг врага: цена в RP (ServerRewards, 0 = выкл)")]
            public int PingCostRp = 50;
            [JsonProperty("Пинг врага: цена в монетах (Economics, 0 = выкл)")]
            public int PingCostCoins = 5000;
            [JsonProperty("Пинг: длительность метки, секунд")]
            public int PingSeconds = 10;
            [JsonProperty("Пинг: обновлений метки в секунду")]
            public int PingHz = 10;
            [JsonProperty("Пинг: сообщить цели после завершения")]
            public bool PingNotifyTarget = false;
        }

        protected override void LoadDefaultConfig() => Config.WriteObject(new ConfigData(), true);

        private class RelData
        {
            public List<ulong> Friends = new List<ulong>();
            public List<ulong> Enemies = new List<ulong>();
            public bool Notify = true;
        }

        private Dictionary<ulong, RelData> _data = new Dictionary<ulong, RelData>();
        private readonly Dictionary<string, long> _proxWarn = new Dictionary<string, long>();
        // кулдаун «внесён во враги» на пару — защита от спама перевносами
        private readonly Dictionary<string, long> _enemyNotice = new Dictionary<string, long>();
        private const long EnemyNoticeCooldownSec = 600;
        private Timer _radar;

        // платный ESP-пинг: payer uid -> сессия
        private class PingSession { public ulong Target; public float Until; }
        private readonly Dictionary<ulong, PingSession> _pings = new Dictionary<ulong, PingSession>();
        private Timer _pingTimer;
        private const string PingUi = "orel_ping";

        private RelData Rel(ulong uid)
        {
            RelData d;
            if (!_data.TryGetValue(uid, out d))
                d = _data[uid] = new RelData();
            return d;
        }

        private void Init()
        {
            Cfg = Config.ReadObject<ConfigData>() ?? new ConfigData();
            Config.WriteObject(Cfg, true);
            if (Interface.Oxide.DataFileSystem.ExistsDatafile($"{Name}/Relations"))
                _data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, RelData>>($"{Name}/Relations")
                        ?? new Dictionary<ulong, RelData>();
            _radar = timer.Every(Math.Max(10, Cfg.ProximityCheckSec), RadarTick);
            _pingTimer = timer.Every(1f / Mathf.Max(2, Cfg.PingHz), PingTick);
        }

        private void Unload()
        {
            _radar?.Destroy();
            _pingTimer?.Destroy();
            foreach (var kv in _pings)
            {
                var p = BasePlayer.FindByID(kv.Key);
                if (p != null) CuiHelper.DestroyUi(p, PingUi + kv.Key);
            }
            _pings.Clear();
            SaveData();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject($"{Name}/Relations", _data);

        // ── Уведомления о входе/выходе ────────────────────────────────────────

        private void OnPlayerConnected(BasePlayer player)
        {
            if (Cfg == null || !Cfg.NotifyJoinLeave || player == null) return;
            NotifyPresence(player, true);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (Cfg == null || !Cfg.NotifyJoinLeave || player == null) return;
            // откладываем — игрок ещё в списках, но уже уходит; состояние списков стабильно
            NotifyPresence(player, false);
        }

        private void NotifyPresence(BasePlayer subject, bool joined)
        {
            ulong sid = subject.userID;
            string sname = Clean(subject.displayName);
            string verb = joined ? "зашёл на сервер" : "вышел из сети";

            // 1) кто следит за subject — сообщаем им
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p.userID == sid) continue;
                RelData d;
                if (!_data.TryGetValue(p.userID, out d) || !d.Notify) continue;
                if (d.Friends.Contains(sid))
                    p.ChatMessage($"<color=#8dd06f>◆ Друг</color> <color=#E8B84B>{sname}</color> {verb}.");
                else if (d.Enemies.Contains(sid))
                    p.ChatMessage($"<color=#cd412b>◆ Враг</color> <color=#E8B84B>{sname}</color> {verb}.");
            }

            // 2) самому вошедшему — кто из его списков уже онлайн
            if (joined)
            {
                var mine = Rel(sid);
                if (!mine.Notify) return;
                int fo = mine.Friends.Count(id => BasePlayer.FindByID(id) != null);
                int eo = mine.Enemies.Count(id => BasePlayer.FindByID(id) != null);
                if (fo > 0) subject.ChatMessage($"<color=#8dd06f>◆</color> Друзей в сети: <color=#E8B84B>{fo}</color>");
                if (eo > 0) subject.ChatMessage($"<color=#cd412b>◆ Внимание:</color> врагов в сети: <color=#E8B84B>{eo}</color>");
            }
        }

        // ── Радар врагов ─────────────────────────────────────────────────────

        private void RadarTick()
        {
            if (Cfg == null || _data.Count == 0) return;
            float r2 = Cfg.ProximityRadius * Cfg.ProximityRadius;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var p in BasePlayer.activePlayerList)
            {
                RelData d;
                if (!_data.TryGetValue(p.userID, out d) || !d.Notify || d.Enemies.Count == 0) continue;
                foreach (var eid in d.Enemies)
                {
                    var enemy = BasePlayer.FindByID(eid);
                    if (enemy == null || enemy.IsSleeping()) continue;
                    if ((enemy.transform.position - p.transform.position).sqrMagnitude > r2) continue;

                    string pair = $"{p.userID}:{eid}";
                    long last;
                    if (_proxWarn.TryGetValue(pair, out last) && now - last < Cfg.ProximityCooldownSec) continue;
                    _proxWarn[pair] = now;
                    p.ChatMessage($"<color=#cd412b>◆ РАДАР:</color> враг <color=#E8B84B>{Clean(enemy.displayName)}</color> в радиусе {(int)Cfg.ProximityRadius} м!");
                }
            }
        }

        // ── Платный ESP-пинг врага ───────────────────────────────────────────

        // инфо для витрин (OctaHub): цены, длительность, какие валюты реально доступны
        [HookMethod("GetPingInfo")]
        public JObject API_PingInfo()
        {
            return new JObject
            {
                ["rp"] = Cfg.PingCostRp, ["coins"] = Cfg.PingCostCoins, ["sec"] = Cfg.PingSeconds,
                ["rpOn"] = Cfg.PingCostRp > 0 && HasPlugin("ServerRewards"),
                ["coinsOn"] = Cfg.PingCostCoins > 0 && HasPlugin("Economics"),
            };
        }

        // currency: "rp" | "coins" | "auto". Возвращает null при успехе или текст ошибки.
        [HookMethod("ScanEnemy")]
        public string API_ScanEnemy(BasePlayer payer, ulong target, string currency)
        {
            if (Cfg == null || payer == null) return "сервис недоступен";
            RelData d;
            if (!_data.TryGetValue(payer.userID, out d) || !d.Enemies.Contains(target))
                return "Это не твой враг — сначала /enemy add ник";
            var t = BasePlayer.FindByID(target);
            if (t == null || t.IsSleeping()) return "Цель не в сети.";
            if (t.IsDead()) return "Цель уже мертва.";
            if (_pings.ContainsKey(payer.userID)) return "Пинг уже активен — дождись конца.";

            string err = null;
            if ((currency == "rp" || currency == "auto") && Cfg.PingCostRp > 0 &&
                TakeRp(payer, Cfg.PingCostRp, out err))
                return StartPing(payer, t, $"{Cfg.PingCostRp} RP");
            if (currency == "rp") return err ?? "RP-оплата выключена в конфиге.";
            if ((currency == "coins" || currency == "auto") && Cfg.PingCostCoins > 0 &&
                TakeCoins(payer, Cfg.PingCostCoins, out err))
                return StartPing(payer, t, $"{Cfg.PingCostCoins} монет");
            if (currency == "coins") return err ?? "Оплата монетами выключена в конфиге.";
            return err ?? "Нет способа оплаты — ServerRewards/Economics не найдены.";
        }

        private string StartPing(BasePlayer payer, BasePlayer target, string paid)
        {
            _pings[payer.userID] = new PingSession
            {
                Target = target.userID,
                Until = Time.realtimeSinceStartup + Mathf.Max(3, Cfg.PingSeconds)
            };
            payer.ChatMessage($"<color=#83b8c7>◆ ПИНГ:</color> <color=#E8B84B>{Clean(target.displayName)}</color> " +
                              $"помечен на {Cfg.PingSeconds} сек (−{paid}).");
            return null;
        }

        private void PingTick()
        {
            if (_pings.Count == 0) return;
            List<ulong> done = null;
            foreach (var kv in _pings)
            {
                var payer = BasePlayer.FindByID(kv.Key);
                var target = BasePlayer.FindByID(kv.Value.Target);
                if (Time.realtimeSinceStartup >= kv.Value.Until ||
                    payer == null || target == null || target.IsDead())
                {
                    if (payer != null) CuiHelper.DestroyUi(payer, PingUi + kv.Key);
                    if (done == null) done = new List<ulong>();
                    done.Add(kv.Key);
                    continue;
                }
                DrawPing(payer, target);
            }
            if (done == null) return;
            foreach (var id in done)
            {
                var s = _pings[id];
                _pings.Remove(id);
                // честное «вас сканировали» — только по завершении и только если включено
                if (!Cfg.PingNotifyTarget) continue;
                var t = BasePlayer.FindByID(s.Target);
                var p = BasePlayer.FindByID(id);
                if (t != null && p != null)
                    t.ChatMessage($"<color=#cd412b>◆ Внимание:</color> <color=#E8B84B>{Clean(p.displayName)}</color> " +
                                  $"отслеживал тебя {Cfg.PingSeconds} сек.");
            }
        }

        // проекция позиции врага в экранные координаты (серверный ESP:
        // угол по каждой оси относительно взгляда / половина FOV, аспект 16:9)
        private void DrawPing(BasePlayer payer, BasePlayer target)
        {
            var eye = payer.eyes.position;
            var dir = Quaternion.Inverse(payer.eyes.rotation) *
                      (target.transform.position + Vector3.up * 1.9f - eye);
            CuiHelper.DestroyUi(payer, PingUi + payer.userID);
            if (dir.z < 0.5f) return; // цель за спиной — метку не рисуем

            const float hf = 37f * Mathf.Deg2Rad;
            const float vf = hf * 9f / 16f;
            float nx = Mathf.Clamp(Mathf.Atan2(dir.x, dir.z) / hf, -0.95f, 0.95f);
            float ny = Mathf.Clamp(Mathf.Atan2(dir.y, dir.z) / vf, -0.9f, 0.9f);
            float ax = 0.5f + nx * 0.5f, ay = 0.5f + ny * 0.5f;
            int dist = (int)(target.transform.position - eye).magnitude;

            var c = new CuiElementContainer();
            c.Add(new CuiElement
            {
                Name = PingUi + payer.userID,
                Parent = "Overlay",
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = $"<color=#ff5a4d>◆ {Clean(target.displayName)}\n{dist} м</color>",
                        FontSize = 12, Align = TextAnchor.MiddleCenter
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = FormattableString.Invariant($"{ax - 0.08f:0.###} {ay:0.###}"),
                        AnchorMax = FormattableString.Invariant($"{ax + 0.08f:0.###} {ay + 0.05f:0.###}")
                    }
                }
            });
            CuiHelper.AddUi(payer, c);
        }

        // ── Оплата (живой поиск плагинов — PluginReference залипает) ──────────

        private static bool HasPlugin(string name)
        {
            foreach (var pl in Interface.Oxide.RootPluginManager.GetPlugins())
                if (pl.Name == name && pl.IsLoaded) return true;
            return false;
        }

        private bool TakeRp(BasePlayer p, int amount, out string err)
        {
            err = null;
            foreach (var pl in Interface.Oxide.RootPluginManager.GetPlugins())
            {
                if (pl.Name != "ServerRewards" || !pl.IsLoaded) continue;
                var bal = pl.Call("CheckPoints", p.userID);
                int have = bal != null ? Convert.ToInt32(bal) : 0;
                if (have < amount) { err = $"не хватает RP: {have}/{amount}"; return false; }
                pl.Call("TakePoints", p.userID, amount);
                return true;
            }
            err = "ServerRewards не установлен";
            return false;
        }

        private bool TakeCoins(BasePlayer p, int amount, out string err)
        {
            err = null;
            foreach (var pl in Interface.Oxide.RootPluginManager.GetPlugins())
            {
                if (pl.Name != "Economics" || !pl.IsLoaded) continue;
                var bal = pl.Call("Balance", p.UserIDString);
                double have = bal != null ? Convert.ToDouble(bal) : 0;
                if (have < amount) { err = $"не хватает монет: {(int)have}/{amount}"; return false; }
                pl.Call("Withdraw", p.UserIDString, (double)amount);
                return true;
            }
            err = "Economics не установлен";
            return false;
        }

        // ── API ──────────────────────────────────────────────────────────────

        [HookMethod("GetRelations")]
        public JObject API_GetRelations(ulong uid)
        {
            RelData d;
            _data.TryGetValue(uid, out d);
            return new JObject
            {
                ["friends"] = new JArray(Names(d?.Friends)),
                ["enemies"] = new JArray(Names(d?.Enemies)),
                ["notify"] = d?.Notify ?? true,
            };
        }

        private IEnumerable<JObject> Names(List<ulong> ids)
        {
            if (ids == null) yield break;
            foreach (var id in ids)
                yield return new JObject { ["id"] = id, ["name"] = NameOf(id) };
        }

        private string NameOf(ulong uid)
        {
            var online = BasePlayer.FindByID(uid);
            if (online != null) return Clean(online.displayName);
            var pl = covalence.Players.FindPlayerById(uid.ToString());
            return pl != null ? Clean(pl.Name) : uid.ToString();
        }

        // target — steamID. Возвращает null или текст ошибки.
        [HookMethod("AddRelation")]
        public string API_Add(ulong uid, string type, ulong target)
        {
            if (target == uid) return "Нельзя добавить себя.";
            var d = Rel(uid);
            var list = type == "enemy" ? d.Enemies : d.Friends;
            var limit = type == "enemy" ? Cfg.MaxEnemies : Cfg.MaxFriends;
            if (list.Contains(target)) return "Уже в списке.";
            if (list.Count >= limit) return $"Лимит ({limit}) исчерпан.";
            // враг не может быть другом и наоборот — чистим пересечение
            var other = type == "enemy" ? d.Friends : d.Enemies;
            other.Remove(target);
            list.Add(target);
            SaveData();
            if (type == "enemy") NotifyDeclaredEnemy(uid, target);
            return null;
        }

        // цель узнаёт, что её объявили врагом — с предложением ответить тем же.
        // Уважает /relnotify цели и кулдаун на пару (защита от спама перевносами).
        private void NotifyDeclaredEnemy(ulong author, ulong target)
        {
            var tp = BasePlayer.FindByID(target);
            if (tp == null || tp.IsSleeping()) return;
            RelData td;
            if (_data.TryGetValue(target, out td) && !td.Notify) return;

            string pk = author + ":" + target;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), last;
            if (_enemyNotice.TryGetValue(pk, out last) && now - last < EnemyNoticeCooldownSec) return;
            _enemyNotice[pk] = now;

            string an = NameOf(author);
            tp.ChatMessage($"<color=#cd412b>◆ ОБЪЯВЛЕН ВРАГОМ:</color> <color=#E8B84B>{an}</color> внёс тебя в список врагов.");
            tp.ChatMessage($"Ответь тем же: <color=#E8B84B>/enemy add {an}</color>");
        }

        [HookMethod("RemoveRelation")]
        public bool API_Remove(ulong uid, string type, ulong target)
        {
            RelData d;
            if (!_data.TryGetValue(uid, out d)) return false;
            var list = type == "enemy" ? d.Enemies : d.Friends;
            bool ok = list.Remove(target);
            if (ok) SaveData();
            return ok;
        }

        [HookMethod("GetNotify")]
        public bool API_GetNotify(ulong uid)
        {
            RelData d;
            return !_data.TryGetValue(uid, out d) || d.Notify;
        }

        [HookMethod("SetNotify")]
        public bool API_SetNotify(ulong uid, bool value)
        {
            Rel(uid).Notify = value;
            SaveData();
            return value;
        }

        // ── Чат-команды ──────────────────────────────────────────────────────

        private static string Clean(string s) =>
            string.IsNullOrEmpty(s) ? "?" : s.Replace("<", "").Replace(">", "");

        private void CmdRelation(BasePlayer player, string[] args, string type)
        {
            var d = Rel(player.userID);
            var list = type == "enemy" ? d.Enemies : d.Friends;
            var label = type == "enemy" ? "врагов" : "друзей";

            if (args.Length == 0 || args[0] == "list")
            {
                if (list.Count == 0) { player.ChatMessage($"Список {label} пуст — /{type} add <ник>"); return; }
                player.ChatMessage($"Список {label} ({list.Count}):");
                foreach (var id in list)
                {
                    var online = BasePlayer.FindByID(id) != null ? "<color=#8dd06f>онлайн</color>" : "<color=#666>офф</color>";
                    player.ChatMessage($"  <color=#E8B84B>{NameOf(id)}</color> {online}");
                }
                return;
            }

            if (args[0] == "add" && args.Length > 1)
            {
                var target = FindPlayer(string.Join(" ", args.Skip(1)));
                if (target == 0) { player.ChatMessage("Игрок не найден (он должен был заходить на сервер)."); return; }
                var err = API_Add(player.userID, type, target);
                player.ChatMessage(err ?? $"Добавлен: <color=#E8B84B>{NameOf(target)}</color>");
                return;
            }

            if (type == "enemy" && args[0] == "ping" && args.Length > 1)
            {
                var pid = FindPlayer(args[1]);
                if (pid == 0) { player.ChatMessage("Игрок не найден."); return; }
                var err2 = API_ScanEnemy(player, pid, args.Length > 2 ? args[2] : "auto");
                if (err2 != null) player.ChatMessage($"<color=#cd412b>◆ ПИНГ:</color> {err2}");
                return;
            }

            if (args[0] == "del" && args.Length > 1)
            {
                var name = string.Join(" ", args.Skip(1));
                var id = list.FirstOrDefault(x =>
                    string.Equals(NameOf(x), name, StringComparison.OrdinalIgnoreCase));
                if (id == 0) { player.ChatMessage("Такого нет в списке."); return; }
                API_Remove(player.userID, type, id);
                player.ChatMessage($"Удалён: <color=#E8B84B>{NameOf(id)}</color>");
                return;
            }

            player.ChatMessage($"/{type} add <ник> · /{type} del <ник> · /{type} list");
        }

        private ulong FindPlayer(string name)
        {
            // сначала онлайн — точное совпадение
            var online = BasePlayer.activePlayerList.FirstOrDefault(p =>
                string.Equals(p.displayName, name, StringComparison.OrdinalIgnoreCase));
            if (online != null) return online.userID;
            var part = BasePlayer.activePlayerList.FirstOrDefault(p =>
                p.displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (part != null) return part.userID;
            // оффлайн — через covalence
            var pl = covalence.Players.FindPlayer(name);
            ulong id;
            return pl != null && ulong.TryParse(pl.Id, out id) ? id : 0UL;
        }

        [ChatCommand("friend")] private void CmdFriend(BasePlayer p, string c, string[] a) => CmdRelation(p, a, "friend");
        [ChatCommand("enemy")]  private void CmdEnemy(BasePlayer p, string c, string[] a) => CmdRelation(p, a, "enemy");

        [ChatCommand("relnotify")]
        private void CmdNotify(BasePlayer p, string c, string[] a)
        {
            bool now = !API_GetNotify(p.userID);
            API_SetNotify(p.userID, now);
            p.ChatMessage(now ? "Уведомления о своих/врагах <color=#8dd06f>включены</color>."
                              : "Уведомления о своих/врагах <color=#cd412b>выключены</color>.");
        }
    }
}
