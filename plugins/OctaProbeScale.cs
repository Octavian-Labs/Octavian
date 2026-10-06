using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    // Пробник: 8 быков с разными стратегиями масштаба трупа.
    // probescale.run  — спавн рядом перед админом
    // probescale.kill — убить всех (уроном, не Kill())
    // probescale.dump — список трупов рядом с типом/scale/parent
    [Info("OctaProbeScale", "Octavian", "1.0.0")]
    [Description("Отладочный плагин: стратегии масштаба туш Gen2-животных")]
    public class OctaProbeScale : RustPlugin
    {
        private const string PrefabBull = "assets/rust.ai/agents/bull/bull.prefab";
        private const string PrefabBoar = "assets/rust.ai/agents/boar/boar.prefab";
        private string _prefab = PrefabBull;

        private class Variant
        {
            public string Tag;
            public float Scale;
            public bool NoDeployable;   // spawnDeployableCorpseOnDeath = false
            public bool TrackCorpse;    // parentEnt-матчинг + масштаб трупа в OnEntitySpawned
            public bool LateRescale;    // свип трупов 2 мин — проверка пост-спавн репликации
            public BaseCombatEntity Ent;
        }

        private readonly List<Variant> _v = new List<Variant>();
        private readonly Dictionary<BaseEntity, int> _corpseFor = new Dictionary<BaseEntity, int>();
        private readonly List<Vector3> _deathSpots = new List<Vector3>();
        private Timer _sweeper;

        [ConsoleCommand("probescale.run")]
        private void CmdRun(ConsoleSystem.Arg arg)
        {
            _prefab = PrefabBull;
            RunSpawn(arg, false);
        }

        [ConsoleCommand("probescale.runboar")]
        private void CmdRunBoar(ConsoleSystem.Arg arg)
        {
            _prefab = PrefabBoar;
            RunSpawn(arg, true);
        }

        private void RunSpawn(ConsoleSystem.Arg arg, bool boar)
        {
            if (!arg.IsAdmin) return;
            var p = arg.Player();
            if (p == null) // RCON — берём первого онлайн-игрока
                foreach (var pl in BasePlayer.activePlayerList) { p = pl; break; }
            var center = p != null ? p.transform.position + p.transform.forward * 10f : new Vector3(0, 80, 0);
            var right = p != null ? p.transform.right : Vector3.right;

            _v.Clear(); _corpseFor.Clear(); _deathSpots.Clear();

            var esc = boar ? 0.55f : 0.5f; // у кабана свита 0.55
            _v.Add(new Variant { Tag = "0 ctrl scale1", Scale = 1f });
            _v.Add(new Variant { Tag = "1 scale2 plain", Scale = 2f });
            _v.Add(new Variant { Tag = "2 scale2+corpsehook", Scale = 2f, TrackCorpse = true });
            _v.Add(new Variant { Tag = "3 scale2+noDeployable", Scale = 2f, NoDeployable = true });
            _v.Add(new Variant { Tag = "4 scale2+lateRescale", Scale = 2f, LateRescale = true });
            _v.Add(new Variant { Tag = "5 scale3+corpsehook", Scale = 3f, TrackCorpse = true });
            _v.Add(new Variant { Tag = "6 scale2+hook+noDepl", Scale = 2f, TrackCorpse = true, NoDeployable = true });
            _v.Add(new Variant { Tag = "7 escortscale+hook", Scale = esc, TrackCorpse = true });

            for (var i = 0; i < _v.Count; i++)
            {
                var v = _v[i];
                var pos = center + right * (i - _v.Count / 2f) * 5f;
                try { pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 0.2f; } catch { }

                var ent = GameManager.server.CreateEntity(_prefab, pos, Quaternion.identity) as BaseCombatEntity;
                if (ent == null) { Puts("probe: spawn fail " + v.Tag); continue; }

                ent.enableSaving = false;
                ent.networkEntityScale = true;
                ent.transform.localScale = Vector3.one * v.Scale;

                var ai = ent as Rust.Ai.Gen2.LivestockAnimal;
                if (ai != null) { try { ai.AnimalName = v.Tag; } catch { } }

                if (v.NoDeployable)
                {
                    var f = ent.GetType().GetField("spawnDeployableCorpseOnDeath");
                    if (f != null) { f.SetValue(ent, false); Puts("probe: " + v.Tag + " noDeployable=ok"); }
                    else Puts("probe: " + v.Tag + " поля spawnDeployableCorpseOnDeath нет!");
                }

                ent.Spawn();
                ent.InitializeHealth(500, 500);
                v.Ent = ent;
            }
            Puts("probe: заспавнено " + _v.Count + " (" + (boar ? "кабаны" : "быки") + ")");
        }

        [ConsoleCommand("probescale.kill")]
        private void CmdKill(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            foreach (var v in _v)
            {
                if (v.Ent == null || v.Ent.IsDestroyed) continue;
                v.Ent.Hurt(999999f); // уроном — реальный путь смерти
                _deathSpots.Add(v.Ent.transform.position);
                if (v.TrackCorpse) _corpseFor[v.Ent] = _v.IndexOf(v);
            }
            // свип для lateRescale — каждые 2с, 2 минуты
            _sweeper?.Destroy();
            var n = 0;
            _sweeper = timer.Repeat(2f, 60, () =>
            {
                n++;
                SweepCorpses(n * 2);
            });
            Puts("probe: все убиты, ждём туши");
        }

        [ConsoleCommand("probescale.dump")]
        private void CmdDump(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            var center = p != null ? p.transform.position : Vector3.zero;
            var r = p != null ? 80f : 100000f;
            Puts("=== трупы рядом ===");
            foreach (var e in BaseNetworkable.serverEntities)
            {
                var bc = e as BaseCorpse;
                if (bc == null || bc.IsDestroyed) continue;
                if (Vector3.Distance(bc.transform.position, center) > r) continue;
                var par = bc.parentEnt;
                Puts(string.Format("corpse {0} scale={1:0.##} pos={2} parent={3} netScale={4}",
                    bc.GetType().Name,
                    bc.transform.localScale.x,
                    bc.transform.position,
                    par == null ? "null" : par.ShortPrefabName,
                    bc.networkEntityScale));
            }
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var bc = entity as BaseCorpse;
            if (bc == null) return;
            var src = bc.parentEnt;
            Puts(string.Format("probe: corpse spawn {0} parent={1} pos={2}",
                bc.GetType().Name,
                src == null ? "null" : src.ShortPrefabName,
                bc.transform.position));
            if (src == null) return;
            int idx;
            if (!_corpseFor.TryGetValue(src, out idx)) return;
            var v = _v[idx];
            Puts("probe: corpse matched variant '" + v.Tag + "' -> scale " + v.Scale);
            bc.networkEntityScale = true;
            bc.transform.localScale = Vector3.one * v.Scale;
        }

        private void SweepCorpses(int elapsedSec)
        {
            foreach (var e in BaseNetworkable.serverEntities)
            {
                var bc = e as BaseCorpse;
                if (bc == null || bc.IsDestroyed) continue;
                foreach (var d in _deathSpots)
                {
                    if (Vector3.Distance(bc.transform.position, d) > 6f) continue;
                    // ищем вариант lateRescale по позиции родителя
                    var par = bc.parentEnt;
                    if (par == null) break;
                    var idx = _v.FindIndex(x => x.Ent == par);
                    if (idx < 0 || !_v[idx].LateRescale) break;
                    var s = _v[idx].Scale;
                    bc.networkEntityScale = true;
                    bc.transform.localScale = Vector3.one * s;
                    try { bc.SendNetworkUpdateImmediate(); } catch { }
                    Puts("probe: lateRescale sweep t+" + elapsedSec + "s -> scale " + s);
                    break;
                }
            }
        }

        private void Unload()
        {
            _sweeper?.Destroy();
        }
    }
}
