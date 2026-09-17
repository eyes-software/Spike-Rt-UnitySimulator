/*
 * etrobo_app.c : ETロボコン走行体のサンプル走行プログラム。
 *
 * このファイルは「実機 (SPIKE-RT) でもそのままビルドできる」ことを狙って書いてある。
 * シミュレータ固有の API は使わず、spike/ 以下の公開 API と ASP3 の
 * サービスコールだけで完結させている。唯一の例外は
 * etsim_register_task() / etsim_register_semaphore() で、実機では
 * これが .cfg の静的 API に置き換わる（下の ETROBO_ON_SIMULATOR を参照）。
 *
 * 走行内容
 *   1. 中央ボタン（シミュレータでは Space キー）が押されるまで待つ
 *   2. カラーセンサの反射光量を PID 制御してライントレース
 *   3. 超音波センサが近距離に何かを見つけたら減速して停止
 */
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>

#include "kernel.h"

#include <spike/hub/battery.h>
#include <spike/hub/button.h>
#include <spike/hub/display.h>
#include <spike/hub/imu.h>
#include <spike/hub/light.h>
#include <spike/pup/colorsensor.h>
#include <spike/pup/motor.h>
#include <spike/pup/ultrasonicsensor.h>

/* シミュレータでビルドしているかどうか。実機のビルドでは 0 になる。 */
#ifndef ETROBO_ON_SIMULATOR
#  define ETROBO_ON_SIMULATOR 1
#endif

/*
 * 走行ループの継続条件。実機では止める外部要因が無いので常に真、
 * シミュレータでは Ctrl-C や接続断で抜けられるようにする。
 * シミュレータ専用 API への依存をこの 1 か所に閉じ込めておく。
 */
#if ETROBO_ON_SIMULATOR
#  include "etsim/etsim_runtime.h"
#  define ETROBO_KEEP_RUNNING() (!etsim_should_exit())
#else
#  define ETROBO_KEEP_RUNNING() (true)
#endif

/* ---------------------------------------------------------------- */
/* 走行パラメータ                                                     */
/* ---------------------------------------------------------------- */

/* ポート割り当て（実機の配線に合わせて変える） */
/*
 * 走行体規定書 M6.2「４ ケーブルの配線」の配線例。
 * シミュレータ側の既定 (controller/src/etsim_device.c の ensure_port_map) と
 * 必ず揃えること。ずれても「モータは見つかる」ので、
 * アームモータを左輪として回して、その場で回るだけになる。
 */
#define PORT_MOTOR_RIGHT   PBIO_PORT_ID_A
#define PORT_MOTOR_LEFT    PBIO_PORT_ID_B
#define PORT_MOTOR_ARM     PBIO_PORT_ID_C
#define PORT_FORCE_SENSOR  PBIO_PORT_ID_D
#define PORT_COLOR_SENSOR  PBIO_PORT_ID_E
#define PORT_ULTRASONIC    PBIO_PORT_ID_F

#if ETROBO_ON_SIMULATOR
/*
 * シミュレータ側の既定と食い違っていないかをコンパイル時に確かめる。
 * ずれても「モータは見つかる」ので、実行してみるまで気づけない。
 * 実機ビルドではこのヘッダは読まない（上の定義だけが効く）。
 */
#include "etsim_ports.h"
#define ETROBO_PORT_OF(c) (PBIO_PORT_ID_A + ((c) - 'A'))
_Static_assert(PORT_MOTOR_RIGHT  == ETROBO_PORT_OF(ETSIM_DEFAULT_PORT_RIGHT),
               "右モータのポートがシミュレータの既定と違います");
_Static_assert(PORT_MOTOR_LEFT   == ETROBO_PORT_OF(ETSIM_DEFAULT_PORT_LEFT),
               "左モータのポートがシミュレータの既定と違います");
_Static_assert(PORT_MOTOR_ARM    == ETROBO_PORT_OF(ETSIM_DEFAULT_PORT_ARM),
               "アームモータのポートがシミュレータの既定と違います");
_Static_assert(PORT_COLOR_SENSOR == ETROBO_PORT_OF(ETSIM_DEFAULT_PORT_COLOR),
               "カラーセンサのポートがシミュレータの既定と違います");
_Static_assert(PORT_ULTRASONIC   == ETROBO_PORT_OF(ETSIM_DEFAULT_PORT_ULTRASONIC),
               "超音波センサのポートがシミュレータの既定と違います");
#endif

/* 制御周期 [us]。ASP3 の RELTIM はマイクロ秒。 */
#define CONTROL_PERIOD_US  4000

