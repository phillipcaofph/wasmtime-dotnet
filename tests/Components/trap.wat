;; Component whose exported function always traps, used to test trap behaviour.
;;
;; Rebuild with:
;;   wasm-tools parse tests/Components/trap.wat -o tests/Components/trap.wasm
(component
  (core module $m
    (func (export "boom") (result i32)
      unreachable)
  )
  (core instance $i (instantiate $m))
  (func (export "boom") (result s32)
    (canon lift (core func $i "boom")))
)
