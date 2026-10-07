/* Compile the real bridge with OS calls substituted only in this test executable. */
#define _POSIX_C_SOURCE 200809L
#include <stdint.h>
#ifdef _WIN32
#include <process.h>
#include <windows.h>
static uintptr_t test_beginthreadex(void *, unsigned, unsigned (__stdcall *)(void *),
                                    void *, unsigned, unsigned *);
static void WINAPI test_sleep(DWORD);
#define _beginthreadex test_beginthreadex
#define Sleep test_sleep
#else
#include <pthread.h>
#include <time.h>
static int test_pthread_create(pthread_t *, const pthread_attr_t *, void *(*)(void *), void *);
static int test_nanosleep(const struct timespec *, struct timespec *);
#define pthread_create test_pthread_create
#define nanosleep test_nanosleep
#endif

#include "../bridge.c"

#ifdef _WIN32
#undef _beginthreadex
#undef Sleep
#else
#undef pthread_create
#undef nanosleep
#endif

static int start_attempts, retry_delays, failures_left, start_error = EAGAIN;
static bridge_cond changed = BRIDGE_COND_INIT;
static job *blocked_job;
static bool entered, unblocked, release_on_retry;
static atomic_int handled;

static bool inject_failure(void) {
    start_attempts++;
    if (failures_left == 0)
        return false;
    failures_left--;
    return true;
}

static void on_retry(void) {
    retry_delays++;
    /* This deadlocks (and CTest times out) if backoff holds the queue mutex. */
    mutex_lock(&mutex);
    if (release_on_retry) {
        unblocked = true;
        cond_broadcast(&changed);
        while (busy)
            cond_wait(&drained, &mutex);
    }
    mutex_unlock(&mutex);
}

#ifdef _WIN32
static uintptr_t test_beginthreadex(void *security, unsigned stack_size,
                                    unsigned (__stdcall *start)(void *), void *argument,
                                    unsigned flags, unsigned *thread_id) {
    if (inject_failure()) {
        errno = start_error;
        return 0;
    }
    return _beginthreadex(security, stack_size, start, argument, flags, thread_id);
}
static void WINAPI test_sleep(DWORD milliseconds) {
    require(milliseconds == 1, "expected 1 ms retry delay");
    on_retry();
}
#else
static int test_pthread_create(pthread_t *thread, const pthread_attr_t *attributes,
                               void *(*start)(void *), void *argument) {
    if (inject_failure())
        return start_error;
    return pthread_create(thread, attributes, start, argument);
}
static int test_nanosleep(const struct timespec *delay, struct timespec *remaining) {
    (void)remaining;
    require(delay->tv_sec == 0 && delay->tv_nsec == 1000000, "expected 1 ms retry delay");
    on_retry();
    return 0;
}
#endif

static void *unexpected_error(const char *message) {
    fail(message);
    return NULL;
}

static void unexpected_delete(void *value) {
    (void)value;
    fail("unexpected value deletion");
}

static void handle_job(void *argument) {
    job *j = argument;
    require(j->kind == JOB_RELEASE, "unexpected job kind");
    mutex_lock(&mutex);
    if (j == blocked_job) {
        entered = true;
        cond_broadcast(&changed);
        while (!unblocked)
            cond_wait(&changed, &mutex);
    }
    mutex_unlock(&mutex);
    bridge_registration_free(j);
    bridge_job_done(j);
    atomic_fetch_add(&handled, 1);
}

static int initialize(size_t maximum) {
    return bridge_init(handle_job, unexpected_error, unexpected_delete, 1, maximum);
}

static void assert_stopped(void) {
    require(workers == 0 && idle == 0 && busy == 0 && returning == 0 &&
            queued == 0 && thread_count == 0 && head == NULL && tail == NULL &&
            atomic_load(&work_queued) == 0 && atomic_load(&live_registrations) == 0,
            "worker or registration state leaked");
}

static job *new_release(void) {
    job *j = calloc(1, sizeof(*j));
    require(j != NULL, "test job allocation failed");
    j->kind = JOB_RELEASE;
    j->registration = bridge_register(NULL, 0);
    require(j->registration != NULL, "test registration allocation failed");
    return j;
}