/*
 * ライントレース
 *
 * 目標値はラインの「縁」を走るための値。シミュレータ既定のコースだと
 * 黒 = 6、白 = 94 なので中点の 50 を狙う。実機では走行前に
 * 黒と白を実測して、その中点をここへ入れる。
 *
 * 注意: 公式コースの緑マットは反射光量 48 で、この目標値とほぼ同じ。
 * センサが白帯の外へ出ると偏差がほぼ 0 になり、まっすぐ走り続けて戻れない。
 * 反射光量だけを見る限りこれは避けられないので、外れない走りが前提。
 */
#define TARGET_REFLECTION  50

/*
 * 直進時の出力 [%]。2026 公式コース (L) を走行体 Re-HackS の実寸で
 * Unity で繰り返し走らせた実測（tools/tune_gains.ps1 -Repeat 8）。
 *   40 -> 8/8 完走  26.18s (26.17〜26.20)
 *   50 -> 6/7 完走  20.89s
 * 速いほど良いわけではない。
 *
 * 完走しない回は、計測区間の本線にゲートポジションの灰色の丸 (反射 64) と
 * 青い区間標識 (反射 38、長さ 100mm) が乗っていて、目標値 50 に対して
 * ±13 の外乱になるところで崩れる。一度白帯の外へ出ると
 * 緑マット (反射 48) が目標値とほぼ同じなので戻れない。
 */
#define BASE_POWER         40

/*
 * ゲインの目安
 *   カラーセンサは約 10mm の横ずれで反射光量が 12 <-> 89 まで振れるので、
 *   反射光量 1 は横ずれ約 0.13mm に相当する。そこから
 *   「反射光量の偏差 38 (= 横ずれ 5mm) を 0.15 秒で戻す」に必要な
 *   左右差を逆算すると出力差 15% 前後、つまり Kp は 0.4 あたりになる。
 *   実測ではもう少し高いほうが安定した（基準出力 40 で 8/8 完走）。
 */
#define PID_KP             0.9f
#define PID_KI             0.030f
/*
 * 微分ゲインは減衰の要。この値を 0.02 程度まで下げると、ラインを外れた
 * あと戻ってこられずその場回転に入る（シミュレータで再現できる）。
 * 目安は「減衰比 0.7 になる値」で、Kp と基準出力から逆算すると 0.08 前後。
 */
#define PID_KD             0.080f

/*
 * 操作量の上限 [%]。1 サンプルの外れ値でその場回転にならないようにする。
 * 基準出力より小さいと片輪を止めきれず、急なカーブで曲がりきれないので、
 * 基準出力と同じ値にしてある（片輪 0%、もう片輪 2 倍まで）。
 */
#define MAX_STEERING       40.0f

/* 微分項の 1 次フィルタ時定数 [s]。黒白の境界での跳ねを抑える */
#define PID_D_FILTER_SEC   0.02f

/* 障害物 */
#define STOP_DISTANCE_MM   120
#define SLOW_DISTANCE_MM   350

/*
 * シミュレータで走らせる間は、ゲインを再ビルドせずに振りたい。
 * 実機ビルドでは環境変数を読まず、上の #define がそのまま使われる。
 *   ETROBO_KP  ETROBO_KI  ETROBO_KD  ETROBO_BASE_POWER  ETROBO_TARGET
 *   ETROBO_MAX_STEERING
 */
#if ETROBO_ON_SIMULATOR
#  include <stdlib.h>
static float env_float(const char *name, float fallback)
{
    const char *v = getenv(name);
    if (v == NULL || *v == '\0') return fallback;
    char *end = NULL;
    float parsed = strtof(v, &end);
    return (end == v) ? fallback : parsed;
}
static int env_int(const char *name, int fallback)
{
    const char *v = getenv(name);
    if (v == NULL || *v == '\0') return fallback;
    char *end = NULL;
    long parsed = strtol(v, &end, 10);
    return (end == v) ? fallback : (int)parsed;
}
#else
#  define env_float(name, fallback) (fallback)
#  define env_int(name, fallback)   (fallback)
#endif

/* ---------------------------------------------------------------- */
/* PID                                                               */
/* ---------------------------------------------------------------- */

typedef struct {
    float kp, ki, kd;
    float integral;
    float prev_error;
    float filtered_derivative;
    float integral_limit;
    float output_limit;
    bool  primed;           /* 1 回目は微分を出さない */
} pid_t;

static void pid_init(pid_t *pid, float kp, float ki, float kd,
                     float integral_limit, float output_limit)
{
    pid->kp = kp;
    pid->ki = ki;
    pid->kd = kd;
    pid->integral = 0.0f;
    pid->prev_error = 0.0f;
    pid->filtered_derivative = 0.0f;
    pid->integral_limit = integral_limit;
    pid->output_limit = output_limit;
    pid->primed = false;
}

