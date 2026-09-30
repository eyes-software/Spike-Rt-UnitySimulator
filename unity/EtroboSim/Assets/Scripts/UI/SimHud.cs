using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// 走行中の状態表示。IMGUI で描くのは、uGUI のプレハブを .unity に
    /// 持たせずに済ませるため（シーンの中身をコードだけで説明できる状態を保つ）。
    /// </summary>
    public sealed class SimHud : MonoBehaviour
    {
        public SimManager Manager;
        public CameraRig Rig;
        public NansyoController Nansyo;
        public SimBootstrap Boot;
        public NansyoJudge Judge;

        GUIStyle _panel;
        GUIStyle _label;
        GUIStyle _title;
        GUIStyle _button;
        GUIStyle _fold;
        Texture2D _panelTex;

        Vector2 _scroll;

        /// <summary>
        /// 節ごとの開閉。行が増えてゲームビューに収まらなくなったので、
        /// 見ないところはたたんでおけるようにした。次に起動したときも同じ状態にする。
        /// </summary>
        bool _openSensors = true, _openMotors = true, _openDrive = true,
             _openNansyo = true, _openView;

        const string PrefKey = "etsim.hud.fold";

        void LoadFolds()
        {
            int f = PlayerPrefs.GetInt(PrefKey, 0b01111);
            _openSensors = (f & 1) != 0;
            _openMotors = (f & 2) != 0;
            _openDrive = (f & 4) != 0;
            _openNansyo = (f & 8) != 0;
            _openView = (f & 16) != 0;
        }

        void SaveFolds()
        {
            int f = (_openSensors ? 1 : 0) | (_openMotors ? 2 : 0) | (_openDrive ? 4 : 0)
                    | (_openNansyo ? 8 : 0) | (_openView ? 16 : 0);
            PlayerPrefs.SetInt(PrefKey, f);
        }

        /// <summary>たためる見出し。開いていれば true。</summary>
        bool Fold(string title, ref bool open)
        {
            if (GUILayout.Button((open ? "- " : "+ ") + title, _fold))
            {
                open = !open;
                SaveFolds();
            }
            return open;
        }

        const float PanelWidth = 430f;
        const float PanelHeight = 900f;

        /// <summary>力士ボトルをボタン 1 回で動かす量 [mm]。</summary>
        const float RikishiStepMm = 20f;

        const float PanelMargin = 12f;

        void EnsureStyles()
        {
            if (_panel != null) return;

            _panelTex = new Texture2D(1, 1);
            _panelTex.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.09f, 0.82f));
            _panelTex.Apply();

            _panel = new GUIStyle
            {
                normal = { background = _panelTex },
                padding = new RectOffset(12, 12, 10, 10)
            };
            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.88f, 0.90f, 0.93f) },
                richText = true
            };
            _title = new GUIStyle(_label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.55f, 0.82f, 1f) }
            };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 11 };
            _fold = new GUIStyle(_label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                margin = new RectOffset(0, 0, 4, 1),
                normal = { textColor = new Color(0.72f, 0.86f, 1f) },
                hover = { textColor = Color.white }
            };

            LoadFolds();
        }

        /// <summary>
        /// パネルを出す側。走行体が出るのと反対側に置く。
        ///
        /// 走行体はスタート直後がいちばん見たいところなので、
        /// そこにパネルを重ねない。L コーススタートなら右、R なら左。
        /// </summary>
        float PanelLeft()
        {
            bool startsOnLeft = Nansyo?.Layout?.StartsOnLeft
                                ?? (Manager.Course != null
                                    && Manager.Course.StartPositionMm.x < Manager.Course.SheetWidthMm * 0.5f);

            if (!_sideLogged)
            {
                _sideLogged = true;
                Debug.Log($"[Etsim] パネルは{(startsOnLeft ? "右" : "左")}に出します"
                          + $"（走行体は{(startsOnLeft ? "L" : "R")}コース側から出ます）");
            }

            return startsOnLeft ? Screen.width - PanelWidth - PanelMargin : PanelMargin;
        }

        bool _sideLogged;

        void OnGUI()
        {
            if (Manager == null || Manager.Robot == null) return;
            EnsureStyles();

            // 画面に収まる高さにする。収まらないぶんはスクロールで届くようにして、
            // 行が黙って消えないようにしてある。見ない節はたためる。
            float panelHeight = Mathf.Min(PanelHeight, Screen.height - PanelMargin * 2f);
            var rect = new Rect(PanelLeft(), PanelMargin, PanelWidth, panelHeight);
            if (Rig != null) Rig.PanelRect = rect;

            GUILayout.BeginArea(rect, _panel);
            _scroll = GUILayout.BeginScrollView(_scroll, false, false,
                                                GUIStyle.none, GUI.skin.verticalScrollbar,
                                                GUIStyle.none);

            GUILayout.Label("ETロボコン シミュレータ "
                            + $"<size=11><color=#8d97a5>{SimVersion.Full}</color></size>", _title);
            GUILayout.Space(4);

            DrawConnection();
            GUILayout.Space(4);
            DrawCourseSide();
            GUILayout.Space(4);
            DrawStart();

            if (Fold("センサ", ref _openSensors)) DrawSensors();
            if (Fold("モータ", ref _openMotors)) DrawMotors();
            if (Fold("走行", ref _openDrive)) DrawTiming();
            if (Fold("難所", ref _openNansyo)) DrawNansyo();

            string mode = Rig != null ? ModeName(Rig.CurrentMode) : "-";
            if (Fold($"視点 : {mode}", ref _openView))
                GUILayout.Label($"<color=#8d97a5>{(Rig != null ? Rig.HelpText : "")}</color>", _label);

            GUILayout.Space(4);
            GUILayout.Label("<color=#8d97a5>R:開始位置へ戻す（ボトルも置き直す）"
                            + "  Space:中央ボタン＋フォースセンサ</color>", _label);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawConnection()
        {
            var server = Manager.Server;
            bool connected = server != null && server.ControllerConnected;
            string state = connected ? "<color=#68d391>接続中</color>" : "<color=#e0a458>待機中</color>";
            string mode = Manager.Lockstep ? "LOCKSTEP" : "FREERUN";

            GUILayout.Label($"制御プロセス : {state}  (UDP {server?.Port})", _label);
            GUILayout.Label($"モード : {mode}   受信 {server?.ReceivedCount ?? 0}  送信 {Manager.SentPackets}", _label);
        }

        /// <summary>
        /// スタートの合図。実機では人が押すもの（ハブの中央ボタン / フォースセンサ）で、
        /// どちらを使うかはチームによって違う。キーボードの Space は両方に入るが、
        /// ここからは片方だけ押せるので、どちらで待っているのかを切り分けられる。
        /// </summary>
        void DrawStart()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>スタート合図</b>", _label, GUILayout.Width(78));

            if (GUILayout.Button("中央ボタン", _button, GUILayout.Width(70)))
                Manager.PressCenterButton();
            if (GUILayout.Button("フォース", _button, GUILayout.Width(62)))
                Manager.PressForceSensor();

            GUILayout.Space(6);
            Manager.CenterLatched = GUILayout.Toggle(Manager.CenterLatched, "中央保持", _button, GUILayout.Width(62));
            Manager.ForceLatched = GUILayout.Toggle(Manager.ForceLatched, "フォース保持", _button, GUILayout.Width(80));
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// L コースと R コースの選択。
        ///
        /// コースは Awake で 1 度だけ組み立てるので、押すとシーンを読み直す。
        /// 走行中の記録は消えるが、難所の配置は引き継ぐ。
        /// </summary>
        void DrawCourseSide()
        {
            if (Boot == null || Nansyo?.Layout == null) return;

            bool left = Nansyo.Layout.StartsOnLeft;
            bool canSwitch = !string.IsNullOrEmpty(Boot.OtherSideCourseFile);

            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>コース</b>", _label, GUILayout.Width(78));

            GUI.enabled = canSwitch || left;
            if (GUILayout.Button(left ? "[L]" : "L", _button, GUILayout.Width(40)) && !left)
                Boot.SwitchCourseSide();

            GUI.enabled = canSwitch || !left;
            if (GUILayout.Button(!left ? "[R]" : "R", _button, GUILayout.Width(40)) && left)
                Boot.SwitchCourseSide();

            GUI.enabled = true;
            GUILayout.Label($"<color=#8d97a5>{Boot.ActiveCourseFile}</color>", _label);
            GUILayout.EndHorizontal();

            if (!canSwitch)
                GUILayout.Label("<color=#8d97a5>  反対側のコースのファイルがありません</color>", _label);
        }

        void DrawSensors()
        {
            var cs = Manager.ColorSensor;
            var us = Manager.Ultrasonic;
            var imu = Manager.Imu;

            GUILayout.Label($"  反射光 {cs.Reflection,3}   色 {ColorName(cs.ColorId)}", _label);
            GUILayout.Label($"  RGB ({cs.R,4},{cs.G,4},{cs.B,4})  HSV ({cs.HueDeg,3},{cs.Saturation,3},{cs.Value,3})", _label);
            GUILayout.Label($"  超音波 {(us.DistanceMm < 0 ? "----" : us.DistanceMm.ToString())} mm", _label);
            GUILayout.Label($"  フォース {(Manager.StartSignalHeld ? SimManager.StartSignalForceN : 0f),5:F1} N"
                            + $"  {(Manager.StartSignalHeld ? "<color=#68d391>押</color>" : "<color=#8d97a5>離</color>")}"
                            + $"   中央ボタン {(Manager.CenterButtonHeld ? "<color=#68d391>押</color>" : "<color=#8d97a5>離</color>")}", _label);
            GUILayout.Label($"  ヨー {imu.YawDeg,7:F2}°  (真値 {imu.TrueYawDeg,7:F2}°)", _label);
        }

        void DrawMotors()
        {
            var r = Manager.Robot;
            GUILayout.Label($"  出力   L {r.MotorLeft.AppliedPercent,4}   R {r.MotorRight.AppliedPercent,4}   A {r.MotorArm.AppliedPercent,4}", _label);
            GUILayout.Label($"  角度   L {r.MotorLeft.AngleDeg,7:F0}  R {r.MotorRight.AngleDeg,7:F0}", _label);
            GUILayout.Label($"  速度   L {r.MotorLeft.SpeedDps,7:F0}  R {r.MotorRight.SpeedDps,7:F0} deg/s", _label);
        }

        void DrawTiming()
        {
            var t = Manager.Timer;
            var pos = Manager.Robot.TruthPositionMm;

            GUILayout.Label($"  位置 ({pos.x,7:F0}, {pos.y,7:F0}) mm  方位 {Manager.Robot.TruthHeadingDeg,6:F1}°", _label);
            GUILayout.Label($"  速度 {Manager.Robot.TruthSpeedMmps,7:F0} mm/s", _label);

            string timerState = t.Finished ? "<color=#68d391>ゴール</color>"
                              : t.Running ? "<color=#f6e05e>計測中</color>"
                              : "<color=#8d97a5>未スタート</color>";
            GUILayout.Label($"  タイム {t.ElapsedSeconds,7:F2} s   {timerState}"
                            + $"   フィニッシュ {FinishText(t)}", _label);
            string remain = t.TimeUp ? $"<color=#fc8181>{t.RemainingSeconds,7:F2} s</color>"
                          : $"{t.RemainingSeconds,7:F2} s";
            GUILayout.Label($"  経過 {t.CompetitionElapsedSeconds,7:F2} s   残り {remain}"
                            + (t.GarageEntered ? "   <color=#68d391>ガレージ IN で停止</color>" : ""), _label);
            GUILayout.Label($"  CP {t.CheckpointsPassed}/{t.CheckpointTotal}   直近ゲート {t.LastGateId}", _label);
            DrawStartPose();

            if (Manager.OutOfCourse)
                GUILayout.Label("  <color=#fc8181>コースアウト</color>", _label);
        }

        /// <summary>
        /// 開始位置。R キーで戻る場所で、既定はコースの start_pose。
        ///
        /// 難所の手前だけを繰り返し試したいことが多いので、走らせて止めた場所を
        /// そのまま開始位置にできるようにしてある。
        /// </summary>
        void DrawStartPose()
        {
            var m = Manager;
            var p = m.StartPositionMm;

            GUILayout.BeginHorizontal();
            GUILayout.Label($"  開始 ({p.x:F0}, {p.y:F0}) {m.StartHeadingDeg:F0}°"
                            + (m.StartMoved ? " <color=#f6e05e>*</color>" : ""), _label,
                            GUILayout.Width(158));

            if (GUILayout.Button("現在地に", _button, GUILayout.Width(62)))
                m.SetStartHere();

            m.PlacementMode = GUILayout.Toggle(m.PlacementMode, "配置(P)", _button, GUILayout.Width(56));

            GUI.enabled = m.StartMoved;
            if (GUILayout.Button("既定へ", _button, GUILayout.Width(50)))
                m.RestoreCourseStart();
            GUI.enabled = true;

            if (GUILayout.Button("保存", _button, GUILayout.Width(40)) && Boot != null)
                Boot.SaveStartPoseToCourse();
            GUILayout.EndHorizontal();

            if (m.PlacementMode)
                GUILayout.Label("<color=#f6e05e>  配置中: ↑↓前後 ←→左右 , . 回転（Shift で 10 倍）"
                                + "／保存でコースに書き込む</color>", _label);
        }

        /// <summary>
        /// フィニッシュ（規約 5.14）。ガレージの中で 3 秒完全停止すると成立する。
        /// ガレージが見つからないコースでは判定しない。
        /// </summary>
        static string FinishText(LapTimer t)
        {
            if (t.Garage == null || !t.Garage.Found) return "<color=#8d97a5>ガレージ無し</color>";
            if (t.FinishEstablished) return "<color=#68d391>成立</color>";
            if (!t.InGarage) return "<color=#8d97a5>ガレージ外</color>";

            return $"<color=#f6e05e>ガレージ内 停止 {t.GarageStopSeconds:F1}"
                   + $"/{LapTimer.FinishHoldSeconds:F0} s</color>";
        }

        /// <summary>
        /// 難所の設定。大会当日まで分からないもの（キャリーボトルの色、
        /// ゲートの配置）をここから変える。
        /// </summary>
        void DrawNansyo()
        {
            if (Nansyo == null || Nansyo.Layout == null) return;
            if (!Nansyo.HasCarryBottle && !Nansyo.HasRikishiBottle && !Nansyo.Layout.HasGrid) return;

            // 物が無いときに行を消すと「設定が消えた」ように見える。そう書く。
            if (!Nansyo.HasCarryBottle && !Nansyo.HasRikishiBottle)
                GUILayout.Label("<color=#fc8181>  ボトルとゲートがありません"
                                + "（難所の入ったコースか確認してください）</color>", _label);


            if (Nansyo.HasCarryBottle)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("  キャリーボトル", _label, GUILayout.Width(110));
                foreach (var c in NansyoController.AllColors)
                {
                    bool on = Nansyo.CarryColor == c;
                    string text = on ? "[" + NansyoController.NameOf(c) + "]" : NansyoController.NameOf(c);
                    if (GUILayout.Button(text, _button, GUILayout.Width(38)) && !on)
                        Nansyo.SetCarryColor(c);
                }
                GUILayout.EndHorizontal();
            }

            if (Nansyo.HasRikishiBottle && Nansyo.Layout.DohyoRadiusMm > 0f)
            {
                var o = Nansyo.RikishiOffsetMm;
                GUILayout.BeginHorizontal();
                GUILayout.Label("  力士ボトル", _label, GUILayout.Width(110));
                GUILayout.Label($"({o.x,4:F0},{o.y,4:F0})mm", _label, GUILayout.Width(96));

                // 規約 5.15.1 は「土俵の任意の位置」。土俵からはみ出す手前で止まる。
                if (GUILayout.Button("X-", _button, GUILayout.Width(28)))
                    Nansyo.SetRikishiOffset(o + new Vector2(-RikishiStepMm, 0f));
                if (GUILayout.Button("X+", _button, GUILayout.Width(28)))
                    Nansyo.SetRikishiOffset(o + new Vector2(RikishiStepMm, 0f));
                if (GUILayout.Button("Y-", _button, GUILayout.Width(28)))
                    Nansyo.SetRikishiOffset(o + new Vector2(0f, -RikishiStepMm));
                if (GUILayout.Button("Y+", _button, GUILayout.Width(28)))
                    Nansyo.SetRikishiOffset(o + new Vector2(0f, RikishiStepMm));
                if (GUILayout.Button("中央", _button, GUILayout.Width(38)))
                    Nansyo.SetRikishiOffset(Vector2.zero);
                GUILayout.EndHorizontal();
            }

            if (!Nansyo.Layout.HasGrid) return;

            foreach (var c in NansyoController.AllColors)
            {
                if (!Nansyo.HasGate(c)) continue;

                var cell = Nansyo.GetGateCell(c);
                var other = Nansyo.Neighbour(c, cell.x, cell.y);
                string dir = NansyoController.IsHorizontal(c) ? "横" : "縦";

                GUILayout.BeginHorizontal();
                GUILayout.Label($"  {NansyoController.NameOf(c)}ゲート ({dir})", _label, GUILayout.Width(110));
                GUILayout.Label($"G{cell.x}-{cell.y}+G{other.x}-{other.y}", _label, GUILayout.Width(96));

                // 向きは色で決まっているので、動かすのは片方の位置だけ
                if (GUILayout.Button("X-", _button, GUILayout.Width(28)))
                    Nansyo.SetGateCell(c, Mathf.Max(1, cell.x - 1), cell.y);
                if (GUILayout.Button("X+", _button, GUILayout.Width(28)))
                    Nansyo.SetGateCell(c, Mathf.Min(NansyoLayout.GridSize, cell.x + 1), cell.y);
                if (GUILayout.Button("Y-", _button, GUILayout.Width(28)))
                    Nansyo.SetGateCell(c, cell.x, Mathf.Max(1, cell.y - 1));
                if (GUILayout.Button("Y+", _button, GUILayout.Width(28)))
                    Nansyo.SetGateCell(c, cell.x, Mathf.Min(NansyoLayout.GridSize, cell.y + 1));
                GUILayout.EndHorizontal();
            }

            DrawJudge();
            GUILayout.Label("<color=#8d97a5>  通過順は 赤→青→黄（表 5-2）。"
                            + $"力士は土俵内 {Nansyo.RikishiRangeMm:F0}mm まで</color>", _label);
        }

        /// <summary>
        /// 難所の成立判定（規約 5.15 / 5.16.2 / 5.17.3）。
        ///
        /// 3 つとも「リザルトポイント提示時」の状態で決まるので、
        /// 走行中はいつでもひっくり返る。いまの状態をそのまま出す。
        /// </summary>
        void DrawJudge()
        {
            if (Judge == null) return;

            string push = Ok(Judge.PushedOut, "成立", "不成立");

            string delivery = !Judge.Delivered
                ? Ng("不成立")
                : Judge.DeliveredColorMatch
                    ? Ok(true, "色一致 (5)", "")
                    : $"<color=#f6e05e>{NansyoController.NameOf(Judge.DeliveredZone.Value)}ゾーン (1)</color>";

            string rally = Judge.RallyLaps > 0
                ? $"<color=#68d391>{Judge.RallyLaps} 周回 ({NansyoJudge.RallyPoints(Judge.RallyLaps)})</color>"
                : Ng("0 周回");

            GUILayout.Label($"  判定 押し出し {push}   デリバリー {delivery}", _label);
            GUILayout.Label($"       ラリー {rally}  次は {NansyoController.NameOf(Judge.NextGate)}"
                            + $"   <b>難所 {Judge.Points} ポイント</b>", _label);
        }

        static string Ok(bool on, string yes, string no)
            => on ? $"<color=#68d391>{yes}</color>" : Ng(no);

        static string Ng(string text) => $"<color=#8d97a5>{text}</color>";

        static string ModeName(CameraRig.Mode m) => m switch
        {
            CameraRig.Mode.Orbit => "ORBIT（注視点まわりを回る）",
            CameraRig.Mode.Chase => "CHASE（走行体を追う）",
            CameraRig.Mode.Onboard => "ONBOARD（走行体の視点）",
            _ => "FREE（自由飛行）"
        };

        static string ColorName(byte id) => id switch
        {
            1 => "BLACK",
            2 => "GRAY",
            3 => "WHITE",
            4 => "RED",
            5 => "ORANGE",
            6 => "YELLOW",
            7 => "GREEN",
            8 => "CYAN",
            9 => "BLUE",
            10 => "VIOLET",
            11 => "MAGENTA",
            _ => "NONE"
        };

        void OnDestroy()
        {
            if (_panelTex != null) Destroy(_panelTex);
        }
    }
}
