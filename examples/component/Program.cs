using System;
using System.IO;
using Wasmtime;
using Wasmtime.Components;

using var engine = new Engine(new Config().WithComponentModel(true));
using var component = Component.FromTextFile(engine, Path.Combine(AppContext.BaseDirectory, "greeter.wat"));
using var linker = new ComponentLinker(engine);
using var store = new Store(engine);

using (var root = linker.Root())
using (var host = root.AddInstance("host"))
{
    host.DefineFunction("name", (arguments, results) =>
        results[0] = ComponentValue.String("WebAssembly"));
}

var instance = linker.Instantiate(store, component);

var greet = instance.GetFunction("greet");
if (greet is null)
{
    Console.WriteLine("error: greet export is missing");
    return;
}

Console.WriteLine(greet.Call(ComponentValue.String("Wasmtime"))!.AsString());
