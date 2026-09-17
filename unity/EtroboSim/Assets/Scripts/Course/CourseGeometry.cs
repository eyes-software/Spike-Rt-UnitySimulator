using System;
using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// コース定義のパスを折れ線に展開する。
    /// arc は SVG の楕円弧コマンド（rx = ry、x-axis-rotation = 0）と同じ
    /// endpoint パラメータ表現を使うので、SVG からの取り込みで形が変わらない。
    /// </summary>
    public static class CourseGeometry
    {
        /// <summary>折れ線化したときの最大弦誤差 [mm]。</summary>
        public const float DefaultToleranceMm = 0.3f;

        public static List<Vector2> Flatten(CoursePath path, float toleranceMm = DefaultToleranceMm)
        {
            var pts = new List<Vector2>(256);
            Vector2 cursor = path.Start;
            pts.Add(cursor);

            foreach (var seg in path.Segments)
            {
                switch (seg.Type)
                {
                    case SegmentType.Line:
                        pts.Add(seg.To);
                        break;
                    case SegmentType.Arc:
                        AppendArc(pts, cursor, seg.To, seg.Radius, seg.LargeArc, seg.Sweep, toleranceMm);
                        break;
                    case SegmentType.Cubic:
                        AppendCubic(pts, cursor, seg.C1, seg.C2, seg.To, toleranceMm);
                        break;
                    case SegmentType.Quad:
                        // 2 次を 3 次に昇格させて同じ経路で扱う
                        AppendCubic(pts, cursor,
                            cursor + (2f / 3f) * (seg.C1 - cursor),
                            seg.To + (2f / 3f) * (seg.C1 - seg.To),
                            seg.To, toleranceMm);
                        break;
                }
                cursor = seg.To;
            }

            if (path.Closed && pts.Count > 1 && (pts[pts.Count - 1] - pts[0]).sqrMagnitude > 1e-6f)
                pts.Add(pts[0]);

            RemoveDuplicates(pts);
            return pts;
        }

        static void RemoveDuplicates(List<Vector2> pts)
        {
            const float epsSq = 1e-8f;
            int w = 1;
            for (int r = 1; r < pts.Count; r++)
            {
                if ((pts[r] - pts[w - 1]).sqrMagnitude > epsSq) pts[w++] = pts[r];
            }
            if (w < pts.Count) pts.RemoveRange(w, pts.Count - w);
        }

        /// <summary>
        /// SVG の endpoint 表現の円弧を折れ線として追加する（始点 p0 は追加済みの前提）。
        /// この座標系は Y 軸が上向きなので、sweep = true は反時計回りになる。
        /// </summary>
        public static void AppendArc(List<Vector2> pts, Vector2 p0, Vector2 p1,
                                     float radius, bool largeArc, bool sweep, float toleranceMm)
        {
            if ((p1 - p0).sqrMagnitude < 1e-10f) return;

            float r = Mathf.Abs(radius);
            // 始点・終点の中点を原点とする座標へ移す
            float dx2 = (p0.x - p1.x) * 0.5f;
            float dy2 = (p0.y - p1.y) * 0.5f;

            float d2 = dx2 * dx2 + dy2 * dy2;
            if (d2 < 1e-12f) return;

            // 半径が小さすぎて両端を結べない場合は SVG の規定どおり最小半径まで広げる
            float lambda = d2 / (r * r);
            if (lambda > 1f) r *= Mathf.Sqrt(lambda);

            float num = r * r - d2;
            float factor = Mathf.Sqrt(Mathf.Max(0f, num / d2));
            if (largeArc == sweep) factor = -factor;

            float cxp = factor * dy2;
            float cyp = -factor * dx2;

            Vector2 center = new Vector2(cxp + (p0.x + p1.x) * 0.5f,
                                         cyp + (p0.y + p1.y) * 0.5f);

            float theta0 = Mathf.Atan2(p0.y - center.y, p0.x - center.x);
            float theta1 = Mathf.Atan2(p1.y - center.y, p1.x - center.x);
            float sweepAngle = theta1 - theta0;

            if (sweep && sweepAngle < 0f) sweepAngle += 2f * Mathf.PI;
            if (!sweep && sweepAngle > 0f) sweepAngle -= 2f * Mathf.PI;

            int steps = ArcStepCount(r, Mathf.Abs(sweepAngle), toleranceMm);
            for (int i = 1; i <= steps; i++)
            {
                float t = theta0 + sweepAngle * i / steps;
                pts.Add(new Vector2(center.x + r * Mathf.Cos(t), center.y + r * Mathf.Sin(t)));
            }
        }

        static int ArcStepCount(float radius, float sweepAngle, float toleranceMm)
        {
            // 弦誤差 tol となる分割角: 2*acos(1 - tol/r)
            float ratio = Mathf.Clamp(1f - toleranceMm / Mathf.Max(radius, 1e-3f), -1f, 1f);
            float stepAngle = 2f * Mathf.Acos(ratio);
            if (stepAngle <= 1e-4f) stepAngle = 1e-4f;
            return Mathf.Clamp(Mathf.CeilToInt(sweepAngle / stepAngle), 1, 4096);
        }

        public static void AppendCubic(List<Vector2> pts, Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3,
                                       float toleranceMm)
        {
            // 制御多角形の長さから必要分割数を見積もる（安全側）
            float polyLen = (c1 - p0).magnitude + (c2 - c1).magnitude + (p3 - c2).magnitude;
            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(polyLen / Mathf.Max(toleranceMm, 1e-3f)) * 2f), 4, 4096);

            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float u = 1f - t;
                Vector2 p = u * u * u * p0
                          + 3f * u * u * t * c1
                          + 3f * u * t * t * c2
                          + t * t * t * p3;
                pts.Add(p);
            }
        }

        /// <summary>折れ線の総長 [mm]。</summary>
        public static float PolylineLength(IReadOnlyList<Vector2> pts)
        {
            float len = 0f;
            for (int i = 1; i < pts.Count; i++) len += (pts[i] - pts[i - 1]).magnitude;
            return len;
        }

        /// <summary>点 p から線分 ab への距離の 2 乗。</summary>
        public static float DistanceSqToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq < 1e-12f) return (p - a).sqrMagnitude;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            return (p - (a + t * ab)).sqrMagnitude;
        }
    }
}
