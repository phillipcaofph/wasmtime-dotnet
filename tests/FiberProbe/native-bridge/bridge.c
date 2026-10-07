/*
 * Native callback bridge prototype.
 *
 * Every function that Wasmtime may run on a fiber stack (the sync/async callback
 * stubs, the async continuation and its finalizer, registration finalizers) is
 * native-only. Managed code only runs on native worker threads created by this
 * pool, which enter the CLR through an UnmanagedCallersOnly handler.
 */
#include <pthread.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { JOB_SYNC = 1, JOB_ASYNC_START = 2, JOB_ASYNC_CANCEL = 3, JOB_RELEASE = 4 };
enum { A_STARTING, A_RUNNING, A_DONE, A_FAILED, A_CONSUMED, A_CANCELLED };
enum { MAX_WORKERS_LIMIT = 256, WORKER_STACK = 8 * 1024 * 1024 };

typedef void (*handler_type)(void *job);
typedef void *(*error_new_type)(const char *message);
typedef void (*val_delete_type)(void *value);
typedef void *(*owner_op_type)(void *context);

typedef struct registration {
    void *managed;             /* GCHandle owned by managed code */
    int async;
} registration;

typedef struct sync_request {
    pthread_cond_t changed;
    owner_op_type op;          /* owner-thread operation posted by the worker */
    void *op_result;
    int op_pending;
    int done;
    void *error;
} sync_request;

typedef struct async_request {
    int state;
    int refs;
    int started;
    void *error;               /* synchronous start error */
    void *results;             /* Wasmtime-owned results, only touched while polled */
    void *staged;
    size_t nresults;
    pthread_cond_t changed;
} async_request;

typedef struct job {
    int kind;
    registration *registration;
    void *context;
    void *args;
    size_t nargs;
    void *results;
    size_t nresults;
    sync_request *sync;
    async_request *async;
    struct job *next;
} job;

static pthread_mutex_t mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t available = PTHREAD_COND_INITIALIZER;
static pthread_cond_t drained = PTHREAD_COND_INITIALIZER;
static job *head, *tail;
static size_t queued, idle, workers, busy, max_workers = 64;
static pthread_t threads[MAX_WORKERS_LIMIT];
static size_t thread_count;
static int shutting_down;
static handler_type handler;
static error_new_type error_new;
static val_delete_type val_delete;
static size_t value_size;

static atomic_uint_fast64_t live_registrations, completed_requests, peak_busy,
    exhaustions, owner_ops, live_async, peak_async, async_cancelled,
    staged_deleted, unreportable_failures, threads_started;

static void check(int result) {
    if (result != 0) {
        fprintf(stderr, "callback bridge: pthread operation failed (%d)\n", result);
        abort();
    }
}

static void require(int condition, const char *message) {
    if (!condition) {
        fprintf(stderr, "callback bridge: %s\n", message);
        abort();
    }
}

static void max_store(atomic_uint_fast64_t *target, uint64_t value) {
    uint64_t seen = atomic_load(target);
    while (value > seen && !atomic_compare_exchange_weak(target, &seen, value)) {
    }
}

static void *worker_main(void *unused) {
    (void)unused;
    check(pthread_mutex_lock(&mutex));
    for (;;) {
        while (!head && !shutting_down)
            check(pthread_cond_wait(&available, &mutex));
        if (!head)
            break;
        job *j = head;
        head = j->next;
        if (!head)
            tail = NULL;
        queued--;
        idle--;
        busy++;
        max_store(&peak_busy, busy);
        check(pthread_mutex_unlock(&mutex));
        handler(j);
        check(pthread_mutex_lock(&mutex));
        busy--;
        idle++;
        if (!head && busy == 0)
            check(pthread_cond_broadcast(&drained));
    }
    idle--;
    workers--;
    check(pthread_mutex_unlock(&mutex));
    return NULL;
}

