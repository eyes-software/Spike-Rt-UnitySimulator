/*
 * pbio/error.h : SPIKE-RT が使う pbio のエラーコード（PC シミュレータ用）。
 *
 * 実機側の定義は pybricks-micropython の lib/pbio/include/pbio/error.h。
 * 数値比較しているコードが壊れないよう、列挙の並びは上流に合わせてある。
 */
#ifndef ETSIM_PBIO_ERROR_H
#define ETSIM_PBIO_ERROR_H

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    PBIO_SUCCESS,
    PBIO_ERROR_FAILED,
    PBIO_ERROR_INVALID_ARG,
    PBIO_ERROR_IO,
    PBIO_ERROR_BUSY,
    PBIO_ERROR_NO_DEV,
    PBIO_ERROR_NOT_IMPLEMENTED,
    PBIO_ERROR_NOT_SUPPORTED,
    PBIO_ERROR_AGAIN,
    PBIO_ERROR_INVALID_OP,
    PBIO_ERROR_TIMEDOUT,
    PBIO_ERROR_CANCELED
} pbio_error_t;

const char *pbio_error_str(pbio_error_t err);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_PBIO_ERROR_H */
