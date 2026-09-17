#ifndef ETSIM_HUB_BUTTON_H
#define ETSIM_HUB_BUTTON_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>
#include <pbio/button.h>
#include <pbio/error.h>

typedef enum _hub_button_t {
    HUB_BUTTON_LEFT   = PBIO_BUTTON_LEFT,
    HUB_BUTTON_CENTER = PBIO_BUTTON_CENTER,
    HUB_BUTTON_RIGHT  = PBIO_BUTTON_RIGHT,
    HUB_BUTTON_BT     = PBIO_BUTTON_RIGHT_UP
} hub_button_t;

pbio_error_t hub_button_is_pressed(hub_button_t *pressed);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_HUB_BUTTON_H */
