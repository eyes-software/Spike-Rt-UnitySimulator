#ifndef ETSIM_PUP_COLOR_SENSOR_H
#define ETSIM_PUP_COLOR_SENSOR_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdbool.h>
#include <stdint.h>
#include <spike/pup_device.h>
#include <pbio/color.h>

typedef struct {
    uint16_t r, g, b;
} pup_color_rgb_t;

typedef pbio_color_hsv_t pup_color_hsv_t;

pup_device_t    *pup_color_sensor_get_device(pbio_port_id_t port);
pup_color_rgb_t  pup_color_sensor_rgb(pup_device_t *pdev);
pup_color_hsv_t  pup_color_sensor_color(pup_device_t *pdev, bool surface);
pup_color_hsv_t  pup_color_sensor_hsv(pup_device_t *pdev, bool surface);
int32_t          pup_color_sensor_reflection(pup_device_t *pdev);
int32_t          pup_color_sensor_ambient(pup_device_t *pdev);
pbio_error_t     pup_color_sensor_light_set(pup_device_t *pdev, int32_t bv1, int32_t bv2, int32_t bv3);
pbio_error_t     pup_color_sensor_light_on(pup_device_t *pdev);
pbio_error_t     pup_color_sensor_light_off(pup_device_t *pdev);
pup_color_hsv_t *pup_color_sensor_detectable_colors(int32_t size, pup_color_hsv_t *colors);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_PUP_COLOR_SENSOR_H */
