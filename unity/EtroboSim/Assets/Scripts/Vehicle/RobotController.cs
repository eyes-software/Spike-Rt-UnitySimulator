using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// 二輪差動駆動の走行体本体。
    ///
    /// 車輪は「摩擦ゼロの球コライダ」として置き、垂直抗力と段差・シーソーの
    /// 乗り越えは PhysX に任せる。前後・左右の摩擦だけを自前のすべりモデルで
    /// 接地点に加える。WheelCollider を使わないのは、あれが四輪車用に
    /// チューニングされていて、この寸法・質量域だと素直に動かないため。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class RobotController : MonoBehaviour
    {
        public RobotSpec Spec = new RobotSpec();

        public WheelMotor MotorLeft { get; private set; }
        public WheelMotor MotorRight { get; private set; }
        public WheelMotor MotorArm { get; private set; }

        Rigidbody _rb;
        Transform _wheelL, _wheelR, _caster, _armPivot;

        float _wheelLoadFraction = 0.4f;  // 片輪が支える重量比
        bool _groundedL, _groundedR;

        /// <summary>コース平面上での実位置 [mm]。</summary>
        public Vector2 TruthPositionMm => CourseSpace.ToCourseMm(transform.position);
        /// <summary>コース平面上での実方位 [deg]。</summary>
        public float TruthHeadingDeg => CourseSpace.YawToHeading(transform.eulerAngles.y);
        /// <summary>機体前後方向の速度 [mm/s]。</summary>
        public float TruthSpeedMmps => Vector3.Dot(_rb.velocity, transform.forward) * CourseSpace.MToMm;

        public Rigidbody Body => _rb;
        public Transform ColorSensorMount { get; private set; }
        public Transform UltrasonicMount { get; private set; }
        public bool AnyWheelGrounded => _groundedL || _groundedR;

        /// <summary>
        /// 走行体を置くレイヤー。Unity 組み込みの "Ignore Raycast"。
        /// レイキャストからは外れるが、物理的な衝突には参加する。
        /// </summary>
        public const int RobotLayer = 2;

        // 地面判定用。走行体自身 (RobotLayer) を除いた全レイヤー。
        int _groundMask;

        /// <summary>組立図から作った表示用メッシュの絶対パス。null なら箱で描く。</summary>
        public string VisualMeshPath { get; set; }

        public void Build(RobotSpec spec, string visualMeshPath = null)
        {
            Spec = spec ?? new RobotSpec();
            VisualMeshPath = visualMeshPath;

            _rb = GetComponent<Rigidbody>();
            _rb.mass = Spec.MassKg;
            _rb.drag = 0f;
            _rb.angularDrag = 0.05f;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rb.centerOfMass = new Vector3(
                0f,
                Spec.CenterOfMassHeightMm * CourseSpace.MmToM - Spec.WheelRadiusM,
                Spec.CenterOfMassXMm * CourseSpace.MmToM);

            MotorLeft  = new WheelMotor(Spec) { Polarity = Spec.LeftMotorMirrored  ? -1 : 1 };
            MotorRight = new WheelMotor(Spec) { Polarity = Spec.RightMotorMirrored ? -1 : 1 };
            MotorArm   = new WheelMotor(Spec) { Polarity = Spec.ArmMotorMirrored   ? -1 : 1 };

            BuildColliders();
            BuildVisuals();
            ComputeLoadDistribution();

            // 走行体のコライダをレイキャスト対象から外す。
            // ここを「自分のレイヤーを除外」と書くと、既定レイヤーにいるコース床まで
            // 除外してしまい、接地もカラーセンサも一切当たらなくなる。
            SetLayerRecursively(transform, RobotLayer);
            _groundMask = Physics.DefaultRaycastLayers;
        }

        static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++)
                SetLayerRecursively(root.GetChild(i), layer);
        }

        void BuildColliders()
        {
            float r = Spec.WheelRadiusM;
            float halfTread = Spec.TreadM * 0.5f;

            // 車輪 = 摩擦ゼロの球。駆動力・横力は自前で接地点に加える。
            var slick = new PhysicMaterial("WheelSlick")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                bounciness = 0f,
                frictionCombine = PhysicMaterialCombine.Minimum,
                bounceCombine = PhysicMaterialCombine.Minimum
            };

            _wheelL = MakeSphere("WheelL", new Vector3(-halfTread, 0f, 0f), r, slick);
            _wheelR = MakeSphere("WheelR", new Vector3(halfTread, 0f, 0f), r, slick);

            // 尾輪。こちらは PhysX の摩擦をそのまま使う
            var casterMat = new PhysicMaterial("Caster")
            {
                dynamicFriction = Spec.CasterFriction,
                staticFriction = Spec.CasterFriction,
                bounciness = 0f,
                frictionCombine = PhysicMaterialCombine.Multiply
            };
            float casterR = Spec.CasterRadiusMm * CourseSpace.MmToM;
            _caster = MakeSphere("Caster",
                new Vector3(0f, casterR - r, Spec.CasterOffsetXMm * CourseSpace.MmToM),
                casterR, casterMat);

            // 車体。床には触れない高さに置く
            var bodyGo = new GameObject("BodyCollider");
            bodyGo.transform.SetParent(transform, false);
            var box = bodyGo.AddComponent<BoxCollider>();
            box.size = new Vector3(Spec.BodyWidthMm, Spec.BodyHeightMm, Spec.BodyLengthMm) * CourseSpace.MmToM;
            box.center = new Vector3(
                0f,
                (Spec.BodyHeightMm * 0.5f + 25f) * CourseSpace.MmToM - r,
                Spec.BodyOffsetXMm * CourseSpace.MmToM);
            box.material = slick;
        }

        Transform MakeSphere(string name, Vector3 localPos, float radius, PhysicMaterial mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            var sc = go.AddComponent<SphereCollider>();
            sc.radius = radius;
            sc.material = mat;
            return go.transform;
        }

        void BuildVisuals()
        {
            float r = Spec.WheelRadiusM;
            float mm = CourseSpace.MmToM;

            // 組立図から作ったメッシュがあればそれを使う。無ければ箱で描く。
            // どちらでも物理・センサは変わらない（見た目だけ）。
            var groups = LdrawMesh.Load(VisualMeshPath);
            var byName = new Dictionary<string, LdrawMesh.Group>();
            if (groups != null)
                foreach (var g in groups) byName[g.Name] = g;

            bool hasMesh = byName.Count > 0;

            if (byName.TryGetValue("body", out var bodyGroup))
                LdrawMesh.Attach(bodyGroup, transform, bodyGroup.Origin);
            else
                RobotVisuals.CreateBox(transform, "Body",
                    new Vector3(0f, (Spec.BodyHeightMm * 0.5f + 25f) * mm - r, Spec.BodyOffsetXMm * mm),
                    new Vector3(Spec.BodyWidthMm, Spec.BodyHeightMm, Spec.BodyLengthMm) * mm,
                    new Color32(240, 210, 60, 255));

            AttachWheelVisual(byName, "wheel_l", _wheelL);
            AttachWheelVisual(byName, "wheel_r", _wheelR);

            // センサ取り付け位置（レイの原点。見た目はメッシュ側に含まれる）
            ColorSensorMount = new GameObject("ColorSensor").transform;
            ColorSensorMount.SetParent(transform, false);
            ColorSensorMount.localPosition = new Vector3(
                Spec.ColorSensorYMm * mm,
                Spec.ColorSensorHeightMm * mm - r,
                Spec.ColorSensorXMm * mm);
            if (!hasMesh)
                RobotVisuals.CreateBox(ColorSensorMount, "Mesh", Vector3.zero,
                    new Vector3(24f, 20f, 26f) * mm, new Color32(40, 40, 46, 255));

            UltrasonicMount = new GameObject("UltrasonicSensor").transform;
            UltrasonicMount.SetParent(transform, false);
            UltrasonicMount.localPosition = new Vector3(
                0f,
                Spec.UltrasonicHeightMm * mm - r,
                Spec.UltrasonicXMm * mm);
            if (!hasMesh)
                RobotVisuals.CreateBox(UltrasonicMount, "Mesh", Vector3.zero,
                    new Vector3(44f, 28f, 20f) * mm, new Color32(40, 40, 46, 255));

            // アーム（見た目のみ。接触判定は Box で行う）
            _armPivot = new GameObject("ArmPivot").transform;
            _armPivot.SetParent(transform, false);

            if (byName.TryGetValue("arm", out var armGroup))
            {
                // 回転中心は組立図から取った実際のアーム軸
                _armPivot.localPosition = armGroup.Origin;
                LdrawMesh.Attach(armGroup, _armPivot, Vector3.zero);
            }
            else
            {
                _armPivot.localPosition = new Vector3(0f, 30f * mm - r, 70f * mm);
                RobotVisuals.CreateBox(_armPivot, "Mesh", new Vector3(0f, 0f, 35f * mm),
                    new Vector3(90f, 10f, 70f) * mm, new Color32(200, 200, 205, 255));
            }
        }

        void AttachWheelVisual(Dictionary<string, LdrawMesh.Group> byName, string key, Transform wheel)
        {
            float mm = CourseSpace.MmToM;
            if (byName.TryGetValue(key, out var g))
                LdrawMesh.Attach(g, wheel, Vector3.zero);
            else
                RobotVisuals.CreateWheel(wheel, "WheelMesh",
                    Spec.WheelDiameterMm * mm, Spec.WheelWidthMm * mm);
        }

        void ComputeLoadDistribution()
        {
            // 車軸を原点、尾輪を -L とし、重心 -a の静的なつり合いから片輪の負担率を出す。
            float l = Mathf.Abs(Spec.CasterOffsetXMm);
            float a = Mathf.Abs(Spec.CenterOfMassXMm);
            float casterShare = (l > 1e-3f) ? Mathf.Clamp01(a / l) : 0f;
            _wheelLoadFraction = Mathf.Clamp((1f - casterShare) * 0.5f, 0.05f, 0.5f);
        }

        public void ResetPose(Vector2 positionMm, float headingDeg)
        {
            Vector3 world = CourseSpace.ToWorld(positionMm, Spec.WheelRadiusM);
            Quaternion rotation = CourseSpace.HeadingToRotation(headingDeg);

            _rb.velocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;

            /*
             * Rigidbody を瞬間移動させるときは Rigidbody.position / rotation を使う。
             * transform への代入だけでは物理ボディに伝わらない。
             * Unity の Auto Sync Transforms は新規プロジェクトの既定が false なので、
             * transform だけ書き換えると最初の物理ステップで元の位置へ引き戻され、
             * 走行体がコースの角に現れてそのまま落下する。
             */
            _rb.position = world;
            _rb.rotation = rotation;

            // 描画とレイキャストが次の物理ステップを待たずに正しい位置を見るよう、
            // transform も同じ値に揃えておく。
            transform.SetPositionAndRotation(world, rotation);

            MotorLeft.Reset();
            MotorRight.Reset();
            MotorArm.Reset();
        }

        /// <summary>SimManager が FixedUpdate から呼ぶ。物理の駆動はここだけ。</summary>
        public void Step(float dt)
        {
            StepWheel(MotorLeft, _wheelL, -1f, dt, out _groundedL);
            StepWheel(MotorRight, _wheelR, +1f, dt, out _groundedR);
            StepArm(dt);
            UpdateWheelVisuals();
        }

        /// <summary>
        /// 静止摩擦でくっついたままとみなす接地点の速度 [m/s]。
        /// 指令 0 のときのじわじわ (数 mm/s 未満) は止めたいが、
        /// 走っているときは必ず転がらせる。
        /// </summary>
        const float StictionContactMps = 0.005f;

        void StepWheel(WheelMotor motor, Transform wheel, float side, float dt, out bool grounded)
        {
            float r = Spec.WheelRadiusM;
            Vector3 center = wheel.position;

            // 接地判定。球コライダの半径ぶん下を見る
            grounded = Physics.Raycast(center, Vector3.down, out RaycastHit hit,
                                       r * 1.35f, _groundMask, QueryTriggerInteraction.Ignore);

            float motorTorque = motor.ComputeTorque();

            if (!grounded)
            {
                // 浮いている間は路面反力なし
                motor.Integrate(motorTorque, 0f, dt);
                WatchStuckWheel(motor, side, false, false, 0f, motorTorque, 0f);
                return;
            }

            Vector3 normal = hit.normal;
            Vector3 contact = hit.point;

            // 車輪の進行方向を接地面に投影する
            Vector3 forward = Vector3.ProjectOnPlane(transform.forward, normal).normalized;
            Vector3 right = Vector3.Cross(normal, forward).normalized;

            Vector3 vContact = _rb.GetPointVelocity(contact);
            float vLong = Vector3.Dot(vContact, forward);
            float vLat = Vector3.Dot(vContact, right);

            // 垂直抗力。斜面では法線と重力のなす角ぶん減る
            float normalLoad = Spec.MassKg * Physics.gravity.magnitude
                               * _wheelLoadFraction
                               * Mathf.Max(0.1f, Vector3.Dot(normal, Vector3.up));

            float maxTraction = Spec.TractionFriction * normalLoad;

            // 横: 横滑り速度を 1 ステップで消す力を、摩擦円で頭打ちにする
            float maxLateral = Spec.LateralFriction * normalLoad;
            float fLatDesired = -vLat * (Spec.MassKg * _wheelLoadFraction) / Mathf.Max(dt, 1e-4f);
            float fLat = Mathf.Clamp(fLatDesired, -maxLateral, maxLateral);

            // 静止摩擦。止まっている車輪は、減速機の抵抗を超える力が来るまで回り出さない。
            //
            // クーロン摩擦は数値安定のため ω=0 付近で線形に落としてあり、
            // ω=0 ではゼロになる。つまり今のモデルはどんなに小さな力でも
            // 車輪が回り出す。尾輪（PhysX の摩擦）から来る微小な力がこれで
            // 積み上がり、モータ指令 0 のまま走行体がじわじわ回り続けていた。
            // くっついている間は、接地点の速度を消す力だけを路面から受ける。
            //
            // 入る条件は「モータが回そうとしていないこと」だけで見る。
            // 以前は打ち消す力 fStick を条件に混ぜていたが、fStick は車体の
            // 速度で決まるので、走っている最中でも条件が成り立ってしまう。
            // 一度入ると SetSolvedOmega(0) で ω を 0 に戻すため次も入る、と
            // 自己保持して車輪が二度と回らなくなっていた。それでも車体には
            // 力を加えるので走りはするが、**エンコーダが 0 のまま**になり、
            // 走行プログラムの距離・座標がまったく進まなくなっていた
            // （ライントレースはカラーセンサだけで成立するので走れてしまう）。
            // 接地点が動いていないことも条件に入れる。車体が進んでいるのに
            // 車輪を止めてしまうと、転がるはずの車輪が回らずエンコーダが
            // 止まる。駆動トルクだけで見ていたときは、ライントレースで
            // 内側の車輪の出力が小さくなったところで捕まっていた。
            if (Mathf.Abs(motor.Omega) < WheelMotor.StictionOmega
                && Mathf.Abs(vLong) < StictionContactMps
                && Mathf.Abs(motor.DriveTorque()) <= motor.StictionTorqueNm)
            {
                // 路面が返せるのは静止摩擦ぶんまで。摩擦円と、車輪を回さずに
                // 支えられるトルクの両方で頭打ちにする。
                float fStickMax = Mathf.Min(maxTraction, motor.StictionTorqueNm / Mathf.Max(r, 1e-4f));
                float fStick = Mathf.Clamp(-vLong * (Spec.MassKg * _wheelLoadFraction)
                                           / Mathf.Max(dt, 1e-4f), -fStickMax, fStickMax);

                _rb.AddForceAtPosition(forward * fStick + right * fLat, contact, ForceMode.Force);
                motor.SetSolvedOmega(0f, dt);
                WatchStuckWheel(motor, side, true, true, vLong, motor.DriveTorque(), 0f);
                return;
            }

            // 前後: 車輪周速と接地点速度の差 (すべり速度) に比例した駆動力。
            //
            // 車輪の回転と駆動力は互いに効き合うので、陽解法で順に計算すると
            // k・r^2・dt / I が 1 を超えたところで 1 ステップごとに符号が反転し、
            // 振動が残って指令 0 でも走行体が動いてしまう。
            // 先に車輪の角速度を陰解法で解いてから、その値で駆動力を出す。
            float k = Spec.SlipStiffness;
            float inertia = Mathf.Max(1e-6f, Spec.WheelInertia);
            float omegaNext = (motor.Omega + (motorTorque + k * vLong * r) * dt / inertia)
                              / (1f + k * r * r * dt / inertia);

            float slip = omegaNext * r - vLong;
            float fLong = Mathf.Clamp(k * slip, -maxTraction, maxTraction);

            // 摩擦円で頭打ちになったときは陰解法の前提が崩れるので、
            // 頭打ちした力でふつうに積分し直す（力は有界なので発散しない）。
            if (Mathf.Abs(k * slip) > maxTraction)
                omegaNext = motor.Omega + (motorTorque - fLong * r) * dt / inertia;

            // 転がり抵抗。接地点の進行方向と逆に、垂直抗力に比例した力。
            // 1 ステップで速度を反転させない大きさに抑える（抑えないと震える）。
            float fRoll = 0f;
            if (Mathf.Abs(vLong) > 1e-5f)
            {
                float cap = Mathf.Abs(vLong) * (Spec.MassKg * _wheelLoadFraction) / Mathf.Max(dt, 1e-4f);
                fRoll = -Mathf.Sign(vLong) * Mathf.Min(Spec.RollingResistance * normalLoad, cap);
            }

            _rb.AddForceAtPosition(forward * (fLong + fRoll) + right * fLat, contact, ForceMode.Force);

            motor.SetSolvedOmega(omegaNext, dt);
            WatchStuckWheel(motor, side, true, false, vLong, motor.DriveTorque(), omegaNext);
        }

        /*
         * 「接地している車輪が止まったまま、車体だけ動いている」のを見張る。
         *
         * この状態になるとエンコーダが進まず、走行プログラムの距離・座標が
         * 凍る。ライントレースはカラーセンサだけで成立してしまうので、走りは
         * 一見成立し、症状が見えない。実際これで長く嵌まったので、起きた
         * ことと、そのときの数字をその場で出す。
         */
        const float StuckOmegaLimit   = 0.5f;    // これ未満なら「止まっている」[rad/s]
        const float StuckBodyMmps     = 50f;     // これ以上なら「車体は動いている」
        const float StuckHoldSeconds  = 0.2f;    // これだけ続いたら報告する
        const float StuckReportEvery  = 2.0f;    // 続く間の報告間隔 [s]

        float _stuckSinceL = -1f, _stuckSinceR = -1f;
        float _stuckReportedL = -99f, _stuckReportedR = -99f;

        void WatchStuckWheel(WheelMotor motor, float side, bool grounded, bool stiction,
                             float vLong, float driveTorque, float omegaNext)
        {
            bool left = side < 0f;
            float bodyMmps = _rb.velocity.magnitude * CourseSpace.MToMm;
            bool stuck = grounded && Mathf.Abs(motor.Omega) < StuckOmegaLimit
                         && bodyMmps > StuckBodyMmps;

            float now = Time.realtimeSinceStartup;
            float since = left ? _stuckSinceL : _stuckSinceR;

            if (!stuck)
            {
                if (left) _stuckSinceL = -1f; else _stuckSinceR = -1f;
                return;
            }

            if (since < 0f)
            {
                if (left) _stuckSinceL = now; else _stuckSinceR = now;
                return;
            }

            if (now - since < StuckHoldSeconds) return;

            float reported = left ? _stuckReportedL : _stuckReportedR;
            if (now - reported < StuckReportEvery) return;
            if (left) _stuckReportedL = now; else _stuckReportedR = now;

            Debug.LogWarning(
                $"[Etsim] {(left ? "左" : "右")}車輪が回っていません"
                + $"（{now - since:F1} 秒）。エンコーダが進まないので走行プログラムの"
                + $"距離・座標が凍ります。"
                + $" 経路={(stiction ? "静止摩擦" : "通常")}"
                + $" ω={motor.Omega:F3}rad/s 次ω={omegaNext:F3}"
                + $" 接地点速度={vLong * 1000f:F0}mm/s 車体速度={bodyMmps:F0}mm/s"
                + $" 駆動トルク={driveTorque:F4}Nm 静止摩擦={motor.StictionTorqueNm:F4}Nm"
                + $" 角度={motor.AngleDeg:F0}deg");
        }

        void StepArm(float dt)
        {
            // アームは接触物を持たない想定なので、無負荷のモータとして回すだけ
            float torque = MotorArm.ComputeTorque();
            MotorArm.Integrate(torque, 0f, dt);

            if (_armPivot != null)
                _armPivot.localRotation = Quaternion.Euler(-MotorArm.AngleDeg, 0f, 0f);
        }

        void UpdateWheelVisuals()
        {
            if (_wheelL != null) _wheelL.localRotation = Quaternion.Euler(MotorLeft.WheelAngleDeg, 0f, 0f);
            if (_wheelR != null) _wheelR.localRotation = Quaternion.Euler(MotorRight.WheelAngleDeg, 0f, 0f);
        }
    }
}
