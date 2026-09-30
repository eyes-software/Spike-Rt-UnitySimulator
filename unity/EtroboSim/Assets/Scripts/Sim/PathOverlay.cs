using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// コースの上に折れ線を描く（見るだけ。物理にもセンサにも影響しない）。
    ///
    ///   Unity.exe ... -etsimOverlay path.txt
    ///
    /// ファイルの書き方（座標はコースの mm）:
    ///   # 名前,RRGGBB[,dot][,太さmm]  ← 線の始まり。色は 16 進（省略すると橙）。
    ///                        dot を付けると点線（12mm 描いて 12mm あける）。太さを省くと 12mm
    ///   x y                  ← 点。空白かカンマで区切る
    ///   x y
    ///                        ← 空行で線が終わる
    /// 走行プログラムが想定している道すじを、実際に走らせる前に確かめるためのもの。
    /// </summary>
    public class PathOverlay : MonoBehaviour
    {
        const float LineWidthM = 0.012f;   // 線の太さ（12mm。コース全体を映したときにも見えるように）
        const float HeightM = 0.002f;      // 床から浮かせる高さ（床の模様に埋もれないように）

        public static PathOverlay Load(string path, Transform parent)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[Etsim] -etsimOverlay のファイルがありません: {path}");
                return null;
            }

            var go = new GameObject("PathOverlay");
            go.transform.SetParent(parent, false);
            var overlay = go.AddComponent<PathOverlay>();

            string name = null;
            Color32 color = new Color32(0xF6, 0x8B, 0x1F, 255);
            bool dotted = false;
            float widthMm = LineWidthM * 1000f;
            var points = new List<Vector2>();
            int lines = 0;

            void Flush()
            {
                if (points.Count >= 2)
                {
                    string n = name ?? $"line{lines}";
                    if (dotted) overlay.AddDotted(n, color, points, widthMm);
                    else overlay.AddLine(n, color, points, widthMm);
                    lines++;
                }
                points.Clear();
                name = null;
                dotted = false;
                widthMm = LineWidthM * 1000f;
            }

            foreach (string raw in File.ReadAllLines(path))
            {
                string s = raw.Trim();
                if (s.Length == 0) { Flush(); continue; }
                if (s.StartsWith("#"))
                {
                    Flush();
                    var f = s.Substring(1).Trim().Split(',');
                    name = f[0].Trim();
                    if (f.Length > 1 && TryParseColor(f[1].Trim(), out var c)) color = c;
                    for (int k = 2; k < f.Length; k++)
                    {
                        string opt = f[k].Trim();
                        if (opt == "dot") dotted = true;
                        else if (float.TryParse(opt, NumberStyles.Float, CultureInfo.InvariantCulture, out float w) && w > 0f)
                            widthMm = w;
                    }
                    continue;
                }
                var v = s.Split(new[] { ' ', '\t', ',' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (v.Length >= 2
                    && float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                    points.Add(new Vector2(x, y));
            }
            Flush();

            Debug.Log($"[Etsim] 道すじを {lines} 本描きました ({path})");
            return overlay;
        }

        const float DotMm = 12f;           // 点線の描く長さとあける長さ

        /// <summary>点線。折れ線を道のりに沿って DotMm ごとに描く・あけるを繰り返す。</summary>
        void AddDotted(string lineName, Color32 color, List<Vector2> pointsMm, float widthMm)
        {
            var dash = new List<Vector2>();
            float along = 0f;          // 今の区切り（描く or あける）の中で進んだ長さ
            bool draw = true;
            int n = 0;
            dash.Add(pointsMm[0]);
            for (int i = 1; i < pointsMm.Count; i++)
            {
                Vector2 a = pointsMm[i - 1], b = pointsMm[i];
                float len = Vector2.Distance(a, b), used = 0f;
                while (len - used > 1e-3f)
                {
                    float step = Mathf.Min(DotMm - along, len - used);
                    used += step; along += step;
                    Vector2 p = Vector2.Lerp(a, b, used / len);
                    if (draw) dash.Add(p);
                    if (along >= DotMm - 1e-3f)
                    {
                        if (draw && dash.Count >= 2) AddLine($"{lineName}_{n++}", color, dash, widthMm);
                        dash.Clear();
                        draw = !draw;
                        along = 0f;
                        if (draw) dash.Add(p);
                    }
                }
            }
            if (draw && dash.Count >= 2) AddLine($"{lineName}_{n}", color, dash, widthMm);
        }

        static readonly Dictionary<uint, Material> _unlit = new Dictionary<uint, Material>();

        /// <summary>照明で暗くならない（指定した色がそのまま見える）材質。</summary>
        static Material UnlitMaterial(Color32 color)
        {
            uint key = (uint)((color.r << 24) | (color.g << 16) | (color.b << 8) | color.a);
            if (_unlit.TryGetValue(key, out var m) && m != null) return m;
            var shader = Shader.Find("Unlit/Color");
            if (shader == null) return RobotVisuals.GetMaterial(color);
            m = new Material(shader) { name = $"Overlay_{color.r:X2}{color.g:X2}{color.b:X2}", color = color };
            _unlit[key] = m;
            return m;
        }

        void AddLine(string lineName, Color32 color, List<Vector2> pointsMm, float widthMm)
        {
            var go = new GameObject(lineName);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.widthMultiplier = widthMm * 0.001f;
            lr.positionCount = pointsMm.Count;
            for (int i = 0; i < pointsMm.Count; i++)
                lr.SetPosition(i, CourseSpace.ToWorld(pointsMm[i], HeightM));
            lr.material = UnlitMaterial(color);
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
        }

        static bool TryParseColor(string hex, out Color32 c)
        {
            c = default;
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                return false;
            c = new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
            return true;
        }
    }
}
