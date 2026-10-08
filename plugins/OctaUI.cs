using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    // ============================================================================
    // OctaUI — общий UI-фреймворк экосистемы Octavian.
    //
    // Потребители:  [PluginReference] Plugin OctaUI;
    //               OctaUI?.Call("Panel", container, parent, name, props);
    //
    // Первым аргументом примитивов идёт CuiElementContainer потребителя —
    // фреймворк добавляет в него элементы (объект передаётся по ссылке).
    // Все параметры вынесены в JObject-словарь props, чтобы API не ломался
    // при добавлении новых опций.
    //
    // Цвета и материалы можно писать токенами ("accent", "bg", "row", "green"),
    // сырыми "r g b a" или #RRGGBB[AA]. Наборы акцента для композитов:
    // "gold" (дефолт), "red", "green", "blue" либо произвольный "r g b a"/#hex —
    // яркий/приглушённый/тёмный/стеклянный оттенки выводятся автоматически.
    // ============================================================================

    [Info("OctaUI", "Octavian", "1.0.0")]
    [Description("Общий UI-фреймворк Octavian: тема, примитивы CUI, композиты, layout-пресеты, ImageLibrary")]
    public class OctaUI : RustPlugin
    {
        [PluginReference] private Plugin ImageLibrary;

        private const string PermAdmin = "octaui.admin";

        #region Конфигурация темы

        private class PluginConfig
        {
            [JsonProperty("Тема")] public ThemeCfg Theme = new ThemeCfg();
        }

        private class ThemeCfg
        {
            [JsonProperty("Акцент яркий")]      public string Accent      = "0.95 0.75 0.30 1";
            [JsonProperty("Акцент")]            public string AccentDim   = "0.82 0.61 0.20 1";
            [JsonProperty("Акцент тёмный")]     public string AccentDark  = "0.48 0.32 0.10 1";
            [JsonProperty("Акцент стекло")]     public string AccentGlass = "0.82 0.61 0.20 0.18";
            [JsonProperty("Фон")]               public string Bg          = "0.015 0.012 0.010 0.94";
            [JsonProperty("Панель")]            public string Panel       = "0.05 0.045 0.035 0.92";
            [JsonProperty("Строка")]            public string Row         = "0.08 0.07 0.05 0.82";
            [JsonProperty("Наведение")]         public string Hover       = "0.13 0.105 0.06 0.95";
            [JsonProperty("Текст")]             public string Text        = "0.93 0.88 0.75 1";
            [JsonProperty("Текст приглушённый")]public string Muted       = "0.58 0.53 0.43 1";
            [JsonProperty("Белый")]             public string White       = "1 0.98 0.90 1";
            [JsonProperty("Зелёный")]           public string Green       = "0.20 0.52 0.27 0.90";
            [JsonProperty("Красный")]           public string Red         = "0.58 0.13 0.10 0.92";
            [JsonProperty("Синий")]             public string Blue        = "0.18 0.35 0.55 0.90";
            [JsonProperty("Фон шкалы")]         public string BarBg       = "0.10 0.05 0.04 0.95";
            [JsonProperty("Шрифт")]             public string Font        = "robotocondensed-bold.ttf";
            [JsonProperty("Шрифт рукописный")]  public string FontHand    = "permanentmarker.ttf";
            [JsonProperty("Материал панелей")]  public string Mat         = "assets/content/ui/uibackgroundblur-ingamemenu.mat";
            [JsonProperty("Материал кнопок")]   public string MatBtn      = "assets/content/ui/uibackgroundblur.mat";
        }

        private PluginConfig _cfg;

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _cfg = Config.ReadObject<PluginConfig>(); }
            catch { _cfg = null; }
            if (_cfg == null || _cfg.Theme == null)
            {
                _cfg = new PluginConfig();
                SaveConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_cfg, true);

        // Токены → поля темы (короткие имена + псевдонимы "gold.*" для привычности)
        private string TokenValue(string name)
        {
            var t = _cfg?.Theme;
            if (t == null || string.IsNullOrEmpty(name)) return null;
            switch (name)
            {
                case "accent":      case "gold":        return t.Accent;
                case "accent.dim":  case "gold.dim":    return t.AccentDim;
                case "accent.dark": case "gold.dark":   return t.AccentDark;
                case "accent.glass":case "gold.glass":  return t.AccentGlass;
                case "bg":          case "glass":       return t.Bg;
                case "panel":       case "glass.lt":    return t.Panel;
                case "row":         case "glass.row":   return t.Row;
                case "hover":       case "glass.hov":   return t.Hover;
                case "text":        case "tmain":       return t.Text;
                case "muted":       case "tmuted":      return t.Muted;
                case "white":       case "twhite":      return t.White;
                case "green":       case "ok":          return t.Green;
                case "red":         case "danger":      return t.Red;
                case "blue":        case "info":        return t.Blue;
                case "barbg":                           return t.BarBg;
                case "font":                            return t.Font;
                case "font.hand":                       return t.FontHand;
                case "mat":         case "material":    return t.Mat;
                case "mat.btn":                         return t.MatBtn;
                default:                                return null;
            }
        }

        // Разрешение цветового параметра: токен | #hex | сырой "r g b a"
        private string ResolveColor(string v, string fallback = "1 1 1 1")
        {
            if (string.IsNullOrEmpty(v)) return fallback;
            if (v[0] == '#') { var hex = HexToRgba(v); return hex ?? v; }
            if (v.Contains(" ")) return v;
            var tok = TokenValue(v);
            return tok ?? v;
        }

        // Разрешение шрифта/материала через те же токены
        private string ResolveFont(string v)
        {
            if (string.IsNullOrEmpty(v)) return _cfg.Theme.Font;
            var tok = TokenValue(v);
            return tok ?? v;
        }

        private string ResolveMat(string v)
        {
            // null — параметр не задан → тематический материал; "" — явно без материала
            if (v == null) return _cfg.Theme.Mat;
            if (v.Length == 0) return "";
            var tok = TokenValue(v);
            return tok ?? v;
        }

        private static string HexToRgba(string hex)
        {
            var h = hex.TrimStart('#');
            if (h.Length != 6 && h.Length != 8) return null;
            int r, g, b, a = 255;
            if (!int.TryParse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)) return null;
            if (!int.TryParse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) return null;
            if (!int.TryParse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return null;
            if (h.Length == 8 && !int.TryParse(h.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a)) return null;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} {3:0.###}",
                r / 255f, g / 255f, b / 255f, a / 255f);
        }

        // Производные акцента: яркий/приглушённый/тёмный/стекло для композитов.
        // Принимает имя ("gold","red","green","blue") или произвольный цвет.
        private JObject AccentSet(string spec)
        {
            var t = _cfg.Theme;
            string bright;
            switch (spec ?? "accent")
            {
                case "accent": case "gold": bright = t.Accent; break;
                case "red":    case "danger": bright = t.Red; break;
                case "green":  case "ok":     bright = t.Green; break;
                case "blue":   case "info":   bright = t.Blue; break;
                default: bright = ResolveColor(spec, t.Accent); break;
            }

            // Именованные акценты — курируемые триады; произвольный — расчётные
            switch (spec)
            {
                case "accent": case "gold": case null:
                    return new JObject { ["bright"] = t.Accent, ["dim"] = t.AccentDim,
                        ["dark"] = t.AccentDark, ["glass"] = t.AccentGlass };
                case "red": case "danger":
                    return new JObject { ["bright"] = t.Red, ["dim"] = MulRgba(t.Red, 0.85f),
                        ["dark"] = MulRgba(t.Red, 0.55f), ["glass"] = AlphaRgba(t.Red, 0.20f) };
                case "green": case "ok":
                    return new JObject { ["bright"] = "0.35 0.75 0.40 1", ["dim"] = t.Green,
                        ["dark"] = MulRgba(t.Green, 0.55f), ["glass"] = AlphaRgba(t.Green, 0.20f) };
                case "blue": case "info":
                    return new JObject { ["bright"] = "0.45 0.65 0.85 1", ["dim"] = t.Blue,
                        ["dark"] = MulRgba(t.Blue, 0.55f), ["glass"] = AlphaRgba(t.Blue, 0.20f) };
            }

            return new JObject { ["bright"] = bright, ["dim"] = MulRgba(bright, 0.86f),
                ["dark"] = MulRgba(bright, 0.50f), ["glass"] = AlphaRgba(bright, 0.20f) };
        }

        private static string MulRgba(string rgba, float k)
        {
            var p = rgba.Split(' ');
            if (p.Length < 4) return rgba;
            float r, g, b, a;
            if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r) ||
                !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g) ||
                !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b) ||
                !float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a)) return rgba;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} {3:0.###}",
                r * k, g * k, b * k, a);
        }

        private static string AlphaRgba(string rgba, float alpha)
        {
            var p = rgba.Split(' ');
            if (p.Length < 4) return rgba;
            return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3:0.###}", p[0], p[1], p[2], alpha);
        }

        #endregion

        #region Парсинг props (JObject)

        private static string S(JObject p, string k, string d = "")
        {
            var t = p?[k];
            if (t == null || t.Type == JTokenType.Null) return d;
            return t.Type == JTokenType.String ? (string)t : t.ToString();
        }

        private static float F(JObject p, string k, float d)
        {
            var t = p?[k];
            if (t == null || t.Type == JTokenType.Null) return d;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (float)t;
            float v;
            return float.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : d;
        }

        private static int I(JObject p, string k, int d)
        {
            var t = p?[k];
            if (t == null || t.Type == JTokenType.Null) return d;
            if (t.Type == JTokenType.Integer) return (int)t;
            int v;
            return int.TryParse((string)t, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : d;
        }

        private static bool B(JObject p, string k, bool d = false)
        {
            var t = p?[k];
            if (t == null || t.Type == JTokenType.Null) return d;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            var s = ((string)t)?.Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "yes" ? true : s == "false" || s == "0" || s == "no" ? false : d;
        }

        // "x1 y1 x2 y2" → (min, max)
        private static void Rect(JObject p, out string aMin, out string aMax, string def = "0 0 1 1")
        {
            var r = (S(p, "rect") ?? "").Trim();
            if (string.IsNullOrEmpty(r)) r = def;
            var parts = r.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            aMin = parts.Length >= 2 ? parts[0] + " " + parts[1] : "0 0";
            aMax = parts.Length >= 4 ? parts[2] + " " + parts[3] : "1 1";
        }

        private static void Offsets(JObject p, out string offMin, out string offMax)
        {
            var o = (S(p, "offset") ?? "").Trim();
            if (string.IsNullOrEmpty(o)) { offMin = "0 0"; offMax = "0 0"; return; }
            var parts = o.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            offMin = parts.Length >= 2 ? parts[0] + " " + parts[1] : "0 0";
            offMax = parts.Length >= 4 ? parts[2] + " " + parts[3] : "0 0";
        }

        private static readonly Dictionary<string, TextAnchor> AlignMap = new Dictionary<string, TextAnchor>
        {
            ["left"] = TextAnchor.MiddleLeft, ["center"] = TextAnchor.MiddleCenter, ["right"] = TextAnchor.MiddleRight,
            ["topleft"] = TextAnchor.UpperLeft, ["top"] = TextAnchor.UpperCenter, ["topright"] = TextAnchor.UpperRight,
            ["bottomleft"] = TextAnchor.LowerLeft, ["bottom"] = TextAnchor.LowerCenter, ["bottomright"] = TextAnchor.LowerRight,
            ["middleleft"] = TextAnchor.MiddleLeft, ["middlecenter"] = TextAnchor.MiddleCenter, ["middleright"] = TextAnchor.MiddleRight,
            ["upperleft"] = TextAnchor.UpperLeft, ["uppercenter"] = TextAnchor.UpperCenter, ["upperright"] = TextAnchor.UpperRight,
            ["lowerleft"] = TextAnchor.LowerLeft, ["lowercenter"] = TextAnchor.LowerCenter, ["lowerright"] = TextAnchor.LowerRight,
        };

        private static TextAnchor AlignOf(string s)
        {
            if (string.IsNullOrEmpty(s)) return TextAnchor.MiddleCenter;
            TextAnchor a;
            return AlignMap.TryGetValue(s.Trim().ToLowerInvariant(), out a) ? a : TextAnchor.MiddleCenter;
        }

        #endregion

        #region API: утилиты

        // Имена элементов в пространстве плагина: UiName("OctaKits","HDR") → "OctaKits_HDR"
        [HookMethod("UiName")]
        private string API_UiName(string prefix, string part) => prefix + "_" + part;

        [HookMethod("Token")]
        private object API_Token(string name) => TokenValue(name);

        [HookMethod("Resolve")]
        private object API_Resolve(string colorOrToken) => ResolveColor(colorOrToken);

        [HookMethod("Theme")]
        private object API_Theme()
        {
            var t = _cfg.Theme;
            return new JObject
            {
                ["accent"] = t.Accent, ["accent.dim"] = t.AccentDim, ["accent.dark"] = t.AccentDark,
                ["accent.glass"] = t.AccentGlass, ["bg"] = t.Bg, ["panel"] = t.Panel, ["row"] = t.Row,
                ["hover"] = t.Hover, ["text"] = t.Text, ["muted"] = t.Muted, ["white"] = t.White,
                ["green"] = t.Green, ["red"] = t.Red, ["blue"] = t.Blue, ["barbg"] = t.BarBg,
                ["font"] = t.Font, ["font.hand"] = t.FontHand, ["mat"] = t.Mat, ["mat.btn"] = t.MatBtn
            };
        }

        [HookMethod("Accent")]
        private object API_Accent(string name) => AccentSet(name);

        [HookMethod("Show")]
        private void API_Show(BasePlayer player, CuiElementContainer c) => CuiHelper.AddUi(player, c);

        // Destroy(player, "name1,name2" или JArray)
        [HookMethod("Destroy")]
        private void API_Destroy(BasePlayer player, object names)
        {
            if (player == null || names == null) return;
            var arr = names as JArray;
            if (arr != null)
            {
                foreach (var n in arr) CuiHelper.DestroyUi(player, n.ToString());
                return;
            }
            foreach (var n in names.ToString().Split(','))
            {
                var t = n.Trim();
                if (t.Length > 0) CuiHelper.DestroyUi(player, t);
            }
        }

        // Ячейка сетки: area "x1 y1 x2 y2", cols, rows(=авто), gap, index|col+row
        [HookMethod("GridRect")]
        private object API_GridRect(JObject p)
        {
            string aMin, aMax;
            Rect(p, out aMin, out aMax, "0 0 1 1");
            var a = (aMin + " " + aMax).Split(' ');
            float x0 = Ff(a[0]), y0 = Ff(a[1]), x1 = Ff(a[2]), y1 = Ff(a[3]);
            int cols = Math.Max(1, I(p, "cols", 1));
            int rows = Math.Max(1, I(p, "rows", 0));
            int index = I(p, "index", -1);
            int col = index >= 0 ? index % cols : I(p, "col", 0);
            int row = index >= 0 ? index / cols : I(p, "row", 0);
            float gap = F(p, "gap", 0.01f);
            float w = (x1 - x0 - gap * (cols - 1)) / cols;
            float h = rows > 0 ? (y1 - y0 - gap * (rows - 1)) / rows : w;
            float cx = x0 + col * (w + gap);
            // отсчёт рядов сверху вниз — привычнее для сеток карточек
            float cy = y1 - (row + 1) * h - row * gap;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####} {3:0.####}",
                cx, cy, cx + w, cy + h);
        }

        private static float Ff(string s)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }

        #endregion

        #region API: примитивы

        // Panel: rect, offset, color, cursor, fadeIn, fadeOut, mat, keyboard, needsCursor
        [HookMethod("Panel")]
        private object API_Panel(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin, oMax;
            Rect(p, out aMin, out aMax);
            Offsets(p, out oMin, out oMax);
            var el = c.Add(new CuiPanel
            {
                Image = { Color = ResolveColor(S(p, "color"), "0 0 0 0"), Material = ResolveMat(S(p, "mat")), FadeIn = F(p, "fadeIn", 0f) },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax },
                FadeOut = F(p, "fadeOut", 0f),
                CursorEnabled = B(p, "cursor"),
                KeyboardEnabled = B(p, "keyboard")
            }, parent, name ?? "");
            if (B(p, "needsCursor"))
                c.Add(new CuiElement { Components = { new CuiNeedsCursorComponent() }, Parent = el });
            return el;
        }

        // Text: rect, offset, color, text, size, align, font, fadeIn, fadeOut, outline, outlineScale
        [HookMethod("Text")]
        private object API_Text(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin, oMax;
            Rect(p, out aMin, out aMax);
            Offsets(p, out oMin, out oMax);
            var el = new CuiElement
            {
                Parent = parent,
                Name = name ?? "",
                FadeOut = F(p, "fadeOut", 0f),
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = S(p, "text"),
                        FontSize = I(p, "size", 11),
                        Align = AlignOf(S(p, "align")),
                        Color = ResolveColor(S(p, "color"), _cfg.Theme.Text),
                        Font = ResolveFont(S(p, "font")),
                        FadeIn = F(p, "fadeIn", 0f)
                    },
                    new CuiRectTransformComponent { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax }
                }
            };
            var oc = S(p, "outline");
            if (!string.IsNullOrEmpty(oc))
            {
                el.Components.Add(new CuiOutlineComponent
                {
                    Color = ResolveColor(oc, "0 0 0 0.8"),
                    Distance = S(p, "outlineScale", "0.5 -0.5")
                });
            }
            c.Add(el);
            return el;
        }

        // Button: rect, offset, color, text, size, align, font, cmd, close, textColor, fadeIn, mat
        [HookMethod("Button")]
        private object API_Button(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin, oMax;
            Rect(p, out aMin, out aMax);
            Offsets(p, out oMin, out oMax);
            var el = c.Add(new CuiButton
            {
                Button =
                {
                    Command = S(p, "cmd"),
                    Color = ResolveColor(S(p, "color"), _cfg.Theme.Panel),
                    Material = ResolveMat(S(p, "mat", "mat.btn")),
                    FadeIn = F(p, "fadeIn", 0f),
                    Close = S(p, "close")
                },
                Text =
                {
                    Text = S(p, "text"),
                    FontSize = I(p, "size", 11),
                    Align = AlignOf(S(p, "align")),
                    Color = ResolveColor(S(p, "textColor"), _cfg.Theme.White),
                    Font = ResolveFont(S(p, "font")),
                    FadeIn = F(p, "fadeIn", 0f)
                },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax },
                FadeOut = F(p, "fadeOut", 0f)
            }, parent, name ?? "");
            return el;
        }

        // Image: rect, offset, src (http-url | pngId | sprite:<path> | item:<shortname>[:skin] | itemid:<id>[:skin]),
        //        color, fadeIn, fadeOut, mat
        [HookMethod("Image")]
        private object API_Image(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin, oMax;
            Rect(p, out aMin, out aMax);
            Offsets(p, out oMin, out oMax);
            var src = S(p, "src");
            var el = new CuiElement
            {
                Parent = parent,
                Name = name ?? "",
                FadeOut = F(p, "fadeOut", 0f)
            };

            if (src.StartsWith("item:", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("itemid:", StringComparison.OrdinalIgnoreCase))
            {
                // предметная иконка: item:<shortname>[:skin] или itemid:<int>[:skin]
                var parts = src.Split(':');
                var idStr = parts.Length > 1 ? parts[1] : "";
                ulong skin = parts.Length > 2 ? ulong.Parse(parts[2]) : 0UL;
                int itemId;
                if (src.StartsWith("itemid:", StringComparison.OrdinalIgnoreCase))
                    itemId = int.Parse(idStr);
                else
                {
                    var def = ItemManager.FindItemDefinition(idStr);
                    if (def == null) return name;
                    itemId = def.itemid;
                }
                el.Components.Add(new CuiImageComponent { ItemId = itemId, SkinId = skin, FadeIn = F(p, "fadeIn", 0f) });
            }
            else if (src.StartsWith("sprite:", StringComparison.OrdinalIgnoreCase))
            {
                el.Components.Add(new CuiImageComponent
                {
                    Sprite = src.Substring(7),
                    Color = ResolveColor(S(p, "color"), "1 1 1 1"),
                    Material = S(p, "mat", "assets/icons/iconmaterial.mat"),
                    FadeIn = F(p, "fadeIn", 0f)
                });
            }
            else if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                el.Components.Add(new CuiRawImageComponent
                {
                    Url = src,
                    Color = ResolveColor(S(p, "color"), "1 1 1 1"),
                    Sprite = "assets/content/textures/generic/fulltransparent.tga",
                    FadeIn = F(p, "fadeIn", 0f)
                });
            }
            else
            {
                // числовая строка — PNG id из ImageLibrary
                el.Components.Add(new CuiRawImageComponent
                {
                    Png = src,
                    Color = ResolveColor(S(p, "color"), "1 1 1 1"),
                    Sprite = "assets/content/textures/generic/fulltransparent.tga",
                    FadeIn = F(p, "fadeIn", 0f)
                });
            }

            el.Components.Add(new CuiRectTransformComponent { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax });
            c.Add(el);
            return name;
        }

        // Sprite: rect, offset, sprite, color, fadeIn, mat — короткая форма Image
        [HookMethod("Sprite")]
        private object API_Sprite(CuiElementContainer c, string parent, string name, JObject p)
        {
            var q = (JObject)p?.DeepClone() ?? new JObject();
            q["src"] = "sprite:" + S(p, "sprite");
            return API_Image(c, parent, name, q);
        }

        // ItemIcon: rect, offset, item (shortname|itemid), skin, amount (>1 → подпись xN в углу)
        [HookMethod("ItemIcon")]
        private object API_ItemIcon(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin, oMax;
            Rect(p, out aMin, out aMax);
            Offsets(p, out oMin, out oMax);
            var itemRef = S(p, "item");
            int itemId;
            if (!int.TryParse(itemRef, out itemId))
            {
                var def = ItemManager.FindItemDefinition(itemRef);
                if (def == null) { Debug.LogWarning($"[OctaUI] ItemIcon: предмет '{itemRef}' не найден"); return name; }
                itemId = def.itemid;
            }
            var el = c.Add(new CuiPanel
            {
                Image = { ItemId = itemId, SkinId = (ulong)F(p, "skin", 0f), FadeIn = F(p, "fadeIn", 0f) },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax },
                CursorEnabled = false
            }, parent, name ?? "");
            var amount = I(p, "amount", 0);
            if (amount > 1)
            {
                API_Text(c, el, null, new JObject
                {
                    ["rect"] = "0.55 0.02 0.98 0.35", ["text"] = "x" + amount, ["size"] = I(p, "amountSize", 9),
                    ["align"] = "bottomright", ["color"] = S(p, "amountColor", "white"),
                    ["outline"] = "0 0 0 0.9", ["outlineScale"] = "0.6 -0.6"
                });
            }
            return el;
        }

        // Input: rect, offset, color, text, size, align, font, cmd, limit, readOnly, password
        [HookMethod("Input")]
        private object API_Input(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin, oMax;
            Rect(p, out aMin, out aMax);
            Offsets(p, out oMin, out oMax);
            var el = new CuiElement
            {
                Parent = parent,
                Name = name ?? "",
                FadeOut = F(p, "fadeOut", 0f),
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = S(p, "text"),
                        CharsLimit = I(p, "limit", 200),
                        Color = ResolveColor(S(p, "color"), _cfg.Theme.White),
                        IsPassword = B(p, "password"),
                        Command = S(p, "cmd"),
                        Font = ResolveFont(S(p, "font", "font.hand")),
                        FontSize = I(p, "size", 12),
                        Align = AlignOf(S(p, "align", "left")),
                        ReadOnly = B(p, "readOnly"),
                        NeedsKeyboard = true
                    },
                    new CuiNeedsCursorComponent(),
                    new CuiRectTransformComponent { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax }
                }
            };
            c.Add(el);
            return name;
        }

        // Outline: rect, offset, color, thick (доля высоты региона, 0.03), thickX
        // рамка по краю заданной области внутри parent
        [HookMethod("Outline")]
        private object API_Outline(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin, oMax;
            Rect(p, out aMin, out aMax);
            Offsets(p, out oMin, out oMax);
            var el = c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax }
            }, parent, name ?? "");

            var col = ResolveColor(S(p, "color"), _cfg.Theme.Accent);
            var th = F(p, "thick", 0.03f);
            var tx = F(p, "thickX", th);
            var fmt = CultureInfo.InvariantCulture;
            string[][] edges =
            {
                new[] { "0", (1f - th).ToString("0.###", fmt), "1", "1" },        // верх
                new[] { "0", "0", "1", th.ToString("0.###", fmt) },               // низ
                new[] { "0", "0", tx.ToString("0.###", fmt), "1" },               // лево
                new[] { (1f - tx).ToString("0.###", fmt), "0", "1", "1" }         // право
            };
            foreach (var e in edges)
            {
                c.Add(new CuiPanel
                {
                    Image = { Color = col },
                    RectTransform = { AnchorMin = e[0] + " " + e[1], AnchorMax = e[2] + " " + e[3] }
                }, el);
            }
            return el;
        }

        // Bar: rect, value (0..1), color, bg — шкала прогресса
        [HookMethod("Bar")]
        private object API_Bar(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin, oMax;
            Rect(p, out aMin, out aMax);
            Offsets(p, out oMin, out oMax);
            var el = c.Add(new CuiPanel
            {
                Image = { Color = ResolveColor(S(p, "bg"), _cfg.Theme.BarBg) },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax }
            }, parent, name ?? "");
            var v = Mathf.Clamp01(F(p, "value", 0f));
            c.Add(new CuiPanel
            {
                Image = { Color = ResolveColor(S(p, "color"), _cfg.Theme.AccentDim) },
                RectTransform = { AnchorMin = "0 0", AnchorMax = v.ToString("0.###", CultureInfo.InvariantCulture) + " 1" }
            }, el);
            return el;
        }

        #endregion

        #region API: композиты (стиль OctaBoss)

        // Root — каркас окна: стеклянный фон + шапка с акцентной линией + заголовок + ✕ + контент-зона.
        // spec: name, rect="0.17 0.08 0.83 0.92", title, subtitle, accent, icon(src), close(cmd), cursor=true, header=true
        // возвращает JObject { root, header, content, icon }
        [HookMethod("Root")]
        private object API_Root(CuiElementContainer c, JObject spec)
        {
            var name = S(spec, "name", "OctaUI_Root");
            var acc = AccentSet(S(spec, "accent"));
            string aMin, aMax;
            Rect(spec, out aMin, out aMax, "0.17 0.08 0.83 0.92");

            var root = c.Add(new CuiPanel
            {
                Image = { Color = _cfg.Theme.Bg, FadeIn = F(spec, "fadeIn", 0f) },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax },
                CursorEnabled = B(spec, "cursor", true)
            }, "Overlay", name);
            c.Add(new CuiElement { Components = { new CuiNeedsCursorComponent() }, Parent = root });

            string header = null, iconName = null;
            var contentRect = "0.008 0.012 0.992 0.985";
            if (B(spec, "header", true))
            {
                header = c.Add(new CuiPanel
                {
                    Image = { Color = _cfg.Theme.Panel },
                    RectTransform = { AnchorMin = "0 0.925", AnchorMax = "1 1" }
                }, root, name + "_HDR");
                c.Add(new CuiPanel
                {
                    Image = { Color = (string)acc["dark"] },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.03" }
                }, header);

                var titleX = "0.02";
                var iconSrc = S(spec, "icon");
                if (!string.IsNullOrEmpty(iconSrc))
                {
                    iconName = name + "_ICON";
                    c.Add(new CuiPanel
                    {
                        Image = { Color = (string)acc["dark"] },
                        RectTransform = { AnchorMin = "0.008 0.10", AnchorMax = "0.055 0.94" }
                    }, header, iconName);
                    API_Image(c, iconName, null, new JObject
                    {
                        ["rect"] = "0.05 0.05 0.95 0.95", ["src"] = iconSrc, ["fadeIn"] = F(spec, "fadeIn", 0f)
                    });
                    titleX = "0.065";
                }

                c.Add(new CuiLabel
                {
                    Text = { Text = S(spec, "title"), FontSize = I(spec, "titleSize", 15),
                             Align = TextAnchor.MiddleLeft, Color = (string)acc["bright"], Font = _cfg.Theme.Font },
                    RectTransform = { AnchorMin = titleX + " 0.55", AnchorMax = "0.80 0.95" }
                }, header);
                var sub = S(spec, "subtitle");
                if (!string.IsNullOrEmpty(sub))
                {
                    c.Add(new CuiLabel
                    {
                        Text = { Text = sub, FontSize = I(spec, "subtitleSize", 10),
                                 Align = TextAnchor.MiddleLeft, Color = _cfg.Theme.Muted, Font = _cfg.Theme.Font },
                        RectTransform = { AnchorMin = titleX + " 0.10", AnchorMax = "0.80 0.55" }
                    }, header);
                }

                var closeCmd = S(spec, "close");
                if (!string.IsNullOrEmpty(closeCmd))
                {
                    c.Add(new CuiButton
                    {
                        Button = { Command = closeCmd, Color = _cfg.Theme.Red },
                        Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = _cfg.Theme.White },
                        RectTransform = { AnchorMin = "0.965 0.15", AnchorMax = "0.995 0.90" }
                    }, header);
                }
                contentRect = "0.008 0.008 0.992 0.918";
            }

            var cr = contentRect.Split(' ');
            var content = c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = cr[0] + " " + cr[1], AnchorMax = cr[2] + " " + cr[3] }
            }, root, name + "_CNT");

            return new JObject { ["root"] = root, ["header"] = header ?? "", ["content"] = content, ["icon"] = iconName ?? "" };
        }

        // Section — заголовок раздела: текст акцентом + тонкая линия
        // props: rect, text, accent
        [HookMethod("Section")]
        private object API_Section(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin, oMax;
            Rect(p, out aMin, out aMax, "0 0 1 0.05");
            Offsets(p, out oMin, out oMax);
            var acc = AccentSet(S(p, "accent"));
            c.Add(new CuiLabel
            {
                Text = { Text = S(p, "text"), FontSize = I(p, "size", 10), Align = TextAnchor.MiddleLeft,
                         Color = (string)acc["dim"], Font = _cfg.Theme.Font },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax }
            }, parent, name ?? "");
            // линия по нижней кромке текста
            var parts = (aMin + " " + aMax).Split(' ');
            float y0 = Ff(parts[1]);
            c.Add(new CuiPanel
            {
                Image = { Color = (string)acc["dark"] },
                RectTransform = { AnchorMin = parts[0] + " " + y0.ToString("0.###", CultureInfo.InvariantCulture),
                                  AnchorMax = parts[2] + " " + (y0 + 0.004f).ToString("0.###", CultureInfo.InvariantCulture) }
            }, parent);
            return name;
        }

        // Card — карточка: фон + левая полоса статуса + рамка выделения
        // props: rect, offset, selected, strip (цвет|"none"), color, accent
        [HookMethod("Card")]
        private object API_Card(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin, oMax;
            Rect(p, out aMin, out aMax);
            Offsets(p, out oMin, out oMax);
            var acc = AccentSet(S(p, "accent"));
            var el = c.Add(new CuiPanel
            {
                Image = { Color = ResolveColor(S(p, "color"), B(p, "selected") ? (string)acc["glass"] : _cfg.Theme.Row) },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax }
            }, parent, name ?? "");

            var strip = S(p, "strip");
            if (!string.IsNullOrEmpty(strip) && strip != "none")
            {
                c.Add(new CuiPanel
                {
                    Image = { Color = ResolveColor(strip, (string)acc["dim"]) },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0.02 1" }
                }, el);
            }
            if (B(p, "selected"))
            {
                c.Add(new CuiPanel { Image = { Color = (string)acc["bright"] }, RectTransform = { AnchorMin = "0 0.97", AnchorMax = "1 1" } }, el);
                c.Add(new CuiPanel { Image = { Color = (string)acc["bright"] }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.03" } }, el);
            }
            return el;
        }

        // IconBtn — квадратная кнопка со спрайтом/картинкой (gear, trash и т.п.)
        // props: rect ИЛИ px "x y w h" + corner "tr|tl|br|bl" (px от угла родителя),
        //        src (sprite:xxx|url|item:...), cmd, close, color, iconColor, pad (0..1), fadeIn
        [HookMethod("IconBtn")]
        private object API_IconBtn(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax, oMin = "0 0", oMax = "0 0";
            var px = S(p, "px");
            if (!string.IsNullOrEmpty(px))
            {
                // пиксельное размещение от угла родителя
                var v = px.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                float x = Ff(v[0]), y = Ff(v[1]), w = Ff(v[2]), h = Ff(v[3]);
                var corner = S(p, "corner", "tr");
                var ci = CultureInfo.InvariantCulture;
                Func<float, float, string> pt = (a, b) =>
                    string.Format(ci, "{0:0.##} {1:0.##}", a, b);
                switch (corner)
                {
                    case "tr": aMin = aMax = "1 1"; oMin = pt(-x - w, -y - h); oMax = pt(-x, -y); break;
                    case "tl": aMin = aMax = "0 1"; oMin = pt(x, -y - h); oMax = pt(x + w, -y); break;
                    case "br": aMin = aMax = "1 0"; oMin = pt(-x - w, y); oMax = pt(-x, y + h); break;
                    default:   aMin = aMax = "0 0"; oMin = pt(x, y); oMax = pt(x + w, y + h); break;
                }
            }
            else Rect(p, out aMin, out aMax);

            var el = c.Add(new CuiButton
            {
                Button = { Command = S(p, "cmd"), Color = ResolveColor(S(p, "color"), _cfg.Theme.Panel),
                           Material = ResolveMat(S(p, "mat", "mat.btn")), FadeIn = F(p, "fadeIn", 0f), Close = S(p, "close") },
                Text = { Text = "", Color = "0 0 0 0" },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax }
            }, parent, name ?? "");

            var pad = F(p, "pad", 0.14f);
            var f = CultureInfo.InvariantCulture;
            API_Image(c, el, null, new JObject
            {
                ["rect"] = string.Format(f, "{0:0.###} {0:0.###} {1:0.###} {1:0.###}", pad, 1f - pad),
                ["src"] = S(p, "src", "sprite:assets/icons/gear.png"),
                ["color"] = ResolveColor(S(p, "iconColor"), _cfg.Theme.White),
                ["fadeIn"] = F(p, "fadeIn", 0f)
            });
            return el;
        }

        // Stepper — подпись | [-] [значение/инпут] [+] + тонкая акцентная шкала
        // props: rect, label, value, fmt, min, max, warn, warnText, edit, cmdMinus, cmdPlus, cmdSet, accent
        [HookMethod("Stepper")]
        private object API_Stepper(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax;
            Rect(p, out aMin, out aMax, "0 0 1 0.088");
            var acc = AccentSet(S(p, "accent"));
            var warn = B(p, "warn");
            var edit = B(p, "edit", true);
            var row = c.Add(new CuiPanel
            {
                Image = { Color = warn ? "0.25 0.12 0.03 0.85" : _cfg.Theme.Row },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax }
            }, parent, name ?? "");

            c.Add(new CuiLabel
            {
                Text = { Text = S(p, "label"), FontSize = 11, Align = TextAnchor.MiddleLeft,
                         Color = warn ? (string)acc["bright"] : _cfg.Theme.Text, Font = _cfg.Theme.Font },
                RectTransform = { AnchorMin = "0.02 0.45", AnchorMax = "0.52 0.97" }
            }, row);
            var warnText = S(p, "warnText");
            if (warn && !string.IsNullOrEmpty(warnText))
            {
                c.Add(new CuiLabel
                {
                    Text = { Text = "⚠ " + warnText, FontSize = 8, Align = TextAnchor.MiddleLeft,
                             Color = (string)acc["bright"], Font = _cfg.Theme.Font },
                    RectTransform = { AnchorMin = "0.02 0.04", AnchorMax = "0.60 0.42" }
                }, row);
            }

            var btnC = edit ? _cfg.Theme.Hover : _cfg.Theme.Panel;
            var txtC = edit ? _cfg.Theme.White : _cfg.Theme.Muted;
            c.Add(new CuiButton
            {
                Button = { Command = edit ? S(p, "cmdMinus") : "", Color = btnC },
                Text = { Text = "−", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = txtC },
                RectTransform = { AnchorMin = "0.60 0.15", AnchorMax = "0.70 0.90" }
            }, row);
            c.Add(new CuiButton
            {
                Button = { Command = edit ? S(p, "cmdPlus") : "", Color = btnC },
                Text = { Text = "+", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = txtC },
                RectTransform = { AnchorMin = "0.88 0.15", AnchorMax = "0.98 0.90" }
            }, row);

            var valName = row + "_v";
            c.Add(new CuiElement
            {
                Parent = row, Name = valName,
                Components =
                {
                    new CuiImageComponent { Color = edit ? _cfg.Theme.Bg : "0.03 0.03 0.03 0.6" },
                    new CuiRectTransformComponent { AnchorMin = "0.715 0.15", AnchorMax = "0.865 0.90" }
                }
            });
            var valStr = S(p, "valueText");
            if (string.IsNullOrEmpty(valStr))
                valStr = F(p, "value", 0f).ToString(S(p, "fmt", "0.##"), CultureInfo.InvariantCulture);
            if (edit)
            {
                c.Add(new CuiElement
                {
                    Parent = valName,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Text = valStr, Command = S(p, "cmdSet"), FontSize = 12,
                            Align = TextAnchor.MiddleCenter, Color = warn ? (string)acc["bright"] : _cfg.Theme.White,
                            CharsLimit = I(p, "limit", 12), NeedsKeyboard = true
                        },
                        new CuiNeedsCursorComponent()
                    }
                });
            }
            else
            {
                c.Add(new CuiLabel
                {
                    Text = { Text = valStr + " 🔒", FontSize = 10, Align = TextAnchor.MiddleCenter,
                             Color = _cfg.Theme.Muted, Font = _cfg.Theme.Font },
                    RectTransform = { AnchorMin = "0.715 0.15", AnchorMax = "0.865 0.90" }
                }, row);
            }

            var track = row + "_t";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.02 0.02 0.015 0.9" },
                RectTransform = { AnchorMin = "0.60 0.04", AnchorMax = "0.98 0.09" }
            }, row, track);
            var min = F(p, "min", 0f); var max = F(p, "max", 1f);
            var t = Mathf.Clamp01((F(p, "value", 0f) - min) / Math.Max(0.0001f, max - min));
            c.Add(new CuiPanel
            {
                Image = { Color = (string)acc["dim"] },
                RectTransform = { AnchorMin = "0 0", AnchorMax = t.ToString("0.###", CultureInfo.InvariantCulture) + " 1" }
            }, track);
            return row;
        }

        // Toggle — подпись | [ВКЛ/OFF] + акцентная линия под включённым
        // props: rect, label, on, cmd, edit, accent
        [HookMethod("Toggle")]
        private object API_Toggle(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax;
            Rect(p, out aMin, out aMax, "0 0 1 0.058");
            var acc = AccentSet(S(p, "accent"));
            var on = B(p, "on");
            var edit = B(p, "edit", true);
            var row = c.Add(new CuiPanel
            {
                Image = { Color = _cfg.Theme.Row },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax }
            }, parent, name ?? "");
            c.Add(new CuiLabel
            {
                Text = { Text = S(p, "label"), FontSize = 11, Align = TextAnchor.MiddleLeft,
                         Color = _cfg.Theme.Text, Font = _cfg.Theme.Font },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.70 1" }
            }, row);
            c.Add(new CuiButton
            {
                Button = { Command = edit ? S(p, "cmd") : "", Color = on ? _cfg.Theme.Green : _cfg.Theme.Panel },
                Text = { Text = on ? "ВКЛ" : "OFF", FontSize = 10, Align = TextAnchor.MiddleCenter,
                         Color = on ? _cfg.Theme.White : _cfg.Theme.Muted },
                RectTransform = { AnchorMin = "0.74 0.16", AnchorMax = "0.97 0.84" }
            }, row);
            if (on)
                c.Add(new CuiPanel
                {
                    Image = { Color = (string)acc["bright"] },
                    RectTransform = { AnchorMin = "0.74 0.05", AnchorMax = "0.97 0.14" }
                }, row);
            return row;
        }

        // TextRow — подпись сверху + поле ввода снизу (строка-редактор)
        // props: rect, label, value, cmd, limit, edit, accent
        [HookMethod("TextRow")]
        private object API_TextRow(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax;
            Rect(p, out aMin, out aMax, "0 0 1 0.082");
            var acc = AccentSet(S(p, "accent"));
            var edit = B(p, "edit", true);
            var row = c.Add(new CuiPanel
            {
                Image = { Color = _cfg.Theme.Row },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax }
            }, parent, name ?? "");
            c.Add(new CuiLabel
            {
                Text = { Text = S(p, "label"), FontSize = 10, Align = TextAnchor.MiddleLeft,
                         Color = (string)acc["dim"], Font = _cfg.Theme.Font },
                RectTransform = { AnchorMin = "0.02 0.55", AnchorMax = "0.98 0.98" }
            }, row);
            var valName = row + "_v";
            c.Add(new CuiElement
            {
                Parent = row, Name = valName,
                Components =
                {
                    new CuiImageComponent { Color = edit ? _cfg.Theme.Bg : "0.03 0.03 0.03 0.6" },
                    new CuiRectTransformComponent { AnchorMin = "0.02 0.06", AnchorMax = "0.98 0.50" }
                }
            });
            var val = S(p, "value");
            if (edit)
            {
                c.Add(new CuiElement
                {
                    Parent = valName,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Text = val, Command = S(p, "cmd"), FontSize = 10,
                            Align = AlignOf(S(p, "align", "left")), Color = _cfg.Theme.White,
                            CharsLimit = I(p, "limit", 200), NeedsKeyboard = true
                        },
                        new CuiNeedsCursorComponent()
                    }
                });
            }
            else
            {
                c.Add(new CuiLabel
                {
                    Text = { Text = (val.Length > 60 ? val.Substring(0, 60) + "…" : val) + " 🔒", FontSize = 9,
                             Align = TextAnchor.MiddleLeft, Color = _cfg.Theme.Muted, Font = _cfg.Theme.Font },
                    RectTransform = { AnchorMin = "0.02 0.06", AnchorMax = "0.98 0.50" }
                }, row);
            }
            return row;
        }

        // Toast — всплывающее уведомление поверх parentUi, авто-затухание
        // OctaUI.Call("Toast", player, parentUiName, "текст", "accent"/rgba, 2.5f)
        private readonly Dictionary<ulong, int> _toastSeq = new Dictionary<ulong, int>();

        [HookMethod("Toast")]
        private void API_Toast(BasePlayer player, string parentUi, string text, string color = "accent", float seconds = 2.5f)
        {
            if (player == null || string.IsNullOrEmpty(parentUi) || string.IsNullOrEmpty(text)) return;
            var uid = player.userID;
            var key = uid;
            int seq;
            _toastSeq.TryGetValue(key, out seq);
            seq++;
            _toastSeq[key] = seq;

            var tname = parentUi + "_TST";
            CuiHelper.DestroyUi(player, tname);
            var c = new CuiElementContainer();
            var acc = AccentSet(color);
            var col = color == "accent" ? (string)acc["dim"] : ResolveColor(color, (string)acc["dim"]);
            var el = c.Add(new CuiPanel
            {
                Image = { Color = col, FadeIn = 0.15f },
                RectTransform = { AnchorMin = "0.30 0.075", AnchorMax = "0.78 0.115" }
            }, parentUi, tname);
            c.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 11, Align = TextAnchor.MiddleCenter,
                         Color = _cfg.Theme.White, FadeIn = 0.15f, Font = _cfg.Theme.Font },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, el);
            CuiHelper.AddUi(player, c);

            timer.Once(Mathf.Max(0.5f, seconds), () =>
            {
                int cur;
                if (!_toastSeq.TryGetValue(key, out cur) || cur != seq) return;
                CuiHelper.DestroyUi(player, tname);
            });
        }

        // Confirm — модальное подтверждение поверх parentUi
        // props: title, yes, no, color (accent|rgba), cmdYes, cmdNo, accent
        [HookMethod("Confirm")]
        private object API_Confirm(CuiElementContainer c, string parentUi, string name, JObject p)
        {
            var dim = c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.75" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, parentUi, name ?? (parentUi + "_CFM"));
            var acc = AccentSet(S(p, "accent", S(p, "color")));
            var col = ResolveColor(S(p, "color"), (string)acc["dim"]);
            var box = c.Add(new CuiPanel
            {
                Image = { Color = _cfg.Theme.Bg },
                RectTransform = { AnchorMin = "0.32 0.40", AnchorMax = "0.68 0.62" }
            }, dim, dim + "_box");
            c.Add(new CuiPanel
            {
                Image = { Color = col },
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" }
            }, box);
            c.Add(new CuiLabel
            {
                Text = { Text = S(p, "title", "Подтвердить?"), FontSize = I(p, "size", 13),
                         Align = TextAnchor.MiddleCenter, Color = _cfg.Theme.White, Font = _cfg.Theme.Font },
                RectTransform = { AnchorMin = "0.05 0.35", AnchorMax = "0.95 0.90" }
            }, box);
            c.Add(new CuiButton
            {
                Button = { Command = S(p, "cmdNo"), Color = _cfg.Theme.Hover },
                Text = { Text = S(p, "no", "ОТМЕНА"), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = _cfg.Theme.Text },
                RectTransform = { AnchorMin = "0.08 0.10", AnchorMax = "0.46 0.30" }
            }, box);
            c.Add(new CuiButton
            {
                Button = { Command = S(p, "cmdYes"), Color = col },
                Text = { Text = S(p, "yes", "ПОДТВЕРДИТЬ"), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = _cfg.Theme.White },
                RectTransform = { AnchorMin = "0.54 0.10", AnchorMax = "0.92 0.30" }
            }, box);
            return dim;
        }

        // Pagination — [◀] x/y [▶]
        // props: rect, page, pages, cmdPrev, cmdNext, fmt ("{0}/{1}")
        [HookMethod("Pagination")]
        private object API_Pagination(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax;
            Rect(p, out aMin, out aMax, "0.35 0 0.65 0.05");
            var row = c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax }
            }, parent, name ?? "");
            c.Add(new CuiButton
            {
                Button = { Command = S(p, "cmdPrev"), Color = _cfg.Theme.Hover },
                Text = { Text = "◀", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = _cfg.Theme.Text },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.25 1" }
            }, row);
            c.Add(new CuiLabel
            {
                Text = { Text = string.Format(S(p, "fmt", "{0}/{1}"), I(p, "page", 1) + 1, I(p, "pages", 1)),
                         FontSize = 11, Align = TextAnchor.MiddleCenter, Color = _cfg.Theme.Muted, Font = _cfg.Theme.Font },
                RectTransform = { AnchorMin = "0.25 0", AnchorMax = "0.75 1" }
            }, row);
            c.Add(new CuiButton
            {
                Button = { Command = S(p, "cmdNext"), Color = _cfg.Theme.Hover },
                Text = { Text = "▶", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = _cfg.Theme.Text },
                RectTransform = { AnchorMin = "0.75 0", AnchorMax = "1 1" }
            }, row);
            return row;
        }

        // ActionBar — нижняя полоса кнопок действий
        // props: rect (полоса), buttons: JArray [{text, cmd, color, enabled, size}]
        [HookMethod("ActionBar")]
        private object API_ActionBar(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax;
            Rect(p, out aMin, out aMax, "0 0 1 0.07");
            var acc = AccentSet(S(p, "accent"));
            var bar = c.Add(new CuiPanel
            {
                Image = { Color = _cfg.Theme.Panel },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax }
            }, parent, name ?? "");
            c.Add(new CuiPanel
            {
                Image = { Color = (string)acc["dark"] },
                RectTransform = { AnchorMin = "0 0.97", AnchorMax = "1 1" }
            }, bar);

            var buttons = p?["buttons"] as JArray;
            if (buttons == null || buttons.Count == 0) return bar;
            var n = buttons.Count;
            var pad = F(p, "pad", 0.012f);
            var gap = F(p, "gap", 0.008f);
            var bw = (1f - pad * 2f - gap * (n - 1)) / n;
            var x = pad;
            var fmt = CultureInfo.InvariantCulture;
            for (var i = 0; i < n; i++)
            {
                var b = buttons[i] as JObject ?? new JObject();
                var enabled = B(b, "enabled", true);
                c.Add(new CuiButton
                {
                    Button = { Command = enabled ? S(b, "cmd") : "",
                               Color = enabled ? ResolveColor(S(b, "color"), (string)acc["dim"]) : _cfg.Theme.Bg },
                    Text = { Text = S(b, "text"), FontSize = I(b, "size", 11), Align = TextAnchor.MiddleCenter,
                             Color = enabled ? _cfg.Theme.White : _cfg.Theme.Muted, Font = _cfg.Theme.Font },
                    RectTransform = { AnchorMin = x.ToString("0.###", fmt) + " 0.18",
                                      AnchorMax = (x + bw).ToString("0.###", fmt) + " 0.82" }
                }, bar);
                x += bw + gap;
            }
            return bar;
        }

        // TabStrip — вертикальный или горизонтальный ряд вкладок
        // props: rect, vertical, tabs: JArray [{label, cmd, selected}], accent
        [HookMethod("TabStrip")]
        private object API_TabStrip(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax;
            Rect(p, out aMin, out aMax);
            var acc = AccentSet(S(p, "accent"));
            var strip = c.Add(new CuiPanel
            {
                Image = { Color = _cfg.Theme.Panel },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax }
            }, parent, name ?? "");
            var tabs = p?["tabs"] as JArray;
            if (tabs == null || tabs.Count == 0) return strip;
            var vertical = B(p, "vertical", true);
            var n = tabs.Count;
            var fmt = CultureInfo.InvariantCulture;
            for (var i = 0; i < n; i++)
            {
                var t = tabs[i] as JObject ?? new JObject();
                var sel = B(t, "selected");
                string ta0, ta1;
                if (vertical)
                {
                    var h = 1f / n;
                    var y1 = 1f - i * h;
                    ta0 = string.Format(fmt, "0.03 {0:0.###}", y1 - h + 0.008f);
                    ta1 = string.Format(fmt, "0.97 {0:0.###}", y1 - 0.008f);
                }
                else
                {
                    var w = 1f / n;
                    ta0 = string.Format(fmt, "{0:0.###} 0.05", i * w);
                    ta1 = string.Format(fmt, "{0:0.###} 0.95", (i + 1) * w - 0.005f);
                }
                c.Add(new CuiButton
                {
                    Button = { Command = S(t, "cmd"), Color = sel ? (string)acc["glass"] : "0 0 0 0" },
                    Text = { Text = (vertical ? (sel ? "▸ " : "   ") : "") + S(t, "label"),
                             FontSize = I(t, "size", 10), Align = vertical ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter,
                             Color = sel ? (string)acc["bright"] : _cfg.Theme.Muted, Font = _cfg.Theme.Font },
                    RectTransform = { AnchorMin = ta0, AnchorMax = ta1 }
                }, strip);
            }
            return strip;
        }

        // ListCard — выбираемая строка-элемент списка (карточка-кнопка с полосой статуса)
        // props: rect, cmd, title, sub, strip, selected, enabled, accent
        [HookMethod("ListCard")]
        private object API_ListCard(CuiElementContainer c, string parent, string name, JObject p)
        {
            string aMin, aMax;
            Rect(p, out aMin, out aMax);
            var acc = AccentSet(S(p, "accent"));
            var sel = B(p, "selected");
            var enabled = B(p, "enabled", true);
            var card = c.Add(new CuiButton
            {
                Button = { Command = enabled ? S(p, "cmd") : "",
                           Color = sel ? (string)acc["glass"] : _cfg.Theme.Row },
                Text = { Text = "", Color = "0 0 0 0" },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax }
            }, parent, name ?? "");
            c.Add(new CuiPanel
            {
                Image = { Color = ResolveColor(S(p, "strip"), enabled ? (string)acc["dim"] : _cfg.Theme.Muted) },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.025 1" }
            }, card);
            c.Add(new CuiLabel
            {
                Text = { Text = S(p, "title"), FontSize = I(p, "size", 12), Align = TextAnchor.MiddleLeft,
                         Color = sel ? (string)acc["bright"] : enabled ? _cfg.Theme.Text : _cfg.Theme.Muted,
                         Font = _cfg.Theme.Font },
                RectTransform = { AnchorMin = "0.07 0.50", AnchorMax = "0.98 0.98" }
            }, card);
            var sub = S(p, "sub");
            if (!string.IsNullOrEmpty(sub))
            {
                c.Add(new CuiLabel
                {
                    Text = { Text = sub, FontSize = 9, Align = TextAnchor.MiddleLeft,
                             Color = _cfg.Theme.Muted, Font = _cfg.Theme.Font },
                    RectTransform = { AnchorMin = "0.07 0.05", AnchorMax = "0.98 0.52" }
                }, card);
            }
            if (sel)
            {
                c.Add(new CuiPanel { Image = { Color = (string)acc["bright"] }, RectTransform = { AnchorMin = "0 0.97", AnchorMax = "1 1" } }, card);
                c.Add(new CuiPanel { Image = { Color = (string)acc["bright"] }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.03" } }, card);
            }
            return card;
        }

        #endregion

        #region API: ImageLibrary

        // Img(url) — готовый src для Image: PNG-id если ImageLibrary уже отдал картинку,
        // иначе исходный url (RawImage подхватит сам). Очередь загрузки фреймворк не ведёт —
        // плагины регистрируют картинки через ImgAdd/RegisterImage при инициализации.
        [HookMethod("Img")]
        private object API_Img(string urlOrName, ulong imageId = 0)
        {
            if (string.IsNullOrEmpty(urlOrName)) return "";
            if (ImageLibrary != null)
            {
                try
                {
                    if ((bool)ImageLibrary.Call("HasImage", urlOrName, imageId))
                    {
                        var png = ImageLibrary.Call("GetImage", urlOrName, imageId) as string;
                        if (!string.IsNullOrEmpty(png)) return png;
                    }
                }
                catch { }
            }
            return urlOrName;
        }

        // ImgAdd(url, name=null, imageId=0) — поставить картинку в очередь загрузки
        [HookMethod("ImgAdd")]
        private object API_ImgAdd(string url, string name = null, ulong imageId = 0)
        {
            if (string.IsNullOrEmpty(url) || ImageLibrary == null) return false;
            try
            {
                ImageLibrary.Call("AddImage", url, name ?? url, imageId);
                return true;
            }
            catch { return false; }
        }

        [HookMethod("ImgHas")]
        private object API_ImgHas(string name, ulong imageId = 0)
        {
            if (ImageLibrary == null) return false;
            try { return (bool)ImageLibrary.Call("HasImage", name, imageId); }
            catch { return false; }
        }

        [HookMethod("ImgReady")]
        private object API_ImgReady()
        {
            if (ImageLibrary == null) return false;
            try { return (bool)ImageLibrary.Call("IsReady"); }
            catch { return false; }
        }

        // ImgItem(shortname, skin=0) — PNG-id иконки предмета; если картинки ещё нет —
        // ставит её в очередь загрузки и возвращает "" (покажется при следующем запросе)
        [HookMethod("ImgItem")]
        private object API_ImgItem(string shortname, ulong skin = 0)
        {
            if (string.IsNullOrEmpty(shortname) || ImageLibrary == null) return "";
            try
            {
                var png = ImageLibrary.Call("GetImage", shortname, skin) as string;
                if (!string.IsNullOrEmpty(png)) return png;
                ImageLibrary.Call("ImportItemList", Name + "_items",
                    new Dictionary<string, Dictionary<ulong, string>>
                    { [shortname] = new Dictionary<ulong, string> { [skin] = "" } });
                return "";
            }
            catch { return ""; }
        }

        // ImgItems(JObject {shortname: skin} | JArray shortnames) — пакетная регистрация иконок
        [HookMethod("ImgItems")]
        private object API_ImgItems(object items)
        {
            if (ImageLibrary == null || items == null) return false;
            try
            {
                var map = new Dictionary<string, Dictionary<ulong, string>>();
                var obj = items as JObject;
                if (obj != null)
                    foreach (var kv in obj.Properties())
                        map[kv.Name] = new Dictionary<ulong, string> { [(ulong)(kv.Value ?? 0)] = "" };
                else
                    foreach (var s in items as JArray ?? new JArray())
                        map[s.ToString()] = new Dictionary<ulong, string> { [0] = "" };
                if (map.Count == 0) return false;
                ImageLibrary.Call("ImportItemList", Name + "_items", map);
                return true;
            }
            catch { return false; }
        }

        #endregion

        #region API: layout-пресеты (шаблонизатор, обобщение CuiData OctaWelcome)

        // Хранилище: data/OctaUI/layout_<plugin>.json — JObject { slot: { rect, color, mat, image, fade, ... } }
        // Пресеты:  data/OctaUI/presets_<plugin>.json — JObject { preset: { slot: {...} } }
        private readonly Dictionary<string, JObject> _layouts = new Dictionary<string, JObject>();
        private readonly Dictionary<string, JObject> _presets = new Dictionary<string, JObject>();

        private string LayoutPath(string plugin) => $"{Name}/layout_{plugin}";
        private string PresetsPath(string plugin) => $"{Name}/presets_{plugin}";

        private JObject LayoutOf(string plugin)
        {
            JObject l;
            if (!_layouts.TryGetValue(plugin, out l))
            {
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(LayoutPath(plugin)))
                    l = Interface.Oxide.DataFileSystem.ReadObject<JObject>(LayoutPath(plugin)) ?? new JObject();
                else l = new JObject();
                _layouts[plugin] = l;
            }
            return l;
        }

        private JObject PresetsOf(string plugin)
        {
            JObject pr;
            if (!_presets.TryGetValue(plugin, out pr))
            {
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(PresetsPath(plugin)))
                    pr = Interface.Oxide.DataFileSystem.ReadObject<JObject>(PresetsPath(plugin)) ?? new JObject();
                else pr = new JObject();
                _presets[plugin] = pr;
            }
            return pr;
        }

        // LayoutGet(plugin, slot=null) → props слота или весь layout
        [HookMethod("LayoutGet")]
        private object API_LayoutGet(string plugin, string slot = null)
        {
            var l = LayoutOf(plugin);
            if (string.IsNullOrEmpty(slot)) return l;
            return l[slot] as JObject;
        }

        // LayoutSet(plugin, slot, props) — записать props слота и сохранить
        [HookMethod("LayoutSet")]
        private object API_LayoutSet(string plugin, string slot, JObject props)
        {
            if (string.IsNullOrEmpty(plugin) || string.IsNullOrEmpty(slot)) return false;
            LayoutOf(plugin)[slot] = props ?? new JObject();
            Interface.Oxide.DataFileSystem.WriteObject(LayoutPath(plugin), LayoutOf(plugin));
            return true;
        }

        // LayoutPreset(plugin, preset, slots JObject) — зарегистрировать пресет (вызывается при инициализации)
        [HookMethod("LayoutPreset")]
        private object API_LayoutPreset(string plugin, string preset, JObject slots)
        {
            if (string.IsNullOrEmpty(plugin) || string.IsNullOrEmpty(preset)) return false;
            var pr = PresetsOf(plugin);
            pr[preset] = slots ?? new JObject();
            Interface.Oxide.DataFileSystem.WriteObject(PresetsPath(plugin), pr);
            return true;
        }

        // LayoutPresets(plugin) → JArray имён пресетов
        [HookMethod("LayoutPresets")]
        private object API_LayoutPresets(string plugin)
        {
            return new JArray(PresetsOf(plugin).Properties().Select(x => x.Name));
        }

        // LayoutApply(plugin, preset) → применить пресет к текущему layout (слоты перезаписываются)
        [HookMethod("LayoutApply")]
        private object API_LayoutApply(string plugin, string preset)
        {
            var pr = PresetsOf(plugin);
            var slots = pr[preset] as JObject;
            if (slots == null) return false;
            var l = LayoutOf(plugin);
            foreach (var kv in slots.Properties())
                l[kv.Name] = kv.Value?.DeepClone();
            Interface.Oxide.DataFileSystem.WriteObject(LayoutPath(plugin), l);
            return true;
        }

        // LayoutReset(plugin) → очистить layout плагина
        [HookMethod("LayoutReset")]
        private object API_LayoutReset(string plugin)
        {
            _layouts[plugin] = new JObject();
            Interface.Oxide.DataFileSystem.WriteObject(LayoutPath(plugin), _layouts[plugin]);
            return true;
        }

        // LayoutImport(plugin, JObject {slot: props}) — массовый импорт (миграция старых CuiData)
        [HookMethod("LayoutImport")]
        private object API_LayoutImport(string plugin, JObject slots)
        {
            if (slots == null) return false;
            var l = LayoutOf(plugin);
            foreach (var kv in slots.Properties())
                if (l[kv.Name] == null) l[kv.Name] = kv.Value?.DeepClone();
            Interface.Oxide.DataFileSystem.WriteObject(LayoutPath(plugin), l);
            return true;
        }

        #endregion

        #region Админка темы (/octaui)

        private void Loaded()
        {
            permission.RegisterPermission(PermAdmin, this);
        }

        private bool CanEdit(BasePlayer p) => p != null && (p.IsAdmin || permission.UserHasPermission(p.UserIDString, PermAdmin));

        [ChatCommand("octaui")]
        private void CmdOctaUI(BasePlayer player)
        {
            if (!CanEdit(player)) { SendReply(player, "[OctaUI] Нет прав (octaui.admin)"); return; }
            DrawThemeEditor(player);
        }

        private const string AdminUi = "OctaUI_Admin";

        // Список токенов редактора: (ключ, подпись, цветовой?)
        private static readonly (string key, string label, bool isColor)[] TokenRows =
        {
            ("Accent", "Акцент яркий", true), ("AccentDim", "Акцент", true),
            ("AccentDark", "Акцент тёмный", true), ("AccentGlass", "Акцент стекло", true),
            ("Bg", "Фон", true), ("Panel", "Панель", true), ("Row", "Строка", true),
            ("Hover", "Наведение", true), ("Text", "Текст", true), ("Muted", "Приглушённый текст", true),
            ("White", "Белый текст", true), ("Green", "Зелёный", true), ("Red", "Красный", true),
            ("Blue", "Синий", true), ("BarBg", "Фон шкалы", true),
            ("Font", "Шрифт", false), ("FontHand", "Шрифт рукописный", false),
            ("Mat", "Материал панелей", false), ("MatBtn", "Материал кнопок", false)
        };

        private string ThemeField(string key)
        {
            var t = _cfg.Theme;
            switch (key)
            {
                case "Accent": return t.Accent; case "AccentDim": return t.AccentDim;
                case "AccentDark": return t.AccentDark; case "AccentGlass": return t.AccentGlass;
                case "Bg": return t.Bg; case "Panel": return t.Panel; case "Row": return t.Row;
                case "Hover": return t.Hover; case "Text": return t.Text; case "Muted": return t.Muted;
                case "White": return t.White; case "Green": return t.Green; case "Red": return t.Red;
                case "Blue": return t.Blue; case "BarBg": return t.BarBg; case "Font": return t.Font;
                case "FontHand": return t.FontHand; case "Mat": return t.Mat; case "MatBtn": return t.MatBtn;
                default: return "";
            }
        }

        private void SetThemeField(string key, string value)
        {
            var t = _cfg.Theme;
            switch (key)
            {
                case "Accent": t.Accent = value; break; case "AccentDim": t.AccentDim = value; break;
                case "AccentDark": t.AccentDark = value; break; case "AccentGlass": t.AccentGlass = value; break;
                case "Bg": t.Bg = value; break; case "Panel": t.Panel = value; break;
                case "Row": t.Row = value; break; case "Hover": t.Hover = value; break;
                case "Text": t.Text = value; break; case "Muted": t.Muted = value; break;
                case "White": t.White = value; break; case "Green": t.Green = value; break;
                case "Red": t.Red = value; break; case "Blue": t.Blue = value; break;
                case "BarBg": t.BarBg = value; break; case "Font": t.Font = value; break;
                case "FontHand": t.FontHand = value; break; case "Mat": t.Mat = value; break;
                case "MatBtn": t.MatBtn = value; break;
            }
        }

        private void DrawThemeEditor(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, AdminUi);
            var c = new CuiElementContainer();
            var fmt = CultureInfo.InvariantCulture;

            var names = (JObject)API_Root(c, new JObject
            {
                ["name"] = AdminUi, ["rect"] = "0.20 0.06 0.80 0.94",
                ["title"] = "◆ OCTAUI — РЕДАКТОР ТЕМЫ", ["subtitle"] = "Значения — «r g b a» (0..1) или #RRGGBBAA; шрифт/материал — путь",
                ["close"] = "octaui close"
            });
            var cnt = (string)names["content"];

            // правая колонка — живой превью-композит
            API_Section(c, cnt, null, new JObject { ["rect"] = "0.70 0.94 0.99 0.985", ["text"] = "ПРЕВЬЮ" });
            var card = (string)API_Card(c, cnt, "PRV_card", new JObject
            {
                ["rect"] = "0.70 0.80 0.99 0.93", ["selected"] = true, ["strip"] = "accent"
            });
            API_Text(c, card, null, new JObject
            {
                ["rect"] = "0.08 0.55 0.95 0.95", ["text"] = "Карточка-превью", ["size"] = 12, ["align"] = "left"
            });
            API_Text(c, card, null, new JObject
            {
                ["rect"] = "0.08 0.08 0.95 0.50", ["text"] = "текст приглушённый", ["size"] = 9,
                ["align"] = "left", ["color"] = "muted"
            });
            API_Bar(c, cnt, null, new JObject { ["rect"] = "0.70 0.76 0.99 0.78", ["value"] = 0.62f });
            API_ActionBar(c, cnt, null, new JObject
            {
                ["rect"] = "0.70 0.68 0.99 0.74",
                ["buttons"] = new JArray
                {
                    new JObject { ["text"] = "ОК", ["cmd"] = "", ["color"] = "green" },
                    new JObject { ["text"] = "ОТМЕНА", ["cmd"] = "", ["color"] = "hover" },
                    new JObject { ["text"] = "ОПАСНО", ["cmd"] = "", ["color"] = "red" }
                }
            });

            // левая колонка — токены в две колонки рядов
            API_Section(c, cnt, null, new JObject { ["rect"] = "0.01 0.94 0.30 0.985", ["text"] = "ТОКЕНЫ ТЕМЫ" });
            var rows = TokenRows;
            var half = (rows.Length + 1) / 2;
            var rowH = 0.052f; var gap = 0.004f; var top = 0.93f;
            for (var i = 0; i < rows.Length; i++)
            {
                var col = i < half ? 0 : 1;
                var ri = i < half ? i : i - half;
                var x0 = col == 0 ? 0.01f : 0.355f;
                var w = 0.335f;
                var y1 = top - ri * (rowH + gap);
                var y0 = y1 - rowH;
                var (key, label, isColor) = rows[i];
                var rowName = $"TK_{key}";
                API_Panel(c, cnt, rowName, new JObject
                {
                    ["rect"] = string.Format(fmt, "{0:0.###} {1:0.###} {2:0.###} {3:0.###}", x0, y0, x0 + w, y1),
                    ["color"] = "row"
                });
                if (isColor)
                {
                    API_Panel(c, rowName, rowName + "_sw", new JObject
                    {
                        ["rect"] = "0.02 0.18 0.10 0.82", ["color"] = ThemeField(key)
                    });
                }
                API_Text(c, rowName, null, new JObject
                {
                    ["rect"] = "0.12 0.55 0.98 0.98", ["text"] = label, ["size"] = 9,
                    ["align"] = "left", ["color"] = isColor ? "text" : "muted"
                });
                var v = rowName + "_v";
                c.Add(new CuiElement
                {
                    Parent = rowName,
                    Name = v,
                    Components =
                    {
                        new CuiImageComponent { Color = _cfg.Theme.Bg },
                        new CuiRectTransformComponent { AnchorMin = "0.12 0.08", AnchorMax = "0.98 0.50" }
                    }
                });
                API_Input(c, v, null, new JObject
                {
                    ["rect"] = "0.02 0 0.98 1", ["text"] = ThemeField(key), ["size"] = 9,
                    ["align"] = "left", ["cmd"] = "octaui set " + key + " ", ["limit"] = 120, ["font"] = "font"
                });
            }

            // низ: сохранить / сбросить
            API_ActionBar(c, cnt, "TK_bar", new JObject
            {
                ["rect"] = "0.01 0.0 0.69 0.055",
                ["buttons"] = new JArray
                {
                    new JObject { ["text"] = "СОХРАНИТЬ ТЕМУ", ["cmd"] = "octaui save", ["color"] = "green" },
                    new JObject { ["text"] = "СБРОСИТЬ К ДЕФОЛТУ", ["cmd"] = "octaui reset", ["color"] = "red" },
                    new JObject { ["text"] = "ПРИМЕНИТЬ", ["cmd"] = "octaui apply", ["color"] = "accent" },
                    new JObject { ["text"] = "ЗАКРЫТЬ", ["cmd"] = "octaui close", ["color"] = "hover" }
                }
            });

            CuiHelper.AddUi(player, c);
        }

        [ConsoleCommand("octaui")]
        private void CmdOctaUiConsole(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !CanEdit(player)) return;
            var args = arg.Args;
            if (args == null || args.Length == 0) return;

            switch (args[0].ToString().ToLowerInvariant())
            {
                case "close":
                    CuiHelper.DestroyUi(player, AdminUi);
                    return;
                case "set":
                    if (args.Length < 3) return;
                    var key = args[1].ToString();
                    var val = string.Join(" ", args.Skip(2).Select(x => x.ToString()));
                    var resolved = ResolveColor(val, val);
                    SetThemeField(key, resolved);
                    DrawThemeEditor(player); // перерисовка с новым значением
                    return;
                case "save":
                    SaveConfig();
                    API_Toast(player, AdminUi, "Тема сохранена в configs/OctaUI.json", "green");
                    return;
                case "apply":
                    DrawThemeEditor(player);
                    API_Toast(player, AdminUi, "Изменения применены к экрану (не забудь СОХРАНИТЬ)", "accent");
                    return;
                case "reset":
                    _cfg.Theme = new ThemeCfg();
                    SaveConfig();
                    DrawThemeEditor(player);
                    API_Toast(player, AdminUi, "Тема сброшена к значениям по умолчанию", "red");
                    return;
            }
        }

        private void Unload()
        {
            if (Interface.Oxide.IsShuttingDown) return;
            foreach (var p in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(p, AdminUi);
        }

        #endregion
    }
}
