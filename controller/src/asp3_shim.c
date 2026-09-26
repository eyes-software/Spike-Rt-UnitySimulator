/*
 * asp3_shim.c : TOPPERS/ASP3 API の PC 実装。
 *
 * タスクは OS スレッド、周期ハンドラは専用スレッドのループ、
 * セマフォはミューテックス + 条件変数で表す。
 *
 * 時間を進める API (dly_tsk / tslp_tsk) は必ず etsim_advance_time() を通す。
 * LOCKSTEP ではここがシミュレータを 1 ステップ進める唯一の入口になるので、
 * 「待たないループ」を書くとシミュレータが進まない点に注意。
 */
#include "kernel.h"

#include "etsim/etsim_device.h"
#include "etsim/etsim_platform.h"
#include "etsim/etsim_runtime.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#define MAX_TASKS      16
#define MAX_CYCLICS     8
#define MAX_SEMAPHORES 16

/* ---------------------------------------------------------------- */
/* オブジェクト                                                      */
/* ---------------------------------------------------------------- */

typedef struct {
    ID   id;
    void (*entry)(intptr_t);
    intptr_t exinf;

    etsim_thread_t thread;
    volatile int   active;     /* スレッドが走っている */
    volatile int   terminate;  /* 終了要求 */
    volatile int   wakeup;     /* wup_tsk のカウント */
    volatile int   released;   /* rel_wai された */
} shim_task_t;

typedef struct {
    ID   id;
    void (*handler)(intptr_t);
    intptr_t exinf;
    RELTIM period_us;
    RELTIM phase_us;

    etsim_thread_t thread;
    volatile int running;
    volatile int started;
} shim_cyclic_t;

typedef struct {
    ID     id;
    uint_t count;
    uint_t max_count;
    int    used;
} shim_sem_t;

static shim_task_t   g_tasks[MAX_TASKS];
static shim_cyclic_t g_cyclics[MAX_CYCLICS];
static shim_sem_t    g_sems[MAX_SEMAPHORES];

static int g_task_count;
static int g_cyclic_count;
static int g_sem_count;

static etsim_mutex_t g_lock;
static etsim_cond_t  g_cond;
static int           g_lock_ready;

/*
 * CPU ロック用。実機では loc_cpu() が割込みを止めるので、その区間は
 * どのタスクからも割り込まれない。PC ではタスクが OS スレッドなので、
 * 何もしないと loc_cpu() で守っているつもりの区間が普通に競合する。
 *
 * 実際、走行プログラムのログ用キュー (LOG.cpp) はこれで壊れていた。
 * 生産側 (TRACER_TASK) と消費側 (LOG_TASK) が head/count を同時に触り、
 * 消費側が同じ要素を延々と読み続けてテレメトリが更新されなくなる。
 *
 * ここでは 1 本のミューテックスで代用する。実機と同じ「他のタスクが
 * 入ってこない」という性質だけを満たす。
 * ＜制約＞ 入れ子で呼ばないこと。ロックしたまま待ちに入らないこと。
 */
static etsim_mutex_t g_cpu_lock;

static void ensure_lock(void)
{
    if (g_lock_ready) return;
    ETSIM_MUTEX_INIT(&g_lock);
    ETSIM_MUTEX_INIT(&g_cpu_lock);
    ETSIM_COND_INIT(&g_cond);
    g_lock_ready = 1;
}

/* ---------------------------------------------------------------- */
/* syslog                                                            */
/* ---------------------------------------------------------------- */

void etsim_syslog(int priority, const char *format, ...)
{
    static const char *names[] = {
        "EMERG", "ALERT", "CRIT", "ERROR", "WARN", "NOTICE", "INFO", "DEBUG"
    };
    const char *name = (priority >= 0 && priority <= 7) ? names[priority] : "?";

    va_list ap;
    va_start(ap, format);

    printf("[%-6s] ", name);
    vprintf(format, ap);
    printf("\n");
    fflush(stdout);

    va_end(ap);
}

/* ---------------------------------------------------------------- */
/* 登録                                                              */
/* ---------------------------------------------------------------- */

static shim_task_t *find_task(ID tskid)
{
    for (int i = 0; i < g_task_count; i++)
        if (g_tasks[i].id == tskid) return &g_tasks[i];
    return NULL;
}

