using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    public sealed class ComponentAsyncYieldTests
    {
        [Fact]
        public async Task ItYieldsAtEpochDeadlinesInsteadOfTrapping()
        {
            using var engine = new Engine(AsyncConfig().WithEpochInterruption(true));
            using var store = new Store(engine);
            var instance = await InstantiateAsync(engine, store);
            store.SetEpochDeadlineAsyncYieldAndUpdate(1);

            using var ticker = StartTicker(engine);
            (await instance.GetFunction("spin")!.CallAsync(ComponentValue.U32(50_000_000)))!
                .AsU32().Should().Be(50_000_000);
        }

        [Fact]
        public async Task ItCancelsAnInfiniteLoopThroughEpochYields()
        {
            using var engine = new Engine(AsyncConfig().WithEpochInterruption(true));
            using var store = new Store(engine);
            var instance = await InstantiateAsync(engine, store);
            store.SetEpochDeadlineAsyncYieldAndUpdate(1);

            using var ticker = StartTicker(engine);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await FluentActions.Awaiting(() => instance.GetFunction("forever")!
                    .CallAsync(Array.Empty<ComponentValue>(), cancellation.Token))
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task ItYieldsWhenTheFuelIntervalIsConsumed()
        {
            using var engine = new Engine(AsyncConfig().WithFuelConsumption(true));
            using var store = new Store(engine);
            store.Fuel = ulong.MaxValue;
            var instance = await InstantiateAsync(engine, store);
            store.SetFuelAsyncYieldInterval(10_000);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await FluentActions.Awaiting(() => instance.GetFunction("forever")!
                    .CallAsync(Array.Empty<ComponentValue>(), cancellation.Token))
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task ItResumesPromptlyAfterEachYield()
        {
            using var engine = new Engine(AsyncConfig().WithFuelConsumption(true));
            using var store = new Store(engine);
            store.Fuel = ulong.MaxValue;
            var instance = await InstantiateAsync(engine, store);
            var spin = instance.GetFunction("spin")!;

            // Each iteration consumes several units of fuel, so this yields well over 5,000 times.
            store.SetFuelAsyncYieldInterval(5_000);
            var stopwatch = Stopwatch.StartNew();
            (await spin.CallAsync(ComponentValue.U32(5_000_000)))!.AsU32().Should().Be(5_000_000);

            // With a 1 ms back-off per yield this would take at least 5 seconds.
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
        }

        [Fact]
        public void ItRejectsYieldingWithoutAsyncSupport()
        {
            using var engine = new Engine(new Config().WithComponentModel(true).WithFuelConsumption(true));
            using var store = new Store(engine);

            store.Invoking(s => s.SetFuelAsyncYieldInterval(10_000)).Should().Throw<InvalidOperationException>();
            store.Invoking(s => s.SetEpochDeadlineAsyncYieldAndUpdate(1)).Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void ItRejectsFuelYieldingWithoutFuelConsumption()
        {
            using var engine = new Engine(AsyncConfig());
            using var store = new Store(engine);

            store.Invoking(s => s.SetFuelAsyncYieldInterval(10_000)).Should().Throw<WasmtimeException>();
        }

        [HostCallbackIsolationFact]
        public async Task ItRunsEpochDeadlineCallbacksOffTheWasmtimeStack()
        {
            using var engine = new Engine(AsyncConfig().WithEpochInterruption(true).WithFuelConsumption(true));
            using var store = new Store(engine);
            store.Fuel = ulong.MaxValue;
            var instance = await InstantiateAsync(engine, store);

            var calls = 0;
            var threadName = "";
            ulong fuelSeen = 0;
            store.SetEpochDeadlineCallback(s =>
            {
                calls++;
                threadName = Thread.CurrentThread.Name;
                fuelSeen = s.Fuel;
                s.SetEpochDeadline(1);
                return 1;
            });

            using var ticker = StartTicker(engine);
            (await instance.GetFunction("spin")!.CallAsync(ComponentValue.U32(50_000_000)))!
                .AsU32().Should().Be(50_000_000);

            calls.Should().BePositive();
            threadName.Should().Be("Wasmtime host callback");
            fuelSeen.Should().BeGreaterThan(0);
        }

        [HostCallbackIsolationFact]
        public async Task ItReportsExceptionsFromIsolatedEpochDeadlineCallbacks()
        {
            using var engine = new Engine(AsyncConfig().WithEpochInterruption(true));
            using var store = new Store(engine);
            var instance = await InstantiateAsync(engine, store);
            store.SetEpochDeadlineCallback(_ => throw new InvalidOperationException("deadline reached"));

            using var ticker = StartTicker(engine);
            var error = await FluentActions.Awaiting(() => instance.GetFunction("forever")!
                    .CallAsync(Array.Empty<ComponentValue>()))
                .Should().ThrowAsync<WasmtimeException>();
            error.Which.InnerException.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Be("deadline reached");
        }

        [HostCallbackIsolationFact]
        public async Task ItReplacesIsolatedEpochDeadlineCallbacks()
        {
            using var engine = new Engine(AsyncConfig().WithEpochInterruption(true));
            using var store = new Store(engine);
            var instance = await InstantiateAsync(engine, store);

            var replacedCalls = 0;
            var calls = 0;
            for (var i = 0; i < 100; i++)
            {
                store.SetEpochDeadlineCallback(_ => { replacedCalls++; return 1; });
            }

            store.SetEpochDeadlineCallback(_ => { calls++; return 1; });

            using var ticker = StartTicker(engine);
            (await instance.GetFunction("spin")!.CallAsync(ComponentValue.U32(20_000_000)))!
                .AsU32().Should().Be(20_000_000);

            replacedCalls.Should().Be(0);
            calls.Should().BePositive();
        }

        private static Config AsyncConfig() =>
            new Config().WithComponentModel(true).WithComponentModelAsync(true);

        private static async Task<ComponentInstance> InstantiateAsync(Engine engine, Store store)
        {
            using var component = Component.FromTextFile(engine, "Components/spin.wat");
            using var linker = new ComponentLinker(engine);
            return await linker.InstantiateAsync(store, component);
        }

        private static EpochTicker StartTicker(Engine engine) => new(engine);

        private sealed class EpochTicker : IDisposable
        {
            private readonly CancellationTokenSource stop = new();
            private readonly Thread thread;

            public EpochTicker(Engine engine)
            {
                thread = new Thread(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        engine.IncrementEpoch();
                        Thread.Sleep(1);
                    }
                }) { IsBackground = true };
                thread.Start();
            }

            public void Dispose()
            {
                stop.Cancel();
                thread.Join();
                stop.Dispose();
            }
        }
    }
}
