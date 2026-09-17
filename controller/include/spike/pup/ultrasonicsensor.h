#ifndef ETSIM_PUP_ULTRASONIC_SENSOR_H
#define ETSIM_PUP_ULTRASONIC_SENSOR_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdbool.h>
#include <stdint.h>
#include <spike/pup_device.h>

pup_device_t *pup_ultrasonic_sensor_get_device(pbio_port_id_t port);
int32_t       pup_ultrasonic_sensor_distance(pup_device_t *pdev);
bool          pup_ultrasonic_sensor_presence(pup_device_t *pdev);
pbio_error_t  pup_ultrasonic_sensor_light_set(pup_device_t *pdev, int32_t bv1, int32_t bv2, int32_t bv3, int32_t bv4);
pbio_error_t  pup_ultrasonic_sensor_light_on(pup_device_t *pdev);
pbio_error_t  pup_ultrasonic_sensor_light_off(pup_device_t *pdev);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_PUP_ULTRASONIC_SENSOR_H */
