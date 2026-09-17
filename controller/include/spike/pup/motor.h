/*
 * spike/pup/motor.h : SPIKE-RT モータ API（シミュレータ実装）。
 * 宣言は spike-rt 本体の drivers/include/spike/pup/motor.h に合わせてある。
 */
#ifndef ETSIM_PUP_MOTOR_H
#define ETSIM_PUP_MOTOR_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdbool.h>
#include <stdint.h>
#include <pbio/error.h>
#include <pbio/port.h>

typedef struct _pup_motor_t pup_motor_t;

typedef enum {
    PUP_DIRECTION_CLOCKWISE = 0,
    PUP_DIRECTION_COUNTERCLOCKWISE = 1
} pup_direction_t;

pup_motor_t *pup_motor_get_device(pbio_port_id_t port);
pbio_error_t pup_motor_setup(pup_motor_t *motor, pup_direction_t positive_direction, bool reset_count);
pbio_error_t pup_motor_reset_count(pup_motor_t *motor);
int32_t      pup_motor_get_count(pup_motor_t *motor);
int32_t      pup_motor_get_speed(pup_motor_t *motor);
pbio_error_t pup_motor_set_speed(pup_motor_t *motor, int speed);
int32_t      pup_motor_get_power(pup_motor_t *motor);
pbio_error_t pup_motor_set_power(pup_motor_t *motor, int power);
pbio_error_t pup_motor_stop(pup_motor_t *motor);
pbio_error_t pup_motor_brake(pup_motor_t *motor);
pbio_error_t pup_motor_hold(pup_motor_t *motor);
bool         pup_motor_is_stalled(pup_motor_t *motor);
int32_t      pup_motor_set_duty_limit(pup_motor_t *motor, int duty_limit);
void         pup_motor_restore_duty_limit(pup_motor_t *motor, int old_value);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_PUP_MOTOR_H */
