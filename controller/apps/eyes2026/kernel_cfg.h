/*
 * kernel_cfg.h : EYES ETロボコン 2026 (m-sugimoto-eyes/eyes-et2026) 用。
 *
 * 実機では app.cfg からカーネルコンフィギュレータが生成するファイル。
 * PC ではその工程が無いので、app.cfg の CRE_TSK / CRE_CYC に書かれている
 * オブジェクトと同じ名前・同じ並びで ID を振ってある。
 *
 * app.cfg 側:
 *   CRE_TSK(MAIN_TASK,   {TA_ACT,  0, main_task,   MAIN_PRIORITY,    ...});
 *   CRE_TSK(TRACER_TASK, {TA_NULL, 0, tracer_task, TRACER_PRIORITY,  ...});
 *   CRE_TSK(LOG_TASK,    {TA_NULL, 0, log_task,    TRACER_PRIORITY+1,...});
 *   CRE_CYC(TRACER_CYC,  {TA_NULL, {TNFY_ACTTSK, TRACER_TASK}, 10*1000, 1*1000});
 */

#ifndef ETSIM_KERNEL_CFG_H
#define ETSIM_KERNEL_CFG_H

#define MAIN_TASK    1
#define TRACER_TASK  2
#define LOG_TASK     3

#define TRACER_CYC   1

#endif /* ETSIM_KERNEL_CFG_H */
