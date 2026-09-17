/*
 * pbio/color.h : 色型（PC シミュレータ用）。
 * 構造体のメンバ並びと PBIO_COLOR_ENCODE の式は上流と同一。
 */
#ifndef ETSIM_PBIO_COLOR_H
#define ETSIM_PBIO_COLOR_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>

typedef struct {
    uint8_t r;
    uint8_t g;
    uint8_t b;
} pbio_color_rgb_t;

typedef struct {
    uint16_t h;  /* 0..359 [deg] */
    uint8_t  s;  /* 0..100 [%]   */
    int8_t   v;  /* 0..100 [%]。負値も許容される */
} pbio_color_hsv_t;

#define PBIO_COLOR_ENCODE(h, s, v) (((h / 30) << 3) | ((s / 51) << 2) | (v / 34))

typedef enum {
    PBIO_COLOR_NONE         = PBIO_COLOR_ENCODE(180, 0, 0),
    PBIO_COLOR_BLACK        = PBIO_COLOR_ENCODE(0, 0, 0),
    PBIO_COLOR_GRAY         = PBIO_COLOR_ENCODE(0, 0, 50),
    PBIO_COLOR_WHITE        = PBIO_COLOR_ENCODE(0, 0, 100),
    PBIO_COLOR_RED          = PBIO_COLOR_ENCODE(0, 100, 100),
    PBIO_COLOR_BROWN        = PBIO_COLOR_ENCODE(30, 100, 50),
    PBIO_COLOR_ORANGE       = PBIO_COLOR_ENCODE(30, 100, 100),
    PBIO_COLOR_YELLOW       = PBIO_COLOR_ENCODE(60, 100, 100),
    PBIO_COLOR_GREEN        = PBIO_COLOR_ENCODE(120, 100, 100),
    PBIO_COLOR_SPRING_GREEN = PBIO_COLOR_ENCODE(150, 100, 100),
    PBIO_COLOR_CYAN         = PBIO_COLOR_ENCODE(180, 100, 100),
    PBIO_COLOR_BLUE         = PBIO_COLOR_ENCODE(240, 100, 100),
    PBIO_COLOR_VIOLET       = PBIO_COLOR_ENCODE(270, 100, 100),
    PBIO_COLOR_MAGENTA      = PBIO_COLOR_ENCODE(300, 100, 100)
} pbio_color_t;

void pbio_color_rgb_to_hsv(const pbio_color_rgb_t *rgb, pbio_color_hsv_t *hsv);
void pbio_color_hsv_to_rgb(const pbio_color_hsv_t *hsv, pbio_color_rgb_t *rgb);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_PBIO_COLOR_H */
