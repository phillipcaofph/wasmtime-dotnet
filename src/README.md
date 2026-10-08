## Installation

You can add a package reference with the [.NET SDK](https://dotnet.microsoft.com/):

```text
$ dotnet add package wasmtime
```

## Introduction

For this introduction, we'll be using a simple WebAssembly module that imports a `hello` function and exports a `run` function:

```wat
(module
  (func $hello (import "" "hello"))
  (func (export "run") (call $hello))
)
```

To use this module from .NET, create a new console project:

```
$ mkdir wasmintro
$ cd wasmintro
$ dotnet new console
```

Next, add a reference to the [Wasmtime package](https://www.nuget.org/packages/Wasmtime):

```
$ dotnet add package wasmtime
```

Replace the contents of `Program.cs` with the following code:

```csharp
using System;
using Wasmtime;

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
var run = instance.GetAction("run")!;
run();
```

An `Engine` is created and then a WebAssembly module is loaded from a string in WebAssembly text format.

A `Linker` defines a function called `hello` that simply prints a hello message.

The module is instantiated and the instance's `run` export is invoked.

To run the application, simply use `dotnet`:

```
$ dotnet run
```

This should print `Hello from C#!`.

## Time-slicing asynchronous component calls

Epoch and fuel limits can suspend an asynchronous call instead of trapping it. Control
returns to .NET at each suspension and the call resumes immediately, so a long-running
guest cooperates with other tasks and observes its `CancellationToken`. No managed code runs
at all at each deadline, so prefer these over `Store.SetEpochDeadlineCallback`, whose callback
runs on the WebAssembly stack:

```csharp
using var engine = new Engine(new Config()
    .WithComponentModel(true)
    .WithComponentModelAsync(true)
    .WithEpochInterruption(true));
using var store = new Store(engine);

// Suspend every epoch tick; something must call engine.IncrementEpoch() periodically.
store.SetEpochDeadlineAsyncYieldAndUpdate(1);

// Or, with Config.WithFuelConsumption(true) and store.Fuel set:
// store.SetFuelAsyncYieldInterval(1_000_000);

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await function.CallAsync(arguments, timeout.Token);
```

Both methods throw `InvalidOperationException` unless the engine was configured with
`WithComponentModelAsync(true)`, and only asynchronous calls can be suspended.
