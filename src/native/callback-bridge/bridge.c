/*
 * Host callback isolation for Wasmtime.Components (wasmtime_callback_bridge).
 *
 * Every function that Wasmtime may run on a fiber stack (the callback stub and
 * registration finalizers) is native-only. Managed code only runs on native
 * worker threads created by this pool, which enter the CLR through an
 * UnmanagedCallersOnly handler.
 */
#ifndef _WIN32
#define _POSIX_C_SOURCE 200809L
#endif
#include <stdatomic.h>
#include <stdbool.h>
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { JOB_SYNC = 1, JOB_RELEASE = 4 };
enum { MAX_WORKERS_LIMIT = 256, WORKER_STACK = 8 * 1024 * 1024 };
enum { THREAD_START_ATTEMPTS = 3, THREAD_START_RETRY_MS = 1 };
enum { THREAD_STARTED, THREAD_START_RETRY, THREAD_START_FAILED };

#ifdef _MSC_VER
#define BRIDGE_NORETURN __declspec(noreturn)
#else
#define BRIDGE_NORETURN _Noreturn
#endif

BRIDGE_NORETURN static void fail(const char *message) {
    fprintf(stderr, "callback bridge: %s\n", message);
    abort();
}

static void require(int condition, const char *message) {
    if (!condition)
        fail(message);
}

static void *worker_main(void *unused);

