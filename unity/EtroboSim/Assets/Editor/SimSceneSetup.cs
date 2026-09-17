using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Etrobo.Sim.EditorTools
{
    /// <summary>
    /// シーンの再生成と、コース定義の StreamingAssets へのコピー。
    ///
    /// SimMain.unity はリポジトリに入っているが、Unity のバージョン差などで
    /// 壊れたときにここから作り直せるようにしてある（中身は空の GameObject に
    /// SimBootstrap を 1 個付けるだけなので、手作業より確実）。
    /// </summary>
    public static class SimSceneSetup
    {
        const string ScenePath = "Assets/Scenes/SimMain.unity";

        [MenuItem("ETロボコン/シミュレータシーンを作り直す", false, 10)]
        public static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var go = new GameObject("SimBootstrap");
            go.AddComponent<SimBootstrap>();

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            var buildScene = new EditorBuildSettingsScene(ScenePath, true);
            EditorBuildSettings.scenes = new[] { buildScene };

            Debug.Log($"[Etsim] {ScenePath} を作り直しました");
        }

        [MenuItem("ETロボコン/コース定義を StreamingAssets へ同期", false, 11)]
        public static void SyncCourseFiles()
        {
            string source = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..", "course"));

            if (!Directory.Exists(source))
            {
                Debug.LogError($"[Etsim] コースディレクトリが見つかりません: {source}");
                return;
            }

            string dest = Application.streamingAssetsPath;
            Directory.CreateDirectory(dest);

            int copied = 0;
            foreach (string file in Directory.GetFiles(source, "*.json"))
            {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
                copied++;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[Etsim] {copied} 件のコース定義を StreamingAssets へコピーしました");
        }

        /// <summary>
        /// コースの床テクスチャを PNG に書き出す。
        /// 画面で見えているものと、コース定義・原図を突き合わせるため。
        ///
        ///   Unity.exe ... -executeMethod Etrobo.Sim.EditorTools.SimSceneSetup.ExportCourseTexture
        ///                 -etsimCourse etrobo_2026_L.json -etsimExport out.png -etsimPixelsPerMm 1
        /// </summary>
        public static void ExportCourseTexture()
        {
            string courseFile = CommandLineArg("-etsimCourse") ?? "etrobo_2026_L.json";
            string outPath = CommandLineArg("-etsimExport") ?? "course_texture.png";
            float pixelsPerMm = 1f;
            string ppm = CommandLineArg("-etsimPixelsPerMm");
            if (ppm != null) float.TryParse(ppm, out pixelsPerMm);

            string path = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..", "course", courseFile));
            if (!File.Exists(path))
                path = Path.Combine(Application.streamingAssetsPath, courseFile);

            if (!File.Exists(path))
            {
                Debug.LogError($"[Etsim] コースが見つかりません: {courseFile}");
                EditorApplication.Exit(1);
                return;
            }

            var course = CourseDefinition.FromJson(File.ReadAllText(path));
            var field = new CourseField(course);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tex = field.BakeTexture(pixelsPerMm);
            sw.Stop();

            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Debug.Log($"[Etsim] {outPath} へ書き出しました "
                      + $"({tex.width}x{tex.height}, {sw.ElapsedMilliseconds} ms)");

            Object.DestroyImmediate(tex);
            EditorApplication.Exit(0);
        }

        static string CommandLineArg(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        [MenuItem("ETロボコン/シミュレータを再生", false, 30)]
        public static void PlaySimulator()
        {
            if (SceneManager.GetActiveScene().path != ScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                EditorSceneManager.OpenScene(ScenePath);
            }
            EditorApplication.isPlaying = true;
        }

        /// <summary>
        /// コマンドラインから再生状態まで持っていく入口。
        ///   Unity.exe -projectPath &lt;path&gt; -executeMethod Etrobo.Sim.EditorTools.SimSceneSetup.OpenAndPlay
        /// -quit は付けないこと。付けると再生に入る前にエディタが終了する。
        /// </summary>
        public static void OpenAndPlay()
        {
            EditorSceneManager.OpenScene(ScenePath);
            Debug.Log("[Etsim] シーンを開きました。再生状態へ入ります");

            // -executeMethod の中で直接 isPlaying を立てるとインポート処理と
            // 競合することがあるので、1 フレーム遅らせてから入る。
            EditorApplication.delayCall += () =>
            {
                EditorApplication.isPlaying = true;
                Debug.Log("[Etsim] 再生を開始しました");
            };
        }
    }
}
