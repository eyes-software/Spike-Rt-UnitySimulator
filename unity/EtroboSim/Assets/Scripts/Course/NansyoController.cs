using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// 難所の配置を実行中に変える。
    ///
    /// キャリーボトルの色とゲートの置き場所は、大会当日にならないと分からない
    /// （規約 5.16.1「参加者は、ボトルの色をリクエストすることはできない」、
    /// 6.3.1「ゲートの配置パターンは大会当日の試走前に公開される」）。
    /// コースを作り直さずに画面から変えられるようにしておく。
    /// </summary>
    public sealed class NansyoController : MonoBehaviour
    {
        /// <summary>キャリーボトルとゲートの色。規約は黄・青・赤の 3 色。</summary>
        public enum BottleColor { Red, Blue, Yellow }

        public static readonly BottleColor[] AllColors =
            { BottleColor.Red, BottleColor.Blue, BottleColor.Yellow };

        public NansyoLayout Layout { get; private set; }

        /// <summary>いま置かれているキャリーボトルの色。</summary>
        public BottleColor CarryColor { get; private set; } = BottleColor.Blue;

        /// <summary>ゲートの置き場所 (GX-Y の X と Y)。</summary>
        public Vector2Int GetGateCell(BottleColor color) => _gateCells[color];

        readonly Dictionary<BottleColor, Vector2Int> _gateCells = new Dictionary<BottleColor, Vector2Int>
        {
            { BottleColor.Red,    new Vector2Int(2, 5) },
            { BottleColor.Blue,   new Vector2Int(5, 3) },
            { BottleColor.Yellow, new Vector2Int(1, 2) },
        };

        readonly Dictionary<BottleColor, Transform> _gates = new Dictionary<BottleColor, Transform>();
        Transform _carryBottle;
        Transform _rikishiBottle;
        float _gatePipeDiameterMm = 18f;
        float _gateInnerHeightMm = 245f;
        float _rikishiRadiusMm = 34f;
        float _carryRadiusMm = 31f;
        float _gateInnerWidthMm = 232f;
        Vector2 _carryHomeMm;
        Vector2 _rikishiOffsetMm;

        /// <summary>成立判定が使うもの。</summary>
        public Transform CarryBottle => _carryBottle;
        public Transform RikishiBottle => _rikishiBottle;
        public float RikishiRadiusMm => _rikishiRadiusMm;
        public float CarryRadiusMm => _carryRadiusMm;

        /// <summary>ゲートの脚の内側の幅 [mm]。走行体はここを通る。</summary>
        public float GateInnerWidthMm => _gateInnerWidthMm;

        public Transform GetGate(BottleColor c) => _gates.TryGetValue(c, out var t) ? t : null;

        public bool HasCarryBottle => _carryBottle != null;
        public bool HasRikishiBottle => _rikishiBottle != null;
        public bool HasGate(BottleColor c) => _gates.ContainsKey(c);

        /// <summary>力士ボトルの置き場所。土俵の中心からのずれ [mm]（コース座標）。</summary>
        public Vector2 RikishiOffsetMm => _rikishiOffsetMm;

        /// <summary>力士ボトルを土俵の中で動かせる範囲 [mm]。</summary>
        public float RikishiRangeMm =>
            Layout == null ? 0f : Mathf.Max(0f, Layout.DohyoRadiusMm - _rikishiRadiusMm);

        public void Initialize(CourseDefinition def, IReadOnlyDictionary<string, Transform> props)
        {
            Layout = NansyoLayout.FromCourse(def);

            props.TryGetValue("carry_bottle", out var carry);
            _carryBottle = carry;

            props.TryGetValue("rikishi_bottle", out var rikishi);
            _rikishiBottle = rikishi;

            foreach (var p in def.Props)
            {
                if (p.Id == "carry_bottle")
                {
                    _carryHomeMm = p.Position;
                    _carryRadiusMm = p.Size.x * 0.5f;
                }
                if (p.Id != "rikishi_bottle") continue;

                _rikishiRadiusMm = p.Size.x * 0.5f;
                // 図面では土俵の中心に置いてあるが、--rikishi-offset でずらしてあるかもしれない
                _rikishiOffsetMm = p.Position - Layout.DohyoCenter;
            }

            foreach (var p in def.Props)
            {
                if (p.Type != "gate") continue;

                var color = ColorOf(p.Color);
                if (color == null) continue;

                if (props.TryGetValue(p.Id, out var t))
                {
                    _gates[color.Value] = t;
                    _gatePipeDiameterMm = p.PipeDiameterMm;
                    _gateInnerWidthMm = p.Size.x;
                    _gateInnerHeightMm = p.Size.z;

                    var cell = FindCell(p.Position);
                    if (cell.HasValue) _gateCells[color.Value] = cell.Value;
                }
            }

            // いまの見た目から、置かれているキャリーボトルの色を拾う
            foreach (var p in def.Props)
            {
                if (p.Id != "carry_bottle") continue;
                var c = ColorOf(p.LabelColor);
                if (c.HasValue) CarryColor = c.Value;
            }
        }

        // -- 変更 -----------------------------------------------------------

        /// <summary>
        /// 力士ボトルを土俵の中で動かす。指定は土俵の中心からのずれ [mm]。
        ///
        /// 規約 5.15.1 は「スタート準備時に土俵の任意の位置に設置される」としか
        /// 言っていないので、どこに置かれても走れることを確かめられるようにする。
        /// ボトルの底が土俵からはみ出さないところまでで頭打ちにする。
        /// </summary>
        public bool SetRikishiOffset(Vector2 offsetMm)
        {
            if (_rikishiBottle == null || Layout == null || Layout.DohyoRadiusMm <= 0f) return false;

            _rikishiOffsetMm = ClampToDohyo(offsetMm, Layout.DohyoRadiusMm, _rikishiRadiusMm);
            PlaceBottle(_rikishiBottle, Layout.DohyoCenter + _rikishiOffsetMm);
            return true;
        }

        /// <summary>土俵からはみ出さないところまで縮めたずれ。</summary>
        public static Vector2 ClampToDohyo(Vector2 offsetMm, float dohyoRadiusMm, float bottleRadiusMm)
        {
            float max = Mathf.Max(0f, dohyoRadiusMm - bottleRadiusMm);
            float len = offsetMm.magnitude;
            return len <= max || len <= 1e-6f ? offsetMm : offsetMm * (max / len);
        }

        /// <summary>
        /// ボトルを決めた場所へ戻す。走行体がぶつけて倒したあとに走り直すため。
        ///
        /// 走行体のリセット（R キー / 制御プロセスの再接続）と一緒に呼ぶ。
        /// これが無いと、一度押し出した力士ボトルが土俵の外に転がったままになる。
        /// </summary>
        public void RestoreBottles()
        {
            if (_rikishiBottle != null && Layout != null)
                PlaceBottle(_rikishiBottle, Layout.DohyoCenter + _rikishiOffsetMm);
            if (_carryBottle != null)
                PlaceBottle(_carryBottle, _carryHomeMm);
        }

        /// <summary>
        /// ボトルを立てて置き直す。Rigidbody を持っているので transform だけでは動かない
        /// （物理の位置が元のままになる）。速度も消さないと、置いた瞬間に飛んでいく。
        /// </summary>
        static void PlaceBottle(Transform t, Vector2 mm)
        {
            var pos = CourseSpace.ToWorld(mm, 0f);
            var rb = t.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = pos;
                rb.rotation = Quaternion.identity;
            }
            t.SetPositionAndRotation(pos, Quaternion.identity);
        }

        public void SetCarryColor(BottleColor color)
        {
            CarryColor = color;
            if (_carryBottle == null) return;

            var label = _carryBottle.Find("Label");
            if (label == null) return;

            var mr = label.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = RobotVisuals.GetMaterial(Rgb(color));
        }

        /// <summary>
        /// ゲートを GX-Y へ置き直す。向きは色で決まる（規約 表 5-2）ので、
        /// 指定するのは片方のゲートポジションだけでよい。
        /// </summary>
        public bool SetGateCell(BottleColor color, int x, int y)
        {
            if (Layout == null || !Layout.HasGrid) return false;
            if (!_gates.TryGetValue(color, out var gate) || gate == null) return false;

            var other = Neighbour(color, x, y);
            if (!Layout.TryGetGatePosition(x, y, out var a)) return false;
            if (!Layout.TryGetGatePosition(other.x, other.y, out var b)) return false;

            _gateCells[color] = new Vector2Int(x, y);

            Vector2 center = (a + b) * 0.5f;
            // コース方位 0 は +y 向き。脚がローカル X に並ぶので、
            // 横のゲート（x 方向に隣り合う）は 90 度。
            float heading = Mathf.Abs(a.y - b.y) < 1f ? 90f : 0f;

            gate.SetPositionAndRotation(CourseSpace.ToWorld(center, 0f),
                                        CourseSpace.HeadingToRotation(heading));
            return true;
        }

        /// <summary>規約 表 5-2 の向きに従った、隣のゲートポジション。</summary>
        public Vector2Int Neighbour(BottleColor color, int x, int y) => Neighbour2(color, x, y);

        /// <summary>インスタンスが無くても引けるようにしたもの（自己テスト用）。</summary>
        public static Vector2Int Neighbour2(BottleColor color, int x, int y)
        {
            if (IsHorizontal(color))
                return new Vector2Int(x < NansyoLayout.GridSize ? x + 1 : x - 1, y);
            return new Vector2Int(x, y < NansyoLayout.GridSize ? y + 1 : y - 1);
        }

        /// <summary>赤=横 / 青=縦 / 黄=横（規約 表 5-2）。</summary>
        public static bool IsHorizontal(BottleColor color) => color != BottleColor.Blue;

        public static string NameOf(BottleColor c) => c switch
        {
            BottleColor.Red => "赤",
            BottleColor.Blue => "青",
            _ => "黄",
        };

        public static Color32 Rgb(BottleColor c) => c switch
        {
            BottleColor.Red => new Color32(0xE8, 0x38, 0x28, 255),
            BottleColor.Blue => new Color32(0x03, 0x6E, 0xB8, 255),
            _ => new Color32(0xFF, 0xEA, 0x00, 255),
        };

        // -- 補助 -----------------------------------------------------------

        static BottleColor? ColorOf(Color32 c)
        {
            foreach (var candidate in AllColors)
            {
                var r = Rgb(candidate);
                if (Mathf.Abs(r.r - c.r) <= 8 && Mathf.Abs(r.g - c.g) <= 8 && Mathf.Abs(r.b - c.b) <= 8)
                    return candidate;
            }
            return null;
        }

        /// <summary>座標がどのゲートポジションの対の中心かを逆に引く。</summary>
        Vector2Int? FindCell(Vector2 centerMm)
        {
            if (Layout == null || !Layout.HasGrid) return null;

            for (int x = 1; x <= NansyoLayout.GridSize; x++)
            {
                for (int y = 1; y <= NansyoLayout.GridSize; y++)
                {
                    foreach (var color in AllColors)
                    {
                        var n = Neighbour(color, x, y);
                        if (!Layout.TryGetGatePosition(x, y, out var a)) continue;
                        if (!Layout.TryGetGatePosition(n.x, n.y, out var b)) continue;
                        if (((a + b) * 0.5f - centerMm).sqrMagnitude < 4f)
                            return new Vector2Int(x, y);
                    }
                }
            }
            return null;
        }
    }
}
