;; A component that uses a resource type defined by the host.
;;
;;   import host.counter: resource
;;     constructor(initial: u32)
;;     increment: func(self: borrow<counter>)
;;     get: func(self: borrow<counter>) -> u32
;;
;;   export tally: func(initial: u32) -> u32    creates a counter, increments it twice, reads it,
;;                                               then drops it (the host destructor runs).
;;   export make: func(initial: u32) -> own<counter>
;;                                               creates a counter and moves it to the caller.
;;   export bump: func(c: borrow<counter>) -> u32
;;                                               increments a counter lent by the caller.
;;   export take: func(c: own<counter>) -> u32  reads a counter moved in by the caller, then drops it.
;;   export keep: func(c: own<counter>)         keeps a counter moved in by the caller.
;;
;; Handles and u32 values are flat scalars, so no memory is needed.
(component
  (import "host" (instance $host
    (export "counter" (type $counter (sub resource)))
    (export "[constructor]counter" (func (param "initial" u32) (result (own $counter))))
    (export "[method]counter.increment" (func (param "self" (borrow $counter))))
    (export "[method]counter.get" (func (param "self" (borrow $counter)) (result u32)))))

  (alias export $host "counter" (type $counter))
  (alias export $host "[constructor]counter" (func $ctor))
  (alias export $host "[method]counter.increment" (func $increment))
  (alias export $host "[method]counter.get" (func $get))

  (core func $ctor_core (canon lower (func $ctor)))
  (core func $increment_core (canon lower (func $increment)))
  (core func $get_core (canon lower (func $get)))
  (core func $drop_core (canon resource.drop $counter))

  (core module $m
    (import "host" "ctor" (func $ctor (param i32) (result i32)))
    (import "host" "increment" (func $increment (param i32)))
    (import "host" "get" (func $get (param i32) (result i32)))
    (import "host" "drop" (func $drop (param i32)))

    (global $kept (mut i32) (i32.const -1))

    (func (export "tally") (param $initial i32) (result i32)
      (local $h i32)
      (local $v i32)
      (local.set $h (call $ctor (local.get $initial)))
      (call $increment (local.get $h))
      (call $increment (local.get $h))
      (local.set $v (call $get (local.get $h)))
      (call $drop (local.get $h))
      (local.get $v))

    (func (export "make") (param $initial i32) (result i32)
      (call $ctor (local.get $initial)))

    ;; A borrow received by an export must be dropped before returning.
    (func (export "bump") (param $h i32) (result i32)
      (local $v i32)
      (call $increment (local.get $h))
      (local.set $v (call $get (local.get $h)))
      (call $drop (local.get $h))
      (local.get $v))

    (func (export "take") (param $h i32) (result i32)
      (local $v i32)
      (local.set $v (call $get (local.get $h)))
      (call $drop (local.get $h))
      (local.get $v))

    (func (export "keep") (param $h i32)
      (if (i32.ne (global.get $kept) (i32.const -1)) (then (call $drop (global.get $kept))))
      (global.set $kept (local.get $h)))
  )

  (core instance $i (instantiate $m
    (with "host" (instance
      (export "ctor" (func $ctor_core))
      (export "increment" (func $increment_core))
      (export "get" (func $get_core))
      (export "drop" (func $drop_core))))))

  (func (export "tally") (param "initial" u32) (result u32)
    (canon lift (core func $i "tally")))
  (func (export "make") (param "initial" u32) (result (own $counter))
    (canon lift (core func $i "make")))
  (func (export "bump") (param "c" (borrow $counter)) (result u32)
    (canon lift (core func $i "bump")))
  (func (export "take") (param "c" (own $counter)) (result u32)
    (canon lift (core func $i "take")))
  (func (export "keep") (param "c" (own $counter))
    (canon lift (core func $i "keep")))
)
