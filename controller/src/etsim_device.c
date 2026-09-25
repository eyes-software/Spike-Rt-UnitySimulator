/*
 * etsim_device.c : SPIKE-RT デバイス API のシミュレータ実装。
 *
 * ここが「実機コードをそのままビルドするための唯一の差し替え点」になる。
 * spike-rt 側の API が変わったときに直すのはこのファイルだけで済むよう、
 * 通信やタイミングの都合は etsim_runtime.c に閉じ込めてある。
 *
 * ポート割り当ては既定値を環境変数で上書きできる。
 *   ETSIM_PORT_LEFT / ETSIM_PORT_RIGHT / ETSIM_PORT_ARM
 *   ETSIM_PORT_COLOR / ETSIM_PORT_ULTRASONIC / ETSIM_PORT_FORCE
 * 値はポート文字 1 文字（A-F）。
 */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "etsim_ports.h"
#include "etsim/etsim_device.h"
#include "etsim/etsim_runtime.h"

#include <pbio/button.h>
#include <pbio/color.h>
#include <pbio/error.h>
#include <pbio/port.h>
#include <spike/pup_device.h>
#include <spike/pup/colorsensor.h>
#include <spike/pup/forcesensor.h>
#include <spike/pup/motor.h>
#include <spike/pup/ultrasonicsensor.h>
#include <spike/hub/battery.h>
#include <spike/hub/button.h>
#include <spike/hub/bluetooth.h>
#include <spike/hub/display.h>
#include <spike/hub/imu.h>
#include <spike/hub/speaker.h>
#include <spike/hub/light.h>
#include <spike/hub/system.h>

/* ---------------------------------------------------------------- */
/* ポート割り当て                                                     */
/* ---------------------------------------------------------------- */

typedef enum {
    DEV_NONE = 0,
    DEV_MOTOR_LEFT,
    DEV_MOTOR_RIGHT,
    DEV_MOTOR_ARM,
    DEV_COLOR,
    DEV_ULTRASONIC,
    DEV_FORCE
} device_kind_t;

struct _pup_motor_t {
    int      motor_index;    /* ETSIM_MOTOR_* */
    int      sign;           /* 正転方向。COUNTERCLOCKWISE で -1 */
    int      configured;
    int      duty_limit;     /* 1..100 */
    /* pup_motor_set_speed 用の閉ループ状態 */
    int      speed_mode;
    int32_t  target_speed;
    float    speed_integral;
};

struct _pup_device_t {
    device_kind_t kind;
    pbio_port_id_t port;
};

static struct _pup_motor_t  g_motors[ETSIM_MOTOR_NUM];
static struct _pup_device_t g_devices[3]; /* color / ultrasonic / force */

static device_kind_t g_port_map[6]; /* index 0..5 = port A..F */

static int port_index(pbio_port_id_t port)
{
    if (port < PBIO_PORT_ID_A || port > PBIO_PORT_ID_F) return -1;
    return (int)port - (int)PBIO_PORT_ID_A;
}

static void assign_port(const char *env_name, char default_port, device_kind_t kind)
{
    const char *v = getenv(env_name);
    char c = (v != NULL && v[0] != '\0') ? v[0] : default_port;

    if (c >= 'a' && c <= 'f') c = (char)(c - 'a' + 'A');
    if (c < 'A' || c > 'F') {
        fprintf(stderr, "[etsim] %s の値 '%c' は A-F ではありません。既定 '%c' を使います\n",
                env_name, c, default_port);
        c = default_port;
    }
    g_port_map[c - 'A'] = kind;
}

