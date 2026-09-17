using System;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    public class ComponentFunctionFixture : ComponentFixture { }

    public sealed class ComponentFunctionTests : IClassFixture<ComponentFunctionFixture>, IDisposable
    {
        private readonly ComponentFunctionFixture fixture;
        private readonly ComponentLinker linker;
        private readonly Store store;

        public ComponentFunctionTests(ComponentFunctionFixture fixture)
        {
            this.fixture = fixture;
            store = new Store(fixture.Engine);
            linker = new ComponentLinker(fixture.Engine);
        }

        public void Dispose()
        {
            store.Dispose();
            linker.Dispose();
        }

        private ComponentInstance InstantiateTinyComponent()
        {
            using var component = fixture.LoadComponent("tiny.wat");
            return linker.Instantiate(store, component);
        }

        [Fact]
        public void ItCallsAnExportedFunction()
        {
            var instance = InstantiateTinyComponent();

            var add = instance.GetFunction("add");

            add.Should().NotBeNull();
            add!.Call(ComponentValue.S32(2), ComponentValue.S32(3))!.AsS32().Should().Be(5);
        }

        [Fact]
        public void ItReportsTheFunctionSignature()
        {
            var instance = InstantiateTinyComponent();

            var add = instance.GetFunction("add")!;

            add.ParameterCount.Should().Be(2);
            add.HasResult.Should().BeTrue();
        }

        [Fact]
        public void ItCallsAFunctionRepeatedly()
        {
            var instance = InstantiateTinyComponent();
            var add = instance.GetFunction("add")!;

            for (var i = 0; i < 100; i++)
            {
                add.Call(ComponentValue.S32(i), ComponentValue.S32(i))!.AsS32().Should().Be(i * 2);
            }
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(-1, 1, 0)]
        [InlineData(int.MaxValue, 1, int.MinValue)]
        [InlineData(int.MinValue, -1, int.MaxValue)]
        public void ItRoundTripsArgumentsAndResults(int a, int b, int expected)
        {
            var instance = InstantiateTinyComponent();
            var add = instance.GetFunction("add")!;

            add.Call(ComponentValue.S32(a), ComponentValue.S32(b))!.AsS32().Should().Be(expected);
        }

        [Fact]
        public void ItReturnsNullForAnUnknownExport()
        {
            var instance = InstantiateTinyComponent();

            instance.GetExport("nope").Should().BeNull();
            instance.GetFunction("nope").Should().BeNull();
            instance.GetFunction("no:such/iface", "nope").Should().BeNull();
        }

        [Fact]
        public void ItThrowsForTheWrongNumberOfArguments()
        {
            var instance = InstantiateTinyComponent();
            var add = instance.GetFunction("add")!;

            add.Invoking(f => f.Call(ComponentValue.S32(1)))
                .Should().Throw<ArgumentException>().WithMessage("*2 argument(s)*1 were given*");
            add.Invoking(f => f.Call())
                .Should().Throw<ArgumentException>();
        }

        [Fact]
        public void ItThrowsForNullArguments()
        {
            var instance = InstantiateTinyComponent();
            var add = instance.GetFunction("add")!;

            add.Invoking(f => f.Call((ComponentValue[])null!)).Should().Throw<ArgumentNullException>();
            instance.Invoking(i => i.GetExport(null!)).Should().Throw<ArgumentNullException>();
            instance.Invoking(i => i.GetFunction((ComponentExport)null!)).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ItCallsBackIntoTheHost()
        {
            var observed = 0;

            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineFunction("transform", (arguments, results) =>
                {
                    observed = arguments[0].AsS32();
                    results[0] = ComponentValue.S32(observed * 2);
                });

                host.DefineFunction("greet", (arguments, results) =>
                    results[0] = ComponentValue.String($"hello, {arguments[0].AsString()}"));
            }

            using var component = fixture.LoadComponent("host-import.wat");
            var instance = linker.Instantiate(store, component);

            instance.GetFunction("run")!.Call(ComponentValue.S32(21))!.AsS32().Should().Be(42);
            observed.Should().Be(21);
        }

        [Fact]
        public void ItSurfacesHostExceptionsAsTraps()
        {
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineFunction("transform", (arguments, results) =>
                    throw new InvalidOperationException("host went bang"));
                host.DefineFunction("greet", (arguments, results) =>
                    results[0] = ComponentValue.String(string.Empty));
            }

            using var component = fixture.LoadComponent("host-import.wat");
            linker.Invoking(l => l.Instantiate(store, component))
                .Should().Throw<WasmtimeException>()
                .WithMessage("*host went bang*");
        }

        [Fact]
        public void ItThrowsWhenAHostFunctionDoesNotSetItsResult()
        {
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineFunction("transform", (arguments, results) => { });
                host.DefineFunction("greet", (arguments, results) =>
                    results[0] = ComponentValue.String(string.Empty));
            }

            using var component = fixture.LoadComponent("host-import.wat");
            linker.Invoking(l => l.Instantiate(store, component))
                .Should().Throw<WasmtimeException>()
                .WithMessage("*did not assign result*");
        }

        /// <summary>
        /// A trap poisons its entire Store, not just the instance that trapped. This holds for
        /// guest-side traps and for host callbacks that throw, so it is pinned here: sharing a
        /// Store across calls that may trap silently breaks every later call.
        /// </summary>
        [Fact]
        public void ATrapPoisonsTheWholeStore()
        {
            using var tiny = fixture.LoadComponent("tiny.wat");
            using var trap = fixture.LoadComponent("trap.wat");

            var add = linker.Instantiate(store, tiny).GetFunction("add")!;
            add.Call(ComponentValue.S32(1), ComponentValue.S32(1))!.AsS32().Should().Be(2);

            linker.Instantiate(store, trap).GetFunction("boom")!
                .Invoking(f => f.Call())
                .Should().Throw<WasmtimeException>();

            add.Invoking(f => f.Call(ComponentValue.S32(1), ComponentValue.S32(1)))
                .Should().Throw<WasmtimeException>()
                .WithMessage("*cannot enter component instance*");
        }
    }
}
