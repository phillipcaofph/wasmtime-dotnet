;; Component exporting sum(xs: list<u32>) -> u32: a container parameter whose type carries no
;; resource, used to check that a resource value nested there is rejected before the call.
(component
  (core module $m
    (memory (export "memory") 1)
    (global $next (mut i32) (i32.const 1024))

    ;; Bump allocator: never frees, which is enough for a test.
    (func (export "realloc") (param $old i32) (param $old_size i32) (param $align i32) (param $size i32) (result i32)
      (local $ptr i32)
      (local.set $ptr
        (i32.and
          (i32.add (global.get $next) (i32.sub (local.get $align) (i32.const 1)))
          (i32.sub (i32.const 0) (local.get $align))))
      (global.set $next (i32.add (local.get $ptr) (local.get $size)))
      (local.get $ptr))

    (func (export "sum") (param $ptr i32) (param $len i32) (result i32)
      (local $total i32)
      (block $done
        (loop $next
          (br_if $done (i32.eqz (local.get $len)))
          (local.set $total (i32.add (local.get $total) (i32.load (local.get $ptr))))
          (local.set $ptr (i32.add (local.get $ptr) (i32.const 4)))
          (local.set $len (i32.sub (local.get $len) (i32.const 1)))
          (br $next)))
      (local.get $total)))

  (core instance $i (instantiate $m))

  (func (export "sum") (param "xs" (list u32)) (result u32)
    (canon lift (core func $i "sum") (memory (core memory $i "memory")) (realloc (core func $i "realloc"))))
)
