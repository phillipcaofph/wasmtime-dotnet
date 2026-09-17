<div align="center">
  <h1><code>wasmtime-dotnet</code></h1>

  <p>
    <strong>.NET embedding of
    <a href="https://github.com/bytecodealliance/wasmtime">Wasmtime</a></strong>
  </p>

  <strong>A <a href="https://bytecodealliance.org/">Bytecode Alliance</a> project</strong>

  <p>
    <a href="https://github.com/bytecodealliance/wasmtime-dotnet/actions/workflows/main.yml">
      <img src="https://github.com/bytecodealliance/wasmtime-dotnet/actions/workflows/main.yml/badge.svg" alt="CI status"/>
    </a>
    <a href="https://www.nuget.org/packages/Wasmtime">
      <img src="https://img.shields.io/nuget/v/wasmtime" alt="Latest Version"/>
    </a>
    <a href="https://bytecodealliance.github.io/wasmtime-dotnet/">
      <img src="https://img.shields.io/badge/docs-main-green" alt="Documentation"/>
    </a>
  </p>

</div>

## Installation

You can add a package reference with the [.NET SDK](https://dotnet.microsoft.com/):

```text
$ dotnet add package wasmtime
```

## Introduction

For this introduction, we'll be using a simple WebAssembly module that imports
a `hello` function and exports a `run` function:

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

```c#
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

An `Engine` is created and then a WebAssembly module is loaded from a string in
WebAssembly text format.

A `Linker` defines a function called `hello` that simply prints a hello message.

The module is instantiated and the instance's `run` export is invoked.

To run the application, simply use `dotnet`:

```
$ dotnet run
```

This should print `Hello from C#!`.

## Components

Types in the `Wasmtime.Components` namespace implement the
[component model](https://component-model.bytecodealliance.org/). A component is
loaded with `Component`, its imports are satisfied by a `ComponentLinker`, and
values crossing the boundary are represented by `ComponentValue`.

The component model must be enabled on the engine's `Config`:

```c#
using System;
using Wasmtime;
using Wasmtime.Components;

using var engine = new Engine(new Config().WithComponentModel(true));
using var component = Component.FromTextFile(engine, "greeter.wat");
using var linker = new ComponentLinker(engine);
using var store = new Store(engine);

// Define the functions the component imports.
using (var root = linker.Root())
using (var host = root.AddInstance("host"))
{
    host.DefineFunction("name", (arguments, results) =>
        results[0] = ComponentValue.String("WebAssembly"));
}

var instance = linker.Instantiate(store, component);
var greet = instance.GetFunction("greet")!;

Console.WriteLine(greet.Call()!.AsString());
```

Functions exported by an interface rather than by the world root are looked up
with the interface name:

```c#
var process = instance.GetFunction("my:pkg/processor@1.0.0", "process")!;
```

A component function takes any number of parameters but has at most one result,
so `Call` returns a single `ComponentValue`, or `null` when the function returns
nothing. Note that a function declared in WIT as returning `result<_, E>` does
have a result here, even though bindings generators usually present it as
returning nothing.

Values are created with the static factories on `ComponentValue` and read back
with its typed accessors:

```c#
var record = ComponentValue.Record(new[]
{
    new KeyValuePair<string, ComponentValue>("value", ComponentValue.F64(36.6)),
    new KeyValuePair<string, ComponentValue>("unit", ComponentValue.Enum("celsius")),
});

var reading = result.Field("value").AsF64();
```

`bool`, the integer and float types, `string`, `enum`, `list`, `tuple`,
`record`, `option` and `result` are supported. `char`, `variant`, `flags`,
`map` and resources are not yet implemented.

Two behaviours are worth knowing about:

- A trap leaves the whole `Store` unusable for further component calls, not just
  the instance that trapped. A store cannot be reused after a trap.
- `ComponentLinker.Root()` and `ComponentLinkerInstance.AddInstance()` take
  exclusive access to what they came from, so the returned instance must be
  disposed before the linker is used again.

See `examples/component` for a complete example.

## Contributing

### Building

Use `dotnet` to build the repository:

```
$ dotnet build Wasmtime.sln
```

This will download the latest development snapshot of Wasmtime for your
platform.

### Testing

Use `dotnet` to run the unit tests:

```
$ dotnet test Wasmtime.sln
```

### Creating the NuGet package

Use `dotnet` to create a NuGet package:

```
$ cd src
$ dotnet pack Wasmtime.sln -c Release /p:Packing=true
```

This will create a `.nupkg` file in `src/bin/Release`.

By default, local builds will use a `-dev` suffix for the package to
differentiate between official packages and development packages.

### Updating Wasmtime for a release

To update the Wasmtime library used for a new release, change `WasmtimeVersion`
in `Directory.Build.props`:

```xml
<WasmtimeVersion Condition="'$(WasmtimeVersion)'==''">$VERSION</WasmtimeVersion>
```

### Publishing the Wasmtime .NET NuGet package

GitHub actions is used to automatically publish a package to NuGet when a tag
is pushed to the repository.

To publish a new release, create a release in GitHub and add the relevant
release notes.

Use a tag of the format `v$VERSION` where `$VERSION` matches the Wasmtime
version used by the .NET package; ensure the tagged commit matches the last
commit to make for the release.

When the release is published on GitHub, an action should automatically start
to build and publish the package to NuGet.
