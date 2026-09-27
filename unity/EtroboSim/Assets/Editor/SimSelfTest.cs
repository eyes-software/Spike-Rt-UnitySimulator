using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Etrobo.Sim.Net;

namespace Etrobo.Sim.EditorTools
{
    /// <summary>
    /// 再生状態にせず確認できる範囲を自動で検証する。
    ///
    /// 物理そのものは実際に走らせないと分からないが、その手前にある
    /// 「コース JSON の解釈」「円弧・ベジエの折れ線化」「カラーセンサが読む色」
    /// 「パケットのバイト配置」は静的に検証できる。ここが崩れていると
    /// 走らせても原因が分からなくなるので、先にここで落としておく。
    ///
    /// コマンドラインからの実行:
    ///   Unity.exe -batchmode -nographics -quit -projectPath &lt;path&gt; \
    ///             -executeMethod Etrobo.Sim.EditorTools.SimSelfTest.RunFromCommandLine
    /// </summary>
    public static class SimSelfTest
    {
        static readonly List<string> _failures = new List<string>();
        static readonly StringBuilder _report = new StringBuilder();

        [MenuItem("ETロボコン/自己テストを実行", false, 40)]
        public static void RunFromMenu()
        {
            if (Run()) Debug.Log(_report.ToString());
            else Debug.LogError(_report.ToString());
        }