/* Caller holds the mutex. Returns false if the job would wait for a worker that can never come. */
static bool enqueue_locked(job *j, bool may_wait) {
    if (shutting_down)
        return false;
    if (queued >= idle) {
        if (workers < max_workers && thread_count < MAX_WORKERS_LIMIT) {
            pthread_attr_t attributes;
            check(pthread_attr_init(&attributes));
            /* The CLR and nested Wasm calls need more than macOS' 512 KiB default. */
            check(pthread_attr_setstacksize(&attributes, WORKER_STACK));
            int result = pthread_create(&threads[thread_count], &attributes, worker_main, NULL);
            check(pthread_attr_destroy(&attributes));
            if (result == 0) {
                thread_count++;
                workers++;
                idle++;
                atomic_fetch_add(&threads_started, 1);
            } else if (!may_wait) {
                atomic_fetch_add(&exhaustions, 1);
                return false;
            }
        } else if (!may_wait) {
            atomic_fetch_add(&exhaustions, 1);
            return false;
        }
    }
    j->next = NULL;
    if (tail)
        tail->next = j;
    else
        head = j;
    tail = j;
    queued++;
    check(pthread_cond_signal(&available));
    return true;
}

static void *exhausted(void) {
    return error_new("callback bridge worker pool exhausted; a nested callback would deadlock");
}

/* Synchronous Wasmtime callback. Runs on the fiber and never enters the CLR. */
static void *sync_callback(void *env, void *context, void *type, void *args,
                           size_t nargs, void *results, size_t nresults) {
    (void)type;
    sync_request r = {0};
    job j = {.kind = JOB_SYNC, .registration = env, .context = context, .args = args,
             .nargs = nargs, .results = results, .nresults = nresults, .sync = &r};
    check(pthread_cond_init(&r.changed, NULL));
    check(pthread_mutex_lock(&mutex));
    if (!enqueue_locked(&j, false)) {
        check(pthread_mutex_unlock(&mutex));
        check(pthread_cond_destroy(&r.changed));
        return exhausted();
    }
    for (;;) {
        while (!r.done && !r.op_pending)
            check(pthread_cond_wait(&r.changed, &mutex));
        if (r.done)
            break;
        /* Store operations that depend on this thread's Wasm activation run here. */
        owner_op_type op = r.op;
        check(pthread_mutex_unlock(&mutex));
        void *result = op(context);
        atomic_fetch_add(&owner_ops, 1);
        check(pthread_mutex_lock(&mutex));
        r.op_result = result;
        r.op_pending = 0;
        check(pthread_cond_broadcast(&r.changed));
    }
    check(pthread_mutex_unlock(&mutex));
    check(pthread_cond_destroy(&r.changed));
    return r.error;
}

/* Called by a worker while it handles a JOB_SYNC request. */
void *bridge_owner_call(job *j, owner_op_type op) {
    require(j->kind == JOB_SYNC, "owner calls require a synchronous callback");
    sync_request *r = j->sync;
    check(pthread_mutex_lock(&mutex));
    require(!r->done && !r->op_pending, "owner call outside an active callback");
    r->op = op;
    r->op_pending = 1;
    check(pthread_cond_broadcast(&r->changed));
    while (r->op_pending)
        check(pthread_cond_wait(&r->changed, &mutex));
    void *result = r->op_result;
    check(pthread_mutex_unlock(&mutex));
    return result;
}

void bridge_sync_complete(job *j, void *error) {
    check(pthread_mutex_lock(&mutex));
    require(j->kind == JOB_SYNC && !j->sync->done, "invalid synchronous completion");
    j->sync->error = error;
    j->sync->done = 1;
    atomic_fetch_add(&completed_requests, 1);
    check(pthread_cond_broadcast(&j->sync->changed));
    check(pthread_mutex_unlock(&mutex));
}

/* Caller holds the mutex. */
static void async_release_locked(async_request *a) {
    require(a->refs > 0, "async request over-released");
    if (--a->refs == 0) {
        check(pthread_cond_destroy(&a->changed));
        free(a->staged);
        free(a);
        atomic_fetch_sub(&live_async, 1);
    }
}

static void delete_staged(async_request *a) {
    for (size_t i = 0; i < a->nresults; i++)
        val_delete((char *)a->staged + i * value_size);
    atomic_fetch_add(&staged_deleted, 1);
}

