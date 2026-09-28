using System;
using UnityEngine;
using Etrobo.Sim.Net;

namespace Etrobo.Sim
{
    /// <summary>
    /// シミュレータ全体の進行役。制御プロセスからの指令適用、物理 1 ステップ、
    /// センサ計算、パケット送信をこの順で回す。
    ///
    /// 動作モードは 2 つ。
    ///   FREERUN  : Unity の FixedUpdate で自走する。実機に近い非同期動作。
    ///   LOCKSTEP : 指令が届いたときだけ物理を 1 ステップ進める。完全に再現可能。
    /// 制御プロセスが ETSIM_AFLAG_LOCKSTEP を立てると自動で切り替わる。
    /// </summary>
    public sealed class SimManager : MonoBehaviour
    {
        [Header("接続")]
        public int Port = Etsim.DefaultPort;
        [Tooltip("他ホストからの接続を許可する。既定ではループバックのみ。")]
        public bool AllowRemoteController = false;

        [Header("シミュレーション")]
        [Tooltip("物理ステップ幅 [s]。制御周期より十分細かくする。")]
        public float FixedStepSeconds = 0.002f;
        [Tooltip("1 フレームで進める最大ステップ数。ロックステップで積み残しを防ぐ上限。")]
        public int MaxStepsPerFrame = 64;
        [Tooltip("ラインからこの距離 [mm] 以上離れたらコースアウトとみなす。")]
        public float OutOfCourseMm = 250f;
        [Tooltip("この秒数だけ指令が途絶えたらモータを止める。")]
        public float ControlTimeoutSeconds = 1.0f;

        [Header("自動スタート")]
        [Tooltip("制御プロセスが接続してからこの秒数後に中央ボタンを自動で押す。0 で無効（手で Space を押す）。")]
        public float AutoStartSeconds = 0f;

        public CourseDefinition Course { get; private set; }
        public CourseField Field { get; private set; }
        public RobotController Robot { get; private set; }
        public LapTimer Timer { get; private set; }
        public SimServer Server { get; private set; }

        public ColorSensorModel ColorSensor { get; private set; }
        public UltrasonicSensorModel Ultrasonic { get; private set; }
        public ImuModel Imu { get; private set; }

        public bool Lockstep { get; private set; }
        public double SimTimeSeconds { get; private set; }
        public uint SentPackets { get; private set; }
        public bool OutOfCourse { get; private set; }
        public Color32 LedColor { get; private set; } = new Color32(0, 0, 0, 255);

        uint _seq;
        uint _ackSeq;
        bool _pendingResetAck;
        ActuatorPacket _command;
        bool _hasEverReceived;

        public void Initialize(CourseDefinition course, CourseField field, RobotController robot)
        {
            Course = course;
            Field = field;
            Robot = robot;
            StartPositionMm = course.StartPositionMm;
            StartHeadingDeg = course.StartHeadingDeg;

            Timer = new LapTimer(course.Gates);
            // ガレージはコース図に印刷されている（規約 5.2.4）。走行体が出る側のものを使う。
            Timer.Garage = GarageArea.FromCourse(
                course, course.StartPositionMm.x < course.SheetWidthMm * 0.5f);
            if (!Timer.Garage.Found)
                Debug.Log("[Etsim] ガレージの枠が見つかりません。フィニッシュは判定しません"
                          + "（コースを作り直すときは --line-min-width 0.5）");
            else
                Debug.Log($"[Etsim] ガレージ 中心 ({Timer.Garage.CenterMm.x:F0}, "
                          + $"{Timer.Garage.CenterMm.y:F0})mm "
                          + $"{Timer.Garage.SizeMm.x:F0}x{Timer.Garage.SizeMm.y:F0}mm");

            // 走行体のコライダは RobotController が "Ignore Raycast" へ移してあるので、
            // 既定のレイキャストレイヤーを使えば自分自身は拾わない。
            int rayMask = Physics.DefaultRaycastLayers;
            ColorSensor = new ColorSensorModel(robot.Spec, robot.ColorSensorMount, rayMask);
            Ultrasonic = new UltrasonicSensorModel(robot.Spec, robot.UltrasonicMount, rayMask);
            Imu = new ImuModel(robot.Spec, robot.Body);

            Time.fixedDeltaTime = FixedStepSeconds;

            Server = new SimServer();
            Server.Start(Port, AllowRemoteController);

            ResetSimulation();
        }

