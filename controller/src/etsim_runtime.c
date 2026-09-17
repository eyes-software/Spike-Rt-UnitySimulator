/*
 * etsim_runtime.c : Unity シミュレータとの UDP 接続。
 *
 * スレッド構成
 *   受信スレッド : センサパケットを受け取り、最新値を保持する（常時）
 *   送信スレッド : アクチュエータ指令を一定周期で送る（FREERUN のみ）
 *
 * LOCKSTEP では送信スレッドを立てず、etsim_advance_time() が
 * 「1 ステップ送って 1 ステップぶんの応答を待つ」を繰り返す。
 * これで実行ごとに結果が一致するので、回帰テストに使える。
 */
#include "etsim/etsim_runtime.h"
#include "etsim/etsim_platform.h"

#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---------------------------------------------------------------- */
/* ランタイム状態                                                     */
/* ---------------------------------------------------------------- */

typedef struct {
    etsim_config_t cfg;

    etsim_socket_t sock;
    struct sockaddr_in sim_addr;

    etsim_mutex_t lock;
    etsim_cond_t  rx_cond;

    etsim_sensor_packet_t   sensors;
    etsim_actuator_packet_t command;

    volatile uint64_t rx_count;   /* 受信したセンサパケット数 */
    volatile int      running;
    volatile int      connected;
    volatile int      exit_requested;

    uint64_t start_wall_us;
    uint64_t sim_time_us;         /* LOCKSTEP で進めた時間 */
    volatile uint64_t last_rx_wall_us;

    etsim_thread_t rx_thread;
    etsim_thread_t tx_thread;
    int rx_thread_started;
    int tx_thread_started;
} etsim_runtime_t;

static etsim_runtime_t g_rt;
static int g_initialized = 0;

/* ---------------------------------------------------------------- */
/* 時刻                                                              */
/* ---------------------------------------------------------------- */

void etsim_sleep_us(uint64_t microseconds)
{
    if (microseconds == 0) return;

    uint64_t deadline = etsim_wall_clock_us() + microseconds;

    /*
     * OS のスリープは 1ms 前後の粒度しか保証されない。制御周期 4ms の
     * 走行体では誤差が無視できないので、大半を sleep で待ってから
     * 最後の 1ms 弱だけスピンして精度を出す。
     */
    if (microseconds > 1500) {
        uint64_t coarse = microseconds - 1000;
#ifdef _WIN32
        Sleep((DWORD)(coarse / 1000));
#else
        struct timespec req;
        req.tv_sec  = (time_t)(coarse / 1000000ULL);
        req.tv_nsec = (long)((coarse % 1000000ULL) * 1000ULL);
        nanosleep(&req, NULL);
#endif
    }

    while (etsim_wall_clock_us() < deadline) {
        ETSIM_YIELD();
    }
}

uint64_t etsim_now_us(void)
{
    if (g_rt.cfg.mode == ETSIM_MODE_LOCKSTEP) return g_rt.sim_time_us;
    if (g_rt.start_wall_us == 0) return 0;
    return etsim_wall_clock_us() - g_rt.start_wall_us;
}

/* ---------------------------------------------------------------- */
/* 終了要求                                                          */
/* ---------------------------------------------------------------- */

static void on_signal(int sig)
{
    (void)sig;
    g_rt.exit_requested = 1;
}

bool etsim_should_exit(void) { return g_rt.exit_requested != 0; }

/* ---------------------------------------------------------------- */
/* 設定                                                              */
/* ---------------------------------------------------------------- */

static uint32_t env_u32(const char *name, uint32_t fallback)
{
    const char *v = getenv(name);
    if (v == NULL || *v == '\0') return fallback;

    char *end = NULL;
    unsigned long parsed = strtoul(v, &end, 10);
    if (end == v) return fallback;
    return (uint32_t)parsed;
}

void etsim_config_defaults(etsim_config_t *cfg)
{
    const char *host = getenv("ETSIM_HOST");

    cfg->host               = (host && *host) ? host : ETSIM_DEFAULT_SIM_HOST;
    cfg->port               = (uint16_t)env_u32("ETSIM_PORT", ETSIM_DEFAULT_SIM_PORT);
    cfg->mode               = env_u32("ETSIM_LOCKSTEP", 0) ? ETSIM_MODE_LOCKSTEP
                                                           : ETSIM_MODE_FREERUN;
    cfg->tx_period_us       = env_u32("ETSIM_TX_PERIOD_US", 2000);
    cfg->step_us            = env_u32("ETSIM_STEP_US", 2000);
    cfg->connect_timeout_ms = env_u32("ETSIM_CONNECT_TIMEOUT_MS", 10000);
    cfg->lost_timeout_ms    = env_u32("ETSIM_LOST_TIMEOUT_MS", 3000);
}

