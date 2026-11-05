#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    /// <summary>
    /// Many resources per callback: several Stores run <c>resource-stress.wat</c> concurrently, one thread
    /// each, every call moving dozens of host objects through a list result and a list of owns, lending each
    /// one to a method, with guest drops running destructors on the calling thread.
    /// </summary>
    public sealed class ComponentResourceStressTests
    {
        private const int StoreCount = 4;
        private const int Iterations = 4;
        private const uint BatchSize = 64;

        [Fact]
        public async Task ManyResourcesPerCallbackAcrossConcurrentStores()
        {
            var destroyed = 0;
            var taken = new ConcurrentBag<Counter>();
            var failures = new ConcurrentQueue<string>();
            var threads = new ConcurrentDictionary<Counter, int>();

            var type = new ComponentHostResourceType<Counter>(counter =>
            {
                // A guest-triggered destructor runs on the thread that called into the guest.
                if (threads.TryRemove(counter, out var thread) && thread != Environment.CurrentManagedThreadId)
                {
                    failures.Enqueue($"destructor of {counter} ran on another thread");
                }

                Interlocked.Increment(ref destroyed);
            });

            using var engine = new Engine(new Config().WithComponentModel(true));
            using var linker = new ComponentLinker(engine);
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineResource("counter", type);
                host.DefineFunction("[method]counter.get", (a, r) =>
                    r[0] = ComponentValue.U32(a[0].AsHostResource(type).Value));
                host.DefineFunction("make-batch", (a, r) =>
                {
                    var n = a[0].AsU32();
                    var items = new ComponentValue[n];
                    for (var i = 0; i < n; i++)
                    {
                        var counter = new Counter((uint)i + 1);
                        threads[counter] = Environment.CurrentManagedThreadId;
                        items[i] = ComponentValue.Resource(type, counter);
                    }

                    r[0] = ComponentValue.List(items);
                });
                host.DefineFunction("sum-batch", (a, r) =>
                    r[0] = ComponentValue.U32((uint)a[0].AsList().Sum(c => c.AsHostResource(type).Value)));
                host.DefineFunction("consume-batch", (a, r) =>
                {
                    // Claim every other object; the binding destroys the unclaimed ones when the callback ends.
                    var items = a[0].AsList();
                    uint total = 0;
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (i % 2 == 0)
                        {
                            var counter = items[i].TakeHostResource(type);
                            threads.TryRemove(counter, out _);
                            taken.Add(counter);
                            total += counter.Value;
                        }
                        else
                        {
                            total += items[i].AsHostResource(type).Value;
                        }
                    }

                    r[0] = ComponentValue.U32(total);
                });
            }

            using var component = Component.FromTextFile(engine, "Components/resource-stress.wat");
            var stores = Enumerable.Range(0, StoreCount).Select(_ => new Store(engine)).ToArray();
            try
            {
                // Every object is 1..BatchSize: get reads all of them, consume-batch the second half.
                var expected = BatchSize * (BatchSize + 1) / 2 + (BatchSize / 2 + 1 + BatchSize) * (BatchSize / 2) / 2;
                var results = await Task.WhenAll(stores.Select(store => Task.Factory.StartNew(() =>
                {
                    var run = linker.Instantiate(store, component).GetFunction("run")!;
                    var values = new uint[Iterations];
                    for (var i = 0; i < Iterations; i++)
                    {
                        values[i] = run.Call(ComponentValue.U32(BatchSize))!.AsU32();
                    }

                    return values;
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));

                results.SelectMany(values => values).Should().OnlyContain(value => value == expected);
                failures.Should().BeEmpty();

                // Per call: the first half dropped by the guest and the unclaimed half of the second half.
                const int runs = StoreCount * Iterations;
                Volatile.Read(ref destroyed).Should().Be((int)(runs * (BatchSize / 2 + BatchSize / 4)));
                taken.Should().HaveCount((int)(runs * (BatchSize / 4)));
                taken.Distinct().Should().HaveCount(taken.Count);

                foreach (var store in stores)
                {
                    var statistics = store.GetComponentResourceStatistics();
                    statistics.HostObjectsHeldByGuests.Should().Be(0);
                    statistics.HostObjectsLent.Should().Be(0);
                    statistics.PendingDrops.Should().Be(0);
                    store.IsComponentPoisoned.Should().BeFalse();
                }
            }
            finally
            {
                foreach (var store in stores)
                {
                    store.Dispose();
                }
            }
        }

        /// <summary>A list of borrows of guest-owned handles reaches one host callback.</summary>
        [Fact(Skip = "Wasmtime 50.0.1 traps with \"BUG: deferred host scope has no call context\" when a host call receives several borrows")]
        public void ListOfBorrowsReachesOneCallback()
        {
            var destroyed = 0;
            var type = new ComponentHostResourceType<Counter>(_ => destroyed++);
            using var engine = new Engine(new Config().WithComponentModel(true));
            using var store = new Store(engine);
            using var linker = new ComponentLinker(engine);
            var sums = 0;
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineResource("counter", type);
                host.DefineFunction("[method]counter.get", (a, r) => r[0] = ComponentValue.U32(a[0].AsHostResource(type).Value));
                host.DefineFunction("make-batch", (a, r) => r[0] = ComponentValue.List(
                    Enumerable.Range(1, (int)a[0].AsU32()).Select(i => ComponentValue.Resource(type, new Counter((uint)i))).ToArray()));
                host.DefineFunction("sum-batch", (a, r) =>
                {
                    sums++;
                    r[0] = ComponentValue.U32((uint)a[0].AsList().Sum(c => c.AsHostResource(type).Value));
                });
                host.DefineFunction("consume-batch", (a, r) => r[0] = ComponentValue.U32(0));
            }

            using var component = Component.FromTextFile(engine, "Components/resource-stress.wat");
            var instance = linker.Instantiate(store, component);

            instance.GetFunction("sum-all")!.Call(ComponentValue.U32(BatchSize))!.AsU32()
                .Should().Be(BatchSize * (BatchSize + 1) / 2);
            sums.Should().Be(1);
            destroyed.Should().Be((int)BatchSize);
            store.GetComponentResourceStatistics().HostObjectsLent.Should().Be(0);
        }
    }
}
