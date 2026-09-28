using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// ソナーの掃引を目で見えるようにする。
    ///
    /// 数字だけ出しても「どこを向いたときに何を見たのか」が伝わらないので、
    /// その場回転を時間をかけて行い、いま出しているビームと、当たった点を
    /// コースの上に描く。当たった点は消さずに残すので、回り終わると
    /// 極座標のプロットがそのままコース上に出来上がる。
    /// </summary>
    public sealed class SonarSweepView : MonoBehaviour
    {
        public SimManager Manager;

        /// <summary>当たらなかったときにビームを描く長さ [m]。</summary>
        const float MissLengthM = 0.35f;

        /// <summary>残す点の大きさ [m]。</summary>
        const float MarkSizeM = 0.012f;

        LineRenderer _beam;
        Transform _marks;

        void EnsureVisuals()
        {
            if (_beam != null) return;

            var go = new GameObject("SonarBeam");
            go.transform.SetParent(transform, false);
            _beam = go.AddComponent<LineRenderer>();
            _beam.positionCount = 2;
            _beam.widthMultiplier = 0.004f;
            _beam.useWorldSpace = true;
            _beam.material = RobotVisuals.GetMaterial(new Color32(0xE8, 0x38, 0x28, 255));
            _beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _beam.receiveShadows = false;

            _marks = new GameObject("SonarMarks").transform;
            _marks.SetParent(transform, false);
        }

        /// <summary>
        /// 掃引を 1 周ぶん回す。seconds が 0 以下なら 1 フレームで終わらせる。
        /// frameDir を渡すと、途中のコマを連番 PNG で保存する。
        /// </summary>
        public IEnumerator Run(float stepDeg, float seconds, System.Action<int> captureFrame)
        {
            if (stepDeg <= 0f || Manager == null || Manager.Robot == null) yield break;

            EnsureVisuals();

            int steps = Mathf.Max(1, Mathf.RoundToInt(360f / stepDeg));
            float wait = seconds > 0f ? seconds / steps : 0f;
            float baseHeading = Manager.StartHeadingDeg;
            var hits = new List<Vector3>();

            Debug.Log($"[Etsim] ソナー掃引（表示つき）{steps} 点 / {seconds:F1} 秒");

            for (int i = 0; i <= steps; i++)
            {
                float h = Mathf.Repeat(baseHeading + i * stepDeg + 180f, 360f) - 180f;
                Manager.Robot.ResetPose(Manager.StartPositionMm, h);
                Physics.SyncTransforms();
                Manager.Ultrasonic.Measure();

                DrawBeam(Manager.Ultrasonic.DistanceMm, hits);
                captureFrame?.Invoke(i);

                if (wait > 0f) yield return new WaitForSecondsRealtime(wait);
                else yield return null;
            }

            Manager.Robot.ResetPose(Manager.StartPositionMm, baseHeading);
            Physics.SyncTransforms();
            Debug.Log($"[Etsim] ソナー掃引（表示つき）終了 当たった点 {hits.Count} 個");
        }

        void DrawBeam(int distanceMm, List<Vector3> hits)
        {
            Transform mount = Manager.Robot.UltrasonicMount;
            if (mount == null) return;

            bool hit = distanceMm >= 0;
            float lenM = hit ? distanceMm * CourseSpace.MmToM : MissLengthM;

            Vector3 from = mount.position;
            Vector3 to = from + mount.forward * lenM;

            _beam.SetPosition(0, from);
            _beam.SetPosition(1, to);
            _beam.material = RobotVisuals.GetMaterial(hit
                ? new Color32(0xE8, 0x38, 0x28, 255)     // 当たり: 赤
                : new Color32(0x8D, 0x97, 0xA5, 255));   // 空振り: 灰

            if (!hit) return;

            hits.Add(to);
            var mark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            mark.name = "hit";
            Destroy(mark.GetComponent<Collider>());      // 自分の点をソナーが拾わないように
            mark.transform.SetParent(_marks, false);
            mark.transform.position = to;
            mark.transform.localScale = Vector3.one * MarkSizeM;
            mark.GetComponent<MeshRenderer>().sharedMaterial =
                RobotVisuals.GetMaterial(new Color32(0x03, 0x6E, 0xB8, 255));
        }
    }
}
