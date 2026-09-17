using System;
using System.IO;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    /// <summary>
    /// Fixture providing an engine with the component model enabled, plus the component binaries
    /// built from the WAT sources in <c>tests/Components</c>.
    /// </summary>
    public class ComponentFixture : IDisposable
    {
        public ComponentFixture()
        {
            Engine = new Engine(new Config().WithComponentModel(true));
        }

        public Engine Engine { get; }

        /// <summary>
        /// Creates a store. A trap poisons its store for all further component calls, so tests
        /// take one each rather than sharing.
        /// </summary>
        public Store CreateStore() => new Store(Engine);

        public Component Tiny() => Component.FromBytes(Engine, File.ReadAllBytes("Components/tiny.wasm"));

        public Component HostImport() => Component.FromBytes(Engine, File.ReadAllBytes("Components/host-import.wasm"));

        public Component Trap() => Component.FromBytes(Engine, File.ReadAllBytes("Components/trap.wasm"));

        public Component Strings() => Component.FromBytes(Engine, File.ReadAllBytes("Components/strings.wasm"));

        public void Dispose()
        {
            Engine.Dispose();
        }
    }

    public class ComponentLinkerTests : IClassFixture<ComponentFixture>
    {
        private readonly ComponentFixture fixture;

        public ComponentLinkerTests(ComponentFixture fixture)
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
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.Tiny();

            var instance = linker.Instantiate(store, component);

            instance.Should().NotBeNull();
        }

        [Fact]
        public void ItThrowsWhenAnImportIsNotDefined()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.HostImport();

            linker.Invoking(l => l.Instantiate(store, component))
                .Should().Throw<WasmtimeException>();
        }

        [Fact]
        public void ItInstantiatesWithHostFunctionsDefined()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.HostImport();

            DefineHost(linker);

            linker.Instantiate(store, component).Should().NotBeNull();
        }

        [Fact]
        public void ItSatisfiesUnknownImportsWithTraps()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.HostImport();

            linker.DefineUnknownImportsAsTraps(component);

            linker.Instantiate(store, component).Should().NotBeNull();
        }

        [Fact]
        public void ItThrowsWhenUsingTheLinkerWhileTheRootIsAlive()
        {
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);
            using var component = fixture.Tiny();

            var root = linker.Root();

            linker.Invoking(l => l.Instantiate(store, component))
                .Should().Throw<InvalidOperationException>();
            linker.Invoking(l => l.Root())
                .Should().Throw<InvalidOperationException>();

            root.Dispose();

            linker.Instantiate(store, component).Should().NotBeNull();
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

            linker.AllowShadowing(true);

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
            using var store = fixture.CreateStore();
            using var linker = new ComponentLinker(fixture.Engine);

            linker.Invoking(l => l.Instantiate(null!, fixture.Tiny())).Should().Throw<ArgumentNullException>();
            linker.Invoking(l => l.Instantiate(store, null!)).Should().Throw<ArgumentNullException>();
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

        private static void DefineHost(ComponentLinker linker)
        {
            using var root = linker.Root();
            using var host = root.AddInstance("host");

            host.DefineFunction("transform", (arguments, results) =>
                results[0] = ComponentValue.S32(arguments[0].AsS32() * 2));

            host.DefineFunction("greet", (arguments, results) =>
                results[0] = ComponentValue.String($"hello, {arguments[0].AsString()}"));
        }
    }
}