static void ensure_port_map(void)
{
    static int done = 0;
    if (done) return;
    done = 1;

    memset(g_port_map, 0, sizeof(g_port_map));

    /*
     * 既定は走行体規定書 M6.2「４ ケーブルの配線」の配線例に合わせてある。
     *   A = 右モータ / B = 左モータ / C = アームモータ
     *   D = フォースセンサ / E = カラーセンサ / F = 超音波センサ
     * 規定書にも「この配線方法は一例です」とあるので、違う場合は環境変数で変える。
     */
    assign_port("ETSIM_PORT_RIGHT",      ETSIM_DEFAULT_PORT_RIGHT,      DEV_MOTOR_RIGHT);
    assign_port("ETSIM_PORT_LEFT",       ETSIM_DEFAULT_PORT_LEFT,       DEV_MOTOR_LEFT);
    assign_port("ETSIM_PORT_ARM",        ETSIM_DEFAULT_PORT_ARM,        DEV_MOTOR_ARM);
    assign_port("ETSIM_PORT_FORCE",      ETSIM_DEFAULT_PORT_FORCE,      DEV_FORCE);
    assign_port("ETSIM_PORT_COLOR",      ETSIM_DEFAULT_PORT_COLOR,      DEV_COLOR);
    assign_port("ETSIM_PORT_ULTRASONIC", ETSIM_DEFAULT_PORT_ULTRASONIC, DEV_ULTRASONIC);

    for (int i = 0; i < ETSIM_MOTOR_NUM; i++) {
        g_motors[i].motor_index = i;
        g_motors[i].sign = 1;
        g_motors[i].duty_limit = 100;
    }
}

void etsim_device_dump_port_map(void)
{
    static const char *names[] = {
        "(なし)", "左モータ", "右モータ", "アームモータ",
        "カラーセンサ", "超音波センサ", "フォースセンサ"
    };
    ensure_port_map();
    printf("[etsim] ポート割り当て:\n");
    for (int i = 0; i < 6; i++) {
        if (g_port_map[i] == DEV_NONE) continue;
        printf("          %c : %s\n", 'A' + i, names[g_port_map[i]]);
    }
}

/* ---------------------------------------------------------------- */
/* pbio の補助関数                                                    */
/* ---------------------------------------------------------------- */

const char *pbio_error_str(pbio_error_t err)
{
    switch (err) {
        case PBIO_SUCCESS:               return "PBIO_SUCCESS";
        case PBIO_ERROR_FAILED:          return "PBIO_ERROR_FAILED";
        case PBIO_ERROR_INVALID_ARG:     return "PBIO_ERROR_INVALID_ARG";
        case PBIO_ERROR_IO:              return "PBIO_ERROR_IO";
        case PBIO_ERROR_BUSY:            return "PBIO_ERROR_BUSY";
        case PBIO_ERROR_NO_DEV:          return "PBIO_ERROR_NO_DEV";
        case PBIO_ERROR_NOT_IMPLEMENTED: return "PBIO_ERROR_NOT_IMPLEMENTED";
        case PBIO_ERROR_NOT_SUPPORTED:   return "PBIO_ERROR_NOT_SUPPORTED";
        case PBIO_ERROR_AGAIN:           return "PBIO_ERROR_AGAIN";
        case PBIO_ERROR_INVALID_OP:      return "PBIO_ERROR_INVALID_OP";
        case PBIO_ERROR_TIMEDOUT:        return "PBIO_ERROR_TIMEDOUT";
        case PBIO_ERROR_CANCELED:        return "PBIO_ERROR_CANCELED";
        default:                         return "PBIO_ERROR_UNKNOWN";
    }
}

void pbio_color_rgb_to_hsv(const pbio_color_rgb_t *rgb, pbio_color_hsv_t *hsv)
{
    float r = rgb->r / 255.0f, g = rgb->g / 255.0f, b = rgb->b / 255.0f;
    float max = r > g ? (r > b ? r : b) : (g > b ? g : b);
    float min = r < g ? (r < b ? r : b) : (g < b ? g : b);
    float delta = max - min;

    float h = 0.0f;
    if (delta > 1e-6f) {
        if (max == r)      h = 60.0f * fmodf((g - b) / delta, 6.0f);
        else if (max == g) h = 60.0f * (((b - r) / delta) + 2.0f);
        else               h = 60.0f * (((r - g) / delta) + 4.0f);
    }
    if (h < 0.0f) h += 360.0f;

    hsv->h = (uint16_t)(h + 0.5f) % 360;
    hsv->s = (uint8_t)((max > 1e-6f ? (delta / max) : 0.0f) * 100.0f + 0.5f);
    hsv->v = (int8_t)(max * 100.0f + 0.5f);
}

