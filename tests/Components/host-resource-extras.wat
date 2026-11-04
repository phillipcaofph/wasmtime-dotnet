;; Component importing the host resource "counter" through two instances, used for the host
;; resource scenarios host-resources.wat cannot express.
;;
;; "host" supplies counter, its constructor and get, plus:
;;   pair()       -> tuple<own<counter>, own<counter>>   (a composite result: the second element
;;                                                        can fail after the first was written)
;;   relay(c)     own<counter> -> own<counter>           (a callback that may return its own argument)
;; "other" supplies a second import of counter (the host defines the same type under both names)
;; and its get method.
;;
;;   pair-sum()        calls pair, returns get(a) + get(b) and drops both handles.
;;   relay-keep(c)     passes c through relay and keeps the result in a global (no drop).
;;   drop-kept()       drops the kept handle, if any.
;;   make(initial)     creates a counter through "host" and returns the own handle.
;;   other-take(c)     own<other.counter>: reads it through "other" and drops it.
;;
;; Memory only serves as the return area of pair.
(component
  (import "host" (instance $host
    (export "counter" (type $counter (sub resource)))
    (export "[constructor]counter" (func (param "initial" u32) (result (own $counter))))
    (export "[method]counter.get" (func (param "self" (borrow $counter)) (result u32)))
    (export "pair" (func (result (tuple (own $counter) (own $counter)))))
    (export "relay" (func (param "c" (own $counter)) (result (own $counter))))))
  (import "other" (instance $other
    (export "counter" (type $counter (sub resource)))
    (export "[method]counter.get" (func (param "self" (borrow $counter)) (result u32)))))

  (alias export $host "counter" (type $counter))
  (alias export $other "counter" (type $other_counter))
  (alias export $host "[constructor]counter" (func $ctor))
  (alias export $host "[method]counter.get" (func $get))
  (alias export $host "pair" (func $pair))
  (alias export $host "relay" (func $relay))
  (alias export $other "[method]counter.get" (func $other_get))

  (core module $memory_module (memory (export "mem") 1))
  (core instance $memory_instance (instantiate $memory_module))

  (core func $ctor_core (canon lower (func $ctor)))
  (core func $get_core (canon lower (func $get)))
  (core func $pair_core (canon lower (func $pair) (memory (core memory $memory_instance "mem"))))
  (core func $relay_core (canon lower (func $relay)))
  (core func $other_get_core (canon lower (func $other_get)))
  (core func $drop_core (canon resource.drop $counter))
  (core func $other_drop_core (canon resource.drop $other_counter))

  (core module $m
    (import "host" "ctor" (func $ctor (param i32) (result i32)))
    (import "host" "get" (func $get (param i32) (result i32)))
    (import "host" "pair" (func $pair (param i32)))
    (import "host" "relay" (func $relay (param i32) (result i32)))
    (import "host" "other-get" (func $other_get (param i32) (result i32)))
    (import "host" "drop" (func $drop (param i32)))
    (import "host" "other-drop" (func $other_drop (param i32)))
    (import "host" "mem" (memory 1))

    (func (export "pair-sum") (result i32)
      (local $v i32)
      (call $pair (i32.const 0))
      (local.set $v
        (i32.add (call $get (i32.load (i32.const 0))) (call $get (i32.load (i32.const 4)))))
      (call $drop (i32.load (i32.const 0)))
      (call $drop (i32.load (i32.const 4)))
      (local.get $v))

    ;; -1 means "nothing kept".
    (global $kept (mut i32) (i32.const -1))
    (func (export "relay-keep") (param $h i32)
      (if (i32.ne (global.get $kept) (i32.const -1)) (then (unreachable)))
      (global.set $kept (call $relay (local.get $h))))

    (func (export "drop-kept")
      (if (i32.eq (global.get $kept) (i32.const -1)) (then (return)))
      (call $drop (global.get $kept))
      (global.set $kept (i32.const -1)))

    (func (export "make") (param $initial i32) (result i32)
      (call $ctor (local.get $initial)))

    (func (export "other-take") (param $h i32) (result i32)
      (local $v i32)
      (local.set $v (call $other_get (local.get $h)))
      (call $other_drop (local.get $h))
      (local.get $v)))

  (core instance $i (instantiate $m
    (with "host" (instance
      (export "ctor" (func $ctor_core))
      (export "get" (func $get_core))
      (export "pair" (func $pair_core))
      (export "relay" (func $relay_core))
      (export "other-get" (func $other_get_core))
      (export "drop" (func $drop_core))
      (export "other-drop" (func $other_drop_core))
      (export "mem" (memory $memory_instance "mem"))))))

  (func (export "pair-sum") (result u32)
    (canon lift (core func $i "pair-sum")))
  (func (export "relay-keep") (param "c" (own $counter))
    (canon lift (core func $i "relay-keep")))
  (func (export "drop-kept")
    (canon lift (core func $i "drop-kept")))
  (func (export "make") (param "initial" u32) (result (own $counter))
    (canon lift (core func $i "make")))
  (func (export "other-take") (param "c" (own $other_counter)) (result u32)
    (canon lift (core func $i "other-take")))
)