/* ---------------------------------------------------------------- */
/* 送受信                                                            */
/* ---------------------------------------------------------------- */

static void send_command_locked(void)
{
    g_rt.command.seq++;

    etsim_actuator_packet_t pkt = g_rt.command;
    pkt.magic        = ETSIM_MAGIC_ACTUATOR;
    pkt.version      = ETSIM_PROTOCOL_VERSION;
    pkt.ctrl_time_us = (uint32_t)etsim_now_us();
    if (g_rt.cfg.mode == ETSIM_MODE_LOCKSTEP) pkt.flags |= ETSIM_AFLAG_LOCKSTEP;

    sendto(g_rt.sock, (const char *)&pkt, (int)sizeof(pkt), 0,
           (struct sockaddr *)&g_rt.sim_addr, (int)sizeof(g_rt.sim_addr));

    /* 一度だけ伝えたいもの（リセット要求など）は送った時点で落とす */
    g_rt.command.flags &= (uint16_t)~(ETSIM_AFLAG_RESET | ETSIM_AFLAG_SHUTDOWN);
    g_rt.command.reset_count_mask = 0;
    g_rt.command.reset_imu = 0;
}

static void send_command(void)
{
    ETSIM_MUTEX_LOCK(&g_rt.lock);
    send_command_locked();
    ETSIM_MUTEX_UNLOCK(&g_rt.lock);
}

static ETSIM_THREAD_RET rx_thread_main(ETSIM_THREAD_ARG arg)
{
    (void)arg;
    etsim_sensor_packet_t pkt;

    while (g_rt.running) {
        struct sockaddr_in from;
#ifdef _WIN32
        int from_len = (int)sizeof(from);
#else
        socklen_t from_len = sizeof(from);
#endif
        int n = recvfrom(g_rt.sock, (char *)&pkt, (int)sizeof(pkt), 0,
                         (struct sockaddr *)&from, &from_len);

        if (n < 0) {
#ifdef _WIN32
            int err = WSAGetLastError();
            /* 相手が未起動だと ICMP 由来の WSAECONNRESET が返る。待ち続ける。 */
            if (err == WSAETIMEDOUT || err == WSAECONNRESET) continue;
#else
            if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) continue;
#endif
            if (!g_rt.running) break;
            continue;
        }

        /* 外部から届くデータなので、形が合わないものは黙って捨てる */
        if (n < (int)sizeof(pkt)) continue;
        if (pkt.magic != ETSIM_MAGIC_SENSOR) continue;
        if (pkt.version != ETSIM_PROTOCOL_VERSION) continue;

        ETSIM_MUTEX_LOCK(&g_rt.lock);
        g_rt.sensors = pkt;
        g_rt.rx_count++;
        g_rt.connected = 1;
        g_rt.last_rx_wall_us = etsim_wall_clock_us();
        ETSIM_COND_BROADCAST(&g_rt.rx_cond);
        ETSIM_MUTEX_UNLOCK(&g_rt.lock);
    }

    ETSIM_THREAD_DONE;
}

static ETSIM_THREAD_RET tx_thread_main(ETSIM_THREAD_ARG arg)
{
    (void)arg;
    uint64_t next = etsim_wall_clock_us();

    while (g_rt.running) {
        send_command();
        next += g_rt.cfg.tx_period_us;

        uint64_t now = etsim_wall_clock_us();
        if (next > now) {
            etsim_sleep_us(next - now);
        } else {
            /* 遅れたぶんは取り戻さず基準を打ち直す（雪だるま式の詰まりを防ぐ） */
            next = now;
        }
    }

    ETSIM_THREAD_DONE;
}

/* ---------------------------------------------------------------- */
/* 初期化・終了                                                       */
/* ---------------------------------------------------------------- */

static bool wait_for_first_packet(uint32_t timeout_ms)
{
    uint64_t deadline = etsim_wall_clock_us() + (uint64_t)timeout_ms * 1000ULL;

    while (etsim_wall_clock_us() < deadline) {
        if (g_rt.connected) return true;
        if (g_rt.exit_requested) return false;

        /* FREERUN では送信スレッドが叩き続けるが、LOCKSTEP では
         * こちらから鳴らさないとシミュレータが送信先を知らない。 */
        if (g_rt.cfg.mode == ETSIM_MODE_LOCKSTEP) send_command();

        etsim_sleep_us(20000);
    }
    return g_rt.connected != 0;
}

