using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// SPIKE アンギュラーモータ 1 軸ぶんの電気・機械モデル。
    ///
    /// トルクは直流モータの線形近似 tau = tau_stall * (duty - omega / omega_noload) を使う。
    /// 実機は PWM 駆動なので厳密には非線形だが、ETロボコンの速度域では
    /// この 1 次近似で十分に実測と合う。合わない場合は RobotSpec の
    /// NoLoadRpm / StallTorqueNm を実測値へ差し替える。
    /// </summary>
    public sealed class WheelMotor
    {
        readonly RobotSpec _spec;

        /// <summary>
        /// モータ軸の回転向きと車輪の前進向きの関係。+1 で一致、-1 で逆。
        ///
        /// 左右のモータは鏡像に取り付くので片側が -1 になる。内部の状態
        /// (Omega / AngleRad) は常に「車輪の前進が正」で保持し、
        /// 外部とのやり取り（指令とエンコーダ値）だけをこの符号で変換する。
        /// こうしておくと物理側は左右を区別せずに書ける。
        /// </summary>
        public int Polarity = 1;

        /// <summary>回転角速度 [rad/s]。</summary>
        public float Omega;
        /// <summary>積算回転角 [rad]。エンコーダ相当。</summary>
        public float AngleRad;

        /// <summary>-100..100 の指令値。</summary>
        public int CommandPercent;
        public byte Mode = 0; // Etsim.MotorMode*

        /// <summary>HOLD モードでの目標角 [rad]。</summary>
        public float HoldTargetRad;

        public WheelMotor(RobotSpec spec) { _spec = spec; }

        public void Reset()
        {
            Omega = 0f;
            AngleRad = 0f;
            CommandPercent = 0;
            Mode = 0;
            HoldTargetRad = 0f;
            ResetReporting();
        }

        public void ResetCount()
        {
            AngleRad = 0f;
            HoldTargetRad = 0f;
            ResetReporting();
        }

        void ResetReporting()
        {
            _reportInit = false;
            _sinceUpdate = float.MaxValue;
            ReportedCountDeg = 0;
            ReportedSpeedDps = 0;
        }

        /// <summary>モータ軸から見た積算角 [deg]。実機のエンコーダと同じ向き。</summary>
        public float AngleDeg => AngleRad * Mathf.Rad2Deg * Polarity;

        /// <summary>モータ軸から見た回転速度 [deg/s]。</summary>
        public float SpeedDps => Omega * Mathf.Rad2Deg * Polarity;

        // ---- 回転センサ（走行体プログラムが読む値） --------------------------
        //
        // データシート: 360 カウント/回転、精度 ±3 度（センサの公差 ＋ ギアの遊び）、
        // 更新 100Hz。軸の角度をそのまま渡すと、実機より良いエンコーダになる。

        float _slackAngleDeg;      // 遊びの中でのセンサ側の位置
        float _sinceUpdate = float.MaxValue;
        bool _reportInit;

        /// <summary>走行体プログラムへ返す積算角 [deg]。刻みと遊びが入る。</summary>
        public int ReportedCountDeg { get; private set; }

        /// <summary>走行体プログラムへ返す回転速度 [deg/s]。</summary>
        public int ReportedSpeedDps { get; private set; }

        /// <summary>
        /// 回転センサの値を更新する。更新周期より細かく呼ばれても、周期ぶんは
        /// 前の値を保持する。
        ///
        /// 遊びはバックラッシュとして入れる。軸が動く向きを変えると、遊びを
        /// 詰めるまでセンサ側は動かない。白色雑音として足すと、差分を積む
        /// オドメトリが際限なく荒れるので、そうはしない（実機のカウント自体は
        /// 再現性がある）。
        /// </summary>
        public void StepReporting(float dt)
        {
            if (!_reportInit)
            {
                _slackAngleDeg = AngleDeg;
                _reportInit = true;
            }

            if (_spec.EncoderUpdateHz > 0f)
            {
                _sinceUpdate += dt;
                if (_sinceUpdate < 1f / _spec.EncoderUpdateHz) return;
                _sinceUpdate = 0f;
            }

            float shaft = AngleDeg;
            float slack = Mathf.Max(_spec.EncoderSlackDeg, 0f);
            if (shaft > _slackAngleDeg + slack) _slackAngleDeg = shaft - slack;
            else if (shaft < _slackAngleDeg - slack) _slackAngleDeg = shaft + slack;

            float step = _spec.EncoderStepDeg;
            ReportedCountDeg = step > 0f
                ? Mathf.RoundToInt(Mathf.Round(_slackAngleDeg / step) * step)
                : Mathf.RoundToInt(_slackAngleDeg);
            ReportedSpeedDps = Mathf.RoundToInt(SpeedDps);
        }

        /// <summary>車輪の前進を正とした積算角 [deg]。オドメトリの検算用。</summary>
        public float WheelAngleDeg => AngleRad * Mathf.Rad2Deg;

        int _appliedPercent;

        /// <summary>実際に印加された出力 [-100..100]。モータ軸から見た符号。</summary>
        public int AppliedPercent => _appliedPercent * Polarity;

        /// <summary>
        /// この制御周期でモータ軸に出るトルク [N*m] を返す。
        /// 車輪から受ける反力は呼び出し側 (RobotController) が差し引く。
        /// </summary>
        public float ComputeTorque() => DriveTorque() - FrictionTorque(Mode == 1 ? _spec.BrakeDamping : 0f);

        /// <summary>
        /// 摩擦を引く前の、電気的に出しているトルク [N*m]。
        ///
        /// 静止摩擦の判定で「回し始めるのに足りるか」を見るのに要る。
        /// ComputeTorque() はここから摩擦を引いたもの。
        /// </summary>
        public float DriveTorque()
        {
            float duty = 0f;

            switch (Mode)
            {
                case 2: // HOLD : 目標角を保つ P-D 制御
                {
                    float err = HoldTargetRad - AngleRad;
                    duty = Mathf.Clamp(err * 3.0f - Omega * 0.08f, -1f, 1f);
                    break;
                }
                case 1: // BRAKE : 短絡制動。指令は 0 で粘性のみ増やす
                    duty = 0f;
                    break;
                case 0: // COAST
                    duty = 0f;
                    break;
                default: // POWER
                {
                    float cmd = CommandPercent;
                    // 不感帯: 小さい指令ではモータが起動しない
                    if (Mathf.Abs(cmd) < _spec.DeadbandPercent) cmd = 0f;
                    duty = Mathf.Clamp(cmd / 100f, -1f, 1f);
                    break;
                }
            }

            _appliedPercent = Mathf.RoundToInt(duty * 100f);

            if (Mode == 0) return 0f; // COAST は電気的トルクなし

            float omega0 = Mathf.Max(1e-3f, _spec.NoLoadOmega);
            return _spec.StallTorqueNm * (duty - Omega / omega0);
        }

        /// <summary>
        /// 止まっている車輪を回し始めるのに要るトルク [N*m]（静止摩擦）。
        ///
        /// クーロン摩擦は速度ゼロ付近で線形に落としてあり（数値安定のため）、
        /// ω=0 では 0 になる。つまり**どんなに小さな力でも回り出す**。
        /// 実機は減速機の抵抗で止まったままなので、その分をここで返す。
        /// </summary>
        public float StictionTorqueNm => _spec.CoulombFrictionNm;

        /// <summary>静止摩擦で止まっているとみなす角速度 [rad/s]。</summary>
        public const float StictionOmega = 0.05f;

        float FrictionTorque(float extraDamping)
        {
            float viscous = (_spec.ViscousDamping + extraDamping) * Omega;

            // クーロン摩擦は速度ゼロ付近で振動しやすいので、
            // 微小速度帯では線形に落として数値的に安定させる。
            const float omegaEps = 0.05f;
            float coulomb = _spec.CoulombFrictionNm *
                            Mathf.Clamp(Omega / omegaEps, -1f, 1f);

            return viscous + coulomb;
        }

        /// <summary>
        /// 路面との連成を解いた角速度を直接入れる。
        ///
        /// 駆動力はすべり速度に比例するので、陽解法で回すと
        /// k・r^2・dt / I が 1 を超えたときに 1 ステップごとに符号が反転して
        /// 振動が残る（実測で k=220, r=28mm, dt=2ms, I=2e-4 のとき 1.73）。
        /// 指令が 0 でも走行体がじりじり動くのはこれが原因なので、
        /// 呼び出し側で陰解法を解いてここへ渡す。
        /// </summary>
        public void SetSolvedOmega(float omega, float dt)
        {
            float limit = _spec.NoLoadOmega * 3f;
            Omega = Mathf.Clamp(omega, -limit, limit);
            AngleRad += Omega * dt;
        }

        /// <summary>角加速度を積分する。reactionTorque は車輪が路面から受ける反力 [N*m]。</summary>
        public void Integrate(float motorTorque, float reactionTorque, float dt)
        {
            float inertia = Mathf.Max(1e-6f, _spec.WheelInertia);
            Omega += (motorTorque - reactionTorque) / inertia * dt;

            // 数値発散の保険。実機の 3 倍速を超えることはない。
            float limit = _spec.NoLoadOmega * 3f;
            Omega = Mathf.Clamp(Omega, -limit, limit);

            AngleRad += Omega * dt;
        }

        /// <summary>
        /// 制御プロセスからの指令を受ける。percent はモータ軸から見た値なので、
        /// ここで車輪の前進を正とする内部表現へ直す。
        /// </summary>
        public void SetCommand(int percent, byte mode)
        {
            // HOLD へ入った瞬間の角度を保持目標にする
            if (mode == 2 && Mode != 2) HoldTargetRad = AngleRad;
            CommandPercent = Mathf.Clamp(percent, -100, 100) * Polarity;
            Mode = mode;
        }
    }
}
