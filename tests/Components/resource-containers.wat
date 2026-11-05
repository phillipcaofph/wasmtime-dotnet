;; Component importing the host resource "counter" and passing handles inside composite values,
;; used to test nested resource marshalling and nested validation.
;;
;;   make-many(n)        -> list<own<counter>>        guest creates n counters (values 0..n-1) and
;;                                                     returns every own handle to the host.
;;   sum(xs)             list<borrow<counter>> -> u32 guest reads each borrow and drops each handle.
;;   maybe(x)            option<own<counter>> -> u32  value of the counter (dropped), or 4294967295.
;;   pair(p)             tuple<own<counter>, u32>     value + p.1; the counter is dropped.
;;   rec(r)              record { a: own<counter>, b: borrow<counter> } -> u32: a + b; both dropped.
;;   maybe-make(flag)    -> option<own<counter>>      some(new counter 77) when flag != 0.
;;
;; Memory holds list and option results (retptr) and list parameters (bump allocator realloc).
(component
  (import "host" (instance $host
    (export "counter" (type $counter (sub resource)))
    (export "[constructor]counter" (func (param "initial" u32) (result (own $counter))))
    (export "[method]counter.get" (func (param "self" (borrow $counter)) (result u32)))))
  (alias export $host "counter" (type $counter))
  (alias export $host "[constructor]counter" (func $ctor))
  (alias export $host "[method]counter.get" (func $get))
  (core func $ctor_core (canon lower (func $ctor)))
  (core func $get_core (canon lower (func $get)))
  (core func $drop_core (canon resource.drop $counter))

  (core module $m
    (import "host" "ctor" (func $ctor (param i32) (result i32)))
    (import "host" "get" (func $get (param i32) (result i32)))
    (import "host" "drop" (func $drop (param i32)))
    (memory (export "mem") 1)
    ;; 0..15: result area; 64..1023: make-many handles; 1024..: bump allocator for realloc.
    (global $bump (mut i32) (i32.const 1024))

    (func (export "realloc") (param $old i32) (param $old_size i32) (param $align i32) (param $size i32)
      (result i32)
      (local $p i32)
      (if (i32.ne (local.get $old) (i32.const 0)) (then (unreachable)))
      (local.set $p
        (i32.and (i32.add (global.get $bump) (i32.sub (local.get $align) (i32.const 1)))
                 (i32.sub (i32.const 0) (local.get $align))))
      (global.set $bump (i32.add (local.get $p) (local.get $size)))
      (if (i32.gt_u (global.get $bump) (i32.const 65536)) (then (unreachable)))
      (local.get $p))

    (func (export "make-many") (param $n i32) (result i32)
      (local $i i32)
      (if (i32.gt_u (local.get $n) (i32.const 200)) (then (unreachable)))
      (block $done
        (loop $next
          (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
          (i32.store (i32.add (i32.const 64) (i32.shl (local.get $i) (i32.const 2)))
                     (call $ctor (local.get $i)))
          (local.set $i (i32.add (local.get $i) (i32.const 1)))
          (br $next)))
      (i32.store (i32.const 0) (i32.const 64))
      (i32.store (i32.const 4) (local.get $n))
      (i32.const 0))

    (func (export "sum") (param $ptr i32) (param $len i32) (result i32)
      (local $i i32) (local $h i32) (local $total i32)
      (block $done
        (loop $next
          (br_if $done (i32.ge_u (local.get $i) (local.get $len)))
          (local.set $h (i32.load (i32.add (local.get $ptr) (i32.shl (local.get $i) (i32.const 2)))))
          (local.set $total (i32.add (local.get $total) (call $get (local.get $h))))
          (call $drop (local.get $h))
          (local.set $i (i32.add (local.get $i) (i32.const 1)))
          (br $next)))
      ;; Reset the allocator: the host does not keep list parameters past the call.
      (global.set $bump (i32.const 1024))
      (local.get $total))

    (func (export "maybe") (param $disc i32) (param $h i32) (result i32)
      (local $v i32)
      (if (i32.eqz (local.get $disc)) (then (return (i32.const -1))))
      (local.set $v (call $get (local.get $h)))
      (call $drop (local.get $h))
      (local.get $v))

    (func (export "pair") (param $h i32) (param $x i32) (result i32)
      (local $v i32)
      (local.set $v (i32.add (call $get (local.get $h)) (local.get $x)))
      (call $drop (local.get $h))
      (local.get $v))

    (func (export "rec") (param $a i32) (param $b i32) (result i32)
      (local $v i32)
      (local.set $v (i32.add (call $get (local.get $a)) (call $get (local.get $b))))
      (call $drop (local.get $a))
      (call $drop (local.get $b))
      (local.get $v))

    (func (export "maybe-make") (param $flag i32) (result i32)
      (if (i32.eqz (local.get $flag))
        (then (i32.store8 (i32.const 0) (i32.const 0)))
        (else
          (i32.store8 (i32.const 0) (i32.const 1))
          (i32.store (i32.const 4) (call $ctor (i32.const 77)))))
      (i32.const 0)))

  (core instance $i (instantiate $m
    (with "host" (instance
      (export "ctor" (func $ctor_core))
      (export "get" (func $get_core))
      (export "drop" (func $drop_core))))))

  (func (export "make-many") (param "n" u32) (result (list (own $counter)))
    (canon lift (core func $i "make-many") (memory (core memory $i "mem"))))
  (func (export "sum") (param "xs" (list (borrow $counter))) (result u32)
    (canon lift (core func $i "sum") (memory (core memory $i "mem")) (realloc (core func $i "realloc"))))
  (func (export "maybe") (param "x" (option (own $counter))) (result u32)
    (canon lift (core func $i "maybe")))
  (func (export "pair") (param "p" (tuple (own $counter) u32)) (result u32)
    (canon lift (core func $i "pair")))
  (type $rec (record (field "a" (own $counter)) (field "b" (borrow $counter))))
  (export $rec_export "rec-arg" (type $rec))
  (func (export "rec") (param "r" $rec_export) (result u32)
    (canon lift (core func $i "rec")))
  (func (export "maybe-make") (param "flag" u32) (result (option (own $counter)))
    (canon lift (core func $i "maybe-make") (memory (core memory $i "mem"))))
)
