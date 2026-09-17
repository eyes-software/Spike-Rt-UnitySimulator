/*
 * etsim_serial.c : シリアルインタフェースドライバの代わり。
 *
 * 実機の走行体は Bluetooth のシリアルポートへログを流す。
 * シミュレータには相手がいないので、標準出力と etsim_serial.log の両方へ出す。
 *
 * 実機と違って送信が詰まらない点に注意。実機では serial_wri_dat が
 * 送りきれずに戻ることがあり、そこでログを捨てる作りになっている
 * アプリだと、シミュレータでは起きない経路になる。
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <kernel.h>
#include <syssvc/serial.h>

#include "etsim/etsim_platform.h"

#define ETSIM_SERIAL_LOG_ENV   "ETSIM_SERIAL_LOG"
#define ETSIM_SERIAL_LOG_FILE  "etsim_serial.log"

static FILE *g_log;
static int   g_opened[8];
static int   g_echo = 1;

static int port_index(ID portid)
{
    if (portid < 1 || portid > 7) return -1;
    return (int)portid;
}

/*
 * ログは既定では標準出力だけに出す。
 * 走行プログラムの置き場でそのまま実行することが多いので、
 * 何も言われずにファイルを作るとリポジトリが汚れるため。
 * ファイルにも残したいときは ETSIM_SERIAL_LOG=<パス> を指定する
 * （ETSIM_SERIAL_LOG= と値を省くと既定名 etsim_serial.log）。
 */
static void ensure_log(void)
{
    static int done = 0;
    if (done) return;
    done = 1;

    const char *path = getenv(ETSIM_SERIAL_LOG_ENV);
    if (path == NULL) return;
    if (path[0] == '\0') path = ETSIM_SERIAL_LOG_FILE;

    g_log = fopen(path, "w");
    if (g_log == NULL)
        fprintf(stderr, "[etsim] シリアルのログ '%s' を開けませんでした\n", path);
}

ER serial_opn_por(ID portid)
{
    int i = port_index(portid);
    if (i < 0) return E_ID;

    if (g_opened[i]) return E_OBJ;   /* 実機と同じく、二重オープンは E_OBJ */
    g_opened[i] = 1;

    ensure_log();
    printf("[etsim] シリアルポート %d を開きました (Bluetooth の代わり)\n", (int)portid);
    return E_OK;
}

ER serial_cls_por(ID portid)
{
    int i = port_index(portid);
    if (i < 0) return E_ID;
    if (!g_opened[i]) return E_OBJ;
    g_opened[i] = 0;
    return E_OK;
}

ER_UINT serial_wri_dat(ID portid, const char *buf, uint_t len)
{
    int i = port_index(portid);
    if (i < 0) return E_ID;
    if (!g_opened[i]) return E_OBJ;
    if (buf == NULL) return E_PAR;

    if (g_log != NULL) {
        fwrite(buf, 1, len, g_log);
        fflush(g_log);
    }
    if (g_echo) {
        fwrite(buf, 1, len, stdout);
        fflush(stdout);
    }

    /* 実機と違って詰まらないので、常に全部書けたことにする */
    return (ER_UINT)len;
}

ER_UINT serial_rea_dat(ID portid, char *buf, uint_t len)
{
    (void)buf;
    (void)len;
    int i = port_index(portid);
    if (i < 0) return E_ID;
    if (!g_opened[i]) return E_OBJ;

    /* 受信は無い。待たせると走行が止まるので、すぐ 0 を返す。 */
    return 0;
}

ER serial_ctl_por(ID portid, uint_t ioctl)
{
    (void)ioctl;
    int i = port_index(portid);
    if (i < 0) return E_ID;
    if (!g_opened[i]) return E_OBJ;
    return E_OK;
}

/* 走行のログが標準出力に混ざると読みにくいときのために切れるようにしておく */
void etsim_serial_set_echo(int on) { g_echo = on ? 1 : 0; }
