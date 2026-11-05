(component
  (core module $d (func (export "dtor") (param i32)))
  (core instance $di (instantiate $d))
  (type $w (resource (rep i32) (dtor (core func $di "dtor"))))
  (import "take" (func (param "w" (own $w))))
)
