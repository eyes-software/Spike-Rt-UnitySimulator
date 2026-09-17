using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// 見た目用のプリミティブ生成。コース・走行体の両方から使う。
    ///
    /// FBX などの外部アセットを一切使わず、寸法から直接組み立てる方針なので
    /// 生成はすべてここに集約する。コライダは呼び出し側が別途付けるため、
    /// ここで作るオブジェクトからは必ず Collider を外している。
    /// </summary>
    public static class RobotVisuals
    {
        static readonly Dictionary<uint, Material> _materialCache = new Dictionary<uint, Material>();
        static Shader _litShader;

        public static Shader LitShader
        {
            get
            {
                if (_litShader != null) return _litShader;
                // ビルトイン RP を既定にしつつ、URP のプロジェクトでも壊れないようにする
                _litShader = Shader.Find("Standard")
                          ?? Shader.Find("Universal Render Pipeline/Lit")
                          ?? Shader.Find("Unlit/Color");
                return _litShader;
            }
        }

        static Shader _unlitShader;

        /// <summary>
        /// 陰影を付けないシェーダ。コースシートに使う。
        ///
        /// シートは印刷物なので、テクスチャの色がそのまま出るのが正しい。
        /// 陰影を付けると、白 (255) と薄い灰 (220) のような差が
        /// 露出で飽和して消えてしまう（ラリーエリアの灰色の丸がこれで見えなくなる）。
        /// センサが読む色と画面の色が一致する、という点でも都合がよい。
        /// </summary>
        public static Shader UnlitShader
        {
            get
            {
                if (_unlitShader != null) return _unlitShader;
                _unlitShader = Shader.Find("Unlit/Texture")
                            ?? Shader.Find("Universal Render Pipeline/Unlit")
                            ?? LitShader;
                return _unlitShader;
            }
        }

        public static Material GetMaterial(Color32 color, float smoothness = 0.15f)
        {
            uint key = (uint)((color.r << 24) | (color.g << 16) | (color.b << 8) | color.a);
            if (_materialCache.TryGetValue(key, out var cached) && cached != null) return cached;

            var mat = new Material(LitShader) { name = $"Sim_{color.r:X2}{color.g:X2}{color.b:X2}" };
            mat.color = color;
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            _materialCache[key] = mat;
            return mat;
        }

        /// <summary>
        /// 半透明のマテリアル。ペットボトルのような透明樹脂に使う。
        ///
        /// Standard シェーダは既定が不透明なので、ブレンドとキーワードを
        /// 自分で切り替えないと alpha が効かない（設定し忘れると真っ白になる）。
        /// </summary>
        public static Material GetTransparentMaterial(Color color, float smoothness = 0.85f)
        {
            uint key = 0x80000000u | (uint)((Mathf.RoundToInt(color.r * 255) << 16)
                                          | (Mathf.RoundToInt(color.g * 255) << 8)
                                          | Mathf.RoundToInt(color.a * 255));
            if (_materialCache.TryGetValue(key, out var cached) && cached != null) return cached;

            var mat = new Material(LitShader) { name = "SimGlass" };
            mat.color = color;

            if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 3f);   // Transparent
            mat.SetOverrideTag("RenderType", "Transparent");
            if (mat.HasProperty("_SrcBlend"))
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend"))
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);   // URP
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            _materialCache[key] = mat;
            return mat;
        }

        /// <summary>
        /// 回転体のメッシュ。profile は (高さ, 半径) の並び [m]。
        /// ペットボトルのように「円柱では済まない形」を寸法から起こすために使う。
        /// </summary>
        public static Mesh CreateLatheMesh(string name, Vector2[] profile, int segments = 32)
        {
            int rings = profile.Length;
            var verts = new Vector3[rings * (segments + 1)];
            var uvs = new Vector2[verts.Length];

            for (int r = 0; r < rings; r++)
            {
                for (int i = 0; i <= segments; i++)
                {
                    float a = i * 2f * Mathf.PI / segments;
                    int idx = r * (segments + 1) + i;
                    verts[idx] = new Vector3(Mathf.Cos(a) * profile[r].y,
                                             profile[r].x,
                                             Mathf.Sin(a) * profile[r].y);
                    uvs[idx] = new Vector2(i / (float)segments, r / (float)(rings - 1));
                }
            }

            var tris = new System.Collections.Generic.List<int>((rings - 1) * segments * 6 + segments * 6);
            for (int r = 0; r + 1 < rings; r++)
            {
                for (int i = 0; i < segments; i++)
                {
                    int a = r * (segments + 1) + i;
                    int b = a + 1;
                    int c = a + segments + 1;
                    int d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }

            // 底と上のふた
            AddCap(verts, tris, profile, segments, 0, true);
            AddCap(verts, tris, profile, segments, rings - 1, false);

            var mesh = new Mesh { name = name };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddCap(Vector3[] verts, System.Collections.Generic.List<int> tris,
                           Vector2[] profile, int segments, int ring, bool down)
        {
            if (profile[ring].y <= 1e-6f) return;
            int centre = ring * (segments + 1);   // 扇の要には縁の点を使い回す
            for (int i = 1; i + 1 <= segments; i++)
            {
                int a = centre;
                int b = centre + i;
                int c = centre + i + 1;
                if (down) { tris.Add(a); tris.Add(b); tris.Add(c); }
                else { tris.Add(a); tris.Add(c); tris.Add(b); }
            }
        }

        /// <summary>シーンを作り直したときに古いマテリアルを引きずらないようにする。</summary>
        public static void ClearCache() => _materialCache.Clear();

        static Transform CreatePrimitive(PrimitiveType type, Transform parent, string name,
                                         Vector3 localPos, Color32 color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;

            // 見た目専用。物理は呼び出し側が明示的に組む。
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.GetComponent<MeshRenderer>().sharedMaterial = GetMaterial(color);
            return go.transform;
        }

        public static Transform CreateBox(Transform parent, string name, Vector3 localPos,
                                          Vector3 size, Color32 color)
        {
            var t = CreatePrimitive(PrimitiveType.Cube, parent, name, localPos, color);
            t.localScale = size;
            return t;
        }

        /// <summary>Y 軸が高さ方向の円柱。size = (直径, 高さ, 直径)。</summary>
        public static Transform CreateCylinder(Transform parent, string name, Vector3 localPos,
                                               float diameter, float height, Color32 color)
        {
            var t = CreatePrimitive(PrimitiveType.Cylinder, parent, name, localPos, color);
            // Unity の Cylinder は高さ 2・直径 1
            t.localScale = new Vector3(diameter, height * 0.5f, diameter);
            return t;
        }

        /// <summary>X 軸（左右方向）を回転軸とする車輪。</summary>
        public static Transform CreateWheel(Transform parent, string name, float diameter, float width)
        {
            var t = CreatePrimitive(PrimitiveType.Cylinder, parent, name, Vector3.zero,
                                    new Color32(32, 32, 34, 255));
            t.localScale = new Vector3(diameter, width * 0.5f, diameter);
            t.localRotation = Quaternion.Euler(0f, 0f, 90f); // 円柱の軸 (Y) を X へ倒す
            return t;
        }

        /// <summary>1 枚の水平な四角形メッシュ。床やコースシートに使う。</summary>
        public static Mesh CreateQuadMesh(float width, float depth, float uvTiling = 1f)
        {
            var mesh = new Mesh { name = "SimQuad" };
            float hw = width * 0.5f, hd = depth * 0.5f;

            mesh.vertices = new[]
            {
                new Vector3(-hw, 0f, -hd),
                new Vector3( hw, 0f, -hd),
                new Vector3( hw, 0f,  hd),
                new Vector3(-hw, 0f,  hd),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(uvTiling, 0f),
                new Vector2(uvTiling, uvTiling),
                new Vector2(0f, uvTiling),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
