using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaAdminTools", "Octavian", "1.1.1")]
    [Description("RCON-инструменты OctavianRSM: адресное администрирование, спавн, телепорт и режим обслуживания")]
    public class OctaAdminTools : RustPlugin
    {
        private sealed class AdminState
        {
            public bool Maintenance { get; set; }
            public string MaintenanceMessage { get; set; } = "Технические работы. Попробуйте позже.";
            public HashSet<ulong> Allowed { get; set; } = new();
        }

        private AdminState _state = new();
        private readonly HashSet<ulong> _godPlayers = new();

        private void Init()
        {
            try { _state = Interface.Oxide.DataFileSystem.ReadObject<AdminState>(Name) ?? new AdminState(); }
            catch { _state = new AdminState(); }
            _state.Allowed ??= new HashSet<ulong>();
        }

        private void Unload() => SaveState();
        private void SaveState() => Interface.Oxide.DataFileSystem.WriteObject(Name, _state);

        private object CanClientLogin(Network.Connection connection)
        {
            if (!_state.Maintenance || connection.authLevel > 0 || _state.Allowed.Contains(connection.userid)) return null;
            return _state.MaintenanceMessage;
        }

        private object OnEntityTakeDamage(BasePlayer player, HitInfo info)
            => player != null && _godPlayers.Contains(player.userID) ? true : null;

        // короткие имена → prefab-пути (актуальный набор Rust)
        private static readonly Dictionary<string, string> Prefabs = new()
        {
            ["bear"]          = "assets/rust.ai/agents/bear/bear.prefab",
            ["polarbear"]     = "assets/rust.ai/agents/bear/polarbear.prefab",
            ["wolf"]          = "assets/rust.ai/agents/wolf/wolf.prefab",
            ["boar"]          = "assets/rust.ai/agents/boar/boar.prefab",
            ["stag"]          = "assets/rust.ai/agents/stag/stag.prefab",
            ["chicken"]       = "assets/rust.ai/agents/chicken/chicken.prefab",
            ["horse"]         = "assets/rust.ai/agents/horse/horse.prefab",
            ["scientist"]     = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientist.prefab",
            ["heavyscientist"]= "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistheavy.prefab",
            ["murderer"]      = "assets/rust.ai/agents/npcplayer/humannpc/murderer/murderer.prefab",
            ["scarecrow"]     = "assets/rust.ai/agents/npcplayer/humannpc/scarecrow/scarecrow.prefab",
            ["bradley"]       = "assets/prefabs/npc/m2bradley/bradleyapc.prefab",
            ["patrolheli"]    = "assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab",
            ["ch47"]          = "assets/prefabs/npc/ch47/ch47scientists.prefab",
            ["minicopter"]    = "assets/content/vehicles/minicopter/minicopter.entity.prefab",
            ["scrapheli"]     = "assets/content/vehicles/scrap heli carrier/scraptransporthelicopter.prefab",
            ["rhib"]          = "assets/content/vehicles/boats/rhib/rhib.prefab",
            ["rowboat"]       = "assets/content/vehicles/boats/rowboat/rowboat.prefab",
            ["sedan"]         = "assets/content/vehicles/sedan_a/sedantest.entity.prefab",
            ["mlrs"]          = "assets/content/vehicles/mlrs/mlrs.entity.prefab",
            ["airdropcrate"]  = "assets/prefabs/misc/supply drop/supply_drop.prefab",
            ["hackablecrate"] = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab"
        };

        private static string ResolvePrefab(string name)
        {
            name = name.Trim().ToLowerInvariant();
            if (Prefabs.TryGetValue(name, out var path)) return path;
            if (name.Contains("/") || name.EndsWith(".prefab")) return name;
            return null;
        }

        private static BasePlayer FindPlayer(ConsoleSystem.Arg arg, int idx)
        {
            var s = arg.GetString(idx);
            if (string.IsNullOrEmpty(s)) return null;
            return BasePlayer.FindAwakeOrSleeping(s);
        }

        // octa.spawnat <entity> <player> — сущность перед лицом игрока (взгляд по земле)
        [ConsoleCommand("octa.spawnat")]
        private void CmdSpawnAt(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var path = ResolvePrefab(arg.GetString(0));
            var target = FindPlayer(arg, 1);
            if (path == null) { arg.ReplyWith($"Unknown entity '{arg.GetString(0)}'. Known: {string.Join(", ", Prefabs.Keys)}"); return; }
            if (target == null) { arg.ReplyWith("Player not found"); return; }

            var ray = target.eyes.HeadRay();
            var forward = ray.direction;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = target.transform.forward;
            forward.Normalize();
            var position = target.transform.position + forward * 6f + Vector3.up * 0.5f;
            var mask = Rust.Layers.Mask.Terrain | Rust.Layers.Mask.World | Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed | Rust.Layers.Mask.Vehicle_Large;
            if (Physics.Raycast(ray, out var hit, 200f, mask, QueryTriggerInteraction.Ignore))
                position = hit.point + Vector3.up * 0.5f;
            Spawn(path, position, Quaternion.LookRotation(forward), arg, target.displayName);
        }

        [ConsoleCommand("octa.targetinfo")]
        private void CmdTargetInfo(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var target = FindPlayer(arg, 0);
            if (target == null) { arg.ReplyWith("Player not found"); return; }
            var ray = target.eyes.HeadRay();
            var mask = Rust.Layers.Mask.Terrain | Rust.Layers.Mask.World | Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed | Rust.Layers.Mask.Vehicle_Large;
            var hitText = Physics.Raycast(ray, out var hit, 200f, mask, QueryTriggerInteraction.Ignore)
                ? $"look=({hit.point.x:0.0}, {hit.point.y:0.0}, {hit.point.z:0.0}) {hit.collider.name}"
                : "look=no hit";
            var pos = target.transform.position;
            arg.ReplyWith($"{target.displayName} [{target.UserIDString}] pos=({pos.x:0.0}, {pos.y:0.0}, {pos.z:0.0}) {hitText}");
        }

        // octa.spawnatpos <entity> <x> <y> <z> — точные координаты
        [ConsoleCommand("octa.spawnatpos")]
        private void CmdSpawnAtPos(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var path = ResolvePrefab(arg.GetString(0));
            if (path == null) { arg.ReplyWith($"Unknown entity '{arg.GetString(0)}'"); return; }
            Spawn(path, new Vector3(arg.GetFloat(1), arg.GetFloat(2), arg.GetFloat(3)), Quaternion.identity, arg, "pos");
        }

        private void Spawn(string path, Vector3 pos, Quaternion rotation, ConsoleSystem.Arg arg, string where)
        {
            var ent = GameManager.server.CreateEntity(path, pos, rotation);
            if (ent == null) { arg.ReplyWith($"CreateEntity failed: {path}"); return; }
            ent.Spawn();
            arg.ReplyWith($"spawned {ent.ShortPrefabName} near {where} at ({pos.x:0.0}, {pos.y:0.0}, {pos.z:0.0})");
        }

        // octa.killtype <shortname> — удалить все сущности типа (зачистка случайных спавнов)
        [ConsoleCommand("octa.killtype")]
        private void CmdKillType(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var name = arg.GetString(0).ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) { arg.ReplyWith("usage: octa.killtype <shortname>"); return; }
            int n = 0;
            var snapshot = new List<BaseNetworkable>(BaseNetworkable.serverEntities);
            foreach (var e in snapshot)
            {
                var be = e as BaseEntity;
                if (be == null || be.IsDestroyed) continue;
                if (be.ShortPrefabName != name && !be.PrefabName.Contains(name)) continue;
                be.Kill();
                n++;
            }
            arg.ReplyWith($"killed {n} × '{name}'");
        }

        [ConsoleCommand("octa.maintenance")]
        private void CmdMaintenance(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var action = arg.GetString(0).ToLowerInvariant();
            if (action == "status" || string.IsNullOrEmpty(action))
            {
                arg.ReplyWith($"maintenance={_state.Maintenance}; allowed={_state.Allowed.Count}; message={_state.MaintenanceMessage}");
                return;
            }
            if (action != "on" && action != "off") { arg.ReplyWith("usage: octa.maintenance on|off [message]"); return; }
            _state.Maintenance = action == "on";
            if (_state.Maintenance && arg.Args != null && arg.Args.Length > 1)
                _state.MaintenanceMessage = string.Join(" ", arg.Args.Skip(1));
            SaveState();
            arg.ReplyWith($"maintenance={_state.Maintenance}; allowed={_state.Allowed.Count}");
        }

        [ConsoleCommand("octa.allow")]
        private void CmdAllow(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var action = arg.GetString(0).ToLowerInvariant();
            if (action == "list" || string.IsNullOrEmpty(action))
            {
                arg.ReplyWith(_state.Allowed.Count == 0 ? "maintenance allow-list is empty" : string.Join(", ", _state.Allowed));
                return;
            }
            if (!ulong.TryParse(arg.GetString(1), out var id)) { arg.ReplyWith("usage: octa.allow add|remove <steamid>"); return; }
            if (action == "add") _state.Allowed.Add(id);
            else if (action == "remove") _state.Allowed.Remove(id);
            else { arg.ReplyWith("usage: octa.allow add|remove|list <steamid>"); return; }
            SaveState();
            arg.ReplyWith($"allow-list {action}: {id}; total={_state.Allowed.Count}");
        }

        [ConsoleCommand("octa.heal")]
        private void CmdHeal(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var p = FindPlayer(arg, 0);
            if (p == null) { arg.ReplyWith("Player not found"); return; }
            p.health = p.MaxHealth();
            p.SendNetworkUpdateImmediate();
            arg.ReplyWith($"healed {p.displayName} to {p.health:0}");
        }

        [ConsoleCommand("octa.revive")]
        private void CmdRevive(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var p = FindPlayer(arg, 0);
            if (p == null) { arg.ReplyWith("Player not found"); return; }
            if (p.IsWounded()) p.StopWounded();
            p.health = Math.Max(p.health, 25f);
            p.SendNetworkUpdateImmediate();
            arg.ReplyWith($"revived {p.displayName}; health={p.health:0}");
        }

        [ConsoleCommand("octa.god")]
        private void CmdGod(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var p = FindPlayer(arg, 0);
            if (p == null) { arg.ReplyWith("Player not found"); return; }
            if (!bool.TryParse(arg.GetString(1), out var enabled)) { arg.ReplyWith("usage: octa.god <player> true|false"); return; }
            if (enabled) _godPlayers.Add(p.userID); else _godPlayers.Remove(p.userID);
            arg.ReplyWith($"god {p.displayName}={enabled}");
        }

        [ConsoleCommand("octa.kill")]
        private void CmdKill(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var p = FindPlayer(arg, 0);
            if (p == null) { arg.ReplyWith("Player not found"); return; }
            p.Die();
            arg.ReplyWith($"killed {p.displayName}");
        }

        [ConsoleCommand("octa.strip")]
        private void CmdStrip(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var p = FindPlayer(arg, 0);
            if (p == null) { arg.ReplyWith("Player not found"); return; }
            p.inventory.Strip();
            arg.ReplyWith($"inventory cleared: {p.displayName}");
        }

        // octa.tppos <player> <x> <y> <z>
        [ConsoleCommand("octa.tppos")]
        private void CmdTpPos(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var p = FindPlayer(arg, 0);
            if (p == null) { arg.ReplyWith("Player not found"); return; }
            var pos = new Vector3(arg.GetFloat(1), arg.GetFloat(2), arg.GetFloat(3));
            Teleport(p, pos);
            arg.ReplyWith($"teleported {p.displayName} → ({pos.x:0}, {pos.y:0}, {pos.z:0})");
        }

        // octa.tpto <player> <targetPlayer>
        [ConsoleCommand("octa.tpto")]
        private void CmdTpTo(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var p = FindPlayer(arg, 0);
            var t = FindPlayer(arg, 1);
            if (p == null || t == null) { arg.ReplyWith("Player not found"); return; }
            Teleport(p, t.transform.position);
            arg.ReplyWith($"teleported {p.displayName} → {t.displayName}");
        }

        private static void Teleport(BasePlayer p, Vector3 pos)
        {
            if (p.IsSleeping()) p.EndSleeping();
            p.Teleport(pos);
        }

        // octa.list — список доступных коротких имён
        [ConsoleCommand("octa.list")]
        private void CmdList(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            arg.ReplyWith(new StringBuilder("octa entities: ").AppendJoin(", ", Prefabs.Keys).ToString());
        }
    }
}
