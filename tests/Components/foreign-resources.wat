;; Component that obtains a WASI p2 resource (wasi:io/streams output-stream, a host::<T> type
;; defined by Wasmtime's WASI implementation, not by the binding) and passes it to a user-defined
;; import as borrow and as own. Used to test "foreign" host resource types in host callbacks.
;;
;;   run-borrow()  gets stdout, passes a borrow to "my:host/log.peek", drops its own handle.
;;   run-own()     gets stdout, passes the own handle to "my:host/log.keep" (host now owns it).
;;   give()        gets stdout and returns the own handle to the host.
;;   put(s)        takes an own output-stream from the host and drops it.
;;   run-relay()   asks the host for an own output-stream through "my:host/log.relay" (a host callback
;;                 returning a foreign own) and drops it; returns 1.
;;   run-echo()    gets stdout, passes the own handle to "my:host/log.echo", which returns an own
;;                 output-stream, and drops what came back; returns 1.
;;   run-echo-pair() gets stdout, passes the own handle to "my:host/log.echo-pair", which returns two own
;;                 output-streams (through memory), and drops both; returns 1.
(component
  (import "wasi:io/streams@0.2.0" (instance $streams
    (export "output-stream" (type (sub resource)))))
  (alias export $streams "output-stream" (type $os))
  (import "wasi:cli/stdout@0.2.0" (instance $stdout
    (alias outer 1 $os (type $os_outer))
    (export "output-stream" (type $os_eq (eq $os_outer)))
    (export "get-stdout" (func (result (own $os_eq))))))
  (import "my:host/log" (instance $log
    (alias outer 1 $os (type $os_outer))
    (export "output-stream" (type $os_eq (eq $os_outer)))
    (export "peek" (func (param "s" (borrow $os_eq)) (result u32)))
    (export "keep" (func (param "s" (own $os_eq)) (result u32)))
    (export "relay" (func (result (own $os_eq))))
    (export "echo" (func (param "s" (own $os_eq)) (result (own $os_eq))))
    (export "echo-pair" (func (param "s" (own $os_eq)) (result (tuple (own $os_eq) (own $os_eq)))))))

  ;; Memory for the results of echo-pair, which do not fit in one core result.
  (core module $mem_module (memory (export "mem") 1))
  (core instance $mem_instance (instantiate $mem_module))
  (alias core export $mem_instance "mem" (core memory $mem))

  (alias export $stdout "get-stdout" (func $get_stdout))
  (alias export $log "peek" (func $peek))
  (alias export $log "keep" (func $keep))
  (alias export $log "relay" (func $relay))
  (alias export $log "echo" (func $echo))
  (alias export $log "echo-pair" (func $echo_pair))
  (core func $get_stdout_core (canon lower (func $get_stdout)))
  (core func $peek_core (canon lower (func $peek)))
  (core func $keep_core (canon lower (func $keep)))
  (core func $relay_core (canon lower (func $relay)))
  (core func $echo_core (canon lower (func $echo)))
  (core func $echo_pair_core (canon lower (func $echo_pair) (memory $mem)))
  (core func $drop_core (canon resource.drop $os))

  (core module $m
    (import "host" "get-stdout" (func $get_stdout (result i32)))
    (import "host" "peek" (func $peek (param i32) (result i32)))
    (import "host" "keep" (func $keep (param i32) (result i32)))
    (import "host" "drop" (func $drop (param i32)))
    (import "host" "relay" (func $relay (result i32)))
    (import "host" "echo" (func $echo (param i32) (result i32)))
    (import "host" "echo-pair" (func $echo_pair (param i32 i32)))
    (import "host" "mem" (memory 1))
    (func (export "run-borrow") (result i32)
      (local $h i32) (local $v i32)
      (local.set $h (call $get_stdout))
      (local.set $v (call $peek (local.get $h)))
      (call $drop (local.get $h))
      (local.get $v))
    (func (export "run-own") (result i32)
      (call $keep (call $get_stdout)))
    (func (export "give") (result i32)
      (call $get_stdout))
    (func (export "put") (param $h i32)
      (call $drop (local.get $h)))
    (func (export "run-relay") (result i32)
      (call $drop (call $relay))
      (i32.const 1))
    (func (export "run-echo") (result i32)
      (call $drop (call $echo (call $get_stdout)))
      (i32.const 1))
    (func (export "run-echo-pair") (result i32)
      (call $echo_pair (call $get_stdout) (i32.const 0))
      (call $drop (i32.load (i32.const 0)))
      (call $drop (i32.load (i32.const 4)))
      (i32.const 1)))
  (core instance $i (instantiate $m
    (with "host" (instance
      (export "get-stdout" (func $get_stdout_core))
      (export "peek" (func $peek_core))
      (export "keep" (func $keep_core))
      (export "drop" (func $drop_core))
      (export "relay" (func $relay_core))
      (export "echo" (func $echo_core))
      (export "echo-pair" (func $echo_pair_core))
      (export "mem" (memory $mem))))))

  (func (export "run-borrow") (result u32) (canon lift (core func $i "run-borrow")))
  (func (export "run-own") (result u32) (canon lift (core func $i "run-own")))
  (func (export "give") (result (own $os)) (canon lift (core func $i "give")))
  (func (export "put") (param "s" (own $os)) (canon lift (core func $i "put")))
  (func (export "run-relay") (result u32) (canon lift (core func $i "run-relay")))
  (func (export "run-echo") (result u32) (canon lift (core func $i "run-echo")))
  (func (export "run-echo-pair") (result u32) (canon lift (core func $i "run-echo-pair")))
)
