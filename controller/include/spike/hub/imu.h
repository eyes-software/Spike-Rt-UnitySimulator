#ifndef ETSIM_HUB_IMU_H
#define ETSIM_HUB_IMU_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdbool.h>
#include <stdint.h>
#include <pbio/error.h>

/*
 * 宣言は SPIKE-RT (etrobocon/spike-rt-RasPike-ART) の
 * drivers/include/spike/hub/imu.h に合わせてある。
 */

pbio_error_t hub_imu_init(void);
bool         hub_imu_is_ready(void);
bool         hub_imu_is_stationary(void);
void         hub_imu_set_tilt(float angle);
void         hub_imu_get_acceleration(float accel[3]);
void         hub_imu_get_angular_velocity(float angv[3]);
float        hub_imu_get_temperature(void);
float        hub_imu_get_heading(void);
void         hub_imu_reset_heading(void);

/* 旧称。以前のサンプルが使っているので残してある。 */
float etsim_imu_get_heading(void);
void  etsim_imu_reset_heading(void);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_HUB_IMU_H */