        public void ResetSimulation()
        {
            Robot.ResetPose(StartPositionMm, StartHeadingDeg);

            if (!_placementLogged)
            {
                _placementLogged = true;
                Debug.Log($"[Etsim] 初期配置: 指定 {StartPositionMm}mm 方位 {StartHeadingDeg}deg"
                          + $" -> transform {Robot.transform.position}"
                          + $" / rigidbody {Robot.Body.position}"
                          + $" / autoSyncTransforms {Physics.autoSyncTransforms}");
            }
            ColorSensor.Reset();
            Ultrasonic.Reset();
            Imu.Reset();
            Timer.Reset();
            _connectedSince = -1f;
            _autoStartFired = false;
            _seenAtStart = false;
            _idleSince = -1.0;
            _idleReports = 0;
            _idleNextReport = 1f;
            _truthLoggedAt = -1.0;        // 真値ログも測り直す
            SimTimeSeconds = 0.0;
            OutOfCourse = false;
            _goalLogged = false;
            LedColor = new Color32(0, 0, 0, 255);
            _command = default;

            // ボトルを置き直すのは難所側の仕事。走行体だけ戻しても、
            // 押し出した力士ボトルが土俵の外に転がったままだと走り直せない。
            OnReset?.Invoke();
        }

        /// <summary>走行をやり直すときに呼ばれる。難所の置き直しに使う。</summary>
        public System.Action OnReset;

        // -- 開始姿勢 ---------------------------------------------------------

        /// <summary>
        /// 走行を始める姿勢。R キーやリセット要求で走行体をここへ戻す。
        ///
        /// 既定はコースの start_pose だが、難所の手前だけ何度も試したいことが
        /// 多いので、任意の場所に置き換えられるようにしてある。
        /// </summary>
        public Vector2 StartPositionMm { get; private set; }
        public float StartHeadingDeg { get; private set; }

        /// <summary>コース既定の開始姿勢から動かしているか。</summary>
        public bool StartMoved =>
            Course != null && ((StartPositionMm - Course.StartPositionMm).sqrMagnitude > 1f
                               || Mathf.Abs(Mathf.DeltaAngle(StartHeadingDeg, Course.StartHeadingDeg)) > 0.5f);

        public void SetStart(Vector2 posMm, float headingDeg)
        {
            StartPositionMm = posMm;
            StartHeadingDeg = headingDeg;
            Debug.Log($"[Etsim] 開始位置を ({posMm.x:F0}, {posMm.y:F0})mm 方位 {headingDeg:F1}° にしました");
            ResetSimulation();
        }

        /// <summary>
        /// 開始位置でその場回転しながらソナーを測る（掃引）。
        ///
        /// 探索の設計に要るのは「どの向きに何が見えるか」なので、走らせる前に
        /// 静的に測れるようにしておく。ソナーは時定数を持たないモデルなので、
        /// 姿勢を変えてその場で読めば 1 点ずつ正確に取れる。
        /// </summary>
        public void RunSonarSweep(float stepDeg)
        {
            if (stepDeg <= 0f || Robot == null) return;

            float baseHeading = StartHeadingDeg;
            Debug.Log($"[Etsim] ソナー掃引 開始 位置 ({StartPositionMm.x:F1}, {StartPositionMm.y:F1})mm"
                      + $" 基準方位 {baseHeading:F1}° 刻み {stepDeg:F1}°");
            Debug.Log("[Etsim] 掃引CSV heading_deg,distance_mm");

            for (float a = 0f; a < 360f; a += stepDeg)
            {
                float h = Mathf.Repeat(baseHeading + a + 180f, 360f) - 180f;
                Robot.ResetPose(StartPositionMm, h);

                // autoSyncTransforms が false なので、レイキャストに姿勢を反映させる
                Physics.SyncTransforms();
                Ultrasonic.Measure();

                Debug.Log($"[Etsim] 掃引CSV {h:F1},{Ultrasonic.DistanceMm}");
            }

            Robot.ResetPose(StartPositionMm, baseHeading);
            Physics.SyncTransforms();
            Debug.Log("[Etsim] ソナー掃引 終了");
        }

        /// <summary>いま走行体がいる場所を開始位置にする。</summary>
        public void SetStartHere() => SetStart(Robot.TruthPositionMm, Robot.TruthHeadingDeg);

        /// <summary>コースの start_pose に戻す。</summary>
        public void RestoreCourseStart() => SetStart(Course.StartPositionMm, Course.StartHeadingDeg);

        // -- 配置モード -------------------------------------------------------

        /// <summary>
        /// 矢印キーで走行体を動かして開始位置を決めるモード。
        ///
        /// 「スタートラインのぎりぎり手前」のような置き方は座標を計算するより
        /// 見ながら合わせるほうが早い。この間だけ矢印キーはハブのボタンではなく
        /// 走行体の移動に使う。
        /// </summary>
        public bool PlacementMode;

        /// <summary>配置モードの動く速さ。Shift を押している間は 10 倍。</summary>
        const float JogMmPerSec = 25f;
        const float JogDegPerSec = 15f;
        const float JogFastFactor = 10f;

        /// <summary>
        /// 制御プロセスの接続から指定の秒数後にフォースセンサを押す。
        ///
        /// 自動スタートは「走り出したら押すのをやめる」ので、走っている途中で
        /// もう一度押させたいときに使う（シナリオの BUTTON 待ちなど、
        /// 実機では人が 2 回押すもの）。秒数は接続からの経過。
        /// </summary>
        public float[] PressForceAt;