void pbio_color_hsv_to_rgb(const pbio_color_hsv_t *hsv, pbio_color_rgb_t *rgb)
{
    float h = (float)hsv->h;
    float s = hsv->s / 100.0f;
    float v = (hsv->v < 0 ? 0 : hsv->v) / 100.0f;

    float c = v * s;
    float x = c * (1.0f - fabsf(fmodf(h / 60.0f, 2.0f) - 1.0f));
    float m = v - c;

    float r = 0, g = 0, b = 0;
    if (h < 60)       { r = c; g = x; }
    else if (h < 120) { r = x; g = c; }
    else if (h < 180) { g = c; b = x; }
    else if (h < 240) { g = x; b = c; }
    else if (h < 300) { r = x; b = c; }
    else              { r = c; b = x; }

    rgb->r = (uint8_t)((r + m) * 255.0f + 0.5f);
    rgb->g = (uint8_t)((g + m) * 255.0f + 0.5f);
    rgb->b = (uint8_t)((b + m) * 255.0f + 0.5f);
}

/* ---------------------------------------------------------------- */
/* モータ                                                            */
/* ---------------------------------------------------------------- */

static int clamp_int(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

pup_motor_t *pup_motor_get_device(pbio_port_id_t port)
{
    ensure_port_map();

    int idx = port_index(port);
    if (idx < 0) return NULL;

    switch (g_port_map[idx]) {
        case DEV_MOTOR_LEFT:  return &g_motors[ETSIM_MOTOR_LEFT];
        case DEV_MOTOR_RIGHT: return &g_motors[ETSIM_MOTOR_RIGHT];
        case DEV_MOTOR_ARM:   return &g_motors[ETSIM_MOTOR_ARM];
        default:              return NULL;
    }
}

pbio_error_t pup_motor_setup(pup_motor_t *motor, pup_direction_t positive_direction, bool reset_count)
{
    if (motor == NULL) return PBIO_ERROR_NO_DEV;

    motor->sign = (positive_direction == PUP_DIRECTION_COUNTERCLOCKWISE) ? -1 : 1;
    motor->configured = 1;
    motor->speed_mode = 0;
    motor->speed_integral = 0.0f;

    if (reset_count) etsim_reset_motor_count(motor->motor_index);
    return PBIO_SUCCESS;
}

pbio_error_t pup_motor_reset_count(pup_motor_t *motor)
{
    if (motor == NULL) return PBIO_ERROR_NO_DEV;
    etsim_reset_motor_count(motor->motor_index);
    return PBIO_SUCCESS;
}

int32_t pup_motor_get_count(pup_motor_t *motor)
{
    if (motor == NULL) return 0;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return motor->sign * s.motor_count[motor->motor_index];
}

int32_t pup_motor_get_speed(pup_motor_t *motor)
{
    if (motor == NULL) return 0;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return motor->sign * s.motor_speed[motor->motor_index];
}

int32_t pup_motor_get_power(pup_motor_t *motor)
{
    if (motor == NULL) return 0;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return motor->sign * s.motor_power[motor->motor_index];
}

pbio_error_t pup_motor_set_power(pup_motor_t *motor, int power)
{
    if (motor == NULL) return PBIO_ERROR_NO_DEV;

    motor->speed_mode = 0;
    int limited = clamp_int(power, -motor->duty_limit, motor->duty_limit);
    etsim_set_motor(motor->motor_index, motor->sign * limited, ETSIM_MOTOR_MODE_POWER);
    return PBIO_SUCCESS;
}

pbio_error_t pup_motor_set_speed(pup_motor_t *motor, int speed)
{
    if (motor == NULL) return PBIO_ERROR_NO_DEV;

    /* 実機は内部で速度の閉ループを回している。ここでも同じように
     * 目標速度を保持し、etsim_device_update() で出力へ落とす。 */
    motor->speed_mode = 1;
    motor->target_speed = speed;
    return PBIO_SUCCESS;
}

pbio_error_t pup_motor_stop(pup_motor_t *motor)
{
    if (motor == NULL) return PBIO_ERROR_NO_DEV;
    motor->speed_mode = 0;
    motor->speed_integral = 0.0f;
    etsim_set_motor(motor->motor_index, 0, ETSIM_MOTOR_MODE_COAST);
    return PBIO_SUCCESS;
}

pbio_error_t pup_motor_brake(pup_motor_t *motor)
{
    if (motor == NULL) return PBIO_ERROR_NO_DEV;
    motor->speed_mode = 0;
    motor->speed_integral = 0.0f;
    etsim_set_motor(motor->motor_index, 0, ETSIM_MOTOR_MODE_BRAKE);
    return PBIO_SUCCESS;
}

pbio_error_t pup_motor_hold(pup_motor_t *motor)
{
    if (motor == NULL) return PBIO_ERROR_NO_DEV;
    motor->speed_mode = 0;
    motor->speed_integral = 0.0f;
    etsim_set_motor(motor->motor_index, 0, ETSIM_MOTOR_MODE_HOLD);
    return PBIO_SUCCESS;
}

bool pup_motor_is_stalled(pup_motor_t *motor)
{
    if (motor == NULL) return false;

    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);

    int idx = motor->motor_index;
    /* 出力を出しているのにほとんど回っていなければストール */
    return (abs(s.motor_power[idx]) > 40) && (abs(s.motor_speed[idx]) < 20);
}

