using System;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    public class ComponentLinkerFixture : ComponentFixture { }

    public sealed class ComponentLinkerTests : IClassFixture<ComponentLinkerFixture>
    {
        private readonly ComponentLinkerFixture fixture;

        public ComponentLinkerTests(ComponentLinkerFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public void ItThrowsForANullEngine()
        {
            FluentActions.Invoking(() => new ComponentLinker(null!))
                .Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ItInstantiatesAComponentWithoutImports()
        {
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.LoadComponent("tiny.wat");

            var instance = linker.Instantiate(fixture.Store, component);

            instance.Should().NotBeNull();
        }

        [Fact]
        public void ItThrowsWhenAnImportIsNotDefined()
        {
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.LoadComponent("host-import-declarations.wat");

            linker.Invoking(l => l.Instantiate(fixture.Store, component))
                .Should().Throw<WasmtimeException>();
        }

        [Fact]
        public void ItInstantiatesWithHostFunctionsDefined()
        {
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.LoadComponent("host-import.wat");

            var receivedInput = 0;
            DefineHost(linker, value => receivedInput = value);

            linker.Instantiate(fixture.Store, component).Should().NotBeNull();
            receivedInput.Should().Be(21);
        }

        [Fact]
        public void ItSatisfiesUnknownImportsWithTraps()
        {
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.LoadComponent("host-import-declarations.wat");

            linker.DefineUnknownImportsAsTraps(component);

            linker.Instantiate(fixture.Store, component).Should().NotBeNull();
        }

        [Fact]
        public void ItThrowsWhenUsingTheLinkerWhileTheRootIsAlive()
        {
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.LoadComponent("tiny.wat");

            var root = linker.Root();

            linker.Invoking(l => l.Instantiate(fixture.Store, component))
                .Should().Throw<InvalidOperationException>();
            linker.Invoking(l => l.Root())
                .Should().Throw<InvalidOperationException>();

            root.Dispose();

            linker.Instantiate(fixture.Store, component).Should().NotBeNull();
        }

        [Fact]
        public void ItThrowsWhenUsingAnInstanceWhileItsNestedInstanceIsAlive()
        {
            using var linker = new ComponentLinker(fixture.Engine);
            using var root = linker.Root();

            var nested = root.AddInstance("host");

            root.Invoking(r => r.AddInstance("other"))
                .Should().Throw<InvalidOperationException>();

            nested.Dispose();

            root.Invoking(r => r.AddInstance("other")).Should().NotThrow();
        }

        [Fact]
        public void ItDisposesNestedInstancesWithTheirParent()
        {
            var linker = new ComponentLinker(fixture.Engine);
            var root = linker.Root();
            var nested = root.AddInstance("host");

            linker.Dispose();

            nested.Invoking(n => n.DefineFunction("f", (args, results) => { }))
                .Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public void ItThrowsForDuplicateDefinitionsUnlessShadowingIsAllowed()
        {
            using var linker = new ComponentLinker(fixture.Engine);

            using (var root = linker.Root())
            {
                using var host = root.AddInstance("host");
                host.DefineFunction("transform", (args, results) => results[0] = ComponentValue.S32(0));

                host.Invoking(h => h.DefineFunction("transform", (args, results) => results[0] = ComponentValue.S32(0)))
                    .Should().Throw<WasmtimeException>();
            }

            linker.AllowShadowing = true;

            using (var root = linker.Root())
            {
                using var host = root.AddInstance("host");
                host.Invoking(h => h.DefineFunction("transform", (args, results) => results[0] = ComponentValue.S32(0)))
                    .Should().NotThrow();
            }
        }

        [Fact]
        public void ItThrowsForNullArguments()
        {
            using var linker = new ComponentLinker(fixture.Engine);

            linker.Invoking(l => l.Instantiate(null!, fixture.LoadComponent("tiny.wat"))).Should().Throw<ArgumentNullException>();
            linker.Invoking(l => l.Instantiate(fixture.Store, null!)).Should().Throw<ArgumentNullException>();
            linker.Invoking(l => l.DefineUnknownImportsAsTraps(null!)).Should().Throw<ArgumentNullException>();

            using var root = linker.Root();
            root.Invoking(r => r.AddInstance(null!)).Should().Throw<ArgumentNullException>();
            root.Invoking(r => r.DefineFunction(null!, (args, results) => { })).Should().Throw<ArgumentNullException>();
            root.Invoking(r => r.DefineFunction("f", null!)).Should().Throw<ArgumentNullException>();
            root.Invoking(r => r.AddModule(null!, null!)).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ItThrowsAfterDisposal()
        {
            var linker = new ComponentLinker(fixture.Engine);
            linker.Dispose();

            linker.Invoking(l => l.Root()).Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public void ItAddsWasiPreview2()
        {
            using var linker = new ComponentLinker(fixture.Engine);

            linker.Invoking(l => l.AddWasiPreview2()).Should().NotThrow();
        }

        [Fact]
        public void ItReplacesTheWasiPreview2WallClock()
        {
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.LoadComponent("wasi-wall-clock.wat");
            using var store = fixture.CreateStore();
            store.SetWasiConfiguration(new WasiConfiguration());
            var expected = new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.FromHours(12))
                .AddTicks(7);

            linker.AddWasiPreview2(new WasiPreview2Configuration()
                .WithWallClock(() => expected, TimeSpan.FromMilliseconds(2)));
            linker.Invoking(l => l.Instantiate(store, component)).Should().NotThrow();
            linker.AllowShadowing.Should().BeFalse();
        }

        [Fact]
        public void ItValidatesTheWasiPreview2Configuration()
        {
            var configuration = new WasiPreview2Configuration();

            configuration.Invoking(c => c.WithWallClock(null!))
                .Should().Throw<ArgumentNullException>();
            configuration.Invoking(c => c.WithWallClock(() => DateTimeOffset.UtcNow, TimeSpan.Zero))
                .Should().Throw<ArgumentOutOfRangeException>();
            using var linker = new ComponentLinker(fixture.Engine);
            linker.Invoking(l => l.AddWasiPreview2(null!))
                .Should().Throw<ArgumentNullException>();
        }

        private static void DefineHost(ComponentLinker linker, Action<int> onTransform)
        {
            using var root = linker.Root();
            using var host = root.AddInstance("host");

            host.DefineFunction("transform", (arguments, results) =>
            {
                var input = arguments[0].AsS32();
                onTransform(input);
                results[0] = ComponentValue.S32(input * 2);
            });

            host.DefineFunction("greet", (arguments, results) =>
                results[0] = ComponentValue.String($"hello, {arguments[0].AsString()}"));
        }
    }
}
