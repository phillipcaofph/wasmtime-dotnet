;; Component with long-running exports, used to test epoch and fuel yielding of async calls.
(component
  (core module $m
    (func (export "spin") (param $n i32) (result i32)
      (local $i i32)
      (block $done
        (loop $loop
          local.get $i
          local.get $n
          i32.ge_u
          br_if $done
          local.get $i
          i32.const 1
          i32.add
          local.set $i
          br $loop))
      local.get $i)
    (func (export "forever")
      (loop $loop
        br $loop))
  )
  (core instance $i (instantiate $m))
  (func (export "spin") (param "n" u32) (result u32)
    (canon lift (core func $i "spin")))
  (func (export "forever")
    (canon lift (core func $i "forever")))
)
