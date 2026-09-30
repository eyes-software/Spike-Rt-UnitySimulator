using System;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// SPIKE 走行体の寸法・特性。すべて実測値に差し替える前提のパラメータ群で、
    /// ここを合わせ込むほどシミュレータのチューニング結果が実機で再現する。
    /// StreamingAssets/robot_spike.json があればそれで上書きされる。
    /// </summary>
    [Serializable]
    public sealed class RobotSpec
    {
        [Header("寸法 [mm]")]
        public float WheelDiameterMm = 56f;    // SPIKE 大ホイール
        public float WheelWidthMm = 14f;
        public float TreadMm = 120f;           // 左右車輪の中心間距離
        public float BodyLengthMm = 180f;
        public float BodyWidthMm = 110f;
        public float BodyHeightMm = 90f;

        /// <summary>車軸中心から見た本体中心のオフセット [mm] (+ が前方)。</summary>
        public float BodyOffsetXMm = -20f;

        [Header("質量 [kg]")]
        public float MassKg = 0.65f;
        /// <summary>車軸中心から見た重心位置 [mm] (+ が前方)。</summary>
        public float CenterOfMassXMm = -15f;
        /// <summary>床面から見た重心高さ [mm]。</summary>
        public float CenterOfMassHeightMm = 45f;

        [Header("尾輪 / キャスタ")]
        public float CasterOffsetXMm = -75f;   // 車軸中心から後方
        public float CasterRadiusMm = 11f;
        public float CasterFriction = 0.06f;

        [Header("モータ特性 (SPIKE ラージアンギュラーモータ)")]
        /// <summary>出力 100% での無負荷回転数 [rpm]。</summary>
        public float NoLoadRpm = 175f;
        /// <summary>出力 100% での停動トルク [N*m]。</summary>
        public float StallTorqueNm = 0.35f;
        /// <summary>車輪 + 減速後ロータの等価慣性 [kg*m^2]。</summary>
        public float WheelInertia = 2.0e-4f;
        /// <summary>粘性摩擦 [N*m/(rad/s)]。</summary>
        public float ViscousDamping = 1.2e-4f;
        /// <summary>クーロン摩擦 [N*m]。無指令時に惰行が止まる強さ。</summary>
        public float CoulombFrictionNm = 2.0e-3f;
        /// <summary>ブレーキ時の追加粘性 [N*m/(rad/s)]。</summary>
        public float BrakeDamping = 6.0e-3f;
        /// <summary>出力の不感帯 [%]。これ以下の指令では回らない。</summary>
        public float DeadbandPercent = 3f;

        [Header("モータの取り付け向き")]
        /// <summary>
        /// 左モータが鏡像に付いているか。true だと「モータ軸の正回転 = 後退」。
        /// 実機では左右のモータが向かい合わせに付くので、通常どちらか一方が true。
        /// アプリ側の pup_motor_setup() の方向指定と辻褄が合っていないと、
        /// 走行体が前に進まずその場で回る。
        /// </summary>
        public bool LeftMotorMirrored = true;
        public bool RightMotorMirrored = false;
        public bool ArmMotorMirrored = false;

        /// <summary>
        /// アームの爪 2 本の内側の間隔 [mm]。0 で爪の当たり判定を作らない。
        ///
        /// 組立図どおりは 56mm。実機では爪がしなって開くが、直径 62mm の
        /// キャリーボトルが通り抜けるほどは開かず、半径ぶん（30mm ほど）
        /// 入ったところで止まる。爪はしならない剛体で作るので、その
        /// 「止まる深さ」が合う幅を入れる。
        ///
        /// 半径 R のボトルが幅 W の隙間に入ると、爪先の角で挟まって
        ///   深さ = R - sqrt(R^2 - (W/2)^2)
        /// で止まる。R=31, W=61 で 25mm、W=62 で素通り。
        /// </summary>
        public float ArmGapMm = 61.0f;

        /// <summary>爪 1 本の厚み [mm]。公式 LDraw モデルの実測。</summary>
        public float ArmProngThicknessMm = 8.0f;

        /// <summary>
        /// カラーセンサがアーム側に付いているか。
        ///
        /// Re-HackS は公式 LDraw でカラーセンサがアームの部分品になっていて、
        /// アームを上げるとセンサが前を向く（下向き → 前向き）。ボトルの色を
        /// 読むのはこの姿勢。false だと車体に固定で、いつも真下を向く。
        /// </summary>
        public bool ColorSensorOnArm = false;

        /// <summary>
        /// アームモータ 1 回転あたり、アームが何回転するかの逆数（減速比）。
        /// モータの角度をこれで割ったものがアームの角度になる。
        /// LDraw からは歯車比が読めないので、1 以外にするときは実機で測ること。
        /// </summary>
        public float ArmGearRatio = 1.0f;

        /// <summary>
        /// アームが動ける範囲 [deg]（アームの角度。開始時の姿勢が 0）。
        /// 実機はストッパや床に当たってそれ以上回らないが、ここではアームを
        /// 無負荷のモータとして回すので、指定しないとどこまでも回る。
        /// 既定は制限なし。
        /// </summary>
        public float ArmMinDeg = float.NegativeInfinity;
        public float ArmMaxDeg = float.PositiveInfinity;

        /// <summary>
        /// 爪の間に入ったボトルを引き寄せておく力の上限 [N]。0 で保持しない。
        ///
        /// 実機では爪がボトルを軽く抱えるので、旋回しても転がり出ない。
        /// 剛体の爪だけだと爪先の 2 点で支えるだけになり、少し向きを変えた
        /// だけで外れてしまうので、そのぶんをばねで補う。
        ///
        /// ただし「バックすれば置いていける」程度の緩さにする。ボトルは
        /// 105g・床の摩擦係数 0.6 なので、動かすには 0.6N ほど要る。
        /// それより十分小さくしておけば、走行体が下がってもボトルは床に
        /// 残り、窓から外れて自然に離れる。旋回で必要な力は 0.05N 未満。
        /// </summary>
        public float ArmGripForceN = 0.25f;

        /// <summary>
        /// 保持を始める位置 [mm]。爪先の面からボトルの軸までの前後距離。
        ///
        /// キャリーボトルは爪先の 4.5mm 手前で止まる（実測）。力士ボトルは
        /// 太いぶん手前の 13.4mm で止まるので、そこには届かない値にしておく。
        /// 太さでも弾いているので二重の歯止めになる。
        /// </summary>
        public float ArmGripCatchMm = 12f;

        /// <summary>ここまで爪先から離れたら放す [mm]。</summary>
        public float ArmGripReleaseMm = 45f;

        [Header("タイヤ - 路面")]
        public float TractionFriction = 0.85f;      // 前後方向の静止摩擦係数
        public float LateralFriction = 1.10f;       // 横滑りに対する摩擦係数
        /// <summary>すべり速度に対する駆動力の立ち上がり [N/(m/s)]。</summary>
        public float SlipStiffness = 220f;

        /// <summary>
        /// 転がり抵抗係数。垂直抗力に掛けた力が、接地点の進行方向と逆に働く。
        ///
        /// これが無いと、指令を切ったあとの走行体が止まらない。車輪は摩擦ゼロの
        /// 球で、駆動力はすべり速度からしか出さないので、いったん転がり出すと
        /// 車輪が路面速度に合ってしまい、減速させるものが何も残らない。
        /// ゴムタイヤと印刷マットなら 0.01〜0.02 くらい。
        /// </summary>
        public float RollingResistance = 0.015f;

        [Header("カラーセンサ")]
        /// <summary>車軸中心からの前方オフセット [mm]。</summary>
        public float ColorSensorXMm = 95f;
        public float ColorSensorYMm = 0f;
        /// <summary>床面からの高さ [mm]。</summary>
        public float ColorSensorHeightMm = 10f;
        /// <summary>計測スポット直径 [mm]。高さが上がるほど広がる。</summary>
        public float ColorSensorSpotMm = 12f;

        /// <summary>
        /// 色を読める距離 [mm]。データシートの "Optimal reading distance: 16 mm"
        /// （Technic Color Sensor。色・反射光量とも同じ）。
        /// ライントレースは床から 8.4mm なので常に範囲内。前を向かせて物の色を
        /// 読むときは、この距離まで近づかないと何も返らない。
        /// </summary>
        public float ColorSensorRangeMm = 16f;

        /// <summary>
        /// 測定の更新周期 [Hz]。データシートの "Sensor sample rate: 100 Hz"。
        /// 走行体プログラムの制御周期（4ms = 250Hz）より遅いので、
        /// ライントレースは同じ反射光量を 2〜3 回続けて読むことになる。
        /// 0 以下で毎ステップ更新（従来の挙動）。
        /// </summary>
        public float ColorSensorUpdateHz = 100f;
        /// <summary>反射光量に乗るノイズの標準偏差 [%]。</summary>
        public float ColorSensorNoise = 0.6f;
        /// <summary>1 次遅れの時定数 [s]。実機センサの応答遅れ。</summary>
        public float ColorSensorTimeConstant = 0.004f;
        /// <summary>真っ黒を読んだときの反射光量。実機のキャリブレーション値に合わせる。</summary>
        public float ReflectionBlack = 6f;
        /// <summary>真っ白を読んだときの反射光量。</summary>
        public float ReflectionWhite = 94f;

        [Header("超音波センサ")]
        public float UltrasonicXMm = 90f;
        public float UltrasonicHeightMm = 60f;
        public float UltrasonicMinMm = 50f;
        public float UltrasonicMaxMm = 2000f;

        /// <summary>
        /// ビームの広がり半角 [deg]。データシートの "Entrance angle: +/- 35 degrees"。
        /// 実機と同じで、正面から外れた物も拾う。
        /// </summary>
        public float UltrasonicBeamDeg = 35f;

        /// <summary>
        /// 距離の誤差 [mm]（1σ）。データシートは "50-2000 mm +/- 20 mm" で、
        /// これを 3σ とみなした値。出力の刻みは 1mm（丸めで入れている）。
        /// </summary>
        public float UltrasonicNoiseMm = 6.7f;

        /// <summary>
        /// 測定の更新周期 [Hz]。データシートの "Sensor sample rate: 100 Hz"。
        /// 走行体プログラムの制御周期（4ms）より遅いので、同じ値が数回続く。
        /// 0 以下で毎ステップ更新（従来の挙動）。
        /// </summary>
        public float UltrasonicUpdateHz = 100f;
        /// <summary>
        /// 反射が返ってくる入射角の上限 [deg]。面の法線からこれ以上傾いて当たると
        /// 音波は鏡のように向こうへ反射して戻らないので、当たっていないものとして扱う。
        ///
        /// これが無いと、広がったビームの下端が床に当たって常に一定の距離
        /// （取り付け高さ / tan(ビーム半角)）を返してしまう。
        /// </summary>
        /// ビーム半角とは対で決める値。半角 35 度だと下端のレイは水平から 35 度
        /// 下を向き、床の法線から見た入射角は 55 度になるので、上限はそれ未満で
        /// なければ床を拾い続ける。
        ///
        /// 35 度にしてあるのは、斜めの壁が返らなくなる角度を以前（半角 12 度 +
        /// 上限 60 度 = 72 度）と揃えるため。ビームを広げたぶん上限を絞らないと、
        /// 斜めの面まで拾うようになってしまう。
        public float UltrasonicMaxIncidenceDeg = 35f;

        [Header("回転センサ（モータ内蔵）")]
        /// <summary>
        /// 1 カウントの角度 [deg]。データシートの "Resolution: 360 counts per
        /// revolution"。走行体プログラムへ返す角度はこの刻みに丸める。
        /// 0 以下で刻まない（従来の挙動）。
        /// </summary>
        public float EncoderStepDeg = 1f;

        /// <summary>
        /// 回転センサの誤差 [deg]。データシートの "Accuracy: <= +/- 3 degrees"、
        /// 中身は「センサの公差 ＋ ギアの遊び」。
        ///
        /// 白色雑音として足すのは誤り。カウント自体は再現性があって、効くのは
        /// **向きを変えたとき**に遊びのぶんだけカウントが動かないこと。
        /// ここではバックラッシュとして入れる（毎回の誤差は積み上がらない）。
        /// 0 以下で遊び無し。
        /// </summary>
        public float EncoderSlackDeg = 3f;

        /// <summary>
        /// 回転センサの更新周期 [Hz]。データシートの "Update rate: 100 Hz"。
        /// 0 以下で毎ステップ更新（従来の挙動）。
        /// </summary>
        public float EncoderUpdateHz = 100f;

        [Header("IMU")]
        public float GyroNoiseDps = 0.05f;
        /// <summary>ジャイロのドリフト [deg/s]。積分誤差の再現用。</summary>
        public float GyroBiasDps = 0.02f;
        public float AccelNoise = 0.02f;
        /// <summary>
        /// 方位を実機（spike-rt の hub_imu_get_heading = pbio の 3D の方位）と同じく、3 軸のジャイロで
        /// 姿勢を積み足し、前向きの軸を水平な面に投げ落とした向きで出す（1）。0 なら車体の上向きの軸まわりの
        /// 回転だけを積み足す（従来。車体が傾いたまま曲がると、回った量を少なめに数える）。
        /// </summary>
        public float ImuHeading3D = 1f;
        /// <summary>
        /// 止まっている間にハブがジャイロの偏りを測って差し引く（pbio と同じ。1）。0 なら偏りを入れっぱなし（従来）。
        /// </summary>
        public float ImuBiasCalibration = 1f;

        [Header("電源")]
        public int BatteryMv = 8000;

        /// <summary>
        /// 組立図から作った表示用メッシュ (.etmesh)。空なら箱で描く。
        /// tools/ldraw_to_mesh.py で作る。物理には一切影響しない。
        /// </summary>
        [Header("見た目")]
        public string VisualMeshFile = "";

        public float WheelRadiusM => WheelDiameterMm * 0.5f * CourseSpace.MmToM;
        public float TreadM => TreadMm * CourseSpace.MmToM;

        /// <summary>出力 100% での無負荷角速度 [rad/s]。</summary>
        public float NoLoadOmega => NoLoadRpm * 2f * Mathf.PI / 60f;

        public static RobotSpec FromJson(string json)
        {
            var root = JsonValue.Parse(json);
            var s = new RobotSpec();

            void F(string key, ref float target) { if (root.Has(key)) target = root[key].AsFloat(target); }

            F("wheel_diameter_mm", ref s.WheelDiameterMm);
            F("wheel_width_mm", ref s.WheelWidthMm);
            F("tread_mm", ref s.TreadMm);
            F("body_length_mm", ref s.BodyLengthMm);
            F("body_width_mm", ref s.BodyWidthMm);
            F("body_height_mm", ref s.BodyHeightMm);
            F("body_offset_x_mm", ref s.BodyOffsetXMm);

            F("mass_kg", ref s.MassKg);
            F("center_of_mass_x_mm", ref s.CenterOfMassXMm);
            F("center_of_mass_height_mm", ref s.CenterOfMassHeightMm);

            F("caster_offset_x_mm", ref s.CasterOffsetXMm);
            F("caster_radius_mm", ref s.CasterRadiusMm);
            F("caster_friction", ref s.CasterFriction);

            F("no_load_rpm", ref s.NoLoadRpm);
            F("stall_torque_nm", ref s.StallTorqueNm);
            F("wheel_inertia", ref s.WheelInertia);
            F("viscous_damping", ref s.ViscousDamping);
            F("coulomb_friction_nm", ref s.CoulombFrictionNm);
            F("brake_damping", ref s.BrakeDamping);
            F("deadband_percent", ref s.DeadbandPercent);

            if (root.Has("left_motor_mirrored"))  s.LeftMotorMirrored  = root["left_motor_mirrored"].AsBool(s.LeftMotorMirrored);
            if (root.Has("right_motor_mirrored")) s.RightMotorMirrored = root["right_motor_mirrored"].AsBool(s.RightMotorMirrored);
            if (root.Has("arm_motor_mirrored"))   s.ArmMotorMirrored   = root["arm_motor_mirrored"].AsBool(s.ArmMotorMirrored);
            F("arm_gap_mm", ref s.ArmGapMm);
            if (root.Has("color_sensor_on_arm"))
                s.ColorSensorOnArm = root["color_sensor_on_arm"].AsBool(s.ColorSensorOnArm);
            F("arm_gear_ratio", ref s.ArmGearRatio);
            F("arm_min_deg", ref s.ArmMinDeg);
            F("arm_max_deg", ref s.ArmMaxDeg);
            F("arm_prong_thickness_mm", ref s.ArmProngThicknessMm);
            F("arm_grip_force_n", ref s.ArmGripForceN);
            F("arm_grip_catch_mm", ref s.ArmGripCatchMm);
            F("arm_grip_release_mm", ref s.ArmGripReleaseMm);

            F("traction_friction", ref s.TractionFriction);
            F("lateral_friction", ref s.LateralFriction);
            F("slip_stiffness", ref s.SlipStiffness);
            F("rolling_resistance", ref s.RollingResistance);

            F("color_sensor_x_mm", ref s.ColorSensorXMm);
            F("color_sensor_y_mm", ref s.ColorSensorYMm);
            F("color_sensor_height_mm", ref s.ColorSensorHeightMm);
            F("color_sensor_spot_mm", ref s.ColorSensorSpotMm);
            F("color_sensor_range_mm", ref s.ColorSensorRangeMm);
            F("color_sensor_update_hz", ref s.ColorSensorUpdateHz);
            F("color_sensor_noise", ref s.ColorSensorNoise);
            F("color_sensor_time_constant", ref s.ColorSensorTimeConstant);
            F("reflection_black", ref s.ReflectionBlack);
            F("reflection_white", ref s.ReflectionWhite);

            F("ultrasonic_x_mm", ref s.UltrasonicXMm);
            F("ultrasonic_height_mm", ref s.UltrasonicHeightMm);
            F("ultrasonic_min_mm", ref s.UltrasonicMinMm);
            F("ultrasonic_max_mm", ref s.UltrasonicMaxMm);
            F("ultrasonic_beam_deg", ref s.UltrasonicBeamDeg);
            F("ultrasonic_noise_mm", ref s.UltrasonicNoiseMm);
            F("ultrasonic_max_incidence_deg", ref s.UltrasonicMaxIncidenceDeg);
            F("ultrasonic_update_hz", ref s.UltrasonicUpdateHz);
            F("encoder_step_deg", ref s.EncoderStepDeg);
            F("encoder_slack_deg", ref s.EncoderSlackDeg);
            F("encoder_update_hz", ref s.EncoderUpdateHz);

            F("gyro_noise_dps", ref s.GyroNoiseDps);
            F("gyro_bias_dps", ref s.GyroBiasDps);
            F("accel_noise", ref s.AccelNoise);
            F("imu_heading_3d", ref s.ImuHeading3D);
            F("imu_bias_calibration", ref s.ImuBiasCalibration);

            if (root.Has("battery_mv")) s.BatteryMv = root["battery_mv"].AsInt(s.BatteryMv);
            if (root.Has("visual_mesh")) s.VisualMeshFile = root["visual_mesh"].AsString(s.VisualMeshFile);

            return s;
        }
    }
}
