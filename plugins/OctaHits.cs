// OctaHits — плавающие цифры урона при попадании.
// Игрок, NPC, животное: над точкой хита вылетает маленькая цифра урона,
// дрейфует вверх и тает — прицел не заслоняется (числа экранно-ориентированы,
// размер не растёт с дистанцией, жизнь < 1с).
// Реализация — CUI на слое Hud: единственный безопасный способ без admin-флага
// (ddraw.text требует IsAdmin у клиента — Facepunch это запретили специально).
// Позиция — проекция мировой точки попадания через глаза атакующего:
// цифра появляется там, куда реально прилетел урон, а не у прицела.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaHits", "Octavian", "1.0.0")]
    [Description("Плавающие цифры урона у точки попадания (игрок/NPC/животное).")]
    public class OctaHits : RustPlugin
    {
        #region Конфигурация

        private Cfg _cfg;

        private class Cfg
        {
            [JsonProperty("Показывать урон по игрокам")]   public bool ShowPlayers = true;
            [JsonProperty("Показывать урон по NPC")]       public bool ShowNpcs = true;
            [JsonProperty("Показывать урон по животным")]  public bool ShowAnimals = true;
            [JsonProperty("Минимальный урон для показа")]  public float MinDamage = 0.5f;
            [JsonProperty("Максимум цифр в секунду на игрока")]
            public int MaxPerSecond = 8;

            [JsonProperty("Время жизни цифры, сек")]       public float Lifetime = 0.7f;
            [JsonProperty("Дрейф вверх за жизнь (доля экрана)")]
            public float Rise = 0.035f;
            [JsonProperty("Случайный разброс позиции (доля экрана)")]
            public float Jitter = 0.012f;

            [JsonProperty("Размер шрифта")]                public int FontSize = 15;
            [JsonProperty("Размер при хедшоте")]           public int HeadshotSize = 20;

            // градация по урону: до Med — светлый, до High — золотой, выше — оранжевый
            [JsonProperty("Порог среднего урона")]         public float MedAt = 15f;
            [JsonProperty("Порог высокого урона")]         public float HighAt = 40f;
            [JsonProperty("Цвет слабого урона")]           public string ColLow = "#ffedd6";
            [JsonProperty("Цвет среднего урона")]          public string ColMed = "#d69a4a";
            [JsonProperty("Цвет высокого урона")]          public string ColHigh = "#ff5a2a";
            [JsonProperty("Цвет хедшота")]                 public string ColHead = "#ff2a2a";

            [JsonProperty("Округление (0 = целые, 1 = десятые)")]
            public int Decimals = 0;
        }

        protected override void LoadConfig()
        {
            // PITFALLS §1: Config == null при горячей загрузке Carbon
            if (Config == null)
            {
                var path = System.IO.Path.Combine(Interface.Oxide.ConfigDirectory, Name + ".json");
                var dyn = new DynamicConfigFile(path);
                if (!dyn.Exists(null)) dyn.WriteObject(new Cfg());
                typeof(Plugin).GetProperty("Config").SetValue(this, dyn);
            }
            base.LoadConfig();
            try { _cfg = Config.ReadObject<Cfg>(); }
            catch (Exception ex) { PrintError($"Конфиг не прочитан, дефолты: {ex.Message}"); _cfg = null; }
            if (_cfg == null) _cfg = new Cfg();
            SaveConfig();
        }

        protected override void LoadDefaultConfig() => _cfg = new Cfg();
        protected override void SaveConfig() { if (_cfg != null) Config.WriteObject(_cfg, true); }

        #endregion

        #region Состояние

        private const string UiBase = "OctaHits";

        // личный выключатель игрока (не persist — лёгкий плагин)
        private readonly HashSet<ulong> _disabled = new HashSet<ulong>();
        // троттлинг: uid → окно секунды {t0, count}
        private readonly Dictionary<ulong, float> _winStart = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, int>  _winCount = new Dictionary<ulong, int>();
        private ulong _seq;

        // имена элементов в полёте — CUI уничтожается строго по имени,
        // дубли имён становятся сиротами, которых не снять (см. PITFALLS по UI)
        private readonly HashSet<string> _live = new HashSet<string>();

        private void Unload()
        {
            foreach (var pl in BasePlayer.activePlayerList)
            {
                if (pl == null) continue;
                foreach (var n in _live) CuiHelper.DestroyUi(pl, n);
                CuiHelper.DestroyUi(pl, UiBase);
            }
            _live.Clear();
        }

        private bool Throttled(ulong uid)
        {
            var now = Time.realtimeSinceStartup;
            float t0;
            if (!_winStart.TryGetValue(uid, out t0) || now - t0 >= 1f)
            { _winStart[uid] = now; _winCount[uid] = 1; return false; }
            var c = _winCount[uid] + 1;
            _winCount[uid] = c;
            return c > _cfg.MaxPerSecond;
        }

        #endregion

        #region Ядро

        // Одна точка входа на любой урон: животные/NPC/игроки — всё BaseCombatEntity.
        // Строения и прочее отсекаем по типу жертвы.
        private void OnEntityTakeDamage(BaseCombatEntity victim, HitInfo info)
        {
            if (victim == null || info == null) return;

            var dmg = info.damageTypes?.Total() ?? 0f;
            if (dmg < _cfg.MinDamage) return;

            var attacker = info.InitiatorPlayer;
            if (attacker == null || attacker.IsNpc || attacker.IsDead()) return;
            if (ReferenceEquals(attacker, victim)) return;                    // самоурон
            if (_disabled.Contains((ulong)attacker.userID)) return;

            // фильтр жертвы
            if (victim is BasePlayer)
            {
                var vp = (BasePlayer)victim;
                if (vp.IsNpc ? !_cfg.ShowNpcs : !_cfg.ShowPlayers) return;
            }
            else if (victim is BaseAnimalNPC || victim.ShortPrefabName != null &&
                     victim.ShortPrefabName.Contains("animal"))
            { if (!_cfg.ShowAnimals) return; }
            else if (victim is BaseNpc) { if (!_cfg.ShowNpcs) return; }
            else return; // строения, техника, ресурсы — не показываем

            var uid = (ulong)attacker.userID;
            if (Throttled(uid)) return;

            var hitPos = info.HitPositionWorld;
            if (hitPos == Vector3.zero) hitPos = victim.bounds.center;
            if (hitPos == Vector3.zero) hitPos = victim.transform.position;
            Show(attacker, hitPos, dmg, info.isHeadshot);
        }

        // Мировая точка хита → экранные координаты через камеру атакующего.
        // Глаза на сервере реплицируются из клиентского ввода — точность достаточная.
        // Возвращает false, если точка за спиной/слишком близко.
        private bool WorldToScreen(BasePlayer p, Vector3 w, out float sx, out float sy)
        {
            sx = 0.5f; sy = 0.5f;
            var eyes = p.eyes;
            if (eyes == null) return false;
            var rel = Quaternion.Inverse(eyes.rotation) * (w - eyes.position);
            if (rel.z < 0.3f) return false;
            // FOV клиента неизвестен — берём типичный вертикальный ~70° (graphics.fov),
            // горизонтальный через aspect 16:9. Погрешность — пара процентов экрана.
            var tanV = Mathf.Tan(35f * Mathf.Deg2Rad);
            var tanH = tanV * (16f / 9f);
            sx = 0.5f + (rel.x / (rel.z * tanH)) * 0.5f;
            sy = 0.5f + (rel.y / (rel.z * tanV)) * 0.5f;
            if (sx < 0.03f || sx > 0.97f || sy < 0.03f || sy > 0.97f) return false; // за кадром
            return true;
        }

        private void Show(BasePlayer p, Vector3 hitPos, float dmg, bool head)
        {
            float sx, sy;
            if (!WorldToScreen(p, hitPos, out sx, out sy)) { sx = 0.5f; sy = 0.55f; }

            // разброс — серия выстрелов не клеит цифры в одну точку
            sx += UnityEngine.Random.Range(-_cfg.Jitter, _cfg.Jitter);
            sy += UnityEngine.Random.Range(-_cfg.Jitter, _cfg.Jitter);

            var color = head ? _cfg.ColHead
                      : dmg >= _cfg.HighAt ? _cfg.ColHigh
                      : dmg >= _cfg.MedAt ? _cfg.ColMed : _cfg.ColLow;
            var size = head ? _cfg.HeadshotSize : _cfg.FontSize;
            var text = dmg.ToString(_cfg.Decimals > 0 ? "F" + _cfg.Decimals : "F0");

            // уникальный префикс на каждый хит — иначе сироты (дубли имён не заменяются)
            var baseName = $"{UiBase}_{_seq++}";
            var steps = 3; // 0 → подъём → подъём → смерть
            for (var i = 0; i < steps; i++)
            {
                var k = i;
                timer.Once(_cfg.Lifetime * i / steps, () => DrawStep(p, baseName, sx, sy, text, color, size, k, steps));
            }
            var last = baseName + "_" + (steps - 1);
            timer.Once(_cfg.Lifetime + 0.35f, () =>
            {
                if (p != null && p.IsConnected) CuiHelper.DestroyUi(p, last);
                _live.Remove(last);
            });
        }

        // Каждый шаг — НОВОЕ имя элемента: старое явно уничтожаем, новое добавляем.
        // Итог — плавный дрейф вверх без сирот и залипания
        private void DrawStep(BasePlayer p, string baseName, float sx, float sy,
                              string text, string color, int size, int step, int steps)
        {
            if (p == null || !p.IsConnected) return;
            if (step > 0)
            {
                var prev = baseName + "_" + (step - 1);
                CuiHelper.DestroyUi(p, prev);
                _live.Remove(prev);
            }
            var name = baseName + "_" + step;
            _live.Add(name);
            var y = Mathf.Clamp01(sy + _cfg.Rise * step / (steps - 1));
            // тающая альфа к последним кадрам — мягко уходит, не мельтешит
            var alpha = step == steps - 1 ? 0.45f : step == steps - 2 ? 0.8f : 1f;
            var c = new CuiElementContainer();
            var el = new CuiElement
            {
                Name = name,
                Parent = "Hud",
                FadeOut = 0.2f
            };
            el.Components.Add(new CuiTextComponent
            {
                Text = text,
                FontSize = size,
                Align = TextAnchor.MiddleCenter,
                Color = HexAlpha(color, alpha),
                FadeIn = 0.05f
            });
            var w = 0.05f;
            var h = 0.03f;
            el.Components.Add(new CuiRectTransformComponent
            {
                AnchorMin = $"{sx - w:0.####} {y - h:0.####}",
                AnchorMax = $"{sx + w:0.####} {y + h:0.####}"
            });
            c.Add(el);
            CuiHelper.AddUi(p, c);
        }

        private static string HexAlpha(string hex, float a)
        {
            // "#rrggbb" → "r g b a"
            try
            {
                var col = ColorEx.Parse(hex);
                return $"{col.r:0.###} {col.g:0.###} {col.b:0.###} {a:0.##}";
            }
            catch { return $"1 1 1 {a:0.##}"; }
        }

        #endregion

        #region Команда

        [ChatCommand("hitnum")]
        private void CmdToggle(BasePlayer p, string cmd, string[] args)
        {
            var uid = (ulong)p.userID;
            if (_disabled.Remove(uid))
                p.ChatMessage("<color=#E8B84B>◆ OctaHits</color> Цифры урона включены");
            else
            {
                _disabled.Add(uid);
                p.ChatMessage("<color=#E8B84B>◆ OctaHits</color> Цифры урона выключены");
            }
        }

        #endregion
    }
}
