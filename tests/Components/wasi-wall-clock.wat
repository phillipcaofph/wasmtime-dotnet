(component
  (import "wasi:clocks/wall-clock@0.2.12" (instance $clock
    (type $datetime' (record
      (field "seconds" u64)
      (field "nanoseconds" u32)))
    (export "datetime" (type $datetime (eq $datetime')))
    (export "now" (func (result $datetime)))
    (export "resolution" (func (result $datetime)))))
  (alias export $clock "now" (func $now))
  (alias export $clock "resolution" (func $resolution))
  (core module $memory
    (memory (export "memory") 1))
  (core instance $memory-instance (instantiate $memory))
  (alias core export $memory-instance "memory" (core memory $linear-memory))
  (core func $now-core
    (canon lower (func $now) (memory $linear-memory)))
  (core func $resolution-core
    (canon lower (func $resolution) (memory $linear-memory)))
  (core module $check
    (import "" "memory" (memory 1))
    (import "" "now" (func $now (param i32)))
    (import "" "resolution" (func $resolution (param i32)))
    (func $start
      i32.const 0
      call $now
      i32.const 0
      i64.load
      i64.const 1738512306
      i64.ne
      if unreachable end
      i32.const 8
      i32.load
      i32.const 700
      i32.ne
      if unreachable end
      i32.const 16
      call $resolution
      i32.const 16
      i64.load
      i64.const 0
      i64.ne
      if unreachable end
      i32.const 24
      i32.load
      i32.const 2000000
      i32.ne
      if unreachable end)
    (start $start))
  (core instance (instantiate $check
    (with "" (instance
      (export "memory" (memory $linear-memory))
      (export "now" (func $now-core))
      (export "resolution" (func $resolution-core))))))
)
