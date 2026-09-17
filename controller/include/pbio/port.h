/*
 * pbio/port.h : ポート ID（PC シミュレータ用）。
 * 実機と同じく ASCII 文字そのものを値に使う。
 */
#ifndef ETSIM_PBIO_PORT_H
#define ETSIM_PBIO_PORT_H

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    PBIO_PORT_ID_A = 'A',
    PBIO_PORT_ID_B = 'B',
    PBIO_PORT_ID_C = 'C',
    PBIO_PORT_ID_D = 'D',
    PBIO_PORT_ID_E = 'E',
    PBIO_PORT_ID_F = 'F'
} pbio_port_id_t;


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_PBIO_PORT_H */