int32_t pup_motor_set_duty_limit(pup_motor_t *motor, int duty_limit)
{
    if (motor == NULL) return 100;
    int old = motor->duty_limit;
    motor->duty_limit = clamp_int(duty_limit, 1, 100);
    return old;
}

void pup_motor_restore_duty_limit(pup_motor_t *motor, int old_value)
{
    if (motor == NULL) return;
    motor->duty_limit = clamp_int(old_value, 1, 100);
}

void etsim_device_update(uint32_t elapsed_us)
{
    etsim_sensor_packet_t s;
    int any_speed_mode = 0;

    for (int i = 0; i < ETSIM_MOTOR_NUM; i++)
        if (g_motors[i].speed_mode) any_speed_mode = 1;

    if (!any_speed_mode) return;

    etsim_get_sensors(&s);
    float dt = (float)elapsed_us * 1e-6f;
    if (dt <= 0.0f) return;

    for (int i = 0; i < ETSIM_MOTOR_NUM; i++) {
        struct _pup_motor_t *m = &g_motors[i];
        if (!m->speed_mode) continue;

        float measured = (float)(m->sign * s.motor_speed[i]);
        float error = (float)m->target_speed - measured;

        /* 目標 1000 deg/s で出力 100% 相当になるゲイン。
         * 積分項は出力飽和時に巻き上がらないよう制限する。 */
        const float kp = 0.10f;
        const float ki = 0.30f;
        const float integral_limit = 100.0f;

        m->speed_integral += error * dt;
        if (m->speed_integral > integral_limit)  m->speed_integral = integral_limit;
        if (m->speed_integral < -integral_limit) m->speed_integral = -integral_limit;

        float output = kp * error + ki * m->speed_integral;
        int power = clamp_int((int)lrintf(output), -m->duty_limit, m->duty_limit);

        etsim_set_motor(i, m->sign * power, ETSIM_MOTOR_MODE_POWER);
    }
}

/* ---------------------------------------------------------------- */
/* センサ共通                                                         */
/* ---------------------------------------------------------------- */

static pup_device_t *get_sensor_device(pbio_port_id_t port, device_kind_t expected, int slot)
{
    ensure_port_map();

    int idx = port_index(port);
    if (idx < 0 || g_port_map[idx] != expected) return NULL;

    g_devices[slot].kind = expected;
    g_devices[slot].port = port;
    return &g_devices[slot];
}

/* ---------------------------------------------------------------- */
/* カラーセンサ                                                       */
/* ---------------------------------------------------------------- */

pup_device_t *pup_color_sensor_get_device(pbio_port_id_t port)
{
    return get_sensor_device(port, DEV_COLOR, 0);
}

int32_t pup_color_sensor_reflection(pup_device_t *pdev)
{
    if (pdev == NULL) return 0;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return s.reflection;
}

int32_t pup_color_sensor_ambient(pup_device_t *pdev)
{
    if (pdev == NULL) return 0;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return s.ambient;
}

pup_color_rgb_t pup_color_sensor_rgb(pup_device_t *pdev)
{
    pup_color_rgb_t rgb = { 0, 0, 0 };
    if (pdev == NULL) return rgb;

    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    rgb.r = s.rgb[0];
    rgb.g = s.rgb[1];
    rgb.b = s.rgb[2];
    return rgb;
}