/* Polled by Wasmtime inside the host future, possibly on a fiber. Native-only. */
static bool async_continuation(void *env) {
    async_request *a = env;
    bool ready = false;
    check(pthread_mutex_lock(&mutex));
    if (a->state == A_DONE) {
        /* Bitwise move: Wasmtime now owns these values; placeholders need no drop. */
        memcpy(a->results, a->staged, a->nresults * value_size);
        a->state = A_CONSUMED;
        ready = true;
    } else if (a->state == A_FAILED) {
        /* The C continuation cannot return an error; placeholder results remain. */
        atomic_fetch_add(&unreportable_failures, 1);
        a->state = A_CONSUMED;
        ready = true;
    }
    check(pthread_mutex_unlock(&mutex));
    return ready;
}

/* Runs when Wasmtime drops the host future (completion, cancellation or Store teardown). */
static void async_finalize(void *env) {
    async_request *a = env;
    check(pthread_mutex_lock(&mutex));
    if (a->state == A_RUNNING) {
        a->state = A_CANCELLED;
        atomic_fetch_add(&async_cancelled, 1);
        job *notify = calloc(1, sizeof(*notify));
        require(notify != NULL, "cancel notification allocation failed");
        notify->kind = JOB_ASYNC_CANCEL;
        notify->async = a;
        a->refs++;
        if (!enqueue_locked(notify, true)) {
            a->refs--;
            free(notify);
        }
    } else if (a->state == A_DONE) {
        delete_staged(a);
        a->state = A_CONSUMED;
    }
    async_release_locked(a);
    check(pthread_mutex_unlock(&mutex));
}

typedef bool (*continuation_callback)(void *);
typedef struct continuation {
    continuation_callback callback;
    void *env;
    void (*finalizer)(void *);
} continuation;

/* Async Wasmtime callback. Arguments die when this returns, so a worker consumes them first. */
static void async_callback(void *env, void *context, void *type, void *args, size_t nargs,
                           void *results, size_t nresults, void **error_ret,
                           continuation *continuation_ret) {
    (void)type;
    async_request *a = calloc(1, sizeof(*a));
    void *staged = calloc(nresults ? nresults : 1, value_size);
    if (!a || !staged) {
        free(a);
        free(staged);
        *error_ret = error_new("callback bridge allocation failed");
        return;
    }
    check(pthread_cond_init(&a->changed, NULL));
    a->state = A_STARTING;
    a->refs = 2; /* Wasmtime continuation + managed worker */
    a->results = results;
    a->staged = staged;
    a->nresults = nresults;
    job j = {.kind = JOB_ASYNC_START, .registration = env, .context = context, .args = args,
             .nargs = nargs, .results = staged, .nresults = nresults, .async = a};
    atomic_fetch_add(&live_async, 1);
    check(pthread_mutex_lock(&mutex));
    if (!enqueue_locked(&j, false)) {
        a->refs = 1;
        async_release_locked(a);
        check(pthread_mutex_unlock(&mutex));
        *error_ret = exhausted();
        return;
    }
    while (!a->started)
        check(pthread_cond_wait(&a->changed, &mutex));
    if (a->error) {
        /* Wasmtime never sees the continuation; release its reference here. */
        *error_ret = a->error;
        a->error = NULL;
        async_release_locked(a);
    } else {
        uint64_t live = atomic_load(&live_async);
        max_store(&peak_async, live);
        continuation_ret->callback = async_continuation;
        continuation_ret->env = a;
        continuation_ret->finalizer = async_finalize;
    }
    check(pthread_mutex_unlock(&mutex));
}

/* Worker: arguments have been read. A non-null error fails the call synchronously. */
void bridge_async_started(job *j, void *error) {
    async_request *a = j->async;
    check(pthread_mutex_lock(&mutex));
    require(j->kind == JOB_ASYNC_START && !a->started, "invalid async start");
    a->error = error;
    a->state = error ? A_CONSUMED : A_RUNNING;
    a->started = 1;
    if (error) {
        atomic_fetch_add(&completed_requests, 1);
        async_release_locked(a); /* the worker's reference; the stub still holds one */
    }
    check(pthread_cond_broadcast(&a->changed));
    check(pthread_mutex_unlock(&mutex));
}

/*
 * Worker: the managed task finished. On success the staged values are owned.
 * Returns false if Wasmtime already dropped the future (staged values are deleted).
 */
bool bridge_async_finish(async_request *a, bool succeeded) {
    check(pthread_mutex_lock(&mutex));
    bool accepted = a->state == A_RUNNING;
    atomic_fetch_add(&completed_requests, 1);
    if (accepted) {
        a->state = succeeded ? A_DONE : A_FAILED;
    } else {
        require(a->state == A_CANCELLED, "invalid async completion");
        if (succeeded)
            delete_staged(a);
    }
    async_release_locked(a);
    check(pthread_mutex_unlock(&mutex));
    return accepted;
}

