;; Component defining and exporting its own resource type "widget", used to test guest resources
;; held by the host.
;;
;; The resource representation is the u32 value passed to the constructor. The core destructor
;; counts drops, records the last dropped representation, and reports the drop to the host
;; through the "host" "dropped" import so a test can observe it without calling back in.
;;
;;   [constructor]widget(value)  -> own<widget>   (lifted directly from canon resource.new)
;;   [method]widget.value(self)  -> u32           (borrow of a type this component defines
;;                                                 arrives as the representation, not a handle)
;;   consume(own<widget>)        -> u32           (reads the representation, then drops the
;;                                                 handle, which runs the destructor)
;;   consume-with(own, borrow)   -> u32           (rep of the own + the borrow's rep, then drops
;;                                                 the own; used for the "same handle as own and
;;                                                 borrow in one call" negative test)
;;   drops() / last-drop()       -> u32           (destructor bookkeeping)
(component
  (import "host" (instance $host
    (export "dropped" (func (param "rep" u32)))))
  (alias export $host "dropped" (func $dropped))
  (core func $dropped_core (canon lower (func $dropped)))

  ;; The destructor lives in its own core instance because the resource type must exist before
  ;; the functions that create and drop it, and the type refers to the destructor.
  (core module $dtor_module
    (import "host" "dropped" (func $dropped (param i32)))
    (global $drops (mut i32) (i32.const 0))
    (global $last_drop (mut i32) (i32.const -1))
    (func (export "dtor") (param $rep i32)
      (global.set $drops (i32.add (global.get $drops) (i32.const 1)))
      (global.set $last_drop (local.get $rep))
      (call $dropped (local.get $rep)))
    (func (export "drops") (result i32) (global.get $drops))
    (func (export "last-drop") (result i32) (global.get $last_drop)))
  (core instance $dtor_instance (instantiate $dtor_module
    (with "host" (instance (export "dropped" (func $dropped_core))))))

  (type $widget_def (resource (rep i32) (dtor (core func $dtor_instance "dtor"))))
  ;; Exported function signatures may only mention exported (or imported) resource types, so
  ;; export the type first and use the exported index from here on.
  (export $widget "widget" (type $widget_def))

  (core func $new (canon resource.new $widget))
  (core func $rep (canon resource.rep $widget))
  (core func $drop (canon resource.drop $widget))

  (core module $m
    (import "" "rep" (func $rep (param i32) (result i32)))
    (import "" "drop" (func $drop (param i32)))

    ;; The borrow parameter is the representation itself because this component owns the type.
    (func (export "[method]widget.value") (param $self i32) (result i32)
      (local.get $self))

    (func (export "consume") (param $h i32) (result i32)
      (local $v i32)
      (local.set $v (call $rep (local.get $h)))
      (call $drop (local.get $h))
      (local.get $v))

    (func (export "consume-with") (param $h i32) (param $peek i32) (result i32)
      (local $v i32)
      (local.set $v (i32.add (call $rep (local.get $h)) (local.get $peek)))
      (call $drop (local.get $h))
      (local.get $v)))
  (core instance $i (instantiate $m
    (with "" (instance
      (export "rep" (func $rep))
      (export "drop" (func $drop))))))

  (func (export "[constructor]widget") (param "value" u32) (result (own $widget))
    (canon lift (core func $new)))
  (func (export "[method]widget.value") (param "self" (borrow $widget)) (result u32)
    (canon lift (core func $i "[method]widget.value")))
  (func (export "consume") (param "w" (own $widget)) (result u32)
    (canon lift (core func $i "consume")))
  (func (export "consume-with") (param "w" (own $widget)) (param "peek" (borrow $widget)) (result u32)
    (canon lift (core func $i "consume-with")))
  (func (export "drops") (result u32)
    (canon lift (core func $dtor_instance "drops")))
  (func (export "last-drop") (result u32)
    (canon lift (core func $dtor_instance "last-drop")))
)
