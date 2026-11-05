using System;
using System.IO;
using Wasmtime;
using Wasmtime.Components;

using var engine = new Engine(new Config().WithComponentModel(true));
using var component = Component.FromTextFile(engine, Path.Combine(AppContext.BaseDirectory, "counter.wat"));
using var linker = new ComponentLinker(engine);
using var store = new Store(engine);

// A host resource type carries plain .NET objects. The destructor runs when a guest drops the
// last owning handle, never for objects the host owns.
var counterType = new ComponentHostResourceType<Counter>(
    counter => Console.WriteLine($"destroyed {counter}"));

using (var root = linker.Root())
using (var host = root.AddInstance("host"))
{
    host.DefineResource("counter", counterType);

    // A callback result always moves the object to the guest.
    host.DefineFunction("[constructor]counter", (arguments, results) =>
        results[0] = ComponentValue.Resource(counterType, new Counter(arguments[0].AsU32())));

    // `self` is a borrow: AsHostResource gives a view that is valid until the callback returns.
    host.DefineFunction("[method]counter.increment", (arguments, results) =>
        arguments[0].AsHostResource(counterType).Value++);
    host.DefineFunction("[method]counter.get", (arguments, results) =>
        results[0] = ComponentValue.U32(arguments[0].AsHostResource(counterType).Value));
}

var instance = linker.Instantiate(store, component);

ComponentFunction Export(string name) =>
    instance.GetFunction(name) ?? throw new InvalidOperationException($"missing export {name}");

// The guest creates, uses and drops a counter.
Console.WriteLine($"tally(10) = {Export("tally").Call(ComponentValue.U32(10))!.AsU32()}");

// An owned result moves the object back to the host, which now owns it.
var made = Export("make").Call(ComponentValue.U32(1))!.AsHostResource(counterType);
Console.WriteLine($"make(1) returned {made}; held by guest: {counterType.IsHeldByGuest(store, made)}");

// Borrow lends the object for one call; Own moves it to the guest, whose drop runs the destructor.
Console.WriteLine($"bump(borrow) = {Export("bump").Call(ComponentValue.Borrow(counterType, made))!.AsU32()}");
Console.WriteLine($"take(own) = {Export("take").Call(ComponentValue.Own(counterType, made))!.AsU32()}");

// Objects a guest still owns are destroyed by ReleaseComponentResources; Store.Dispose runs no destructors.
Export("keep").Call(ComponentValue.Own(counterType, new Counter(42)));
Console.WriteLine($"held by guest before release: {counterType.CountHeldByGuest(store)}");
store.ReleaseComponentResources();
Console.WriteLine($"held by guest after release: {counterType.CountHeldByGuest(store)}");

internal sealed class Counter
{
    public Counter(uint value) => Value = value;

    public uint Value { get; set; }

    public override string ToString() => $"Counter({Value})";
}
