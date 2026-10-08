;; Component whose exported function always traps, used to test trap behaviour.
(component
  (core module $m
    (func (export "boom") (result i32)
      unreachable)
  )
  (core instance $i (instantiate $m))
  (func (export "boom") (result s32)
    (canon lift (core func $i "boom")))
)
