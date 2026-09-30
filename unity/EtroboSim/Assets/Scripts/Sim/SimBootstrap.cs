using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Etrobo.Sim
{
    /// <summary>
    /// シーンに 1 つだけ置くエントリポイント。
    /// コース JSON を読み、床・難所・走行体・カメラ・HUD をすべてここから生成する。
    ///
    /// シーンに事前配置するオブジェクトをこのコンポーネント 1 個だけにしてあるのは、
    /// .unity ファイルの差分が読めなくなるのを避けるため。構成はすべてコードと
    /// JSON に載っているので、レビューも履歴も追える。
    /// </summary>
    public sealed class SimBootstrap : MonoBehaviour
    {
        [Header("コース")]
        [Tooltip("StreamingAssets か リポジトリの course/ から探すファイル名。")]
        public string CourseFile = "practice_oval.json";

        [Tooltip("走行体パラメータ。省略すると既定値を使う。")]
        public string RobotSpecFile = "robot_rehacks_2026.json";

        [Tooltip("床テクスチャの解像度 [px/mm]。見た目だけの設定で、センサ精度には影響しない。")]
        public float SheetTexturePixelsPerMm = 1.0f;

        [Tooltip("床テクスチャの総画素数の上限。大きいコースはこれに合わせて解像度が落ちる。"
                 + " 上げるほど近寄ったときに綺麗になるが、起動が遅くメモリを食う。")]
        public float MaxSheetTexturePixels = 20_000_000f;

        [Header("接続")]
        public int Port = Net.Etsim.DefaultPort;
        public bool AllowRemoteController = false;

        [Header("物理")]
        public float FixedStepSeconds = 0.002f;

        [Header("自動スタート")]
        [Tooltip("制御プロセスの接続からこの秒数後に中央ボタンを自動で押す。0 で無効。")]
        public float AutoStartSeconds = 0f;

        public SimManager Manager { get; private set; }
        public CameraRig Camera { get; private set; }
        public NansyoController Nansyo { get; private set; }
        public NansyoJudge Judge { get; private set; }

        /// <summary>いま読んでいるコースのファイル名。</summary>
        public string ActiveCourseFile { get; private set; }

        /// <summary>反対側（L ⇔ R）のコースのファイル名。無ければ null。</summary>
        public string OtherSideCourseFile { get; private set; }

        void Awake()
        {
            Application.runInBackground = true; // フォーカスを外しても走り続けさせる

            // 版を最初に出す。ログや録画を後から突き合わせるのに要る。
            Debug.Log(SimVersion.Banner);

            var course = LoadCourse();
            var spec = LoadRobotSpec();

            var builder = new CourseBuilder(course);
            builder.Build(SheetTexturePixelsPerMm, MaxSheetTexturePixels);

            var robot = CreateRobot(spec);

            CreateLighting();

            Manager = gameObject.AddComponent<SimManager>();
            Manager.Port = Port;
            Manager.AllowRemoteController = AllowRemoteController;
            Manager.FixedStepSeconds = FixedStepSeconds;
            Manager.AutoStartSeconds = ResolveAutoStartSeconds();
            // -etsimTruthLog <秒> : 真の位置と向きを定期的にログへ出す（計測用）
            if (float.TryParse(CommandLineArg("-etsimTruthLog"), out float truthLog))
                Manager.TruthLogSeconds = truthLog;
            // -etsimPressForce 3,10 : 接続から 3 秒後と 10 秒後にフォースセンサを押す
            string presses = CommandLineArg("-etsimPressForce");
            if (!string.IsNullOrEmpty(presses))
            {
                var parts = presses.Split(',');
                var times = new System.Collections.Generic.List<float>();
                foreach (var t in parts)
                    if (float.TryParse(t, out float sec)) times.Add(sec);
                Manager.PressForceAt = times.ToArray();
            }

            Manager.Initialize(course, builder.Field, robot);

            ApplyStartCommandLine();

            Camera = CreateCamera(course, robot.transform);

            // 難所の配置は大会当日まで分からないので、実行中に変えられるようにする
            Nansyo = gameObject.AddComponent<NansyoController>();
            Nansyo.Initialize(course, builder.Props);
            ApplyNansyoCommandLine();
            ApplyStashedNansyo();
            Manager.OnReset += Nansyo.RestoreBottles;

            Judge = gameObject.AddComponent<NansyoJudge>();
            Judge.Initialize(Nansyo, robot);
            Manager.Judge = Judge;
            Manager.OnReset += Judge.ResetJudge;

            var hud = gameObject.AddComponent<SimHud>();
            hud.Manager = Manager;
            hud.Rig = Camera;
            hud.Nansyo = Nansyo;
            hud.Boot = this;
            hud.Judge = Judge;

            SetUpScreenshotIfRequested();

            SetUpRecordIfRequested();

            SetUpSonarSweepIfRequested();

            SetUpOverlayIfRequested();

            // props は JSON の件数と**組み立てた件数**の両方を出す。
            // 片方だけだと、置いたはずの難所が出ていないときに切り分けられない。
            Debug.Log($"[Etsim] コース '{course.Name}' を構築しました "
                      + $"({course.SheetWidthMm}x{course.SheetHeightMm}mm, "
                      + $"paths={course.Paths.Count}, props={builder.Props.Count}/{course.Props.Count})");

            if (builder.Props.Count != course.Props.Count)
                Debug.LogWarning($"[Etsim] 組み立てられなかった prop があります "
                                 + $"({course.Props.Count - builder.Props.Count} 個)");
        }

        /// <summary>
        /// 開始姿勢をコマンドラインから差し替える。
        ///   -etsimStart 1200,800        （方位はコースの既定のまま）
        ///   -etsimStart 1200,800,90     （方位も指定。0 が +y 向き）
        /// 難所の手前だけを繰り返し試すときに使う。
        /// </summary>
        void ApplyStartCommandLine()
        {
            string arg = CommandLineArg("-etsimStart");
            if (string.IsNullOrEmpty(arg)) return;

            var f = arg.Split(',');
            if (f.Length < 2
                || !float.TryParse(f[0], out float x) || !float.TryParse(f[1], out float y))
            {
                Debug.LogWarning($"[Etsim] -etsimStart {arg} は読めませんでした (例 1200,800,90)");
                return;
            }

            float heading = Manager.StartHeadingDeg;
            if (f.Length >= 3 && !float.TryParse(f[2], out heading)) heading = Manager.StartHeadingDeg;

            Manager.SetStart(new Vector2(x, y), heading);
        }

        /// <summary>
        /// 難所の配置をコマンドラインから指定する。
        ///   -etsimGates red=G2-5,blue=G5-3,yellow=G1-2
        ///   -etsimCarryColor red
        ///   -etsimRikishi 60,-40        （土俵の中心からのずれ [mm]）
        /// 大会当日に配置パターンが出たあと、決まった配置で繰り返し走らせるため。
        /// </summary>
        void ApplyNansyoCommandLine()
        {
            string carry = CommandLineArg("-etsimCarryColor");
            if (!string.IsNullOrEmpty(carry) && TryParseColor(carry, out var cc))
                Nansyo.SetCarryColor(cc);

            string rikishi = CommandLineArg("-etsimRikishi");
            if (!string.IsNullOrEmpty(rikishi))
            {
                var xy = rikishi.Split(',');
                if (xy.Length == 2
                    && float.TryParse(xy[0], out float dx) && float.TryParse(xy[1], out float dy)
                    && Nansyo.SetRikishiOffset(new Vector2(dx, dy)))
                {
                    var o = Nansyo.RikishiOffsetMm;
                    Debug.Log($"[Etsim] 力士ボトルを土俵の中心から ({o.x:F0}, {o.y:F0})mm へ置きました");
                }
                else
                {
                    Debug.LogWarning($"[Etsim] -etsimRikishi {rikishi} は置けませんでした");
                }
            }

            string gates = CommandLineArg("-etsimGates");
            if (string.IsNullOrEmpty(gates)) return;

            foreach (var item in gates.Split(','))
            {
                var kv = item.Split('=');
                if (kv.Length != 2) continue;
                if (!TryParseColor(kv[0], out var color)) continue;
                if (!TryParseCell(kv[1], out int x, out int y)) continue;

                if (Nansyo.SetGateCell(color, x, y))
                    Debug.Log($"[Etsim] {NansyoController.NameOf(color)}ゲートを G{x}-{y} へ置きました");
                else
                    Debug.LogWarning($"[Etsim] {kv[0]}={kv[1]} は置けませんでした");
            }
        }

        static bool TryParseColor(string text, out NansyoController.BottleColor color)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "red": case "赤": color = NansyoController.BottleColor.Red; return true;
                case "blue": case "青": color = NansyoController.BottleColor.Blue; return true;
                case "yellow": case "黄": color = NansyoController.BottleColor.Yellow; return true;
            }
            color = NansyoController.BottleColor.Red;
            return false;
        }

        /// <summary>"G2-5" を (2, 5) に。</summary>
        static bool TryParseCell(string text, out int x, out int y)
        {
            x = y = 0;
            var t = text.Trim().TrimStart('G', 'g');
            var parts = t.Split('-');
            return parts.Length == 2
                   && int.TryParse(parts[0], out x)
                   && int.TryParse(parts[1], out y);
        }

        /// <summary>
        /// コマンドラインで指定されたら、起動後に画面を撮って終了する。
        ///   Unity.exe ... -etsimScreenshot out.png [-etsimScreenshotDelay 2]
        /// 見え方の確認を目視の報告だけに頼らずに済ませるため。
        /// </summary>
        /// <summary>
        /// ソナーの掃引。
        ///   -etsimSonarSweep 1            1° 刻みで一瞬で測る（数字だけ）
        ///   -etsimSonarSweep 2,20         20 秒かけて回す。画面で見える
        ///   -etsimSonarSweep 2,20,<出力先> さらにコマを連番 PNG で保存する
        /// </summary>
        /// <summary>
        /// コースの上に道すじを描く（見るだけ）。
        ///   -etsimOverlay path.txt   書き方は PathOverlay を参照
        /// </summary>
        void SetUpOverlayIfRequested()
        {
            string path = CommandLineArg("-etsimOverlay");
            if (string.IsNullOrEmpty(path)) return;
            PathOverlay.Load(path, transform);
        }

        void SetUpSonarSweepIfRequested()
        {
            string arg = CommandLineArg("-etsimSonarSweep");
            if (string.IsNullOrEmpty(arg)) return;

            var f = arg.Split(',');
            if (!float.TryParse(f[0], out float step) || step <= 0f)
            {
                Debug.LogWarning($"[Etsim] -etsimSonarSweep {arg} は読めませんでした (例 2,20)");
                return;
            }

            float seconds = 0f;
            if (f.Length > 1) float.TryParse(f[1], out seconds);
            string frameDir = f.Length > 2 ? f[2] : null;

            if (seconds <= 0f)
            {
                Manager.RunSonarSweep(step);   // 一瞬で測るほう
                return;
            }

            var view = gameObject.AddComponent<SonarSweepView>();
            view.Manager = Manager;
            StartCoroutine(AnimatedSweep(view, step, seconds, frameDir));
        }

        System.Collections.IEnumerator AnimatedSweep(SonarSweepView view, float step,
                                                     float seconds, string frameDir)
        {
            // カメラが落ち着くのを待ってから走行体に寄る。CameraRig の Start が
            // コース全体に合わせ直すので、先に寄せても戻される。
            yield return new WaitForSecondsRealtime(3f);
            if (!string.IsNullOrEmpty(frameDir)) Directory.CreateDirectory(frameDir);

            // 1 コマ目から寄った絵にしたいので、先に寄せて描画を 1 回通す。
            // FrameAt はカメラの目標を変えるだけで、実際に動くのは LateUpdate。
            if (Camera != null)
            {
                Camera.Smoothing = 0f;
                Camera.FrameAt(Manager.StartPositionMm, 0.9f, 42f);
                yield return null;
                yield return null;
            }

            yield return view.Run(step, seconds, i =>
            {
                if (Camera == null) return;

                // CameraRig の初期化がコース全体に合わせ直すので、
                // 1 回寄せるだけだと戻される。撮る直前に毎回寄せ直す。
                Camera.Smoothing = 0f;
                Camera.FrameAt(Manager.StartPositionMm, 0.9f, 42f);

                if (string.IsNullOrEmpty(frameDir)) return;
                CaptureFrame(Path.Combine(frameDir, $"sweep_{i:D4}.png"));
            });

            Debug.Log("[Etsim] ソナー掃引 終了");
        }

        void SetUpScreenshotIfRequested()
        {
            string path = CommandLineArg("-etsimScreenshot");
            if (string.IsNullOrEmpty(path)) return;

            float delay = 2f;
            string d = CommandLineArg("-etsimScreenshotDelay");
            if (d != null) float.TryParse(d, System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture, out delay);

            StartCoroutine(CaptureAfter(path, delay));
        }

        System.Collections.IEnumerator CaptureAfter(string path, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);

            if (ApplyScreenshotFrame())
            {
                yield return null;
                yield return null;
            }

            CaptureFrame(path);
        }

        /// <summary>
        /// -etsimScreenshotFrame の指定どおりにカメラを寄せる。寄せたら true。
        ///
        /// CameraRig は LateUpdate で自分の目標へ戻すので、撮る直前に毎回呼ぶ。
        /// </summary>
        bool ApplyScreenshotFrame()
        {
            // -etsimScreenshotFrame robot で走行体に寄せてから撮る。
            // 見た目の確認は全体像では判断できない。
            // CameraRig の Start がコース全体に合わせ直すので、待ってから寄せる。
            string frame = CommandLineArg("-etsimScreenshotFrame");
            if (string.IsNullOrEmpty(frame) || Camera == null) return false;

            Camera.Smoothing = 0f;
            if (frame == "robot")
            {
                Camera.FrameRobot();
                return true;
            }

            // "x,y[,距離m[,見下ろし角]]" で任意の場所に寄る
            var parts = frame.Split(',');
            if (parts.Length >= 2
                && float.TryParse(parts[0], out float fx)
                && float.TryParse(parts[1], out float fy))
            {
                float dist = (parts.Length > 2 && float.TryParse(parts[2], out float d)) ? d : 1.2f;
                float pitch = (parts.Length > 3 && float.TryParse(parts[3], out float pa)) ? pa : 25f;
                Camera.FrameAt(new Vector2(fx, fy), dist, pitch);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 走行の様子を連番 PNG で残す。
        ///   -etsimRecord <出力先>[,fps[,秒[,connect[,幅px]]]]
        /// 4 つ目に connect を付けると、制御プロセスが接続してから録り始める。
        /// Unity の起動にかかる時間は毎回違うので、動き出しを確実に捉えたいときは
        /// 秒数で待つより接続を待つほうが確か。
        ///
        /// 5 つ目は 1 コマの幅[px]。既定の 1600 だと 1 コマ 0.3 秒ほどかかり、
        /// 数コマしか撮れないうちに動きが終わってしまう。動画にするときは
        /// 720 くらいまで落として、コマ数を稼ぐ。
        ///
        /// 動画にするのは外（ffmpeg）でやる。ここは実際に撮れた時刻を
        /// frames.txt に残すので、再生速度はそれを見て決められる。
        /// </summary>
        void SetUpRecordIfRequested()
        {
            string arg = CommandLineArg("-etsimRecord");
            if (string.IsNullOrEmpty(arg)) return;

            var f = arg.Split(',');
            string dir = f[0];
            float fps = (f.Length > 1 && float.TryParse(f[1], out float v) && v > 0f) ? v : 20f;
            float seconds = (f.Length > 2 && float.TryParse(f[2], out float s) && s > 0f) ? s : 15f;
            bool onConnect = f.Length > 3 && f[3] == "connect";
            int width = (f.Length > 4 && int.TryParse(f[4], out int w) && w >= 160) ? w : 1600;

            StartCoroutine(Record(dir, fps, seconds, onConnect, width));
        }

        System.Collections.IEnumerator Record(string dir, float fps, float seconds, bool onConnect, int width)
        {
            if (onConnect)
                while (Manager == null || Manager.Server == null || !Manager.Server.ControllerConnected)
                    yield return null;

            Directory.CreateDirectory(dir);
            if (ApplyScreenshotFrame()) { yield return null; yield return null; }

            int total = Mathf.Max(1, Mathf.RoundToInt(seconds * fps));
            float interval = 1f / fps;
            float t0 = Time.realtimeSinceStartup;
            var times = new System.Text.StringBuilder();

            Debug.Log($"[Etsim] 録画開始 {dir} 目標 {fps:F0}fps {seconds:F0}秒 (最大 {total} コマ)");

            // 動きは実時間で 2〜3 秒しか続かないので、1 コマの費用がそのまま
            // コマ数に効く。RenderTexture と Texture2D は録画の間だけ使い回し、
            // 書き出しは PNG ではなく JPG にする。
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : UnityEngine.Camera.main;
            if (cam == null)
            {
                Debug.LogError("[Etsim] カメラが見つからないので録画できません");
                yield break;
            }

            width = Mathf.Max(160, width) & ~1;
            int height = Mathf.RoundToInt(width / Mathf.Max(0.1f, cam.aspect)) & ~1;
            var rt = new RenderTexture(width, height, 24);
            var shot = new Texture2D(width, height, TextureFormat.RGB24, false);
            var area = new Rect(0, 0, width, height);

            // WaitForEndOfFrame で Unity の描画に相乗りするほうが安く見えるが、
            // その Unity 自身がこのコースでは 7fps 程度でしか回っていないので、
            // こちらの都合で cam.Render() を呼ぶほうがコマ数を稼げる。
            // 720px・JPG・テクスチャの使い回しと合わせて実測 30fps。
            int shotCount = 0;
            for (int i = 0; i < total; i++)
            {
                ApplyScreenshotFrame();

                var prevTarget = cam.targetTexture;
                var prevActive = RenderTexture.active;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                shot.ReadPixels(area, 0, 0);
                shot.Apply();
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;

                File.WriteAllBytes(Path.Combine(dir, $"f_{i:D5}.jpg"), shot.EncodeToJPG(85));
                shotCount = i + 1;
                times.Append(i).Append(',')
                     .Append((Time.realtimeSinceStartup - t0).ToString("F3")).Append('\n');

                // 1 コマの保存が間隔より遅いときは待たない。撮れるだけ撮る。
                if (Time.realtimeSinceStartup - t0 < (i + 1) * interval)
                    yield return new WaitForSecondsRealtime(interval);

                if (Time.realtimeSinceStartup - t0 > seconds) break;
            }

            Destroy(shot);
            rt.Release();
            Destroy(rt);
            total = shotCount;

            File.WriteAllText(Path.Combine(dir, "frames.txt"), times.ToString());
            float span = Time.realtimeSinceStartup - t0;
            Debug.Log($"[Etsim] 録画終了 {total} コマ / {span:F1} 秒 = 実測 {total / Mathf.Max(0.001f, span):F1}fps");
        }

        /// <summary>
        /// カメラの絵を PNG で保存する。掃引のコマ撮りからも使う。
        /// quiet を立てるとログを出さない（録画で 1 コマごとに出ると読めなくなる）。
        /// width は 1 コマの幅[px]。小さくすると 1 コマの保存が速くなる。
        /// </summary>
        public void CaptureFrame(string path, bool quiet = false, int width = 1600)
        {
            // ScreenCapture は別モジュール扱いで、依存を増やしたくない。
            // カメラを RenderTexture へ描けばコアの機能だけで済み、
            // ウィンドウの大きさやフォーカスにも左右されない。
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : UnityEngine.Camera.main;
            if (cam == null)
            {
                Debug.LogError("[Etsim] カメラが見つからないので画面を保存できません");
                return;
            }

            // 動画の幅は偶数でないと h.264 に渡せない
            width = Mathf.Max(160, width) & ~1;
            int height = Mathf.RoundToInt(width / Mathf.Max(0.1f, cam.aspect)) & ~1;

            var rt = new RenderTexture(width, height, 24);
            var previous = cam.targetTexture;
            var previousActive = RenderTexture.active;

            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var shot = new Texture2D(width, height, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            shot.Apply();

            cam.targetTexture = previous;
            RenderTexture.active = previousActive;

            System.IO.File.WriteAllBytes(path, shot.EncodeToPNG());

            Destroy(shot);
            rt.Release();
            Destroy(rt);

            if (!quiet) Debug.Log($"[Etsim] 画面を {path} へ保存しました ({width}x{height})");
        }

        static string CommandLineArg(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        /// <summary>
        /// 自動スタートの秒数。コマンドラインからも渡せるようにしておくと、
        /// エディタを触らずに回帰テストを回せる。
        ///   Unity.exe ... -etsimAutoStart 2
        /// </summary>
        float ResolveAutoStartSeconds()
        {
            string v = CommandLineArg("-etsimAutoStart");
            if (v != null && float.TryParse(v, System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture,
                                            out float parsed))
                return parsed;
            return AutoStartSeconds;
        }

        // -- L/R の切り替え ---------------------------------------------------

        /// <summary>シーンを読み直すときに引き継ぐコース。</summary>
        static string _nextCourseFile;

        /// <summary>同じく引き継ぐ難所の配置。せっかく合わせた配置を消さないため。</summary>
        static PendingNansyo _nextNansyo;

        struct PendingNansyo
        {
            public bool Valid;
            public NansyoController.BottleColor Carry;
            public Vector2Int Red, Blue, Yellow;
            public Vector2 RikishiOffset;
        }

        /// <summary>
        /// L コースと R コースを入れ替える。
        ///
        /// コースは Awake で 1 度だけ組み立てるので、作り直すにはシーンを読み直す。
        /// 途中まで走っていた分は破棄される（実機でもコースを移れば置き直し）。
        /// </summary>
        public bool SwitchCourseSide()
        {
            if (string.IsNullOrEmpty(OtherSideCourseFile))
            {
                Debug.LogWarning($"[Etsim] '{ActiveCourseFile}' の反対側のコースが見つかりません");
                return false;
            }

            // 組み直した直後の連打は捨てる。押すとパネルが反対側へ動くので、
            // 同じ場所をもう一度押すと元に戻ってしまい、何が起きたのか分からなくなる。
            if (Time.timeSinceLevelLoad < SwitchCooldownSeconds) return false;

            // 読み直しは OnGUI の途中では始めない。押した瞬間に消えると
            // レイアウトの入れ子が閉じられずに例外になる。
            _switchRequested = true;
            return true;
        }

        bool _switchRequested;

        /// <summary>組み直した直後に切り替えを受け付けない時間 [s]。</summary>
        const float SwitchCooldownSeconds = 1.0f;

        void Update()
        {
            if (!_switchRequested) return;
            _switchRequested = false;

            StashNansyo();
            _nextCourseFile = OtherSideCourseFile;
            Debug.Log($"[Etsim] コースを '{_nextCourseFile}' に切り替えます");
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        void StashNansyo()
        {
            if (Nansyo == null || Nansyo.Layout == null) return;

            _nextNansyo = new PendingNansyo
            {
                Valid = true,
                Carry = Nansyo.CarryColor,
                Red = Nansyo.GetGateCell(NansyoController.BottleColor.Red),
                Blue = Nansyo.GetGateCell(NansyoController.BottleColor.Blue),
                Yellow = Nansyo.GetGateCell(NansyoController.BottleColor.Yellow),
                // 反対側のコースは鏡像なので、土俵の中の相対位置を保つには X を反転する
                RikishiOffset = new Vector2(-Nansyo.RikishiOffsetMm.x, Nansyo.RikishiOffsetMm.y),
            };
        }

        void ApplyStashedNansyo()
        {
            if (!_nextNansyo.Valid) return;
            _nextNansyo.Valid = false;

            Nansyo.SetCarryColor(_nextNansyo.Carry);
            Nansyo.SetGateCell(NansyoController.BottleColor.Red, _nextNansyo.Red.x, _nextNansyo.Red.y);
            Nansyo.SetGateCell(NansyoController.BottleColor.Blue, _nextNansyo.Blue.x, _nextNansyo.Blue.y);
            Nansyo.SetGateCell(NansyoController.BottleColor.Yellow, _nextNansyo.Yellow.x, _nextNansyo.Yellow.y);
            Nansyo.SetRikishiOffset(_nextNansyo.RikishiOffset);
            Debug.Log("[Etsim] 切り替え前の難所の配置を引き継ぎました");
        }

        /// <summary>
        /// 反対側のコースのファイル名。'_L' と '_R' を入れ替えて、実在するものだけ返す。
        /// </summary>
        string FindOtherSide(string file)
        {
            string other = SwapSideInFileName(file);
            return other != null && ResolveDataFile(other) != null ? other : null;
        }

        /// <summary>
        /// ファイル名の中の側を入れ替える。無ければ null。
        /// 'etrobo_2026_L_nansyo.json' ⇔ 'etrobo_2026_R_nansyo.json'。
        /// </summary>
        public static string SwapSideInFileName(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;

            // 区切りに挟まれた 1 文字の L / R だけを見る。
            // 'RELAY' のような語の中の文字を書き換えないため。
            for (int i = 1; i < file.Length - 1; i++)
            {
                char c = file[i];
                if (c != 'L' && c != 'R') continue;
                if (file[i - 1] != '_') continue;
                if (file[i + 1] != '_' && file[i + 1] != '.') continue;

                char swapped = c == 'L' ? 'R' : 'L';
                return file.Substring(0, i) + swapped + file.Substring(i + 1);
            }
            return null;
        }

        CourseDefinition LoadCourse()
        {
            // -etsimCourse で差し替えられるようにしておく。
            // 難所を置いたコースなど、シーンを編集せずに切り替えたいことがある。
            // パネルから L/R を切り替えたときは _nextCourseFile が入っている。
            string file = _nextCourseFile ?? CommandLineArg("-etsimCourse") ?? CourseFile;
            _nextCourseFile = null;
            ActiveCourseFile = file;
            OtherSideCourseFile = FindOtherSide(file);
            string path = ResolveDataFile(file);
            if (path == null)
            {
                Debug.LogError($"[Etsim] コースファイル '{file}' が見つかりません。"
                               + " StreamingAssets か course/ に置いてください。");
                return new CourseDefinition();
            }

            try
            {
                return CourseDefinition.FromJson(File.ReadAllText(path));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Etsim] コース '{path}' の読み込みに失敗しました: {e.Message}");
                return new CourseDefinition();
            }
        }

        RobotSpec LoadRobotSpec()
        {
            // -etsimRobot で差し替えられるようにしておく（-etsimCourse と同じ扱い）。
            // パラメータを振って比べるとき、シーンも共有ファイルも触らずに済む。
            string file = CommandLineArg("-etsimRobot") ?? RobotSpecFile;
            string path = ResolveDataFile(file);
            if (path == null) return new RobotSpec(); // 既定値で動く

            try
            {
                return RobotSpec.FromJson(File.ReadAllText(path));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Etsim] 走行体パラメータ '{path}' を読めませんでした"
                                 + $" ({e.Message})。既定値を使います。");
                return new RobotSpec();
            }
        }

        /// <summary>
        /// StreamingAssets を優先し、無ければリポジトリの course/ を探す。
        /// エディタで JSON を編集しながら実行できるようにするため。
        /// </summary>
        /// <summary>
        /// いまの開始姿勢をコース JSON の start_pose へ書き戻す。戻り値は書いた件数。
        ///
        /// StreamingAssets と リポジトリの course/ に同じ名前のコースがある。
        /// 読むのは StreamingAssets 側だが、そちらだけ直すと次に course/ から
        /// コピーした時点で消える。**あるものは両方**書き換える。
        /// </summary>
        public int SaveStartPoseToCourse()
        {
            if (Manager == null || string.IsNullOrEmpty(ActiveCourseFile)) return 0;

            int written = 0;
            foreach (string path in CourseFileCandidates(ActiveCourseFile))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    File.WriteAllText(path, CourseFileWriter.ReplaceStartPose(
                        json, Manager.StartPositionMm, Manager.StartHeadingDeg));
                    written++;
                    Debug.Log($"[Etsim] 開始位置を書き込みました: {path}");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Etsim] '{path}' に書き込めませんでした: {e.Message}");
                }
            }

            if (written == 0)
                Debug.LogWarning($"[Etsim] '{ActiveCourseFile}' の置き場所が見つかりません");

            return written;
        }

        static System.Collections.Generic.List<string> CourseFileCandidates(string fileName)
        {
            var list = new System.Collections.Generic.List<string>();

            string streaming = Path.Combine(Application.streamingAssetsPath, fileName);
            if (File.Exists(streaming)) list.Add(streaming);

            string repo = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..", "course", fileName));
            if (File.Exists(repo) && repo != streaming) list.Add(repo);

            return list;
        }

        static string ResolveDataFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            string streaming = Path.Combine(Application.streamingAssetsPath, fileName);
            if (File.Exists(streaming)) return streaming;

            // <repo>/unity/EtroboSim/Assets/.. から <repo>/course/ へ
            string repoCourse = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..", "course", fileName));
            if (File.Exists(repoCourse)) return repoCourse;

            return null;
        }

        RobotController CreateRobot(RobotSpec spec)
        {
            var go = new GameObject("SPIKE_Robot");
            go.AddComponent<Rigidbody>();
            var robot = go.AddComponent<RobotController>();

            // 組立図から作った見た目メッシュ。無ければ箱で描かれる。
            string meshPath = ResolveDataFile(spec.VisualMeshFile);
            if (!string.IsNullOrEmpty(spec.VisualMeshFile) && meshPath == null)
                Debug.LogWarning($"[Etsim] 走行体メッシュ '{spec.VisualMeshFile}' が見つかりません。箱で描きます。");

            robot.Build(spec, meshPath);
            return robot;
        }

        void CreateLighting()
        {
            var lightGo = new GameObject("SunLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.98f, 0.94f);
            light.intensity = 1.05f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(52f, -35f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.66f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.48f, 0.50f, 0.54f);
            RenderSettings.ambientGroundColor = new Color(0.28f, 0.28f, 0.30f);
        }

        CameraRig CreateCamera(CourseDefinition course, Transform target)
        {
            var go = new GameObject("MainCamera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<UnityEngine.Camera>();
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 60f;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.20f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            go.AddComponent<AudioListener>();

            var rig = go.AddComponent<CameraRig>();
            rig.Target = target;
            rig.CourseCenter = CourseSpace.ToWorld(
                new Vector2(course.SheetWidthMm * 0.5f, course.SheetHeightMm * 0.5f));
            rig.CourseSizeM = new Vector2(course.SheetWidthMm, course.SheetHeightMm) * CourseSpace.MmToM;
            return rig;
        }
    }
}
