using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// 難所の成立判定（競技規約 1.0.1 の 5.15 / 5.16 / 5.17）。
    ///
    /// 3 つとも「リザルトポイント提示時」に判定するので、走行中はいつでも
    /// 成立・不成立がひっくり返る。ここでは毎フレーム今の状態を出しておき、
    /// ゴール時にその時点の内容を記録に残す。
    ///
    /// ET相撲とボトルデリバリーは**今そうなっているか**を見るだけでよいが、
    /// ETラリーだけは通った順番を覚える必要があるので状態を持つ。
    /// </summary>
    public sealed class NansyoJudge : MonoBehaviour
    {
        public NansyoController Nansyo;
        public RobotController Robot;

        // -- 結果 -------------------------------------------------------------

        /// <summary>ボトル押し出し（5.15）が成立している。5 ポイント。</summary>
        public bool PushedOut { get; private set; }

        /// <summary>デリバリー（5.16.2）が成立している。1 ポイント。</summary>
        public bool Delivered { get; private set; }

        /// <summary>置かれたドロップゾーンの色。色が一致していれば 5 ポイント。</summary>
        public NansyoController.BottleColor? DeliveredZone { get; private set; }

        public bool DeliveredColorMatch =>
            Delivered && DeliveredZone.HasValue && DeliveredZone.Value == Nansyo.CarryColor;

        /// <summary>ETラリー（5.17.3）の成立周回数。累積。</summary>
        public int RallyLaps => _rally.Laps;

        /// <summary>次に通るべきゲートの色。周回の途中経過。</summary>
        public NansyoController.BottleColor NextGate => RallyOrder[_rally.Step];

        /// <summary>直近に通ったゲートの色。まだ無ければ null。</summary>
        public NansyoController.BottleColor? LastGate => _rally.Last;

        /// <summary>規約 表 5-3 の通過順序。</summary>
        public static readonly NansyoController.BottleColor[] RallyOrder =
        {
            NansyoController.BottleColor.Red,
            NansyoController.BottleColor.Blue,
            NansyoController.BottleColor.Yellow,
        };

        /// <summary>難所の課題ポイント（規約 表 6-2 の 5〜7 番）。</summary>
        public int Points => (PushedOut ? 5 : 0)
                             + (Delivered ? (DeliveredColorMatch ? 5 : 1) : 0)
                             + RallyPoints(RallyLaps);

        /// <summary>ETラリーのポイント。1/2/3 周回で 5/10/15。いずれか 1 つだけ。</summary>
        public static int RallyPoints(int laps) => Mathf.Clamp(laps, 0, 3) * 5;

        // -- 判定のしきい値 ---------------------------------------------------

        /// <summary>「静止している」とみなす速さ [mm/s]。</summary>
        const float StillMmPerSec = 10f;

        /// <summary>「横倒しでない」とみなす傾き [deg]。これを超えたら倒れている。</summary>
        const float UprightLimitDeg = 30f;

        RallyState _rally;
        readonly Dictionary<NansyoController.BottleColor, GateCrossing> _crossings =
            new Dictionary<NansyoController.BottleColor, GateCrossing>();

        /// <summary>ETラリーの途中経過。純粋な値にしてあるので単体で試せる。</summary>
        public struct RallyState
        {
            /// <summary>次に通るべきゲートが RallyOrder の何番目か。</summary>
            public int Step;
            public int Laps;
            public NansyoController.BottleColor? Last;
        }

        /// <summary>
        /// ゲートを 1 本通ったときの状態遷移（規約 5.17.3）。
        /// 同色の連続通過は 1 回。順番どおりでなければ 1 番目からやり直し。
        /// </summary>
        public static RallyState AdvanceRally(RallyState s, NansyoController.BottleColor passed)
        {
            if (s.Last.HasValue && s.Last.Value == passed) return s;
            s.Last = passed;

            if (passed == RallyOrder[s.Step])
            {
                s.Step++;
                if (s.Step >= RallyOrder.Length)
                {
                    s.Step = 0;
                    s.Laps++;
                }
                return s;
            }

            // 順番が違うのでやり直し。通ったのが 1 番目の色なら、それが 1 本目になる。
            s.Step = passed == RallyOrder[0] ? 1 : 0;
            return s;
        }

        /// <summary>力士ボトルが土俵の外へ完全に出ているか（規約 5.15）。</summary>
        public static bool IsOutsideDohyo(Vector2 bottleMm, Vector2 dohyoCenterMm,
                                          float bottleRadiusMm, float dohyoRadiusMm)
            => Vector2.Distance(bottleMm, dohyoCenterMm) - bottleRadiusMm > dohyoRadiusMm;

        public void Initialize(NansyoController nansyo, RobotController robot)
        {
            Nansyo = nansyo;
            Robot = robot;
            ResetJudge();
        }

        public void ResetJudge()
        {
            PushedOut = false;
            Delivered = false;
            DeliveredZone = null;
            _rally = default;
            _crossings.Clear();
        }

        void FixedUpdate()
        {
            if (Nansyo == null || Nansyo.Layout == null || Robot == null) return;

            JudgeSumo();
            JudgeDelivery();
            JudgeRally();
        }

        // -- ET相撲 (5.15) -----------------------------------------------------

        /// <summary>
        /// 「力士ボトルを土俵外に完全に押し出す」。底面がコースに接地して静止していること。
        /// 横倒しは含まれない。
        /// </summary>
        void JudgeSumo()
        {
            var bottle = Nansyo.RikishiBottle;
            if (bottle == null || Nansyo.Layout.DohyoRadiusMm <= 0f)
            {
                PushedOut = false;
                SumoReason = "力士ボトルがありません";
                return;
            }

            // 成立しない理由を残す。「不成立」だけだと、どこまでできていたのか
            // （まだ土俵の中なのか、倒したのか、転がっている最中なのか）が分からない。
            if (Vector3.Angle(bottle.up, Vector3.up) > UprightLimitDeg)
            {
                PushedOut = false;
                SumoReason = "横倒し";
                return;
            }

            if (!IsStandingStill(bottle))
            {
                PushedOut = false;
                SumoReason = "まだ動いている";
                return;
            }

            // 「完全に」押し出す。底面が土俵の線にかかっていたらまだ成立しない。
            Vector2 pos = CourseSpace.ToCourseMm(bottle.position);
            float d = Vector2.Distance(pos, Nansyo.Layout.DohyoCenter);
            PushedOut = IsOutsideDohyo(pos, Nansyo.Layout.DohyoCenter,
                                       Nansyo.RikishiRadiusMm, Nansyo.Layout.DohyoRadiusMm);
            SumoReason = PushedOut ? null
                       : $"土俵にかかっている（中心から {d:F0}mm、外まであと "
                         + $"{Nansyo.Layout.DohyoRadiusMm + Nansyo.RikishiRadiusMm - d:F0}mm）";
        }

        /// <summary>押し出しが成立していない理由。成立していれば null。</summary>
        public string SumoReason { get; private set; }

        // -- ボトルデリバリー (5.16.2) ----------------------------------------

        /// <summary>
        /// 「ドロップゾーン内にボトルの底面が一部でも接して置かれている」。
        /// 一部でよいので、底の円がエリアに重なっていれば成立。
        /// </summary>
        void JudgeDelivery()
        {
            var bottle = Nansyo.CarryBottle;
            if (bottle == null || !IsStandingStill(bottle))
            {
                Delivered = false;
                DeliveredZone = null;
                return;
            }

            Vector2 pos = CourseSpace.ToCourseMm(bottle.position);

            foreach (var zone in Nansyo.Layout.DropZones)
            {
                if (!zone.Touches(pos, Nansyo.CarryRadiusMm)) continue;

                Delivered = true;
                DeliveredZone = zone.Color;
                return;
            }

            Delivered = false;
            DeliveredZone = null;
        }

        /// <summary>
        /// 底面がコースに接地して静止していて、横倒しでないこと。
        /// 相撲もデリバリーも同じ条件（規約 5.15 / 5.16.2）。
        /// </summary>
        bool IsStandingStill(Transform bottle)
        {
            if (Vector3.Angle(bottle.up, Vector3.up) > UprightLimitDeg) return false;

            var rb = bottle.GetComponent<Rigidbody>();
            if (rb == null) return true;

            return rb.velocity.magnitude * CourseSpace.MToMm <= StillMmPerSec;
        }

        // -- ETラリー (5.17.3) -------------------------------------------------

        /// <summary>
        /// ゲートごとの通り抜けの途中経過。
        ///
        /// 走行体**全体**が通ること、方向と接触は不問。そこで
        /// 「走行体の四隅がゲートの面をまたぎ切った」ことで判定する。
        /// またいでいる間に 1 つでも脚の外側へ出たら、その通過は無効。
        /// </summary>
        public struct GateCrossing
        {
            public int Side;        // 直前にどちら側にいたか (+1/-1)、0 は未定
            public bool Valid;      // またいでいる間ずっと脚の内側だったか

            /// <summary>
            /// 四隅を与えて 1 ステップ進める。通過が成立したら true。
            ///
            /// axis は脚の並ぶ向き（単位ベクトル）。面はゲートの位置で無限に広がるが、
            /// またいでいる間の脚の内外を見るので、遠くで面を横切っても数えない。
            /// </summary>
            public bool Step(Vector2 gateCenterMm, Vector2 axis, float halfOpeningMm,
                             IReadOnlyList<Vector2> corners)
            {
                Vector2 normal = new Vector2(-axis.y, axis.x);

                bool allPositive = true, allNegative = true, allInside = true;
                for (int i = 0; i < corners.Count; i++)
                {
                    Vector2 d = corners[i] - gateCenterMm;
                    float sDist = Vector2.Dot(d, normal);
                    float t = Vector2.Dot(d, axis);

                    if (sDist <= 0f) allPositive = false;
                    if (sDist >= 0f) allNegative = false;
                    if (Mathf.Abs(t) > halfOpeningMm) allInside = false;
                }

                if (allPositive || allNegative)
                {
                    int side = allPositive ? 1 : -1;
                    bool passed = Side != 0 && side != Side && Valid;

                    Side = side;
                    Valid = true;    // 次にまたぎ始めるまでは有効
                    return passed;
                }

                // またいでいる最中。1 度でも脚の外へ出たらこの通過は数えない。
                if (!allInside) Valid = false;
                return false;
            }
        }

        void JudgeRally()
        {
            foreach (var color in NansyoController.AllColors)
            {
                var gate = Nansyo.GetGate(color);
                if (gate == null) continue;

                if (!_crossings.TryGetValue(color, out var c))
                    c = new GateCrossing { Side = 0, Valid = true };

                // ゲートの面。脚はローカル X に並び、通り抜けるのはローカル Z 方向。
                Vector2 center = CourseSpace.ToCourseMm(gate.position);
                Vector2 axis = CourseSpace.FlattenToCourse(gate.right).normalized;

                if (c.Step(center, axis, Nansyo.GateInnerWidthMm * 0.5f, RobotFootprint()))
                    OnGatePassed(color);

                _crossings[color] = c;
            }
        }

        /// <summary>
        /// 通過順の判定（規約 5.17.3）。
        /// 同色の連続通過は 1 回。順番どおりでなければ 1 番目からやり直し。
        /// </summary>
        void OnGatePassed(NansyoController.BottleColor color)
        {
            var before = _rally;
            _rally = AdvanceRally(_rally, color);

            if (_rally.Laps != before.Laps)
                Debug.Log($"[Etsim] ETラリー {_rally.Laps} 周回目が成立しました");
            else if (_rally.Step != before.Step || _rally.Last != before.Last)
                Debug.Log($"[Etsim] {NansyoController.NameOf(color)}ゲートを通過"
                          + $"（次は {NansyoController.NameOf(NextGate)}）");
        }

        /// <summary>
        /// 走行体の接地面の四隅（コース座標 mm）。
        ///
        /// 「走行体全体」なので、車体だけでなくキャスタと車輪まで含めた外形で見る。
        /// </summary>
        Vector2[] RobotFootprint()
        {
            return Footprint(Robot.Spec, Robot.TruthPositionMm, Robot.TruthHeadingDeg, _corners);
        }

        /// <summary>走行体の外形の四隅 [mm]。into に書いて返す。</summary>
        public static Vector2[] Footprint(RobotSpec spec, Vector2 posMm, float headingDeg,
                                          Vector2[] into)
        {
            float halfBody = spec.BodyLengthMm * 0.5f;
            float front = Mathf.Max(spec.BodyOffsetXMm + halfBody, spec.WheelDiameterMm * 0.5f);
            float back = Mathf.Min(spec.BodyOffsetXMm - halfBody,
                                   spec.CasterOffsetXMm - spec.CasterRadiusMm);
            float half = Mathf.Max(spec.BodyWidthMm, spec.TreadMm + spec.WheelWidthMm) * 0.5f;

            float rad = headingDeg * Mathf.Deg2Rad;
            Vector2 fwd = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            Vector2 left = new Vector2(-fwd.y, fwd.x);

            into[0] = posMm + fwd * front + left * half;
            into[1] = posMm + fwd * front - left * half;
            into[2] = posMm + fwd * back + left * half;
            into[3] = posMm + fwd * back - left * half;
            return into;
        }

        readonly Vector2[] _corners = new Vector2[4];

        /// <summary>ゴールしたときなどに 1 行で残す。</summary>
        public string Summary()
        {
            string delivery = !Delivered ? "不成立"
                            : DeliveredColorMatch ? "色一致"
                            : $"{NansyoController.NameOf(DeliveredZone.Value)}ゾーン";

            return $"押し出し {(PushedOut ? "成立" : "不成立（" + SumoReason + "）")} / "
                   + $"デリバリー {delivery} / ラリー {RallyLaps} 周回"
                   + $" = 難所 {Points} ポイント";
        }
    }
}
