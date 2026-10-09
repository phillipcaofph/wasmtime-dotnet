(component
  (import "observe" (func $observe (param "x" s32) (result s32)))
  (core func $lower (canon lower (func $observe)))
  (core module $m
    (type $s (struct (field i32)))
    (type $a (array i32))
    (import "" "observe" (func $observe (param i32) (result i32)))
    (func (export "run") (param $n i32) (result i32)
      (local $sum i32)
      (local $root (ref null $s))
      i32.const 5050
      struct.new $s
      local.set $root
      block $done
        loop $again
          local.get $n
          i32.eqz
          br_if $done
          i32.const 16384
          array.new_default $a
          drop
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
      local.get $root
      struct.get $s 0
      local.get $sum
      i32.ne
      if
        unreachable
      end
      local.get $sum))
  (core instance $i (instantiate $m
    (with "" (instance (export "observe" (func $lower))))))
  (func (export "run") (param "n" s32) (result s32)
    (canon lift (core func $i "run")))
)