static float pid_update(pid_t *pid, float error, float dt)
{
    if (dt <= 0.0f) return 0.0f;

    /* 直線が長いと積分が溜まりすぎてカーブで戻らなくなるので頭打ちにする */
    pid->integral += error * dt;
    if (pid->integral > pid->integral_limit)  pid->integral = pid->integral_limit;
    if (pid->integral < -pid->integral_limit) pid->integral = -pid->integral_limit;

    /*
     * 生の微分は黒白の境界で跳ねる（反射光量が 1 サンプルで 12 -> 89 と動く）。
     * そのまま使うと操作量が飽和して走行体がその場で回るので、
     * 1 次フィルタを通してから使う。
     */
    float derivative = 0.0f;
    if (pid->primed) {
        float raw = (error - pid->prev_error) / dt;
        float alpha = dt / (PID_D_FILTER_SEC + dt);
        pid->filtered_derivative += (raw - pid->filtered_derivative) * alpha;
        derivative = pid->filtered_derivative;
    } else {
        pid->primed = true;
    }
    pid->prev_error = error;

    float output = pid->kp * error + pid->ki * pid->integral + pid->kd * derivative;

    if (output > pid->output_limit)  output = pid->output_limit;
    if (output < -pid->output_limit) output = -pid->output_limit;
    return output;
}

/* ---------------------------------------------------------------- */
/* デバイス                                                          */
/* ---------------------------------------------------------------- */

static pup_motor_t  *motor_left;
static pup_motor_t  *motor_right;
static pup_motor_t  *motor_arm;
static pup_device_t *color_sensor;
static pup_device_t *ultrasonic_sensor;

static bool setup_devices(void)
{
    motor_left  = pup_motor_get_device(PORT_MOTOR_LEFT);
    motor_right = pup_motor_get_device(PORT_MOTOR_RIGHT);
    motor_arm   = pup_motor_get_device(PORT_MOTOR_ARM);

    if (motor_left == NULL || motor_right == NULL) {
        syslog(LOG_ERROR, "駆動モータが見つかりません (左=%c 右=%c)",
               PORT_MOTOR_LEFT, PORT_MOTOR_RIGHT);
        return false;
    }

    /*
     * 左右のモータは向かい合わせに付くので、片方は逆回転が正方向になる。
     * ここを間違えると走行体がその場で回る。
     */
    SVC_PERROR(pup_motor_setup(motor_left, PUP_DIRECTION_COUNTERCLOCKWISE, true));
    SVC_PERROR(pup_motor_setup(motor_right, PUP_DIRECTION_CLOCKWISE, true));
    if (motor_arm != NULL) {
        SVC_PERROR(pup_motor_setup(motor_arm, PUP_DIRECTION_CLOCKWISE, true));
    }

    color_sensor = pup_color_sensor_get_device(PORT_COLOR_SENSOR);
    if (color_sensor == NULL) {
        syslog(LOG_ERROR, "カラーセンサが見つかりません (ポート %c)", PORT_COLOR_SENSOR);
        return false;
    }

    ultrasonic_sensor = pup_ultrasonic_sensor_get_device(PORT_ULTRASONIC);
    if (ultrasonic_sensor == NULL) {
        syslog(LOG_WARNING, "超音波センサが見つかりません。障害物検知なしで走ります");
    }

    syslog(LOG_NOTICE, "ポート 右=%c 左=%c アーム=%c カラー=%c 超音波=%c",
           'A' + (PORT_MOTOR_RIGHT - PBIO_PORT_ID_A),
           'A' + (PORT_MOTOR_LEFT  - PBIO_PORT_ID_A),
           'A' + (PORT_MOTOR_ARM   - PBIO_PORT_ID_A),
           'A' + (PORT_COLOR_SENSOR - PBIO_PORT_ID_A),
           'A' + (PORT_ULTRASONIC  - PBIO_PORT_ID_A));

    hub_imu_init();
    return true;
}

static int clamp_power(int power)
{
    if (power > 100) return 100;
    if (power < -100) return -100;
    return power;
}

static void stop_driving(void)
{
    pup_motor_brake(motor_left);
    pup_motor_brake(motor_right);
}

static bool center_button_pressed(void)
{
    hub_button_t pressed = (hub_button_t)0;
    if (hub_button_is_pressed(&pressed) != PBIO_SUCCESS) return false;
    return (pressed & HUB_BUTTON_CENTER) != 0;
}

