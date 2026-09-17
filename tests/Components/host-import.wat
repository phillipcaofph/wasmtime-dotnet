;; Component importing a "host" instance, used to test host function definitions.
;;
;; "transform" is lowered and called by "run", so defining it exercises a real guest-to-host
;; call. "greet" is only imported, so it must still be defined for instantiation to succeed.
;;
;; Rebuild with:
;;   wasm-tools parse tests/Components/host-import.wat -o tests/Components/host-import.wasm
(component
  (import "host" (instance $h
    (export "transform" (func (param "x" s32) (result s32)))
    (export "greet" (func (param "name" string) (result string)))))
  (alias export $h "transform" (func $transform))
  (core func $transform_core (canon lower (func $transform)))
  (core module $m
    (import "host" "transform" (func $transform (param i32) (result i32)))
    (func (export "run") (param i32) (result i32)
      local.get 0
      call $transform)
  )
  (core instance $i (instantiate $m
    (with "host" (instance (export "transform" (func $transform_core))))))
  (func (export "run") (param "x" s32) (result s32)
    (canon lift (core func $i "run")))
)
