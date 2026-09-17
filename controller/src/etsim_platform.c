#include "etsim/etsim_platform.h"

bool etsim_thread_start(etsim_thread_t *thread,
#ifdef _WIN32
                        DWORD (WINAPI *entry)(LPVOID),
#else
                        void *(*entry)(void *),
#endif
                        void *arg)
{
    if (thread == NULL || entry == NULL) return false;

#ifdef _WIN32
    *thread = CreateThread(NULL, 0, entry, arg, 0, NULL);
    return *thread != NULL;
#else
    return pthread_create(thread, NULL, entry, arg) == 0;
#endif
}

bool etsim_thread_join(etsim_thread_t *thread, uint32_t timeout_ms)
{
    if (thread == NULL) return true;

#ifdef _WIN32
    if (*thread == NULL) return true;
    DWORD r = WaitForSingleObject(*thread, timeout_ms);
    CloseHandle(*thread);
    *thread = NULL;
    return r == WAIT_OBJECT_0;
#else
    /*
     * pthread には時間つき join が移植性のある形で無い。
     * ここで待つ相手はいずれも「停止フラグを見て自分で抜けるループ」なので、
     * 素直にブロックして join する。
     */
    (void)timeout_ms;
    if (*thread == ETSIM_THREAD_NULL) return true;
    pthread_join(*thread, NULL);
    *thread = ETSIM_THREAD_NULL;
    return true;
#endif
}

void etsim_cond_wait_ms(etsim_cond_t *cond, etsim_mutex_t *mutex, uint32_t timeout_ms)
{
#ifdef _WIN32
    SleepConditionVariableCS(cond, mutex, (DWORD)timeout_ms);
#else
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);

    deadline.tv_sec  += (time_t)(timeout_ms / 1000u);
    deadline.tv_nsec += (long)((timeout_ms % 1000u) * 1000000L);
    if (deadline.tv_nsec >= 1000000000L) {
        deadline.tv_sec += 1;
        deadline.tv_nsec -= 1000000000L;
    }

    pthread_cond_timedwait(cond, mutex, &deadline);
#endif
}

uint64_t etsim_wall_clock_us(void)
{
#ifdef _WIN32
    static LARGE_INTEGER freq;
    LARGE_INTEGER now;
    if (freq.QuadPart == 0) QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&now);
    return (uint64_t)((now.QuadPart * 1000000ULL) / (uint64_t)freq.QuadPart);
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000ULL + (uint64_t)ts.tv_nsec / 1000ULL;
#endif
}

#ifdef _MSC_VER
/*
 * MSVC には unistd.h が無いので、走行プログラムが使う分だけ用意する。
 * 宣言は include/msvc/unistd.h にある。
 */
int usleep(unsigned int usec)
{
    Sleep((DWORD)((usec + 999u) / 1000u));
    return 0;
}

unsigned int sleep(unsigned int seconds)
{
    Sleep((DWORD)seconds * 1000u);
    return 0;
}
#endif
