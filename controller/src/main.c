/*
 * main.c : 制御プロセスのエントリポイント。
 *
 * 実機 (SPIKE-RT) ではカーネルが main_task を起こすが、
 * PC ではここがその役目を代わりに果たす。
 * 走行体のコード自体は app/ 以下の main_task() に閉じている。
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "kernel.h"
#include "etsim/etsim_device.h"
#include "etsim/etsim_runtime.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/*
 * Windows のコンソールの既定のコードページは日本語環境では 932 (Shift-JIS)。
 * こちらは /utf-8 でビルドしていて文字列リテラルが UTF-8 のバイト列なので、
 * そのまま出すと化ける。出力コードページを UTF-8 にして合わせる。
 *
 * コードページはプロセスではなく**コンソール**の設定で、終わっても残る。
 * 同じ窓で動かす他のコマンドを巻き込まないよう、終了時に元へ戻す
 * （強制終了されたときは戻せない。そのときは chcp で戻すこと）。
 *
 * 出力をパイプやファイルへ流すときはコンソールを通らないので、
 * ここは効かない。受け側を UTF-8 にすること（README 参照）。
 */
static UINT g_prev_console_cp;

static void restore_console_cp(void)
{
    if (g_prev_console_cp != 0) SetConsoleOutputCP(g_prev_console_cp);
}

static void setup_console_cp(void)
{
    UINT prev = GetConsoleOutputCP();
    if (prev == 0 || prev == CP_UTF8) return;   /* コンソールが無い / もう UTF-8 */
    if (!SetConsoleOutputCP(CP_UTF8)) return;

    g_prev_console_cp = prev;
    atexit(restore_console_cp);
}
#else
static void setup_console_cp(void) { }
#endif

static void print_usage(const char *argv0)
{
    printf(
        "使い方: %s [オプション]\n"
        "\n"
        "  --host <IP>        シミュレータのアドレス (既定 127.0.0.1)\n"
        "  --port <番号>      シミュレータの UDP ポート (既定 %d)\n"
        "  --lockstep         指令 1 回につき物理 1 ステップ進める決定論モード\n"
        "  --step-us <us>     ロックステップの 1 ステップ幅 (既定 2000)\n"
        "  --tx-period <us>   フリーラン時の送信周期 (既定 2000)\n"
        "  --timeout-ms <ms>  接続待ちの上限 (既定 10000)\n"
        "  -h, --help         このヘルプ\n"
        "\n"
        "同じ設定は環境変数でも指定できます:\n"
        "  ETSIM_HOST ETSIM_PORT ETSIM_LOCKSTEP ETSIM_STEP_US\n"
        "  ETSIM_TX_PERIOD_US ETSIM_CONNECT_TIMEOUT_MS\n"
        "  ETSIM_PORT_LEFT ETSIM_PORT_RIGHT ETSIM_PORT_ARM\n"
        "  ETSIM_PORT_COLOR ETSIM_PORT_ULTRASONIC ETSIM_PORT_FORCE\n",
        argv0, ETSIM_DEFAULT_SIM_PORT);
}

static int parse_args(int argc, char **argv, etsim_config_t *cfg)
{
    for (int i = 1; i < argc; i++) {
        const char *a = argv[i];
        int has_value = (i + 1 < argc);

        if (strcmp(a, "-h") == 0 || strcmp(a, "--help") == 0) {
            print_usage(argv[0]);
            return 1;
        } else if (strcmp(a, "--lockstep") == 0) {
            cfg->mode = ETSIM_MODE_LOCKSTEP;
        } else if (strcmp(a, "--host") == 0 && has_value) {
            cfg->host = argv[++i];
        } else if (strcmp(a, "--port") == 0 && has_value) {
            cfg->port = (uint16_t)atoi(argv[++i]);
        } else if (strcmp(a, "--step-us") == 0 && has_value) {
            cfg->step_us = (uint32_t)atoi(argv[++i]);
        } else if (strcmp(a, "--tx-period") == 0 && has_value) {
            cfg->tx_period_us = (uint32_t)atoi(argv[++i]);
        } else if (strcmp(a, "--timeout-ms") == 0 && has_value) {
            cfg->connect_timeout_ms = (uint32_t)atoi(argv[++i]);
        } else {
            fprintf(stderr, "不明な引数: %s\n\n", a);
            print_usage(argv[0]);
            return -1;
        }
    }
    return 0;
}

int main(int argc, char **argv)
{
    /* 標準出力をリダイレクトすると既定で全バッファになる。
     * 走行ログを別プロセスで追えるよう、行ごとに吐き出させる。 */
    setvbuf(stdout, NULL, _IONBF, 0);

    setup_console_cp();

    etsim_config_t cfg;
    etsim_config_defaults(&cfg);

    int r = parse_args(argc, argv, &cfg);
    if (r != 0) return r < 0 ? 1 : 0;

    if (!etsim_runtime_init(&cfg)) return 1;

    etsim_device_dump_port_map();

    /* app.cfg の CRE_TSK / CRE_CYC に相当する登録。走行プログラムごとに定義する。 */
    etsim_app_register_objects();

    /* ここから先は実機と同じコードが動く */
    main_task(0);

    etsim_kernel_shutdown();
    etsim_runtime_shutdown();

    printf("[etsim] 終了しました\n");
    return 0;
}
