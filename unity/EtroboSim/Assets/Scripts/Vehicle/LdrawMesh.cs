using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Etrobo.Sim
{
    /// <summary>
    /// tools/ldraw_to_mesh.py が書き出した走行体メッシュ (.etmesh) を読む。
    ///
    /// ETロボコンの走行体は LDraw 形式で組立図が公開されているので、
    /// 外観を箱で近似するのではなく、実際の部品そのものを描ける。
    /// 変換は Python 側で済ませてあり、ここは座標をそのまま Mesh に積むだけ。
    ///
    /// 形式は「寸法データと同じ扱い」で、FBX などの外部アセットには依存しない。
    /// </summary>
    public static class LdrawMesh
    {
        public sealed class Group
        {
            public string Name;
            /// <summary>走行体ローカルでの回転中心 [m]。頂点はここを原点にしてある。</summary>
            public Vector3 Origin;
            public Mesh Mesh;
            public Color32[] Colors;   // サブメッシュごとの色
        }

        const uint Magic = 0x534D5445;   // "ETMS" (リトルエンディアン)

        /// <summary>読めなければ null。呼び出し側は箱の見た目へ落とす。</summary>
        public static List<Group> Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            try
            {
                using (var fs = File.OpenRead(path))
                using (var r = new BinaryReader(fs))
                {
                    if (r.ReadUInt32() != Magic)
                    {
                        Debug.LogWarning($"[Etsim] '{path}' は .etmesh ではありません");
                        return null;
                    }

                    uint version = r.ReadUInt32();
                    if (version != 1)
                    {
                        Debug.LogWarning($"[Etsim] '{path}' の版 {version} は読めません");
                        return null;
                    }

                    int groupCount = (int)r.ReadUInt32();
                    var groups = new List<Group>(groupCount);
                    for (int g = 0; g < groupCount; g++) groups.Add(ReadGroup(r));
                    return groups;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Etsim] 走行体メッシュ '{path}' を読めませんでした ({e.Message})");
                return null;
            }
        }

        static Group ReadGroup(BinaryReader r)
        {
            int nameLen = r.ReadByte();
            string name = System.Text.Encoding.ASCII.GetString(r.ReadBytes(nameLen));

            var origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            int vertexCount = (int)r.ReadUInt32();
            var verts = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                verts[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            int subCount = (int)r.ReadUInt32();
            var colors = new Color32[subCount];
            var indices = new int[subCount][];
            for (int s = 0; s < subCount; s++)
            {
                colors[s] = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
                int n = (int)r.ReadUInt32();
                var idx = new int[n];
                for (int i = 0; i < n; i++) idx[i] = (int)r.ReadUInt32();
                indices[s] = idx;
            }

            var mesh = new Mesh { name = "Ldraw_" + name };
            // 部品ごとに頂点が数万あるので 16bit では足りない
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++)
                mesh.SetTriangles(indices[s], s, false);

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return new Group { Name = name, Origin = origin, Mesh = mesh, Colors = colors };
        }

        /// <summary>まとまりを親の下に置く。コライダは付けない（物理は別に組んである）。</summary>
        public static Transform Attach(Group group, Transform parent, Vector3 localPosition)
        {
            var go = new GameObject("Ldraw_" + group.Name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            go.AddComponent<MeshFilter>().sharedMesh = group.Mesh;

            var mr = go.AddComponent<MeshRenderer>();
            var mats = new Material[group.Colors.Length];
            for (int i = 0; i < mats.Length; i++)
                mats[i] = RobotVisuals.GetMaterial(group.Colors[i], 0.25f);
            mr.sharedMaterials = mats;

            return go.transform;
        }
    }
}
