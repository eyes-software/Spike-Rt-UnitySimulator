/*
 * etsim_app_config.c : EYES ETロボコン 2026 の app.cfg に相当する登録。
 *
 * 走行プログラム (m-sugimoto-eyes/eyes-et2026) には一切手を入れない。
 * 実機のカーネルコンフィギュレータがやっていたことを、ここで実行時に行う。
 */

#include <kernel.h>

#include "kernel_cfg.h"

/* app.cpp が定義しているタスク。app.h の中で extern "C" になっている。 */
extern void main_task(intptr_t exinf);
extern void tracer_task(intptr_t exinf);
extern void log_task(intptr_t exinf);
extern void sonar_task(intptr_t exinf);

/*
 * CRE_CYC の通知方法が TNFY_ACTTSK なので、周期ハンドラは
 * 「タスクを起こす」だけ。ハンドラ本体で走行処理をするわけではない。
 */
static void tracer_cyc_handler(intptr_t exinf)
{
    (void)exinf;
    act_tsk(TRACER_TASK);
}

void etsim_app_register_objects(void)
{
    /* MAIN_TASK は TA_ACT だが、main() が main_task を直接呼ぶので登録だけ。 */
    etsim_register_task(MAIN_TASK,   main_task,   0);
    etsim_register_task(TRACER_TASK, tracer_task, 0);
    etsim_register_task(LOG_TASK,    log_task,    0);
    /* SonarProc が act_tsk(SONAR_TASK) で起こす。ET相撲で使う。 */
    etsim_register_task(SONAR_TASK,  sonar_task,  0);

    /* 周期 10ms、位相 1ms。app.cfg の値をそのまま使う。 */
    etsim_register_cyclic(TRACER_CYC, tracer_cyc_handler, 0, 10 * 1000, 1 * 1000);
}