pup_color_hsv_t pup_color_sensor_hsv(pup_device_t *pdev, bool surface)
{
    pup_color_hsv_t hsv = { 0, 0, 0 };
    if (pdev == NULL) return hsv;

    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);

    hsv.h = s.hsv_h;
    hsv.s = s.hsv_s;
    /* surface = false は「自ら光る対象を測る」モード。
     * シミュレータには光源が無いので、反射光を切った分だけ暗く返す。 */
    hsv.v = surface ? (int8_t)s.hsv_v : (int8_t)(s.hsv_v / 4);
    return hsv;
}

pup_color_hsv_t pup_color_sensor_color(pup_device_t *pdev, bool surface)
{
    /* 実機は検出した離散色の代表 HSV を返す。同じ振る舞いに合わせる。 */
    pup_color_hsv_t hsv = { 0, 0, 0 };
    if (pdev == NULL) return hsv;

    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);

    uint16_t h = 0;
    uint8_t  sat = 100;
    int8_t   v = 100;

    switch (s.color_id) {
        case ETSIM_COLOR_BLACK:   h = 0;   sat = 0;   v = 0;   break;
        case ETSIM_COLOR_GRAY:    h = 0;   sat = 0;   v = 50;  break;
        case ETSIM_COLOR_WHITE:   h = 0;   sat = 0;   v = 100; break;
        case ETSIM_COLOR_RED:     h = 0;   break;
        case ETSIM_COLOR_ORANGE:  h = 30;  break;
        case ETSIM_COLOR_YELLOW:  h = 60;  break;
        case ETSIM_COLOR_GREEN:   h = 120; break;
        case ETSIM_COLOR_CYAN:    h = 180; break;
        case ETSIM_COLOR_BLUE:    h = 240; break;
        case ETSIM_COLOR_VIOLET:  h = 270; break;
        case ETSIM_COLOR_MAGENTA: h = 300; break;
        default:                  h = 180; sat = 0; v = 0; break; /* NONE */
    }

    hsv.h = h;
    hsv.s = sat;
    hsv.v = surface ? v : (int8_t)(v / 4);
    return hsv;
}

pbio_error_t pup_color_sensor_light_set(pup_device_t *pdev, int32_t bv1, int32_t bv2, int32_t bv3)
{
    (void)bv1; (void)bv2; (void)bv3;
    return pdev ? PBIO_SUCCESS : PBIO_ERROR_NO_DEV;
}

pbio_error_t pup_color_sensor_light_on(pup_device_t *pdev)
{
    return pdev ? PBIO_SUCCESS : PBIO_ERROR_NO_DEV;
}

pbio_error_t pup_color_sensor_light_off(pup_device_t *pdev)
{
    return pdev ? PBIO_SUCCESS : PBIO_ERROR_NO_DEV;
}

pup_color_hsv_t *pup_color_sensor_detectable_colors(int32_t size, pup_color_hsv_t *colors)
{
    /* 検出色テーブルの差し替えはシミュレータ側の分類に影響しない。
     * 受け取ったテーブルをそのまま返して、呼び出し側を壊さないでおく。 */
    (void)size;
    return colors;
}

/* ---------------------------------------------------------------- */
/* 超音波センサ                                                       */
/* ---------------------------------------------------------------- */

pup_device_t *pup_ultrasonic_sensor_get_device(pbio_port_id_t port)
{
    return get_sensor_device(port, DEV_ULTRASONIC, 1);
}

int32_t pup_ultrasonic_sensor_distance(pup_device_t *pdev)
{
    if (pdev == NULL) return -1;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return s.ultrasonic_mm;
}

bool pup_ultrasonic_sensor_presence(pup_device_t *pdev)
{
    if (pdev == NULL) return false;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return s.ultrasonic_presence != 0;
}

pbio_error_t pup_ultrasonic_sensor_light_set(pup_device_t *pdev, int32_t bv1, int32_t bv2, int32_t bv3, int32_t bv4)
{
    (void)bv1; (void)bv2; (void)bv3; (void)bv4;
    return pdev ? PBIO_SUCCESS : PBIO_ERROR_NO_DEV;
}

pbio_error_t pup_ultrasonic_sensor_light_on(pup_device_t *pdev)
{
    return pdev ? PBIO_SUCCESS : PBIO_ERROR_NO_DEV;
}

