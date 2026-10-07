#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

typedef struct request {
    void *args;
    size_t nargs;
    void *results;
    size_t nresults;
    struct request *next;
    pthread_cond_t completed;
    void *error;
    int done;
} request;

typedef struct bridge {
    pthread_mutex_t mutex;
    pthread_cond_t available;
    request *head;
    request *tail;
    size_t active;
    int closed;
} bridge;

static atomic_uint_fast64_t live_bridges;
static atomic_uint_fast64_t completed_requests;

static void check(int result) {
    if (result != 0) {
        fprintf(stderr, "callback bridge: pthread operation failed (%d)\n", result);
        abort();
    }
}

static void require(int condition) {
    if (!condition) {
        fputs("callback bridge: invalid lifetime or request state\n", stderr);
        abort();
    }
}

bridge *bridge_create(void) {
    bridge *b = calloc(1, sizeof(*b));
    if (!b) {
        perror("callback bridge allocation");
        return NULL;
    }
    check(pthread_mutex_init(&b->mutex, NULL));
    check(pthread_cond_init(&b->available, NULL));
    atomic_fetch_add(&live_bridges, 1);
    return b;
}

/* This is the actual Wasmtime callback. It never enters the managed runtime. */
static void *callback(void *env, void *context, void *type, void *args,
                      size_t nargs, void *results, size_t nresults) {
    (void)context;
    (void)type;
    bridge *b = env;
    request r = {.args = args, .nargs = nargs,
                 .results = results, .nresults = nresults};
    check(pthread_cond_init(&r.completed, NULL));
    check(pthread_mutex_lock(&b->mutex));
    require(!b->closed);
    b->active++;
    if (b->tail)
        b->tail->next = &r;
    else
        b->head = &r;
    b->tail = &r;
    check(pthread_cond_signal(&b->available));
    while (!r.done)
        check(pthread_cond_wait(&r.completed, &b->mutex));
    b->active--;
    check(pthread_mutex_unlock(&b->mutex));
    check(pthread_cond_destroy(&r.completed));
    return r.error;
}

/* Wasmtime owns this registration and calls its finalizer on success or error. */
static void finalize(void *env) {
    bridge *b = env;
    check(pthread_mutex_lock(&b->mutex));
    require(!b->closed && b->active == 0);
    b->closed = 1;
    check(pthread_cond_broadcast(&b->available));
    check(pthread_mutex_unlock(&b->mutex));
}

typedef void *(*callback_type)(void *, void *, void *, void *, size_t, void *, size_t);
typedef void (*finalizer_type)(void *);

callback_type bridge_callback(void) { return callback; }
finalizer_type bridge_finalizer(void) { return finalize; }

request *bridge_wait(bridge *b) {
    check(pthread_mutex_lock(&b->mutex));
    while (!b->head && !b->closed)
        check(pthread_cond_wait(&b->available, &b->mutex));
    request *r = b->head;
    if (r) {
        b->head = r->next;
        if (!b->head)
            b->tail = NULL;
    }
    check(pthread_mutex_unlock(&b->mutex));
    return r;
}

void bridge_arguments(request *r, void **args, size_t *nargs,
                      void **results, size_t *nresults) {
    *args = r->args;
    *nargs = r->nargs;
    *results = r->results;
    *nresults = r->nresults;
}

void bridge_complete(bridge *b, request *r, void *error) {
    check(pthread_mutex_lock(&b->mutex));
    require(!r->done);
    r->error = error;
    r->done = 1;
    atomic_fetch_add(&completed_requests, 1);
    check(pthread_cond_signal(&r->completed));
    check(pthread_mutex_unlock(&b->mutex));
}

void bridge_destroy(bridge *b) {
    require(b->closed && b->active == 0 && !b->head);
    check(pthread_cond_destroy(&b->available));
    check(pthread_mutex_destroy(&b->mutex));
    free(b);
    atomic_fetch_sub(&live_bridges, 1);
}

uint64_t bridge_live(void) { return atomic_load(&live_bridges); }
uint64_t bridge_completed(void) { return atomic_load(&completed_requests); }
