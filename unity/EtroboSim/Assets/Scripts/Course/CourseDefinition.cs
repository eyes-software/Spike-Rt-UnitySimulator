using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Etrobo.Sim
{
    public enum SegmentType { Line, Arc, Cubic, Quad }
    public enum AreaShape { Rect, Circle, Polygon }
    public enum GateKind { Start, Checkpoint, Goal }

    public sealed class PathSegment
    {
        public SegmentType Type;
        public Vector2 To;      // 終点 [mm]
        public Vector2 C1, C2;  // ベジエ制御点 [mm]
        public float Radius;    // 円弧半径 [mm]
        public bool Sweep;      // SVG と同じ。true = 角度が増える向き（この座標系では反時計回り）
        public bool LargeArc;   // SVG と同じ。true = 優弧
    }

    public sealed class CoursePath
    {
        public string Id = "path";
        /// <summary>
        /// 描画順。大きいほど手前。int.MinValue は「未指定」を表し、
        /// CourseDefinition.FromJson が既定値（areas の後ろ）を割り当てる。
        /// </summary>
        public int Z = int.MinValue;
        public Color32 Color = new Color32(16, 16, 16, 255);
        public float WidthMm = 20f;
        public bool Closed;
        public Vector2 Start;
        public List<PathSegment> Segments = new List<PathSegment>();
    }

    public sealed class CourseArea
    {
        public string Id = "area";
        /// <summary>
        /// 描画順。大きいほど手前。int.MinValue は「未指定」を表し、
        /// CourseDefinition.FromJson が宣言順を割り当てる。
        /// </summary>
        public int Z = int.MinValue;
        public AreaShape Shape = AreaShape.Rect;
        public Color32 Color = new Color32(154, 154, 154, 255);
        public Vector2 Center;
        public Vector2 Size;        // rect のみ [mm]
        public float RotationDeg;   // rect のみ
        public float Radius;        // circle のみ [mm]
        /// <summary>
        /// polygon のときの輪郭。複数あると偶奇規則で穴になる。
        /// 文字やロゴは外側の輪郭と内側の輪郭でできているので、
        /// 別々の塗りとして扱うと穴が埋まってしまう。
        /// </summary>
        public List<List<Vector2>> Contours = new List<List<Vector2>>();

        /// <summary>最初の輪郭。単一輪郭しか無い場合の短縮。</summary>
        public List<Vector2> Points => Contours.Count > 0 ? Contours[0] : _empty;

        /// <summary>
        /// 輪郭が重なったときの塗り規則。真なら非ゼロ回転数規則（PDF の既定）で、
        /// 向きが逆の輪郭だけが穴になる。偽なら偶奇規則で、重なりは必ず穴になる。
        /// </summary>
        public bool FillNonZero = true;

        static readonly List<Vector2> _empty = new List<Vector2>();
    }

    public sealed class CourseProp
    {
        public string Id = "prop";
        public string Type = "block"; // block / cylinder / wall / ramp / seesaw / gate
        public Color32 Color = new Color32(180, 180, 180, 255);
        public Vector2 Position;      // [mm]
        public float HeadingDeg;
        public Vector3 Size = new Vector3(55, 55, 55); // 幅X・奥行Y・高さZ [mm]
        public float Mass = 0.05f;    // [kg]
        public bool Movable;
        public float PivotHeightMm = 30f; // seesaw のみ

        /// <summary>
        /// 床から重心までの高さ [mm]。0 以下なら形状の中心（一様密度）。
        /// 水を入れたペットボトルは重心が低く、押しても倒れにくい。
        /// ここを入れないと、倒れやすさが実物とまるで変わる。
        /// </summary>
        public float CenterOfMassHeightMm;

        /// <summary>bottle のラベル。床からの高さ [mm] と帯の幅 [mm]、色。</summary>
        public float LabelBottomMm;
        public float LabelHeightMm;
        public Color32 LabelColor = new Color32(32, 32, 32, 255);

        /// <summary>gate のみ。管の外径 [mm]。</summary>
        public float PipeDiameterMm = 18f;
    }

    public sealed class CourseGate
    {
        public string Id = "gate";
        public Vector2 From, To;
        public GateKind Kind = GateKind.Checkpoint;

        /// <summary>
        /// 成立する通過の向き。規約の図に描かれている矢印にあたる
        /// （5.9 スタート / 5.12 チェックポイント到達 / 5.13 LAP ゲート到達）。
        /// ゼロのときは向きを問わない。
        ///
        /// 公式コースには入れてある。手書きのコースで入れ忘れたときに
        /// 黙って厳しくなるより、向き不問のままのほうが安全なので既定は
        /// ゼロにしている。
        /// </summary>
        public Vector2 Direction = Vector2.zero;
    }

    /// <summary>
    /// コース定義 JSON をそのまま表すデータ。単位はすべて mm、
    /// 原点はシート左下、X が右、Y が奥。
    /// </summary>
    public sealed class CourseDefinition
    {
        public string Name = "course";
        public float SheetWidthMm = 4000f;
        public float SheetHeightMm = 2000f;
        public Color32 SheetColor = new Color32(242, 242, 240, 255);

        public List<CoursePath> Paths = new List<CoursePath>();
        public List<CourseArea> Areas = new List<CourseArea>();
        public List<CourseProp> Props = new List<CourseProp>();
        public List<CourseGate> Gates = new List<CourseGate>();

        public Vector2 StartPositionMm = new Vector2(700f, 300f);
        public float StartHeadingDeg;

        public static CourseDefinition FromJson(string json)
        {
            var root = JsonValue.Parse(json);
            var c = new CourseDefinition();

            c.Name = root["name"].AsString("course");

            string units = root["units"].AsString("mm");
            if (!string.Equals(units, "mm", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("コース定義: units は \"mm\" のみ対応しています (指定: " + units + ")");

            var sheet = root["sheet"];
            if (sheet.Kind == JsonKind.Object)
            {
                c.SheetWidthMm = sheet["width"].AsFloat(c.SheetWidthMm);
                c.SheetHeightMm = sheet["height"].AsFloat(c.SheetHeightMm);
                c.SheetColor = ParseColor(sheet["color"].AsString(null), c.SheetColor);
            }

            foreach (var p in root["paths"].Items()) c.Paths.Add(ParsePath(p));
            foreach (var a in root["areas"].Items()) c.Areas.Add(ParseArea(a));

            // z が無いコース定義は、従来どおり「areas -> paths」の順で描く。
            // 1000 の下駄を履かせているだけなので、片方にだけ z を書いても破綻しない。
            AssignDefaultDrawOrder(c);
            foreach (var o in root["props"].Items()) c.Props.Add(ParseProp(o));
            foreach (var g in root["gates"].Items()) c.Gates.Add(ParseGate(g));

            var sp = root["start_pose"];
            if (sp.Kind == JsonKind.Object)
            {
                c.StartPositionMm = ParseVec2(sp["position"], c.StartPositionMm);
                c.StartHeadingDeg = sp["heading"].AsFloat(0f);
            }

            return c;
        }

        /// <summary>z を省略した要素に既定の描画順を割り当てる。</summary>
        static void AssignDefaultDrawOrder(CourseDefinition c)
        {
            for (int i = 0; i < c.Areas.Count; i++)
                if (c.Areas[i].Z == int.MinValue) c.Areas[i].Z = i;

            for (int i = 0; i < c.Paths.Count; i++)
                if (c.Paths[i].Z == int.MinValue) c.Paths[i].Z = 1000 + i;
        }

        static CoursePath ParsePath(JsonValue v)
        {
            var path = new CoursePath
            {
                Id = v["id"].AsString("path"),
                Color = ParseColor(v["color"].AsString(null), new Color32(16, 16, 16, 255)),
                WidthMm = v["width"].AsFloat(20f),
                Closed = v["closed"].AsBool(false),
                Z = v.Has("z") ? v["z"].AsInt() : int.MinValue,
                Start = ParseVec2(v["start"], Vector2.zero)
            };

            foreach (var s in v["segments"].Items())
            {
                string type = (s["type"].AsString("line") ?? "line").ToLowerInvariant();
                var seg = new PathSegment { To = ParseVec2(s["to"], Vector2.zero) };

                switch (type)
                {
                    case "line":
                        seg.Type = SegmentType.Line;
                        break;
                    case "arc":
                        seg.Type = SegmentType.Arc;
                        seg.Radius = s["r"].AsFloat(s["radius"].AsFloat(0f));
                        seg.Sweep = s["sweep"].AsBool(true);
                        seg.LargeArc = s["large"].AsBool(false);
                        if (seg.Radius <= 0f)
                            throw new FormatException("コース定義: arc セグメントに正の r が必要です (path " + path.Id + ")");
                        break;
                    case "cubic":
                        seg.Type = SegmentType.Cubic;
                        seg.C1 = ParseVec2(s["c1"], Vector2.zero);
                        seg.C2 = ParseVec2(s["c2"], Vector2.zero);
                        break;
                    case "quad":
                        seg.Type = SegmentType.Quad;
                        seg.C1 = ParseVec2(s["c"], ParseVec2(s["c1"], Vector2.zero));
                        break;
                    default:
                        throw new FormatException("コース定義: 未知のセグメント種別 '" + type + "' (path " + path.Id + ")");
                }
                path.Segments.Add(seg);
            }
            return path;
        }

        static CourseArea ParseArea(JsonValue v)
        {
            var a = new CourseArea
            {
                Id = v["id"].AsString("area"),
                Color = ParseColor(v["color"].AsString(null), new Color32(154, 154, 154, 255)),
                Center = ParseVec2(v["center"], Vector2.zero),
                RotationDeg = v["rotation"].AsFloat(0f),
                Radius = v["radius"].AsFloat(0f),
                Z = v.Has("z") ? v["z"].AsInt() : int.MinValue
            };

            string shape = (v["shape"].AsString("rect") ?? "rect").ToLowerInvariant();
            switch (shape)
            {
                case "rect":
                    a.Shape = AreaShape.Rect;
                    a.Size = ParseVec2(v["size"], new Vector2(100f, 100f));
                    break;
                case "circle":
                    a.Shape = AreaShape.Circle;
                    break;
                case "polygon":
                {
                    a.Shape = AreaShape.Polygon;
                    a.FillNonZero = (v["fill_rule"].AsString("nonzero") != "evenodd");

                    // 複数輪郭 (穴あき) と、従来の単一輪郭の両方を受ける
                    if (v.Has("contours"))
                    {
                        foreach (var contour in v["contours"].Items())
                        {
                            var pts = new List<Vector2>();
                            foreach (var pt in contour.Items()) pts.Add(ParseVec2(pt, Vector2.zero));
                            if (pts.Count >= 3) a.Contours.Add(pts);
                        }
                    }
                    else
                    {
                        var pts = new List<Vector2>();
                        foreach (var pt in v["points"].Items()) pts.Add(ParseVec2(pt, Vector2.zero));
                        if (pts.Count >= 3) a.Contours.Add(pts);
                    }
                    break;
                }
                default:
                    throw new FormatException("コース定義: 未知の area shape '" + shape + "'");
            }
            return a;
        }

        static CourseProp ParseProp(JsonValue v)
        {
            var p = new CourseProp
            {
                Id = v["id"].AsString("prop"),
                Type = (v["type"].AsString("block") ?? "block").ToLowerInvariant(),
                Color = ParseColor(v["color"].AsString(null), new Color32(180, 180, 180, 255)),
                Position = ParseVec2(v["position"], Vector2.zero),
                HeadingDeg = v["heading"].AsFloat(0f),
                Mass = v["mass"].AsFloat(0.05f),
                Movable = v["movable"].AsBool(false),
                PivotHeightMm = v["pivot_height"].AsFloat(30f),
                CenterOfMassHeightMm = v["center_of_mass_height_mm"].AsFloat(0f),
                LabelBottomMm = v["label_bottom_mm"].AsFloat(0f),
                LabelHeightMm = v["label_height_mm"].AsFloat(0f),
                PipeDiameterMm = v["pipe_diameter_mm"].AsFloat(18f)
            };
            if (v.Has("label_color"))
                p.LabelColor = ParseColor(v["label_color"].AsString(null), p.LabelColor);

            var size = v["size"];
            if (size.Count >= 3)
                p.Size = new Vector3(size[0].AsFloat(), size[1].AsFloat(), size[2].AsFloat());
            else if (size.Count == 2)
                p.Size = new Vector3(size[0].AsFloat(), size[1].AsFloat(), size[1].AsFloat());

            return p;
        }

        static CourseGate ParseGate(JsonValue v)
        {
            var g = new CourseGate
            {
                Id = v["id"].AsString("gate"),
                From = ParseVec2(v["from"], Vector2.zero),
                To = ParseVec2(v["to"], Vector2.zero),
                Direction = ParseVec2(v["dir"], Vector2.zero)
            };

            switch ((v["kind"].AsString("checkpoint") ?? "checkpoint").ToLowerInvariant())
            {
                case "start": g.Kind = GateKind.Start; break;
                case "goal": g.Kind = GateKind.Goal; break;
                default: g.Kind = GateKind.Checkpoint; break;
            }
            return g;
        }

        static Vector2 ParseVec2(JsonValue v, Vector2 fallback)
        {
            if (v.Count < 2) return fallback;
            return new Vector2(v[0].AsFloat(), v[1].AsFloat());
        }

        public static Color32 ParseColor(string hex, Color32 fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            string s = hex.TrimStart('#');
            if (s.Length != 6 && s.Length != 8) return fallback;

            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                return fallback;

            if (s.Length == 6)
                return new Color32((byte)(value >> 16), (byte)(value >> 8), (byte)value, 255);
            return new Color32((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }
    }
}
