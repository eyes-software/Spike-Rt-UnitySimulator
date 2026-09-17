using System;
using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// コース定義を画像に焼く。表示用テクスチャの生成にだけ使う。
    ///
    /// 1 画素ごとに CourseField へ「ここは何色か」を尋ねる作り方だと、
    /// コストが「画素数 x その位置の候補数」になる。公式コースは面が 7600 個あり、
    /// 800 万画素で 10 秒近くかかっていた。
    ///
    /// ここでは逆に、要素を描画順に 1 つずつ塗っていく。コストは
    /// 「塗る面積の合計」になり、画素数に対してほぼ線形で収まる。
    ///
    /// カラーセンサはこの画像ではなく CourseField の幾何を直接見るので、
    /// ここでの近似（曲線の折れ線化、継ぎ目の処理）はセンサ値に影響しない。
    /// </summary>
    public static class CourseRasterizer
    {
        /// <summary>
        /// 継ぎ目を円で丸める角度のしきい値 [deg]。
        /// これ未満の折れは三角形（ベベル）で埋める。
        /// </summary>
        const float RoundJointThresholdDeg = 20f;

        /// <summary>
        /// 走査線の塗り範囲を広げる量 [画素]。隣り合う図形の継ぎ目に
        /// 1 画素の塗り残しが出るのを防ぐ（FillPolygon の説明を参照）。
        /// 丸め誤差より十分大きく、見た目に影響しない程度に小さい値。
        /// </summary>
        const float EdgeEpsilon = 1e-3f;

        public static Texture2D Bake(CourseDefinition def, CourseField field, float pixelsPerMm)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(def.SheetWidthMm * pixelsPerMm), 16, 16384);
            int h = Mathf.Clamp(Mathf.RoundToInt(def.SheetHeightMm * pixelsPerMm), 16, 16384);

            float sx = w / def.SheetWidthMm;
            float sy = h / def.SheetHeightMm;

            var pixels = new Color32[w * h];
            var sheet = def.SheetColor;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = sheet;

            // areas と paths を z の昇順に混ぜて、後のものほど上に塗る
            var order = new List<(int z, int seq, bool isPath, int index)>(
                def.Areas.Count + def.Paths.Count);
            for (int i = 0; i < def.Areas.Count; i++) order.Add((def.Areas[i].Z, i, false, i));
            for (int i = 0; i < def.Paths.Count; i++) order.Add((def.Paths[i].Z, i, true, i));
            order.Sort((a, b) => a.z != b.z ? a.z.CompareTo(b.z) : a.seq.CompareTo(b.seq));

            var scratch = new List<Vector2>(64);
            var crossings = new List<Vector2>(32);   // x = 交点, y = 辺の向き (+1/-1)

            foreach (var item in order)
            {
                if (item.isPath)
                {
                    var path = def.Paths[item.index];
                    var pts = field.PathPolylines[item.index];
                    FillThickPolyline(pixels, w, h, sx, sy, pts, path.WidthMm * 0.5f,
                                      path.Color, scratch, crossings);
                }
                else
                {
                    FillArea(pixels, w, h, sx, sy, def.Areas[item.index], scratch, crossings);
                }
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true)
            {
                name = "CourseSheet",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 8
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, false);
            return tex;
        }

        // ---- 面 ----------------------------------------------------------

        static void FillArea(Color32[] px, int w, int h, float sx, float sy,
                             CourseArea area, List<Vector2> scratch, List<Vector2> crossings)
        {
            scratch.Clear();

            switch (area.Shape)
            {
                case AreaShape.Rect:
                {
                    Vector2 half = area.Size * 0.5f;
                    float rad = area.RotationDeg * Mathf.Deg2Rad;
                    float c = Mathf.Cos(rad), s = Mathf.Sin(rad);

                    // ローカルの 4 隅を回してから画素座標へ
                    AddRotated(scratch, area.Center, -half.x, -half.y, c, s, sx, sy);
                    AddRotated(scratch, area.Center, half.x, -half.y, c, s, sx, sy);
                    AddRotated(scratch, area.Center, half.x, half.y, c, s, sx, sy);
                    AddRotated(scratch, area.Center, -half.x, half.y, c, s, sx, sy);
                    break;
                }
                case AreaShape.Circle:
                    AppendCircle(scratch, area.Center, area.Radius, sx, sy);
                    break;
                default:
                {
                    // 輪郭が複数あるときは 1 回の走査でまとめて塗る。
                    // 輪郭ごとに塗ると、穴（文字の内側）まで埋まってしまう。
                    if (area.Contours.Count == 0) return;
                    if (area.Contours.Count == 1)
                    {
                        if (area.Contours[0].Count < 3) return;
                        foreach (var p in area.Contours[0]) scratch.Add(ToPixel(p, sx, sy));
                        break;
                    }

                    var contours = new List<List<Vector2>>(area.Contours.Count);
                    foreach (var contour in area.Contours)
                    {
                        if (contour.Count < 3) continue;
                        var pixelContour = new List<Vector2>(contour.Count);
                        foreach (var p in contour) pixelContour.Add(ToPixel(p, sx, sy));
                        contours.Add(pixelContour);
                    }
                    FillContours(px, w, h, contours, area.Color, crossings, area.FillNonZero);
                    return;
                }
            }

            FillPolygon(px, w, h, scratch, area.Color, crossings);
        }

        static void AddRotated(List<Vector2> into, Vector2 center, float lx, float ly,
                               float cos, float sin, float sx, float sy)
        {
            // CourseField の rect 判定と同じ向きに回す
            var p = new Vector2(center.x + lx * cos - ly * sin,
                                center.y + lx * sin + ly * cos);
            into.Add(ToPixel(p, sx, sy));
        }

        static void AppendCircle(List<Vector2> into, Vector2 center, float radiusMm,
                                 float sx, float sy)
        {
            float radiusPx = radiusMm * Mathf.Max(sx, sy);
            int steps = Mathf.Clamp(Mathf.CeilToInt(radiusPx * 2f), 8, 64);
            for (int i = 0; i < steps; i++)
            {
                float a = i * 2f * Mathf.PI / steps;
                into.Add(ToPixel(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radiusMm, sx, sy));
            }
        }

        // ---- 太い折れ線 ---------------------------------------------------

        static void FillThickPolyline(Color32[] px, int w, int h, float sx, float sy,
                                      List<Vector2> pts, float halfWidthMm, Color32 color,
                                      List<Vector2> scratch, List<Vector2> crossings)
        {
            if (pts == null || pts.Count < 2) return;

            // 隣り合う図形が「辺をぴったり共有する」状態をなくすための伸ばし量 [mm]。
            //
            // 辺を共有すると、その辺が画素中心の境界に乗ったときに
            // どちらの図形もその画素を自分の外と判定し、1 画素幅の塗り残しが出る。
            // 四角形を進行方向へわずかに伸ばして重ねれば、共有辺自体が無くなる。
            //
            // 伸ばすのは進行方向だけなので帯の幅は変わらない。端も、もともと
            // 半幅ぶんの丸い端で覆われている内側なので外形は変わらない。
            float overlapMm = 1.5f / Mathf.Max(sx, sy);

            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 a = pts[i - 1], b = pts[i];
                Vector2 d = b - a;
                float len = d.magnitude;
                if (len < 1e-6f) continue;

                Vector2 dir = d / len;
                Vector2 n = new Vector2(-dir.y, dir.x) * halfWidthMm;

                Vector2 a2 = a - dir * overlapMm;
                Vector2 b2 = b + dir * overlapMm;

                scratch.Clear();
                scratch.Add(ToPixel(a2 + n, sx, sy));
                scratch.Add(ToPixel(b2 + n, sx, sy));
                scratch.Add(ToPixel(b2 - n, sx, sy));
                scratch.Add(ToPixel(a2 - n, sx, sy));
                FillPolygon(px, w, h, scratch, color, crossings);

                // 継ぎ目の処理。
                //
                // 四角形だけを並べると、曲がりの外側に楔形の隙間が残る。
                // 幅 320mm の白帯だと 1 度の折れでも約 3mm 空き、
                // 折れ線化した曲線に沿って扇状の筋になって見える。
                //
                // 小さい折れは三角形（ベベル）で塞ぐ。これは面積が小さく、
                // 頂点ごとに円を塗るより桁違いに安い。
                // 大きく折れるところだけ、丸い継ぎ目にするため円を足す
                // （CourseField の距離判定は丸い継ぎ目なので、そちらに合わせる）。
                if (i + 1 < pts.Count)
                {
                    Vector2 next = pts[i + 1] - b;
                    if (next.sqrMagnitude > 1e-12f)
                    {
                        float turn = Vector2.Angle(d, next);
                        if (turn > RoundJointThresholdDeg)
                        {
                            scratch.Clear();
                            AppendCircle(scratch, b, halfWidthMm, sx, sy);
                            FillPolygon(px, w, h, scratch, color, crossings);
                        }
                        else if (turn > 0.01f)
                        {
                            // 三角形も四角形と重なるよう、少しだけ外へ出す。
                            // 帯の幅より外へは出ないよう、半幅は超えない。
                            float reach = Mathf.Min(halfWidthMm + overlapMm, halfWidthMm * 1.02f);
                            Vector2 n1r = n / halfWidthMm * reach;
                            Vector2 n2r = new Vector2(-next.y, next.x) / next.magnitude * reach;

                            // 外側がどちらかを調べずに両側とも塞ぐ。
                            // 内側の三角形は四角形の内部に収まるので害はない。
                            FillTriangle(px, w, h, sx, sy, b, b + n1r, b + n2r, color, scratch, crossings);
                            FillTriangle(px, w, h, sx, sy, b, b - n1r, b - n2r, color, scratch, crossings);
                        }
                    }
                }
            }

            // 端は丸める。CourseField の距離判定（丸い端）に合わせる。
            scratch.Clear();
            AppendCircle(scratch, pts[0], halfWidthMm, sx, sy);
            FillPolygon(px, w, h, scratch, color, crossings);

            scratch.Clear();
            AppendCircle(scratch, pts[pts.Count - 1], halfWidthMm, sx, sy);
            FillPolygon(px, w, h, scratch, color, crossings);
        }

        static void FillTriangle(Color32[] px, int w, int h, float sx, float sy,
                                 Vector2 a, Vector2 b, Vector2 c, Color32 color,
                                 List<Vector2> scratch, List<Vector2> crossings)
        {
            scratch.Clear();
            scratch.Add(ToPixel(a, sx, sy));
            scratch.Add(ToPixel(b, sx, sy));
            scratch.Add(ToPixel(c, sx, sy));
            FillPolygon(px, w, h, scratch, color, crossings);
        }

        // ---- 多角形の塗り --------------------------------------------------

        static Vector2 ToPixel(Vector2 mm, float sx, float sy) => new Vector2(mm.x * sx, mm.y * sy);

        static readonly Comparison<Vector2> CompareCrossings = (a, b) => a.x.CompareTo(b.x);

        static readonly List<List<Vector2>> _singleContour = new List<List<Vector2>>(1) { null };

        /// <summary>
        /// 走査線で多角形を塗る。偶奇規則なので凹多角形でも正しく塗れる。
        /// 座標は画素単位、Y は下から上（配列の行 y が下から数えた行）。
        /// </summary>
        static void FillPolygon(Color32[] px, int w, int h, List<Vector2> poly,
                                Color32 color, List<Vector2> crossings)
        {
            if (poly.Count < 3) return;
            _singleContour[0] = poly;
            FillContours(px, w, h, _singleContour, color, crossings, false);
        }

        /// <summary>
        /// 複数の輪郭をまとめて塗る。内側の輪郭は穴になる。
        /// 文字やロゴのように穴を持つ図形は、輪郭ごとに塗ると穴が埋まる。
        ///
        /// <paramref name="nonZero"/> が真なら非ゼロ回転数規則。輪郭の向きが
        /// 逆のところだけが穴になるので、同じ向きの輪郭が重なっても穴があかない。
        /// 偽なら偶奇規則で、重なりが必ず穴になる。PDF の塗り規則をそのまま使う。
        /// </summary>
        static void FillContours(Color32[] px, int w, int h, List<List<Vector2>> contours,
                                 Color32 color, List<Vector2> crossings, bool nonZero)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var poly in contours)
            {
                if (poly == null) continue;
                for (int i = 0; i < poly.Count; i++)
                {
                    if (poly[i].y < minY) minY = poly[i].y;
                    if (poly[i].y > maxY) maxY = poly[i].y;
                }
            }
            if (minY > maxY) return;

            int y0 = Mathf.Max(0, Mathf.FloorToInt(minY));
            int y1 = Mathf.Min(h - 1, Mathf.CeilToInt(maxY));

            for (int y = y0; y <= y1; y++)
            {
                float scan = y + 0.5f;

                crossings.Clear();
                foreach (var poly in contours)
                {
                    if (poly == null) continue;
                    int n = poly.Count;
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        Vector2 a = poly[j], b = poly[i];
                        // 上向き・下向きを片側だけ含めて、頂点でのニ重計上を避ける
                        if ((a.y > scan) == (b.y > scan)) continue;
                        float t = (scan - a.y) / (b.y - a.y);
                        crossings.Add(new Vector2(a.x + t * (b.x - a.x), b.y > a.y ? 1f : -1f));
                    }
                }
                if (crossings.Count < 2) continue;

                crossings.Sort(CompareCrossings);

                int row = y * w;
                int winding = 0;
                for (int k = 0; k + 1 < crossings.Count; k++)
                {
                    winding += (int)crossings[k].y;

                    // 偶奇規則なら偶数番目から奇数番目まで、
                    // 非ゼロ規則なら回転数が 0 でない区間を塗る。
                    bool insideSpan = nonZero ? winding != 0 : (k % 2) == 0;
                    if (!insideSpan) continue;

                    // 両端を EdgeEpsilon だけ広げてから画素に落とす。
                    //
                    // 隣り合う図形が共有する辺が、ちょうど画素中心の境界
                    // （交点の x が n + 0.5）に乗ると、浮動小数の丸めの向きが
                    // 図形ごとに変わって、どちらもその画素を自分の外と判定し、
                    // 1 画素幅の塗り残しが出る。帯が水平な区間では共有辺が
                    // 垂直になるため、帯を縦に貫く線として見えてしまう。
                    //
                    // 広げるぶんは重なるだけで、重なりは同じ色なので害はない。
                    int xs = Mathf.Max(0, Mathf.CeilToInt(crossings[k].x - 0.5f - EdgeEpsilon));
                    int xe = Mathf.Min(w - 1, Mathf.FloorToInt(crossings[k + 1].x - 0.5f + EdgeEpsilon));
                    for (int x = xs; x <= xe; x++) px[row + x] = color;
                }
            }
        }
    }
}
