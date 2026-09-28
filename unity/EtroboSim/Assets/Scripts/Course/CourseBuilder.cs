using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// CourseDefinition から実際の GameObject 一式を組み立てる。
    /// 外部 3D アセットは使わず、寸法から毎回生成する。
    /// </summary>
    public sealed class CourseBuilder
    {
        public GameObject Root { get; private set; }
        public CourseField Field { get; private set; }
        public Transform Sheet { get; private set; }

        /// <summary>組み立てた難所などの置き物。id で引ける（実行中に動かすため）。</summary>
        public IReadOnlyDictionary<string, Transform> Props => _props;

        readonly Dictionary<string, Transform> _props = new Dictionary<string, Transform>();
        public List<CourseGate> Gates => _def.Gates;

        readonly CourseDefinition _def;

        public CourseBuilder(CourseDefinition def)
        {
            _def = def;
        }

        /// <summary>床テクスチャの総画素数の上限。Build() で指定する。</summary>
        float _maxSheetTexturePixels = 20_000_000f;

        public GameObject Build(float sheetTexturePixelsPerMm = 1.0f,
                                float maxSheetTexturePixels = 20_000_000f)
        {
            _maxSheetTexturePixels = Mathf.Max(10_000f, maxSheetTexturePixels);
            Field = new CourseField(_def);

            Root = new GameObject("Course_" + _def.Name);
            BuildSheet(sheetTexturePixelsPerMm);
            BuildProps();
            BuildGateGizmos();
            return Root;
        }

        float ClampToPixelBudget(float requestedPixelsPerMm)
        {
            float areaMm2 = _def.SheetWidthMm * _def.SheetHeightMm;
            if (areaMm2 <= 0f) return requestedPixelsPerMm;

            // Texture2D の 1 辺の上限 (16384) も超えないようにする
            float byBudget = Mathf.Sqrt(_maxSheetTexturePixels / areaMm2);
            float bySide = 16384f / Mathf.Max(_def.SheetWidthMm, _def.SheetHeightMm);

            return Mathf.Min(requestedPixelsPerMm, Mathf.Min(byBudget, bySide));
        }

        void BuildSheet(float pixelsPerMm)
        {
            float w = _def.SheetWidthMm * CourseSpace.MmToM;
            float d = _def.SheetHeightMm * CourseSpace.MmToM;

            var go = new GameObject("Sheet");
            go.transform.SetParent(Root.transform, false);
            // コース原点をワールド原点に合わせるため、シートの中心へオフセットする
            go.transform.localPosition = new Vector3(w * 0.5f, 0f, d * 0.5f);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = RobotVisuals.CreateQuadMesh(w, d);

            var mr = go.AddComponent<MeshRenderer>();
            // 陰影なし。印刷物の色をそのまま出す（理由は RobotVisuals.UnlitShader を参照）
            var mat = new Material(RobotVisuals.UnlitShader) { name = "CourseSheet" };

            // 床テクスチャは 1 画素ずつ幾何をサンプリングして作るので、
            // コースが大きいと生成時間が効いてくる。公式コース (5460x3640mm) を
            // 0.5px/mm で焼くと 500 万画素になり、起動のたびに数秒待たされる。
            // テクスチャは見た目専用（センサは幾何を直接見る）なので、
            // 総画素数に上限を設けて大きいコースだけ自動で解像度を落とす。
            float effectivePixelsPerMm = ClampToPixelBudget(pixelsPerMm);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var texture = Field.BakeTexture(effectivePixelsPerMm);
            sw.Stop();

            string note = effectivePixelsPerMm < pixelsPerMm
                ? $" (要求 {pixelsPerMm} px/mm を画素数上限に合わせて {effectivePixelsPerMm:F2} へ調整)"
                : "";
            Debug.Log($"[Etsim] 床テクスチャ {texture.width}x{texture.height} を "
                      + $"{sw.ElapsedMilliseconds} ms で生成しました{note}。"
                      + "見た目専用なので、センサの精度には影響しません。");
            mat.mainTexture = texture;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            mr.sharedMaterial = mat;

            // 平面メッシュには厚みがないので、コライダは薄い箱を下向きに置く
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(w, 0.02f, d);
            box.center = new Vector3(0f, -0.01f, 0f);

            var surface = go.AddComponent<CourseSurface>();
            surface.Field = Field;

            Sheet = go.transform;
        }

        void BuildProps()
        {
            if (_def.Props.Count == 0) return;

            var propsRoot = new GameObject("Props");
            propsRoot.transform.SetParent(Root.transform, false);

            foreach (var p in _def.Props)
            {
                switch (p.Type)
                {
                    case "seesaw": BuildSeesaw(propsRoot.transform, p); break;
                    case "ramp": BuildRamp(propsRoot.transform, p); break;
                    case "cylinder": BuildSimpleProp(propsRoot.transform, p, true); break;
                    case "bottle": BuildBottle(propsRoot.transform, p); break;
                    case "gate": BuildGate(propsRoot.transform, p); break;
                    default: BuildSimpleProp(propsRoot.transform, p, false); break;
                }
            }
        }

        static Vector3 SizeToMeters(Vector3 sizeMm) => sizeMm * CourseSpace.MmToM;

        GameObject NewProp(Transform parent, CourseProp p, float heightM)
        {
            var go = new GameObject(p.Id);
            go.transform.SetParent(parent, false);
            _props[p.Id] = go.transform;
            go.transform.SetPositionAndRotation(
                CourseSpace.ToWorld(p.Position, heightM),
                CourseSpace.HeadingToRotation(p.HeadingDeg));

            var surface = go.AddComponent<PropSurface>();
            surface.SurfaceColor = p.Color;
            return go;
        }

        void BuildSimpleProp(Transform parent, CourseProp p, bool cylinder)
        {
            Vector3 size = SizeToMeters(p.Size);
            // size は (幅X, 奥行Y, 高さZ)。Unity では高さが Y なので入れ替える。
            Vector3 unitySize = new Vector3(size.x, size.z, size.y);

            var go = NewProp(parent, p, unitySize.y * 0.5f);

            if (cylinder)
            {
                RobotVisuals.CreateCylinder(go.transform, "Mesh", Vector3.zero,
                    size.x, unitySize.y, p.Color);
                var cc = go.AddComponent<CapsuleCollider>();
                cc.direction = 1; // Y 軸
                cc.radius = size.x * 0.5f;
                cc.height = unitySize.y;
            }
            else
            {
                RobotVisuals.CreateBox(go.transform, "Mesh", Vector3.zero, unitySize, p.Color);
                var bc = go.AddComponent<BoxCollider>();
                bc.size = unitySize;
            }

            if (p.Movable)
            {
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = Mathf.Max(0.001f, p.Mass);
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
        }

        /// <summary>
        /// ペットボトル。ET相撲の力士ボトルと、ボトルデリバリーのキャリーボトル。
        ///
        /// 円柱ではなく、胴・肩・首・キャップの断面を回した形にする。
        /// 実物は肩から上が細いので、円柱で近似すると距離センサに映る幅も
        /// 押したときの当たり方も変わる。底は実物と同じく胴より小さい接地円で、
        /// これが倒れやすさ（接地円の半径 ÷ 重心高さ）を決める。
        ///
        /// 当たり判定は凸 MeshCollider。カプセルだと底が丸くて自立せず、
        /// 「底面が接地して静止していること」という競技の判定が再現できない。
        /// </summary>
        void BuildBottle(Transform parent, CourseProp p)
        {
            float mm = CourseSpace.MmToM;
            float radius = p.Size.x * 0.5f * mm;
            float height = p.Size.z * mm;

            var go = NewProp(parent, p, 0f);

            var mesh = RobotVisuals.CreateLatheMesh(p.Id + "_Mesh", BottleProfile(radius, height));

            var body = new GameObject("Mesh");
            body.transform.SetParent(go.transform, false);
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            // PET は透明。色に alpha を持たせて半透明マテリアルで描く。
            var glass = new Color(p.Color.r / 255f, p.Color.g / 255f, p.Color.b / 255f, 0.22f);
            body.AddComponent<MeshRenderer>().sharedMaterial =
                RobotVisuals.GetTransparentMaterial(glass);

            var mc = body.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = true;

            // ラベル。養生テープなので不透明。色を見る課題なので高さを実物に合わせる。
            if (p.LabelHeightMm > 0f)
            {
                // カラーセンサが横から見たときに、胴ではなくラベルの色を返せるよう
                // 帯の範囲を覚えておく。当たり判定は胴のままで、増やさない。
                var surface = go.GetComponent<PropSurface>();
                if (surface != null)
                {
                    surface.HasLabel = true;
                    surface.LabelColor = p.LabelColor;
                    surface.LabelBottomMm = p.LabelBottomMm;
                    surface.LabelTopMm = p.LabelBottomMm + p.LabelHeightMm;
                }

                float labelCentre = (p.LabelBottomMm + p.LabelHeightMm * 0.5f) * mm;
                RobotVisuals.CreateCylinder(go.transform, "Label",
                    new Vector3(0f, labelCentre, 0f),
                    radius * 2.03f, p.LabelHeightMm * mm, p.LabelColor);
            }

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(0.001f, p.Mass);
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            if (p.CenterOfMassHeightMm > 0f)
                rb.centerOfMass = new Vector3(0f, p.CenterOfMassHeightMm * mm, 0f);

            //
            // 転がり抵抗のぶんを角速度の減衰で入れる。
            //
            // PhysX は転がり抵抗を持たないので、これが無いと横倒しになった
            // ボトルがコース上をいつまでも転がり続ける（実測で 100 秒たっても
            // 止まらなかった）。実物の PET ボトルは紙のマットの上なら数秒で
            // 止まるので、判定の「静止していること」がいつまでも成立しない。
            // 立っている間はほとんど効かない（回らないので）。
            //
            rb.angularDrag = 4f;
            rb.drag = 0.2f;
        }

        /// <summary>
        /// ペットボトルの断面 (高さ, 半径)。原点は底。
        /// 丸型ボトルの実物に合わせて、接地円は胴の 0.8 倍、
        /// 首とキャップは容量によらずほぼ同じ太さ（口内径 φ20）にしている。
        /// </summary>
        static Vector2[] BottleProfile(float radius, float height)
        {
            float mm = CourseSpace.MmToM;
            float neck = 13f * mm;
            float cap = 15f * mm;
            float capH = 14f * mm;

            return new[]
            {
                new Vector2(0f,                 radius * 0.80f),
                new Vector2(3f * mm,            radius * 0.97f),
                new Vector2(8f * mm,            radius),
                new Vector2(height * 0.66f,     radius),
                new Vector2(height * 0.74f,     radius * 0.97f),
                new Vector2(height - 40f * mm,  radius * 0.80f),
                new Vector2(height - 22f * mm,  Mathf.Max(neck, radius * 0.45f)),
                new Vector2(height - capH - 2f * mm, neck),
                new Vector2(height - capH,      neck),
                new Vector2(height - capH,      cap),
                new Vector2(height,             cap),
            };
        }

        /// <summary>
        /// ETラリーのゲート。VP13 の塩ビ管に TS エルボと TS チーズを付けたコの字。
        /// 管はビニテープの色、継手は塩ビの灰色で、実物と同じ塗り分けにしてある。
        /// 足元のチーズはゲートの面と直交に寝ていて、これが自立する脚になる。
        ///
        /// size は (脚の内寸, 未使用, 内側の高さ) [mm]。
        /// 脚はローカル X 方向に並ぶ。コース方位 0 は +y を向くので、
        /// heading 0 なら脚は y 方向（前後）、heading 90 なら x 方向（左右）に並ぶ。
        /// </summary>
        void BuildGate(Transform parent, CourseProp p)
        {
            float mm = CourseSpace.MmToM;
            float innerWidth = p.Size.x * mm;
            float innerHeight = p.Size.z * mm;
            float pipe = p.PipeDiameterMm * mm;

            // TS 継手は管より一回り太い。VP13 用の実寸に近い値。
            float fitting = 26f * mm;
            float fittingArm = 21f * mm;       // 継手の中心から端まで
            var gray = new Color32(120, 124, 128, 255);

            var go = NewProp(parent, p, 0f);

            float legX = (innerWidth + pipe) * 0.5f;
            float topY = innerHeight + pipe * 0.5f;
            float tieY = fitting * 0.5f;       // 足元のチーズの中心高さ

            for (int i = 0; i < 2; i++)
            {
                float x = (i == 0) ? -legX : legX;

                // 足元の TS チーズ。ゲートの面と直交に寝かせる。
                AddPipe(go.transform, "Tee" + i, new Vector3(x, tieY, 0f),
                        fitting, fittingArm * 2f, gray, Axis.Z);
                AddPipe(go.transform, "TeeUp" + i, new Vector3(x, tieY + fittingArm * 0.5f, 0f),
                        fitting, fittingArm, gray, Axis.Y);

                // 脚（色付きの管）
                float legBottom = tieY + fittingArm;
                float legTop = topY - fittingArm;
                AddPipe(go.transform, "Leg" + i,
                        new Vector3(x, (legBottom + legTop) * 0.5f, 0f),
                        pipe, legTop - legBottom, p.Color, Axis.Y);

                // 上の TS エルボ（縦の足と横の足）
                AddPipe(go.transform, "ElbowV" + i,
                        new Vector3(x, topY - fittingArm * 0.5f, 0f),
                        fitting, fittingArm, gray, Axis.Y);
                float inward = (i == 0) ? fittingArm * 0.5f : -fittingArm * 0.5f;
                AddPipe(go.transform, "ElbowH" + i, new Vector3(x + inward, topY, 0f),
                        fitting, fittingArm, gray, Axis.X);
            }

            // 上の管（色付き）。エルボの内側どうしを結ぶ。
            AddPipe(go.transform, "Top", new Vector3(0f, topY, 0f),
                    pipe, innerWidth + pipe - fittingArm, p.Color, Axis.X);
        }

        enum Axis { X, Y, Z }

        /// <summary>管 1 本。円柱プリミティブの軸 (Y) を指定した向きへ倒す。</summary>
        static void AddPipe(Transform parent, string name, Vector3 localPos,
                            float diameter, float length, Color32 color, Axis axis)
        {
            var t = RobotVisuals.CreateCylinder(parent, name, localPos, diameter, length, color);
            if (axis == Axis.X) t.localRotation = Quaternion.Euler(0f, 0f, 90f);
            else if (axis == Axis.Z) t.localRotation = Quaternion.Euler(90f, 0f, 0f);

            var cc = t.gameObject.AddComponent<CapsuleCollider>();
            cc.direction = 1;      // 縮尺は t に入っているのでローカル寸法で指定する
            cc.radius = 0.5f;
            cc.height = 2f;
        }

        void BuildRamp(Transform parent, CourseProp p)
        {
            Vector3 size = SizeToMeters(p.Size); // (幅, 奥行, 高さ)
            var go = NewProp(parent, p, 0f);

            // 斜面は薄板を傾けて作る。斜辺長を出しておかないと厚みぶん浮く。
            float run = size.y;
            float rise = size.z;
            float slopeLen = Mathf.Sqrt(run * run + rise * rise);
            float angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;
            const float plankThickness = 0.006f;

            var plank = RobotVisuals.CreateBox(go.transform, "Mesh",
                new Vector3(0f, rise * 0.5f, 0f),
                new Vector3(size.x, plankThickness, slopeLen), p.Color);
            plank.localRotation = Quaternion.Euler(-angle, 0f, 0f);

            var bc = plank.gameObject.AddComponent<BoxCollider>();
            bc.size = Vector3.one; // 親のスケールが効くので単位サイズでよい
        }

        void BuildSeesaw(Transform parent, CourseProp p)
        {
            Vector3 size = SizeToMeters(p.Size); // (長さ, 幅, 厚み)
            float pivotH = p.PivotHeightMm * CourseSpace.MmToM;

            var go = NewProp(parent, p, 0f);

            // 支点。動かないので kinematic にする
            var pivot = new GameObject("Pivot");
            pivot.transform.SetParent(go.transform, false);
            pivot.transform.localPosition = new Vector3(0f, pivotH * 0.5f, 0f);
            var pivotRb = pivot.AddComponent<Rigidbody>();
            pivotRb.isKinematic = true;
            RobotVisuals.CreateBox(pivot.transform, "Mesh", Vector3.zero,
                new Vector3(size.y * 0.5f, pivotH, size.y * 0.25f), new Color32(90, 90, 96, 255));

            // 板。支点まわりに前後へ傾く
            var plankGo = new GameObject("Plank");
            plankGo.transform.SetParent(go.transform, false);
            plankGo.transform.localPosition = new Vector3(0f, pivotH + size.z * 0.5f, 0f);

            var plankSurface = plankGo.AddComponent<PropSurface>();
            plankSurface.SurfaceColor = p.Color;

            RobotVisuals.CreateBox(plankGo.transform, "Mesh", Vector3.zero,
                new Vector3(size.y, size.z, size.x), p.Color);

            var plankCol = plankGo.AddComponent<BoxCollider>();
            plankCol.size = new Vector3(size.y, size.z, size.x);

            var plankRb = plankGo.AddComponent<Rigidbody>();
            plankRb.mass = Mathf.Max(0.01f, p.Mass);
            plankRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var hinge = plankGo.AddComponent<HingeJoint>();
            hinge.connectedBody = pivotRb;
            hinge.anchor = new Vector3(0f, -size.z * 0.5f, 0f);
            hinge.axis = new Vector3(1f, 0f, 0f); // 左右方向の軸まわりに前後へ傾く
            hinge.useLimits = true;
            float limitDeg = Mathf.Atan2(pivotH, size.x * 0.5f) * Mathf.Rad2Deg;
            hinge.limits = new JointLimits { min = -limitDeg, max = limitDeg, bounciness = 0f };
            hinge.useSpring = false;
        }

        void BuildGateGizmos()
        {
            if (_def.Gates.Count == 0) return;

            var go = new GameObject("Gates");
            go.transform.SetParent(Root.transform, false);
            var viz = go.AddComponent<GateVisualizer>();
            viz.Gates = _def.Gates;
        }
    }

    /// <summary>計測ゲートはシーンビューにだけ描く。床の色を汚さないため。</summary>
    public sealed class GateVisualizer : MonoBehaviour
    {
        public List<CourseGate> Gates;

        void OnDrawGizmos()
        {
            if (Gates == null) return;
            foreach (var g in Gates)
            {
                Gizmos.color = g.Kind switch
                {
                    GateKind.Start => new Color(0.2f, 0.9f, 0.3f, 0.9f),
                    GateKind.Goal => new Color(0.9f, 0.3f, 0.2f, 0.9f),
                    _ => new Color(0.4f, 0.6f, 1f, 0.7f)
                };
                Vector3 a = CourseSpace.ToWorld(g.From, 0.005f);
                Vector3 b = CourseSpace.ToWorld(g.To, 0.005f);
                Gizmos.DrawLine(a, b);
                Gizmos.DrawLine(a, a + Vector3.up * 0.08f);
                Gizmos.DrawLine(b, b + Vector3.up * 0.08f);
            }
        }
    }
}
