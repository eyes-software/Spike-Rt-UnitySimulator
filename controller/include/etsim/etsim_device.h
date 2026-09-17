/*
 * etsim_device.h
 *
 * SPIKE-RT デバイス API 実装の内部フック。
 * アプリケーションからは使わない。
 */
#ifndef ETSIM_DEVICE_H
#define ETSIM_DEVICE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * 速度制御 (pup_motor_set_speed) の閉ループを 1 周期ぶん回す。
 * ASP3 shim が時間を進める直前に呼ぶ。
 * set_power しか使っていないアプリでは何もしない。
 */
void etsim_device_update(uint32_t elapsed_us);

/* ポート割り当てを標準出力へ表示する（起動時の確認用）。 */
void etsim_device_dump_port_map(void);

#ifdef __cplusplus
}
#endif

#endif /* ETSIM_DEVICE_H */