static shim_cyclic_t *find_cyclic(ID cycid)
{
    for (int i = 0; i < g_cyclic_count; i++)
        if (g_cyclics[i].id == cycid) return &g_cyclics[i];
    return NULL;
}

static shim_sem_t *find_sem(ID semid)
{
    for (int i = 0; i < g_sem_count; i++)
        if (g_sems[i].id == semid && g_sems[i].used) return &g_sems[i];
    return NULL;
}

ER etsim_register_task(ID tskid, void (*entry)(intptr_t), intptr_t exinf)
{
    ensure_lock();

    if (entry == NULL) return E_PAR;
    if (find_task(tskid) != NULL) return E_OBJ;
    if (g_task_count >= MAX_TASKS) return E_NOID;

    shim_task_t *t = &g_tasks[g_task_count++];
    memset(t, 0, sizeof(*t));
    t->id = tskid;
    t->entry = entry;
    t->exinf = exinf;
    t->thread = ETSIM_THREAD_NULL;
    return E_OK;
}

ER etsim_register_cyclic(ID cycid, void (*handler)(intptr_t), intptr_t exinf,
                         RELTIM period_us, RELTIM phase_us)
{
    ensure_lock();

    if (handler == NULL || period_us == 0) return E_PAR;
    if (find_cyclic(cycid) != NULL) return E_OBJ;
    if (g_cyclic_count >= MAX_CYCLICS) return E_NOID;

    shim_cyclic_t *c = &g_cyclics[g_cyclic_count++];
    memset(c, 0, sizeof(*c));
    c->id = cycid;
    c->handler = handler;
    c->exinf = exinf;
    c->period_us = period_us;
    c->phase_us = phase_us;
    c->thread = ETSIM_THREAD_NULL;
    return E_OK;
}

ER etsim_register_semaphore(ID semid, uint_t initial_count, uint_t max_count)
{
    ensure_lock();

    if (find_sem(semid) != NULL) return E_OBJ;
    if (g_sem_count >= MAX_SEMAPHORES) return E_NOID;
    if (max_count == 0) max_count = 1;
    if (initial_count > max_count) return E_PAR;

    shim_sem_t *s = &g_sems[g_sem_count++];
    s->id = semid;
    s->count = initial_count;
    s->max_count = max_count;
    s->used = 1;
    return E_OK;
}

/* ---------------------------------------------------------------- */
/* タスク                                                            */
/* ---------------------------------------------------------------- */

static ETSIM_THREAD_RET task_thread_main(ETSIM_THREAD_ARG arg)
{
    shim_task_t *t = (shim_task_t *)arg;
    t->entry(t->exinf);
    t->active = 0;
    ETSIM_THREAD_DONE;
}

ER act_tsk(ID tskid)
{
    shim_task_t *t = find_task(tskid);
    if (t == NULL) return E_NOEXS;
    if (t->active) return E_QOVR; /* 既に走っている */

    t->active = 1;
    t->terminate = 0;
    t->wakeup = 0;
    t->released = 0;

    if (!etsim_thread_start(&t->thread, task_thread_main, t)) {
        t->active = 0;
        return E_SYS;
    }
    return E_OK;
}

ER ext_tsk(void)
{
    /*
     * 実機ではここで戻らないが、PC ではスレッド関数から抜けてもらう必要がある。
     * タスク本体の末尾で呼ぶ限り挙動は同じ。途中で呼ぶ使い方には対応しない。
     */
    return E_OK;
}

ER ter_tsk(ID tskid)
{
    shim_task_t *t = find_task(tskid);
    if (t == NULL) return E_NOEXS;

    /* 強制終了はしない。フラグを立てて自発的に抜けてもらう。 */
    t->terminate = 1;
    t->released = 1;

    ETSIM_MUTEX_LOCK(&g_lock);
    ETSIM_COND_BROADCAST(&g_cond);
    ETSIM_MUTEX_UNLOCK(&g_lock);
    return E_OK;
}

ER get_tid(ID *p_tskid)
{
    if (p_tskid == NULL) return E_PAR;
    *p_tskid = 0; /* PC 側では呼び出し元タスクを特定しない */
    return E_OK;
}

ER chg_pri(ID tskid, PRI tskpri) { (void)tskid; (void)tskpri; return E_OK; }

ER get_pri(ID tskid, PRI *p_tskpri)
{
    (void)tskid;
    if (p_tskpri == NULL) return E_PAR;
    *p_tskpri = 1;
    return E_OK;
}

