using System;
using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// コース面の「どの位置が何色か」を解析的に答えるための構造。
    ///
    /// テクスチャを焼いて読む方式だと 4000x2000mm を 1px/mm でも 32MB になり、
    /// しかも解像度がそのままカラーセンサの精度上限になってしまう。
    /// ここでは幾何のまま保持し、一様グリッドで絞り込んで距離判定する。
    /// 解像度に依存せず、1 サンプルあたり数十回の距離計算で済む。
    /// </summary>
    public sealed class CourseField
    {
        struct Edge
        {
            public Vector2 A, B;
            public float HalfWidth;
            public int Order;      // 描画順。大きいほど手前
            public int ColorIndex;
        }

        struct AreaItem
        {
            public AreaShape Shape;
            public Vector2 Min, Max;   // 内外判定前の足切り用
            public Vector2 Center;
            public Vector2 HalfSize;
            public float CosR, SinR;   // rect の逆回転用
            public float Radius;
            public Vector2[][] Contours;   // polygon。複数あれば向きの逆な輪郭が穴になる
            public bool FillNonZero;       // 輪郭が重なったときの塗り規則
            public int Order;
            public int ColorIndex;
        }

        readonly CourseDefinition _def;
        readonly List<Edge> _edges = new List<Edge>();
        readonly List<AreaItem> _areas = new List<AreaItem>();
        readonly List<Color32> _palette = new List<Color32>();

        readonly float _cellSizeMm;
        readonly int _cols, _rows;
        readonly List<int>[] _edgeCells;  // セル -> _edges の index
        readonly List<int>[] _areaCells;  // セル -> _areas の index

        /// <summary>パスごとの折れ線。メッシュ生成にも使う。</summary>
        public readonly List<List<Vector2>> PathPolylines = new List<List<Vector2>>();

        public CourseDefinition Definition => _def;
        public Color32 SheetColor => _def.SheetColor;

        /// <summary>
        /// 探索格子の 1 セルの大きさ [mm]。0 以下を渡すと要素数から自動で決める。
        /// </summary>
        public CourseField(CourseDefinition def, float cellSizeMm = 0f)
        {
            _def = def ?? throw new ArgumentNullException(nameof(def));

            // セルの大きさは「実際に登録する要素数」から決めたいので、
            // 先にパスを折れ線化してしまう。セグメント数からの見積もりでは、
            // 曲線 1 本が何十本の線分になるかを読み違えて格子が粗くなる。
            var polylines = new List<List<Vector2>>(def.Paths.Count);
            int edgeCount = 0;
            foreach (var p in def.Paths)
            {
                var pts = CourseGeometry.Flatten(p);
                polylines.Add(pts);
                edgeCount += Mathf.Max(0, pts.Count - 1);
            }

            _cellSizeMm = cellSizeMm > 0f
                ? Mathf.Max(8f, cellSizeMm)
                : ChooseCellSize(def, def.Areas.Count + edgeCount);

            _cols = Mathf.Max(1, Mathf.CeilToInt(def.SheetWidthMm / _cellSizeMm));
            _rows = Mathf.Max(1, Mathf.CeilToInt(def.SheetHeightMm / _cellSizeMm));
            _edgeCells = new List<int>[_cols * _rows];
            _areaCells = new List<int>[_cols * _rows];

            // 描画順は各要素の Z に従う。CourseDefinition が既定値を割り当て済みなので、
            // z を書いていないコースでも「areas -> paths」の従来どおりの順になる。
            foreach (var a in def.Areas) AddArea(a, a.Z);
            for (int i = 0; i < def.Paths.Count; i++) AddPath(def.Paths[i], polylines[i], def.Paths[i].Z);
        }

        /// <summary>
        /// 1 セルあたりの候補数がだいたい一定になるようセルの大きさを決める。
        /// 要素が増えるほど細かくなり、1 画素あたりの距離計算回数が跳ね上がらない。
        /// </summary>
        static float ChooseCellSize(CourseDefinition def, int elementCount)
        {
            float areaMm2 = Mathf.Max(1f, def.SheetWidthMm * def.SheetHeightMm);
            const float targetPerCell = 4f;

            float cell = Mathf.Sqrt(areaMm2 * targetPerCell / Mathf.Max(1, elementCount));

            // 下限は 6mm。これより細かくするとセル数が増えすぎて、
            // グリッドの構築とメモリのほうが重くなる。
            return Mathf.Clamp(cell, 6f, 64f);
        }

        int ColorIndexOf(Color32 c)
        {
            for (int i = 0; i < _palette.Count; i++)
            {
                var q = _palette[i];
                if (q.r == c.r && q.g == c.g && q.b == c.b && q.a == c.a) return i;
            }
            _palette.Add(c);
            return _palette.Count - 1;
        }

        void AddArea(CourseArea a, int order)
        {
            var item = new AreaItem
            {
                Shape = a.Shape,
                Center = a.Center,
                Radius = a.Radius,
                Order = order,
                ColorIndex = ColorIndexOf(a.Color)
            };

            Vector2 min, max;
            switch (a.Shape)
            {
                case AreaShape.Rect:
                {
                    item.HalfSize = a.Size * 0.5f;
                    float rad = a.RotationDeg * Mathf.Deg2Rad;
                    item.CosR = Mathf.Cos(rad);
                    item.SinR = Mathf.Sin(rad);
                    float ext = item.HalfSize.magnitude; // 回転を含む保守的な外接半径
                    min = a.Center - new Vector2(ext, ext);
                    max = a.Center + new Vector2(ext, ext);
                    break;
                }
                case AreaShape.Circle:
                    min = a.Center - new Vector2(a.Radius, a.Radius);
                    max = a.Center + new Vector2(a.Radius, a.Radius);
                    break;
                default:
                {
                    if (a.Contours.Count == 0) return;
                    item.FillNonZero = a.FillNonZero;

                    item.Contours = new Vector2[a.Contours.Count][];
                    bool first = true;
                    min = max = Vector2.zero;

                    for (int i = 0; i < a.Contours.Count; i++)
                    {
                        item.Contours[i] = a.Contours[i].ToArray();
                        foreach (var p in item.Contours[i])
                        {
                            if (first) { min = max = p; first = false; }
                            else { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
                        }
                    }
                    if (first) return;
                    break;
                }
            }

            item.Min = min;
            item.Max = max;
            _areas.Add(item);
            RegisterAabb(_areaCells, _areas.Count - 1, min, max);
        }

        void AddPath(CoursePath p, List<Vector2> pts, int order)
        {
            PathPolylines.Add(pts);

            float half = Mathf.Max(0.1f, p.WidthMm * 0.5f);
            int colorIndex = ColorIndexOf(p.Color);

            for (int i = 1; i < pts.Count; i++)
            {
                var e = new Edge
                {
                    A = pts[i - 1],
                    B = pts[i],
                    HalfWidth = half,
                    Order = order,
                    ColorIndex = colorIndex
                };
                _edges.Add(e);

                Vector2 min = Vector2.Min(e.A, e.B) - new Vector2(half, half);
                Vector2 max = Vector2.Max(e.A, e.B) + new Vector2(half, half);
                RegisterAabb(_edgeCells, _edges.Count - 1, min, max);
            }
        }

        void RegisterAabb(List<int>[] cells, int index, Vector2 min, Vector2 max)
        {
            int c0 = Mathf.Clamp(Mathf.FloorToInt(min.x / _cellSizeMm), 0, _cols - 1);
            int c1 = Mathf.Clamp(Mathf.FloorToInt(max.x / _cellSizeMm), 0, _cols - 1);
            int r0 = Mathf.Clamp(Mathf.FloorToInt(min.y / _cellSizeMm), 0, _rows - 1);
            int r1 = Mathf.Clamp(Mathf.FloorToInt(max.y / _cellSizeMm), 0, _rows - 1);

            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int k = r * _cols + c;
                    (cells[k] ??= new List<int>(4)).Add(index);
                }
            }
        }

        int CellIndex(Vector2 mm)
        {
            int c = Mathf.FloorToInt(mm.x / _cellSizeMm);
            int r = Mathf.FloorToInt(mm.y / _cellSizeMm);
            if (c < 0 || c >= _cols || r < 0 || r >= _rows) return -1;
            return r * _cols + c;
        }

        /// <summary>
        /// コース座標 [mm] のその点の色。シート外は黒（カラーセンサが台から外れた状態）。
        /// </summary>
        public Color32 SampleColor(Vector2 mm)
        {
            int cell = CellIndex(mm);
            if (cell < 0) return new Color32(24, 24, 24, 255);

            // 描画順は負にもなり得る（未指定のまま渡された場合）ので、下限から始める
            int bestOrder = int.MinValue;
            int bestColor = -1;

            var areaList = _areaCells[cell];
            if (areaList != null)
            {
                for (int i = 0; i < areaList.Count; i++)
                {
                    var a = _areas[areaList[i]];
                    if (a.Order < bestOrder) continue;
                    // 多角形の全頂点走査に入る前に外接矩形で落とす。
                    // ロゴのような頂点数の多い図形がいくつもあると、ここの差が大きい。
                    if (mm.x < a.Min.x || mm.x > a.Max.x || mm.y < a.Min.y || mm.y > a.Max.y) continue;
                    if (!AreaContains(a, mm)) continue;
                    bestOrder = a.Order;
                    bestColor = a.ColorIndex;
                }
            }

            var edgeList = _edgeCells[cell];
            if (edgeList != null)
            {
                for (int i = 0; i < edgeList.Count; i++)
                {
                    var e = _edges[edgeList[i]];
                    if (e.Order < bestOrder) continue;
                    if (CourseGeometry.DistanceSqToSegment(mm, e.A, e.B) > e.HalfWidth * e.HalfWidth) continue;
                    bestOrder = e.Order;
                    bestColor = e.ColorIndex;
                }
            }

            return bestColor < 0 ? _def.SheetColor : _palette[bestColor];
        }

        static bool AreaContains(in AreaItem a, Vector2 p)
        {
            switch (a.Shape)
            {
                case AreaShape.Rect:
                {
                    Vector2 d = p - a.Center;
                    // rect のローカル座標へ逆回転
                    float lx = d.x * a.CosR + d.y * a.SinR;
                    float ly = -d.x * a.SinR + d.y * a.CosR;
                    return Mathf.Abs(lx) <= a.HalfSize.x && Mathf.Abs(ly) <= a.HalfSize.y;
                }
                case AreaShape.Circle:
                    return (p - a.Center).sqrMagnitude <= a.Radius * a.Radius;
                default:
                {
                    // 輪郭をすべて通して数えるので、内側の輪郭が穴になる。
                    // 非ゼロ回転数規則なら辺の向きまで数え、偶奇規則なら数だけ数える。
                    // CourseRasterizer と同じ規則でなければ、見た目とセンサの色がずれる。
                    int winding = 0;
                    foreach (var pts in a.Contours)
                    {
                        for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
                        {
                            if ((pts[i].y > p.y) == (pts[j].y > p.y)) continue;
                            float x = (pts[j].x - pts[i].x) * (p.y - pts[i].y) / (pts[j].y - pts[i].y) + pts[i].x;
                            if (p.x >= x) continue;
                            winding += a.FillNonZero ? (pts[i].y > pts[j].y ? 1 : -1) : 1;
                        }
                    }
                    return a.FillNonZero ? winding != 0 : (winding & 1) != 0;
                }
            }
        }

        /// <summary>
        /// 最も近いラインまでの距離 [mm]。コースアウト判定に使う。
        /// 近傍セルに何もなければ float.MaxValue を返す。
        /// </summary>
        public float DistanceToNearestLine(Vector2 mm, float searchRadiusMm = 300f)
        {
            int c0 = Mathf.Clamp(Mathf.FloorToInt((mm.x - searchRadiusMm) / _cellSizeMm), 0, _cols - 1);
            int c1 = Mathf.Clamp(Mathf.FloorToInt((mm.x + searchRadiusMm) / _cellSizeMm), 0, _cols - 1);
            int r0 = Mathf.Clamp(Mathf.FloorToInt((mm.y - searchRadiusMm) / _cellSizeMm), 0, _rows - 1);
            int r1 = Mathf.Clamp(Mathf.FloorToInt((mm.y + searchRadiusMm) / _cellSizeMm), 0, _rows - 1);

            float bestSq = float.MaxValue;
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    var list = _edgeCells[r * _cols + c];
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var e = _edges[list[i]];
                        float dSq = CourseGeometry.DistanceSqToSegment(mm, e.A, e.B);
                        if (dSq < bestSq) bestSq = dSq;
                    }
                }
            }
            return bestSq == float.MaxValue ? float.MaxValue : Mathf.Sqrt(bestSq);
        }

        /// <summary>
        /// 床表示用のテクスチャを焼く。センサはこれを使わないので、
        /// 見た目に足りる解像度でよい。
        ///
        /// 実際の描画は CourseRasterizer が行う。1 画素ずつこのクラスへ
        /// 問い合わせる方式だと、面の多いコースで極端に遅くなるため。
        /// </summary>
        public Texture2D BakeTexture(float pixelsPerMm = 0.5f)
        {
            return CourseRasterizer.Bake(_def, this, pixelsPerMm);
        }
    }
}
