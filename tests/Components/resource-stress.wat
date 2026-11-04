;; Component importing the host resource "counter" with host functions that take and return many
;; resources at once, used by the resource stress tests.
;;
;; "host" supplies counter and:
;;   [method]counter.get  borrow<counter> -> u32
;;   make-batch(n)        -> list<own<counter>>         the host creates n counters
;;   sum-batch(xs)        list<borrow<counter>> -> u32  the host reads every counter
;;   consume-batch(xs)    list<own<counter>> -> u32     the host receives every counter
;;
;;   run(n)      asks the host for n counters, reads each through get (one borrow per call), drops
;;               the first half (one host destructor each) and moves the second half to
;;               consume-batch. Returns the sum of the reads + consume-batch.
;;   sum-all(n)  asks the host for n counters, lends all of them to sum-batch in one call, then
;;               drops them all. Returns sum-batch.
;;
;; The libc instance supplies memory and a bump allocator that wraps around: no allocation
;; outlives one call of run.
(component
  (import "host" (instance $host
    (export "counter" (type $counter (sub resource)))
    (export "[method]counter.get" (func (param "self" (borrow $counter)) (result u32)))
    (export "make-batch" (func (param "n" u32) (result (list (own $counter)))))
    (export "sum-batch" (func (param "xs" (list (borrow $counter))) (result u32)))
    (export "consume-batch" (func (param "xs" (list (own $counter))) (result u32)))))

  (alias export $host "counter" (type $counter))
  (alias export $host "[method]counter.get" (func $get))
  (alias export $host "make-batch" (func $make))
  (alias export $host "sum-batch" (func $sum))
  (alias export $host "consume-batch" (func $consume))

  (core module $libc
    (memory (export "memory") 1)
    (global $bump (mut i32) (i32.const 1024))
    (func (export "realloc") (param $old i32) (param $oldsz i32) (param $align i32) (param $n i32) (result i32)
      (local $r i32)
      (if (i32.ne (local.get $old) (i32.const 0)) (then (unreachable)))
      (if (i32.gt_u (local.get $n) (i32.const 16384)) (then (unreachable)))
      (if (i32.gt_u (i32.add (global.get $bump) (local.get $n)) (i32.const 65536))
        (then (global.set $bump (i32.const 1024))))
      (local.set $r (global.get $bump))
      (global.set $bump
        (i32.and (i32.add (i32.add (global.get $bump) (local.get $n)) (i32.const 7)) (i32.const -8)))
      (local.get $r)))
  (core instance $libc_i (instantiate $libc))

  (core func $make_core
    (canon lower (func $make) (memory (core memory $libc_i "memory")) (realloc (core func $libc_i "realloc"))))
  (core func $sum_core (canon lower (func $sum) (memory (core memory $libc_i "memory"))))
  (core func $consume_core (canon lower (func $consume) (memory (core memory $libc_i "memory"))))
  (core func $get_core (canon lower (func $get)))
  (core func $drop_core (canon resource.drop $counter))

  (core module $m
    (import "libc" "memory" (memory 1))
    (import "host" "get" (func $get (param i32) (result i32)))
    (import "host" "make" (func $make (param i32 i32)))
    (import "host" "sum" (func $sum (param i32 i32) (result i32)))
    (import "host" "consume" (func $consume (param i32 i32) (result i32)))
    (import "host" "drop" (func $drop (param i32)))

    ;; Asks the host for n counters; returns the address of the handle array.
    (func $make-checked (param $n i32) (result i32)
      ;; Bytes 0..7 receive the list result (pointer, length).
      (call $make (local.get $n) (i32.const 0))
      (if (i32.ne (i32.load (i32.const 4)) (local.get $n)) (then (unreachable)))
      (i32.load (i32.const 0)))

    (func $handle (param $ptr i32) (param $i i32) (result i32)
      (i32.load (i32.add (local.get $ptr) (i32.shl (local.get $i) (i32.const 2)))))

    (func (export "run") (param $n i32) (result i32)
      (local $ptr i32)
      (local $half i32)
      (local $i i32)
      (local $total i32)
      (local.set $ptr (call $make-checked (local.get $n)))

      ;; Each own handle is lent as a borrow to one get call; the guest keeps it.
      (block $done
        (loop $next
          (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
          (local.set $total
            (i32.add (local.get $total) (call $get (call $handle (local.get $ptr) (local.get $i)))))
          (local.set $i (i32.add (local.get $i) (i32.const 1)))
          (br $next)))

      (local.set $half (i32.shr_u (local.get $n) (i32.const 1)))
      (local.set $i (i32.const 0))
      (block $done
        (loop $next
          (br_if $done (i32.ge_u (local.get $i) (local.get $half)))
          (call $drop (call $handle (local.get $ptr) (local.get $i)))
          (local.set $i (i32.add (local.get $i) (i32.const 1)))
          (br $next)))

      (i32.add (local.get $total)
        (call $consume
          (i32.add (local.get $ptr) (i32.shl (local.get $half) (i32.const 2)))
          (i32.sub (local.get $n) (local.get $half)))))

    (func (export "sum-all") (param $n i32) (result i32)
      (local $ptr i32)
      (local $i i32)
      (local $total i32)
      (local.set $ptr (call $make-checked (local.get $n)))
      (local.set $total (call $sum (local.get $ptr) (local.get $n)))
      (block $done
        (loop $next
          (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
          (call $drop (call $handle (local.get $ptr) (local.get $i)))
          (local.set $i (i32.add (local.get $i) (i32.const 1)))
          (br $next)))
      (local.get $total)))

  (core instance $i (instantiate $m
    (with "libc" (instance $libc_i))
    (with "host" (instance
      (export "get" (func $get_core))
      (export "make" (func $make_core))
      (export "sum" (func $sum_core))
      (export "consume" (func $consume_core))
      (export "drop" (func $drop_core))))))

  (func (export "run") (param "n" u32) (result u32)
    (canon lift (core func $i "run")))
  (func (export "sum-all") (param "n" u32) (result u32)
    (canon lift (core func $i "sum-all")))
)
