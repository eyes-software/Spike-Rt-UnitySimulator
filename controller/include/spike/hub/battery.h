#ifndef ETSIM_HUB_BATTERY_H
#define ETSIM_HUB_BATTERY_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>

uint16_t hub_battery_get_voltage(void);
uint16_t hub_battery_get_current(void);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_HUB_BATTERY_H */
