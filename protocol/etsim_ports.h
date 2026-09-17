/*
 * etsim_ports.h : 既定のポート割り当て。
 *
 * ETロボコン2026 走行体規定書 M6.2「４ ケーブルの配線」の配線例。
 * 規定書にも「この配線方法は一例です」とあるので、実機の配線が違えば
 * 環境変数 ETSIM_PORT_* で変えられる。
 *
 * このファイルが存在する理由:
 *   シミュレータ側 (controller/src/etsim_device.c) とアプリ側
 *   (controller/app/etrobo_app.c) がそれぞれ別にポートを決めていると、
 *   片方だけ直したときに「モータは見つかるのに役割が入れ替わる」という
 *   気づきにくい壊れ方をする。アームモータを左輪として回すので、
 *   走行体はその場で回るだけになる。
 *   アプリ側はシミュレータ向けビルドのときだけこのヘッダを取り込み、
 *   _Static_assert で食い違いをコンパイル時に落とす。
 */

#ifndef ETSIM_PORTS_H
#define ETSIM_PORTS_H

#define ETSIM_DEFAULT_PORT_RIGHT       'A'
#define ETSIM_DEFAULT_PORT_LEFT        'B'
#define ETSIM_DEFAULT_PORT_ARM         'C'
#define ETSIM_DEFAULT_PORT_FORCE       'D'
#define ETSIM_DEFAULT_PORT_COLOR       'E'
#define ETSIM_DEFAULT_PORT_ULTRASONIC  'F'

#endif /* ETSIM_PORTS_H */
