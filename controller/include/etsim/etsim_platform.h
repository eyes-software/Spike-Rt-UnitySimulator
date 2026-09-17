/*
 * etsim_platform.h
 *
 * Windows / POSIX のスレッド・排他・ソケットの差を吸収する内部ヘッダ。
 * 制御プロセスは Windows でも WSL/Linux でも同じソースでビルドできるようにしたいが、
 * 差分をあちこちの #ifdef に散らすと追えなくなるので 1 か所に閉じ込める。
 *
 * winsock2.h は windows.h より先に読む必要があるため、
 * ソケット用のヘッダもここでまとめて取り込む。
 */
#ifndef ETSIM_PLATFORM_H
#define ETSIM_PLATFORM_H

#ifdef _WIN32
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <winsock2.h>
#  include <ws2tcpip.h>
#  include <windows.h>
#  include <timeapi.h>   /* timeBeginPeriod / timeEndPeriod */
#  ifdef _MSC_VER
#    pragma comment(lib, "ws2_32.lib")
#    pragma comment(lib, "winmm.lib")
#  endif

typedef SOCKET etsim_socket_t;
#  define ETSIM_INVALID_SOCKET INVALID_SOCKET
#  define ETSIM_CLOSE_SOCKET(s) closesocket(s)

typedef CRITICAL_SECTION etsim_mutex_t;
#  define ETSIM_MUTEX_INIT(m)    InitializeCriticalSection(m)
#  define ETSIM_MUTEX_DESTROY(m) DeleteCriticalSection(m)
#  define ETSIM_MUTEX_LOCK(m)    EnterCriticalSection(m)
#  define ETSIM_MUTEX_UNLOCK(m)  LeaveCriticalSection(m)

typedef CONDITION_VARIABLE etsim_cond_t;
#  define ETSIM_COND_INIT(c)      InitializeConditionVariable(c)
#  define ETSIM_COND_DESTROY(c)   ((void)0)
#  define ETSIM_COND_BROADCAST(c) WakeAllConditionVariable(c)
#  define ETSIM_COND_WAIT_MS(c, m, ms) \
       SleepConditionVariableCS((c), (m), (DWORD)(ms))

typedef HANDLE etsim_thread_t;
#  define ETSIM_THREAD_NULL  NULL
#  define ETSIM_THREAD_RET   DWORD WINAPI
#  define ETSIM_THREAD_ARG   LPVOID
#  define ETSIM_THREAD_DONE  return 0

#  define ETSIM_YIELD() YieldProcessor()

#else /* POSIX */

#  include <arpa/inet.h>
#  include <errno.h>
#  include <netinet/in.h>
#  include <pthread.h>
#  include <sched.h>
#  include <sys/socket.h>
#  include <sys/time.h>
#  include <time.h>
#  include <unistd.h>

typedef int etsim_socket_t;
#  define ETSIM_INVALID_SOCKET (-1)
#  define ETSIM_CLOSE_SOCKET(s) close(s)

typedef pthread_mutex_t etsim_mutex_t;
#  define ETSIM_MUTEX_INIT(m)    pthread_mutex_init((m), NULL)
#  define ETSIM_MUTEX_DESTROY(m) pthread_mutex_destroy(m)
#  define ETSIM_MUTEX_LOCK(m)    pthread_mutex_lock(m)
#  define ETSIM_MUTEX_UNLOCK(m)  pthread_mutex_unlock(m)

typedef pthread_cond_t etsim_cond_t;
#  define ETSIM_COND_INIT(c)      pthread_cond_init((c), NULL)
#  define ETSIM_COND_DESTROY(c)   pthread_cond_destroy(c)
#  define ETSIM_COND_BROADCAST(c) pthread_cond_broadcast(c)

typedef pthread_t etsim_thread_t;
#  define ETSIM_THREAD_NULL  ((pthread_t)0)
#  define ETSIM_THREAD_RET   void *
#  define ETSIM_THREAD_ARG   void *
#  define ETSIM_THREAD_DONE  return NULL

#  define ETSIM_YIELD() sched_yield()

#endif

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* スレッド生成。成功で true。 */
bool etsim_thread_start(etsim_thread_t *thread,
#ifdef _WIN32
                        DWORD (WINAPI *entry)(LPVOID),
#else
                        void *(*entry)(void *),
#endif
                        void *arg);

/* スレッドの終了を待つ。timeout_ms を過ぎたら諦めて false。 */
bool etsim_thread_join(etsim_thread_t *thread, uint32_t timeout_ms);

/* 条件変数の時間つき待ち。呼び出し時点で mutex を保持していること。 */
void etsim_cond_wait_ms(etsim_cond_t *cond, etsim_mutex_t *mutex, uint32_t timeout_ms);

/* 単調増加するマイクロ秒カウンタ。 */
uint64_t etsim_wall_clock_us(void);

#ifdef __cplusplus
}
#endif

#endif /* ETSIM_PLATFORM_H */
