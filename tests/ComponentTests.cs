using System;
using System.IO;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    public class ComponentTestsFixture : ComponentFixture { }

    public sealed class ComponentTests : IClassFixture<ComponentTestsFixture>
    {
        private const string Text = @"
(component
  (core module $m
    (func (export ""add"") (param i32 i32) (result i32)
      local.get 0
      local.get 1
      i32.add)
  )
  (core instance $i (instantiate $m))
  (func (export ""add"") (param ""a"" s32) (param ""b"" s32) (result s32)
    (canon lift (core func $i ""add"")))
)";

        private readonly ComponentTestsFixture fixture;

        public ComponentTests(ComponentTestsFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public void ItLoadsAComponentFromText()
        {
            using var store = fixture.CreateStore();
            using var component = Component.FromText(fixture.Engine, Text);
            using var linker = new ComponentLinker(fixture.Engine);

            var add = linker.Instantiate(store, component).GetFunction("add")!;

            add.Call(ComponentValue.S32(40), ComponentValue.S32(2))!.AsS32().Should().Be(42);
        }

        [Fact]
        public void ItLoadsAComponentFromATextFile()
        {
            using var store = fixture.CreateStore();
            using var component = Component.FromTextFile(fixture.Engine, "Components/tiny.wat");
            using var linker = new ComponentLinker(fixture.Engine);

            var add = linker.Instantiate(store, component).GetFunction("add")!;

            add.Call(ComponentValue.S32(1), ComponentValue.S32(2))!.AsS32().Should().Be(3);
        }

        [Fact]
        public void ItThrowsForInvalidText()
        {
            FluentActions.Invoking(() => Component.FromText(fixture.Engine, "(component (this is not valid)"))
                .Should().Throw<WasmtimeException>();
        }

        [Fact]
        public void ItThrowsForInvalidBytes()
        {
            FluentActions.Invoking(() => Component.FromBytes(fixture.Engine, new byte[] { 1, 2, 3, 4 }))
                .Should().Throw<WasmtimeException>();
        }

        [Fact]
        public void ItThrowsForNullArguments()
        {
            FluentActions.Invoking(() => Component.FromText(null!, Text)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => Component.FromText(fixture.Engine, null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => Component.FromTextFile(fixture.Engine, null!)).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ItRoundTripsThroughSerialization()
        {
            using var store = fixture.CreateStore();
            using var original = Component.FromText(fixture.Engine, Text);

            var serialized = original.Serialize();
            serialized.Should().NotBeEmpty();

            using var restored = Component.Deserialize(fixture.Engine, serialized);
            using var linker = new ComponentLinker(fixture.Engine);

            var add = linker.Instantiate(store, restored).GetFunction("add")!;

            add.Call(ComponentValue.S32(2), ComponentValue.S32(2))!.AsS32().Should().Be(4);
        }

        [Fact]
        public void ItLooksUpExportsOnTheComponentItself()
        {
            using var component = Component.FromText(fixture.Engine, Text);

            using var export = component.GetExport("add");

            export.Should().NotBeNull();
            component.GetExport("nope").Should().BeNull();
        }
    }
}
