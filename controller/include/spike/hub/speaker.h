#ifndef ETSIM_HUB_SPEAKER_H
#define ETSIM_HUB_SPEAKER_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>

/*
 * スピーカ。音はシミュレータでは鳴らないので、呼ばれたことだけ記録する。
 * 宣言は SPIKE-RT の drivers/include/spike/hub/speaker.h に合わせてある。
 */

void hub_speaker_set_volume(uint8_t volume);
void hub_speaker_play_tone(uint16_t frequency, int32_t duration);
void hub_speaker_stop(void);


#ifdef __cplusplus
}
#endif

#endif /* ETSIM_HUB_SPEAKER_H */
