#ifndef ETSIM_PBIO_BUTTON_H
#define ETSIM_PBIO_BUTTON_H

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    PBIO_BUTTON_LEFT_DOWN = 1 << 0,
    PBIO_BUTTON_LEFT      = 1 << 1,
    PBIO_BUTTON_CENTER    = 1 << 2,
    PBIO_BUTTON_RIGHT     = 1 << 3,
    PBIO_BUTTON_RIGHT_UP  = 1 << 4,
    PBIO_BUTTON_UP        = 1 << 5,
    PBIO_BUTTON_DOWN      = 1 << 6
} pbio_button_flags_t;


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_PBIO_BUTTON_H */
