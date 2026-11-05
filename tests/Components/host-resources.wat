;; Component importing a host-defined resource type, used to test host resources.
;;
;; The "host" instance supplies the resource type "counter", its constructor, two methods and a
;; static function, plus free functions taking own<counter> and borrow<counter>. The exports
;; exercise every direction a handle can travel:
;;
;;   roundtrip(initial)  guest creates, increments and reads a counter through borrows, passes a
;;                       borrow to a free function, then drops its own handle (host destructor runs).
;;   make(initial)       guest creates a counter and returns the own handle to the host.
;;   take(c)             host passes an own handle in; guest reads it and drops it (host destructor).
;;   give-back(c)        host passes an own handle in; guest hands it straight back to the host
;;                       through the "consume" import (no destructor runs inside the guest).
;;   inspect(c)          host passes a borrow in; guest forwards it to a method and to "peek".
;;   stash(c)            host passes an own handle in; the guest keeps it in a global (no drop).
;;   unstash()           guest drops the stashed handle, if any (host destructor runs).
;;   swap(a, b)          own + borrow in one call; returns get(a) + get(b) and drops both handles.
;;
;; No memory or realloc is needed: handles and u32 values are all flat scalars.
(component
  (import "host" (instance $host
    (export "counter" (type $counter (sub resource)))
    (export "[constructor]counter" (func (param "initial" u32) (result (own $counter))))
    (export "[method]counter.get" (func (param "self" (borrow $counter)) (result u32)))
    (export "[method]counter.increment" (func (param "self" (borrow $counter))))
    (export "[static]counter.live" (func (result u32)))
    (export "consume" (func (param "c" (own $counter)) (result u32)))
    (export "peek" (func (param "c" (borrow $counter)) (result u32)))))

  (alias export $host "counter" (type $counter))
  (alias export $host "[constructor]counter" (func $ctor))
  (alias export $host "[method]counter.get" (func $get))
  (alias export $host "[method]counter.increment" (func $increment))
  (alias export $host "[static]counter.live" (func $live))
  (alias export $host "consume" (func $consume))
  (alias export $host "peek" (func $peek))

  (core func $ctor_core (canon lower (func $ctor)))
  (core func $get_core (canon lower (func $get)))
  (core func $increment_core (canon lower (func $increment)))
  (core func $live_core (canon lower (func $live)))
  (core func $consume_core (canon lower (func $consume)))
  (core func $peek_core (canon lower (func $peek)))
  (core func $drop_core (canon resource.drop $counter))

  (core module $m
    (import "host" "ctor" (func $ctor (param i32) (result i32)))
    (import "host" "get" (func $get (param i32) (result i32)))
    (import "host" "increment" (func $increment (param i32)))
    (import "host" "live" (func $live (result i32)))
    (import "host" "consume" (func $consume (param i32) (result i32)))
    (import "host" "peek" (func $peek (param i32) (result i32)))
    (import "host" "drop" (func $drop (param i32)))

    ;; roundtrip(initial) -> initial + 1, observed through both the method and "peek".
    (func (export "roundtrip") (param $initial i32) (result i32)
      (local $h i32)
      (local $v i32)
      (local.set $h (call $ctor (local.get $initial)))
      (call $increment (local.get $h))
      (local.set $v (call $get (local.get $h)))
      (if (i32.ne (call $peek (local.get $h)) (local.get $v)) (then (unreachable)))
      ;; Dropping an own handle of a host resource runs the host destructor.
      (call $drop (local.get $h))
      (local.get $v))

    ;; make(initial) -> own<counter>; ownership moves to the caller.
    (func (export "make") (param $initial i32) (result i32)
      (call $ctor (local.get $initial)))

    ;; take(own<counter>) -> value; the guest drops the handle, so the host destructor runs.
    (func (export "take") (param $h i32) (result i32)
      (local $v i32)
      (local.set $v (call $get (local.get $h)))
      (call $drop (local.get $h))
      (local.get $v))

    ;; give-back(own<counter>) -> whatever "consume" returns; ownership moves back to the host.
    (func (export "give-back") (param $h i32) (result i32)
      (call $consume (local.get $h)))

    ;; inspect(borrow<counter>) -> get + peek. A borrow received by an export is a handle in this
    ;; instance's table and must be dropped before returning, or the call traps with
    ;; "borrow handles still remain at the end of the call".
    (func (export "inspect") (param $h i32) (result i32)
      (local $v i32)
      (local.set $v
        (i32.add (call $get (local.get $h)) (call $peek (local.get $h))))
      (call $drop (local.get $h))
      (local.get $v))

    ;; stash(own<counter>): keep the handle in a global. -1 means "nothing stashed".
    (global $stashed (mut i32) (i32.const -1))
    (func (export "stash") (param $h i32)
      (if (i32.ne (global.get $stashed) (i32.const -1)) (then (unreachable)))
      (global.set $stashed (local.get $h)))

    ;; unstash(): drop the stashed handle, which runs the host destructor.
    (func (export "unstash")
      (if (i32.eq (global.get $stashed) (i32.const -1)) (then (return)))
      (call $drop (global.get $stashed))
      (global.set $stashed (i32.const -1)))

    ;; swap(own<counter>, borrow<counter>) -> get(a) + get(b); drops both handles.
    (func (export "swap") (param $a i32) (param $b i32) (result i32)
      (local $v i32)
      (local.set $v (i32.add (call $get (local.get $a)) (call $get (local.get $b))))
      (call $drop (local.get $a))
      (call $drop (local.get $b))
      (local.get $v))

    ;; live() -> the host's count of live counters, forwarded from the static function.
    (func (export "live") (result i32)
      (call $live))
  )

  (core instance $i (instantiate $m
    (with "host" (instance
      (export "ctor" (func $ctor_core))
      (export "get" (func $get_core))
      (export "increment" (func $increment_core))
      (export "live" (func $live_core))
      (export "consume" (func $consume_core))
      (export "peek" (func $peek_core))
      (export "drop" (func $drop_core))))))

  (func (export "roundtrip") (param "initial" u32) (result u32)
    (canon lift (core func $i "roundtrip")))
  (func (export "make") (param "initial" u32) (result (own $counter))
    (canon lift (core func $i "make")))
  (func (export "take") (param "c" (own $counter)) (result u32)
    (canon lift (core func $i "take")))
  (func (export "give-back") (param "c" (own $counter)) (result u32)
    (canon lift (core func $i "give-back")))
  (func (export "inspect") (param "c" (borrow $counter)) (result u32)
    (canon lift (core func $i "inspect")))
  (func (export "live") (result u32)
    (canon lift (core func $i "live")))
  (func (export "stash") (param "c" (own $counter))
    (canon lift (core func $i "stash")))
  (func (export "unstash")
    (canon lift (core func $i "unstash")))
  (func (export "swap") (param "a" (own $counter)) (param "b" (borrow $counter)) (result u32)
    (canon lift (core func $i "swap")))
)
