/*
 * syssvc/serial.h
 *
 * シリアルインタフェースドライバの API。走行体のログ出力が使う。
 * シミュレータでは、書き込まれた内容を標準出力と etsim_serial.log へ流す。
 * 実機の Bluetooth と違って詰まらないので、送信は必ず成功する。
 */
#ifndef ETSIM_SYSSVC_SERIAL_H
#define ETSIM_SYSSVC_SERIAL_H

#include <kernel.h>
#include <serial/serial.h>

#ifdef __cplusplus
extern "C" {
#endif

ER      serial_opn_por(ID portid);
ER      serial_cls_por(ID portid);
ER_UINT serial_wri_dat(ID portid, const char *buf, uint_t len);
ER_UINT serial_rea_dat(ID portid, char *buf, uint_t len);
ER      serial_ctl_por(ID portid, uint_t ioctl);

#ifdef __cplusplus
}
#endif

#endif /* ETSIM_SYSSVC_SERIAL_H */
