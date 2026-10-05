using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Etrobo.Sim.EditorTools
{
    /// <summary>
    /// 再生を始めたとき（と、L/R の切り替えでシーンを読み直したとき）に、
    /// エディタの Game ビューの拡大率（ツールバーの「スケール」）を 1 倍へ戻す。
    ///
    /// 1 倍を超えていると、Game ビューは画面の中央を拡大して見せるので、
    /// 上下左右の端が見えなくなる。HUD のパネルは画面の端から 12px の所に
    /// 置いてあるので、1.06 倍でも左右 40px ずつ切れて、R コース（パネルが左）では
    /// 見出しやたたむボタンが押せなくなっていた。L コース（パネルが右）でも
    /// 右端が同じだけ切れているが、余白とスクロールバーなので気づきにくい。
    ///
    /// 拡大率は Game ビューの内部の値なので、公開されていない SnapZoom を呼ぶ。
    /// Unity のバージョンで見つからないときは何もしない（ログだけ出す）。
    /// </summary>
    [InitializeOnLoad]
    static class GameViewScaleGuard
    {
        static GameViewScaleGuard()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode) ResetScale();
            };
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (EditorApplication.isPlaying) ResetScale();
            };
        }

        static void ResetScale()
        {
            // 再生開始の直後は Game ビューの大きさがまだ決まっていないことがあるので、
            // 1 フレーム遅らせる。
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var type = Type.GetType("UnityEditor.GameView,UnityEditor");
                    var snap = type?.GetMethod("SnapZoom", BindingFlags.Instance | BindingFlags.NonPublic,
                                               null, new[] { typeof(float) }, null);
                    var zoomField = type?.GetField("m_ZoomArea", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (snap == null || zoomField == null)
                    {
                        Debug.LogWarning("[Etsim] Game ビューの拡大率を戻せません（この Unity では内部の作りが違います）");
                        return;
                    }

                    foreach (var view in Resources.FindObjectsOfTypeAll(type))
                    {
                        var zoom = zoomField.GetValue(view);
                        var scaleProp = zoom?.GetType().GetProperty("scale");
                        if (scaleProp == null) continue;
                        float scale = ((Vector2)scaleProp.GetValue(zoom)).x;
                        if (Mathf.Abs(scale - 1f) < 0.001f) continue;

                        snap.Invoke(view, new object[] { 1f });
                        ((EditorWindow)view).Repaint();
                        Debug.Log($"[Etsim] Game ビューの拡大率を {scale:F3} 倍から 1 倍に戻しました"
                                  + "（拡大していると画面の端の HUD が切れるため）");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Etsim] Game ビューの拡大率を戻せません: {e.Message}");
                }
            };
        }
    }
}
