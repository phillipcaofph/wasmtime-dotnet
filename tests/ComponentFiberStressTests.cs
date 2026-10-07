using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Wasmtime.Tests
{
    public sealed class ComponentFiberStressTests
    {
        private readonly ITestOutputHelper output;

        public ComponentFiberStressTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public static IEnumerable<object[]> Scenarios()
        {
            foreach (var scenario in new[] { "baseline", "concurrent-baseline", "callbacks", "concurrent", "sync-control", "public-api", "startup", "cancellation", "errors" })
            foreach (var serverGc in new[] { false, true })
            foreach (var backgroundGc in new[] { false, true })
            {
                yield return new object[] { scenario, serverGc, backgroundGc };
            }
        }

        public static IEnumerable<object[]> ConfidenceScenarios()
        {
            foreach (var scenario in new[] { "nested-sync", "nested-async", "lifecycle", "guest-gc", "stack-overflow" })
            foreach (var serverGc in new[] { false, true })
            foreach (var backgroundGc in new[] { false, true })
            {
                yield return new object[] { scenario, serverGc, backgroundGc };
            }
        }

        [ThreadBackendTheory]
        [MemberData(nameof(ConfidenceScenarios))]
        public async Task ThreadBackendSurvivesNestedCallsAndTeardown(
            string scenario, bool serverGc, bool backgroundGc)
        {
            await NativeFibersPreserveManagedState(scenario, serverGc, backgroundGc);
        }

        public static IEnumerable<object[]> BridgeScenarios()
        {
            foreach (var scenario in new[]
            {
                "shared-linker", "bridge-values", "bridge-registration", "lifecycle", "nested-sync", "nested-async",
                "guest-gc", "bridge-reentrant", "bridge-exhaustion", "bridge-async", "bridge-async-cancel",
                "bridge-async-errors", "bridge-store-guard"
            })
            foreach (var serverGc in new[] { false, true })
            foreach (var backgroundGc in new[] { false, true })
            {
                yield return new object[] { scenario, serverGc, backgroundGc };
            }
        }

        [CallbackBridgeTheory]
        [MemberData(nameof(BridgeScenarios))]
        public async Task CallbackBridgePreservesValuesAndLifetimes(
            string scenario, bool serverGc, bool backgroundGc)
        {
            await NativeFibersPreserveManagedState(scenario, serverGc, backgroundGc);
        }

        [UnixFiberTheory]
        [MemberData(nameof(Scenarios))]
        public async Task NativeFibersPreserveManagedState(string scenario, bool serverGc, bool backgroundGc)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FiberProbe", "Wasmtime.FiberProbe.dll"));
            start.ArgumentList.Add(scenario);
            start.ArgumentList.Add(Environment.GetEnvironmentVariable("WASMTIME_FIBER_ITERATIONS") ?? "20");
            start.Environment["DOTNET_gcServer"] = serverGc ? "1" : "0";
            start.Environment["DOTNET_gcConcurrent"] = backgroundGc ? "1" : "0";
            start.Environment.Remove("WASMTIME_FIBER_DIAGNOSTIC_AFFINITY");
            start.Environment.Remove("WASMTIME_FIBER_DIAGNOSTIC_NO_FORCED_GC");
            start.Environment.Remove("WASMTIME_BRIDGE_UNSAFE_DIRECT_STORE_ACCESS");
            if (scenario == "bridge-exhaustion")
            {
                start.Environment["WASMTIME_BRIDGE_MAX_WORKERS"] = "1";
            }
            else
            {
                start.Environment.Remove("WASMTIME_BRIDGE_MAX_WORKERS");
            }
            var gcStress = Environment.GetEnvironmentVariable("WASMTIME_FIBER_GC_STRESS");
            if (!string.IsNullOrEmpty(gcStress))
            {
                start.Environment["DOTNET_GCStress"] = gcStress;
            }
            // Dumps stay outside the repository and can be collected by CI after a native crash.
            var dumps = Environment.GetEnvironmentVariable("WASMTIME_FIBER_DUMP_DIRECTORY")
                ?? Path.Combine(Path.GetTempPath(), "wasmtime-fiber-dumps");
            Directory.CreateDirectory(dumps);
            start.Environment["DOTNET_DbgEnableMiniDump"] = "1";
            start.Environment["DOTNET_DbgMiniDumpName"] = Path.Combine(dumps, "fiber-%p.dmp");
            start.Environment["DOTNET_DbgMiniDumpType"] = "2";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Probe did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(
                int.Parse(Environment.GetEnvironmentVariable("WASMTIME_FIBER_TIMEOUT_SECONDS")
                    ?? (scenario == "shared-linker" ? "600" : "120"))));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException($"Fiber probe {scenario} hung.\n{await stdout}\n{await stderr}");
            }

            var standardOutput = await stdout;
            var standardError = await stderr;
            output.WriteLine(standardOutput);
            output.WriteLine(standardError);
            Assert.True(process.ExitCode == 0,
                $"Fiber probe {scenario} exited {process.ExitCode}.\n{standardOutput}\n{standardError}");
            using var report = JsonDocument.Parse(standardOutput.Trim());
            var root = report.RootElement;
            Assert.Equal("passed", root.GetProperty("status").GetString());
            Assert.Equal(scenario, root.GetProperty("scenario").GetString());
            Assert.True(root.GetProperty("forcedGc").GetBoolean());
            Assert.False(root.GetProperty("threadAffine").GetBoolean());
            var threadBackend = Environment.GetEnvironmentVariable("WASMTIME_THREAD_FIBER_EXPERIMENT") == "1";
            Assert.Equal(threadBackend, root.GetProperty("threadBackend").GetBoolean());
            var callbackBridge = Environment.GetEnvironmentVariable("WASMTIME_CALLBACK_BRIDGE_EXPERIMENT") == "1";
            var iterationsArgument = int.Parse(start.ArgumentList[2]);
            Assert.Equal(callbackBridge, root.GetProperty("callbackBridge").GetBoolean());
            if (callbackBridge)
            {
                Assert.Equal(0UL, root.GetProperty("bridgesLive").GetUInt64());
                Assert.Equal(0UL, root.GetProperty("bridgeLiveAsync").GetUInt64());
                // Each async-errors iteration fails late once per result shape: s32, none and bool.
                Assert.Equal(scenario == "bridge-async-errors" ? checked((ulong)iterationsArgument * 3) : 0UL,
                    root.GetProperty("bridgeLateFailures").GetUInt64());
                Assert.Equal(scenario == "bridge-async-errors" ? (ulong)iterationsArgument : 0UL,
                    root.GetProperty("bridgePoisonedResults").GetUInt64());
                Assert.Equal(0, root.GetProperty("bridgePendingCauses").GetInt32());
                Assert.True(root.GetProperty("bridgeThreadsStarted").GetUInt64() <= 64);
                Assert.True(root.GetProperty("bridgeRequests").GetUInt64() >=
                    (ulong)root.GetProperty("callbacks").GetInt32());
                if (scenario is "callbacks" or "concurrent")
                {
                    Assert.True(root.GetProperty("callbackThreadCount").GetInt32() > 0);
                    Assert.True(root.GetProperty("pollingThreadCount").GetInt32() >= 2);
                }
            }
            if (threadBackend)
            {
                if (scenario == "sync-control")
                {
                    Assert.Equal(0UL, root.GetProperty("nativeThreadsStarted").GetUInt64());
                }
                else
                {
                    Assert.True(root.GetProperty("nativeThreadsStarted").GetUInt64() > 0);
                }
                Assert.Equal(0UL, root.GetProperty("nativeThreadsLive").GetUInt64());
                if (scenario is not ("errors" or "sync-control" or "stack-overflow"))
                {
                    Assert.True(root.GetProperty("nativeTlsSuspensions").GetUInt64() > 0,
                        "No worker activation list was saved across suspension.");
                }
                if (scenario is "callbacks" or "concurrent")
                {
                    Assert.True(root.GetProperty("callbackThreadCount").GetInt32() > 0);
                    Assert.True(root.GetProperty("pollingThreadCount").GetInt32() >= 2);
                }
            }
            var iterations = int.Parse(start.ArgumentList[2]);
            Assert.Equal(iterations, root.GetProperty("iterations").GetInt32());
            if (root.GetProperty("processorCount").GetInt32() > 1)
            {
                Assert.Equal(serverGc, root.GetProperty("serverGc").GetBoolean());
            }
            if (scenario is not ("errors" or "sync-control" or "stack-overflow" or "bridge-values" or
                "bridge-async-errors"))
            {
                Assert.True(root.GetProperty("pendingPolls").GetInt32() > 0, "No actual yielding was observed.");
            }
            if (scenario is "callbacks" or "concurrent" or "guest-gc" or "shared-linker")
            {
                var calls = checked(iterations * (scenario is "concurrent" or "guest-gc" or "shared-linker" ? 4 : 1));
                Assert.Equal(checked(calls * 100), root.GetProperty("callbacks").GetInt32());
                Assert.Equal(checked(calls * 100), root.GetProperty("collections").GetInt32());
                Assert.Equal(calls, root.GetProperty("migrations").GetInt32());
            }
            if (scenario is "public-api" or "startup")
            {
                Assert.Equal(checked(iterations * 100), root.GetProperty("callbacks").GetInt32());
            }
            if (scenario is "baseline" or "concurrent-baseline")
            {
                Assert.Equal(0, root.GetProperty("callbacks").GetInt32());
                Assert.Equal(checked(iterations * (scenario == "concurrent-baseline" ? 4 : 1)),
                    root.GetProperty("migrations").GetInt32());
            }
            if (scenario == "sync-control")
            {
                Assert.Equal(checked(iterations * 400), root.GetProperty("callbacks").GetInt32());
                Assert.Equal(checked(iterations * 400), root.GetProperty("collections").GetInt32());
                Assert.Equal(0, root.GetProperty("pendingPolls").GetInt32());
            }
            if (scenario is "nested-sync" or "nested-async")
            {
                Assert.Equal(checked(iterations * 1600), root.GetProperty("callbacks").GetInt32());
                Assert.Equal(checked(iterations * 1600), root.GetProperty("collections").GetInt32());
                Assert.Equal(checked(iterations * 400), root.GetProperty("nestedCalls").GetInt32());
                Assert.Equal(checked(iterations * 400), root.GetProperty("recoveredTraps").GetInt32());
                Assert.Equal(checked(iterations * 4), root.GetProperty("pendingPolls").GetInt32());
            }
            if (scenario == "lifecycle")
            {
                Assert.Equal(iterations, root.GetProperty("lifecycleCycles").GetInt32());
                Assert.Equal(checked(iterations * 2), root.GetProperty("recoveredTraps").GetInt32());
            }
            if (scenario == "guest-gc")
            {
                Assert.Equal(checked(iterations * 400), root.GetProperty("guestCollections").GetInt32());
                if (callbackBridge)
                {
                    // Every Store GC ran natively on the activation that owns the Wasm frames.
                    Assert.Equal(checked((ulong)iterations * 400), root.GetProperty("bridgeOwnerOperations").GetUInt64());
                }
            }
            if (callbackBridge && scenario is "lifecycle" or "errors")
            {
                Assert.Equal(scenario == "lifecycle" ? iterations : 1, root.GetProperty("causesTransferred").GetInt32());
            }
            if (scenario == "bridge-reentrant")
            {
                Assert.Equal(checked(iterations * 19), root.GetProperty("callbacks").GetInt32());
                Assert.Equal(checked(iterations * 3), root.GetProperty("nestedCalls").GetInt32());
                Assert.Equal(iterations, root.GetProperty("recoveredTraps").GetInt32());
                Assert.Equal(iterations, root.GetProperty("stackOverflows").GetInt32());
                Assert.True(root.GetProperty("bridgePeakWorkers").GetUInt64() >= 4);
            }
            if (scenario == "bridge-exhaustion")
            {
                Assert.Equal(checked(iterations * 10), root.GetProperty("callbacks").GetInt32());
                Assert.Equal(iterations, root.GetProperty("recoveredTraps").GetInt32());
                Assert.Equal((ulong)iterations, root.GetProperty("bridgeExhaustions").GetUInt64());
                Assert.Equal(1UL, root.GetProperty("bridgeThreadsStarted").GetUInt64());
            }
            if (scenario == "bridge-async")
            {
                Assert.Equal(checked(iterations * 352), root.GetProperty("callbacks").GetInt32());
                Assert.Equal(checked((ulong)iterations * 352), root.GetProperty("bridgeRequests").GetUInt64());
                // Pending async imports do not pin a thread each.
                Assert.True(root.GetProperty("bridgePeakAsync").GetUInt64() >= 8);
                Assert.True(root.GetProperty("bridgeThreadsStarted").GetUInt64() <
                    root.GetProperty("bridgePeakAsync").GetUInt64());
            }
            if (scenario == "bridge-async-cancel")
            {
                Assert.Equal(iterations, root.GetProperty("lifecycleCycles").GetInt32());
                Assert.Equal((ulong)iterations, root.GetProperty("bridgeAsyncCancelled").GetUInt64());
                Assert.Equal((ulong)iterations, root.GetProperty("bridgeStagedDeleted").GetUInt64());
            }
            if (scenario == "bridge-async-errors")
            {
                Assert.Equal(checked(iterations * 6), root.GetProperty("recoveredTraps").GetInt32());
                Assert.Equal(checked(iterations * 6), root.GetProperty("causesTransferred").GetInt32());
            }
            if (scenario == "bridge-store-guard")
            {
                // Per iteration: two rejections in each of three sync callbacks, two in one async callback.
                Assert.Equal(checked(iterations * 8), root.GetProperty("storeGuardRejections").GetInt32());
                // The async callback reads fuel and collects garbage both before and after its first yield.
                Assert.Equal(checked(iterations * 13), root.GetProperty("ownerFuelOperations").GetInt32());
                // Each sync callback also proxies one GC and one epoch deadline update. Operations after
                // the async callback yields run on the poll loop, not through the native stub.
                Assert.Equal(checked((ulong)iterations * 17), root.GetProperty("bridgeOwnerOperations").GetUInt64());
            }
            if (scenario == "stack-overflow")
            {
                Assert.Equal(iterations, root.GetProperty("stackOverflows").GetInt32());
            }
            if (scenario == "bridge-values")
            {
                Assert.Equal(checked(iterations * 2), root.GetProperty("callbacks").GetInt32());
                Assert.Equal(checked(iterations * 2), root.GetProperty("collections").GetInt32());
                Assert.Equal(checked((ulong)iterations * 2), root.GetProperty("bridgeRequests").GetUInt64());
            }
            if (scenario == "bridge-registration")
            {
                Assert.Equal(1, root.GetProperty("recoveredTraps").GetInt32());
                Assert.Equal(checked(iterations * 100), root.GetProperty("callbacks").GetInt32());
            }
            if (callbackBridge && scenario == "shared-linker")
            {
                Assert.InRange((ulong)root.GetProperty("callbackThreadCount").GetInt32(), 1UL,
                    root.GetProperty("bridgeThreadsStarted").GetUInt64());
                Assert.True(root.GetProperty("pollingThreadCount").GetInt32() >= 8);
                Assert.Equal((ulong)root.GetProperty("callbacks").GetInt32(),
                    root.GetProperty("bridgeRequests").GetUInt64());
            }
        }

        public sealed class CallbackBridgeTheoryAttribute : TheoryAttribute
        {
            public CallbackBridgeTheoryAttribute()
            {
                // Isolated callbacks never run managed code on a Wasmtime fiber, so these probes
                // also run on Windows.
                if (Environment.GetEnvironmentVariable("WASMTIME_CALLBACK_BRIDGE_EXPERIMENT") != "1")
                {
                    Skip = "Opt-in native callback bridge confidence probes.";
                }
            }
        }

        public sealed class ThreadBackendTheoryAttribute : UnixFiberTheoryAttribute
        {
            public ThreadBackendTheoryAttribute()
            {
                if (Skip is null && Environment.GetEnvironmentVariable("WASMTIME_THREAD_FIBER_EXPERIMENT") != "1")
                {
                    Skip = "Opt-in thread-backed native runtime confidence probes.";
                }
            }
        }
    }

    public class UnixFiberTheoryAttribute : TheoryAttribute
    {
        public UnixFiberTheoryAttribute()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Skip = "Fiber/CLR compatibility probes target Linux and macOS; Windows fibers are unsupported.";
            }
        }
    }
}
