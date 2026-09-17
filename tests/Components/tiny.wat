;; Minimal component with no imports, used to test instantiation and calling.
;;
;; Rebuild with:
;;   wasm-tools parse tests/Components/tiny.wat -o tests/Components/tiny.wasm
(component
  (core module $m
    (func (export "add") (param i32 i32) (result i32)
      local.get 0
      local.get 1
      i32.add)
  )
  (core instance $i (instantiate $m))
  (func (export "add") (param "a" s32) (param "b" s32) (result s32)
    (canon lift (core func $i "add")))
)
