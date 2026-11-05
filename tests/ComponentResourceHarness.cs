#nullable enable

using System;
using System.Collections.Generic;
using Wasmtime.Components;

namespace Wasmtime.Tests
{
    /// <summary>A host object carried by the <c>counter</c> resource of the resource fixtures.</summary>
    public sealed class Counter
    {
        public Counter(uint value) => Value = value;

        public uint Value { get; set; }

        public override string ToString() => $"Counter({Value})";
    }

    /// <summary>
    /// A linker defining the host side of <c>host-resources.wat</c>, <c>resource-containers.wat</c> and
    /// <c>host-resource-extras.wat</c>: the <c>counter</c> resource in instances <c>host</c> and <c>other</c>,
    /// its constructor and methods, and the free functions. Every callback can be replaced per test.
    /// </summary>
    public sealed class CounterHost : IDisposable
    {
        private readonly object gate = new();

        public CounterHost(
            Engine engine,
            ComponentHostResourceType<Counter>? type = null,
            string resourceName = "counter",
            Action<CounterHost>? configure = null)
        {
            Type = type ?? new ComponentHostResourceType<Counter>(OnDestroyed);
            configure?.Invoke(this);
            Linker = new ComponentLinker(engine);

            using var root = Linker.Root();
            using (var host = root.AddInstance("host"))
            {
                host.DefineResource(resourceName, Type);
                host.DefineFunction("[constructor]counter", (a, r) => (Constructor ?? DefaultConstructor)(a, r));
                host.DefineFunction("[method]counter.get", (a, r) => (Get ?? DefaultGet)(a, r));
                host.DefineFunction("[method]counter.increment", (a, r) => a[0].AsHostResource(Type).Value++);
                host.DefineFunction("[static]counter.live", (a, r) => r[0] = ComponentValue.U32((uint)Live));
                host.DefineFunction("consume", (a, r) => (Consume ?? DefaultConsume)(a, r));
                host.DefineFunction("peek", (a, r) => (Peek ?? DefaultGet)(a, r));
                host.DefineFunction("pair", (a, r) => (Pair ?? DefaultPair)(a, r));
                host.DefineFunction("relay", (a, r) => (Relay ?? DefaultRelay)(a, r));
                host.DefineFunction("combine", (a, r) => (Combine ?? DefaultCombine)(a, r));
            }

            using (var other = root.AddInstance("other"))
            {
                other.DefineResource("counter", Type);
                other.DefineFunction("[method]counter.get", (a, r) => r[0] = ComponentValue.U32(a[0].AsHostResource(Type).Value));
            }
        }

        public ComponentHostResourceType<Counter> Type { get; }

        public ComponentLinker Linker { get; }

        /// <summary>Objects destroyed by the type's destructor, in order.</summary>
        public List<Counter> Destroyed { get; } = new();

        /// <summary>Events in order: callbacks may add their own, destructions add "destroy N".</summary>
        public List<string> Log { get; } = new();

        /// <summary>Runs inside the destructor after recording; may throw.</summary>
        public Action<Counter>? OnDestroy { get; set; }

        /// <summary>Objects created by the default constructor, in order.</summary>
        public List<Counter> Created { get; } = new();

        public int Live => Created.Count - Destroyed.Count;

        public ComponentFunctionCallback? Constructor { get; set; }

        public ComponentFunctionCallback? Get { get; set; }

        public ComponentFunctionCallback? Peek { get; set; }

        public ComponentFunctionCallback? Consume { get; set; }

        public ComponentFunctionCallback? Pair { get; set; }

        public ComponentFunctionCallback? Relay { get; set; }

        public ComponentFunctionCallback? Combine { get; set; }

        public ComponentInstance Instantiate(Store store, Component component) => Linker.Instantiate(store, component);

        public void Dispose() => Linker.Dispose();

        private void OnDestroyed(Counter counter)
        {
            lock (gate)
            {
                Destroyed.Add(counter);
                Log.Add($"destroy {counter.Value}");
            }

            OnDestroy?.Invoke(counter);
        }

        private void DefaultConstructor(ReadOnlySpan<ComponentValue> arguments, Span<ComponentValue> results)
        {
            var counter = new Counter(arguments[0].AsU32());
            Created.Add(counter);
            results[0] = ComponentValue.Resource(Type, counter);
        }

        private void DefaultGet(ReadOnlySpan<ComponentValue> arguments, Span<ComponentValue> results) =>
            results[0] = ComponentValue.U32(arguments[0].AsHostResource(Type).Value);

        private void DefaultConsume(ReadOnlySpan<ComponentValue> arguments, Span<ComponentValue> results) =>
            results[0] = ComponentValue.U32(arguments[0].TakeHostResource(Type).Value);

        private void DefaultPair(ReadOnlySpan<ComponentValue> arguments, Span<ComponentValue> results)
        {
            var first = new Counter(1);
            var second = new Counter(2);
            Created.Add(first);
            Created.Add(second);
            results[0] = ComponentValue.Tuple(new[] { ComponentValue.Resource(Type, first), ComponentValue.Resource(Type, second) });
        }

        private void DefaultRelay(ReadOnlySpan<ComponentValue> arguments, Span<ComponentValue> results) =>
            results[0] = arguments[0];

        private void DefaultCombine(ReadOnlySpan<ComponentValue> arguments, Span<ComponentValue> results) =>
            results[0] = ComponentValue.U32(
                arguments[0].TakeHostResource(Type).Value + arguments[1].AsHostResource(Type).Value);
    }
}
