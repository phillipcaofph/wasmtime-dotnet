(component
  (import "observe" (func $observe (param "x" s32) (result s32)))
  (core func $lower (canon lower (func $observe)))
  (core module $m
    (import "" "observe" (func $observe (param i32) (result i32)))
    (func $run (export "run") (param $n i32) (result i32)
      (local $sum i32)
      block $done
        loop $again
          local.get $n
          i32.eqz
          br_if $done
          local.get $sum
          local.get $n
          call $observe
          i32.add
          local.set $sum
          local.get $n
          i32.const 1
          i32.sub
          local.set $n
          br $again
        end
      end
      local.get $sum)
    (func (export "spin") (param $n i32) (result i32)
      loop $again
        local.get $n
        i32.const 1
        i32.sub
        local.tee $n
        br_if $again
      end
      i32.const 42)
    (func $boom (export "boom")
      unreachable))
  (core instance $i (instantiate $m
    (with "" (instance (export "observe" (func $lower))))))
  (func (export "run") (param "n" s32) (result s32)
    (canon lift (core func $i "run")))
  (func (export "spin") (param "n" s32) (result s32)
    (canon lift (core func $i "spin")))
  (func (export "boom")
    (canon lift (core func $i "boom")))
)
