/*
 * pbio/geometry.h : 直方体の面の向き（PC シミュレータ用）。
 *
 * 上流では spike/hub/display.h が <pbio/light_matrix.h> 経由でこれを取り込む。
 * 公式の C++ ラッパ Display.h が PBIO_SIDE_TOP などを直接使うので、
 * 名前と値を上流と同じに置いておく。
 *
 * 値は pbio の軸エンコード (符号 << 2) | 軸 で、pybricks-micropython の
 * lib/pbio/include/pbio/geometry.h と同じ（そちらは後に
 * PBIO_GEOMETRY_SIDE_* へ改名されたが、式は変わっていない）。
 * シミュレータはディスプレイを模擬しないので値そのものは使わないが、
 * 実機と食い違う定数を置くと後で紛れるため合わせてある。
 */
#ifndef ETSIM_PBIO_GEOMETRY_H
#define ETSIM_PBIO_GEOMETRY_H

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    PBIO_SIDE_FRONT  = (0 << 2) | 0,  /* +X */
    PBIO_SIDE_LEFT   = (0 << 2) | 1,  /* +Y */
    PBIO_SIDE_TOP    = (0 << 2) | 2,  /* +Z */
    PBIO_SIDE_BACK   = (1 << 2) | 0,  /* -X */
    PBIO_SIDE_RIGHT  = (1 << 2) | 1,  /* -Y */
    PBIO_SIDE_BOTTOM = (1 << 2) | 2,  /* -Z */
} pbio_side_t;

#ifdef __cplusplus
}
#endif

#endif /* ETSIM_PBIO_GEOMETRY_H */
