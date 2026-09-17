using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// 視点操作。
    ///
    /// 走行の確認では「真上から全体を見る」「斜め上から車体の姿勢を見る」
    /// 「センサの高さまで降りて路面との関係を見る」を行き来したいので、
    /// 見下ろし角（ピッチ）と回り込み（ヨー）を自由に変えられるようにしてある。
    ///
    ///   ORBIT   注視点のまわりを回る。既定。注視点は走行体かコース中心
    ///   CHASE   走行体を追う。相対的な角度と距離は自分で決められる
    ///   ONBOARD 走行体の視点
    ///   FREE    自由飛行。WASD で移動、右ドラッグで向きを変える
    /// </summary>
    public sealed class CameraRig : MonoBehaviour
    {
        public enum Mode { Orbit, Chase, Onboard, Free }

        public Transform Target;
        public Vector3 CourseCenter;
        public Vector2 CourseSizeM = new Vector2(4f, 2f);

        public Mode CurrentMode = Mode.Orbit;

        [Header("操作感")]
        [Tooltip("マウス 1px あたりの回転量 [deg]")]
        public float LookSensitivity = 0.25f;
        [Tooltip("ホイール 1 目盛りあたりのズーム率")]
        public float ZoomStep = 0.12f;
        [Tooltip("自由飛行の移動速度 [m/s]")]
        public float FlySpeed = 1.5f;
        [Tooltip("視点追従の滑らかさ。0 で即時")]
        public float Smoothing = 0.08f;

        /// <summary>注視点。ORBIT / CHASE の回転中心。</summary>
        public Vector3 Focus { get; private set; }

        float _yaw;
        float _pitch = 55f;
        float _distance = 2f;

        // CHASE は走行体の向きに追従する角度、ORBIT は絶対角度を持つ
        float _chaseYawOffset;
        float _chasePitch = 25f;
        float _chaseDistance = 1.0f;

        Camera _camera;
        bool _initialized;

        /// <summary>HUD に出す操作説明。モードごとに変わる。</summary>
        public string HelpText
        {
            get
            {
                switch (CurrentMode)
                {
                    case Mode.Orbit:
                        return "Tab:視点  右/中ドラッグ:回転  Shift+ドラッグ or 中ドラッグ:平行移動  ホイール:ズーム  F:走行体に注目  G:全体";
                    case Mode.Chase:
                        return "Tab:視点  右ドラッグ:回り込み  ホイール:距離";
                    case Mode.Onboard:
                        return "Tab:視点  右ドラッグ:首振り";
                    default:
                        return "Tab:視点  右ドラッグ:向き  WASD:移動  Q/E:上下  Shift:高速";
                }
            }
        }

        void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        void Start()
        {
            FrameCourse();
            _initialized = true;
        }

        /// <summary>コース全体が入る位置へ引く。</summary>
        public void FrameCourse()
        {
            Focus = CourseCenter;
            _yaw = 0f;
            _pitch = 89f;   // ほぼ真上。わずかに傾けて「上から」だと分かるようにする
            _distance = DistanceToFitCourse();
        }

        /// <summary>走行体に寄る。</summary>
        public void FrameRobot()
        {
            if (Target == null) return;
            Focus = Target.position;
            _pitch = 45f;
            _distance = 0.9f;
        }

        /// <summary>指定した場所に寄る。確認用の画面保存で使う。</summary>
        public void FrameAt(Vector2 courseMm, float distanceM, float pitchDeg, float yawDeg = 0f)
        {
            Focus = CourseSpace.ToWorld(courseMm, 0f);
            _yaw = yawDeg;
            _pitch = Mathf.Clamp(pitchDeg, 1f, 89f);
            _distance = Mathf.Max(0.05f, distanceM);
        }

        /// <summary>コース全体が画角に収まる距離。</summary>
        float DistanceToFitCourse()
        {
            if (_camera == null) _camera = GetComponent<Camera>();
            float halfV = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfH = halfV * _camera.aspect;

            float needV = CourseSizeM.y * 0.5f / halfV;
            float needH = CourseSizeM.x * 0.5f / halfH;
            return Mathf.Max(needV, needH) * 1.08f;
        }

        /// <summary>
        /// パネルが占めている矩形（画面座標、左上原点）。SimHud が入れる。
        /// ここにカーソルがある間はカメラを動かさない。パネルを操作したいのに
        /// 視点が回ったりズームしたりするのを避けるため。
        /// </summary>
        public Rect PanelRect;

        bool PointerOverPanel()
        {
            if (PanelRect.width <= 0f) return false;

            // Input.mousePosition は左下原点。PanelRect は GUI と同じ左上原点。
            var p = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return PanelRect.Contains(p);
        }

        void LateUpdate()
        {
            if (!_initialized) return;

            HandleModeSwitch();

            if (PointerOverPanel()) return;

            switch (CurrentMode)
            {
                case Mode.Orbit: UpdateOrbit(); break;
                case Mode.Chase: UpdateChase(); break;
                case Mode.Onboard: UpdateOnboard(); break;
                case Mode.Free: UpdateFree(); break;
            }
        }

        void HandleModeSwitch()
        {
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                CurrentMode = (Mode)(((int)CurrentMode + 1) % 4);

                // モードを移るとき、今見ている場所を引き継いで飛ばないようにする
                if (CurrentMode == Mode.Orbit)
                {
                    Focus = Target != null ? Target.position : CourseCenter;
                    _yaw = transform.eulerAngles.y;
                    _pitch = Mathf.Clamp(NormalizePitch(transform.eulerAngles.x), 1f, 89f);
                    _distance = Mathf.Max(0.2f, Vector3.Distance(transform.position, Focus));
                }
            }

            if (Input.GetKeyDown(KeyCode.F)) FrameRobot();
            if (Input.GetKeyDown(KeyCode.G)) FrameCourse();
        }

        static float NormalizePitch(float eulerX)
        {
            return eulerX > 180f ? eulerX - 360f : eulerX;
        }

        /// <summary>回転入力。右ボタンか中ボタンのドラッグ。</summary>
        bool ReadLook(out float dx, out float dy)
        {
            dx = dy = 0f;
            bool dragging = Input.GetMouseButton(1) || Input.GetMouseButton(2);
            if (!dragging) return false;

            dx = Input.GetAxisRaw("Mouse X") * LookSensitivity * 12f;
            dy = Input.GetAxisRaw("Mouse Y") * LookSensitivity * 12f;
            return true;
        }

        float ReadZoom() => Input.mouseScrollDelta.y;

        void UpdateOrbit()
        {
            // Shift を押しながら、または中ボタンで平行移動
            bool panning = Input.GetMouseButton(2)
                           || (Input.GetMouseButton(1) && (Input.GetKey(KeyCode.LeftShift)
                                                           || Input.GetKey(KeyCode.RightShift)));

            if (ReadLook(out float dx, out float dy))
            {
                if (panning)
                {
                    // 画面上の移動量を、距離に比例させてワールドの平行移動に直す
                    float scale = _distance * 0.0015f;
                    Focus -= transform.right * dx * scale + transform.up * dy * scale;
                }
                else
                {
                    _yaw += dx;
                    // ピッチは真上 (90) と水平 (1) の間に収める。
                    // 0 を跨ぐと上下が反転して操作不能になるため。
                    _pitch = Mathf.Clamp(_pitch + dy, 1f, 89f);
                }
            }

            float zoom = ReadZoom();
            if (Mathf.Abs(zoom) > 0.01f)
                _distance = Mathf.Clamp(_distance * (1f - zoom * ZoomStep), 0.15f, 80f);

            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 wanted = Focus + rotation * new Vector3(0f, 0f, -_distance);

            Apply(wanted, rotation);
        }

        void UpdateChase()
        {
            if (Target == null) { UpdateOrbit(); return; }

            if (ReadLook(out float dx, out float dy))
            {
                _chaseYawOffset += dx;
                _chasePitch = Mathf.Clamp(_chasePitch + dy, -5f, 85f);
            }

            float zoom = ReadZoom();
            if (Mathf.Abs(zoom) > 0.01f)
                _chaseDistance = Mathf.Clamp(_chaseDistance * (1f - zoom * ZoomStep), 0.2f, 20f);

            Focus = Target.position + Vector3.up * 0.05f;

            var rotation = Quaternion.Euler(_chasePitch, Target.eulerAngles.y + _chaseYawOffset, 0f);
            Vector3 wanted = Focus + rotation * new Vector3(0f, 0f, -_chaseDistance);

            Apply(wanted, rotation);
        }

        void UpdateOnboard()
        {
            if (Target == null) { UpdateOrbit(); return; }

            if (ReadLook(out float dx, out float dy))
            {
                _chaseYawOffset = Mathf.Clamp(_chaseYawOffset + dx, -120f, 120f);
                _chasePitch = Mathf.Clamp(_chasePitch + dy, -60f, 60f);
            }

            // 走行体の少し後ろ上から前を見る
            Vector3 position = Target.TransformPoint(new Vector3(0f, 0.11f, 0.02f));
            var rotation = Quaternion.Euler(_chasePitch * 0.5f + 18f,
                                            Target.eulerAngles.y + _chaseYawOffset, 0f);

            // 車載視点は揺れをそのまま見たいので補間しない
            transform.SetPositionAndRotation(position, rotation);
            Focus = position + rotation * Vector3.forward;
        }

        void UpdateFree()
        {
            if (ReadLook(out float dx, out float dy))
            {
                _yaw += dx;
                _pitch = Mathf.Clamp(_pitch - dy, -89f, 89f);
            }

            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            float speed = FlySpeed * (Input.GetKey(KeyCode.LeftShift) ? 4f : 1f);
            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) move += rotation * Vector3.forward;
            if (Input.GetKey(KeyCode.S)) move -= rotation * Vector3.forward;
            if (Input.GetKey(KeyCode.D)) move += rotation * Vector3.right;
            if (Input.GetKey(KeyCode.A)) move -= rotation * Vector3.right;
            if (Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;

            Vector3 position = transform.position + move * speed * Time.unscaledDeltaTime;

            transform.SetPositionAndRotation(position, rotation);
            Focus = position + rotation * Vector3.forward;
        }

        void Apply(Vector3 wantedPosition, Quaternion wantedRotation)
        {
            if (Smoothing <= 0f)
            {
                transform.SetPositionAndRotation(wantedPosition, wantedRotation);
                return;
            }

            // Time.deltaTime ではなく unscaled を使う。ロックステップで
            // timeScale が変わってもカメラ操作の感触を一定に保つため。
            float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Smoothing);
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, wantedPosition, t),
                Quaternion.Slerp(transform.rotation, wantedRotation, t));
        }
    }
}