/* 中央ボタンが押されるまで待つ。シミュレータでは Space キー。 */
static void wait_for_start(void)
{
    syslog(LOG_NOTICE, "中央ボタン (シミュレータでは Space キー) でスタートします");
    hub_display_text("GO?", 500, 100);

    while (!center_button_pressed() && ETROBO_KEEP_RUNNING()) {
        dly_tsk(20 * 1000);
    }

    /* 押しっぱなしのまま走り出さないよう、離されるまで待つ */
    while (center_button_pressed() && ETROBO_KEEP_RUNNING()) {
        dly_tsk(20 * 1000);
    }

    syslog(LOG_NOTICE, "スタート");
}

/* ---------------------------------------------------------------- */
/* メイン                                                            */
/* ---------------------------------------------------------------- */

void main_task(intptr_t exinf)
{
    (void)exinf;

    syslog(LOG_NOTICE, "ETロボコン走行体 起動 (バッテリ %u mV)",
           (unsigned)hub_battery_get_voltage());

    if (!setup_devices()) {
        syslog(LOG_ERROR, "デバイスの初期化に失敗しました");
        return;
    }

#if ETROBO_ON_SIMULATOR
    /*
     * シミュレータでは、つなぎ直すたびにスタート地点からやり直す。
     * ゲインを変えて何度も走らせるときに、前回の走行が終わった場所から
     * 続いてしまうと比較にならない。実機ではこの呼び出し自体が無い。
     */
    etsim_request_sim_reset();
    dly_tsk(200 * 1000);
#endif

    wait_for_start();

    const float kp     = env_float("ETROBO_KP", PID_KP);
    const float ki     = env_float("ETROBO_KI", PID_KI);
    const float kd     = env_float("ETROBO_KD", PID_KD);
    const int   target = env_int("ETROBO_TARGET", TARGET_REFLECTION);
    const int   cruise = env_int("ETROBO_BASE_POWER", BASE_POWER);
    const float max_st = env_float("ETROBO_MAX_STEERING", MAX_STEERING);

    syslog(LOG_NOTICE, "走行パラメータ Kp=%.3f Ki=%.3f Kd=%.3f 目標反射=%d 基準出力=%d 操作量上限=%.1f",
           (double)kp, (double)ki, (double)kd, target, cruise, (double)max_st);

    pid_t steering;
    pid_init(&steering, kp, ki, kd, 40.0f, max_st);

    const float dt = CONTROL_PERIOD_US * 1e-6f;
    uint32_t tick = 0;

    while (ETROBO_KEEP_RUNNING()) {
        /* --- センサ読み取り --- */
        int32_t reflection = pup_color_sensor_reflection(color_sensor);

        int32_t distance = -1;
        if (ultrasonic_sensor != NULL)
            distance = pup_ultrasonic_sensor_distance(ultrasonic_sensor);

        /* --- 障害物で減速・停止 --- */
        int base_power = cruise;
        if (distance >= 0 && distance < STOP_DISTANCE_MM) {
            stop_driving();
            syslog(LOG_NOTICE, "%d mm 先に障害物を検出したので停止します", (int)distance);
            break;
        }
        if (distance >= 0 && distance < SLOW_DISTANCE_MM) {
            base_power = cruise / 2;
        }

        /* --- ライントレース --- */
        float error = (float)(target - reflection);
        float turn = pid_update(&steering, error, dt);

        int left  = clamp_power(base_power - (int)turn);
        int right = clamp_power(base_power + (int)turn);

        pup_motor_set_power(motor_left, left);
        pup_motor_set_power(motor_right, right);

        /* --- 状態表示 (0.5 秒ごと) --- */
        if (++tick % 125 == 0) {
            syslog(LOG_INFO, "反射 %3d  操作量 %6.1f  出力 L%4d R%4d  距離 %5d",
                   (int)reflection, (double)turn, left, right, (int)distance);
        }

        dly_tsk(CONTROL_PERIOD_US);
    }

    stop_driving();
    syslog(LOG_NOTICE, "走行終了");
}

#if ETROBO_ON_SIMULATOR
/*
 * 実機では .cfg の CRE_TSK / CRE_SEM がこれに相当する。
 * シミュレータではカーネルコンフィギュレータを通さないので、
 * ここで同じ内容を実行時に登録する。
 *
 * main() が main_task を呼ぶ前に 1 回呼ぶ。
 * このサンプルは追加タスクを使わないので中身は空。
 */
void etsim_app_register_objects(void)
{
    /* 例: etsim_register_task(ID_SUB_TASK, sub_task, 0); */
    /*     etsim_register_semaphore(ID_SENSOR_SEM, 1, 1);  */
}
#endif
