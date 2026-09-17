/*
 * kernel.h : TOPPERS/ASP3 カーネル API の PC 向け shim。
 *
 * SPIKE-RT のアプリは ASP3 のタスク・周期ハンドラ・セマフォを使って書かれている。
 * それを PC で動かすため、ここでは各オブジェクトを OS スレッドへ写している。
 *
 * ＜実機との違い＞
 *  - 優先度ベースの厳密なスケジューリングはしない。OS のスケジューラに任せる。
 *    優先度に依存した排他（高優先度タスクが動いている間は低優先度が走らない）を
 *    前提にしたコードは、シミュレータでは競合し得る。セマフォで明示的に守ること。
 *  - 静的 API (.cfg) は処理しない。CRE_TSK / CRE_CYC / CRE_SEM に相当する登録を
 *    etsim_register_task() などで実行時に行う。app 側の etrobo_app.c を参照。
 *  - RELTIM の単位は ASP3 と同じくマイクロ秒。
 */
#ifndef ETSIM_KERNEL_H
#define ETSIM_KERNEL_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* --- 基本型 (TOPPERS 共通定義に準拠) ------------------------------- */

typedef signed int    int_t;
typedef unsigned int  uint_t;
typedef int_t         bool_t;
typedef int_t         ER;
typedef int_t         ID;
typedef uint_t        ATR;
typedef uint_t        STAT;
typedef uint_t        MODE;
typedef int_t         PRI;
typedef int_t         TMO;
typedef uint_t        RELTIM;   /* 相対時間 [us] */
typedef uint64_t      SYSTIM;   /* システム時刻 [us] */
typedef int_t         ER_BOOL;
typedef int_t         ER_ID;
typedef int_t         ER_UINT;
typedef void         *VP;
typedef void (*FP)(void);

#ifndef TRUE
#  define TRUE  1
#endif
#ifndef FALSE
#  define FALSE 0
#endif

#define TMO_POL   0
#define TMO_FEVR  (-1)
#define TMO_NBLK  (-2)

#define TA_NULL   0U

/* --- エラーコード -------------------------------------------------- */

#define E_OK      0
#define E_SYS     (-5)
#define E_NOSPT   (-9)
#define E_RSFN    (-10)
#define E_RSATR   (-11)
#define E_PAR     (-17)
#define E_ID      (-18)
#define E_CTX     (-25)
#define E_MACV    (-26)
#define E_OACV    (-27)
#define E_ILUSE   (-28)
#define E_NOMEM   (-33)
#define E_NOID    (-34)
#define E_NORES   (-35)
#define E_OBJ     (-41)
#define E_NOEXS   (-42)
#define E_QOVR    (-43)
#define E_RLWAI   (-49)
#define E_TMOUT   (-50)
#define E_DLT     (-51)
#define E_CLS     (-52)
#define E_WBLK    (-57)
#define E_BOVR    (-58)

/* --- タスク -------------------------------------------------------- */

ER act_tsk(ID tskid);
ER ext_tsk(void);
ER ter_tsk(ID tskid);
ER slp_tsk(void);
ER tslp_tsk(TMO tmout);
ER wup_tsk(ID tskid);
ER can_wup(ID tskid);
ER rel_wai(ID tskid);
ER dly_tsk(RELTIM dlytim);
ER chg_pri(ID tskid, PRI tskpri);
ER get_pri(ID tskid, PRI *p_tskpri);
ER get_tid(ID *p_tskid);

/* --- 周期ハンドラ -------------------------------------------------- */

ER sta_cyc(ID cycid);
ER stp_cyc(ID cycid);

/* --- セマフォ ------------------------------------------------------ */

ER wai_sem(ID semid);
ER pol_sem(ID semid);
ER twai_sem(ID semid, TMO tmout);
ER sig_sem(ID semid);

/* --- 時間 ---------------------------------------------------------- */

ER get_tim(SYSTIM *p_systim);

/* --- 割込み・CPU ロック -------------------------------------------- */
/* PC では実割込みが無いので、いずれも何もしないで E_OK を返す。
 * これらで排他を取っているコードはシミュレータでは保護されないため、
 * セマフォへ置き換える必要がある。                                   */

ER loc_cpu(void);
ER unl_cpu(void);
ER dis_dsp(void);
ER ena_dsp(void);
bool_t sns_ker(void);
bool_t sns_ctx(void);
bool_t sns_dpn(void);

/* --- シミュレータ固有の登録 API ------------------------------------ */
/*
 * .cfg で静的に作っていたオブジェクトを、起動時にここで登録する。
 * ID はアプリ側のヘッダ (kernel_cfg.h 相当) と同じ値を使うこと。
 */

ER etsim_register_task(ID tskid, void (*entry)(intptr_t), intptr_t exinf);
ER etsim_register_cyclic(ID cycid, void (*handler)(intptr_t), intptr_t exinf,
                         RELTIM period_us, RELTIM phase_us);
ER etsim_register_semaphore(ID semid, uint_t initial_count, uint_t max_count);

/*
 * 静的 API (app.cfg の CRE_TSK / CRE_CYC) の代わり。
 *
 * 実機ではカーネルコンフィギュレータが app.cfg を読んでタスクや周期ハンドラを
 * 作るが、PC ではその工程が無い。そこで main() が main_task を呼ぶ前に
 * この関数を 1 回呼ぶので、走行プログラムごとに 1 つだけ定義して、
 * 中で etsim_register_task() / etsim_register_cyclic() を呼ぶ。
 * 追加のタスクを使わない走行プログラムでは空でよい。
 */
void etsim_app_register_objects(void);

/* すべての周期ハンドラとタスクを止めて後始末する。main() が呼ぶ。 */
void etsim_kernel_shutdown(void);

/* --- syslog -------------------------------------------------------- */

#define LOG_EMERG   0
#define LOG_ALERT   1
#define LOG_CRIT    2
#define LOG_ERROR   3
#define LOG_WARNING 4
#define LOG_NOTICE  5
#define LOG_INFO    6
#define LOG_DEBUG   7

void etsim_syslog(int priority, const char *format, ...);

#define syslog(prio, ...) etsim_syslog((prio), __VA_ARGS__)

/* サービスコールの戻り値を確認するための TOPPERS 定番マクロ */
#define SVC_PERROR(expr)                                                   \
    do {                                                                   \
        ER etsim_svc_err_ = (ER)(expr);                                    \
        if (etsim_svc_err_ < 0) {                                          \
            etsim_syslog(LOG_ERROR, "%s (%d) reported at %s:%d",           \
                         #expr, (int)etsim_svc_err_, __FILE__, __LINE__);  \
        }                                                                  \
    } while (0)

/* アプリ側のエントリ。main() から呼ばれる。 */
void main_task(intptr_t exinf);

#ifdef __cplusplus
}
#endif

#endif /* ETSIM_KERNEL_H */
