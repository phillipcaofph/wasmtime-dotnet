#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    public class ComponentResourceContainerFixture : ComponentFixture { }

    /// <summary>
    /// Host resources nested in lists, options, tuples and records, as parameters and results
    /// (<c>resource-containers.wat</c>), including validation at every nesting level.
    /// </summary>
    public sealed class ComponentResourceContainerTests : IClassFixture<ComponentResourceContainerFixture>
    {
        private readonly ComponentResourceContainerFixture fixture;

        public ComponentResourceContainerTests(ComponentResourceContainerFixture fixture)
        {
            this.fixture = fixture;
        }

        private ComponentInstance Instantiate(CounterHost host, Store store)
        {
            using var component = fixture.LoadComponent("resource-containers.wat");
            return host.Instantiate(store, component);
        }

        private static ComponentFunction Function(ComponentInstance instance, string name) =>
            instance.GetFunction(name) ?? throw new InvalidOperationException($"Missing export {name}.");

        private static ComponentValue Record(params (string Name, ComponentValue Value)[] fields) =>
            ComponentValue.Record(fields.Select(f => new KeyValuePair<string, ComponentValue>(f.Name, f.Value)).ToArray());

        [Fact]
        public void ListOfOwnedResultsBecomeHostObjects()
        {
            using var host = new CounterHost(fixture.Engine);
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);

            var made = Function(instance, "make-many").Call(ComponentValue.U32(3))!.AsList();

            made.Should().HaveCount(3);
            made.Select(v => v.ResourceOwnership).Should().AllBeEquivalentTo(ComponentResourceOwnership.Own);
            made.Select(v => v.AsHostResource(host.Type).Value).Should().Equal(0u, 1u, 2u);
            made.Select(v => v.AsHostResource(host.Type)).Should().Equal(host.Created);
            host.Destroyed.Should().BeEmpty();
            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(0);
        }

        [Fact]
        public void ListOfBorrowsLendsEachObject()
        {
            using var host = new CounterHost(fixture.Engine);
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counters = new[] { new Counter(0), new Counter(1), new Counter(2) };

            Function(instance, "sum")
                .Call(ComponentValue.List(counters.Select(c => ComponentValue.Borrow(host.Type, c)).ToArray()))!
                .AsU32().Should().Be(3);

            host.Destroyed.Should().BeEmpty();
            store.GetComponentResourceStatistics().HostObjectsLent.Should().Be(0);

            // The same object may be lent more than once in one call.
            Function(instance, "sum")
                .Call(ComponentValue.List(new[]
                {
                    ComponentValue.Borrow(host.Type, counters[2]),
                    ComponentValue.Resource(host.Type, counters[2]),
                }))!
                .AsU32().Should().Be(4);
            store.GetComponentResourceStatistics().HostObjectsLent.Should().Be(0);
        }

        [Fact]
        public void OptionOfOwnMoves()
        {
            using var host = new CounterHost(fixture.Engine);
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(9);

            Function(instance, "maybe").Call(ComponentValue.Some(ComponentValue.Own(host.Type, counter)))!
                .AsU32().Should().Be(9);
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);

            Function(instance, "maybe").Call(ComponentValue.None())!.AsU32().Should().Be(uint.MaxValue);
        }

        [Fact]
        public void TupleAndRecordMarshal()
        {
            using var host = new CounterHost(fixture.Engine);
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var paired = new Counter(5);

            Function(instance, "pair")
                .Call(ComponentValue.Tuple(new[] { ComponentValue.Own(host.Type, paired), ComponentValue.U32(10) }))!
                .AsU32().Should().Be(15);
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(paired);

            var owned = new Counter(3);
            var lent = new Counter(4);
            Function(instance, "rec")
                .Call(Record(("a", ComponentValue.Own(host.Type, owned)), ("b", ComponentValue.Borrow(host.Type, lent))))!
                .AsU32().Should().Be(7);
            host.Destroyed.Should().Equal(paired, owned);
            store.GetComponentResourceStatistics().HostObjectsLent.Should().Be(0);
        }

        [Fact]
        public void OptionOfOwnResult()
        {
            using var host = new CounterHost(fixture.Engine);
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);

            var some = Function(instance, "maybe-make").Call(ComponentValue.U32(1))!;
            some.IsSome.Should().BeTrue();
            some.Payload!.ResourceOwnership.Should().Be(ComponentResourceOwnership.Own);
            some.Payload.AsHostResource(host.Type).Value.Should().Be(77);

            Function(instance, "maybe-make").Call(ComponentValue.U32(0))!.IsSome.Should().BeFalse();
            host.Destroyed.Should().BeEmpty();
        }

        [Fact]
        public void NestedBorrowInOwnPositionRejected()
        {
            using var host = new CounterHost(fixture.Engine);
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);

            Function(instance, "maybe")
                .Invoking(f => f.Call(ComponentValue.Some(ComponentValue.Borrow(host.Type, new Counter(1)))))
                .Should().Throw<ArgumentException>().WithMessage("*x.some*");

            Function(instance, "maybe").Call(ComponentValue.None())!.AsU32().Should().Be(uint.MaxValue);
        }

        [Fact]
        public void RecordWithMissingOrReorderedFieldsRejectedBeforeTheCall()
        {
            using var host = new CounterHost(fixture.Engine);
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var owned = new Counter(2);
            var lent = new Counter(9);

            Function(instance, "rec")
                .Invoking(f => f.Call(Record(("a", ComponentValue.Own(host.Type, owned)))))
                .Should().Throw<ArgumentException>().WithMessage("r:*2 field*");
            Function(instance, "rec")
                .Invoking(f => f.Call(Record(
                    ("b", ComponentValue.Borrow(host.Type, lent)),
                    ("a", ComponentValue.Own(host.Type, owned)))))
                .Should().Throw<ArgumentException>().WithMessage("r:*'a'*");
            Function(instance, "rec")
                .Invoking(f => f.Call(Record(
                    ("a", ComponentValue.Own(host.Type, owned)),
                    ("b", ComponentValue.Borrow(host.Type, lent)),
                    ("c", ComponentValue.U32(1)))))
                .Should().Throw<ArgumentException>();

            // Nothing was transferred, and the store is still usable.
            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(0);
            store.GetComponentResourceStatistics().HostObjectsLent.Should().Be(0);
            Function(instance, "rec")
                .Call(Record(("a", ComponentValue.Own(host.Type, owned)), ("b", ComponentValue.Borrow(host.Type, lent))))!
                .AsU32().Should().Be(11);
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(owned);
        }

        [Fact]
        public void ResourceAtNonResourcePositionRejectedWithItsPath()
        {
            using var host = new CounterHost(fixture.Engine);
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);

            // In a function with resources, at a nested position whose type has none.
            Function(instance, "pair")
                .Invoking(f => f.Call(ComponentValue.Tuple(new[]
                {
                    ComponentValue.Own(host.Type, new Counter(1)),
                    ComponentValue.Resource(host.Type, new Counter(2)),
                })))
                .Should().Throw<ArgumentException>().WithMessage("p[1]:*");

            // In a function without any resource.
            using var component = fixture.LoadComponent("u32-list.wat");
            var plain = new ComponentLinker(fixture.Engine);
            using (plain)
            {
                var sum = Function(plain.Instantiate(store, component), "sum");
                sum.Invoking(f => f.Call(ComponentValue.List(new[]
                    {
                        ComponentValue.U32(1),
                        ComponentValue.Resource(host.Type, new Counter(2)),
                    })))
                    .Should().Throw<ArgumentException>().WithMessage("xs[1]:*");
                sum.Call(ComponentValue.List(new[] { ComponentValue.U32(1), ComponentValue.U32(2) }))!
                    .AsU32().Should().Be(3);
            }

            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(0);
            Function(instance, "pair")
                .Call(ComponentValue.Tuple(new[] { ComponentValue.Own(host.Type, new Counter(1)), ComponentValue.U32(10) }))!
                .AsU32().Should().Be(11);
        }

        [Fact]
        public void NestedDuplicateOwnRejected()
        {
            using var host = new CounterHost(fixture.Engine);
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(1);

            Function(instance, "rec")
                .Invoking(f => f.Call(Record(
                    ("a", ComponentValue.Own(host.Type, counter)),
                    ("b", ComponentValue.Borrow(host.Type, counter)))))
                .Should().Throw<InvalidOperationException>();

            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(0);
            Function(instance, "maybe").Call(ComponentValue.None())!.AsU32().Should().Be(uint.MaxValue);
        }

        [Fact]
        public void NestedWrongKindRejected()
        {
            using var host = new CounterHost(fixture.Engine);
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);

            Function(instance, "sum")
                .Invoking(f => f.Call(ComponentValue.List(new[] { ComponentValue.U32(1) })))
                .Should().Throw<ArgumentException>().WithMessage("*xs[0]*");
            Function(instance, "pair")
                .Invoking(f => f.Call(ComponentValue.Tuple(new[] { ComponentValue.U32(1), ComponentValue.U32(10) })))
                .Should().Throw<ArgumentException>().WithMessage("*p[0]*");

            Function(instance, "maybe").Call(ComponentValue.None())!.AsU32().Should().Be(uint.MaxValue);
        }

        [Fact]
        public void LocalResourceImportFailsToCompile()
        {
            fixture.Invoking(f => f.LoadComponent("local-resource-import.wat"))
                .Should().Throw<WasmtimeException>().WithMessage("*not valid to be used as import*");
        }

        [Theory]
        [InlineData("host-resources.wat")]
        [InlineData("guest-resources.wat")]
        [InlineData("resource-containers.wat")]
        [InlineData("foreign-resources.wat")]
        [InlineData("host-resource-extras.wat")]
        public void ResourceFixturesCompile(string file)
        {
            using var component = fixture.LoadComponent(file);
            component.Should().NotBeNull();
        }
    }
}
