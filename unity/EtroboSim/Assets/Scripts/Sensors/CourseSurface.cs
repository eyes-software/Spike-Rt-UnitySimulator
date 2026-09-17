using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// コースシート面につけるマーカ。カラーセンサはこれを見つけたときだけ
    /// CourseField を解析的にサンプリングする（テクスチャ解像度に縛られない）。
    /// </summary>
    public sealed class CourseSurface : MonoBehaviour
    {
        public CourseField Field;
    }

    /// <summary>
    /// 難所オブジェクトなど、単色で塗られた立体につけるマーカ。
    /// カラーセンサがこれを読んだときは、この色をそのまま返す。
    /// </summary>
    public sealed class PropSurface : MonoBehaviour
    {
        public Color32 SurfaceColor = new Color32(180, 180, 180, 255);
    }
}
