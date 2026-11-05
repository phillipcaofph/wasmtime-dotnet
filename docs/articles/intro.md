# Introduction to .NET embedding of Wasmtime

[Wasmtime](https://github.com/bytecodealliance/wasmtime) is a standalone runtime capable of executing [WebAssembly](https://webassembly.org/) outside of a web browser.

The [.NET embedding of Wasmtime](https://github.com/bytecodealliance/wasmtime-dotnet) enables .NET developers to easily instantiate and execute WebAssembly modules using Wasmtime.

For this tutorial, we will create a WebAssembly module and use that WebAssembly module from a .NET 5 application.

# A simple WebAssembly module

One of the reasons why WebAssembly is so exciting is that [many languages are able to target WebAssembly](https://github.com/appcypher/awesome-wasm-langs).  This means, for example, a plugin model based on WebAssembly could enable developers to write sandboxed, cross-platform plugins in any number of languages.

For this introduction, however, we will use a  WebAssembly module in [WebAssembly Text Format](https://developer.mozilla.org/en-US/docs/WebAssembly/Understanding_the_text_format):

```text
(module
  (func $hello (import "" "hello"))
  (func (export "run") (call $hello))
)
```

This module simply imports a `hello` function from the host and exports a `run` function that calls the imported function.

# Using the WebAssembly module from .NET

## Installing a .NET 5 SDK

Install a [.NET 5 SDK](https://dotnet.microsoft.com/download/dotnet/5.0) for your platform if you haven't already.

This will add a `dotnet` command to your PATH.

## Creating the .NET project

The .NET program will be a simple console application, so create a new console project with `dotnet new`:

```text
mkdir tutorial
cd tutorial
dotnet new console
```

## Referencing the Wasmtime package

To use the .NET embedding of Wasmtime from the project, we need to add a reference to the [Wasmtime NuGet package](https://www.nuget.org/packages/Wasmtime):

```text
dotnet add package wasmtime
```

This will add a `PackageReference` to the project file so that .NET embedding for Wasmtime can be used.

## Implementing the .NET code

Replace the contents of `Program.cs` with the following:

```c#
using System;
using Wasmtime;

namespace Tutorial
{
    class Program
    {
        static void Main(string[] args)
        {
            using var engine = new Engine();

            using var module = Module.FromText(
                engine,
                "hello",
                "(module (func $hello (import \"\" \"hello\")) (func (export \"run\") (call $hello)))"
            );

            using var linker = new Linker(engine);
            using var store = new Store(engine);

            linker.Define(
                "",
                "hello",
                Function.FromCallback(store, () => Console.WriteLine("Hello from C#!"))
            );

            var instance = linker.Instantiate(store, module);
            var run = instance.GetAction(store, "run");
            run();
        }
    }
}
```

The [`Linker`](https://bytecodealliance.github.io/wasmtime-dotnet/api/Wasmtime.Linker.html) class is responsible for linking in those defined functions, such as `hello` in this example.

Here we are defining a function named `hello` that simply prints `Hello from C#!` when called from WebAssembly.

A WebAssembly module _instantiation_ is the stateful representation of a module that can be executed.

This code is calling the `run` function defined in WebAssembly that is exported by the instance; this function then calls the `hello` function defined in C#.

## Building the .NET application

Use `dotnet build` to build the .NET application:

```text
dotnet build
```

This will create a `tutorial` (or `tutorial.exe` on Windows) executable in the `bin/Debug/net5.0` directory that implements the .NET application.

## Running the .NET application
 
To run the .NET application, simply invoke the executable file or use `dotnet`:

```text
dotnet run
```

This should result in the following output:

```text
Hello from C#!
```

# Component resources

The component model lets a component import and export _resource_ types: opaque handles to
state that lives on the other side of the boundary. The .NET embedding supports them in
host imports, export calls and nested values (lists, records, tuples, options, results and
variants; not maps). A complete program is in
[`examples/resources`](https://github.com/bytecodealliance/wasmtime-dotnet/tree/main/examples/resources).

## Host resources

A resource type the host implements is a `ComponentHostResourceType<T>`. Its values are
ordinary .NET objects of type `T`; the binding keeps the table that maps guest handles to
them. Define the type in a linker instance under the name the component imports, then
create, read and claim objects in host functions:

```c#
var counterType = new ComponentHostResourceType<Counter>(counter => counter.Dispose());

using (var root = linker.Root())
using (var host = root.AddInstance("host"))
{
    host.DefineResource("counter", counterType);

    // A callback result always moves the object to the guest.
    host.DefineFunction("[constructor]counter", (args, results) =>
        results[0] = ComponentValue.Resource(counterType, new Counter(args[0].AsU32())));

    // `self` is a borrow: a view of the object for the duration of the callback.
    host.DefineFunction("[method]counter.get", (args, results) =>
        results[0] = ComponentValue.U32(args[0].AsHostResource(counterType).Value));

    // An owned argument: TakeHostResource claims it, so no destructor runs for it.
    host.DefineFunction("consume", (args, results) =>
        results[0] = ComponentValue.U32(args[0].TakeHostResource(counterType).Value));
}
```

The destructor passed to the type runs exactly once for each object a guest owns and gives
up: when the guest drops its handle, when a callback ends without claiming an owned
argument, or when `Store.ReleaseComponentResources` runs. It runs on the thread running the
WebAssembly code, and never for objects the host owns.

## Own and borrow

`own<T>` moves an object between the host and the guest; `borrow<T>` lends it for one call.

- `ComponentValue.Resource(type, value)` takes the ownership from the parameter or result it
  lands in. `ComponentValue.Own(type, value)` and `ComponentValue.Borrow(type, value)` state
  it, and the binding checks the statement against the function's type before calling.
- Passing an object to an `own` parameter of an export moves it into the guest, and the
  guest's eventual drop runs the destructor. An object can be owned by one guest at a time,
  in any Store; a second move throws before the call.
- Passing it to a `borrow` parameter lends it: the host still owns it after the call.
- An `own` result of an export moves the object back to the host. The binding no longer
  tracks it, so `AsHostResource` (or `TakeHostResource`) simply returns it.

Every resource position of an export call is validated, at every nesting level, before
anything reaches Wasmtime. A mistake throws a .NET exception and leaves the Store usable.

## Views and Take

Resource arguments of a host callback are _views_:

- `AsHostResource` (host objects) and `AsResource` (other handles) return the value without
  taking ownership. A view expires when the callback returns; using it afterwards throws
  `ObjectDisposedException`.
- `TakeHostResource` and `TakeResource` claim an `own` argument. The host then owns the object
  (or the `ComponentResource` handle) and decides what happens to it.
- An `own` argument that is neither taken nor passed back to the guest in the callback's
  results is released when the callback ends: a host object's destructor runs, and a native
  handle is dropped. Reading an argument with `AsHostResource` and keeping the object does
  not claim it.

## Guest and other native resources

Resources that this binding did not define, such as a guest's own resource types, WASI
streams or `DefineUnknownImportsAsTraps` stubs, appear as `ComponentResource` handles. Each
handle owns one native value and ends in exactly one way:

- passed to an `own` parameter or returned as an `own` result (`State` becomes `Transferred`);
- `Drop()`, which drops it now and runs the guest's destructor. It throws if the handle is not
  live, inside a host callback, or while another component operation is in progress on the
  Store;
- `Dispose()`, which never throws for state reasons: it drops now when that is safe and
  otherwise marks the handle `DropPending`. Pending drops run in
  `Store.FlushComponentResources()` or `Store.ReleaseComponentResources()`, never inside an
  unrelated call;
- the Store's teardown, which only frees native memory.

`ComponentValue.DisposeResources()` disposes every handle in a value tree, for example the
elements of a `list<own<widget>>` result. A handle finalized while still owned frees its
memory but leaves the resource allocated in the Store (`ComponentResourceStatistics.LeakedHandles`).

## Tearing down a Store

`Store.Dispose()` runs no destructors and no guest code. When host objects that guests still
own need deterministic cleanup, call `Store.ReleaseComponentResources()` first: it runs
pending drops and then the destructor of every host object a guest still owns, throwing an
`AggregateException` if any of them fail. Afterwards the Store accepts no further component
operations. `Store.GetComponentResourceStatistics()` reports what is still held, lent or
pending.