pbio_error_t pup_ultrasonic_sensor_light_off(pup_device_t *pdev)
{
    return pdev ? PBIO_SUCCESS : PBIO_ERROR_NO_DEV;
}

/* ---------------------------------------------------------------- */
/* フォースセンサ                                                     */
/* ---------------------------------------------------------------- */

pup_device_t *pup_force_sensor_get_device(pbio_port_id_t port)
{
    return get_sensor_device(port, DEV_FORCE, 2);
}

float pup_force_sensor_force(pup_device_t *pdev)
{
    if (pdev == NULL) return 0.0f;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return s.force_newton;
}

float pup_force_sensor_distance(pup_device_t *pdev)
{
    /* 押し込み量。シミュレータは力だけを持つので、
     * 実機の最大ストローク 8mm を線形に割り当てる。 */
    if (pdev == NULL) return 0.0f;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    float d = s.force_newton / 10.0f * 8.0f;
    return d > 8.0f ? 8.0f : d;
}

bool pup_force_sensor_pressed(pup_device_t *pdev, float force)
{
    if (pdev == NULL) return false;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return s.force_newton >= force;
}

bool pup_force_sensor_touched(pup_device_t *pdev)
{
    if (pdev == NULL) return false;
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return s.force_pressed != 0;
}

/* ---------------------------------------------------------------- */
/* ハブ                                                              */
/* ---------------------------------------------------------------- */

pbio_error_t hub_button_is_pressed(hub_button_t *pressed)
{
    if (pressed == NULL) return PBIO_ERROR_INVALID_ARG;

    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);

    int flags = 0;
    if (s.button_state & ETSIM_BUTTON_CENTER)    flags |= HUB_BUTTON_CENTER;
    if (s.button_state & ETSIM_BUTTON_LEFT)      flags |= HUB_BUTTON_LEFT;
    if (s.button_state & ETSIM_BUTTON_RIGHT)     flags |= HUB_BUTTON_RIGHT;
    if (s.button_state & ETSIM_BUTTON_BLUETOOTH) flags |= HUB_BUTTON_BT;

    *pressed = (hub_button_t)flags;
    return PBIO_SUCCESS;
}

pbio_error_t hub_imu_init(void) { return PBIO_SUCCESS; }

void hub_imu_get_acceleration(float accel[3])
{
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    accel[0] = s.accel_mss[0];
    accel[1] = s.accel_mss[1];
    accel[2] = s.accel_mss[2];
}

void hub_imu_get_angular_velocity(float angv[3])
{
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    angv[0] = s.gyro_dps[0];
    angv[1] = s.gyro_dps[1];
    angv[2] = s.gyro_dps[2];
}

float hub_imu_get_temperature(void) { return 25.0f; }

float hub_imu_get_heading(void)
{
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return s.yaw_deg;
}

void hub_imu_reset_heading(void) { etsim_request_imu_reset(); }

/* 旧称。以前のサンプルが使っている。 */
float etsim_imu_get_heading(void) { return hub_imu_get_heading(); }
void  etsim_imu_reset_heading(void) { hub_imu_reset_heading(); }

bool hub_imu_is_ready(void) { return true; }

/*
 * 静止判定。実機は加速度と角速度のばらつきで見ている。
 * ここは角速度の大きさだけで代用する。
 */
bool hub_imu_is_stationary(void)
{
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    float mag = fabsf(s.gyro_dps[0]) + fabsf(s.gyro_dps[1]) + fabsf(s.gyro_dps[2]);
    return mag < 1.0f;
}

/* 取り付け角の補正。シミュレータは走行体の姿勢をそのまま返すので何もしない。 */
void hub_imu_set_tilt(float angle) { (void)angle; }

/* スピーカ。音は鳴らないので、呼ばれたことだけ残す。 */
void hub_speaker_set_volume(uint8_t volume) { (void)volume; }
void hub_speaker_play_tone(uint16_t frequency, int32_t duration)
{
    (void)frequency;
    (void)duration;
}
void hub_speaker_stop(void) { }

uint16_t hub_battery_get_voltage(void)
{
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    return (uint16_t)s.battery_mv;
}