/* 起床待ち。wup_tsk か rel_wai で解除される。 */
static ER sleep_task(shim_task_t *t, TMO tmout)
{
    if (t == NULL) {
        /* 登録されていないスレッドからの slp_tsk。時間だけ進めて戻る。 */
        etsim_advance_time(tmout > 0 ? (uint32_t)tmout : 1000);
        return E_OK;
    }

    uint64_t deadline = (tmout > 0)
        ? etsim_wall_clock_us() + (uint64_t)tmout
        : 0;

    for (;;) {
        ETSIM_MUTEX_LOCK(&g_lock);
        if (t->wakeup > 0) {
            t->wakeup--;
            ETSIM_MUTEX_UNLOCK(&g_lock);
            return E_OK;
        }
        if (t->released) { t->released = 0; ETSIM_MUTEX_UNLOCK(&g_lock); return E_RLWAI; }
        ETSIM_MUTEX_UNLOCK(&g_lock);

        if (etsim_should_exit() || t->terminate) return E_RLWAI;
        if (deadline != 0 && etsim_wall_clock_us() >= deadline) return E_TMOUT;

        /* 待っている間もシミュレータの時間は進める */
        etsim_advance_time(1000);
    }
}

ER slp_tsk(void)
{
    /* 呼び出し元タスクが特定できないので、単一タスク構成を前提にする。
     * 複数タスクで slp_tsk を使う場合はセマフォへ置き換えること。 */
    return sleep_task(g_task_count > 0 ? &g_tasks[0] : NULL, TMO_FEVR);
}

ER tslp_tsk(TMO tmout)
{
    if (tmout == TMO_POL) return E_TMOUT;
    if (tmout == TMO_FEVR) return slp_tsk();

    etsim_device_update((uint32_t)tmout);
    etsim_advance_time((uint32_t)tmout);
    return E_TMOUT;
}

ER wup_tsk(ID tskid)
{
    shim_task_t *t = find_task(tskid);
    if (t == NULL) return E_NOEXS;

    ETSIM_MUTEX_LOCK(&g_lock);
    t->wakeup++;
    ETSIM_COND_BROADCAST(&g_cond);
    ETSIM_MUTEX_UNLOCK(&g_lock);
    return E_OK;
}

ER can_wup(ID tskid)
{
    shim_task_t *t = find_task(tskid);
    if (t == NULL) return E_NOEXS;

    ETSIM_MUTEX_LOCK(&g_lock);
    int n = t->wakeup;
    t->wakeup = 0;
    ETSIM_MUTEX_UNLOCK(&g_lock);
    return (ER)n;
}

ER rel_wai(ID tskid)
{
    shim_task_t *t = find_task(tskid);
    if (t == NULL) return E_NOEXS;

    ETSIM_MUTEX_LOCK(&g_lock);
    t->released = 1;
    ETSIM_COND_BROADCAST(&g_cond);
    ETSIM_MUTEX_UNLOCK(&g_lock);
    return E_OK;
}

ER dly_tsk(RELTIM dlytim)
{
    /* 速度制御の閉ループをここで回してから時間を進める。
     * 実機ではファームウェアが裏で回している部分に相当する。 */
    etsim_device_update(dlytim);
    etsim_advance_time(dlytim);
    return etsim_should_exit() ? E_RLWAI : E_OK;
}

/* ---------------------------------------------------------------- */
/* 周期ハンドラ                                                      */
/* ---------------------------------------------------------------- */

static ETSIM_THREAD_RET cyclic_thread_main(ETSIM_THREAD_ARG arg)
{
    shim_cyclic_t *c = (shim_cyclic_t *)arg;

    if (c->phase_us > 0) etsim_sleep_us(c->phase_us);

    uint64_t next = etsim_wall_clock_us();
    while (c->running && !etsim_should_exit()) {
        c->handler(c->exinf);

        next += c->period_us;
        uint64_t now = etsim_wall_clock_us();
        if (next > now) {
            etsim_sleep_us(next - now);
        } else {
            /* 処理が周期に間に合わなかった。基準を打ち直して詰まりを防ぐ。 */
            next = now;
        }
    }

    c->running = 0;
    ETSIM_THREAD_DONE;
}