        public static void RunFromCommandLine()
        {
            bool ok = Run();
            Console.WriteLine(_report.ToString());
            EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool Run()
        {
            _failures.Clear();
            _report.Clear();
            _report.AppendLine("=== ETロボコンシミュレータ 自己テスト ===");

            try
            {
                CheckPacketLayout();
                CheckArcGeometry();
                CheckCourseLoading();
                CheckColorSampling();
                CheckLapTimer();
                CheckUltrasonic();
                CheckRobotSpecFiles();
                CheckArmGrip();
                CheckNansyo();
                CheckNansyoLayout();
                CheckNansyoJudge();
                CheckGarage();
                CheckOfficialCourse();
                CheckRasterizerMatchesGeometry();
            }
            catch (Exception e)
            {
                Fail("テスト中に例外: " + e);
            }

            _report.AppendLine();
            if (_failures.Count == 0)
            {
                _report.AppendLine("すべて通りました");
                return true;
            }

            _report.AppendLine($"{_failures.Count} 件失敗しました");
            return false;
        }

        // ---- 個別のテスト ------------------------------------------------

        static void CheckPacketLayout()
        {
            Section("パケットのバイト配置");

            var buffer = new byte[Etsim.SensorPacketSize];
            var sensor = new SensorPacket
            {
                Seq = 0x11223344,
                Reflection = 42,
                UltrasonicMm = -1,
                YawDeg = -12.5f,
                CountL = -1000,
                CountR = 2000,
                BatteryMv = 8000,
                TruthXMm = 1234.5f
            };
            sensor.WriteTo(buffer);

            // C 側 (protocol/etsim_protocol.h) と同じオフセットで読み直せること
            Expect("magic", ReadU32(buffer, 0), Etsim.MagicSensor);
            Expect("version", ReadU16(buffer, 4), Etsim.ProtocolVersion);
            Expect("seq", ReadU32(buffer, 8), 0x11223344u);
            Expect("reflection", ReadI32(buffer, 24), 42);
            Expect("ultrasonic_mm", ReadI32(buffer, 44), -1);
            Expect("yaw_deg", ReadF32(buffer, 56), -12.5f);
            Expect("motor_count[0]", ReadI32(buffer, 92), -1000);
            Expect("motor_count[1]", ReadI32(buffer, 96), 2000);
            Expect("battery_mv", ReadI32(buffer, 132), 8000);
            Expect("truth_x_mm", ReadF32(buffer, 136), 1234.5f);

            // アクチュエータ側は受信経路をそのまま通す
            var raw = new byte[Etsim.ActuatorPacketSize];
            WriteU32(raw, 0, Etsim.MagicActuator);
            WriteU16(raw, 4, Etsim.ProtocolVersion);
            WriteU16(raw, 6, Etsim.AFlagLockstep);
            WriteU32(raw, 8, 7);
            WriteI32(raw, 16, -60);   // motor_power[0]
            WriteI32(raw, 20, 60);    // motor_power[1]
            raw[28] = Etsim.MotorModePower;
            raw[31] = 0x03;           // reset_count_mask
            raw[35] = 1;              // reset_imu

            if (!ActuatorPacket.TryParse(raw, raw.Length, out var cmd))
            {
                Fail("アクチュエータパケットを解釈できませんでした");
                return;
            }
            Expect("actuator.seq", cmd.Seq, 7u);
            Expect("actuator.PowerL", cmd.PowerL, -60);
            Expect("actuator.PowerR", cmd.PowerR, 60);
            Expect("actuator.Lockstep", cmd.Lockstep, true);
            Expect("actuator.ResetCountMask", cmd.ResetCountMask, (byte)0x03);
            Expect("actuator.ResetImu", cmd.ResetImu, (byte)1);

            // 長さ不足やマジック違いは弾けること
            if (ActuatorPacket.TryParse(raw, 10, out _)) Fail("短いパケットを受け入れてしまいました");
            var broken = (byte[])raw.Clone();
            broken[0] ^= 0xFF;
            if (ActuatorPacket.TryParse(broken, broken.Length, out _)) Fail("マジック違いを受け入れてしまいました");
        }

        static void CheckArcGeometry()
        {
            Section("円弧の折れ線化");

            // (400,0) から (0,400) へ半径 400 の反時計回り。中心は原点。
            var pts = new List<Vector2> { new Vector2(400f, 0f) };
            CourseGeometry.AppendArc(pts, new Vector2(400f, 0f), new Vector2(0f, 400f),
                                     400f, false, true, 0.2f);

            if (pts.Count < 8)
            {
                Fail($"円弧の分割数が少なすぎます ({pts.Count})");
                return;
            }

            Expect("終点 x", pts[pts.Count - 1].x, 0f, 0.5f);
            Expect("終点 y", pts[pts.Count - 1].y, 400f, 0.5f);

            // すべての点が中心から半径 400 の位置にあること
            float worst = 0f;
            foreach (var p in pts) worst = Mathf.Max(worst, Mathf.Abs(p.magnitude - 400f));
            Expect("半径の最大誤差", worst, 0f, 0.5f);

            // 反時計回りなので中間点は第 1 象限を通る
            var mid = pts[pts.Count / 2];
            if (mid.x <= 0f || mid.y <= 0f)
                Fail($"円弧が逆回りです (中間点 {mid})");

            float length = CourseGeometry.PolylineLength(pts);
            Expect("弧長", length, 400f * Mathf.PI * 0.5f, 1.0f);
        }

        static void CheckCourseLoading()
        {
            Section("コース定義の読み込み");

            var course = LoadCourse("practice_oval.json");
            if (course == null) return;

            Expect("シート幅", course.SheetWidthMm, 4000f, 0.01f);
            Expect("パス数", course.Paths.Count, 1);
            Expect("ゲート数", course.Gates.Count, 5);

            var field = new CourseField(course);
            Expect("折れ線の本数", field.PathPolylines.Count, 1);

            var loop = field.PathPolylines[0];
            // closed 指定なので始点と終点が一致すること
            Expect("閉ループ", (loop[0] - loop[loop.Count - 1]).magnitude, 0f, 0.01f);

            // 角丸長方形 (直線 2200x2 + 600x2 + 半径 400 の四分円 4 つ) の周長
            float expected = 2f * 2200f + 2f * 600f + 2f * Mathf.PI * 400f;
            Expect("周長", CourseGeometry.PolylineLength(loop), expected, 5f);
        }

        static void CheckColorSampling()
        {
            Section("カラーセンサが読む色");

            var course = LoadCourse("practice_oval.json");
            if (course == null) return;
            var field = new CourseField(course);

            // 下側の直線は y=300、線幅 20mm
            var onLine = field.SampleColor(new Vector2(2000f, 300f));
            var offLine = field.SampleColor(new Vector2(2000f, 500f));
            var justOutside = field.SampleColor(new Vector2(2000f, 311f));

            Expect("ライン上は黒", Luminance(onLine) < 0.2f, true);
            Expect("ライン外はシート色", Luminance(offLine) > 0.8f, true);
            Expect("線幅 20mm の外側", Luminance(justOutside) > 0.8f, true);

            // 線の縁 (y=310) のすぐ内側はまだ黒
            var justInside = field.SampleColor(new Vector2(2000f, 309f));
            Expect("線幅 20mm の内側", Luminance(justInside) < 0.2f, true);

            // シートの外は真っ黒 (台から外れた状態)
            var outside = field.SampleColor(new Vector2(-100f, 300f));
            Expect("シート外", Luminance(outside) < 0.2f, true);

            // ラインまでの距離
            Expect("ラインまでの距離", field.DistanceToNearestLine(new Vector2(2000f, 400f)), 100f, 1f);
        }

        static void CheckLapTimer()
        {
            Section("計測ゲート");

            var gates = new List<CourseGate>
            {
                new CourseGate { Id = "start", Kind = GateKind.Start, From = new Vector2(0f, -50f), To = new Vector2(0f, 50f) },
                new CourseGate { Id = "cp",    Kind = GateKind.Checkpoint, From = new Vector2(100f, -50f), To = new Vector2(100f, 50f) },
                new CourseGate { Id = "goal",  Kind = GateKind.Goal, From = new Vector2(200f, -50f), To = new Vector2(200f, 50f) }
            };

            var timer = new LapTimer(gates);
            timer.Reset();
            Expect("チェックポイント総数", timer.CheckpointTotal, 1);

            timer.Step(new Vector2(-10f, 0f), 0.01f);   // 初回は前回位置の記録だけ
            timer.Step(new Vector2(10f, 0f), 0.01f);    // start を通過
            Expect("計測開始", timer.Running, true);

            timer.Step(new Vector2(110f, 0f), 0.01f);   // checkpoint
            Expect("チェックポイント通過", timer.CheckpointsPassed, 1);

            timer.Step(new Vector2(210f, 0f), 0.01f);   // goal
            Expect("ゴール", timer.Finished, true);
            // start を跨いだステップで計測が始まるので、goal までは 2 区間ぶん
            Expect("経過時間", timer.ElapsedSeconds, 0.02f, 0.001f);

            // チェックポイントを迂回してゴール線だけ跨いだ場合はゴールと認めない。
            // ゴール線だけを長くして、cp の範囲外を通れるようにする。
            var detourGates = new List<CourseGate>
            {
                new CourseGate { Id = "start", Kind = GateKind.Start, From = new Vector2(0f, -50f), To = new Vector2(0f, 50f) },
                new CourseGate { Id = "cp",    Kind = GateKind.Checkpoint, From = new Vector2(100f, -50f), To = new Vector2(100f, 50f) },
                new CourseGate { Id = "goal",  Kind = GateKind.Goal, From = new Vector2(200f, -50f), To = new Vector2(200f, 250f) }
            };

            var skipper = new LapTimer(detourGates);
            skipper.Reset();
            skipper.Step(new Vector2(-10f, 0f), 0.01f);
            skipper.Step(new Vector2(10f, 0f), 0.01f);    // start を通過
            skipper.Step(new Vector2(10f, 200f), 0.01f);  // cp の範囲外へ迂回
            skipper.Step(new Vector2(210f, 200f), 0.01f); // cp を通らずに goal 線だけ跨ぐ
            Expect("チェックポイント未通過ならゴールにしない", skipper.Finished, false);
            Expect("迂回中も計測は続く", skipper.Running, true);
        }

        /// <summary>
        /// 公式コース (PDF から変換したもの) が正しく読めているかを確かめる。
        ///
        /// ここで見たいのは寸法より「どこが何色に見えるか」。特にブロックエリアの
        /// カラーサークルは黒いラインの上に重なっているので、描画順 z が効いていないと
        /// サークルが線に隠れてカラーセンサが色を読めなくなる。
        /// </summary>
        /// <summary>
        /// 超音波センサ。床を拾わないこと、正面の壁は拾うこと。
        ///
        /// ビームは円錐状に広がるので、下端はどこかで必ず床に当たる
        /// （取り付け高さ 60mm / tan(12度) で 282mm 先）。音波は浅い角度で
        /// 当たった面からは戻ってこないので、そこを落とさないと
        /// 何も無いところで一定の距離を返し続ける。
        /// </summary>
        static void CheckUltrasonic()
        {
            Section("超音波センサ");

            var spec = new RobotSpec();
            var root = new GameObject("UltrasonicTest");
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);

            try
            {
                // 床は 4m 四方の薄い板。上面が y=0。
                floor.transform.position = new Vector3(0f, -0.01f, 0f);
                floor.transform.localScale = new Vector3(4f, 0.02f, 4f);

                // センサは取り付け高さで前 (+Z) を向く
                var mount = new GameObject("mount").transform;
                mount.SetParent(root.transform);
                mount.position = new Vector3(0f, spec.UltrasonicHeightMm * CourseSpace.MmToM, 0f);
                mount.rotation = Quaternion.identity;

                var model = new UltrasonicSensorModel(spec, mount, Physics.DefaultRaycastLayers);

                // 壁はまず遠くへ退避させておく
                wall.transform.position = new Vector3(0f, 0.05f, 50f);
                wall.transform.localScale = new Vector3(1f, 0.1f, 0.02f);
                Physics.SyncTransforms();

                model.Step();
                Expect("床だけなら検出しない", model.Present, false);

                // 正面 300mm に高さ 100mm の壁を置く
                wall.transform.position = new Vector3(0f, 0.05f, 0.300f);
                Physics.SyncTransforms();

                model.Step();
                Expect("正面の壁を検出する", model.Present, true);
                // 壁は厚さ 20mm の板を中心 300mm に置いてあるので、手前の面は 290mm。
                // ノイズの標準偏差は 3mm なので、余裕を見て 4 シグマで判定する。
                Expect("壁までの距離", model.DistanceMm, 290, 12);

                // 斜めに置いた壁。法線がセンサから大きく外れると返らない
                wall.transform.rotation = Quaternion.Euler(0f, 75f, 0f);
                Physics.SyncTransforms();

                model.Step();
                Expect("浅い角度の壁は返らない", model.Present, false);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(wall);
                UnityEngine.Object.DestroyImmediate(floor);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// 同梱の走行体パラメータが読めること。キー名を打ち間違えると
        /// 既定値のまま静かに走ってしまい、実機と合わない理由が見えなくなる。
        /// </summary>
        static void CheckRobotSpecFiles()
        {
            Section("走行体パラメータ");

            foreach (var name in new[] { "robot_spike.json", "robot_rehacks_2026.json" })
            {
                string path = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", "..", "..", "course", name));
                if (!File.Exists(path)) path = Path.Combine(Application.streamingAssetsPath, name);
                if (!File.Exists(path)) { Fail($"{name} が見つかりません"); continue; }

                RobotSpec spec;
                try { spec = RobotSpec.FromJson(File.ReadAllText(path)); }
                catch (Exception e) { Fail($"{name} を読めません: {e.Message}"); continue; }

                // 公開資料で分かっている値。ここがずれていたら差し替え忘れ。
                Expect($"{name} 車輪径", spec.WheelDiameterMm, 56f, 0.01f);
                Expect($"{name} トレッドが正", spec.TreadMm > 0f, true);
                Expect($"{name} 質量が正", spec.MassKg > 0f, true);
                Expect($"{name} 左モータは鏡像", spec.LeftMotorMirrored, true);

                // キー名の打ち間違いは、既定値のまま静かに走るので気づけない。
                // 値を 1 つずつ動かして、結果が変わらないキー = 読まれていないキー を挙げる。
                CheckNoUnusedKeys(name, File.ReadAllText(path));

                if (name != "robot_rehacks_2026.json") continue;

                // 公式 LDraw モデル (ReHackSM6.ldr) から読み取った寸法。
                // 1 LDU = 0.4mm なので、これらは端数まで意味のある値。
                // 組立図が改訂されたらここが落ちる。
                Expect("Re-HackS トレッド", spec.TreadMm, 112.0f, 0.01f);
                Expect("Re-HackS 車輪幅", spec.WheelWidthMm, 14.0f, 0.01f);
                Expect("Re-HackS カラーセンサ前方", spec.ColorSensorXMm, 40.0f, 0.01f);
                Expect("Re-HackS カラーセンサ高さ", spec.ColorSensorHeightMm, 8.4f, 0.01f);
                Expect("Re-HackS 距離センサ前方", spec.UltrasonicXMm, 17.6f, 0.01f);
                Expect("Re-HackS 距離センサ高さ", spec.UltrasonicHeightMm, 148.0f, 0.01f);
                Expect("Re-HackS キャスタ位置", spec.CasterOffsetXMm, -86.4f, 0.01f);
                Expect("Re-HackS 全長", spec.BodyLengthMm, 205.6f, 0.01f);
                Expect("Re-HackS 全幅", spec.BodyWidthMm, 136.0f, 0.01f);
                Expect("Re-HackS 全高", spec.BodyHeightMm, 159.6f, 0.01f);

                // 組立図から作った見た目メッシュ。無くても走れるが、
                // 指定してあるのに読めない状態は取り違えなので落とす。
                if (!string.IsNullOrEmpty(spec.VisualMeshFile))
                {
                    string meshPath = Path.GetFullPath(
                        Path.Combine(Application.dataPath, "..", "..", "..", "course", spec.VisualMeshFile));
                    if (!File.Exists(meshPath))
                        meshPath = Path.Combine(Application.streamingAssetsPath, spec.VisualMeshFile);

                    var groups = LdrawMesh.Load(meshPath);
                    Expect("走行体メッシュが読める", groups != null, true);
                    if (groups != null)
                    {
                        var names = new List<string>();
                        int tris = 0;
                        foreach (var g in groups)
                        {
                            names.Add(g.Name);
                            tris += (int)(g.Mesh.triangles.Length / 3);
                        }
                        _report.AppendLine($"  --   まとまり {string.Join(", ", names)}  三角形 {tris}");
                        Expect("車体のまとまりがある", names.Contains("body"), true);
                        Expect("左右の車輪がある",
                               names.Contains("wheel_l") && names.Contains("wheel_r"), true);
                        Expect("アームがある", names.Contains("arm"), true);
                    }
                }
            }
        }

        /// <summary>
        /// JSON の数値・真偽キーを 1 つずつ変えてみて、読み込み結果が変わらないものを探す。
        /// 変わらなければ RobotSpec.FromJson がそのキーを見ていない = 綴り間違い。
        /// </summary>
        static void CheckNoUnusedKeys(string name, string json)
        {
            string baseline = JsonUtility.ToJson(RobotSpec.FromJson(json));
            var unused = new List<string>();

            foreach (Match m in Regex.Matches(json, @"""([A-Za-z0-9_]+)""\s*:\s*(-?[0-9.]+|true|false)"))
            {
                string key = m.Groups[1].Value;
                if (key.StartsWith("_")) continue;

                string value = m.Groups[2].Value;
                string moved = value == "true" ? "false"
                             : value == "false" ? "true"
                             : (float.Parse(value, CultureInfo.InvariantCulture) + 12345f)
                                   .ToString(CultureInfo.InvariantCulture);

                string probe = json.Substring(0, m.Index)
                             + "\"" + key + "\": " + moved
                             + json.Substring(m.Index + m.Length);

                if (JsonUtility.ToJson(RobotSpec.FromJson(probe)) == baseline) unused.Add(key);
            }

            int keyCount = Regex.Matches(json, @"""[A-Za-z0-9_]+""\s*:").Count;
            _report.AppendLine($"  --   {name} のキー {keyCount} 個を照合");
            Expect($"{name} に読まれないキーが無い", unused.Count == 0, true);
            if (unused.Count > 0)
                _report.AppendLine("       読まれていないキー: " + string.Join(", ", unused));
        }

        /// <summary>
        /// 難所（ET相撲・ボトルデリバリー・ETラリー）の配置。
        /// 置き場所はコース図に印刷されているので、tools/add_nansyo.py がそこから読む。
        /// ここでは「読み取りが規約どおりか」を確かめる。
        /// </summary>
        static void CheckNansyo()
        {
            Section("難所の配置");

            var course = LoadCourse("etrobo_2026_L_nansyo.json", required: false);
            if (course == null)
            {
                _report.AppendLine("  --   etrobo_2026_L_nansyo.json が無いので飛ばしました");
                return;
            }

            CourseProp rikishi = null, carry = null;
            var gates = new List<CourseProp>();
            foreach (var p in course.Props)
            {
                if (p.Id == "rikishi_bottle") rikishi = p;
                else if (p.Id == "carry_bottle") carry = p;
                else if (p.Type == "gate") gates.Add(p);
            }

            // ET相撲: 力士ボトルは土俵の中
            Expect("力士ボトルがある", rikishi != null, true);
            if (rikishi != null)
            {
                // 土俵 = コース上でいちばん大きい円
                CourseArea dohyo = null;
                foreach (var a in course.Areas)
                    if (a.Shape == AreaShape.Circle && a.Center.x < course.SheetWidthMm * 0.5f
                        && (dohyo == null || a.Radius > dohyo.Radius)) dohyo = a;

                Expect("土俵が見つかる", dohyo != null, true);
                if (dohyo != null)
                {
                    float d = Vector2.Distance(rikishi.Position, dohyo.Center);
                    Expect("力士ボトルが土俵の中", d + rikishi.Size.x * 0.5f <= dohyo.Radius, true);
                }

                // 難所組立図 1.0.2: PET 500丸 φ68 x 210H、水入りで約 105g
                Expect("力士ボトルの径", rikishi.Size.x, 68f, 0.1f);
                Expect("力士ボトルの高さ", rikishi.Size.z, 210f, 0.1f);
                Expect("力士ボトルの質量", rikishi.Mass, 0.105f, 0.001f);
                Expect("力士ボトルの重心が低い", rikishi.CenterOfMassHeightMm < rikishi.Size.z * 0.5f, true);
            }

            // ボトルデリバリー: キャリーボトルはピックアップポジションの上
            Expect("キャリーボトルがある", carry != null, true);
            if (carry != null)
            {
                Expect("キャリーボトルの径", carry.Size.x, 62f, 0.1f);
                Expect("キャリーボトルの高さ", carry.Size.z, 150f, 0.1f);
                Expect("キャリーボトルの質量", carry.Mass, 0.105f, 0.001f);
            }

            // ETラリー: 赤・青・黄の 3 つ
            Expect("ゲートが 3 つ", gates.Count, 3);

            // 既定値ではなく、実際に使う走行体の寸法で確かめる
            var spec = LoadRobotSpec("robot_rehacks_2026.json") ?? new RobotSpec();
            foreach (var g in gates)
            {
                // ゲートポジションの間隔は 250mm。脚をその上に置くので内寸はこれだけ。
                Expect($"{g.Id} の内寸", g.Size.x, 250f - g.PipeDiameterMm, 0.5f);
                Expect($"{g.Id} の高さ", g.Size.z, 245f, 0.5f);

                // 走行体が通れること。ここが通らない配置は置く意味がない。
                Expect($"{g.Id} を走行体が通れる", g.Size.x > spec.BodyWidthMm, true);
                Expect($"{g.Id} を走行体がくぐれる", g.Size.z > spec.BodyHeightMm, true);
            }
            if (gates.Count > 0)
                _report.AppendLine(string.Format(
                    "  --   ゲート内寸 {0:F0}mm に対し走行体の全幅 {1:F0}mm（片側 {2:F0}mm の余裕）",
                    gates[0].Size.x, spec.BodyWidthMm, (gates[0].Size.x - spec.BodyWidthMm) * 0.5f));
        }

        /// <summary>
        /// 難所の配置をコース図から読む処理。ゲートポジションの番号付けは
        /// 規約 5.17.1 で決まっていて、間違えるとゲートが別の場所に立つ。
        /// </summary>
        static void CheckNansyoLayout()
        {
            Section("難所の配置の読み取り");

            var left = LoadCourse("etrobo_2026_L.json", required: false);
            if (left == null)
            {
                _report.AppendLine("  --   etrobo_2026_L.json が無いので飛ばしました");
                return;
            }

            var l = NansyoLayout.FromCourse(left);
            Expect("L コースは左から出る", l.StartsOnLeft, true);
            Expect("5x5 の格子が見つかる", l.HasGrid, true);
            Expect("ゲートポジションの間隔", l.GridPitchMm, 250f, 0.5f);

            // 規約 図 5-25: G1-1 が左上、X は右へ、Y は下へ
            if (l.TryGetGatePosition(1, 1, out var g11)) Expect("G1-1", g11, new Vector2(1057f, 1495f));
            if (l.TryGetGatePosition(5, 1, out var g51)) Expect("G5-1", g51, new Vector2(2057f, 1495f));
            if (l.TryGetGatePosition(1, 5, out var g15)) Expect("G1-5", g15, new Vector2(1057f, 495f));
            if (l.TryGetGatePosition(5, 5, out var g55)) Expect("G5-5", g55, new Vector2(2057f, 495f));

            Expect("ピックアップポジションが見つかる", l.HasPickup, true);
            // 図面から出た値なので端数がある。1mm 見ておく。
            if (l.HasPickup)
            {
                Expect("ピックアップ位置 x", l.Pickup.x, 320.6f, 1f);
                Expect("ピックアップ位置 y", l.Pickup.y, 804.4f, 1f);
            }
            Expect("土俵の半径", l.DohyoRadiusMm, 183.8f, 0.5f);

            // R コースは左右反転（規約 5.17.1「記載した例を左右反転した鏡像配置」）
            var right = LoadCourse("etrobo_2026_R.json", required: false);
            if (right != null)
            {
                var r = NansyoLayout.FromCourse(right);
                Expect("R コースは右から出る", r.StartsOnLeft, false);
                Expect("R も 5x5 が見つかる", r.HasGrid, true);
                if (r.TryGetGatePosition(1, 1, out var r11) && r.TryGetGatePosition(5, 1, out var r51))
                {
                    // 鏡像なので X が増えると左へ動く
                    Expect("R は X が増えると左へ", r51.x < r11.x, true);
                    Expect("R も Y が増えると下へ",
                           r.TryGetGatePosition(1, 5, out var r15) && r15.y < r11.y, true);
                }
            }

            // 向きは色で決まる（規約 表 5-2）。隣の取り方がそれに従うこと。
            var red = NansyoController.Neighbour2(NansyoController.BottleColor.Red, 2, 5);
            var blue = NansyoController.Neighbour2(NansyoController.BottleColor.Blue, 5, 3);
            var yellow = NansyoController.Neighbour2(NansyoController.BottleColor.Yellow, 1, 2);
            Expect("赤は横に並ぶ", red, new Vector2Int(3, 5));
            Expect("青は縦に並ぶ", blue, new Vector2Int(5, 4));
            Expect("黄は横に並ぶ", yellow, new Vector2Int(2, 2));

            // 開始位置をコース JSON へ書き戻す処理。
            // 書式を決め打ちせず、対応する括弧で範囲を取れていること。
            // （逐語的文字列なので " は "" と書く）
            const string pretty = @"{ ""name"": ""x"", ""start_pose"": { ""position"": [ 1.0, 2.0 ], ""heading"": -90.0 }, ""units"": ""mm"" }";
            string patched = CourseFileWriter.ReplaceStartPose(pretty, new Vector2(2530f, 3467.3f), -90f);
            Expect("start_pose を差し替える",
                   patched.Contains(@"""position"": [2530.0, 3467.3]"), true);
            Expect("方位も入る", patched.Contains(@"""heading"": -90.0"), true);
            Expect("他の項目は残る", patched.Contains(@"""units"": ""mm"""), true);
            Expect("start_pose は 1 つだけ",
                   patched.Split(new[] { @"""start_pose""" }, System.StringSplitOptions.None).Length, 2);

            // 中に文字列で { } が入っていても、対応を取り違えないこと
            const string tricky = @"{""start_pose"": {""note"": ""a{b}c"", ""heading"": 0.0}, ""z"": 1}";
            string patched2 = CourseFileWriter.ReplaceStartPose(tricky, Vector2.zero, 90f);
            Expect("文字列の括弧に釣られない", patched2.EndsWith(@", ""z"": 1}"), true);
            Expect("差し替え後に古い中身が残らない", patched2.Contains("a{b}c"), false);

            // start_pose が無いコースにも足せる
            string added = CourseFileWriter.ReplaceStartPose(
                @"{ ""units"": ""mm"" }", new Vector2(10f, 20f), 45f);
            Expect("無ければ足す", added.Contains(@"""start_pose"""), true);
            Expect("足しても元は残る", added.Contains(@"""units"": ""mm"""), true);

            // 端では内側へ折り返す
            Expect("右端の横ゲートは内側へ",
                   NansyoController.Neighbour2(NansyoController.BottleColor.Red, 5, 1),
                   new Vector2Int(4, 1));
            Expect("下端の縦ゲートは内側へ",
                   NansyoController.Neighbour2(NansyoController.BottleColor.Blue, 1, 5),
                   new Vector2Int(1, 4));

            // 力士ボトルは「土俵の任意の位置」（規約 5.15.1）。
            // ただし底が土俵からはみ出さないところまでで頭打ちにしている。
            // 土俵 183.8mm / ボトル半径 34mm なので、動かせるのは 149.8mm まで。
            const float dohyo = 183.8f, bottle = 34f;
            Expect("土俵の中なら動かさない",
                   NansyoController.ClampToDohyo(new Vector2(60f, -40f), dohyo, bottle),
                   new Vector2(60f, -40f));
            Expect("中心はそのまま",
                   NansyoController.ClampToDohyo(Vector2.zero, dohyo, bottle), Vector2.zero);

            var far = NansyoController.ClampToDohyo(new Vector2(300f, 0f), dohyo, bottle);
            Expect("はみ出す指定は土俵の縁で止まる", far.x, dohyo - bottle, 0.05f);
            Expect("止めても向きは変わらない", far.y, 0f, 0.001f);

            var diag = NansyoController.ClampToDohyo(new Vector2(200f, 200f), dohyo, bottle);
            Expect("斜めでも縁までの距離は同じ", diag.magnitude, dohyo - bottle, 0.05f);
            Expect("斜めの向きは保つ", diag.x - diag.y, 0f, 0.001f);

            // L コースと R コースの入れ替え。ファイル名の '_L' / '_R' だけを見る。
            Expect("L を R に", SimBootstrap.SwapSideInFileName("etrobo_2026_L_nansyo.json"),
                   "etrobo_2026_R_nansyo.json");
            Expect("R を L に", SimBootstrap.SwapSideInFileName("etrobo_2026_R.json"),
                   "etrobo_2026_L.json");
            Expect("側の無いファイルは入れ替えない",
                   SimBootstrap.SwapSideInFileName("practice_oval.json"), null);
            // 'LAP' の L や 'RALLY' の R を書き換えてしまわないこと
            Expect("語の中の文字は見ない",
                   SimBootstrap.SwapSideInFileName("etrobo_LAP.json"), null);
        }

        /// <summary>
        /// 難所の成立判定（規約 5.15 / 5.16.2 / 5.17.3）。
        /// ここを間違えると、走れていないのに成立したことになる。
        /// </summary>
        static void CheckNansyoJudge()
        {
            Section("難所の成立判定");

            const float dohyoR = 183.75f, bottleR = 34f;
            var center = new Vector2(660f, 1701f);

            // ET相撲 (5.15)「土俵外に完全に押し出す」
            Expect("土俵の中は不成立",
                   NansyoJudge.IsOutsideDohyo(center, center, bottleR, dohyoR), false);
            Expect("縁にかかっていれば不成立",
                   NansyoJudge.IsOutsideDohyo(center + new Vector2(dohyoR, 0f), center,
                                              bottleR, dohyoR), false);
            Expect("底面が完全に出たら成立",
                   NansyoJudge.IsOutsideDohyo(center + new Vector2(dohyoR + bottleR + 1f, 0f),
                                              center, bottleR, dohyoR), true);

            // ボトルデリバリー (5.16.2)「底面が一部でも接して置かれている」
            var zone = new NansyoLayout.DropZone
            {
                Color = NansyoController.BottleColor.Blue,
                CenterMm = new Vector2(2480f, 1052f),
                SizeMm = new Vector2(200f, 200f),
            };
            Expect("ゾーンの真ん中は成立", zone.Touches(zone.CenterMm, 31f), true);
            Expect("一部でもかかれば成立",
                   zone.Touches(zone.CenterMm + new Vector2(120f, 0f), 31f), true);
            Expect("完全に外なら不成立",
                   zone.Touches(zone.CenterMm + new Vector2(140f, 0f), 31f), false);
            // 角の判定は辺ではなく角までの距離で決まる。
            // 半径 31 のボトルは、角から 20mm 斜めに出ていてもまだ触れている（28.3mm）。
            Expect("角の近くはまだ触れている",
                   zone.Touches(zone.CenterMm + new Vector2(120f, 120f), 31f), true);
            Expect("角から離れれば不成立",
                   zone.Touches(zone.CenterMm + new Vector2(125f, 125f), 31f), false);

            // ETラリー (5.17.3) 通過順 赤→青→黄
            var st = default(NansyoJudge.RallyState);
            st = NansyoJudge.AdvanceRally(st, NansyoController.BottleColor.Red);
            st = NansyoJudge.AdvanceRally(st, NansyoController.BottleColor.Blue);
            Expect("順番どおりなら進む", st.Step, 2);
            st = NansyoJudge.AdvanceRally(st, NansyoController.BottleColor.Yellow);
            Expect("赤青黄で 1 周回", st.Laps, 1);
            Expect("周回すると先頭に戻る", st.Step, 0);

            // 同色の連続通過は 1 回と見なす
            st = default;
            st = NansyoJudge.AdvanceRally(st, NansyoController.BottleColor.Red);
            st = NansyoJudge.AdvanceRally(st, NansyoController.BottleColor.Red);
            Expect("同色の連続は 1 回", st.Step, 1);

            // 順番外はやり直し。通ったのが赤ならそれが 1 本目になる
            st = default;
            st = NansyoJudge.AdvanceRally(st, NansyoController.BottleColor.Red);
            st = NansyoJudge.AdvanceRally(st, NansyoController.BottleColor.Yellow);
            Expect("順番外はやり直し", st.Step, 0);
            Expect("やり直しても周回は減らない", st.Laps, 0);

            st = default;
            st = NansyoJudge.AdvanceRally(st, NansyoController.BottleColor.Blue);
            Expect("いきなり青は数えない", st.Step, 0);
            st = NansyoJudge.AdvanceRally(st, NansyoController.BottleColor.Red);
            Expect("そのあと赤なら 1 本目", st.Step, 1);

            // 周回は累積する
            st = default;
            for (int lap = 0; lap < 2; lap++)
                foreach (var c in NansyoJudge.RallyOrder)
                    st = NansyoJudge.AdvanceRally(st, c);
            Expect("2 周回は累積", st.Laps, 2);

            Expect("1 周回は 5 ポイント", NansyoJudge.RallyPoints(1), 5);
            Expect("3 周回は 15 ポイント", NansyoJudge.RallyPoints(3), 15);
            Expect("4 周回でも 15 ポイント止まり", NansyoJudge.RallyPoints(4), 15);

            CheckGatePassing();
        }

        /// <summary>
        /// ゲートの通り抜け。走行体**全体**が脚の間を通ったときだけ成立する
        /// （規約 5.17.3）。脚の外を回ったのに数えてしまうと、実機では
        /// 取れないポイントが取れているように見える。
        /// </summary>
        static void CheckGatePassing()
        {
            var spec = LoadRobotSpec("robot_rehacks_2026.json") ?? new RobotSpec();

            // ゲートの脚はローカル X に並ぶ（CourseBuilder.BuildGate）。
            // 判定はその向きを gate.right から取るので、方位との対応を固定しておく。
            // 横のゲート = heading 90、縦のゲート = heading 0（NansyoController.SetGateCell）。
            var yokoAxis = CourseSpace.FlattenToCourse(
                CourseSpace.HeadingToRotation(90f) * Vector3.right);
            var tateAxis = CourseSpace.FlattenToCourse(
                CourseSpace.HeadingToRotation(0f) * Vector3.right);
            // 向きの符号は問わない（脚は左右対称に並ぶので、判定は軸の正負で変わらない）。
            Expect("横のゲートの脚は x 方向に並ぶ", Mathf.Abs(yokoAxis.x), 1f, 0.001f);
            Expect("横のゲートの脚は y 方向に広がらない", yokoAxis.y, 0f, 0.001f);
            Expect("縦のゲートの脚は y 方向に並ぶ", Mathf.Abs(tateAxis.y), 1f, 0.001f);
            Expect("縦のゲートの脚は x 方向に広がらない", tateAxis.x, 0f, 0.001f);

            var gate = new Vector2(1432f, 495f);
            var axis = yokoAxis;                   // 横のゲート。脚は x 方向に並ぶ
            float halfOpen = 232f * 0.5f;
            var corners = new Vector2[4];

            // 脚の間をまっすぐ通る（方位 90 = +y 向き）
            Expect("脚の間を通れば成立", WalkThrough(spec, corners, gate, axis, halfOpen, 0f), true);

            // 脚の外（ゲートの真横 400mm）を通る
            Expect("脚の外を通っても数えない",
                   WalkThrough(spec, corners, gate, axis, halfOpen, 400f), false);

            // 少しだけずれて、車体の端が脚に掛かる位置
            float edge = halfOpen - spec.BodyWidthMm * 0.5f + 10f;
            Expect("端が脚に掛かったら数えない",
                   WalkThrough(spec, corners, gate, axis, halfOpen, edge), false);
        }

        /// <summary>
        /// ガレージ（規約 5.2.4）とフィニッシュ（5.14）。
        /// 枠はコース図に印刷された灰色の コ の字で、座標を手で書いていない。
        /// 図面の線幅が 0.9mm なので、--line-min-width を上げると枠ごと消える。
        /// </summary>
        static void CheckGarage()
        {
            Section("ガレージ");

            var left = LoadCourse("etrobo_2026_L.json", required: false);
            if (left == null)
            {
                _report.AppendLine("  --   etrobo_2026_L.json が無いので飛ばしました");
                return;
            }

            var g = GarageArea.FromCourse(left, wantLeft: true);
            Expect("L コースのガレージが見つかる", g.Found, true);
            if (!g.Found) return;

            // 公式コース図の灰色の枠: x 195.7..445.7, y 40..360
            Expect("ガレージの幅", g.SizeMm.x, 250f, 1f);
            Expect("ガレージの奥行き", g.SizeMm.y, 320f, 1f);
            Expect("ガレージの中心 x", g.CenterMm.x, 320.7f, 1f);
            Expect("ガレージの中心 y", g.CenterMm.y, 200f, 1f);

            // 本線はガレージの真ん中に入ってくる（黒線 line_12_0 の端は x=321）
            Expect("本線の入口がガレージの中", g.Contains(new Vector2(321f, 300f)), true);
            Expect("枠の外は入っていない", g.Contains(new Vector2(321f, 400f)), false);

            // 「走行体の一部」でよい（規約 5.14）。鼻先だけ入っていても成立する。
            var spec = LoadRobotSpec("robot_rehacks_2026.json") ?? new RobotSpec();
            var corners = new Vector2[4];

            NansyoJudge.Footprint(spec, new Vector2(321f, 200f), -90f, corners);
            Expect("ガレージのまん中にいる", g.TouchedBy(corners), true);

            // 後ろ半分がはみ出す位置。前の角だけが枠に入る。
            NansyoJudge.Footprint(spec, new Vector2(321f, 430f), -90f, corners);
            Expect("鼻先だけでも成立", g.TouchedBy(corners), true);

            NansyoJudge.Footprint(spec, new Vector2(321f, 600f), -90f, corners);
            Expect("完全に外なら不成立", g.TouchedBy(corners), false);

            // R コースは左右反転
            var right = LoadCourse("etrobo_2026_R.json", required: false);
            if (right != null)
            {
                var gr = GarageArea.FromCourse(right, wantLeft: false);
                Expect("R コースのガレージが見つかる", gr.Found, true);
                if (gr.Found)
                {
                    Expect("R は鏡像の位置",
                           gr.CenterMm.x, left.SheetWidthMm - g.CenterMm.x, 1f);
                    Expect("R も同じ大きさ", gr.SizeMm.x, g.SizeMm.x, 0.5f);
                }
            }
        }

        /// <summary>ゲートの手前から奥まで 1mm 刻みで通し、成立したかを返す。</summary>
        static bool WalkThrough(RobotSpec spec, Vector2[] corners, Vector2 gate,
                                Vector2 axis, float halfOpen, float offsetMm)
        {
            var crossing = new NansyoJudge.GateCrossing { Side = 0, Valid = true };
            bool passed = false;

            for (float y = -400f; y <= 400f; y += 1f)
            {
                var pos = gate + axis * offsetMm + new Vector2(0f, y);
                NansyoJudge.Footprint(spec, pos, 90f, corners);
                if (crossing.Step(gate, axis, halfOpen, corners)) passed = true;
            }
            return passed;
        }

        static void CheckOfficialCourse()
        {
            Section("公式コース (2026 L)");

            var course = LoadCourse("etrobo_2026_L.json", required: false);
            if (course == null)
            {
                _report.AppendLine("  --   etrobo_2026_L.json が無いので飛ばしました");
                return;
            }

            // 縮尺 1/2 の図面を実寸へ戻したサイズ
            Expect("シート幅", course.SheetWidthMm, 5460f, 1f);
            Expect("シート高さ", course.SheetHeightMm, 3640f, 1f);
            Expect("マットが緑", course.SheetColor.g > course.SheetColor.r
                                 && course.SheetColor.g > course.SheetColor.b, true);

            var field = new CourseField(course);

            // 本線とその周り（スタート地点の断面で見る）
            ExpectColorName("ライン中心", field, new Vector2(2520f, 3427f), "dark");
            ExpectColorName("白帯の上", field, new Vector2(2620f, 3427f), "white");
            ExpectColorName("白帯の外", field, new Vector2(2760f, 3427f), "green");

            // ブロックエリアのカラーサークル。線 (z=172) の上に z=173.. で重なっている
            ExpectColorName("緑サークル", field, new Vector2(908f, 620f), "green");
            ExpectColorName("黄サークル", field, new Vector2(908f, 870f), "yellow");
            ExpectColorName("赤サークル", field, new Vector2(906f, 1120f), "red");
            ExpectColorName("青サークル", field, new Vector2(908f, 1370f), "blue");
            ExpectColorName("サークル間は線", field, new Vector2(908f, 745f), "dark");

            // 緑のマットは、反射光量ではラインの縁と見分けがつかない。
            //
            // マット #149E4E の輝度は 0.48 なので、反射光量は 6 + 88*0.48 = 48。
            // 縁を狙うライントレースの目標値 50 とほぼ同じで、センサがマットへ
            // 出てしまうと偏差がほぼ 0 になり、制御はまっすぐ走り続ける。
            // 反射光量だけを見るライントレーサは、一度帯の外へ出ると戻れない。
            // 復帰させたいなら色 (HSV) を見るか、目標値を下げて
            // マット側にも戻る向きの偏差が出るようにする。
            var spec = new RobotSpec();
            float matReflection = spec.ReflectionBlack
                + (spec.ReflectionWhite - spec.ReflectionBlack)
                  * Luminance(field.SampleColor(new Vector2(2760f, 3427f)));
            Expect("緑マットの反射光量", matReflection, 48.4f, 1.0f);
            _report.AppendLine("       ライン縁を狙う目標値 50 とほぼ同じ。"
                               + "帯の外へ出ると偏差が消えるので戻れない");

            // 計測区間の本線 (x=321 の直線) には、線の色でないものが乗っている。
            // 走行体が狙う左縁 x=311 の上で、目標値 50 に対して ±13 の外乱になる。
            // ライントレースがここで崩れて帯の外へ出るので、場所を固定しておく。
            float grayCircle = spec.ReflectionBlack
                + (spec.ReflectionWhite - spec.ReflectionBlack)
                  * Luminance(field.SampleColor(new Vector2(311f, 800f)));
            float blueMark = spec.ReflectionBlack
                + (spec.ReflectionWhite - spec.ReflectionBlack)
                  * Luminance(field.SampleColor(new Vector2(311f, 1030f)));
            Expect("本線上のゲートポジション (灰) の反射光量", grayCircle, 63.6f, 1.0f);
            Expect("本線上の区間標識 (青) の反射光量", blueMark, 38.0f, 1.0f);

            // スタート姿勢はラインの縁に乗っていること。
            // CourseField.SampleColor は 1 点サンプルなので、縁ちょうどでは
            // 線の色か白のどちらかしか返らない。中間の値は実センサと同じく
            // スポットを平均して初めて出るので、ここでも平均して確かめる。
            float lum = SpotLuminance(field, new Vector2(2530f, 3427f), 12f);
            Expect("スタート時のセンサはラインの縁 (スポット平均)",
                   lum > 0.2f && lum < 0.8f, true);

            // 縁からずらせば、ちゃんと黒側・白側へ振り切れること
            Expect("縁より内側は黒寄り", SpotLuminance(field, new Vector2(2515f, 3427f), 12f) < 0.2f, true);
            Expect("縁より外側は白寄り", SpotLuminance(field, new Vector2(2550f, 3427f), 12f) > 0.8f, true);

            int starts = 0, goals = 0, checkpoints = 0;
            foreach (var g in course.Gates)
            {
                if (g.Kind == GateKind.Start) starts++;
                else if (g.Kind == GateKind.Goal) goals++;
                else checkpoints++;
            }
            Expect("スタートゲート", starts, 1);
            Expect("ゴールゲート", goals, 1);
            Expect("チェックポイント", checkpoints, 3);

            // 文字とロゴの穴。塗り規則を間違えると、穴が埋まるか逆に穴が増える。
            //
            // 「製作 ETロボコン実行委員会」の「ロ」は、外側の輪郭 1 本と
            // 逆回りの矩形 1 本でできている。矩形の向きを落とすと穴が埋まり
            // 「ロ」が「■」になる。逆に、向きを見ずに偶奇規則で塗ると、
            // 同じ向きの輪郭が重なっている矢印に穴があく。
            ExpectColorName("「ロ」の穴はマット色", field, new Vector2(1825.3f, 2190.8f), "green");
            ExpectColorName("「ロ」の画は白", field, new Vector2(1809f, 2190f), "white");
            ExpectColorName("矢印は穴があかない", field, new Vector2(1385f, 3170f), "yellow");

            int multi = 0, reversed = 0;
            foreach (var a in course.Areas)
            {
                if (a.Shape != AreaShape.Polygon || a.Contours.Count < 2) continue;
                multi++;
                // 向きが逆の輪郭が 1 本でもあれば、そこが穴になる
                float first = SignedArea(a.Contours[0]);
                for (int i = 1; i < a.Contours.Count; i++)
                {
                    if (SignedArea(a.Contours[i]) * first < 0f) { reversed++; break; }
                }
            }
            _report.AppendLine(string.Format("  --   輪郭が 2 本以上の面 {0} 個", multi));
            Expect("向きが逆の輪郭 (穴) を持つ面がある", reversed > 0, true);
        }

        /// <summary>符号付き面積。向き (時計回り/反時計回り) を符号で返す。</summary>
        static float SignedArea(List<Vector2> poly)
        {
            float s = 0f;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                s += poly[j].x * poly[i].y - poly[i].x * poly[j].y;
            return s * 0.5f;
        }

        /// <summary>
        /// 焼いたテクスチャと、センサが見る幾何 (CourseField.SampleColor) が一致するか。
        ///
        /// 表示は CourseRasterizer、センサは CourseField と別経路なので、
        /// ここがずれると「画面で見えている色」と「センサが読む色」が食い違う。
        /// 境界の 1 画素は原理的に一致しないので、一致率で見る。
        /// </summary>
        static void CheckRasterizerMatchesGeometry()
        {
            Section("表示テクスチャと幾何の一致");

            var course = LoadCourse("etrobo_2026_L.json", required: false)
                         ?? LoadCourse("practice_oval.json");
            if (course == null) return;

            var field = new CourseField(course);

            const float pixelsPerMm = 0.5f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tex = field.BakeTexture(pixelsPerMm);
            sw.Stop();

            _report.AppendLine($"  --   {tex.width}x{tex.height} を {sw.ElapsedMilliseconds} ms で生成");

            var pixels = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            float mmPerPxX = course.SheetWidthMm / w;
            float mmPerPxY = course.SheetHeightMm / h;

            var rng = new System.Random(12345);
            int checkedCount = 0, agreed = 0;

            for (int i = 0; i < 4000; i++)
            {
                int x = rng.Next(1, w - 1);
                int y = rng.Next(1, h - 1);

                var mm = new Vector2((x + 0.5f) * mmPerPxX, (y + 0.5f) * mmPerPxY);
                Color32 expected = field.SampleColor(mm);
                Color32 actual = pixels[y * w + x];

                // 境界にかかる画素は原理的に一致しないので、
                // 隣接画素と色が違う（= 境界上）ものは判定から外す
                Color32 left = pixels[y * w + (x - 1)];
                Color32 right = pixels[y * w + (x + 1)];
                Color32 down = pixels[(y - 1) * w + x];
                Color32 up = pixels[(y + 1) * w + x];
                if (!Same(actual, left) || !Same(actual, right)
                    || !Same(actual, down) || !Same(actual, up)) continue;

                checkedCount++;
                if (Same(actual, expected)) agreed++;
            }

            if (checkedCount < 500)
            {
                Fail($"判定できた画素が少なすぎます ({checkedCount})");
                UnityEngine.Object.DestroyImmediate(tex);
                return;
            }

            float rate = agreed / (float)checkedCount;
            _report.AppendLine($"  --   境界以外 {checkedCount} 画素を照合");
            Expect("表示とセンサの色が一致", rate > 0.995f, true);
            if (rate <= 0.995f)
                _report.AppendLine($"       一致率 {rate * 100f:F2}%");

            // ランダム抽出だけだと、小さい要素が丸ごと描かれていなくても素通りする。
            // 面の中心を 1 つずつ見て、描き落としの種類を特定できるようにする。
            CheckEveryAreaCenter(course, field, pixels, w, h, mmPerPxX, mmPerPxY);

            // 太いパスの内側に地色が残っていないか。継ぎ目の塗り残しは
            // 1 画素幅の筋になるので、ランダム抽出ではまず当たらない。
            CheckWidePathsHaveNoHoles(course, field, pixels, w, h, mmPerPxX, mmPerPxY);

            UnityEngine.Object.DestroyImmediate(tex);
        }

        static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;

        /// <summary>
        /// 太いパス（白帯など）の内側を断面ごとに走査して、地色が残っていないか調べる。
        ///
        /// 継ぎ目の塗り残しは 1 画素幅の筋として出るので、画素をランダムに抜き取る
        /// 検査ではまず当たらない。帯を横断する向きに一定間隔で見ていく。
        /// </summary>
        static void CheckWidePathsHaveNoHoles(CourseDefinition course, CourseField field,
                                              Color32[] pixels, int w, int h,
                                              float mmPerPxX, float mmPerPxY)
        {
            const float wideThresholdMm = 100f;
            var wide = new List<int>();
            for (int i = 0; i < course.Paths.Count; i++)
                if (course.Paths[i].WidthMm >= wideThresholdMm) wide.Add(i);

            if (wide.Count == 0)
            {
                _report.AppendLine($"  --   幅 {wideThresholdMm}mm 以上のパスが無いので飛ばしました");
                return;
            }

            int sampled = 0, holes = 0;
            Vector2 firstHole = Vector2.zero;

            foreach (int index in wide)
            {
                var path = course.Paths[index];
                var pts = field.PathPolylines[index];
                float half = path.WidthMm * 0.5f * 0.9f;   // 縁ぎりぎりは外す

                for (int i = 1; i < pts.Count; i++)
                {
                    Vector2 a = pts[i - 1], b = pts[i];
                    Vector2 d = b - a;
                    float len = d.magnitude;
                    if (len < 1e-6f) continue;

                    Vector2 normal = new Vector2(-d.y, d.x) / len;
                    int along = Mathf.Max(1, Mathf.CeilToInt(len / 2f));

                    for (int s = 0; s < along; s++)
                    {
                        Vector2 center = a + d * ((s + 0.5f) / along);

                        for (float k = -half; k <= half; k += 4f)
                        {
                            Vector2 mm = center + normal * k;
                            int x = Mathf.FloorToInt(mm.x / mmPerPxX);
                            int y = Mathf.FloorToInt(mm.y / mmPerPxY);
                            if (x < 0 || x >= w || y < 0 || y >= h) continue;

                            sampled++;
                            if (!Same(pixels[y * w + x], course.SheetColor)) continue;

                            // 幾何側も地色だと言うなら、そこは本当に帯の外
                            if (Same(field.SampleColor(
                                    new Vector2((x + 0.5f) * mmPerPxX, (y + 0.5f) * mmPerPxY)),
                                    course.SheetColor)) continue;

                            if (holes == 0) firstHole = mm;
                            holes++;
                        }
                    }
                }
            }

            _report.AppendLine($"  --   太いパスの断面 {sampled} 点を走査");
            Expect("帯の内側に塗り残しが無い", holes, 0);
            if (holes > 0)
                _report.AppendLine($"       最初の塗り残し: ({firstHole.x:F0}, {firstHole.y:F0}) mm");
        }

        /// <summary>
        /// すべての面について、その中心が焼いたテクスチャでも同じ色になっているか。
        /// 描き落としがあると、その形・色にまとまって現れるので原因を絞りやすい。
        /// 上に別の要素が重なっている中心は、幾何側 (SampleColor) も同じ答えを返すので
        /// 判定から外れず正しく比較できる。
        /// </summary>
        static void CheckEveryAreaCenter(CourseDefinition course, CourseField field,
                                         Color32[] pixels, int w, int h,
                                         float mmPerPxX, float mmPerPxY)
        {
            var missedByKind = new Dictionary<string, int>();
            int total = 0, mismatched = 0;

            foreach (var area in course.Areas)
            {
                Vector2 center;
                switch (area.Shape)
                {
                    case AreaShape.Rect:
                    case AreaShape.Circle:
                        center = area.Center;
                        break;
                    default:
                        if (area.Points.Count < 3) continue;
                        center = Vector2.zero;
                        foreach (var p in area.Points) center += p;
                        center /= area.Points.Count;
                        break;
                }

                int x = Mathf.FloorToInt(center.x / mmPerPxX);
                int y = Mathf.FloorToInt(center.y / mmPerPxY);
                if (x < 0 || x >= w || y < 0 || y >= h) continue;

                total++;
                Color32 expected = field.SampleColor(
                    new Vector2((x + 0.5f) * mmPerPxX, (y + 0.5f) * mmPerPxY));
                Color32 actual = pixels[y * w + x];
                if (Same(expected, actual)) continue;

                mismatched++;
                string key = $"{area.Shape} #{area.Color.r:X2}{area.Color.g:X2}{area.Color.b:X2}";
                missedByKind[key] = missedByKind.TryGetValue(key, out int n) ? n + 1 : 1;
            }

            _report.AppendLine($"  --   面の中心 {total} 個を照合");
            Expect("すべての面が焼けている", mismatched, 0);

            foreach (var kv in missedByKind)
                _report.AppendLine($"       不一致: {kv.Key} x {kv.Value}");
        }

        /// <summary>サンプルした色が期待どおりの系統かを見る。</summary>
        static void ExpectColorName(string what, CourseField field, Vector2 mm, string expected)
        {
            Color32 c = field.SampleColor(mm);
            float lum = Luminance(c);

            bool ok;
            switch (expected)
            {
                case "dark": ok = lum < 0.15f; break;
                case "white": ok = lum > 0.85f; break;
                case "green": ok = c.g > c.r + 30 && c.g > c.b + 30; break;
                case "red": ok = c.r > c.g + 60 && c.r > c.b + 60; break;
                case "blue": ok = c.b > c.r + 60 && c.b > c.g + 30; break;
                case "yellow": ok = c.r > 150 && c.g > 150 && c.b < 100; break;
                default: ok = false; break;
            }

            if (ok) _report.AppendLine($"  ok   {what} = RGB({c.r},{c.g},{c.b})");
            else Fail($"{what} = RGB({c.r},{c.g},{c.b}) (期待 {expected})");
        }

        // ---- 補助 --------------------------------------------------------

        /// <summary>course/ か StreamingAssets から走行体パラメータを読む。</summary>
        static RobotSpec LoadRobotSpec(string fileName)
        {
            string path = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..", "course", fileName));
            if (!File.Exists(path)) path = Path.Combine(Application.streamingAssetsPath, fileName);
            if (!File.Exists(path)) return null;
            try { return RobotSpec.FromJson(File.ReadAllText(path)); }
            catch { return null; }
        }

        static CourseDefinition LoadCourse(string fileName, bool required = true)
        {
            string path = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..", "course", fileName));

            if (!File.Exists(path))
            {
                path = Path.Combine(Application.streamingAssetsPath, fileName);
            }
            if (!File.Exists(path))
            {
                if (required) Fail($"コースファイルが見つかりません: {fileName}");
                return null;
            }

            try
            {
                return CourseDefinition.FromJson(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Fail($"コース '{fileName}' の読み込みに失敗: {e.Message}");
                return null;
            }
        }

        static float Luminance(Color32 c)
            => (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;

        /// <summary>
        /// 直径 diameterMm の円内を平均した輝度。実機のカラーセンサは点ではなく
        /// 面を見ているので、ラインの縁の「中間値」はこの平均でしか出てこない。
        /// ColorSensorModel と同じ黄金角配置で 9 点取る。
        /// </summary>
        static float SpotLuminance(CourseField field, Vector2 centerMm, float diameterMm)
        {
            const int count = 9;
            const float golden = 2.39996323f;
            float radius = diameterMm * 0.5f;

            float sum = Luminance(field.SampleColor(centerMm));
            for (int i = 1; i < count; i++)
            {
                float r = Mathf.Sqrt(i / (float)(count - 1)) * radius;
                float a = i * golden;
                sum += Luminance(field.SampleColor(
                    centerMm + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r)));
            }
            return sum / count;
        }

        /// <summary>
        /// アームの爪とボトルの寸法。
        ///
        /// 実機ではキャリーボトルが半径ぶんほど爪に入り、そのまま運べる。
        /// 力士ボトルは太くて入らない。爪は剛体の箱なので、入る深さは
        /// 爪の幅だけで決まる。ここがずれると押し出しも運搬も変わる。
        /// </summary>
        static void CheckArmGrip()
        {
            Section("アームの爪");

            var spec = new RobotSpec();
            const float carry = 62f;     // キャリーボトル 300mL 丸
            const float sumo = 68f;      // 力士ボトル 500mL 丸
            float gap = spec.ArmGapMm;

            Expect("キャリーボトルは入る", ArmGrip.Fits(carry, gap), true);
            Expect("力士ボトルは入らない", ArmGrip.Fits(sumo, gap), false);

            // 入る深さ。実機は「半径ぶんちょうどぐらい」。
            float depth = ArmGrip.WedgeDepthMm(carry, gap);
            if (depth < carry * 0.5f * 0.7f || depth > carry * 0.5f)
                Fail($"キャリーボトルの入る深さ {depth:F1}mm が半径 {carry * 0.5f:F1}mm と合わない");
            else
                _report.AppendLine($"  ok   キャリーボトルの入る深さ = {depth:F1}mm");

            // 止まる位置（爪先からボトルの軸まで）。前が正。
            float carryStop = carry * 0.5f - depth;
            float sumoStop = sumo * 0.5f - ArmGrip.WedgeDepthMm(sumo, gap);

            Expect("キャリーボトルは抱える",
                   ArmGrip.InCatchWindow(0f, carryStop, gap, spec.ArmGripCatchMm), true);
            Expect("力士ボトルは抱えない",
                   ArmGrip.InCatchWindow(0f, sumoStop, gap, spec.ArmGripCatchMm), false);
            Expect("離れたら放す",
                   ArmGrip.ShouldRelease(0f, spec.ArmGripReleaseMm + 1f, gap, spec.ArmGripReleaseMm), true);

            // 保持の強さ。バックすれば置いていける程度に緩くする。
            const float bottleKg = 0.105f;
            const float floorFriction = 0.6f;
            float slip = bottleKg * 9.81f * floorFriction;      // 床を引きずるのに要る力
            float turn = bottleKg * 2f * 2f * 0.15f;            // 2rad/s で半径 150mm を回る
            if (spec.ArmGripForceN >= slip)
                Fail($"保持 {spec.ArmGripForceN:F2}N が強すぎる（床の摩擦 {slip:F2}N 以上だとバックしても付いてくる）");
            else if (spec.ArmGripForceN <= turn)
                Fail($"保持 {spec.ArmGripForceN:F2}N が弱すぎる（旋回に要る {turn:F2}N 以下）");
            else
                _report.AppendLine($"  ok   保持 {spec.ArmGripForceN:F2}N"
                                   + $" (旋回 {turn:F2}N < 保持 < 引きずり {slip:F2}N)");
        }

        static void Section(string name) => _report.AppendLine($"\n[{name}]");

        static void Expect<T>(string what, T actual, T expected)
        {
            if (Equals(actual, expected)) _report.AppendLine($"  ok   {what} = {actual}");
            else Fail($"{what} = {actual} (期待 {expected})");
        }

        static void Expect(string what, float actual, float expected, float tolerance)
        {
            if (Mathf.Abs(actual - expected) <= tolerance)
                _report.AppendLine($"  ok   {what} = {actual:F3}");
            else
                Fail($"{what} = {actual:F3} (期待 {expected:F3} ± {tolerance})");
        }

        static void Fail(string message)
        {
            _failures.Add(message);
            _report.AppendLine($"  NG   {message}");
        }

        static uint ReadU32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        static int ReadI32(byte[] b, int o) => unchecked((int)ReadU32(b, o));
        static float ReadF32(byte[] b, int o) => BitConverter.ToSingle(BitConverter.GetBytes(ReadU32(b, o)), 0);

        static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void WriteI32(byte[] b, int o, int v) => WriteU32(b, o, unchecked((uint)v));
    }
}