bool etsim_runtime_init(const etsim_config_t *cfg)
{
    if (g_initialized) return true;

    memset(&g_rt, 0, sizeof(g_rt));
    g_rt.sock = ETSIM_INVALID_SOCKET;

    if (cfg != NULL) {
        g_rt.cfg = *cfg;
    } else {
        etsim_config_defaults(&g_rt.cfg);
    }
    if (g_rt.cfg.host == NULL)          g_rt.cfg.host = ETSIM_DEFAULT_SIM_HOST;
    if (g_rt.cfg.port == 0)             g_rt.cfg.port = ETSIM_DEFAULT_SIM_PORT;
    if (g_rt.cfg.tx_period_us == 0)     g_rt.cfg.tx_period_us = 2000;
    if (g_rt.cfg.step_us == 0)          g_rt.cfg.step_us = 2000;
    if (g_rt.cfg.connect_timeout_ms == 0) g_rt.cfg.connect_timeout_ms = 10000;

#ifdef _WIN32
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        fprintf(stderr, "[etsim] WSAStartup に失敗しました\n");
        return false;
    }
    timeBeginPeriod(1); /* Sleep の粒度を 1ms に上げる */
#endif

    g_rt.sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (g_rt.sock == ETSIM_INVALID_SOCKET) {
        fprintf(stderr, "[etsim] UDP ソケットを作れませんでした\n");
        return false;
    }

    memset(&g_rt.sim_addr, 0, sizeof(g_rt.sim_addr));
    g_rt.sim_addr.sin_family = AF_INET;
    g_rt.sim_addr.sin_port   = htons(g_rt.cfg.port);
    if (inet_pton(AF_INET, g_rt.cfg.host, &g_rt.sim_addr.sin_addr) != 1) {
        fprintf(stderr, "[etsim] 接続先アドレスが不正です: %s\n", g_rt.cfg.host);
        return false;
    }

    /* 受信でブロックしっぱなしにならないようタイムアウトを入れる */
#ifdef _WIN32
    DWORD tv = 200;
    setsockopt(g_rt.sock, SOL_SOCKET, SO_RCVTIMEO, (const char *)&tv, sizeof(tv));
#else
    struct timeval tv;
    tv.tv_sec  = 0;
    tv.tv_usec = 200000;
    setsockopt(g_rt.sock, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
#endif

    ETSIM_MUTEX_INIT(&g_rt.lock);
    ETSIM_COND_INIT(&g_rt.rx_cond);

    g_rt.command.magic   = ETSIM_MAGIC_ACTUATOR;
    g_rt.command.version = ETSIM_PROTOCOL_VERSION;
    g_rt.sensors.ultrasonic_mm = -1;

    g_rt.running = 1;
    g_rt.start_wall_us = etsim_wall_clock_us();

    signal(SIGINT, on_signal);
#ifdef SIGTERM
    signal(SIGTERM, on_signal);
#endif

    if (!etsim_thread_start(&g_rt.rx_thread, rx_thread_main, NULL)) {
        fprintf(stderr, "[etsim] 受信スレッドを作れませんでした\n");
        return false;
    }
    g_rt.rx_thread_started = 1;

    if (g_rt.cfg.mode == ETSIM_MODE_FREERUN) {
        if (etsim_thread_start(&g_rt.tx_thread, tx_thread_main, NULL))
            g_rt.tx_thread_started = 1;
    }

    printf("[etsim] %s:%u へ接続します (%s)\n",
           g_rt.cfg.host, (unsigned)g_rt.cfg.port,
           g_rt.cfg.mode == ETSIM_MODE_LOCKSTEP ? "LOCKSTEP" : "FREERUN");

    if (!wait_for_first_packet(g_rt.cfg.connect_timeout_ms)) {
        fprintf(stderr,
                "[etsim] シミュレータから応答がありません。"
                " Unity 側を再生状態にしてから起動してください。\n");
        etsim_runtime_shutdown();
        return false;
    }

    printf("[etsim] 接続しました\n");
    g_initialized = 1;
    return true;
}

void etsim_runtime_shutdown(void)
{
    if (!g_rt.running) return;

    /* 終了を伝えてモータを止めさせる */
    ETSIM_MUTEX_LOCK(&g_rt.lock);
    g_rt.command.flags |= ETSIM_AFLAG_SHUTDOWN;
    for (int i = 0; i < ETSIM_MOTOR_NUM; i++) {
        g_rt.command.motor_power[i] = 0;
        g_rt.command.motor_mode[i]  = ETSIM_MOTOR_MODE_COAST;
    }
    send_command_locked();
    ETSIM_MUTEX_UNLOCK(&g_rt.lock);

    g_rt.running = 0;

    if (g_rt.tx_thread_started) { etsim_thread_join(&g_rt.tx_thread, 1000); g_rt.tx_thread_started = 0; }
    if (g_rt.rx_thread_started) { etsim_thread_join(&g_rt.rx_thread, 1000); g_rt.rx_thread_started = 0; }

    if (g_rt.sock != ETSIM_INVALID_SOCKET) {
        ETSIM_CLOSE_SOCKET(g_rt.sock);
        g_rt.sock = ETSIM_INVALID_SOCKET;
    }

    ETSIM_COND_DESTROY(&g_rt.rx_cond);
    ETSIM_MUTEX_DESTROY(&g_rt.lock);

#ifdef _WIN32
    timeEndPeriod(1);
    WSACleanup();
#endif

    g_initialized = 0;
}

