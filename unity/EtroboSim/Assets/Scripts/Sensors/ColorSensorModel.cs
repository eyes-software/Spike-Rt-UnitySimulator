using UnityEngine;
using Etrobo.Sim.Net;

namespace Etrobo.Sim
{
    /// <summary>
    /// SPIKE カラーセンサのモデル。
    ///
    /// 実機のセンサは点ではなく直径 10mm 前後の面を見ているので、
    /// 1 点サンプルだとライン端でのしきい値挙動が実機とずれる。
    /// ここではスポット内を複数点サンプルして面積平均を取る。
    /// これがないと PID のゲインが実機と合わない。
    /// </summary>
    public sealed class ColorSensorModel
    {
        readonly RobotSpec _spec;
        readonly Transform _mount;
        readonly int _rayMask;
        readonly Vector2[] _spotOffsets;

        float _filteredReflection;
        Color _filteredColor = Color.white;
        bool _initialized;

        public int Reflection { get; private set; }
        public int Ambient { get; private set; }
        public Color32 RawColor { get; private set; }
        public ushort R { get; private set; }
        public ushort G { get; private set; }
        public ushort B { get; private set; }
        public ushort HueDeg { get; private set; }
        public byte Saturation { get; private set; }
        public byte Value { get; private set; }
        public byte ColorId { get; private set; }

        public ColorSensorModel(RobotSpec spec, Transform mount, int rayMask)
        {
            _spec = spec;
            _mount = mount;
            _rayMask = rayMask;
            _spotOffsets = BuildSpotPattern(9);
        }

        /// <summary>
        /// 円形スポットを面積が等しい同心リングに割り、各リングを均等サンプルする。
        /// 単純な正方格子より少ない点数で面積平均に近づく。
        /// </summary>
        static Vector2[] BuildSpotPattern(int count)
        {
            var pts = new Vector2[count];
            pts[0] = Vector2.zero;
            const float golden = 2.39996323f; // 黄金角 [rad]
            for (int i = 1; i < count; i++)
            {
                float r = Mathf.Sqrt(i / (float)(count - 1));
                float a = i * golden;
                pts[i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            }
            return pts;
        }

        public void Reset()
        {
            _initialized = false;
            _filteredReflection = 0f;
            _filteredColor = Color.white;
        }

        public void Step(float dt)
        {
            Color sum = Color.black;
            int hits = 0;

            float spotRadiusM = _spec.ColorSensorSpotMm * 0.5f * CourseSpace.MmToM;
            // 取り付け高さより十分下まで見る。段差に乗ってもシート面を拾えるように余裕を持たせる
            float rayLength = (_spec.ColorSensorHeightMm + 60f) * CourseSpace.MmToM;

            Vector3 down = -_mount.up;
            Vector3 right = _mount.right;
            Vector3 fwd = _mount.forward;

            foreach (var off in _spotOffsets)
            {
                Vector3 origin = _mount.position
                                 + right * (off.x * spotRadiusM)
                                 + fwd * (off.y * spotRadiusM);

                if (!Physics.Raycast(origin, down, out RaycastHit hit, rayLength,
                                     _rayMask, QueryTriggerInteraction.Ignore))
                    continue;

                sum += SampleAt(hit);
                hits++;
            }

            // どこにも当たらない = センサが台から外れている
            Color instant = hits > 0 ? sum / hits : Color.black;

            if (!_initialized)
            {
                _filteredColor = instant;
                _initialized = true;
            }
            else
            {
                // 1 次遅れ。時定数 0 のときは即時反映
                float tau = Mathf.Max(_spec.ColorSensorTimeConstant, 1e-6f);
                float alpha = 1f - Mathf.Exp(-dt / tau);
                _filteredColor = Color.Lerp(_filteredColor, instant, alpha);
            }

            Publish(_filteredColor, dt);
        }

        Color SampleAt(in RaycastHit hit)
        {
            var surface = hit.collider.GetComponentInParent<CourseSurface>();
            if (surface != null && surface.Field != null)
            {
                Vector2 mm = CourseSpace.ToCourseMm(hit.point);
                return surface.Field.SampleColor(mm);
            }

            var prop = hit.collider.GetComponentInParent<PropSurface>();
            if (prop != null) return prop.SurfaceColor;

            // 未知の物体は中間グレー扱い
            return new Color(0.5f, 0.5f, 0.5f, 1f);
        }

        void Publish(Color c, float dt)
        {
            RawColor = c;

            // sRGB の相対輝度。カラーセンサの反射光量は概ねこれに比例する。
            float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

            float reflection = Mathf.Lerp(_spec.ReflectionBlack, _spec.ReflectionWhite, lum);
            reflection += Gaussian() * _spec.ColorSensorNoise;

            // 反射光量だけは実機に合わせてさらにわずかに鈍らせる
            float tau = Mathf.Max(_spec.ColorSensorTimeConstant, 1e-6f);
            float alpha = 1f - Mathf.Exp(-dt / tau);
            _filteredReflection = Mathf.Lerp(_filteredReflection, reflection, alpha);

            Reflection = Mathf.Clamp(Mathf.RoundToInt(_filteredReflection), 0, 100);
            Ambient = Mathf.Clamp(Mathf.RoundToInt(lum * 12f), 0, 100);

            // SPIKE の生 RGB は 0..1024 スケール
            R = ToRaw(c.r);
            G = ToRaw(c.g);
            B = ToRaw(c.b);

            Color.RGBToHSV(c, out float h, out float s, out float v);
            HueDeg = (ushort)Mathf.RoundToInt(h * 360f) ;
            if (HueDeg >= 360) HueDeg = 0;
            Saturation = (byte)Mathf.Clamp(Mathf.RoundToInt(s * 100f), 0, 100);
            Value = (byte)Mathf.Clamp(Mathf.RoundToInt(v * 100f), 0, 100);

            ColorId = Classify(HueDeg, Saturation, Value);
        }

        static ushort ToRaw(float channel)
            => (ushort)Mathf.Clamp(Mathf.RoundToInt(channel * 1024f), 0, 1024);

        /// <summary>
        /// pup_color_sensor_color() 相当の離散化。
        /// Pybricks の分類と同じく HSV のしきい値で判定する。
        /// </summary>
        static byte Classify(int hue, int sat, int val)
        {
            if (val < 12) return Etsim.ColorBlack;
            if (sat < 20) return val > 60 ? Etsim.ColorWhite : Etsim.ColorGray;

            if (hue < 20 || hue >= 340) return Etsim.ColorRed;
            if (hue < 45) return Etsim.ColorOrange;
            if (hue < 75) return Etsim.ColorYellow;
            if (hue < 160) return Etsim.ColorGreen;
            if (hue < 200) return Etsim.ColorCyan;
            if (hue < 260) return Etsim.ColorBlue;
            if (hue < 300) return Etsim.ColorViolet;
            return Etsim.ColorMagenta;
        }

        static float Gaussian()
        {
            // Box-Muller。UnityEngine.Random は一様分布しか返さない
            float u1 = Mathf.Max(1e-6f, UnityEngine.Random.value);
            float u2 = UnityEngine.Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }
    }
}
