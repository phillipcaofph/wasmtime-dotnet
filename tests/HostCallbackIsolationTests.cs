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

        private static readonly AsyncLocal<string?> ambient = new();

        [HostCallbackIsolationFact]
        public void ItFlowsTheCallersExecutionContextIntoSynchronousCallbacks()
        {
            using var engine = new Engine(new Config().WithComponentModel(true));
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine) { IsolateHostCallbacks = true };

            string? observed = null;
            System.Globalization.CultureInfo? culture = null;
            DefineHost(linker, (arguments, results) =>
            {
                observed = ambient.Value;
                culture = System.Globalization.CultureInfo.CurrentCulture;
                // Changes made by the callback stay with the callback, as with an awaited method.
                ambient.Value = "changed by callback";
                results[0] = ComponentValue.S32(arguments[0].AsS32() * 2);
            });

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
                ambient.Value = "instantiate";
                var instance = linker.Instantiate(store, component);
                observed.Should().Be("instantiate");
                culture!.Name.Should().Be("fr-FR");

                ambient.Value = "call";
                instance.GetFunction("run")!.Call(ComponentValue.S32(1))!.AsS32().Should().Be(2);
                observed.Should().Be("call");
                ambient.Value.Should().Be("call");

                // Suppressed flow is honoured: the callback sees an empty context.
                using (ExecutionContext.SuppressFlow())
                {
                    instance.GetFunction("run")!.Call(ComponentValue.S32(1));
                }

                observed.Should().BeNull();
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previousCulture;
                ambient.Value = null;
            }
        }

        [HostCallbackIsolationFact]
        public async Task ItFlowsTheCallersExecutionContextIntoAsynchronousCallbacks()
        {
            using var engine = new Engine(AsyncConfig());
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine);

            string? beforeYield = null, afterYield = null;
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineAsyncFunction("transform", async (arguments, cancellationToken) =>
                {
                    beforeYield = ambient.Value;
                    await Task.Yield();
                    afterYield = ambient.Value;
                    return new[] { ComponentValue.S32(arguments[0].AsS32() * 2) };
                });
                host.DefineAsyncFunction("greet", (arguments, cancellationToken) =>
                    Task.FromResult(new[] { ComponentValue.String(string.Empty) }));
            }

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            ambient.Value = "instantiate";
            var instance = await linker.InstantiateAsync(store, component);
            beforeYield.Should().Be("instantiate");
            afterYield.Should().Be("instantiate");

            ambient.Value = "call";
            (await instance.GetFunction("run")!.CallAsync(ComponentValue.S32(4)))!.AsS32().Should().Be(8);
            beforeYield.Should().Be("call");
            afterYield.Should().Be("call");
            ambient.Value = null;
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
                    if (ComponentCallbackHooks.HostWorkPending(context))
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
            ComponentCallbackHooks.HostWorkPending(context).Should().BeFalse();
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
        public void ItProxiesSupportedStoreOperationsAndRejectsOthers()
        {
            using var engine = new Engine(new Config().WithComponentModel(true).WithFuelConsumption(true));
            using var store = new Store(engine);
            store.Fuel = 1_000_000;
            using var linker = new ComponentLinker(engine) { IsolateHostCallbacks = true };

            ulong observedFuel = 0;
            Exception? rejected = null;
            Exception? limitsRejected = null;
            DefineHost(linker, (arguments, results) =>
            {
                observedFuel = store.Fuel;
                store.Fuel = 500_000;
                store.GC();
                rejected = Record.Exception(() => store.SetWasiConfiguration(new WasiConfiguration()));
                limitsRejected = Record.Exception(() => store.SetLimits(memorySize: 1 << 20));
                results[0] = ComponentValue.S32(arguments[0].AsS32() * 2);
            });

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            linker.Instantiate(store, component);

            observedFuel.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1_000_000);
            store.Fuel.Should().BeLessThanOrEqualTo(500_000).And.BeGreaterThan(0);
            rejected.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Contain("isolated host callback");
            limitsRejected.Should().BeOfType<InvalidOperationException>();

            // The guard is scoped to the callback.
            store.SetWasiConfiguration(new WasiConfiguration());
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

            // Wasmtime v48 refuses to enter a Store again after a call is dropped mid-flight (at
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
        public async Task ItIsolatesByDefaultForAsynchronousEngines()
        {
            using var engine = new Engine(AsyncConfig());
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine);
            linker.IsolateHostCallbacks.Should().BeTrue();

            string? callbackThreadName = null;
            DefineHost(linker, (arguments, results) =>
            {
                callbackThreadName = Thread.CurrentThread.Name;
                results[0] = ComponentValue.S32(arguments[0].AsS32() * 2);
            });

            using var component = Component.FromTextFile(engine, "Components/host-import.wat");
            await linker.InstantiateAsync(store, component);

            callbackThreadName.Should().Be(WorkerThreadName);
        }

        [Fact]
        public unsafe void CoreHostFunctionsRunOnTheCallingStackOnAsynchronousEngines()
        {
            using var engine = new Engine(AsyncConfig());
            using var store = new Store(engine);
            using var linker = new Linker(engine);
            using var module = Module.FromText(engine, "core", @"
(module
  (import ""env"" ""callback"" (func $callback (param i32) (result i32)))
  (func (export ""run"") (param i32) (result i32)
    local.get 0
    call $callback))");

            var callerMarker = 0;
            var callerStack = (long)&callerMarker;
            var callerThread = Environment.CurrentManagedThreadId;
            int? callbackThread = null;
            long callbackStack = 0;
            linker.DefineFunction("env", "callback", (int value) =>
            {
                var marker = 0;
                callbackStack = (long)&marker;
                callbackThread = Environment.CurrentManagedThreadId;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                return value * 2;
            });

            var run = linker.Instantiate(store, module).GetFunction<int, int>("run")!;

            // Core calls are synchronous, so Wasmtime runs them without a fiber even on async engines:
            // the callback runs on the caller's thread, a little further down the same stack.
            run(21).Should().Be(42);
            callbackThread.Should().Be(callerThread);
            (callerStack - callbackStack).Should().BeInRange(0, 256 * 1024);
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
