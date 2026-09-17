using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// SPIKE 超音波センサのモデル。
    ///
    /// 超音波は細いレーザではなく円錐状に広がるので、中心 1 本のレイだと
    /// 「斜めに置かれた壁を実機より近く/遠く読む」挙動が再現できない。
    /// ここではビーム内に複数本のレイを飛ばし、最短距離を採る（実機と同じ挙動）。
    ///
    /// さらに、浅い角度で当たった面は音波が返ってこないので落とす。
    /// これが無いと、広がったビームの下端が必ず床を捉えて、
    /// 何も無いところでも一定の距離を返してしまう。
    /// </summary>
    public sealed class UltrasonicSensorModel
    {
        readonly RobotSpec _spec;
        readonly Transform _mount;
        readonly int _rayMask;
        readonly Vector3[] _beamDirsLocal;

        /// <summary>検出距離 [mm]。範囲外は -1。</summary>
        public int DistanceMm { get; private set; } = -1;
        public bool Present => DistanceMm >= 0;

        public UltrasonicSensorModel(RobotSpec spec, Transform mount, int rayMask)
        {
            _spec = spec;
            _mount = mount;
            _rayMask = rayMask;
            _beamDirsLocal = BuildBeam(spec.UltrasonicBeamDeg, 8);
        }

        static Vector3[] BuildBeam(float halfAngleDeg, int ringCount)
        {
            var dirs = new Vector3[ringCount + 1];
            dirs[0] = Vector3.forward;

            float tan = Mathf.Tan(halfAngleDeg * Mathf.Deg2Rad);
            for (int i = 0; i < ringCount; i++)
            {
                float a = i * 2f * Mathf.PI / ringCount;
                // ビーム縁の円周上に等間隔で配置する
                dirs[i + 1] = new Vector3(Mathf.Cos(a) * tan, Mathf.Sin(a) * tan, 1f).normalized;
            }
            return dirs;
        }

        public void Reset() => DistanceMm = -1;

        public void Step()
        {
            float maxM = _spec.UltrasonicMaxMm * CourseSpace.MmToM;
            float cosLimit = Mathf.Cos(Mathf.Clamp(_spec.UltrasonicMaxIncidenceDeg, 0f, 89f) * Mathf.Deg2Rad);
            float best = float.MaxValue;

            foreach (var local in _beamDirsLocal)
            {
                Vector3 dir = _mount.TransformDirection(local);
                if (Physics.Raycast(_mount.position, dir, out RaycastHit hit, maxM,
                                    _rayMask, QueryTriggerInteraction.Ignore))
                {
                    // 浅い角度で当たった面は、音波が鏡のように向こうへ反射して
                    // 戻ってこない。光のレイと違ってここが効く。
                    // 入射角を法線との角度で見て、浅すぎるものは捨てる。
                    if (Vector3.Dot(hit.normal, -dir) < cosLimit) continue;

                    // 斜めに当たったぶんは行路長そのものが返るので hit.distance をそのまま使う
                    if (hit.distance < best) best = hit.distance;
                }
            }

            if (best == float.MaxValue)
            {
                DistanceMm = -1;
                return;
            }

            float mm = best * CourseSpace.MToMm + Gaussian() * _spec.UltrasonicNoiseMm;

            if (mm > _spec.UltrasonicMaxMm) { DistanceMm = -1; return; }
            // 最小検出距離より近いものは、実機同様に最小値へ張り付く
            DistanceMm = Mathf.RoundToInt(Mathf.Max(mm, _spec.UltrasonicMinMm));
        }

        static float Gaussian()
        {
            float u1 = Mathf.Max(1e-6f, UnityEngine.Random.value);
            float u2 = UnityEngine.Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }
    }
}
