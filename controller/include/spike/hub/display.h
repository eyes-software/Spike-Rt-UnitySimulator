#ifndef ETSIM_HUB_DISPLAY_H
#define ETSIM_HUB_DISPLAY_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>
#include <pbio/error.h>

pbio_error_t hub_display_orientation(uint8_t up);
pbio_error_t hub_display_off(void);
pbio_error_t hub_display_pixel(uint8_t row, uint8_t column, uint8_t brightness);
pbio_error_t hub_display_image(uint8_t *image);
pbio_error_t hub_display_number(const int8_t num);
pbio_error_t hub_display_char(const char c);
pbio_error_t hub_display_text(const char *text, uint32_t on, uint32_t off);
pbio_error_t hub_display_text_scroll(const char *text, uint32_t delay);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_HUB_DISPLAY_H */
