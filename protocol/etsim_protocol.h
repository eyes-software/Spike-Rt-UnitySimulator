/*
 * etsim_protocol.h
 *
 * ETロボコン Unity シミュレータ <-> 制御プロセス 間の通信プロトコル定義。
 *
 * このヘッダは C 側 (制御プロセス) と C# 側 (Unity) で共有する唯一の仕様であり、
 * C# 側の実装 (Assets/Scripts/Net/EtsimPacket.cs) は本ファイルのオフセットに
 * 1 バイト単位で一致させること。末尾の _Static_assert がサイズを保証する。
 *
 * 通信は localhost UDP。
 *   - Unity がサーバ (既定 127.0.0.1:54001) で待ち受ける。
 *   - 制御プロセスは任意のポートからアクチュエータパケットを送る。
 *   - Unity は「最後にアクチュエータパケットが届いた送信元」へセンサパケットを返す。
 *     したがって制御プロセス側で受信ポートを決め打ちする必要はない。
 *
 * 動作モードは 2 つ。
 *   FREERUN : Unity は FixedUpdate ごとにセンサパケットを送る。制御プロセスは
 *             最新値を読み捨て方式で受け取る。実機の挙動に近い。
 *   LOCKSTEP: 制御プロセスがアクチュエータを送るたびに Unity が物理を規定ステップ
 *             進めてセンサを返す。再現性が要る回帰テスト向け。
 *             制御プロセスが ETSIM_AFLAG_LOCKSTEP を立てると Unity が切り替わる。
 */
#ifndef ETSIM_PROTOCOL_H
#define ETSIM_PROTOCOL_H

#include <stdint.h>

#define ETSIM_PROTOCOL_VERSION   1

/* 'E','T','S','S' / 'E','T','S','A' (リトルエンディアン前提) */
#define ETSIM_MAGIC_SENSOR       0x53535445u
#define ETSIM_MAGIC_ACTUATOR     0x41535445u

#define ETSIM_DEFAULT_SIM_PORT   54001
#define ETSIM_DEFAULT_SIM_HOST   "127.0.0.1"

/* センサパケットの flags */
#define ETSIM_SFLAG_RUNNING      0x0001u  /* 計測走行中 */
#define ETSIM_SFLAG_LOCKSTEP     0x0002u  /* シミュレータがロックステップ動作中 */
#define ETSIM_SFLAG_GOAL         0x0004u  /* ゴールライン通過済み */
#define ETSIM_SFLAG_OUT_OF_COURSE 0x0008u /* コースアウト判定 */
#define ETSIM_SFLAG_RESET_DONE   0x0010u  /* リセット要求を処理した */

/* アクチュエータパケットの flags */
#define ETSIM_AFLAG_LOCKSTEP     0x0001u  /* ロックステップを要求 */
#define ETSIM_AFLAG_RESET        0x0002u  /* 走行体を初期姿勢へ戻す */
#define ETSIM_AFLAG_SHUTDOWN     0x0004u  /* 制御プロセス終了通知 */
#define ETSIM_AFLAG_START        0x0008u  /* 計測開始 (スタートボタン相当) */

/* モータの停止モード / 指令モード */
#define ETSIM_MOTOR_MODE_COAST   0
#define ETSIM_MOTOR_MODE_BRAKE   1
#define ETSIM_MOTOR_MODE_HOLD    2
#define ETSIM_MOTOR_MODE_POWER   3

/* motor_power[] / motor_count[] のインデックス */
#define ETSIM_MOTOR_LEFT         0
#define ETSIM_MOTOR_RIGHT        1
#define ETSIM_MOTOR_ARM          2
#define ETSIM_MOTOR_NUM          3

/* button_state のビット */
#define ETSIM_BUTTON_CENTER      0x01u
#define ETSIM_BUTTON_LEFT        0x02u
#define ETSIM_BUTTON_RIGHT       0x04u
#define ETSIM_BUTTON_BLUETOOTH   0x08u

#pragma pack(push, 1)

