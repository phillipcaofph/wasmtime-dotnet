#nullable enable

using System;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    public class ComponentForeignResourceFixture : ComponentFixture { }

    /// <summary>
    /// Host resources defined outside this binding (WASI p2 <c>output-stream</c>) reaching host callbacks and
    /// export calls as <see cref="ComponentResource"/> handles (<c>foreign-resources.wat</c>).
    /// </summary>
    public sealed class ComponentForeignResourceTests : IClassFixture<ComponentForeignResourceFixture>
    {
        private readonly ComponentForeignResourceFixture fixture;

        public ComponentForeignResourceTests(ComponentForeignResourceFixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>The <c>my:host/log</c> imports; each callback can be replaced per test.</summary>
        private sealed class LogHost : IDisposable
        {
            public LogHost(Engine engine)
            {
                Linker = new ComponentLinker(engine);
                Linker.AddWasiPreview2();
                using var root = Linker.Root();
                using var log = root.AddInstance("my:host/log");
                log.DefineFunction("peek", (a, r) => (Peek ?? ((_, results) => results[0] = ComponentValue.U32(7)))(a, r));
                log.DefineFunction("keep", (a, r) => (Keep ?? ((_, results) => results[0] = ComponentValue.U32(3)))(a, r));
                log.DefineFunction("relay", (a, r) =>
                    (Relay ?? throw new InvalidOperationException("relay is not configured."))(a, r));
                log.DefineFunction("echo", (a, r) => (Echo ?? ((args, results) => results[0] = args[0]))(a, r));
                log.DefineFunction("echo-pair", (a, r) =>
                    (EchoPair ?? throw new InvalidOperationException("echo-pair is not configured."))(a, r));
            }

            public ComponentLinker Linker { get; }

            public ComponentFunctionCallback? Peek { get; set; }

            public ComponentFunctionCallback? Keep { get; set; }

            public ComponentFunctionCallback? Relay { get; set; }

            public ComponentFunctionCallback? Echo { get; set; }

            public ComponentFunctionCallback? EchoPair { get; set; }

            public void Dispose() => Linker.Dispose();
        }

        private Store CreateWasiStore()
        {
            var store = fixture.CreateStore();
            store.SetWasiConfiguration(new WasiConfiguration());
            return store;
        }

        private ComponentInstance Instantiate(LogHost host, Store store)
        {
            using var component = fixture.LoadComponent("foreign-resources.wat");
            return host.Linker.Instantiate(store, component);
        }

        private static ComponentFunction Function(ComponentInstance instance, string name) =>
            instance.GetFunction(name) ?? throw new InvalidOperationException($"Missing export {name}.");

        [Fact]
        public void ForeignBorrowReachesCallbackAsOpaqueView()
        {
            using var host = new LogHost(fixture.Engine);
            using var store = CreateWasiStore();
            ComponentValue? captured = null;
            ComponentResource? view = null;
            Exception? dropError = null;
            Exception? disposeError = null;
            ComponentResourceState? stateInside = null;
            host.Peek = (a, r) =>
            {
                captured = a[0];
                a[0].IsHostResource().Should().BeFalse();
                a[0].ResourceOwnership.Should().Be(ComponentResourceOwnership.Borrow);
                view = a[0].AsResource();
                stateInside = view.State;
                dropError = Record.Exception(() => view.Drop());
                disposeError = Record.Exception(() => view.Dispose());
                a[0].Invoking(v => v.TakeResource()).Should().Throw<InvalidOperationException>();
                r[0] = ComponentValue.U32(7);
            };
            var instance = Instantiate(host, store);

            Function(instance, "run-borrow").Call()!.AsU32().Should().Be(7);

            view!.IsOwned.Should().BeFalse();
            view.Type.IsHostDefined.Should().BeFalse();
            stateInside.Should().Be(ComponentResourceState.Live);
            dropError.Should().BeOfType<InvalidOperationException>();
            disposeError.Should().BeNull();
            view.State.Should().Be(ComponentResourceState.Expired);
            captured!.Invoking(v => v.AsResource()).Should().Throw<ObjectDisposedException>();
            captured!.ToString().Should().Be("borrow<native resource>");
        }

        [Fact]
        public void ForeignBorrowIsReleasedBeforeUserCode()
        {
            using var host = new LogHost(fixture.Engine);
            using var store = CreateWasiStore();
            host.Peek = (a, r) => r[0] = ComponentValue.U32(7);
            var instance = Instantiate(host, store);

            Function(instance, "run-borrow").Call()!.AsU32().Should().Be(7);
            Function(instance, "run-borrow").Call()!.AsU32().Should().Be(7);
        }

        [Fact]
        public void ForeignOwnTakenByCallback()
        {
            using var host = new LogHost(fixture.Engine);
            using var store = CreateWasiStore();
            ComponentResource? kept = null;
            host.Keep = (a, r) =>
            {
                a[0].ResourceOwnership.Should().Be(ComponentResourceOwnership.Own);
                kept = a[0].TakeResource();
                a[0].Invoking(v => v.TakeResource()).Should().Throw<InvalidOperationException>();
                r[0] = ComponentValue.U32(3);
            };
            var instance = Instantiate(host, store);

            Function(instance, "run-own").Call()!.AsU32().Should().Be(3);

            kept!.IsOwned.Should().BeTrue();
            kept.State.Should().Be(ComponentResourceState.Live);
            store.GetComponentResourceStatistics().PendingDrops.Should().Be(0);
            kept.Drop();
            kept.State.Should().Be(ComponentResourceState.Dropped);
        }

        [Fact]
        public void UnclaimedForeignOwnIsDropped()
        {
            using var host = new LogHost(fixture.Engine);
            using var store = CreateWasiStore();
            ComponentResource? viewed = null;
            host.Keep = (a, r) =>
            {
                viewed = a[0].AsResource();
                r[0] = ComponentValue.U32(3);
            };
            var instance = Instantiate(host, store);

            Function(instance, "run-own").Call()!.AsU32().Should().Be(3);

            store.GetComponentResourceStatistics().PendingDrops.Should().Be(1);
            viewed!.State.Should().Be(ComponentResourceState.DropPending);
            store.FlushComponentResources();
            store.GetComponentResourceStatistics().PendingDrops.Should().Be(0);
            viewed.State.Should().Be(ComponentResourceState.Dropped);
            Function(instance, "run-own").Call()!.AsU32().Should().Be(3);
        }

        [Fact]
        public void ForeignOwnResultHandle()
        {
            using var host = new LogHost(fixture.Engine);
            using var store = CreateWasiStore();
            var instance = Instantiate(host, store);

            var value = Function(instance, "give").Call()!;

            value.IsHostResource().Should().BeFalse();
            value.ResourceOwnership.Should().Be(ComponentResourceOwnership.Own);
            var stream = value.TakeResource();
            stream.IsOwned.Should().BeTrue();
            stream.State.Should().Be(ComponentResourceState.Live);
            Function(instance, "put").Call(ComponentValue.Own(stream)).Should().BeNull();
            stream.State.Should().Be(ComponentResourceState.Transferred);
        }

        [Fact]
        public void ForeignOwnReturnedFromCallback()
        {
            using var host = new LogHost(fixture.Engine);
            using var store = CreateWasiStore();
            ComponentResource? kept = null;
            host.Keep = (a, r) =>
            {
                kept = a[0].TakeResource();
                r[0] = ComponentValue.U32(3);
            };
            host.Relay = (a, r) => r[0] = ComponentValue.Own(kept!);
            var instance = Instantiate(host, store);
            Function(instance, "run-own").Call();

            Function(instance, "run-relay").Call()!.AsU32().Should().Be(1);

            kept!.State.Should().Be(ComponentResourceState.Transferred);

            // Wasmtime would report "unknown handle index"; the binding refuses without a native call.
            kept.Invoking(k => k.Drop()).Should().Throw<ObjectDisposedException>();
            store.GetComponentResourceStatistics().PendingDrops.Should().Be(0);
        }

        [Fact]
        public void UnclaimedOwnArgumentReturnedAsResultMoves()
        {
            using var host = new LogHost(fixture.Engine);
            using var store = CreateWasiStore();
            ComponentResource? viewed = null;
            host.Echo = (a, r) =>
            {
                viewed = a[0].AsResource();
                r[0] = a[0];
            };
            var instance = Instantiate(host, store);

            // Not claimed, but handed straight back: it counts as moved, not as an unclaimed argument to drop.
            Function(instance, "run-echo").Call()!.AsU32().Should().Be(1);

            viewed!.State.Should().Be(ComponentResourceState.Transferred);
            store.GetComponentResourceStatistics().PendingDrops.Should().Be(0);
            store.IsComponentPoisoned.Should().BeFalse();
            Function(instance, "run-echo").Call()!.AsU32().Should().Be(1);
        }

        [Fact]
        public void FailedResultWriteDisposesUnclaimedArgumentOnce()
        {
            using var host = new LogHost(fixture.Engine);
            using var store = CreateWasiStore();
            ComponentResource? viewed = null;
            host.EchoPair = (a, r) =>
            {
                viewed = a[0].AsResource();
                r[0] = ComponentValue.Tuple(new[] { a[0], a[0] });
            };
            var instance = Instantiate(host, store);

            Function(instance, "run-echo-pair").Invoking(f => f.Call())
                .Should().Throw<WasmtimeException>().Which.InnerException.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Contain("more than once");

            // The second write failed, so nothing moved: the unclaimed argument is disposed exactly once.
            viewed!.State.Should().Be(ComponentResourceState.DropPending);
            store.GetComponentResourceStatistics().PendingDrops.Should().Be(1);
        }

        [Fact]
        public void ReturningANativeHandleOfAnotherTypeTrapsAndKeepsIt()
        {
            using var host = new LogHost(fixture.Engine);
            using (var root = host.Linker.Root())
            using (var widgets = root.AddInstance("host"))
            {
                widgets.DefineFunction("dropped", (a, r) => { });
            }

            using var store = CreateWasiStore();
            var instance = Instantiate(host, store);
            using var guest = fixture.LoadComponent("guest-resources.wat");
            var widgetInstance = host.Linker.Instantiate(store, guest);
            var widget = Function(widgetInstance, "[constructor]widget").Call(ComponentValue.U32(4))!.TakeResource();
            host.Relay = (a, r) => r[0] = ComponentValue.Own(widget);

            Function(instance, "run-relay").Invoking(f => f.Call())
                .Should().Throw<WasmtimeException>().Which.InnerException.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Contain("results[0]");

            // Refused before the results were committed: the handle is still the host's.
            widget.State.Should().Be(ComponentResourceState.Live);
            widget.Dispose();
            widget.State.Should().Be(ComponentResourceState.Dropped);
        }

        [Fact]
        public void DisposeResourcesSkipsUnclaimedCallbackArgument()
        {
            using var host = new LogHost(fixture.Engine);
            using var store = CreateWasiStore();
            ComponentResource? viewed = null;
            ComponentResourceState? stateInside = null;
            host.Keep = (a, r) =>
            {
                viewed = a[0].AsResource();
                a[0].DisposeResources();
                stateInside = viewed.State;
                r[0] = ComponentValue.U32(3);
            };
            var instance = Instantiate(host, store);

            Function(instance, "run-own").Call()!.AsU32().Should().Be(3);

            // Skipped inside the callback; the end-of-callback cleanup disposed it once.
            stateInside.Should().Be(ComponentResourceState.Live);
            viewed!.State.Should().Be(ComponentResourceState.DropPending);
            store.GetComponentResourceStatistics().PendingDrops.Should().Be(1);
            store.FlushComponentResources();
            viewed.State.Should().Be(ComponentResourceState.Dropped);
        }

        [Fact]
        public void ReturningASpentHandleTraps()
        {
            using var host = new LogHost(fixture.Engine);
            using var store = CreateWasiStore();
            var instance = Instantiate(host, store);
            var stream = Function(instance, "give").Call()!.TakeResource();
            stream.Drop();
            host.Relay = (a, r) => r[0] = ComponentValue.Own(stream);

            Function(instance, "run-relay").Invoking(f => f.Call())
                .Should().Throw<WasmtimeException>().Which.InnerException.Should().BeOfType<InvalidOperationException>();
        }

        [Fact]
        public void ReturningAHandleOfAnotherStoreTraps()
        {
            using var host = new LogHost(fixture.Engine);
            using var first = CreateWasiStore();
            using var second = CreateWasiStore();
            var a = Instantiate(host, first);
            var b = Instantiate(host, second);
            using var stream = Function(a, "give").Call()!.TakeResource();
            host.Relay = (args, r) => r[0] = ComponentValue.Own(stream);

            Function(b, "run-relay").Invoking(f => f.Call())
                .Should().Throw<WasmtimeException>().Which.InnerException.Should().BeOfType<InvalidOperationException>();

            stream.State.Should().Be(ComponentResourceState.Live);
        }
    }
}
