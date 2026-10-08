#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    /// <summary>
    /// In-process coverage of <see cref="HostCallbackIsolation"/>. Tests that need the native
    /// isolation library skip when it is unavailable; build with -p:BuildCallbackBridge=true.
    /// </summary>
    public sealed class HostCallbackIsolationTests
    {
        private const string WorkerThreadName = "Wasmtime host callback";

        [Fact]
        public void ItValidatesTheSpinDuration()
        {
            var original = HostCallbackIsolation.SpinDuration;

            FluentActions.Invoking(() => HostCallbackIsolation.SpinDuration = TimeSpan.FromTicks(-1))
                .Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => HostCallbackIsolation.SpinDuration = TimeSpan.FromMilliseconds(2))
                .Should().Throw<ArgumentOutOfRangeException>();

            HostCallbackIsolation.SpinDuration.Should().Be(original);
        }

        [Fact]
        public void ItValidatesTheWorkerThreadLimit()
        {
            FluentActions.Invoking(() => HostCallbackIsolation.MaxWorkerThreads = 0)
                .Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => HostCallbackIsolation.MaxWorkerThreads = 257)
                .Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void ItIsDisabledByDefault()
        {
            using var engine = new Engine(new Config().WithComponentModel(true));
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine);
            linker.IsolateHostCallbacks.Should().BeFalse();

            var callingThread = Environment.CurrentManagedThreadId;
            int? callbackThread = null;
            DefineHost(linker, (arguments, results) =>
            {
                callbackThread = Environment.CurrentManagedThreadId;
                results[0] = ComponentValue.S32(arguments[0].AsS32() * 2);
            });

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            linker.Instantiate(store, component);

            callbackThread.Should().Be(callingThread);
        }

        [HostCallbackIsolationFact]
        public void ItRejectsChangingTheWorkerLimitAfterIsolationStarted()
        {
            using var engine = new Engine(new Config().WithComponentModel(true));
            using var linker = new ComponentLinker(engine) { IsolateHostCallbacks = true };
            DefineHost(linker, (arguments, results) => results[0] = arguments[0]);

            FluentActions.Invoking(() => HostCallbackIsolation.MaxWorkerThreads = HostCallbackIsolation.MaxWorkerThreads)
                .Should().Throw<InvalidOperationException>();
        }

        [HostCallbackIsolationFact]
        public void ItRunsSynchronousCallbacksOnAWorkerThread()
        {
            using var engine = new Engine(new Config().WithComponentModel(true));
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine) { IsolateHostCallbacks = true };

            var callingThread = Environment.CurrentManagedThreadId;
            int? callbackThread = null;
            string? callbackThreadName = null;
            DefineHost(linker, (arguments, results) =>
            {
                callbackThread = Environment.CurrentManagedThreadId;
                callbackThreadName = Thread.CurrentThread.Name;
                results[0] = ComponentValue.S32(arguments[0].AsS32() * 2);
            });

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            var instance = linker.Instantiate(store, component);

            instance.GetFunction("run")!.Call(ComponentValue.S32(50))!.AsS32().Should().Be(100);
            callbackThread.Should().NotBe(callingThread);
            callbackThreadName.Should().Be(WorkerThreadName);
        }


        [HostCallbackIsolationFact]
        public void NestedInstancesInheritIsolation()
        {
            using var engine = new Engine(new Config().WithComponentModel(true));
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine) { IsolateHostCallbacks = true };

            string? callbackThreadName = null;
            DefineHost(linker, (arguments, results) =>
            {
                callbackThreadName = Thread.CurrentThread.Name;
                results[0] = ComponentValue.S32(arguments[0].AsS32() * 2);
            });

            // Changing the setting afterwards does not affect functions already defined.
            linker.IsolateHostCallbacks = false;

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            linker.Instantiate(store, component);

            callbackThreadName.Should().Be(WorkerThreadName);
        }

        [HostCallbackIsolationFact]
        public void ItSurfacesIsolatedCallbackExceptionsAsTraps()
        {
            using var engine = new Engine(new Config().WithComponentModel(true));
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine) { IsolateHostCallbacks = true };
            DefineHost(linker, (arguments, results) =>
                throw new InvalidOperationException("isolated host went bang"));

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            linker.Invoking(l => l.Instantiate(store, component))
                .Should().Throw<WasmtimeException>()
                .WithMessage("*isolated host went bang*")
                .WithInnerException<InvalidOperationException>();

            // The recorded cause was consumed, so it cannot surface from a later Store's call.
            HostCallbackDispatcher.PendingCauses.Should().Be(0);
        }

        [HostCallbackIsolationFact]
        public void ItRejectsStoreUseFromIsolatedCallbacks()
        {
            using var engine = new Engine(new Config().WithComponentModel(true).WithFuelConsumption(true));
            using var store = new Store(engine);
            store.Fuel = 1_000_000;
            using var linker = new ComponentLinker(engine) { IsolateHostCallbacks = true };

            Exception? rejected = null;
            DefineHost(linker, (arguments, results) =>
            {
                // The Store's thread is blocked in native code waiting for this callback.
                rejected = Record.Exception(() => store.Fuel);
                results[0] = ComponentValue.S32(arguments[0].AsS32() * 2);
            });

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            linker.Instantiate(store, component);

            rejected.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Contain("isolated host callback");

            // The guard is scoped to the callback.
            store.Fuel.Should().BeGreaterThan(0);
        }

        [HostCallbackIsolationFact]
        public async Task ItRunsSynchronousCallbacksIsolatedDuringAsyncCalls()
        {
            using var engine = new Engine(AsyncConfig());
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine) { IsolateHostCallbacks = true };

            string? callbackThreadName = null;
            DefineHost(linker, (arguments, results) =>
            {
                callbackThreadName = Thread.CurrentThread.Name;
                results[0] = ComponentValue.S32(arguments[0].AsS32() * 2);
            });

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            var instance = await linker.InstantiateAsync(store, component);

            for (var i = 0; i < 100; i++)
            {
                (await instance.GetFunction("run")!.CallAsync(ComponentValue.S32(i)))!.AsS32().Should().Be(i * 2);
            }

            callbackThreadName.Should().Be(WorkerThreadName);
        }

        private static Config AsyncConfig() =>
            new Config().WithComponentModel(true).WithComponentModelAsync(true);

        private static void DefineHost(ComponentLinker linker, ComponentFunctionCallback transform)
        {
            using var root = linker.Root();
            using var host = root.AddInstance("host");
            host.DefineFunction("transform", transform);
            host.DefineFunction("greet", (arguments, results) =>
                results[0] = ComponentValue.String("hello " + arguments[0].AsString()));
        }
    }

    public sealed class HostCallbackIsolationFactAttribute : FactAttribute
    {
        public HostCallbackIsolationFactAttribute()
        {
            // CI sets this so a missing native library fails the tests instead of skipping them.
            if (!HostCallbackIsolation.IsSupported &&
                Environment.GetEnvironmentVariable("WASMTIME_REQUIRE_HOST_CALLBACK_ISOLATION") != "1")
            {
                Skip = "The native host callback isolation library is not available; build with -p:BuildCallbackBridge=true.";
            }
        }
    }
}