static void test_startup(const char *scenario) {
    if (strcmp(scenario, "invalid") == 0) {
        require(initialize(0) == 0 && initialize(257) == 0, "invalid limit accepted");
        require(start_attempts == 0, "invalid initialization started a worker");
    } else if (strcmp(scenario, "transient") == 0) {
        failures_left = 2;
        require(initialize(1) == 1, "transient startup failure did not recover");
        require(start_attempts == 3 && retry_delays == 2, "wrong startup retry count");
    } else if (strcmp(scenario, "retry-initialization") == 0 ||
               strcmp(scenario, "permanent") == 0) {
        bool permanent = strcmp(scenario, "permanent") == 0;
        failures_left = permanent ? 1 : 3;
        start_error = permanent ? EINVAL : EAGAIN;
        require(initialize(1) == -1, "startup failure was not reported");
        require(start_attempts == (permanent ? 1 : 3) &&
                retry_delays == (permanent ? 0 : 2), "wrong failure retry count");
        assert_stopped();
        require(atomic_load(&threads_started) == 0, "failed start counted as a worker");
    } else {
        require(strcmp(scenario, "finalizer") == 0, "unknown startup test");
    }

    require(initialize(1) == 1, "initialization did not succeed");
    mutex_lock(&mutex);
    require(workers == 1 && idle == 1, "initialization did not create one ready worker");
    mutex_unlock(&mutex);
    require(atomic_load(&threads_started) == 1, "reinitialization created an extra worker");
    registration *r = bridge_register(NULL, 0);
    require(r != NULL, "registration allocation failed");
    /* No callback has run: finalization must use the prestarted worker. */
    registration_finalize(r);
    bridge_shutdown();
    require(atomic_load(&handled) == 1, "registration finalizer was not handled");
    assert_stopped();
}

static void test_growth(const char *scenario) {
    bool full = strcmp(scenario, "pool-limit") == 0;
    bool refused = strcmp(scenario, "growth-refused") == 0;
    release_on_retry = strcmp(scenario, "reuse-during-backoff") == 0;
    require(full || refused || release_on_retry || strcmp(scenario, "growth-retry") == 0,
            "unknown growth test");
    require(initialize(full ? 1 : 2) == 1, "initialization failed");
    blocked_job = new_release();
    mutex_lock(&mutex);
    require(enqueue_locked(blocked_job, false) == ENQUEUED, "first job was refused");
    while (!entered)
        cond_wait(&changed, &mutex);
    require(busy == 1 && idle == 0, "first worker is not busy");

    failures_left = refused ? 3 : 2;
    int attempts_before = start_attempts;
    job *next = new_release();
    int result = enqueue_locked(next, false);
    require(result == (full ? REFUSED_FULL : refused ? REFUSED_THREAD : ENQUEUED),
            "wrong enqueue result");
    require(start_attempts - attempts_before == (full ? 0 : release_on_retry ? 1 : 3),
            "wrong growth attempt count");
    require(retry_delays == (full ? 0 : release_on_retry ? 1 : 2),
            "wrong growth retry count");
    require(atomic_load(&threads_started) == (uint64_t)(full || refused || release_on_retry ? 1 : 2),
            "wrong worker count");
    require(atomic_load(&exhaustions) == (uint64_t)(full || refused ? 1 : 0),
            "wrong exhaustion count");
    if (result != ENQUEUED) {
        bridge_registration_free(next);
        free(next);
    }
    unblocked = true;
    cond_broadcast(&changed);
    mutex_unlock(&mutex);
    bridge_shutdown();
    require(atomic_load(&handled) == (full || refused ? 1 : 2), "jobs lost");
    assert_stopped();
}

int main(int argc, char **argv) {
    require(argc == 2, "expected one test scenario");
    if (strncmp(argv[1], "growth-", 7) == 0 || strcmp(argv[1], "pool-limit") == 0 ||
        strcmp(argv[1], "reuse-during-backoff") == 0)
        test_growth(argv[1]);
    else
        test_startup(argv[1]);
    printf("%s: passed\n", argv[1]);
    return 0;
}