        int _pressedCount;

        void UpdateScriptedPresses()
        {
            if (PressForceAt == null || PressForceAt.Length == 0) return;
            if (Server == null || !Server.ControllerConnected || _connectedSince < 0f) return;

            float elapsed = Time.realtimeSinceStartup - _connectedSince;
            while (_pressedCount < PressForceAt.Length && elapsed >= PressForceAt[_pressedCount])
            {
                _pressedCount++;
                PressForceSensor();
                Debug.Log($"[Etsim] フォースセンサを押しました（接続から {elapsed:F1} 秒、"
                          + $"{_pressedCount}/{PressForceAt.Length} 回目）");
            }
        }

        void HandlePlacement(float dt)
        {
            if (!PlacementMode) return;

            float fast = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                         ? JogFastFactor : 1f;
            float step = JogMmPerSec * fast * dt;
            float turn = JogDegPerSec * fast * dt;

            float ahead = (Input.GetKey(KeyCode.UpArrow) ? 1f : 0f)
                          - (Input.GetKey(KeyCode.DownArrow) ? 1f : 0f);
            float side = (Input.GetKey(KeyCode.RightArrow) ? 1f : 0f)
                         - (Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f);
            float spin = (Input.GetKey(KeyCode.Comma) ? 1f : 0f)
                         - (Input.GetKey(KeyCode.Period) ? 1f : 0f);

            if (ahead == 0f && side == 0f && spin == 0f) return;

            float rad = StartHeadingDeg * Mathf.Deg2Rad;
            Vector2 fwd = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            Vector2 right = new Vector2(fwd.y, -fwd.x);

            JogStart(fwd * (ahead * step) + right * (side * step), spin * turn);
        }

        /// <summary>開始位置を少し動かす。走行体もその場へ移す。</summary>
        public void JogStart(Vector2 deltaMm, float deltaHeadingDeg)
        {
            StartPositionMm += deltaMm;
            StartHeadingDeg = Mathf.Repeat(StartHeadingDeg + deltaHeadingDeg + 180f, 360f) - 180f;
            Robot.ResetPose(StartPositionMm, StartHeadingDeg);
            Timer.Reset();

            // 自分で動かしたぶんを「指令 0 なのに動いた」と誤報しないよう基準を取り直す
            _idleSince = -1.0;
        }

        /// <summary>難所の成立判定。ゴールのログに結果を載せるために持つ。</summary>
        public NansyoJudge Judge;