uint16_t hub_battery_get_current(void)
{
    etsim_sensor_packet_t s;
    etsim_get_sensors(&s);
    /* 出力の合計から概算する。実機の実測とは合わないので参考値。 */
    int total = abs(s.motor_power[0]) + abs(s.motor_power[1]) + abs(s.motor_power[2]);
    return (uint16_t)(60 + total * 4);
}

void hub_system_shutdown(void)
{
    printf("[etsim] hub_system_shutdown() が呼ばれました\n");
    etsim_runtime_shutdown();
    exit(0);
}

/*
 * Bluetooth。シミュレータには相手がいないので、広告は成功したことにして
 * 「未接続」を返し続ける。走行ログは serial_wri_dat のほうで標準出力に出る。
 *
 * 実機では、接続を待ってから走り出すアプリだとここで止まる。
 * シミュレータでは待たせずに済むよう、常に未接続を返して先へ進ませる。
 */
pbio_error_t hub_bluetooth_enable_advertising(void) { return PBIO_SUCCESS; }
pbio_error_t hub_bluetooth_disable_advertising(void) { return PBIO_SUCCESS; }

/*
 * 既定は「未接続」。相手がいないので、接続を待つ作りのアプリを
 * フォースセンサで抜けさせるため。
 *
 * ただし「接続していないとログを出さない」作りだと、走行ログが一切
 * 取れずに何が起きているのか分からなくなる。ログを見たいときは
 * 環境変数 ETSIM_BT_CONNECTED=1 で「接続済み」を返せるようにしておく。
 * シリアルの出力先は結局こちらの標準出力なので、嘘にはならない。
 */
pbio_error_t hub_bluetooth_is_connected(bool *connected)
{
    if (connected == NULL) return PBIO_ERROR_INVALID_ARG;

    const char *env = getenv("ETSIM_BT_CONNECTED");
    *connected = (env != NULL && env[0] == '1');
    return PBIO_SUCCESS;
}

/* ハブの 5x5 表示は端末へ出す。走行中の状態確認には十分。 */
pbio_error_t hub_display_orientation(uint8_t up) { (void)up; return PBIO_SUCCESS; }
pbio_error_t hub_display_off(void) { return PBIO_SUCCESS; }

pbio_error_t hub_display_pixel(uint8_t row, uint8_t column, uint8_t brightness)
{
    (void)row; (void)column; (void)brightness;
    return PBIO_SUCCESS;
}

pbio_error_t hub_display_image(uint8_t *image) { (void)image; return PBIO_SUCCESS; }

pbio_error_t hub_display_number(const int8_t num)
{
    printf("[hub] display: %d\n", (int)num);
    return PBIO_SUCCESS;
}

pbio_error_t hub_display_char(const char c)
{
    printf("[hub] display: %c\n", c);
    return PBIO_SUCCESS;
}

pbio_error_t hub_display_text(const char *text, uint32_t on, uint32_t off)
{
    (void)on; (void)off;
    printf("[hub] display: %s\n", text ? text : "");
    return PBIO_SUCCESS;
}

pbio_error_t hub_display_text_scroll(const char *text, uint32_t delay)
{
    (void)delay;
    printf("[hub] display: %s\n", text ? text : "");
    return PBIO_SUCCESS;
}

pbio_error_t hub_light_on_hsv(const pbio_color_hsv_t *hsv)
{
    if (hsv == NULL) return PBIO_ERROR_INVALID_ARG;
    pbio_color_rgb_t rgb;
    pbio_color_hsv_to_rgb(hsv, &rgb);
    etsim_set_led(rgb.r, rgb.g, rgb.b);
    return PBIO_SUCCESS;
}

pbio_error_t hub_light_on_color(pbio_color_t color)
{
    /* pbio_color_t は HSV を 8bit に詰めた値。展開して RGB へ戻す。 */
    pbio_color_hsv_t hsv;
    hsv.h = (uint16_t)(((color >> 3) & 0x1F) * 30);
    hsv.s = (uint8_t)(((color >> 2) & 0x01) * 100);
    hsv.v = (int8_t)((color & 0x03) * 34);
    if (hsv.v > 100) hsv.v = 100;
    return hub_light_on_hsv(&hsv);
}

pbio_error_t hub_light_off(void)
{
    etsim_set_led(0, 0, 0);
    return PBIO_SUCCESS;
}
