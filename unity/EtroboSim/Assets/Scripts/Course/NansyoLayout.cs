using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// 難所の配置場所をコース図から読み取る。
    ///
    /// ゲートポジションも土俵もピックアップポジションもコースに印刷されているので、
    /// 座標を持たずに JSON から拾う。tools/add_nansyo.py と同じ読み方で、
    /// あちらは事前に、こちらは実行中に配置を変えるために使う。
    /// </summary>
    public sealed class NansyoLayout
    {
        /// <summary>ゲートポジションの名前。競技規約 5.17.1 の GX-Y。</summary>
        public const int GridSize = 5;

        /// <summary>走行体が L コース側 (シートの左半分) から出るか。</summary>
        public bool StartsOnLeft { get; private set; }

        public bool HasGrid { get; private set; }
        public Vector2 Pickup { get; private set; }
        public bool HasPickup { get; private set; }
        public Vector2 DohyoCenter { get; private set; }
        public float DohyoRadiusMm { get; private set; }

        /// <summary>ドロップゾーン。コースに印刷された 200mm 角の赤・青・黄のエリア。</summary>
        public struct DropZone
        {
            public NansyoController.BottleColor Color;
            public Vector2 CenterMm;
            public Vector2 SizeMm;

            /// <summary>点がこのエリアに入っているか（半径 r の円で見る）。</summary>
            public bool Touches(Vector2 pointMm, float radiusMm)
            {
                Vector2 d = pointMm - CenterMm;
                Vector2 half = SizeMm * 0.5f;
                // 長方形までの距離。中に入っていれば 0 になる。
                float dx = Mathf.Max(0f, Mathf.Abs(d.x) - half.x);
                float dy = Mathf.Max(0f, Mathf.Abs(d.y) - half.y);
                return dx * dx + dy * dy <= radiusMm * radiusMm;
            }
        }

        public IReadOnlyList<DropZone> DropZones => _dropZones;
        readonly List<DropZone> _dropZones = new List<DropZone>();

        readonly Vector2[,] _grid = new Vector2[GridSize, GridSize];

        /// <summary>ゲートポジションの間隔 [mm]。ゲートの脚はこの上に置く。</summary>
        public float GridPitchMm { get; private set; } = 250f;

        public static NansyoLayout FromCourse(CourseDefinition def)
        {
            var layout = new NansyoLayout();
            layout.Read(def);
            return layout;
        }

        /// <summary>GX-Y (どちらも 1..5) の座標 [mm]。</summary>
        public bool TryGetGatePosition(int x, int y, out Vector2 mm)
        {
            mm = Vector2.zero;
            if (!HasGrid || x < 1 || x > GridSize || y < 1 || y > GridSize) return false;
            mm = _grid[x - 1, y - 1];
            return true;
        }

        void Read(CourseDefinition def)
        {
            float midX = def.SheetWidthMm * 0.5f;
            StartsOnLeft = def.StartPositionMm.x < midX;

            // 走行体が出る側のコースを扱う
            bool wantLeft = StartsOnLeft;

            var dots = new List<Vector2>();
            CourseArea biggestCircle = null;

            foreach (var a in def.Areas)
            {
                if (a.Shape != AreaShape.Circle) continue;
                if ((a.Center.x < midX) != wantLeft) continue;

                if (a.Radius >= 20f && a.Radius <= 25f) dots.Add(a.Center);
                if (a.Radius >= 100f && (biggestCircle == null || a.Radius > biggestCircle.Radius))
                    biggestCircle = a;
            }

            if (biggestCircle != null)
            {
                DohyoCenter = biggestCircle.Center;
                DohyoRadiusMm = biggestCircle.Radius;
            }

            BuildGrid(dots, wantLeft);
            ReadDropZones(def, midX, wantLeft);
        }

        /// <summary>
        /// ドロップゾーンを読む。200mm 角で、キャリーボトルと同じ 3 色に塗ってある
        /// エリアがそれ。コースの反対側にも同じものがあるので、側で絞る。
        /// </summary>
        void ReadDropZones(CourseDefinition def, float midX, bool wantLeft)
        {
            _dropZones.Clear();

            foreach (var a in def.Areas)
            {
                if (a.Shape != AreaShape.Rect) continue;
                if (Mathf.Abs(a.Size.x - 200f) > 5f || Mathf.Abs(a.Size.y - 200f) > 5f) continue;
                if ((a.Center.x < midX) != wantLeft) continue;

                foreach (var c in NansyoController.AllColors)
                {
                    var rgb = NansyoController.Rgb(c);
                    if (Mathf.Abs(rgb.r - a.Color.r) > 8 || Mathf.Abs(rgb.g - a.Color.g) > 8
                        || Mathf.Abs(rgb.b - a.Color.b) > 8) continue;

                    _dropZones.Add(new DropZone { Color = c, CenterMm = a.Center, SizeMm = a.Size });
                    break;
                }
            }
        }

        void BuildGrid(List<Vector2> dots, bool wantLeft)
        {
            // 5 個そろっている列・行だけが格子。本線の上にあるピックアップ
            // ポジションが 1 個混ざるので、数で見分ける。
            var xs = CountedValues(dots, true);
            var ys = CountedValues(dots, false);
            if (xs.Count != GridSize || ys.Count != GridSize) return;

            // X は横方向。L コースは左から右、R コースは鏡像なので右から左。
            if (!wantLeft) xs.Reverse();
            // Y は縦方向で 1 が上。コース座標の y は上ほど大きい。
            ys.Reverse();

            for (int i = 0; i < GridSize; i++)
                for (int j = 0; j < GridSize; j++)
                    _grid[i, j] = new Vector2(xs[i], ys[j]);

            HasGrid = true;
            GridPitchMm = Mathf.Abs(xs[1] - xs[0]);

            // 格子に含まれない灰色マーカがピックアップポジション
            foreach (var d in dots)
            {
                bool onGrid = false;
                for (int i = 0; i < GridSize && !onGrid; i++)
                    for (int j = 0; j < GridSize && !onGrid; j++)
                        if ((d - _grid[i, j]).sqrMagnitude < 1f) onGrid = true;

                if (!onGrid)
                {
                    Pickup = d;
                    HasPickup = true;
                    break;
                }
            }
        }

        /// <summary>5 個以上そろっている座標値を小さい順に返す。</summary>
        static List<float> CountedValues(List<Vector2> dots, bool useX)
        {
            var counts = new Dictionary<int, int>();
            foreach (var d in dots)
            {
                int key = Mathf.RoundToInt(useX ? d.x : d.y);
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }

            var values = new List<float>();
            foreach (var kv in counts)
                if (kv.Value >= GridSize) values.Add(kv.Key);

            values.Sort();
            return values;
        }
    }
}
