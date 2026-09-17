using System;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    public class ComponentFunctionTests : IClassFixture<ComponentFixture>
    {
        private readonly ComponentFixture fixture;

        public ComponentFunctionTests(ComponentFixture fixture)
        {
            this.fixture = fixture;
        }

        private ComponentInstance InstantiateTiny(ComponentLinker linker, Store store)
        {
            using var component = fixture.Tiny();
            return linker.Instantiate(store, component);
        }

        [Fact]
        public void ItCallsAnExportedFunction()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var instance = InstantiateTiny(linker, store);

            var add = instance.GetFunction("add");

            add.Should().NotBeNull();
            add!.Call(ComponentValue.S32(2), ComponentValue.S32(3))!.AsS32().Should().Be(5);
        }

        [Fact]
        public void ItReportsTheFunctionSignature()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var instance = InstantiateTiny(linker, store);

            var add = instance.GetFunction("add")!;

            add.ParameterCount.Should().Be(2);
            add.HasResult.Should().BeTrue();
        }

        [Fact]
        public void ItCallsAFunctionRepeatedly()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var instance = InstantiateTiny(linker, store);
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
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var instance = InstantiateTiny(linker, store);
            var add = instance.GetFunction("add")!;

            add.Call(ComponentValue.S32(a), ComponentValue.S32(b))!.AsS32().Should().Be(expected);
        }

        [Fact]
        public void ItReturnsNullForAnUnknownExport()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var instance = InstantiateTiny(linker, store);

            instance.GetExport("nope").Should().BeNull();
            instance.GetFunction("nope").Should().BeNull();
            instance.GetFunction("no:such/iface", "nope").Should().BeNull();
        }

        [Fact]
        public void ItThrowsForTheWrongNumberOfArguments()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var instance = InstantiateTiny(linker, store);
            var add = instance.GetFunction("add")!;

            add.Invoking(f => f.Call(ComponentValue.S32(1)))
                .Should().Throw<ArgumentException>().WithMessage("*2 argument(s)*1 were given*");
            add.Invoking(f => f.Call())
                .Should().Throw<ArgumentException>();
        }

        [Fact]
        public void ItThrowsForNullArguments()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var instance = InstantiateTiny(linker, store);
            var add = instance.GetFunction("add")!;

            add.Invoking(f => f.Call((ComponentValue[])null!)).Should().Throw<ArgumentNullException>();
            instance.Invoking(i => i.GetExport(null!)).Should().Throw<ArgumentNullException>();
            instance.Invoking(i => i.GetFunction((ComponentExport)null!)).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ItCallsBackIntoTheHost()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
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

            using var component = fixture.HostImport();
            var instance = linker.Instantiate(store, component);

            instance.GetFunction("run")!.Call(ComponentValue.S32(21))!.AsS32().Should().Be(42);
            observed.Should().Be(21);
        }

        [Fact]
        public void ItSurfacesHostExceptionsAsTraps()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);

            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineFunction("transform", (arguments, results) =>
                    throw new InvalidOperationException("host went bang"));
                host.DefineFunction("greet", (arguments, results) =>
                    results[0] = ComponentValue.String(string.Empty));
            }

            using var component = fixture.HostImport();
            var instance = linker.Instantiate(store, component);

            instance.GetFunction("run")!
                .Invoking(f => f.Call(ComponentValue.S32(1)))
                .Should().Throw<WasmtimeException>()
                .WithMessage("*host went bang*");
        }

        [Fact]
        public void ItThrowsWhenAHostFunctionDoesNotSetItsResult()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);

            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                host.DefineFunction("transform", (arguments, results) => { });
                host.DefineFunction("greet", (arguments, results) =>
                    results[0] = ComponentValue.String(string.Empty));
            }

            using var component = fixture.HostImport();
            var instance = linker.Instantiate(store, component);

            instance.GetFunction("run")!
                .Invoking(f => f.Call(ComponentValue.S32(1)))
                .Should().Throw<WasmtimeException>()
                .WithMessage("*did not assign result*");
        }

        /// <summary>
        /// A trap poisons its entire store, not just the instance that trapped. This holds for
        /// guest-side traps and for host callbacks that throw, so it is pinned here: sharing a
        /// store across calls that may trap silently breaks every later call.
        /// </summary>
        [Fact]
        public void ATrapPoisonsTheWholeStore()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);

            using var tiny = fixture.Tiny();
            using var trap = fixture.Trap();

            var add = linker.Instantiate(store, tiny).GetFunction("add")!;
            add.Call(ComponentValue.S32(1), ComponentValue.S32(1))!.AsS32().Should().Be(2);

            linker.Instantiate(store, trap).GetFunction("boom")!
                .Invoking(f => f.Call())
                .Should().Throw<WasmtimeException>();

            add.Invoking(f => f.Call(ComponentValue.S32(1), ComponentValue.S32(1)))
                .Should().Throw<WasmtimeException>()
                .WithMessage("*cannot enter component instance*");
        }

        private ComponentInstance InstantiateStrings(ComponentLinker linker, Store store)
        {
            using var component = fixture.Strings();
            return linker.Instantiate(store, component);
        }

        [Theory]
        [InlineData("world", "hi world")]
        [InlineData("", "hi ")]
        [InlineData("h\u00e9llo", "hi h\u00e9llo")]
        [InlineData("\u4e16\u754c", "hi \u4e16\u754c")]
        [InlineData("\ud83c\udf89", "hi \ud83c\udf89")]
        public void ItPassesAndReturnsStrings(string name, string expected)
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var greet = InstantiateStrings(linker, store).GetFunction("greet")!;

            greet.Call(ComponentValue.String(name))!.AsString().Should().Be(expected);
        }

        /// <summary>
        /// The guest reports the byte length it was given, so this fails if the argument was
        /// encoded as anything other than UTF-8.
        /// </summary>
        [Theory]
        [InlineData("abc", 3)]
        [InlineData("h\u00e9llo", 6)]
        [InlineData("\u4e16\u754c", 6)]
        [InlineData("\ud83c\udf89", 4)]
        [InlineData("", 0)]
        public void ItPassesStringsAsUtf8(string name, int expectedByteLength)
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var length = InstantiateStrings(linker, store).GetFunction("length")!;

            length.Call(ComponentValue.String(name))!.AsS32().Should().Be(expectedByteLength);
        }

        [Fact]
        public void ItReturnsALongStringFromTheGuest()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var greet = InstantiateStrings(linker, store).GetFunction("greet")!;

            var name = new string('x', 10_000);

            greet.Call(ComponentValue.String(name))!.AsString().Should().Be("hi " + name);
        }

        [Fact]
        public void ItReturnsAListFromTheGuest()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            var numbers = InstantiateStrings(linker, store).GetFunction("numbers")!;

            numbers.ParameterCount.Should().Be(0);

            var result = numbers.Call()!.AsList();

            result.Should().HaveCount(3);
            result[0].AsS32().Should().Be(10);
            result[1].AsS32().Should().Be(20);
            result[2].AsS32().Should().Be(30);
        }
    }
}