/* Minimal threading layer: pthreads on Unix, SRW locks and condition variables on Windows. */
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <process.h>
#include <windows.h>
#define BRIDGE_EXPORT __declspec(dllexport)
typedef SRWLOCK bridge_mutex;
typedef CONDITION_VARIABLE bridge_cond;
typedef HANDLE bridge_thread;
#define BRIDGE_MUTEX_INIT SRWLOCK_INIT
#define BRIDGE_COND_INIT CONDITION_VARIABLE_INIT
static void mutex_lock(bridge_mutex *m) { AcquireSRWLockExclusive(m); }
static void mutex_unlock(bridge_mutex *m) { ReleaseSRWLockExclusive(m); }
static void cond_init(bridge_cond *c) { InitializeConditionVariable(c); }
static void cond_destroy(bridge_cond *c) { (void)c; }
static void cond_wait(bridge_cond *c, bridge_mutex *m) {
    require(SleepConditionVariableSRW(c, m, INFINITE, 0), "condition wait failed");
}
static void cond_signal(bridge_cond *c) { WakeConditionVariable(c); }
static void cond_broadcast(bridge_cond *c) { WakeAllConditionVariable(c); }
#define cpu_relax() YieldProcessor()
static uint64_t now_ns(void) {
    LARGE_INTEGER counter, frequency;
    QueryPerformanceCounter(&counter);
    QueryPerformanceFrequency(&frequency);
    return (uint64_t)((double)counter.QuadPart * 1e9 / (double)frequency.QuadPart);
}
static unsigned __stdcall thread_entry(void *argument) {
    worker_main(argument);
    return 0;
}
static int thread_start(bridge_thread *thread) {
    /* The CLR and nested Wasm calls need a large stack; reserve it explicitly. */
    uintptr_t handle = _beginthreadex(NULL, WORKER_STACK, thread_entry, NULL,
                                      STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (handle) {
        *thread = (HANDLE)handle;
        return THREAD_STARTED;
    }
    return errno == EAGAIN ? THREAD_START_RETRY : THREAD_START_FAILED;
}
static void thread_start_delay(void) { Sleep(THREAD_START_RETRY_MS); }
static void thread_join(bridge_thread thread) {
    require(WaitForSingleObject(thread, INFINITE) == WAIT_OBJECT_0, "worker join failed");
    CloseHandle(thread);
}
#else
#include <pthread.h>
#include <time.h>
#define BRIDGE_EXPORT __attribute__((visibility("default")))
typedef pthread_mutex_t bridge_mutex;
typedef pthread_cond_t bridge_cond;
typedef pthread_t bridge_thread;
#define BRIDGE_MUTEX_INIT PTHREAD_MUTEX_INITIALIZER
#define BRIDGE_COND_INIT PTHREAD_COND_INITIALIZER
static void check(int result) {
    if (result != 0) {
        fprintf(stderr, "callback bridge: pthread operation failed (%d)\n", result);
        abort();
    }
}
static void mutex_lock(bridge_mutex *m) { check(pthread_mutex_lock(m)); }
static void mutex_unlock(bridge_mutex *m) { check(pthread_mutex_unlock(m)); }
static void cond_init(bridge_cond *c) { check(pthread_cond_init(c, NULL)); }
static void cond_destroy(bridge_cond *c) { check(pthread_cond_destroy(c)); }
static void cond_wait(bridge_cond *c, bridge_mutex *m) { check(pthread_cond_wait(c, m)); }
static void cond_signal(bridge_cond *c) { check(pthread_cond_signal(c)); }
static void cond_broadcast(bridge_cond *c) { check(pthread_cond_broadcast(c)); }
static int thread_start(bridge_thread *thread) {
    pthread_attr_t attributes;
    check(pthread_attr_init(&attributes));
    /* The CLR and nested Wasm calls need more than macOS' 512 KiB default. */
    check(pthread_attr_setstacksize(&attributes, WORKER_STACK));
    int result = pthread_create(thread, &attributes, worker_main, NULL);
    check(pthread_attr_destroy(&attributes));
    if (result == 0)
        return THREAD_STARTED;
    return result == EAGAIN ? THREAD_START_RETRY : THREAD_START_FAILED;
}
static void thread_start_delay(void) {
    struct timespec delay = {.tv_nsec = THREAD_START_RETRY_MS * 1000 * 1000};
    while (nanosleep(&delay, &delay) != 0 && errno == EINTR) {
    }
}
static void thread_join(bridge_thread thread) { check(pthread_join(thread, NULL)); }
#if defined(__x86_64__) || defined(__i386__)
#define cpu_relax() __builtin_ia32_pause()
#elif defined(__aarch64__) || defined(__arm__)
#define cpu_relax() __asm__ __volatile__("yield")
#else
#define cpu_relax() ((void)0)
#endif
static uint64_t now_ns(void) {
    struct timespec now;
    check(clock_gettime(CLOCK_MONOTONIC, &now));
    return (uint64_t)now.tv_sec * 1000000000u + (uint64_t)now.tv_nsec;
}
#endif

static _Atomic uint64_t spin_budget_ns;

/*
 * Busy-waits up to the spin budget for either flag to become non-zero. A short callback
 * completes faster than a futex sleep/wake pair, which dominates the handoff cost.
 * Callers must still re-check under the mutex before relying on the state.
 */
static void spin_for(atomic_int *first, atomic_int *second) {
    uint64_t budget = atomic_load_explicit(&spin_budget_ns, memory_order_relaxed);
    uint64_t deadline = 0;
    if (!budget)
        return;
    for (unsigned i = 1;; i++) {
        if (atomic_load_explicit(first, memory_order_acquire) ||
            (second && atomic_load_explicit(second, memory_order_acquire)))
            return;
        cpu_relax();
        if ((i & 63) == 0) {
            uint64_t now = now_ns();
            if (!deadline)
                deadline = now + budget;
            else if (now >= deadline)
                return;
        }
    }
}

typedef void (*handler_type)(void *job);
typedef void *(*error_new_type)(const char *message);

typedef struct registration {
    void *managed;             /* GCHandle owned by managed code */
} registration;

typedef struct sync_request {
    bridge_cond changed;
    atomic_int done;
    void *error;
} sync_request;

typedef struct job {
    int kind;
    registration *registration;
    void *context;
    void *type;                /* only valid while the stub is blocked */
    void *args;
    size_t nargs;
    void *results;
    size_t nresults;
    sync_request *sync;
    bool *released;            /* set once the blocked stub may resume; owned by the worker */
    struct job *next;
} job;

static bridge_mutex mutex = BRIDGE_MUTEX_INIT;
static bridge_cond available = BRIDGE_COND_INIT;
static bridge_cond drained = BRIDGE_COND_INIT;
static job *head, *tail;
/* returning: busy workers whose stub was released, so they become idle without further waits. */
static size_t queued, idle, workers, busy, returning, spinning, max_workers = 64;
static bridge_thread threads[MAX_WORKERS_LIMIT];
static size_t thread_count;
static int shutting_down;
static handler_type handler;
static error_new_type error_new;
static atomic_int work_queued; /* mirrors queued so idle workers can spin without the mutex */

static atomic_uint_fast64_t live_registrations, completed_requests, peak_busy,
    exhaustions, threads_started;

static void max_store(atomic_uint_fast64_t *target, uint64_t value) {
    uint_fast64_t seen = atomic_load(target);
    while (value > seen && !atomic_compare_exchange_weak(target, &seen, value)) {
    }
}

static void *worker_main(void *unused) {
    (void)unused;
    mutex_lock(&mutex);
    for (;;) {
        bool spun = false;
        while (!head && !shutting_down) {
            if (!spun && atomic_load_explicit(&spin_budget_ns, memory_order_relaxed)) {
                /* Enqueuers skip the wake-up while a worker spins; it re-checks head under the mutex. */
                spun = true;
                spinning++;
                mutex_unlock(&mutex);
                spin_for(&work_queued, NULL);
                mutex_lock(&mutex);
                spinning--;
                continue;
            }
            cond_wait(&available, &mutex);
        }
        if (!head)
            break;
        job *j = head;
        head = j->next;
        if (!head)
            tail = NULL;
        queued--;
        atomic_fetch_sub(&work_queued, 1);
        idle--;
        busy++;
        max_store(&peak_busy, busy);
        bool released = false;
        j->released = &released;
        mutex_unlock(&mutex);
        handler(j);
        mutex_lock(&mutex);
        if (released)
            returning--;
        busy--;
        idle++;
        if (!head && busy == 0)
            cond_broadcast(&drained);
    }
    idle--;
    workers--;
    mutex_unlock(&mutex);
    return NULL;
}

enum { ENQUEUED, REFUSED_SHUTDOWN, REFUSED_FULL, REFUSED_THREAD };

/* Caller holds the mutex. Refuses the job if it would wait for a worker that can never come. */
static int enqueue_locked(job *j, bool may_wait) {
    if (shutting_down)
        return REFUSED_SHUTDOWN;
    /* A returning worker frees up shortly, so waiting for it cannot deadlock. */
    int attempts = 0;
    while (queued >= idle + returning) {
        bool full = workers >= max_workers || thread_count >= MAX_WORKERS_LIMIT;
        if (full) {
            if (!may_wait) {
                atomic_fetch_add(&exhaustions, 1);
                return REFUSED_FULL;
            }
            break;
        }
        int start = thread_start(&threads[thread_count]);
        if (start == THREAD_STARTED) {
            thread_count++;
            workers++;
            idle++;
            atomic_fetch_add(&threads_started, 1);
            break;
        }
        attempts++;
        if (start == THREAD_START_RETRY && attempts < THREAD_START_ATTEMPTS) {
            mutex_unlock(&mutex);
            thread_start_delay();
            mutex_lock(&mutex);
            if (shutting_down)
                return REFUSED_SHUTDOWN;
            continue;
        }
        if (!may_wait || workers == 0) {
            atomic_fetch_add(&exhaustions, 1);
            return REFUSED_THREAD;
        }
        break;
    }
    j->next = NULL;
    if (tail)
        tail->next = j;
    else
        head = j;
    tail = j;
    queued++;
    atomic_fetch_add(&work_queued, 1);
    if (spinning < queued)
        cond_signal(&available);
    return ENQUEUED;
}

/* Caller holds the mutex; Wasmtime copies the message. */
static void *refused_locked(int reason) {
    char message[200];
    switch (reason) {
    case REFUSED_SHUTDOWN: return error_new("callback bridge is shutting down");
    case REFUSED_THREAD:
        snprintf(message, sizeof message,
                 "callback bridge worker pool exhausted; the operating system refused a new worker thread "
                 "(%zu workers, %zu busy)", workers, busy);
        return error_new(message);
    default:
        snprintf(message, sizeof message,
                 "callback bridge worker pool exhausted; a nested callback would deadlock "
                 "(%zu of %zu workers busy)", busy, max_workers);
        return error_new(message);
    }
}

/* Synchronous Wasmtime callback. Runs on the fiber and never enters the CLR. */
static void *sync_callback(void *env, void *context, void *type, void *args,
                           size_t nargs, void *results, size_t nresults) {
    sync_request r = {0};
    job j = {.kind = JOB_SYNC, .registration = env, .context = context, .type = type,
             .args = args, .nargs = nargs, .results = results, .nresults = nresults, .sync = &r};
    cond_init(&r.changed);
    mutex_lock(&mutex);
    int reason = enqueue_locked(&j, false);
    if (reason != ENQUEUED) {
        void *error = refused_locked(reason);
        mutex_unlock(&mutex);
        cond_destroy(&r.changed);
        return error;
    }
    mutex_unlock(&mutex);
    spin_for(&r.done, NULL);
    /* Always re-acquire: the completer may still be signalling r.changed. */
    mutex_lock(&mutex);
    while (!r.done)
        cond_wait(&r.changed, &mutex);
    mutex_unlock(&mutex);
    cond_destroy(&r.changed);
    return r.error;
}

BRIDGE_EXPORT void bridge_sync_complete(job *j, void *error) {
    mutex_lock(&mutex);
    require(j->kind == JOB_SYNC && !j->sync->done, "invalid synchronous completion");
    j->sync->error = error;
    *j->released = true;
    returning++;
    j->sync->done = 1;
    atomic_fetch_add(&completed_requests, 1);
    cond_broadcast(&j->sync->changed);
    mutex_unlock(&mutex);
}

/* Worker: frees a heap-allocated job once it has been handled. */
BRIDGE_EXPORT void bridge_job_done(job *j) {
    if (j->kind == JOB_RELEASE)
        free(j);
}

BRIDGE_EXPORT void bridge_job(job *j, int *kind, void **managed, void **context, void **type,
                              void **args, size_t *nargs, void **results, size_t *nresults) {
    *kind = j->kind;
    *managed = j->registration ? j->registration->managed : NULL;
    *context = j->context;
    *type = j->type;
    *args = j->args;
    *nargs = j->nargs;
    *results = j->results;
    *nresults = j->nresults;
}

/* Wasmtime owns registrations; the managed GCHandle is freed later on a worker. */
static void registration_finalize(void *env) {
    registration *r = env;
    job *j = calloc(1, sizeof(*j));
    require(j != NULL, "release allocation failed");
    j->kind = JOB_RELEASE;
    j->registration = r;
    mutex_lock(&mutex);
    require(enqueue_locked(j, true) == ENQUEUED, "could not schedule registration release");
    mutex_unlock(&mutex);
}

BRIDGE_EXPORT void bridge_registration_free(job *j) {
    free(j->registration);
    j->registration = NULL;
    atomic_fetch_sub(&live_registrations, 1);
}

BRIDGE_EXPORT int bridge_init(handler_type managed_handler, error_new_type new_error,
                              size_t maximum) {
    mutex_lock(&mutex);
    int status = !shutting_down && managed_handler && new_error &&
                 maximum > 0 && maximum <= MAX_WORKERS_LIMIT;
    for (int attempt = 0; status == 1 && workers == 0;) {
        int start = thread_start(&threads[thread_count]);
        if (start == THREAD_STARTED) {
            thread_count++;
            workers++;
            idle++;
            atomic_fetch_add(&threads_started, 1);
            break;
        }
        attempt++;
        if (start != THREAD_START_RETRY || attempt == THREAD_START_ATTEMPTS) {
            status = -1;
            break;
        }
        mutex_unlock(&mutex);
        thread_start_delay();
        mutex_lock(&mutex);
        status = shutting_down ? 0 : 1;
    }
    if (status == 1) {
        handler = managed_handler;
        error_new = new_error;
        max_workers = maximum;
    }
    mutex_unlock(&mutex);
    return status;
}

/* How long a waiting thread busy-polls before sleeping; 0 disables spinning. */
BRIDGE_EXPORT void bridge_set_spin(uint64_t nanoseconds) {
    atomic_store(&spin_budget_ns, nanoseconds);
}

BRIDGE_EXPORT registration *bridge_register(void *managed) {
    registration *r = calloc(1, sizeof(*r));
    if (r) {
        r->managed = managed;
        atomic_fetch_add(&live_registrations, 1);
    }
    return r;
}

/* Used only when Wasmtime rejected the registration before taking ownership. */
BRIDGE_EXPORT void bridge_registration_abandon(registration *r) {
    free(r);
    atomic_fetch_sub(&live_registrations, 1);
}

BRIDGE_EXPORT void *bridge_sync_callback(void) { return (void *)sync_callback; }
BRIDGE_EXPORT void *bridge_registration_finalizer(void) { return (void *)registration_finalize; }

/* Waits for queued work, then joins every worker. Must not be called from a worker. */
BRIDGE_EXPORT void bridge_shutdown(void) {
    mutex_lock(&mutex);
    while (head || busy)
        cond_wait(&drained, &mutex);
    shutting_down = 1;
    cond_broadcast(&available);
    size_t count = thread_count;
    mutex_unlock(&mutex);
    for (size_t i = 0; i < count; i++)
        thread_join(threads[i]);
    mutex_lock(&mutex);
    require(workers == 0 && idle == 0, "worker accounting failed");
    thread_count = 0;
    shutting_down = 0;
    mutex_unlock(&mutex);
}

BRIDGE_EXPORT uint64_t bridge_counter(int which) {
    switch (which) {
    case 0: return atomic_load(&live_registrations);
    case 1: return atomic_load(&completed_requests);
    case 2: return atomic_load(&peak_busy);
    case 3: return atomic_load(&exhaustions);
    case 10: return atomic_load(&threads_started);
    default: return UINT64_MAX;
    }
}
