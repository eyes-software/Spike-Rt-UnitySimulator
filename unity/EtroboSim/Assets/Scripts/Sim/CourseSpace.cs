using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// コース座標系 (mm, X=右, Y=奥, 方位は +X から反時計回り) と
    /// Unity のワールド座標系 (m, 左手系, Y=上) の変換。
    ///
    /// Unity の Y 回転は上から見て時計回りなので、コース方位 θ との関係は
    /// yaw = 90 - θ になる。ここを 1 か所に閉じ込めて他では素で書かない。
    /// </summary>
    public static class CourseSpace
    {
        public const float MmToM = 0.001f;
        public const float MToMm = 1000f;

        public static Vector3 ToWorld(Vector2 mm, float heightM = 0f)
            => new Vector3(mm.x * MmToM, heightM, mm.y * MmToM);

        public static Vector2 ToCourseMm(Vector3 world)
            => new Vector2(world.x * MToMm, world.z * MToMm);

        /// <summary>コース方位 [deg] -> Unity の Y 軸オイラー角 [deg]。</summary>
        public static float HeadingToYaw(float headingDeg) => 90f - headingDeg;

        /// <summary>Unity の Y 軸オイラー角 [deg] -> コース方位 [deg] (-180..180)。</summary>
        public static float YawToHeading(float yawDeg)
        {
            float h = 90f - yawDeg;
            h %= 360f;
            if (h > 180f) h -= 360f;
            if (h < -180f) h += 360f;
            return h;
        }

        public static Quaternion HeadingToRotation(float headingDeg)
            => Quaternion.Euler(0f, HeadingToYaw(headingDeg), 0f);

        /// <summary>ワールドの水平方向ベクトルをコース平面の 2D ベクトルへ。</summary>
        public static Vector2 FlattenToCourse(Vector3 world)
            => new Vector2(world.x, world.z);
    }
}
