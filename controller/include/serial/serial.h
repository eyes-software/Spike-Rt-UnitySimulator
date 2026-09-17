/*
 * serial/serial.h
 *
 * TOPPERS のシリアルインタフェースドライバのうち、走行体が使う分だけ。
 * 実機では Bluetooth のシリアルポートに出るログが、
 * シミュレータでは標準出力とファイルへ流れる。
 */
#ifndef ETSIM_SERIAL_H
#define ETSIM_SERIAL_H

#ifdef __cplusplus
extern "C" {
#endif

#include <kernel.h>

/* 実機の定義に合わせたポート番号。1 番が Bluetooth。 */
#ifndef SIO_USB_PORTID
#define SIO_USB_PORTID        1
#endif
#ifndef SIO_BLUETOOTH_PORTID
#define SIO_BLUETOOTH_PORTID  2
#endif


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_SERIAL_H */
