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
    /// Coverage of <see cref="ComponentLinkerInstance.DefineAsyncFunction"/>, which always runs
    /// isolated; build with -p:BuildCallbackBridge=true.
    /// </summary>
    public sealed class ComponentAsyncHostFunctionTests
    {

        [HostCallbackIsolationFact]
        public async Task ItAwaitsAsynchronousCallbacks()
        {
            using var engine = new Engine(AsyncConfig());
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine);

            var calls = 0;
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineAsyncFunction("transform", async (arguments, cancellationToken) =>
                {
                    await Task.Yield();
                    await Task.Delay(1, cancellationToken);
                    Interlocked.Increment(ref calls);
                    return new[] { ComponentValue.S32(arguments[0].AsS32() * 2) };
                });
                host.DefineAsyncFunction("greet", (arguments, cancellationToken) =>
                    Task.FromResult(new[] { ComponentValue.String("hello " + arguments[0].AsString()) }));
            }

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            var instance = await linker.InstantiateAsync(store, component);
            var run = instance.GetFunction("run")!;

            for (var i = 0; i < 20; i++)
            {
                (await run.CallAsync(ComponentValue.S32(i)))!.AsS32().Should().Be(i * 2);
            }

            calls.Should().Be(21);
        }

        [HostCallbackIsolationFact]
        public async Task ItAwaitsAsynchronousCallbacksWhileYieldingFuel()
        {
            using var engine = new Engine(AsyncConfig().WithFuelConsumption(true));
            using var store = new Store(engine);
            store.Fuel = ulong.MaxValue;
            using var linker = new ComponentLinker(engine);
            var context = store.Context.handle;

            var pendingObserved = 0;
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineAsyncFunction("transform", async (arguments, cancellationToken) =>
                {
                    await Task.Delay(5, cancellationToken);
                    // The poll loop must see the pending callback so it waits instead of re-polling.
                    if (HostCallbackDispatcher.HasPendingWork(context))
                    {
                        Interlocked.Increment(ref pendingObserved);
                    }
                    return new[] { ComponentValue.S32(arguments[0].AsS32() * 2) };
                });
                host.DefineAsyncFunction("greet", (arguments, cancellationToken) =>
                    Task.FromResult(new[] { ComponentValue.String("hello " + arguments[0].AsString()) }));
            }

            store.SetFuelAsyncYieldInterval(1);
            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            var instance = await linker.InstantiateAsync(store, component);
            var run = instance.GetFunction("run")!;

            for (var i = 0; i < 10; i++)
            {
                (await run.CallAsync(ComponentValue.S32(i)))!.AsS32().Should().Be(i * 2);
            }

            pendingObserved.Should().Be(11);
            HostCallbackDispatcher.HasPendingWork(context).Should().BeFalse();
        }

        [HostCallbackIsolationFact]
        public async Task ItSurfacesAsynchronousCallbackFailures()
        {
            using var engine = new Engine(AsyncConfig());
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine);

            var fail = false;
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineAsyncFunction("transform", async (arguments, cancellationToken) =>
                {
                    await Task.Yield();
                    if (fail)
                    {
                        throw new InvalidOperationException("async host went bang");
                    }

                    return new[] { ComponentValue.S32(arguments[0].AsS32() * 2) };
                });
                host.DefineAsyncFunction("greet", (arguments, cancellationToken) =>
                    Task.FromResult(new[] { ComponentValue.String(string.Empty) }));
            }

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            var instance = await linker.InstantiateAsync(store, component);

            fail = true;
            await FluentActions.Awaiting(() => instance.GetFunction("run")!.CallAsync(ComponentValue.S32(1)))
                .Should().ThrowAsync<Exception>()
                .Where(e => e.ToString().Contains("async host went bang"));
        }

        [HostCallbackIsolationFact]
        public async Task ItProxiesStoreOperationsFromAsynchronousCallbacks()
        {
            using var engine = new Engine(AsyncConfig().WithFuelConsumption(true));
            using var store = new Store(engine);
            store.Fuel = 1_000_000;
            using var linker = new ComponentLinker(engine);

            ulong fuelBeforeYield = 0, fuelAfterYield = 0;
            Exception? rejected = null;
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineAsyncFunction("transform", async (arguments, cancellationToken) =>
                {
                    // Before the first yield the native stub runs the operation on the Store's stack.
                    fuelBeforeYield = store.Fuel;
                    store.Fuel = 800_000;
                    store.GC();

                    await Task.Yield();

                    // Afterwards the poll loop driving the call runs it between polls.
                    fuelAfterYield = store.Fuel;
                    store.Fuel = 500_000;
                    store.GC();
                    store.SetEpochDeadline(1_000);
                    rejected = Record.Exception(() => store.SetLimits(memorySize: 1 << 20));
                    return new[] { ComponentValue.S32(arguments[0].AsS32() * 2) };
                });
                host.DefineAsyncFunction("greet", (arguments, cancellationToken) =>
                    Task.FromResult(new[] { ComponentValue.String(string.Empty) }));
            }

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            await linker.InstantiateAsync(store, component);

            fuelBeforeYield.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1_000_000);
            fuelAfterYield.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(800_000);
            store.Fuel.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(500_000);
            rejected.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Contain("isolated host callback");
        }

        [HostCallbackIsolationFact]
        public async Task ItRejectsStoreOperationsAfterTheCallEnds()
        {
            using var engine = new Engine(AsyncConfig().WithFuelConsumption(true));
            using var store = new Store(engine);
            store.Fuel = 1_000_000;
            using var linker = new ComponentLinker(engine);

            var block = false;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineAsyncFunction("transform", async (arguments, cancellationToken) =>
                {
                    if (block)
                    {
                        entered.TrySetResult();
                        await gate.Task;
                        observed.TrySetResult(Record.Exception(() => store.Fuel));
                    }

                    return new[] { ComponentValue.S32(arguments[0].AsS32() * 2) };
                });
                host.DefineAsyncFunction("greet", (arguments, cancellationToken) =>
                    Task.FromResult(new[] { ComponentValue.String(string.Empty) }));
            }

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            var instance = await linker.InstantiateAsync(store, component);

            block = true;
            using var cancellation = new CancellationTokenSource();
            var call = instance.GetFunction("run")!.CallAsync(new[] { ComponentValue.S32(1) }, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();

            gate.SetResult();
            (await observed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Contain("isolated host callback");
            store.Fuel.Should().BeGreaterThan(0);
        }

        [HostCallbackIsolationFact]
        public async Task ItRejectsStoreOperationsFromACallbackThatOutlivedItsCall()
        {
            using var engine = new Engine(AsyncConfig().WithFuelConsumption(true));
            using var store = new Store(engine);
            store.Fuel = 1_000_000;
            using var linker = new ComponentLinker(engine);

            var phase = 0;
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong secondFuel = 0;
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineAsyncFunction("transform", async (arguments, cancellationToken) =>
                {
                    switch (Volatile.Read(ref phase))
                    {
                        case 1:
                            firstEntered.TrySetResult();
                            await firstGate.Task;
                            observed.TrySetResult(Record.Exception(() => store.Fuel));
                            break;
                        case 2:
                            secondEntered.TrySetResult();
                            await secondGate.Task;
                            secondFuel = store.Fuel;
                            break;
                    }

                    return new[] { ComponentValue.S32(arguments[0].AsS32() * 2) };
                });
                host.DefineAsyncFunction("greet", (arguments, cancellationToken) =>
                    Task.FromResult(new[] { ComponentValue.String(string.Empty) }));
            }

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            var first = await linker.InstantiateAsync(store, component);
            Volatile.Write(ref phase, 1);

            using var cancellation = new CancellationTokenSource();
            var call = first.GetFunction("run")!.CallAsync(new[] { ComponentValue.S32(1) }, cancellation.Token);
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();

            // Wasmtime refuses to enter a Store again after a call is dropped mid-flight (at
            // instantiation or at the next call, depending on timing), so no later call can poll on
            // its behalf. If that changes, the poll-session binding must still keep a later call
            // from serving the abandoned callback.
            async Task ExpectAbandonedCallbackRejected(Exception refused)
            {
                refused.Should().BeOfType<WasmtimeException>().Which.Message.Should().Contain("cannot enter");
                firstGate.SetResult();
                (await observed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeOfType<InvalidOperationException>()
                    .Which.Message.Should().Contain("isolated host callback");
            }

            Volatile.Write(ref phase, 0);
            ComponentInstance? second = null;
            var reentry = await Record.ExceptionAsync(async () => second = await linker.InstantiateAsync(store, component));
            if (reentry is not null)
            {
                await ExpectAbandonedCallbackRejected(reentry);
                return;
            }

            Volatile.Write(ref phase, 2);
            var secondCall = second!.GetFunction("run")!.CallAsync(new[] { ComponentValue.S32(2) });
            await Task.WhenAny(secondEntered.Task, secondCall).WaitAsync(TimeSpan.FromSeconds(10));
            if (!secondEntered.Task.IsCompleted)
            {
                await ExpectAbandonedCallbackRejected((await Record.ExceptionAsync(() => secondCall))!);
                return;
            }

            firstGate.SetResult();
            (await observed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Contain("isolated host callback");

            secondGate.SetResult();
            await secondCall.WaitAsync(TimeSpan.FromSeconds(10));
            secondFuel.Should().BeGreaterThan(0);
        }

        [HostCallbackIsolationFact]
        public async Task ItToleratesCancellationRacingCallbackCompletion()
        {
            using var engine = new Engine(AsyncConfig());
            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            using var linker = new ComponentLinker(engine);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = false;
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineAsyncFunction("transform", async (arguments, cancellationToken) =>
                {
                    if (Volatile.Read(ref block))
                    {
                        entered.TrySetResult();
                        await release.Task;
                    }

                    return new[] { ComponentValue.S32(arguments[0].AsS32() * 2) };
                });
                host.DefineAsyncFunction("greet", (arguments, cancellationToken) =>
                    Task.FromResult(new[] { ComponentValue.String(string.Empty) }));
            }

            for (var i = 0; i < 100; i++)
            {
                entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var store = new Store(engine);
                Volatile.Write(ref block, false);
                var instance = await linker.InstantiateAsync(store, component);
                Volatile.Write(ref block, true);
                using var cancellation = new CancellationTokenSource();
                var call = instance.GetFunction("run")!.CallAsync(new[] { ComponentValue.S32(1) }, cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                // Completes the callback and cancels the call at the same moment, so the cancel job
                // races the callback's completion and the call may end either way.
                var gate = new Barrier(2);
                var completer = Task.Run(() => { gate.SignalAndWait(); release.SetResult(); });
                var canceller = Task.Run(() => { gate.SignalAndWait(); cancellation.Cancel(); });
                await Task.WhenAll(completer, canceller);

                var outcome = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
                if (outcome is not null)
                {
                    outcome.Should().BeAssignableTo<OperationCanceledException>();
                }
            }
        }

        [HostCallbackIsolationFact]
        public async Task ItReleasesPerStoreStateWhenTheStoreIsDisposed()
        {
            using var engine = new Engine(AsyncConfig());
            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            using var linker = new ComponentLinker(engine);
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineAsyncFunction("transform", async (arguments, cancellationToken) =>
                {
                    await Task.Yield();
                    return new[] { ComponentValue.S32(arguments[0].AsS32() * 2) };
                });
                host.DefineAsyncFunction("greet", (arguments, cancellationToken) =>
                    Task.FromResult(new[] { ComponentValue.String(string.Empty) }));
            }

            var contexts = new IntPtr[20];
            for (var i = 0; i < contexts.Length; i++)
            {
                using var store = new Store(engine);
                contexts[i] = store.ContextHandleUnchecked;
                await linker.InstantiateAsync(store, component);
            }

            foreach (var context in contexts)
            {
                HostCallbackDispatcher.IsContextTracked(context).Should().BeFalse();
            }
        }

        private static Config AsyncConfig() =>
            new Config().WithComponentModel(true).WithComponentModelAsync(true);
    }
}
