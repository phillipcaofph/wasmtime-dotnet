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
                int.Parse(Environment.GetEnvironmentVariable("WASMTIME_FIBER_TIMEOUT_SECONDS") ?? "120")));
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
            var iterations = int.Parse(start.ArgumentList[2]);
            Assert.Equal(iterations, root.GetProperty("iterations").GetInt32());
            if (root.GetProperty("processorCount").GetInt32() > 1)
            {
                Assert.Equal(serverGc, root.GetProperty("serverGc").GetBoolean());
            }
            if (scenario is not ("errors" or "sync-control"))
            {
                Assert.True(root.GetProperty("pendingPolls").GetInt32() > 0, "No actual yielding was observed.");
            }
            if (scenario is "callbacks" or "concurrent")
            {
                var calls = checked(iterations * (scenario == "concurrent" ? 4 : 1));
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
        }
    }

    public sealed class UnixFiberTheoryAttribute : TheoryAttribute
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