/* ---------------------------------------------------------------- */
/* Unity -> 制御プロセス : センサ値                                   */
/* ---------------------------------------------------------------- */
typedef struct {
    uint32_t magic;            /*   0 : ETSIM_MAGIC_SENSOR */
    uint16_t version;          /*   4 */
    uint16_t flags;            /*   6 : ETSIM_SFLAG_* */
    uint32_t seq;              /*   8 : シミュレータ側連番 */
    uint32_t ack_seq;          /*  12 : 反映済みアクチュエータ seq */
    uint32_t sim_time_us;      /*  16 : シミュレーション時刻 [us] */

    /* カラーセンサ */
    uint8_t  color_id;         /*  20 : pup_color_t 相当 (下記 ETSIM_COLOR_*) */
    uint8_t  _pad0[3];         /*  21 */
    int32_t  reflection;       /*  24 : 0..100 */
    int32_t  ambient;          /*  28 : 0..100 */
    uint16_t rgb[3];           /*  32 : 0..1024 (SPIKE 生値レンジ) */
    uint16_t hsv_h;            /*  38 : 0..359 [deg] */
    uint8_t  hsv_s;            /*  40 : 0..100 [%] */
    uint8_t  hsv_v;            /*  41 : 0..100 [%] */
    uint8_t  _pad1[2];         /*  42 */

    /* 超音波センサ */
    int32_t  ultrasonic_mm;    /*  44 : -1 = 検出範囲外 */
    uint8_t  ultrasonic_presence; /* 48 : 他デバイスの発光検出 */
    /* フォースセンサ */
    uint8_t  force_pressed;    /*  49 */
    uint8_t  _pad2[2];         /*  50 */
    float    force_newton;     /*  52 */

    /* ハブ IMU (yaw は反時計回り正、走行体前方 = +X ローカル) */
    float    yaw_deg;          /*  56 */
    float    pitch_deg;        /*  60 */
    float    roll_deg;         /*  64 */
    float    gyro_dps[3];      /*  68 : x,y,z 角速度 [deg/s] */
    float    accel_mss[3];     /*  80 : x,y,z 加速度 [m/s^2] */

    /* モータ : [0]=左 [1]=右 [2]=アーム */
    int32_t  motor_count[3];   /*  92 : 積算回転角 [deg] */
    int32_t  motor_speed[3];   /* 104 : 回転速度 [deg/s] */
    int32_t  motor_power[3];   /* 116 : 実際に印加された出力 -100..100 */

    /* ハブ */
    uint8_t  button_state;     /* 128 : ETSIM_BUTTON_* */
    uint8_t  _pad3[3];         /* 129 */
    int32_t  battery_mv;       /* 132 */

    /* 真値 : 実機では取得不可。デバッグ表示・自動採点用 */
    float    truth_x_mm;       /* 136 : コース座標系 X [mm] */
    float    truth_y_mm;       /* 140 : コース座標系 Y [mm] */
    float    truth_heading_deg;/* 144 : コース座標系 方位 [deg] */
    float    truth_speed_mmps; /* 148 : 前後方向速度 [mm/s] */
} etsim_sensor_packet_t;       /* = 152 bytes */

/* ---------------------------------------------------------------- */
/* 制御プロセス -> Unity : アクチュエータ指令                          */
/* ---------------------------------------------------------------- */
typedef struct {
    uint32_t magic;            /*   0 : ETSIM_MAGIC_ACTUATOR */
    uint16_t version;          /*   4 */
    uint16_t flags;            /*   6 : ETSIM_AFLAG_* */
    uint32_t seq;              /*   8 : 制御プロセス側連番 */
    uint32_t ctrl_time_us;     /*  12 : 制御プロセス時刻 [us] */

    int32_t  motor_power[3];   /*  16 : -100..100 */
    uint8_t  motor_mode[3];    /*  28 : ETSIM_MOTOR_MODE_* */
    uint8_t  reset_count_mask; /*  31 : bit0..2 を立てた軸の積算角をゼロ化 */
    uint8_t  led_rgb[3];       /*  32 : ハブ LED */
    uint8_t  reset_imu;        /*  35 : 非 0 でヨー角をゼロ化 */
    uint8_t  display[5];       /*  36 : ハブ 5x5 表示の簡易チャンネル */
    uint8_t  _pad0[3];         /*  41 */
} etsim_actuator_packet_t;     /* = 44 bytes */

#pragma pack(pop)

/* pup_color_t に対応する色 ID */
#define ETSIM_COLOR_NONE     0
#define ETSIM_COLOR_BLACK    1
#define ETSIM_COLOR_GRAY     2
#define ETSIM_COLOR_WHITE    3
#define ETSIM_COLOR_RED      4
#define ETSIM_COLOR_ORANGE   5
#define ETSIM_COLOR_YELLOW   6
#define ETSIM_COLOR_GREEN    7
#define ETSIM_COLOR_CYAN     8
#define ETSIM_COLOR_BLUE     9
#define ETSIM_COLOR_VIOLET   10
#define ETSIM_COLOR_MAGENTA  11

#if defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
_Static_assert(sizeof(etsim_sensor_packet_t) == 152,
               "etsim_sensor_packet_t のレイアウトが C# 側と一致しません");
_Static_assert(sizeof(etsim_actuator_packet_t) == 44,
               "etsim_actuator_packet_t のレイアウトが C# 側と一致しません");
#endif

#endif /* ETSIM_PROTOCOL_H */
