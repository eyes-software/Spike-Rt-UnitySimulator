using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// ガレージ（競技規約 5.2.4）。フィニッシュ（5.14）の判定に使う。
    ///
    /// 規約は「ガレージ手前の黒線から、灰色に囲まれた白色の領域」。
    /// その灰色の枠はコース図に印刷されているので、座標を手で書かずに読み取る。
    /// 枠は上が開いた コ の字（黒線が入ってくる側が開いている）で、
    /// 幅 1.8mm の灰色の線 3 本でできている。
    ///
    /// 図面の線幅は 0.9mm（縮尺 1/2）なので、既定の --line-min-width 1.0 だと
    /// 落ちる。コースを作り直すときは 0.5 を指定すること（README 参照）。
    /// </summary>
    public sealed class GarageArea
    {
        /// <summary>ガレージの範囲 [mm]。</summary>
        public Vector2 CenterMm { get; private set; }
        public Vector2 SizeMm { get; private set; }

        public bool Found { get; private set; }

        /// <summary>灰色の枠線とみなす線幅の範囲 [mm]。土俵の縁（10mm）と区別する。</summary>
        const float FrameWidthMinMm = 1.0f;
        const float FrameWidthMaxMm = 3.0f;

        /// <summary>枠とみなす大きさの下限 [mm]。AR マーカの枠（80mm 角）を外す。</summary>
        const float MinSideMm = 150f;

        static readonly Color32 FrameColor = new Color32(0xA6, 0xA7, 0xA7, 255);

        /// <summary>走行体が出る側のガレージを読む。</summary>
        public static GarageArea FromCourse(CourseDefinition def, bool wantLeft)
        {
            var g = new GarageArea();
            if (def == null) return g;

            float midX = def.SheetWidthMm * 0.5f;

            foreach (var p in def.Paths)
            {
                if (p.WidthMm < FrameWidthMinMm || p.WidthMm > FrameWidthMaxMm) continue;
                if (!SameColor(p.Color, FrameColor)) continue;

                if (!TryBounds(p, out Vector2 min, out Vector2 max)) continue;
                if (max.x - min.x < MinSideMm || max.y - min.y < MinSideMm) continue;

                float cx = (min.x + max.x) * 0.5f;
                if ((cx < midX) != wantLeft) continue;

                g.CenterMm = new Vector2(cx, (min.y + max.y) * 0.5f);
                g.SizeMm = max - min;
                g.Found = true;
                return g;
            }
            return g;
        }

        /// <summary>点がガレージの中にあるか。</summary>
        public bool Contains(Vector2 pointMm)
        {
            if (!Found) return false;
            Vector2 d = pointMm - CenterMm;
            return Mathf.Abs(d.x) <= SizeMm.x * 0.5f && Mathf.Abs(d.y) <= SizeMm.y * 0.5f;
        }

        /// <summary>走行体の一部でもガレージに入っているか（規約 5.14 は「一部」）。</summary>
        public bool TouchedBy(Vector2[] footprintMm)
        {
            if (!Found || footprintMm == null) return false;

            for (int i = 0; i < footprintMm.Length; i++)
                if (Contains(footprintMm[i])) return true;

            // 四隅がどれも外でも、ガレージの辺をまたいで立っていれば「一部が中」。
            // 走行体の外形（傾いた四角形）とガレージ（傾いていない四角形）が重なるかを
            // 分離軸で見る。以前は外形に外接する傾いていない四角形で見ていたので、
            // 斜めを向いているとガレージから離れていても重なると判定した。
            return OverlapsRotated(footprintMm);
        }

        /// <summary>
        /// 四隅（前左・前右・後左・後右の順）で表した走行体の外形が、ガレージの四角形と重なるか。
        /// 分離軸定理：コースの x・y 軸と、外形の 2 辺の向きの計 4 軸のどれかで
        /// 影が離れていれば重ならない。
        /// </summary>
        bool OverlapsRotated(Vector2[] c)
        {
            Vector2 half = SizeMm * 0.5f;
            Vector2[] g =
            {
                CenterMm + new Vector2(-half.x, -half.y), CenterMm + new Vector2(half.x, -half.y),
                CenterMm + new Vector2(half.x, half.y),   CenterMm + new Vector2(-half.x, half.y),
            };
            Vector2[] axes = { Vector2.right, Vector2.up, (c[1] - c[0]).normalized, (c[2] - c[0]).normalized };
            foreach (var a in axes)
            {
                if (a.sqrMagnitude < 1e-6f) continue;
                float cMin = float.MaxValue, cMax = float.MinValue, gMin = float.MaxValue, gMax = float.MinValue;
                foreach (var p in c) { float d = Vector2.Dot(p, a); cMin = Mathf.Min(cMin, d); cMax = Mathf.Max(cMax, d); }
                foreach (var p in g) { float d = Vector2.Dot(p, a); gMin = Mathf.Min(gMin, d); gMax = Mathf.Max(gMax, d); }
                if (cMax < gMin || gMax < cMin) return false;
            }
            return true;
        }

        bool OverlapsRect(Vector2[] corners)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var c in corners)
            {
                minX = Mathf.Min(minX, c.x); maxX = Mathf.Max(maxX, c.x);
                minY = Mathf.Min(minY, c.y); maxY = Mathf.Max(maxY, c.y);
            }
            Vector2 half = SizeMm * 0.5f;
            return minX <= CenterMm.x + half.x && maxX >= CenterMm.x - half.x
                && minY <= CenterMm.y + half.y && maxY >= CenterMm.y - half.y;
        }

        static bool SameColor(Color32 a, Color32 b)
            => Mathf.Abs(a.r - b.r) <= 8 && Mathf.Abs(a.g - b.g) <= 8 && Mathf.Abs(a.b - b.b) <= 8;

        static bool TryBounds(CoursePath path, out Vector2 min, out Vector2 max)
        {
            min = new Vector2(float.MaxValue, float.MaxValue);
            max = new Vector2(float.MinValue, float.MinValue);

            int n = 0;
            Add(path.Start, ref min, ref max, ref n);
            foreach (var seg in path.Segments)
            {
                Add(seg.To, ref min, ref max, ref n);
                if (seg.Type == SegmentType.Cubic)
                {
                    Add(seg.C1, ref min, ref max, ref n);
                    Add(seg.C2, ref min, ref max, ref n);
                }
            }
            return n >= 3;
        }

        static void Add(Vector2 p, ref Vector2 min, ref Vector2 max, ref int n)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
            n++;
        }
    }
}
