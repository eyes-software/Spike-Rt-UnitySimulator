#ifndef ETSIM_PUP_FORCE_SENSOR_H
#define ETSIM_PUP_FORCE_SENSOR_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdbool.h>
#include <stdint.h>
#include <spike/pup_device.h>

pup_device_t *pup_force_sensor_get_device(pbio_port_id_t port);
float         pup_force_sensor_force(pup_device_t *pdev);
float         pup_force_sensor_distance(pup_device_t *pdev);
bool          pup_force_sensor_pressed(pup_device_t *pdev, float force);
bool          pup_force_sensor_touched(pup_device_t *pdev);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_PUP_FORCE_SENSOR_H */