/* Worker: release a cancellation notification's reference. */
void bridge_job_done(job *j) {
    check(pthread_mutex_lock(&mutex));
    if (j->kind == JOB_ASYNC_CANCEL)
        async_release_locked(j->async);
    if (j->kind == JOB_ASYNC_CANCEL || j->kind == JOB_RELEASE)
        free(j);
    check(pthread_mutex_unlock(&mutex));
}

void bridge_job(job *j, int *kind, void **managed, void **context, void **args, size_t *nargs,
                void **results, size_t *nresults, void **request) {
    *kind = j->kind;
    *managed = j->registration ? j->registration->managed : NULL;
    *context = j->context;
    *args = j->args;
    *nargs = j->nargs;
    *results = j->results;
    *nresults = j->nresults;
    *request = j->async;
}

/* Wasmtime owns registrations; the managed GCHandle is freed later on a worker. */
static void registration_finalize(void *env) {
    registration *r = env;
    job *j = calloc(1, sizeof(*j));
    require(j != NULL, "release allocation failed");
    j->kind = JOB_RELEASE;
    j->registration = r;
    check(pthread_mutex_lock(&mutex));
    require(enqueue_locked(j, true), "registration released after shutdown");
    check(pthread_mutex_unlock(&mutex));
}

void bridge_registration_free(job *j) {
    free(j->registration);
    j->registration = NULL;
    atomic_fetch_sub(&live_registrations, 1);
}

int bridge_init(handler_type managed_handler, error_new_type new_error,
                val_delete_type delete_value, size_t size, size_t maximum) {
    check(pthread_mutex_lock(&mutex));
    int ok = !shutting_down && managed_handler && new_error && delete_value && size &&
             maximum > 0 && maximum <= MAX_WORKERS_LIMIT;
    if (ok) {
        handler = managed_handler;
        error_new = new_error;
        val_delete = delete_value;
        value_size = size;
        max_workers = maximum;
    }
    check(pthread_mutex_unlock(&mutex));
    return ok;
}

registration *bridge_register(void *managed, int async) {
    registration *r = calloc(1, sizeof(*r));
    if (r) {
        r->managed = managed;
        r->async = async;
        atomic_fetch_add(&live_registrations, 1);
    }
    return r;
}

/* Used only when Wasmtime rejected the registration before taking ownership. */
void bridge_registration_abandon(registration *r) {
    free(r);
    atomic_fetch_sub(&live_registrations, 1);
}

void *bridge_sync_callback(void) { return (void *)sync_callback; }
void *bridge_async_callback(void) { return (void *)async_callback; }
void *bridge_registration_finalizer(void) { return (void *)registration_finalize; }

/* Waits for queued work, then joins every worker. Must not be called from a worker. */
void bridge_shutdown(void) {
    check(pthread_mutex_lock(&mutex));
    while (head || busy)
        check(pthread_cond_wait(&drained, &mutex));
    shutting_down = 1;
    check(pthread_cond_broadcast(&available));
    size_t count = thread_count;
    check(pthread_mutex_unlock(&mutex));
    for (size_t i = 0; i < count; i++)
        check(pthread_join(threads[i], NULL));
    check(pthread_mutex_lock(&mutex));
    require(workers == 0 && idle == 0, "worker accounting failed");
    thread_count = 0;
    shutting_down = 0;
    check(pthread_mutex_unlock(&mutex));
}

uint64_t bridge_counter(int which) {
    switch (which) {
    case 0: return atomic_load(&live_registrations);
    case 1: return atomic_load(&completed_requests);
    case 2: return atomic_load(&peak_busy);
    case 3: return atomic_load(&exhaustions);
    case 4: return atomic_load(&owner_ops);
    case 5: return atomic_load(&live_async);
    case 6: return atomic_load(&peak_async);
    case 7: return atomic_load(&async_cancelled);
    case 8: return atomic_load(&staged_deleted);
    case 9: return atomic_load(&unreportable_failures);
    case 10: return atomic_load(&threads_started);
    default: return UINT64_MAX;
    }
}
