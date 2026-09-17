using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// ハブ内蔵 IMU のモデル。
    ///
    /// ETロボコンの走行では方位角の積分ドリフトが実走行の成否を左右するので、
    /// 真値をそのまま返さず、バイアス + ホワイトノイズを積分した値を返す。
    /// ドリフトを消したい場合は RobotSpec の GyroBiasDps / GyroNoiseDps を 0 にする。
    /// </summary>
    public sealed class ImuModel
    {
        readonly RobotSpec _spec;
        readonly Rigidbody _rb;
        readonly Transform _body;

        float _yawOffsetDeg;     // reset_imu 相当
        float _integratedYawDeg; // ノイズ込みで積分したヨー角
        Vector3 _prevVelocity;
        float _biasZ;

        public float YawDeg { get; private set; }
        public float PitchDeg { get; private set; }
        public float RollDeg { get; private set; }
        public Vector3 GyroDps { get; private set; }
        public Vector3 AccelMss { get; private set; }

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
        }

        /// <summary>pup_imu / hub_imu の角度リセット相当。現在姿勢を 0 にする。</summary>
        public void ResetHeading()
        {
            _yawOffsetDeg = CourseSpace.YawToHeading(_body.eulerAngles.y);
            _integratedYawDeg = 0f;
        }

        public void Step(float dt)
        {
            // --- 角速度 (機体座標系) ---
            Vector3 omegaWorld = _rb.angularVelocity * Mathf.Rad2Deg;
            Vector3 omegaBody = _body.InverseTransformDirection(omegaWorld);

            // Unity の機体座標 (x=右, y=上, z=前) を
            // SPIKE ハブの向き (x=前, y=左, z=上) へ並べ替える
            float gx = omegaBody.z;
            float gy = -omegaBody.x;
            float gz = omegaBody.y;

            gz += _biasZ + Gaussian() * _spec.GyroNoiseDps;
            gx += Gaussian() * _spec.GyroNoiseDps;
            gy += Gaussian() * _spec.GyroNoiseDps;

            GyroDps = new Vector3(gx, gy, gz);

            // --- 姿勢 ---
            // ヨーはジャイロ積分なのでドリフトする。ピッチ・ロールは
            // 重力で補正されるため実機でもドリフトしないので真値を使う。
            _integratedYawDeg += gz * dt;
            YawDeg = _integratedYawDeg;

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
