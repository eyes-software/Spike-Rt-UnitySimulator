using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// ハブ内蔵 IMU のモデル。
    ///
    /// ETロボコンの走行では方位角の積分ドリフトが実走行の成否を左右するので、
    /// 真値をそのまま返さず、バイアス + ホワイトノイズの入ったジャイロから方位を出す。
    /// ドリフトを消したい場合は RobotSpec の GyroBiasDps / GyroNoiseDps を 0 にする。
    ///
    /// 方位の出し方は実機に合わせてある（RobotSpec.ImuHeading3D = 1）。
    /// 実機の hub_imu_get_heading（etrobocon/spike-rt-RasPike-ART）は
    /// pbio_imu_get_heading(PBIO_IMU_HEADING_TYPE_3D) で、pbio は
    ///   ・3 軸のジャイロを積み足して姿勢（クォータニオン）を持ち、止まっている間は重力の向きに寄せる
    ///   ・前向きの軸を水平な面に投げ落とした向きを方位とし、±180 度をまたいだら回った回数を数える
    /// ので、車体が傾いていても、水平に回った分だけを数える。
    /// 車体の上向きの軸まわりの回転だけを積み足す（ImuHeading3D = 0、従来）と、ボトルを持って
    /// 前に傾いたまま曲がったときに、回った量を少なめに数える（シミュレータだけのずれになる）。
    ///
    /// また pbio は、止まっていてモータが空転しているときにジャイロの偏りを測って差し引く
    /// （RobotSpec.ImuBiasCalibration = 1）。ここでは「止まっている」を真値の動きで見る。
    /// </summary>
    public sealed class ImuModel
    {
        readonly RobotSpec _spec;
        readonly Rigidbody _rb;
        readonly Transform _body;

        float _yawOffsetDeg;     // reset_imu 相当（真値の表示用）
        float _integratedYawDeg; // 従来の出し方：上向きの軸まわりのジャイロを積み足したヨー角
        Vector3 _prevVelocity;
        float _biasZ;

        // ---- 実機と同じ出し方（3D の方位）----
        Quaternion _att = Quaternion.identity;   // 推定した姿勢（機体 → ワールド）
        float _headingRotations;                 // 方位が ±180 度をまたいだ回数
        float _prevProjection;                   // 前回の、水平な面に投げ落とした向き [deg]
        float _headingOffset3D;                  // リセットしたときの方位
        bool _attValid;

        // ---- 確かめ用：ノイズも偏りもない理想のジャイロで積み足した姿勢 ----
        // 本当の姿勢との差は、角速度に現れない回転（物理エンジンが食い込みを押し戻すときなど）。
        Quaternion _perfect = Quaternion.identity;
        float _perfectLogged;     // 最後にログに出した差 [deg]

        // ---- ハブがする偏りの補正 ----
        Vector3 _biasEst;          // 推定した偏り（機体座標、Unity の軸）[deg/s]
        float _stillSeconds;       // 止まり続けている時間
        float _biasWeightSeconds;  // これまでに偏りを測った時間（長いほど新しい値を少しずつ混ぜる）

        const float StillAngularDps = 0.5f;   // 止まっているとみなす角速度（真値）
        const float StillSpeedMmPerSec = 2f;  // 同じく速さ
        const float StillSettleSeconds = 0.5f;// 止まってからこの時間たったら偏りを測る
        const float GravityGain = 0.5f;       // 止まっている間、傾きを重力の向きへ寄せる速さ [1/s]

        public float YawDeg { get; private set; }
        public float PitchDeg { get; private set; }
        public float RollDeg { get; private set; }
        public Vector3 GyroDps { get; private set; }
        public Vector3 AccelMss { get; private set; }

        /// <summary>ハブが推定している偏り（上向きの軸まわり）[deg/s]。確かめ用。</summary>
        public float BiasEstimateZDps => _biasEst.y;
        /// <summary>入れている偏り（上向きの軸まわり）[deg/s]。確かめ用。</summary>
        public float BiasZDps => _biasZ;

        bool Heading3D => _spec.ImuHeading3D > 0.5f;
        bool BiasCalibration => _spec.ImuBiasCalibration > 0.5f;

        public ImuModel(RobotSpec spec, Rigidbody rb)
        {
            _spec = spec;
            _rb = rb;
            _body = rb.transform;
            RandomizeBias();
        }

        void RandomizeBias()
        {
            // 電源投入ごとにバイアスが変わる実機の挙動に合わせる
            _biasZ = UnityEngine.Random.Range(-1f, 1f) * _spec.GyroBiasDps;
        }

        public void Reset()
        {
            _yawOffsetDeg = 0f;
            _integratedYawDeg = 0f;
            _prevVelocity = Vector3.zero;
            RandomizeBias();
            YawDeg = PitchDeg = RollDeg = 0f;
            GyroDps = Vector3.zero;
            AccelMss = Vector3.zero;
            _yawOffsetDeg = CourseSpace.YawToHeading(_body.eulerAngles.y);
            // 電源を入れ直したのと同じ：偏りの推定も初めから
            _biasEst = Vector3.zero;
            _stillSeconds = 0f;
            _biasWeightSeconds = 0f;
            ResetAttitude();
        }

        /// <summary>pup_imu / hub_imu の角度リセット相当。現在姿勢を 0 にする。</summary>
        public void ResetHeading()
        {
            _yawOffsetDeg = CourseSpace.YawToHeading(_body.eulerAngles.y);
            _integratedYawDeg = 0f;
            // 姿勢そのものは持ち続け、今の方位を 0 にする（pbio_imu_set_heading(0) と同じ）
            if (!_attValid) ResetAttitude();
            _headingOffset3D = UnwrappedHeading();
            YawDeg = 0f;
        }

        /// <summary>推定する姿勢を真値の姿勢で始める（電源投入時、ハブは重力で傾きを知っている）。</summary>
        void ResetAttitude()
        {
            _att = _body.rotation;
            _perfect = _body.rotation;
            _perfectLogged = 0f;
            _headingRotations = 0f;
            _prevProjection = Projection(_att);
            _headingOffset3D = _prevProjection;
            _attValid = true;
        }

        public void Step(float dt)
        {
            // --- 角速度 (機体座標系、Unity の軸：x=右, y=上, z=前) ---
            Vector3 omegaWorld = _rb.angularVelocity * Mathf.Rad2Deg;
            Vector3 omegaBody = _body.InverseTransformDirection(omegaWorld);

            // ジャイロの読み（偏りは上向きの軸だけ、ノイズは 3 軸）
            Vector3 meas = new Vector3(
                omegaBody.x + Gaussian() * _spec.GyroNoiseDps,
                omegaBody.y + _biasZ + Gaussian() * _spec.GyroNoiseDps,
                omegaBody.z + Gaussian() * _spec.GyroNoiseDps);

            // --- ハブがする偏りの補正：止まっている間の読みの平均を偏りとし、少しずつ更新する ---
            bool still = omegaWorld.magnitude < StillAngularDps
                         && _rb.velocity.magnitude * CourseSpace.MToMm < StillSpeedMmPerSec;
            _stillSeconds = still ? _stillSeconds + dt : 0f;
            if (BiasCalibration && _stillSeconds > StillSettleSeconds)
            {
                _biasWeightSeconds += dt;
                float k = dt / Mathf.Max(_biasWeightSeconds, 1f);   // 測った時間が長いほど、新しい値は少しずつ
                _biasEst += (meas - _biasEst) * k;
            }
            Vector3 cal = BiasCalibration ? meas - _biasEst : meas;

            // 確かめ用：理想のジャイロ（真の角速度そのまま）で積み足した姿勢と、本当の姿勢の方位の差
            float pr = omegaBody.magnitude;
            if (pr > 1e-6f) _perfect = Normalize(_perfect * Quaternion.AngleAxis(pr * dt, omegaBody / pr));
            float hidden = Mathf.DeltaAngle(Projection(_perfect), Projection(_body.rotation));
            if (Mathf.Abs(hidden - _perfectLogged) > 0.2f)
            {
                Vector2 p = CourseSpace.ToCourseMm(_body.position);
                Debug.Log($"[Etsim] ジャイロに映らない回転 累計 {hidden:+0.00;-0.00}°（{hidden - _perfectLogged:+0.00;-0.00}°）" +
                          $" 位置 ({p.x:F0}, {p.y:F0})mm 方位 {CourseSpace.YawToHeading(_body.eulerAngles.y):F1}°" +
                          $" 角速度 {omegaWorld.magnitude:F1}deg/s");
                _perfectLogged = hidden;
            }

            // Unity の機体座標を SPIKE ハブの向き (x=前, y=左, z=上) へ並べ替える。
            // Unity は左手系、ハブは右手系なので、回転の向き（角速度）は 3 軸とも符号が逆になる
            // （加速度のようなふつうのベクトルは並べ替えだけでよい）。実機のログで、右へ曲がると
            // Z が負になる（方位の増え方と逆）ことを確かめた。以前は符号を変えておらず、Z が実機と逆だった。
            GyroDps = new Vector3(-cal.z, cal.x, -cal.y);

            // --- 方位 ---
            // 従来の出し方：上向きの軸まわりの回転だけを積み足す
            _integratedYawDeg += cal.y * dt;

            // 実機の出し方：3 軸で姿勢を積み足し、前向きの軸を水平な面に投げ落とした向き
            float rate = cal.magnitude;
            if (rate > 1e-6f) _att = _att * Quaternion.AngleAxis(rate * dt, cal / rate);
            if (still)
            {
                // 止まっている間は、加速度で見た重力の向きに傾きを寄せる（ヨーは変えない）。
                // 加速度が示すのは本当の傾きなので、推定した上向きの軸を「本当の上向きの軸」へ寄せる
                // （コースの真上へ寄せると、ボトルを持って傾いたまま止まったときに傾きを 0 と思い込み、
                //   その後に曲がると方位を大きく間違える）。
                Vector3 upEst = _att * Vector3.up;
                Vector3 upTrue = _body.rotation * Vector3.up;
                Quaternion toGravity = Quaternion.FromToRotation(upEst, upTrue);
                _att = Quaternion.Slerp(Quaternion.identity, toGravity, Mathf.Clamp01(GravityGain * dt)) * _att;
            }
            _att = Normalize(_att);
            float heading3D = UnwrappedHeading() - _headingOffset3D;

            YawDeg = Heading3D ? heading3D : _integratedYawDeg;

            Vector3 e = _body.eulerAngles;
            PitchDeg = NormalizeDeg(-(e.x));
            RollDeg = NormalizeDeg(e.z);

            // --- 加速度 (重力込み、機体座標系) ---
            Vector3 accelWorld = (_rb.velocity - _prevVelocity) / Mathf.Max(dt, 1e-5f);
            _prevVelocity = _rb.velocity;
            Vector3 specific = accelWorld - Physics.gravity; // 比力: 静止時は +1G を読む
            Vector3 aBody = _body.InverseTransformDirection(specific);

            AccelMss = new Vector3(
                aBody.z + Gaussian() * _spec.AccelNoise,
                -aBody.x + Gaussian() * _spec.AccelNoise,
                aBody.y + Gaussian() * _spec.AccelNoise);
        }

        /// <summary>前向きの軸を水平な面に投げ落とした向き [deg]（右回りが正。Unity のヨーと同じ向き）。</summary>
        static float Projection(Quaternion att)
        {
            Vector3 f = att * Vector3.forward;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        /// <summary>±180 度をまたいだ回数を数えて続けた方位 [deg]。</summary>
        float UnwrappedHeading()
        {
            float p = Projection(_att);
            float d = p - _prevProjection;
            if (d < -180f) _headingRotations += 1f;
            else if (d > 180f) _headingRotations -= 1f;
            _prevProjection = p;
            return _headingRotations * 360f + p;
        }

        static Quaternion Normalize(Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (n < 1e-9f) return Quaternion.identity;
            return new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
        }

        /// <summary>真値の方位 [deg]。リセット基準からの相対角。デバッグ表示用。</summary>
        public float TrueYawDeg => NormalizeDeg(CourseSpace.YawToHeading(_body.eulerAngles.y) - _yawOffsetDeg);

        static float NormalizeDeg(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg < -180f) deg += 360f;
            return deg;
        }

        static float Gaussian()
        {
            float u1 = Mathf.Max(1e-6f, UnityEngine.Random.value);
            float u2 = UnityEngine.Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }
    }
}
