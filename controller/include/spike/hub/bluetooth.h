/*
 * spike/hub/bluetooth.h : ハブの Bluetooth（PC シミュレータ用）。
 *
 * 宣言は上流 (etrobocon/spike-rt-RasPike-ART の
 * drivers/include/spike/hub/bluetooth.h) と同じ。
 * 実装は etsim_device.c にあるスタブで、常に「未接続」を返す。
 */
#ifndef ETSIM_HUB_BLUETOOTH_H
#define ETSIM_HUB_BLUETOOTH_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdbool.h>
#include <pbio/error.h>

pbio_error_t hub_bluetooth_enable_advertising(void);
pbio_error_t hub_bluetooth_disable_advertising(void);
pbio_error_t hub_bluetooth_is_connected(bool *connected);

#ifdef __cplusplus
}
#endif

#endif /* ETSIM_HUB_BLUETOOTH_H */
