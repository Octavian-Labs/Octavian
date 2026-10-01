using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    // OctaTP — телепортация в домовом стиле OctavianRSM.
    // Писался как компактная замена NTeleportation (nivex): без сканера монументов
    // и интеграций с кланами — только дома, запросы игрокам, возврат и админ-tp.
    [Info("OctaTP", "Octavian", "1.1.0")]
    [Description("Телепортация: дома (sethome/home/delhome), запросы tpr/tpa, возврат tpb, города outpost/bandit, админские tp/tphere и телепорт по метке карты /tpm")]
    public class OctaTP : RustPlugin
    {
        private const string PermUse    = "octatp.use";    // дома и запросы
        private const string PermAdmin  = "octatp.admin";  // /tp, /tphere — без кулдауна и отсчёта

        private sealed class PluginConfig
        {
            [JsonProperty("Максимум домов на игрока")]
            public int HomesLimit = 3;

            [JsonProperty("Отсчёт перед телепортом (сек, 0 = мгновенно)")]
            public int CountdownSec = 10;

            [JsonProperty("Кулдаун между телепортами (сек)")]
            public int CooldownSec = 120;

            [JsonProperty("Отменять телепорт при получении урона")]
            public bool InterruptOnDamage = true;

            [JsonProperty("Отменять телепорт при движении")]
            public bool InterruptOnMove = true;

            [JsonProperty("Время жизни запроса /tpr (сек)")]
            public int RequestTimeoutSec = 30;

            [JsonProperty("Запретить дома на чужой территории (зона чужого шкафа)")]
            public bool BlockEnemyCupboard = true;

            [JsonProperty("Дома только в зоне своего шкафа")]
            public bool RequireOwnCupboard = false;

            [JsonProperty("Разрешить /tpb — возврат на точку до телепорта")]
            public bool AllowTpBack = true;

            [JsonProperty("Разрешить /outpost и /bandit")]
            public bool MonumentTp = true;

            [JsonProperty("Блокировать телепорт в города пока игрок в розыске (hostile)")]
            public bool BlockWhenHostile = true;

            [JsonProperty("Имена маркеров карты: Город NPC (Outpost)")]
            public string[] OutpostMarkers = { "compound", "outpost" };

            [JsonProperty("Имена маркеров карты: Лагерь бандитов")]
            public string[] BanditMarkers = { "bandit_town" };

            [JsonProperty("Префикс сообщений в чате")]
            public string ChatPrefix = "<color=#E8B84B>◆ OctaTP</color>";
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

        // ── Данные: дома и точка возврата живут в data/OctaTP.json ───────────

        private sealed class PlayerData
        {
            public Dictionary<string, float[]> Homes = new();
            public float[] Back;
        }

        private Dictionary<ulong, PlayerData> _data = new();

        private PlayerData Get(BasePlayer p) =>
            _data.TryGetValue(p.userID, out var d) ? d : _data[p.userID] = new PlayerData();

        private void LoadData()
        {
            try { _data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerData>>(Name) ?? new(); }
            catch { _data = new(); }
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _data);

        private static float[] Pack(Vector3 v) => new[] { v.x, v.y, v.z };
        private static Vector3 Unpack(float[] a) => a != null && a.Length == 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;
        private static bool HasPos(float[] a) => a != null && a.Length == 3;

        // ── Состояние телепортов (в памяти, переживать рестарт не должно) ────

        private sealed class PendingTp
        {
            public Vector3 From;
            public Vector3 To;
            public Timer Timer;
            public int Left;
        }

        private readonly Dictionary<ulong, PendingTp> _pending = new();       // отсчёт
        private readonly Dictionary<ulong, float> _cooldown = new();          // unix-время окончания
        private readonly Dictionary<ulong, ulong> _requests = new();          // цель -> проситель
        private readonly Dictionary<ulong, float> _requestTime = new();       // цель -> unix-время запроса
        private readonly HashSet<ulong> _damaged = new();                     // получил урон во время отсчёта
        private readonly HashSet<ulong> _mapTp = new();                       // админы с включённым тп по метке

        private Vector3? _outpostPos;  // ищем один раз при загрузке по маркерам карты
        private Vector3? _banditPos;

        private void Init()
        {
            LoadData();
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
        }

        private void Unload()
        {
            foreach (var p in _pending.Values) p.Timer?.Destroy();
            _pending.Clear();
            _requests.Clear();
            _requestTime.Clear();
            _damaged.Clear();
            _mapTp.Clear();
            SaveData();
        }

        private void OnServerInitialized()
        {
            // маркер монумента на карте: prefab id 1724395471, категория = имя города
            _outpostPos = FindMonumentPos(_cfg.OutpostMarkers);
            _banditPos = FindMonumentPos(_cfg.BanditMarkers);
        }

        private static Vector3? FindMonumentPos(string[] markers)
        {
            foreach (var prefab in World.Serialization.world.prefabs)
            {
                if (prefab.id == 1724395471 && markers.Contains(prefab.category))
                {
                    var pos = new Vector3(prefab.position.x, prefab.position.y, prefab.position.z);
                    var ground = TerrainMeta.HeightMap.GetHeight(pos);
                    return pos.y < ground ? new Vector3(pos.x, ground + 1f, pos.z) : pos;
                }
            }
            return null;
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null) return;
            CancelPending(player.userID);
            _damaged.Remove(player.userID);
        }

        private void Msg(BasePlayer p, string text) =>
            p.ChatMessage($"{_cfg.ChatPrefix} <color=#9AA4B2>—</color> {text}");

        private bool HasPerm(BasePlayer p, string perm) =>
            permission.UserHasPermission(p.UserIDString, perm) || p.Connection?.authLevel >= 2;

        // ── Ядро телепортации ───────────────────────────────────────────────

        // проверки кулдауна/права уже сделаны вызывающим кодом
        private void StartTeleport(BasePlayer player, Vector3 target)
        {
            var isAdmin = HasPerm(player, PermAdmin);

            // кулдаун (админам не считаем)
            if (!isAdmin && _cooldown.TryGetValue(player.userID, out var until) && until > TimeStamp())
            {
                Msg(player, $"Кулдаун: подождите ещё <color=#E8B84B>{until - TimeStamp():0}</color> сек");
                return;
            }

            // уже ждёт телепорт — повторный вызов отменяет
            if (CancelPending(player.userID))
            {
                Msg(player, "Телепорт отменён");
                return;
            }

            if (isAdmin || _cfg.CountdownSec <= 0)
            {
                DoTeleport(player, target);
                return;
            }

            _pending[player.userID] = new PendingTp
            {
                From = player.transform.position,
                To = target,
                Left = _cfg.CountdownSec,
                Timer = timer.Every(1f, () => TickPending(player.userID))
            };
            Msg(player, $"Телепортация через <color=#E8B84B>{_cfg.CountdownSec}</color> сек — не двигайтесь");
        }

        private void TickPending(ulong userId)
        {
            if (!_pending.TryGetValue(userId, out var p)) return;
            var player = BasePlayer.FindByID(userId);

            var fail = player == null || !player.IsConnected ? "игрок отключился"
                : player.IsDead() ? "вы погибли"
                : _damaged.Remove(userId) ? "вы получили урон"
                : _cfg.InterruptOnMove && Vector3.Distance(player.transform.position, p.From) > 1.5f ? "вы сдвинулись"
                : null;

            if (fail != null)
            {
                p.Timer?.Destroy();
                _pending.Remove(userId);
                if (player != null && player.IsConnected) Msg(player, $"Телепорт отменён: {fail}");
                return;
            }

            if (--p.Left > 0) return;

            p.Timer?.Destroy();
            _pending.Remove(userId);
            DoTeleport(player, p.To);
        }

        private bool CancelPending(ulong userId)
        {
            if (!_pending.TryGetValue(userId, out var p)) return false;
            p.Timer?.Destroy();
            _pending.Remove(userId);
            return true;
        }

        private void DoTeleport(BasePlayer player, Vector3 pos)
        {
            var mount = player.GetMounted();
            if (mount != null) mount.DismountPlayer(player, false);
            player.SetParent(null);
            Get(player).Back = Pack(player.transform.position); // точка для /tpb — любой телепорт
            player.Teleport(pos);
            player.SendNetworkUpdateImmediate();
            SaveData();
            if (!HasPerm(player, PermAdmin) && _cfg.CooldownSec > 0)
                _cooldown[player.userID] = TimeStamp() + _cfg.CooldownSec;
        }

        private static float TimeStamp() => (float)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!_cfg.InterruptOnDamage) return;
            var p = entity as BasePlayer;
            if (p != null && _pending.ContainsKey(p.userID)) _damaged.Add(p.userID);
        }

        // ── Поиск игрока по имени/steamid ───────────────────────────────────

        private BasePlayer FindPlayer(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            query = query.Trim().ToLowerInvariant();
            BasePlayer partial = null;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p.UserIDString == query) return p;
                var name = p.displayName?.ToLowerInvariant() ?? "";
                if (name == query) return p;
                if (partial == null && name.Contains(query)) partial = p;
            }
            return partial;
        }

        private void Help(BasePlayer p) => Msg(p,
            "<color=#E8B84B>/sethome имя</color> · <color=#E8B84B>/home имя</color> · " +
            "<color=#E8B84B>/delhome имя</color> · <color=#E8B84B>/homes</color> · " +
            "<color=#E8B84B>/tpr игрок</color> · <color=#E8B84B>/tpa</color> · <color=#E8B84B>/tpb</color> · " +
            "<color=#E8B84B>/outpost</color> · <color=#E8B84B>/bandit</color>");

        // ── Дома ────────────────────────────────────────────────────────────

        [ChatCommand("sethome")]
        private void CmdSetHome(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermUse)) { Msg(player, "Нет доступа (<color=#E06666>octatp.use</color>)"); return; }
            if (args.Length == 0) { Msg(player, "Укажите имя: <color=#E8B84B>/sethome base</color>"); return; }

            var name = args[0].Trim().ToLowerInvariant();
            if (name.Length > 20) { Msg(player, "Имя дома — максимум 20 символов"); return; }

            var d = Get(player);
            if (!d.Homes.ContainsKey(name) && d.Homes.Count >= _cfg.HomesLimit)
            {
                Msg(player, $"Лимит домов: <color=#E06666>{_cfg.HomesLimit}</color>. Удалите старый: /delhome имя");
                return;
            }
            if (_cfg.RequireOwnCupboard && !player.IsBuildingAuthed())
            {
                Msg(player, "Дом можно ставить только в зоне действия <color=#E06666>своего шкафа</color>");
                return;
            }
            if (_cfg.BlockEnemyCupboard && player.IsBuildingBlocked())
            {
                Msg(player, "Нельзя ставить дом на <color=#E06666>чужой территории</color>");
                return;
            }

            d.Homes[name] = Pack(player.transform.position);
            SaveData();
            Msg(player, $"Дом <color=#E8B84B>{name}</color> сохранён ({d.Homes.Count}/{_cfg.HomesLimit})");
        }

        [ChatCommand("home")]
        private void CmdHome(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermUse)) { Msg(player, "Нет доступа (<color=#E06666>octatp.use</color>)"); return; }
            if (args.Length == 0) { CmdHomes(player, command, args); return; }

            var d = Get(player);
            if (!d.Homes.TryGetValue(args[0].Trim().ToLowerInvariant(), out var pos))
            {
                Msg(player, $"Дом не найден. Список: <color=#E8B84B>/homes</color>");
                return;
            }
            StartTeleport(player, Unpack(pos));
        }

        [ChatCommand("homes")]
        private void CmdHomes(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermUse)) { Msg(player, "Нет доступа (<color=#E06666>octatp.use</color>)"); return; }
            var d = Get(player);
            Msg(player, d.Homes.Count == 0
                ? "Домов нет. Создать: <color=#E8B84B>/sethome имя</color>"
                : $"Ваши дома ({d.Homes.Count}/{_cfg.HomesLimit}): <color=#E8B84B>{string.Join("</color>, <color=#E8B84B>", d.Homes.Keys)}</color>");
        }

        [ChatCommand("delhome")]
        private void CmdDelHome(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermUse)) { Msg(player, "Нет доступа (<color=#E06666>octatp.use</color>)"); return; }
            var d = Get(player);
            if (args.Length == 0 || !d.Homes.Remove(args[0].Trim().ToLowerInvariant()))
            {
                Msg(player, "Дом не найден. Список: <color=#E8B84B>/homes</color>");
                return;
            }
            SaveData();
            Msg(player, "Дом удалён");
        }

        // ── Возврат ─────────────────────────────────────────────────────────

        [ChatCommand("tpb")]
        private void CmdTpBack(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermUse)) { Msg(player, "Нет доступа (<color=#E06666>octatp.use</color>)"); return; }
            if (!_cfg.AllowTpBack) { Msg(player, "Возврат отключён в конфиге"); return; }
            if (!HasPos(Get(player).Back)) { Msg(player, "Некуда возвращаться — вы ещё не телепортировались"); return; }
            StartTeleport(player, Unpack(Get(player).Back));
        }

        // ── Запросы игрокам ─────────────────────────────────────────────────

        [ChatCommand("tpr")]
        private void CmdTpr(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermUse)) { Msg(player, "Нет доступа (<color=#E06666>octatp.use</color>)"); return; }
            if (args.Length == 0) { Msg(player, "Укажите игрока: <color=#E8B84B>/tpr имя</color>"); return; }

            var target = FindPlayer(args[0]);
            if (target == null) { Msg(player, "Игрок не найден"); return; }
            if (target == player) { Msg(player, "Себе запрос отправлять не надо"); return; }

            _requests[target.userID] = player.userID;
            _requestTime[target.userID] = TimeStamp();
            Msg(player, $"Запрос отправлен <color=#E8B84B>{target.displayName}</color> ({_cfg.RequestTimeoutSec} сек)");
            Msg(target, $"<color=#E8B84B>{player.displayName}</color> просит телепорт к вам — принять: <color=#E8B84B>/tpa</color>");
        }

        [ChatCommand("tpa")]
        private void CmdTpa(BasePlayer player, string command, string[] args)
        {
            if (!_requests.TryGetValue(player.userID, out var requesterId)
                || _requestTime[player.userID] + _cfg.RequestTimeoutSec < TimeStamp())
            {
                _requests.Remove(player.userID);
                Msg(player, "Нет активных запросов");
                return;
            }
            _requests.Remove(player.userID);
            _requestTime.Remove(player.userID);

            var requester = BasePlayer.FindByID(requesterId);
            if (requester == null || !requester.IsConnected || !HasPerm(requester, PermUse))
            {
                Msg(player, "Игрок уже отключился");
                return;
            }
            Msg(player, $"Принято — <color=#E8B84B>{requester.displayName}</color> скоро будет у вас");
            StartTeleport(requester, player.transform.position);
        }

        // ── Админские телепорты ─────────────────────────────────────────────

        // /tp имя | /tp a b | /tp x y z | /tp here имя
        [ChatCommand("tp")]
        private void CmdTp(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermAdmin)) { Msg(player, "Нет доступа (<color=#E06666>octatp.admin</color>)"); return; }
            if (args.Length == 0)
            {
                Msg(player, "<color=#E8B84B>/tp игрок</color> · <color=#E8B84B>/tp a b</color> · " +
                            "<color=#E8B84B>/tp x y z</color> · <color=#E8B84B>/tp here игрок</color>");
                return;
            }

            if (args[0] == "here" && args.Length > 1)
            {
                var t = FindPlayer(args[1]);
                if (t == null) { Msg(player, "Игрок не найден"); return; }
                DoTeleport(t, player.transform.position);
                Msg(player, $"<color=#E8B84B>{t.displayName}</color> телепортирован к вам");
                return;
            }

            if (args.Length >= 3 &&
                float.TryParse(args[0], out var x) && float.TryParse(args[1], out var y) && float.TryParse(args[2], out var z))
            {
                DoTeleport(player, new Vector3(x, y, z));
                Msg(player, $"Телепорт в <color=#E8B84B>{x:0} {y:0} {z:0}</color>");
                return;
            }

            var first = FindPlayer(args[0]);
            if (first == null) { Msg(player, "Игрок не найден"); return; }

            if (args.Length >= 2)
            {
                var second = FindPlayer(args[1]);
                if (second == null) { Msg(player, "Второй игрок не найден"); return; }
                DoTeleport(first, second.transform.position);
                Msg(player, $"<color=#E8B84B>{first.displayName}</color> → <color=#E8B84B>{second.displayName}</color>");
                return;
            }

            DoTeleport(player, first.transform.position);
            Msg(player, $"Вы → <color=#E8B84B>{first.displayName}</color>");
        }

        [ChatCommand("tphere")]
        private void CmdTpHere(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermAdmin)) { Msg(player, "Нет доступа (<color=#E06666>octatp.admin</color>)"); return; }
            if (args.Length == 0) { Msg(player, "Укажите игрока: <color=#E8B84B>/tphere имя</color>"); return; }
            var t = FindPlayer(args[0]);
            if (t == null) { Msg(player, "Игрок не найден"); return; }
            DoTeleport(t, player.transform.position);
            Msg(player, $"<color=#E8B84B>{t.displayName}</color> телепортирован к вам");
        }

        // ── Города: /outpost и /bandit ──────────────────────────────────────

        [ChatCommand("outpost")]
        private void CmdOutpost(BasePlayer player, string command, string[] args) => MonumentTeleport(player, _outpostPos, "Город NPC");

        [ChatCommand("bandit")]
        private void CmdBandit(BasePlayer player, string command, string[] args) => MonumentTeleport(player, _banditPos, "Лагерь бандитов");

        private void MonumentTeleport(BasePlayer player, Vector3? pos, string label)
        {
            if (!HasPerm(player, PermUse)) { Msg(player, "Нет доступа (<color=#E06666>octatp.use</color>)"); return; }
            if (!_cfg.MonumentTp) { Msg(player, "Телепорты в города отключены в конфиге"); return; }
            if (pos == null) { Msg(player, $"Метка <color=#E06666>{label}</color> не найдена на этой карте"); return; }
            if (_cfg.BlockWhenHostile && !HasPerm(player, PermAdmin) && player.unHostileTime > Time.time)
            {
                Msg(player, "Вы в <color=#E06666>розыске</color> — сначала отойдите от городов");
                return;
            }
            StartTeleport(player, pos.Value);
            Msg(player, $"Цель: <color=#E8B84B>{label}</color>");
        }

        // ── Админ: телепорт по метке на карте (/tpm вкл/выкл) ───────────────

        [ChatCommand("tpm")]
        private void CmdTpMarker(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermAdmin)) { Msg(player, "Нет доступа (<color=#E06666>octatp.admin</color>)"); return; }
            if (_mapTp.Remove(player.userID))
                Msg(player, "Телепорт по метке <color=#E06666>выключен</color> — метки работают как обычно");
            else
            {
                _mapTp.Add(player.userID);
                Msg(player, "Телепорт по метке <color=#59C073>включён</color> — поставьте метку на карте (правый клик)");
            }
        }

        // хук создания метки на карте; false — метка не ставится, вместо неё телепорт
        private object OnMapMarkerAdd(BasePlayer player, ProtoBuf.MapNote note)
        {
            if (player == null || !_mapTp.Contains(player.userID)) return null;
            var pos = note.worldPosition;

            // земля или крыша: луч сверху вниз, берём самую высокую поверхность
            float y = TerrainMeta.HeightMap.GetHeight(pos);
            var top = TerrainMeta.HighestPoint.y + 250f;
            var hits = Physics.RaycastAll(new Vector3(pos.x, top, pos.z), Vector3.down, top,
                Rust.Layers.Mask.World | Rust.Layers.Mask.Terrain | Rust.Layers.Mask.Default,
                QueryTriggerInteraction.Ignore);
            if (hits.Length > 0) y = hits.Max(h => h.point.y);
            if (player.IsFlying) y = Mathf.Max(y, player.transform.position.y);

            player.PauseFlyHackDetection(15f); // чтобы античит не кикнул за мгновенный прыжок
            DoTeleport(player, new Vector3(pos.x, y + 1f, pos.z));
            Msg(player, $"Телепорт по метке: <color=#E8B84B>{pos.x:0} {y:0} {pos.z:0}</color>");
            return false;
        }
    }
}