ER sta_cyc(ID cycid)
{
    shim_cyclic_t *c = find_cyclic(cycid);
    if (c == NULL) return E_NOEXS;
    if (c->running) return E_OK;

    c->running = 1;
    if (!etsim_thread_start(&c->thread, cyclic_thread_main, c)) {
        c->running = 0;
        return E_SYS;
    }
    c->started = 1;
    return E_OK;
}

ER stp_cyc(ID cycid)
{
    shim_cyclic_t *c = find_cyclic(cycid);
    if (c == NULL) return E_NOEXS;
    c->running = 0;
    return E_OK;
}

/* ---------------------------------------------------------------- */
/* セマフォ                                                          */
/* ---------------------------------------------------------------- */

static ER wait_sem_impl(ID semid, TMO tmout)
{
    ensure_lock();

    shim_sem_t *s = find_sem(semid);
    if (s == NULL) return E_NOEXS;

    uint64_t deadline = (tmout > 0) ? etsim_wall_clock_us() + (uint64_t)tmout : 0;

    ETSIM_MUTEX_LOCK(&g_lock);
    for (;;) {
        if (s->count > 0) {
            s->count--;
            ETSIM_MUTEX_UNLOCK(&g_lock);
            return E_OK;
        }
        if (tmout == TMO_POL) {
            ETSIM_MUTEX_UNLOCK(&g_lock);
            return E_TMOUT;
        }
        if (deadline != 0 && etsim_wall_clock_us() >= deadline) {
            ETSIM_MUTEX_UNLOCK(&g_lock);
            return E_TMOUT;
        }
        if (etsim_should_exit()) {
            ETSIM_MUTEX_UNLOCK(&g_lock);
            return E_RLWAI;
        }
        etsim_cond_wait_ms(&g_cond, &g_lock, 20);
    }
}

ER wai_sem(ID semid)            { return wait_sem_impl(semid, TMO_FEVR); }
ER pol_sem(ID semid)            { return wait_sem_impl(semid, TMO_POL); }
ER twai_sem(ID semid, TMO tmout) { return wait_sem_impl(semid, tmout); }

ER sig_sem(ID semid)
{
    ensure_lock();

    shim_sem_t *s = find_sem(semid);
    if (s == NULL) return E_NOEXS;

    ETSIM_MUTEX_LOCK(&g_lock);
    if (s->count >= s->max_count) {
        ETSIM_MUTEX_UNLOCK(&g_lock);
        return E_QOVR;
    }
    s->count++;
    ETSIM_COND_BROADCAST(&g_cond);
    ETSIM_MUTEX_UNLOCK(&g_lock);
    return E_OK;
}

/* ---------------------------------------------------------------- */
/* 時間・その他                                                      */
/* ---------------------------------------------------------------- */

ER get_tim(SYSTIM *p_systim)
{
    if (p_systim == NULL) return E_PAR;
    *p_systim = etsim_now_us();
    return E_OK;
}

ER loc_cpu(void)
{
    ensure_lock();
    ETSIM_MUTEX_LOCK(&g_cpu_lock);
    return E_OK;
}

ER unl_cpu(void)
{
    ETSIM_MUTEX_UNLOCK(&g_cpu_lock);
    return E_OK;
}

/* ディスパッチ禁止も同じ扱いにする。狙いは「他のタスクを入れない」こと。 */
ER dis_dsp(void) { return loc_cpu(); }
ER ena_dsp(void) { return unl_cpu(); }

bool_t sns_ker(void) { return FALSE; }
bool_t sns_ctx(void) { return FALSE; }
bool_t sns_dpn(void) { return FALSE; }

void etsim_kernel_shutdown(void)
{
    for (int i = 0; i < g_cyclic_count; i++) {
        g_cyclics[i].running = 0;
    }
    for (int i = 0; i < g_task_count; i++) {
        g_tasks[i].terminate = 1;
        g_tasks[i].released = 1;
    }

    if (g_lock_ready) {
        ETSIM_MUTEX_LOCK(&g_lock);
        ETSIM_COND_BROADCAST(&g_cond);
        ETSIM_MUTEX_UNLOCK(&g_lock);
    }

    for (int i = 0; i < g_cyclic_count; i++) {
        if (g_cyclics[i].started) etsim_thread_join(&g_cyclics[i].thread, 1000);
        g_cyclics[i].started = 0;
    }
    for (int i = 0; i < g_task_count; i++) {
        if (g_tasks[i].thread != ETSIM_THREAD_NULL) etsim_thread_join(&g_tasks[i].thread, 1000);
    }
}
