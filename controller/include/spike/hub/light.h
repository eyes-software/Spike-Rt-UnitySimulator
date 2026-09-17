#ifndef ETSIM_HUB_LIGHT_H
#define ETSIM_HUB_LIGHT_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>
#include <pbio/color.h>
#include <pbio/error.h>

pbio_error_t hub_light_on_hsv(const pbio_color_hsv_t *hsv);
pbio_error_t hub_light_on_color(pbio_color_t color);
pbio_error_t hub_light_off(void);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_HUB_LIGHT_H */
