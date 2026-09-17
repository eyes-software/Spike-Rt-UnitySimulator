/*
 * etsim_runtime.h
 *
 * Unity シミュレータとの接続を担うランタイム。
 * SPIKE-RT の API 実装 (etsim_device.c) と ASP3 shim (asp3_shim.c) は
 * どちらもここを経由してシミュレータとやり取りする。
 *
 * アプリケーションコードから直接呼ぶ必要があるのは etsim_runtime_init() と
 * etsim_runtime_shutdown() だけで、それも既定の main() が代行する。
 */
#ifndef ETSIM_RUNTIME_H
#define ETSIM_RUNTIME_H

#include <stdbool.h>
#include <stdint.h>

#include "etsim_protocol.h"

#ifdef __cplusplus
extern "C" {
#endif

/* 実行モード */
typedef enum {
    /* シミュレータは自走し、こちらは最新のセンサ値を読むだけ。実機に近い。 */
    ETSIM_MODE_FREERUN = 0,
    /* こちらが指令を送るたびにシミュレータが 1 ステップ進む。完全に再現可能。 */
    ETSIM_MODE_LOCKSTEP = 1
} etsim_mode_t;

typedef struct {
    const char  *host;          /* 既定 "127.0.0.1" */
    uint16_t     port;          /* 既定 54001 */
    etsim_mode_t mode;
    uint32_t     tx_period_us;  /* FREERUN での送信周期。既定 2000 */
    uint32_t     step_us;       /* LOCKSTEP での 1 ステップ幅。既定 2000 */
    uint32_t     connect_timeout_ms; /* 初回センサ受信までの待ち。既定 10000 */
    uint32_t     lost_timeout_ms;    /* 接続後にセンサが途絶えたら終了。既定 3000。0 で無効 */
} etsim_config_t;

/* 環境変数から既定値を埋める。
 *   ETSIM_HOST, ETSIM_PORT, ETSIM_LOCKSTEP, ETSIM_STEP_US, ETSIM_TX_PERIOD_US,
 *   ETSIM_CONNECT_TIMEOUT_MS, ETSIM_LOST_TIMEOUT_MS
 */
void etsim_config_defaults(etsim_config_t *cfg);

/* 接続してランタイムを開始する。成功で true。
 * 初回のセンサパケットを受け取るまでブロックするので、
 * 戻った時点でセンサ値は必ず有効。 */
bool etsim_runtime_init(const etsim_config_t *cfg);

void etsim_runtime_shutdown(void);

bool etsim_runtime_is_connected(void);

/* シミュレータ時刻を us 単位で進める。
 * FREERUN では実時間の待ち、LOCKSTEP では物理ステップの進行になる。 */
void etsim_advance_time(uint32_t microseconds);

/* ランタイム開始からの経過時間 [us]。
 * LOCKSTEP ではシミュレータ時刻、FREERUN では実時間。 */
uint64_t etsim_now_us(void);

/* --- センサ／アクチュエータへのアクセス -------------------------------- */

/* 最新のセンサパケットのスナップショットを取る。
 * 受信スレッドと競合しないようコピーを返す。 */
void etsim_get_sensors(etsim_sensor_packet_t *out);

/* アクチュエータ指令を更新する。実際の送信は送信スレッド（FREERUN）または
 * etsim_advance_time()（LOCKSTEP）が行う。 */
void etsim_set_motor(int index, int32_t power, uint8_t mode);
void etsim_reset_motor_count(int index);
void etsim_set_led(uint8_t r, uint8_t g, uint8_t b);
void etsim_request_imu_reset(void);
void etsim_request_sim_reset(void);

/* --- 補助 -------------------------------------------------------------- */

/* 実時間で指定時間だけ待つ。解像度は 1ms 未満を狙ってスピン併用。 */
void etsim_sleep_us(uint64_t microseconds);

/* Ctrl-C などで終了要求が来たか。走行ループの継続条件に使える。 */
bool etsim_should_exit(void);

#ifdef __cplusplus
}
#endif

#endif /* ETSIM_RUNTIME_H */