        /// <summary>
        /// 起動直後に一度だけ、走行体とセンサが想定どおりの場所にいるかを出す。
        /// 「動かない」「センサが黒のまま」の原因はほぼここで分かる
        /// （走行体の位置ずれ、レイヤーマスク、床コライダの欠落）。
        /// </summary>
        void LogStartupDiagnostics()
        {
            var mount = Robot.ColorSensorMount;
            bool hit = Physics.Raycast(mount.position, -mount.up, out RaycastHit h, 1.0f,
                                       Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

            string rayInfo = hit
                ? $"{h.collider.name}(layer {h.collider.gameObject.layer}) 距離 {h.distance * 1000f:F1}mm"
                : "<何にも当たらず>";

            var surface = UnityEngine.Object.FindObjectOfType<CourseSurface>();
            string sheetInfo = surface != null
                ? $"{surface.name}(layer {surface.gameObject.layer}) bounds {surface.GetComponent<Collider>()?.bounds}"
                : "<CourseSurface が見つからない>";

            string usInfo = Ultrasonic.Present
                ? Ultrasonic.DistanceMm + " mm"
                : "検出なし (前に障害物なし)";

            Debug.Log(
                "[Etsim] 起動時チェック\n"
                + $"  走行体   : world {Robot.transform.position} layer {Robot.gameObject.layer}\n"
                + $"  真値位置 : {Robot.TruthPositionMm} mm  方位 {Robot.TruthHeadingDeg:F1} deg\n"
                + $"  センサ   : world {mount.position}  down {-mount.up}\n"
                + $"  レイ結果 : {rayInfo}\n"
                + $"  接地     : L/R いずれか {Robot.AnyWheelGrounded}\n"
                + $"  反射光量 : {ColorSensor.Reflection}\n"
                + $"  超音波   : {usInfo}\n"
                + $"  コース面 : {sheetInfo}");
        }

        int _connectionGeneration;
        bool _seenAtStart;
        bool _goalLogged;
        bool _wasConnected;
        bool _diagnosticsLogged;
        bool _placementLogged;

        void Update()
        {
            if (Robot == null) return;

            if (!_diagnosticsLogged && Time.timeSinceLevelLoad > 1.0f)
            {
                _diagnosticsLogged = true;
                LogStartupDiagnostics();
            }

            if (Lockstep)
            {
                // 指令が届いたぶんだけ進める。届かない間は完全に止まる。
                int steps = 0;
                while (steps < MaxStepsPerFrame)
                {
                    var cmd = Server.TakeLatest(out bool hasNew);
                    if (!hasNew) break;
                    _command = cmd;
                    _hasEverReceived = true;
                    ApplyCommand(cmd);
                    StepOnce(FixedStepSeconds, simulatePhysicsManually: true);
                    steps++;
                }
            }

            HandleKeyboardShortcuts();
        }

        void FixedUpdate()
        {
            if (Robot == null || Lockstep) return;

            var cmd = Server.TakeLatest(out bool hasNew);
            if (hasNew)
            {
                _command = cmd;
                _hasEverReceived = true;
                CheckLockstepSwitch(cmd);
            }

            ApplyCommand(_command);
            StepOnce(Time.fixedDeltaTime, simulatePhysicsManually: false);
        }

        void CheckLockstepSwitch(in ActuatorPacket cmd)
        {
            if (!cmd.Lockstep) return;

            Lockstep = true;
            Physics.simulationMode = SimulationMode.Script;
            Debug.Log("[Etsim] ロックステップモードへ切り替えました");
        }

        void ApplyCommand(in ActuatorPacket cmd)
        {
            _ackSeq = cmd.Seq;

            if (cmd.Shutdown)
            {
                Server.ForgetController();
                StopMotors();
                return;
            }

            if (cmd.RequestReset)
            {
                ResetSimulation();
                _pendingResetAck = true;
                return;
            }

            // 指令が途絶えたらモータを切る。実機でプログラムが落ちた状態に相当。
            if (_hasEverReceived && Server.SecondsSinceLastPacket > ControlTimeoutSeconds)
            {
                StopMotors();
                return;
            }

            Robot.MotorLeft.SetCommand(cmd.PowerL, cmd.ModeL);
            Robot.MotorRight.SetCommand(cmd.PowerR, cmd.ModeR);
            Robot.MotorArm.SetCommand(cmd.PowerA, cmd.ModeA);

            if ((cmd.ResetCountMask & 0x01) != 0) Robot.MotorLeft.ResetCount();
            if ((cmd.ResetCountMask & 0x02) != 0) Robot.MotorRight.ResetCount();
            if ((cmd.ResetCountMask & 0x04) != 0) Robot.MotorArm.ResetCount();

            if (cmd.ResetImu != 0) Imu.ResetHeading();

            LedColor = new Color32(cmd.LedR, cmd.LedG, cmd.LedB, 255);
        }

        void StopMotors()
        {
            Robot.MotorLeft.SetCommand(0, Etsim.MotorModeCoast);
            Robot.MotorRight.SetCommand(0, Etsim.MotorModeCoast);
            Robot.MotorArm.SetCommand(0, Etsim.MotorModeCoast);
        }

        void StepOnce(float dt, bool simulatePhysicsManually)
        {
            Robot.Step(dt);

            if (simulatePhysicsManually) Physics.Simulate(dt);

            ColorSensor.Step(dt);
            Ultrasonic.Step(dt);
            Imu.Step(dt);

            Vector2 pos = Robot.TruthPositionMm;
            int passedBefore = Timer.CheckpointsPassed;

            // ゲートもガレージも「走行体の一部」で判定する（規約 5.9 / 5.12 /
            // 5.13 / 5.14）。中心 1 点ではないので、四隅を渡す。
            NansyoJudge.Footprint(Robot.Spec, pos, Robot.TruthHeadingDeg, _footprint);
            Timer.Step(_footprint, dt);

            bool finishedBefore = Timer.FinishEstablished;
            Timer.StepFinish(_footprint, IsFullyStopped(), dt);

            // 走行プログラムが終わったところで難所の状態を残す。
            // 周回しないシナリオ（難所だけの試験など）はゴールを踏まないので、
            // ゴール時のログだけだと結果がどこにも出ない。
            // ControllerConnected は相手の宛先を覚えているかなので、落ちても true のまま。
            // 「指令が途絶えた」= モータを切るのと同じ条件で見る。
            bool connectedNow = Server != null && Server.ControllerConnected
                                && Server.SecondsSinceLastPacket <= ControlTimeoutSeconds;
            if (_wasConnected && !connectedNow)
            {
                _wasConnected = false;
                if (Judge != null) Debug.Log($"[Etsim] 走行終了時の難所 {Judge.Summary()}");
            }
            else if (connectedNow)
            {
                _wasConnected = true;
            }

            if (Timer.FinishEstablished && !finishedBefore)
                Debug.Log($"[Etsim] フィニッシュ成立（ガレージで {LapTimer.FinishHoldSeconds:F0} 秒"
                          + " 完全停止）。競技終了です");

            CheckIdleDrift(pos, Robot.TruthHeadingDeg);
            LogTruthPose(pos, Robot.TruthHeadingDeg);

            float lineDistance = Field.DistanceToNearestLine(pos, OutOfCourseMm * 1.5f);
            bool outNow = lineDistance > OutOfCourseMm;

            // 走行の結果をログにも残す。バッチ実行や調整スクリプトは
            // 画面が見えないので、ここを読んで合否を判定する。
            if (Timer.CheckpointsPassed != passedBefore)
                Debug.Log($"[Etsim] チェックポイント通過 {Timer.LastGateId} " +
                          $"({Timer.CheckpointsPassed}/{Timer.CheckpointTotal}) t={Timer.ElapsedSeconds:F2}s");

            if (Timer.Finished && !_goalLogged)
            {
                _goalLogged = true;
                Debug.Log($"[Etsim] ゴール 計測タイム {Timer.ElapsedSeconds:F2} 秒");
                // 難所は「リザルトポイント提示時」の状態で決まる（規約 5.4.7）
                if (Judge != null) Debug.Log($"[Etsim] 難所 {Judge.Summary()}");
            }

            if (outNow && !OutOfCourse)
                Debug.Log($"[Etsim] コースアウト 位置 ({pos.x:F0}, {pos.y:F0}) mm  " +
                          $"最寄りの線まで {lineDistance:F0} mm");

            OutOfCourse = outNow;

            SimTimeSeconds += dt;
            SendSensorPacket();
        }

        void SendSensorPacket()
        {
            if (!Server.ControllerConnected) return;

            ushort flags = 0;
            if (Timer.Running) flags |= Etsim.SFlagRunning;
            if (Lockstep) flags |= Etsim.SFlagLockstep;
            if (Timer.Finished) flags |= Etsim.SFlagGoal;
            if (OutOfCourse) flags |= Etsim.SFlagOutOfCourse;
            if (_pendingResetAck)
            {
                flags |= Etsim.SFlagResetDone;
                _pendingResetAck = false;
            }

            var p = new SensorPacket
            {
                Flags = flags,
                Seq = ++_seq,
                AckSeq = _ackSeq,
                SimTimeUs = (uint)(SimTimeSeconds * 1e6),

                ColorId = ColorSensor.ColorId,
                Reflection = ColorSensor.Reflection,
                Ambient = ColorSensor.Ambient,
                R = ColorSensor.R,
                G = ColorSensor.G,
                B = ColorSensor.B,
                HsvH = ColorSensor.HueDeg,
                HsvS = ColorSensor.Saturation,
                HsvV = ColorSensor.Value,

                UltrasonicMm = Ultrasonic.DistanceMm,
                UltrasonicPresence = (byte)(Ultrasonic.Present ? 1 : 0),
                ForcePressed = (byte)(StartSignalHeld ? 1 : 0),
                ForceNewton = StartSignalHeld ? StartSignalForceN : 0f,

                YawDeg = Imu.YawDeg,
                PitchDeg = Imu.PitchDeg,
                RollDeg = Imu.RollDeg,
                GyroX = Imu.GyroDps.x,
                GyroY = Imu.GyroDps.y,
                GyroZ = Imu.GyroDps.z,
                AccelX = Imu.AccelMss.x,
                AccelY = Imu.AccelMss.y,
                AccelZ = Imu.AccelMss.z,

                CountL = Robot.MotorLeft.ReportedCountDeg,
                CountR = Robot.MotorRight.ReportedCountDeg,
                CountA = Robot.MotorArm.ReportedCountDeg,
                SpeedL = Robot.MotorLeft.ReportedSpeedDps,
                SpeedR = Robot.MotorRight.ReportedSpeedDps,
                SpeedA = Robot.MotorArm.ReportedSpeedDps,
                PowerL = Robot.MotorLeft.AppliedPercent,
                PowerR = Robot.MotorRight.AppliedPercent,
                PowerA = Robot.MotorArm.AppliedPercent,

                ButtonState = _buttonState,
                BatteryMv = Robot.Spec.BatteryMv,

                TruthXMm = Robot.TruthPositionMm.x,
                TruthYMm = Robot.TruthPositionMm.y,
                TruthHeadingDeg = Robot.TruthHeadingDeg,
                TruthSpeedMmps = Robot.TruthSpeedMmps
            };

            Server.Send(p);
            SentPackets++;
        }

        /// <summary>
        /// スタート合図で押されたことにするフォースセンサの力 [N]。
        ///
        /// 走行体を始動させる合図は、チームによって「ハブの中央ボタン」と
        /// 「フォースセンサを手で押す」に分かれる。実機ではどちらも人が押すので、
        /// シミュレータでも同じ合図（Space / 自動スタート）が両方に入るようにする。
        /// 2N は指で押した程度の力。
        /// </summary>
        public const float StartSignalForceN = 2.0f;

        /// <summary>パネルのボタンを 1 回押したときに押されている時間 [s]。指で押す長さ。</summary>
        public const float StartTapSeconds = 0.4f;

        /// <summary>フォースセンサが押されている間 true。</summary>
        public bool StartSignalHeld => _forceHeld;

        /// <summary>ハブの中央ボタンが押されている間 true。</summary>
        public bool CenterButtonHeld => (_buttonState & Etsim.ButtonCenter) != 0;

        /// <summary>押しっぱなしにする（パネルのトグル）。</summary>
        public bool CenterLatched;
        public bool ForceLatched;

        /// <summary>パネルから 1 回押す。実機で人が指で押すのと同じ長さだけ押す。</summary>
        public void PressCenterButton() => _centerUntil = Time.realtimeSinceStartup + StartTapSeconds;
        /// <summary>
        /// フォースセンサを一瞬押す。スタート操作（規約 5.8）にあたるので、
        /// 走行タイムの計測もここから始める（5.6.1 はスタート合図からだが、
        /// シミュレータには審判がいないので操作の瞬間を合図とみなす）。
        /// </summary>
        public void PressForceSensor()
        {
            _forceUntil = Time.realtimeSinceStartup + StartTapSeconds;
            Timer?.StartSignal();
        }

        byte _buttonState;
        bool _forceHeld;
        float _centerUntil = -1f;
        float _forceUntil = -1f;
        float _connectedSince = -1f;
        bool _autoStartFired;

        /// <summary>自動スタートが中央ボタンを押している間 true。HUD 表示用。</summary>
        public bool AutoStartHolding { get; private set; }

        void HandleKeyboardShortcuts()
        {
            // 実機のハブボタンに相当。制御プログラムのスタート待ちを解除する。
            // Space は実機で人が合図を出すのと同じく、中央ボタンとフォースセンサの
            // 両方に入る。パネルからはどちらかだけを押せる。
            _buttonState = 0;
            float now = Time.realtimeSinceStartup;
            bool space = Input.GetKey(KeyCode.Space);

            if (space || CenterLatched || now < _centerUntil) _buttonState |= Etsim.ButtonCenter;
            _forceHeld = space || ForceLatched || now < _forceUntil;

            // 配置モードの間、矢印キーは走行体を動かすほうに使う
            if (!PlacementMode)
            {
                if (Input.GetKey(KeyCode.LeftArrow)) _buttonState |= Etsim.ButtonLeft;
                if (Input.GetKey(KeyCode.RightArrow)) _buttonState |= Etsim.ButtonRight;
            }

            if (Input.GetKeyDown(KeyCode.P)) PlacementMode = !PlacementMode;
            if (Input.GetKeyDown(KeyCode.R)) ResetSimulation();

            HandlePlacement(Time.deltaTime);

            UpdateAutoStart();
            UpdateScriptedPresses();
        }

        Vector2 _idleFrom;
        float _idleFromHeadingDeg;
        double _idleSince = -1.0;
        int _idleReports;
        float _idleNextReport = 1f;

        /// <summary>指令が 0 なのに走行体が動いていないか見張る。</summary>
        const float IdleDriftMm = 20f;

        /// <summary>同じく、向きのずれ。ジャイロの模擬ドリフトより十分小さくとる。</summary>
        const float IdleTurnDeg = 0.5f;

        /// <summary>
        /// 「止まっている」とみなす回転の速さ [deg/s]。
        /// 静止していても PhysX の解の揺れで 0.02〜0.03 は出るので、その上に取る。
        /// じわじわした回転は瞬時値ではなく累積角（IdleTurnDeg）のほうで捕まえる。
        /// </summary>
        const float IdleSpinDps = 0.05f;

        readonly Vector2[] _footprint = new Vector2[4];

        /// <summary>
        /// 完全停止（規約 5.11）。
        /// 「ハブに直接つながっている全てのモータが停止または保持を続け、
        /// 走行体が機械的に停止している状態」。止めていても保持していても
        /// 車輪が回っていなければよいので、指令ではなく実際の動きで見る。
        /// </summary>
        bool IsFullyStopped()
        {
            const float StillMmPerSec = 5f;
            const float StillDegPerSec = 5f;

            return Robot.Body.velocity.magnitude * CourseSpace.MToMm < StillMmPerSec
                   && Mathf.Abs(Robot.MotorLeft.SpeedDps) < StillDegPerSec
                   && Mathf.Abs(Robot.MotorRight.SpeedDps) < StillDegPerSec
                   && Mathf.Abs(Robot.MotorArm.SpeedDps) < StillDegPerSec;
        }

        /// <summary>
        /// モータ指令が 0 の間に走行体が動いてしまっていないか調べる。
        ///
        /// 車輪は摩擦ゼロの球で、駆動力はすべり速度からしか出さないので、
        /// 何かの弾みで転がり出すと止めるものが無い。実機は静止摩擦と
        /// 減速機の抵抗で止まっているので、動いて見えるならモデルの穴。
        ///
        /// **向きも見る。** その場で回るぶんには位置が変わらないので、
        /// 移動量だけを見ていると気づけない。実際、方位のじわじわした
        /// ずれをこれで取りこぼしていた。
        /// </summary>
        void CheckIdleDrift(Vector2 pos, float headingDeg)
        {
            bool idle = _command.PowerL == 0 && _command.PowerR == 0;
            if (!idle)
            {
                _idleSince = -1.0;
                return;
            }

            if (_idleSince < 0.0)
            {
                StartIdleWindow(pos, headingDeg);
                return;
            }

            if (_idleReports >= 6 || SimTimeSeconds - _idleSince < _idleNextReport) return;

            float speedMmPerSec = Robot.Body.velocity.magnitude * 1000f;
            float spinDps = Robot.Body.angularVelocity.magnitude * Mathf.Rad2Deg;
            float moved = Vector2.Distance(pos, _idleFrom);
            float turned = Mathf.Abs(Mathf.DeltaAngle(_idleFromHeadingDeg, headingDeg));

            // 止まるまでの惰行は「動いている」とは言わない。
            // 積算した量だけで見ると、停止後もずっと警告が出続ける。
            bool stopped = speedMmPerSec < 1f && spinDps < IdleSpinDps;
            if ((moved < IdleDriftMm && turned < IdleTurnDeg) || stopped)
            {
                if (stopped) StartIdleWindow(pos, headingDeg);
                _idleNextReport = (float)(SimTimeSeconds - _idleSince) + 5f;
                return;
            }

            _idleReports++;
            _idleNextReport = (float)(SimTimeSeconds - _idleSince) + 10f;
            Debug.LogWarning(
                $"[Etsim] モータ指令が 0 のまま {SimTimeSeconds - _idleSince:F1} 秒で"
                + $" {moved:F0}mm 動き {turned:F2}° 回りました"
                + $" (速度 {speedMmPerSec:F1}mm/s 角速度 {spinDps:F3}deg/s"
                + $" 接地 {Robot.AnyWheelGrounded})");
        }

        /// <summary>
        /// 真の位置と向きを一定周期でログに出す。0 で無効（既定）。
        /// じわじわした流れを追うための計測用。HUD は読み取れないので
        /// バッチ実行から数字で追えるようにしてある。
        /// </summary>
        public float TruthLogSeconds;

        double _truthLoggedAt = -1.0;
        Vector2 _truthLogFrom;
        float _truthLogFromHeadingDeg;

        // 走った道のり。毎ステップ足す。走行体が報告してくる距離と
        // 突き合わせるのはこちらで、始点からの直線距離ではない。
        // コースを一周すると、道のりは直線距離の 2 倍近くになる。
        Vector2 _truthPrev;
        float _truthPathMm;

        void LogTruthPose(Vector2 pos, float headingDeg)
        {
            if (TruthLogSeconds <= 0f) return;

            if (_truthLoggedAt < 0.0)
            {
                _truthLoggedAt = SimTimeSeconds;
                _truthLogFrom = pos;
                _truthPrev = pos;
                _truthPathMm = 0f;
                _truthLogFromHeadingDeg = headingDeg;
                return;
            }

            _truthPathMm += Vector2.Distance(pos, _truthPrev);
            _truthPrev = pos;

            if (SimTimeSeconds - _truthLoggedAt < TruthLogSeconds) return;
            _truthLoggedAt = SimTimeSeconds;

            Debug.Log($"[Etsim] 真値 t={SimTimeSeconds:F0}s 位置 ({pos.x:F2}, {pos.y:F2})mm"
                      + $" 方位 {headingDeg:F4}°"
                      + $" 累積 {_truthPathMm:F2}mm"
                      + $" (直線 {Vector2.Distance(pos, _truthLogFrom):F2}mm)"
                      + $" / {Mathf.DeltaAngle(_truthLogFromHeadingDeg, headingDeg):F4}°"
                      + $" アーム {Robot.ArmAngleDeg:F1}°"
                      + $" 速度 {Robot.Body.velocity.magnitude * 1000f:F3}mm/s"
                      + $" 角速度 {Robot.Body.angularVelocity.y * Mathf.Rad2Deg:F5}deg/s");

            // ボトルも一緒に出す。「いつまでも動いていないか」を見るため。
            if (Judge == null || Judge.Nansyo == null) return;
            LogBottle("力士", Judge.Nansyo.RikishiBottle);
            LogBottle("キャリー", Judge.Nansyo.CarryBottle);
        }

        static void LogBottle(string name, Transform bottle)
        {
            if (bottle == null) return;

            var rb = bottle.GetComponent<Rigidbody>();
            Vector2 p = CourseSpace.ToCourseMm(bottle.position);

            Debug.Log($"[Etsim] {name}ボトル ({p.x,7:F1}, {p.y,7:F1})mm"
                      + $" 傾き {Vector3.Angle(bottle.up, Vector3.up),5:F1}°"
                      + $" 速さ {(rb != null ? rb.velocity.magnitude * CourseSpace.MToMm : 0f),7:F2}mm/s"
                      + $" 回転 {(rb != null ? rb.angularVelocity.magnitude * Mathf.Rad2Deg : 0f),7:F2}deg/s");
        }

        void StartIdleWindow(Vector2 pos, float headingDeg)
        {
            _idleSince = SimTimeSeconds;
            _idleFrom = pos;
            _idleFromHeadingDeg = headingDeg;
        }

        /// <summary>
        /// 接続から一定時間後に中央ボタンを自動で押す。無人で回す回帰テスト用。
        /// 起動時刻ではなく「制御プロセスが接続してから」を基準にするのは、
        /// 相手がまだボタンを見ていない間に押し終わってしまうのを避けるため。
        ///
        /// 1 回押して終わりにすると、相手がボタンを見に来る前に押し終わっていた場合に
        /// 走行が始まらないまま待ち続ける（ゲイン探索では「時間切れ」に見える）。
        /// 走行体が動き出すまで、間を空けて押し直す。
        /// </summary>
        void UpdateAutoStart()
        {
            AutoStartHolding = false;

            // 制御プロセスを起動し直したら、走行はやり直し。
            // リセット要求の到着を待たずに、接続そのものを合図にする
            // （待っていると、繋ぎ直しのたびに自動スタートを取りこぼす）。
            if (Server != null && Server.ConnectionGeneration != _connectionGeneration)
            {
                _connectionGeneration = Server.ConnectionGeneration;
                _autoStartFired = false;
                _connectedSince = -1f;
                _seenAtStart = false;
                _pressedCount = 0;
                Debug.Log($"[Etsim] 制御プロセスが接続しました (第 {_connectionGeneration} 世代) 相手 {Server.ControllerPeer}");
            }

            if (AutoStartSeconds <= 0f || _autoStartFired) return;

            if (Server == null || !Server.ControllerConnected)
            {
                _connectedSince = -1f;
                return;
            }

            if (_connectedSince < 0f) _connectedSince = Time.realtimeSinceStartup;

            // 走り出したかどうかは、スタート地点からの移動で見る。
            //
            // ただし繋ぎ直した直後は、走行体がまだ前回の終了位置にいる。
            // そこで「移動した」と判定すると、リセットが届く前に自動スタートを
            // 使い切ってしまい、いつまでもボタン待ちのままになる。
            // 一度スタート地点にいるのを見てから、移動を見はじめる。
            float fromStart = Vector2.Distance(Robot.TruthPositionMm, StartPositionMm);
            bool atStart = fromStart <= AutoStartMovedMm;
            if (atStart) _seenAtStart = true;
            if (!_seenAtStart)
            {
                _connectedSince = -1f;    // リセットが届くまで数えはじめない
                return;
            }

            // 走り出したかどうかは、離れた距離だけでなく**いま動いているか**でも見る。
            // 置いた直後の沈み込みで 20mm 以上すべることがあり、距離だけだと
            // それを走り出しと取り違えて、押すのをやめてしまう。
            float speedMmPerSec = Robot.Body.velocity.magnitude * CourseSpace.MToMm;
            if (!atStart && speedMmPerSec > AutoStartMovingMmPerSec)
            {
                _autoStartFired = true;
                Debug.Log($"[Etsim] 自動スタートで走り出しました"
                          + $"（開始位置から {fromStart:F0}mm / {speedMmPerSec:F0}mm/s）");
                return;
            }

            float elapsed = Time.realtimeSinceStartup - _connectedSince - AutoStartSeconds;
            if (elapsed < 0f) return;

            // 実機のボタンと同じく、押しっぱなしではなく一定時間だけ押す。
            // 押す 0.4 秒 -> 離す 0.6 秒 を繰り返す。
            const float holdSeconds = 0.4f;
            const float cycleSeconds = 1.0f;
            if (elapsed % cycleSeconds < holdSeconds)
            {
                _buttonState |= Etsim.ButtonCenter;
                _forceHeld = true;
                AutoStartHolding = true;
            }
        }

        /// <summary>自動スタートが「走り出した」とみなす移動量 [mm]。</summary>
        const float AutoStartMovedMm = 20f;

        /// <summary>同じく、走っているとみなす速さ [mm/s]。沈み込みの滑りと区別する。</summary>
        const float AutoStartMovingMmPerSec = 30f;

        void OnDestroy()
        {
            Server?.Dispose();
            // 手動シミュレーションのまま終わると次のシーンで物理が止まる
            Physics.simulationMode = SimulationMode.FixedUpdate;
        }

        void OnApplicationQuit() => Server?.Dispose();
    }
}
