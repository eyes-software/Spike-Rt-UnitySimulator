/*
 * unistd.h : MSVC には無い POSIX ヘッダの穴埋め。
 *
 * 走行プログラムが習慣で取り込んでいることがあるが、実際に使うのは
 * せいぜい usleep 程度なので、そこだけ用意する。
 * このディレクトリは MSVC のときだけインクルードパスに入れる。
 */
#ifndef ETSIM_COMPAT_UNISTD_H
#define ETSIM_COMPAT_UNISTD_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

int usleep(unsigned int usec);
unsigned int sleep(unsigned int seconds);

#ifdef __cplusplus
}
#endif

#endif /* ETSIM_COMPAT_UNISTD_H */