bool etsim_runtime_is_connected(void) { return g_rt.connected != 0; }

/* ---------------------------------------------------------------- */
/* 時間進行                                                          */
/* ---------------------------------------------------------------- */

static void lockstep_step(void)
{
    ETSIM_MUTEX_LOCK(&g_rt.lock);
    uint64_t before = g_rt.rx_count;
    send_command_locked();

    /* シミュレータは 1 指令につき必ず 1 センサパケットを返す */
    uint64_t deadline = etsim_wall_clock_us() + 2000000ULL; /* 2 秒 */
    while (g_rt.rx_count == before && !g_rt.exit_requested) {
        if (etsim_wall_clock_us() > deadline) {
            ETSIM_MUTEX_UNLOCK(&g_rt.lock);
            fprintf(stderr, "[etsim] ロックステップの応答が途絶えました\n");
            g_rt.exit_requested = 1;
            return;
        }
        etsim_cond_wait_ms(&g_rt.rx_cond, &g_rt.lock, 50);
    }
    ETSIM_MUTEX_UNLOCK(&g_rt.lock);
}

/*
 * 接続後にセンサが途絶えたら終了要求を立てる。
 * シミュレータを閉じたのに制御プロセスだけ残って回り続けるのを防ぐ。
 */
static void check_connection_lost(void)
{
    if (g_rt.cfg.lost_timeout_ms == 0) return;
    if (!g_rt.connected) return;

    uint64_t last = g_rt.last_rx_wall_us;
    if (last == 0) return;

    uint64_t silence_us = etsim_wall_clock_us() - last;
    if (silence_us < (uint64_t)g_rt.cfg.lost_timeout_ms * 1000ULL) return;

    fprintf(stderr, "[etsim] シミュレータからの応答が %.1f 秒途絶えたので終了します\n",
            (double)silence_us / 1e6);
    g_rt.exit_requested = 1;
}

void etsim_advance_time(uint32_t microseconds)
{
    check_connection_lost();

    if (g_rt.cfg.mode == ETSIM_MODE_LOCKSTEP) {
        uint32_t step = g_rt.cfg.step_us;
        uint32_t steps = (microseconds + step - 1) / step;
        if (steps == 0) steps = 1;

        for (uint32_t i = 0; i < steps && !g_rt.exit_requested; i++) {
            lockstep_step();
            g_rt.sim_time_us += step;
        }
        return;
    }

    etsim_sleep_us(microseconds);
}

/* ---------------------------------------------------------------- */
/* センサ／アクチュエータ                                             */
/* ---------------------------------------------------------------- */

void etsim_get_sensors(etsim_sensor_packet_t *out)
{
    if (out == NULL) return;
    ETSIM_MUTEX_LOCK(&g_rt.lock);
    *out = g_rt.sensors;
    ETSIM_MUTEX_UNLOCK(&g_rt.lock);
}

void etsim_set_motor(int index, int32_t power, uint8_t mode)
{
    if (index < 0 || index >= ETSIM_MOTOR_NUM) return;
    if (power > 100) power = 100;
    if (power < -100) power = -100;

    ETSIM_MUTEX_LOCK(&g_rt.lock);
    g_rt.command.motor_power[index] = power;
    g_rt.command.motor_mode[index]  = mode;
    ETSIM_MUTEX_UNLOCK(&g_rt.lock);
}

void etsim_reset_motor_count(int index)
{
    if (index < 0 || index >= ETSIM_MOTOR_NUM) return;
    ETSIM_MUTEX_LOCK(&g_rt.lock);
    g_rt.command.reset_count_mask |= (uint8_t)(1u << index);
    ETSIM_MUTEX_UNLOCK(&g_rt.lock);
}

void etsim_set_led(uint8_t r, uint8_t g, uint8_t b)
{
    ETSIM_MUTEX_LOCK(&g_rt.lock);
    g_rt.command.led_rgb[0] = r;
    g_rt.command.led_rgb[1] = g;
    g_rt.command.led_rgb[2] = b;
    ETSIM_MUTEX_UNLOCK(&g_rt.lock);
}

void etsim_request_imu_reset(void)
{
    ETSIM_MUTEX_LOCK(&g_rt.lock);
    g_rt.command.reset_imu = 1;
    ETSIM_MUTEX_UNLOCK(&g_rt.lock);
}

void etsim_request_sim_reset(void)
{
    ETSIM_MUTEX_LOCK(&g_rt.lock);
    g_rt.command.flags |= ETSIM_AFLAG_RESET;
    ETSIM_MUTEX_UNLOCK(&g_rt.lock);
}
