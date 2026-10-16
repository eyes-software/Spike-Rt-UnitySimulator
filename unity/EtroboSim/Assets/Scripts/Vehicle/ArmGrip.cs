using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// アームの爪に入ったボトルを、緩く抱えておく仕掛け。
    ///
    /// 爪そのものは剛体の箱 2 本で作ってある（RobotController.BuildArmProngs）。
    /// 実機の爪はしなって少し開くので、キャリーボトル（直径 62mm）は
    /// 半径ぶんほど入ったところで止まり、そのまま抱えられて運べる。
    /// 剛体の箱だけだと、止まる位置は再現できても「抱える」がない。
    /// 爪先の角 2 点に乗っているだけなので、旋回すると転がり出てしまう。
    ///
    /// そこで、爪の口に入ったボトルを爪先へ引き寄せる弱いばねを足す。
    /// 力の上限は、
    ///
    ///   旋回でボトルに要る力 (0.05N 未満) ＜ 上限 ＜ 床の摩擦 (0.6N)
    ///
    /// に置く。こうすると旋回では外れず、走行体が下がるとボトルは床に
    /// 残って、そのまま口から外れて離れる。関節で固定しないのはこのため。
    ///
    /// 力士ボトル（直径 68mm）は口に入らないので、太さで弾く。爪先から
    /// 15mm 手前で挟まって止まり、そもそも窓にも入らないが、幅を変えて
    /// 試すときのために太さの判定も残してある。
    /// </summary>
    public sealed class ArmGrip
    {
        readonly Rigidbody _body;
        readonly Transform _robot;
        readonly RobotSpec _spec;
        BoxCollider _prongL, _prongR;

        Rigidbody _held;

        readonly Collider[] _hits = new Collider[16];

        /// <summary>抱えているボトル。持っていなければ null。</summary>
        public Rigidbody Held => _held;

        /// <summary>口に入るかを探す範囲 [mm]。爪先まわりだけ見れば足りる。</summary>
        const float SearchRadiusMm = 70f;

        /// <summary>この誤差 [mm] で上限いっぱいの力になる。</summary>
        const float FullForceErrorMm = 10f;

        /// <summary>相対速度に対する減衰 [N/(m/s)]。行き過ぎて弾むのを抑えるだけ。</summary>
        const float DampingNsPerM = 1.5f;

        /// <summary>爪先が当たっている高さかを見るときの、ボトル上下端からの余裕 [mm]。</summary>
        const float HeightMarginMm = 10f;

        public ArmGrip(Rigidbody body, Transform robot, RobotSpec spec)
        {
            _body = body;
            _robot = robot;
            _spec = spec;
        }

        public void SetProngs(BoxCollider left, BoxCollider right)
        {
            _prongL = left;
            _prongR = right;
        }

        public void Release()
        {
            if (_held != null) Debug.Log("アーム: ボトルを放しました");
            _held = null;
        }

        /// <summary>RobotController.StepArm から毎回呼ぶ。</summary>
        public void Step(float dt)
        {
            if (_spec == null || _spec.ArmGripForceN <= 0f) return;
            if (_prongL == null || _prongR == null) return;

            float mm = CourseSpace.MmToM;
            Vector3 seat = SeatWorld();
            Vector3 right = Flat(_robot.right);
            Vector3 fwd = Flat(_robot.forward);
            if (right.sqrMagnitude < 1e-6f || fwd.sqrMagnitude < 1e-6f) return;

            if (_held == null) _held = Find(seat, right, fwd);
            if (_held == null) return;

            if (!Offsets(_held, seat, right, fwd, out float dx, out float dz))
            {
                Release();
                return;
            }

            if (ShouldRelease(dx, dz, _spec.ArmGapMm, _spec.ArmGripReleaseMm))
            {
                Release();
                return;
            }

            // 爪先の中央へ引き寄せる。ボトルを倒さないよう重心に掛ける。
            // 走行体が後ろへ動いている間は弱くする（ArmGripBackwardScale）。押している間は抱えたまま、
            // 下がるときはボトルを床に残して離れやすくする。
            float scale = GripScale(Vector3.Dot(_body.velocity, fwd), _spec.ArmGripBackwardScale);
            float maxN = _spec.ArmGripForceN * scale;
            Vector3 err = -(dx * right + dz * fwd) * mm;
            Vector3 vRel = Horizontal(_held.velocity - _body.GetPointVelocity(seat));
            Vector3 f = (err * (_spec.ArmGripForceN / (FullForceErrorMm * mm))
                         - vRel * DampingNsPerM) * scale;
            f = Vector3.ClampMagnitude(f, maxN);

            _held.AddForce(f, ForceMode.Force);
            _body.AddForce(-f, ForceMode.Force);
        }

        /// <summary>走行体の前後の速さがこれより遅く後ろへ動いていたら「下がっている」とみなす [m/s]。</summary>
        const float BackingMps = 0.01f;

        /// <summary>
        /// 保持力に掛ける割合。走行体が後ろへ動いている（前後の速さが -BackingMps より遅い）間は
        /// backwardScale、それ以外（前へ進む・止まっている・その場で回る）は 1。
        /// </summary>
        public static float GripScale(float forwardSpeedMps, float backwardScale)
        {
            return forwardSpeedMps < -BackingMps ? Mathf.Clamp01(backwardScale) : 1f;
        }

        /// <summary>爪先 2 本の中央。口の入口の真ん中にあたる。</summary>
        Vector3 SeatWorld()
        {
            return (Tip(_prongL) + Tip(_prongR)) * 0.5f;
        }

        static Vector3 Tip(BoxCollider c)
        {
            return c.transform.TransformPoint(c.center + new Vector3(0f, 0f, c.size.z * 0.5f));
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 1e-8f ? Vector3.zero : v.normalized;
        }

        // 水平成分だけ取り出す（長さはそのまま）。速度に Flat を使うと長さが 1 に
        // そろってしまい、相対速度が常に 1 m/s として扱われる（減衰の項が常に
        // 1.5N になり、保持力が常に上限に張り付いていた）。
        static Vector3 Horizontal(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        Rigidbody Find(Vector3 seat, Vector3 right, Vector3 fwd)
        {
            int n = Physics.OverlapSphereNonAlloc(
                seat, SearchRadiusMm * CourseSpace.MmToM, _hits, ~0,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < n; i++)
            {
                var rb = _hits[i].attachedRigidbody;
                if (rb == null || rb == _body || rb.isKinematic) continue;
                if (!Fits(DiameterMm(_hits[i]), _spec.ArmGapMm)) continue;
                if (!Offsets(rb, seat, right, fwd, out float dx, out float dz)) continue;
                if (!InCatchWindow(dx, dz, _spec.ArmGapMm, _spec.ArmGripCatchMm)) continue;

                Debug.Log($"アーム: ボトルを抱えました ({rb.name}"
                          + $" 横 {dx:F1}mm 前後 {dz:F1}mm)");
                return rb;
            }
            return null;
        }

        /// <summary>立っているボトルの太さ [mm]。当たり判定の水平方向の広がりで見る。</summary>
        static float DiameterMm(Collider c)
        {
            Vector3 e = c.bounds.extents;
            return 2f * Mathf.Max(e.x, e.z) * CourseSpace.MToMm;
        }

        /// <summary>
        /// 爪先の中央から見たボトルの位置 [mm]。dx は右が正、dz は前が正。
        /// 爪先が胴に当たる高さに無ければ false（アームを上げている・倒れている）。
        /// </summary>
        bool Offsets(Rigidbody rb, Vector3 seat, Vector3 right, Vector3 fwd,
                     out float dx, out float dz)
        {
            dx = dz = 0f;
            if (rb == null) return false;

            var col = rb.GetComponentInChildren<Collider>();
            if (col == null) return false;

            Bounds b = col.bounds;
            float margin = HeightMarginMm * CourseSpace.MmToM;
            if (seat.y < b.min.y + margin || seat.y > b.max.y - margin) return false;

            Vector3 d = rb.position - seat;
            dx = Vector3.Dot(d, right) * CourseSpace.MToMm;
            dz = Vector3.Dot(d, fwd) * CourseSpace.MToMm;
            return true;
        }

        //
        // 判定そのものは状態を持たない。自己テストから直接呼べるようにしてある。
        //

        /// <summary>爪の間に入る太さか。</summary>
        public static bool Fits(float diameterMm, float gapMm)
        {
            return diameterMm > 0f && diameterMm <= gapMm + 2f;
        }

        /// <summary>
        /// 直径 d のボトルが幅 gap の爪に入ったとき、爪先からどれだけ入って
        /// 止まるか [mm]。爪先の角 2 点に挟まれる位置で決まる。
        /// gap が d 以上なら素通りするので、止まらないという意味で 0 を返す。
        /// </summary>
        public static float WedgeDepthMm(float diameterMm, float gapMm)
        {
            float r = diameterMm * 0.5f;
            float w = gapMm * 0.5f;
            if (w >= r) return 0f;
            return r - Mathf.Sqrt(r * r - w * w);
        }

        /// <summary>抱え始める範囲か。</summary>
        public static bool InCatchWindow(float dxMm, float dzMm, float gapMm, float catchMm)
        {
            return Mathf.Abs(dzMm) <= catchMm && Mathf.Abs(dxMm) <= gapMm * 0.5f;
        }

        /// <summary>放す範囲まで離れたか。</summary>
        public static bool ShouldRelease(float dxMm, float dzMm, float gapMm, float releaseMm)
        {
            return Mathf.Abs(dzMm) > releaseMm || Mathf.Abs(dxMm) > gapMm * 0.5f + 15f;
        }
    }
}
