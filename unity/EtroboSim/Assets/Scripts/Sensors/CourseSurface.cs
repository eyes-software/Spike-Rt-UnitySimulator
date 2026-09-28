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

        /// <summary>
        /// 側面に巻かれた帯（ボトルのラベル）。高さで色が変わる物のために持つ。
        /// センサがどの高さを見たかで返す色が変わる。前を向いたカラーセンサで
        /// ボトルの色を読む課題があるので、胴とラベルを区別できないと困る。
        /// 高さはプロップの原点（＝底）からの mm。
        /// </summary>
        public bool HasLabel;
        public Color32 LabelColor;
        public float LabelBottomMm;
        public float LabelTopMm;

        /// <summary>当たった点の色。worldY はワールドの高さ [m]。</summary>
        public Color32 ColorAt(float worldY)
        {
            if (!HasLabel) return SurfaceColor;

            float mm = (worldY - transform.position.y) * CourseSpace.MToMm;
            return (mm >= LabelBottomMm && mm <= LabelTopMm) ? LabelColor : SurfaceColor;
        }
    }
}
