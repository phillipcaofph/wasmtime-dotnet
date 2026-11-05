#nullable enable

using System;
using System.Collections.Generic;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    public class ComponentHostResourceFixture : ComponentFixture { }

    /// <summary>
    /// Host-defined resources on a synchronous engine with direct callbacks (<c>host-resources.wat</c>,
    /// plus <c>host-resource-extras.wat</c> for composite results, aliasing and returned arguments).
    /// </summary>
    public sealed class ComponentHostResourceTests : IClassFixture<ComponentHostResourceFixture>
    {
        private readonly ComponentHostResourceFixture fixture;

        public ComponentHostResourceTests(ComponentHostResourceFixture fixture)
        {
            this.fixture = fixture;
        }

        private CounterHost Host(Action<CounterHost>? configure = null) =>
            new(fixture.Engine, configure: configure);

        private ComponentInstance Instantiate(CounterHost host, Store store, string file = "host-resources.wat")
        {
            using var component = fixture.LoadComponent(file);
            return host.Instantiate(store, component);
        }

        private static ComponentFunction Function(ComponentInstance instance, string name) =>
            instance.GetFunction(name) ?? throw new InvalidOperationException($"Missing export {name}.");

        [Fact]
        public void RoundtripRunsDestructorOnce()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);

            Function(instance, "roundtrip").Call(ComponentValue.U32(5))!.AsU32().Should().Be(6);

            host.Created.Should().ContainSingle();
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(host.Created[0]);
            host.Type.CountHeldByGuest(store).Should().Be(0);
            Function(instance, "live").Call()!.AsU32().Should().Be(0);
        }

        [Fact]
        public void ConstructorResultMovesObjectToGuest()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var heldDuringGet = new List<bool>();
            host.Get = (a, r) =>
            {
                var counter = a[0].AsHostResource(host.Type);
                heldDuringGet.Add(host.Type.IsHeldByGuest(store, counter));
                r[0] = ComponentValue.U32(counter.Value);
            };
            var instance = Instantiate(host, store);

            Function(instance, "roundtrip").Call(ComponentValue.U32(1))!.AsU32().Should().Be(2);

            heldDuringGet.Should().Equal(true);
            host.Type.IsHeldByGuest(store, host.Created[0]).Should().BeFalse();
        }

        [Fact]
        public void MakeReturnsOwnershipToHost()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);

            var made = Function(instance, "make").Call(ComponentValue.U32(10))!;

            made.ResourceOwnership.Should().Be(ComponentResourceOwnership.Own);
            made.IsHostResource(host.Type).Should().BeTrue();
            made.AsHostResource(host.Type).Should().BeSameAs(host.Created.Should().ContainSingle().Subject);
            made.AsHostResource(host.Type).Value.Should().Be(10);
            made.TakeHostResource(host.Type).Should().BeSameAs(host.Created[0]);
            host.Destroyed.Should().BeEmpty();
            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(0);
        }

        [Fact]
        public void InspectLendsAsBorrow()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(4);

            Function(instance, "inspect").Call(ComponentValue.Borrow(host.Type, counter))!.AsU32().Should().Be(8);

            host.Destroyed.Should().BeEmpty();
            store.GetComponentResourceStatistics().HostObjectsLent.Should().Be(0);
            host.Type.IsHeldByGuest(store, counter).Should().BeFalse();
        }

        [Fact]
        public void InferredResourceLendsAtBorrowAndMovesAtOwn()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(3);

            Function(instance, "inspect").Call(ComponentValue.Resource(host.Type, counter))!.AsU32().Should().Be(6);
            host.Destroyed.Should().BeEmpty();

            Function(instance, "take").Call(ComponentValue.Resource(host.Type, counter))!.AsU32().Should().Be(3);
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);
        }

        [Fact]
        public void TakeMovesAndGuestDropDestroysOnce()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(12);

            Function(instance, "take").Call(ComponentValue.Own(host.Type, counter))!.AsU32().Should().Be(12);

            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);
            host.Type.CountHeldByGuest(store).Should().Be(0);
            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(0);
        }

        [Fact]
        public void TakenOwnArgumentIsNotDestroyed()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            Counter? taken = null;
            host.Consume = (a, r) =>
            {
                taken = a[0].TakeHostResource(host.Type);
                r[0] = ComponentValue.U32(taken.Value);
            };
            var instance = Instantiate(host, store);
            var made = Function(instance, "make").Call(ComponentValue.U32(7))!.AsHostResource(host.Type);

            Function(instance, "give-back").Call(ComponentValue.Own(host.Type, made))!.AsU32().Should().Be(7);

            taken.Should().BeSameAs(made);
            host.Destroyed.Should().BeEmpty();
            host.Type.IsHeldByGuest(store, made).Should().BeFalse();
        }

        [Fact]
        public void UnclaimedOwnArgumentIsDestroyedAtCallbackEnd()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            host.Consume = (a, r) =>
            {
                host.Log.Add("consume");
                r[0] = ComponentValue.U32(99);
            };
            var instance = Instantiate(host, store);
            var counter = new Counter(5);

            Function(instance, "give-back").Call(ComponentValue.Own(host.Type, counter))!.AsU32().Should().Be(99);

            host.Log.Should().Equal("consume", "destroy 5");
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);
        }

        [Fact]
        public void ViewedButUnclaimedOwnArgumentIsDestroyed()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            host.Consume = (a, r) => r[0] = ComponentValue.U32(a[0].AsHostResource(host.Type).Value * 2);
            var instance = Instantiate(host, store);
            var counter = new Counter(5);

            Function(instance, "give-back").Call(ComponentValue.Own(host.Type, counter))!.AsU32().Should().Be(10);

            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);
        }

        [Fact]
        public void OwnArgumentReturnedAsResultIsNotDestroyed()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store, "host-resource-extras.wat");
            var counter = new Counter(8);

            Function(instance, "relay-keep").Call(ComponentValue.Own(host.Type, counter));

            host.Destroyed.Should().BeEmpty();
            host.Type.IsHeldByGuest(store, counter).Should().BeTrue();

            Function(instance, "drop-kept").Call();

            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);
            host.Type.IsHeldByGuest(store, counter).Should().BeFalse();
        }

        [Fact]
        public void OwnArgumentRewrappedAsResultIsNotDestroyed()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            host.Relay = (a, r) => r[0] = ComponentValue.Own(host.Type, a[0].AsHostResource(host.Type));
            var instance = Instantiate(host, store, "host-resource-extras.wat");
            var counter = new Counter(8);

            Function(instance, "relay-keep").Call(ComponentValue.Own(host.Type, counter));

            host.Destroyed.Should().BeEmpty();
            host.Type.IsHeldByGuest(store, counter).Should().BeTrue();
        }

        [Fact]
        public void BorrowArgumentsAreReleasedAutomatically()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            host.Get = (a, r) => r[0] = ComponentValue.U32(42);
            host.Peek = (a, r) => r[0] = ComponentValue.U32(42);
            var instance = Instantiate(host, store);

            Function(instance, "roundtrip").Call(ComponentValue.U32(1))!.AsU32().Should().Be(42);

            host.Destroyed.Should().ContainSingle();
        }

        [Fact]
        public void BorrowViewExpiresAfterCallback()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            ComponentValue? captured = null;
            host.Peek = (a, r) =>
            {
                captured = a[0];
                a[0].ResourceOwnership.Should().Be(ComponentResourceOwnership.Borrow);
                r[0] = ComponentValue.U32(a[0].AsHostResource(host.Type).Value);
            };
            var instance = Instantiate(host, store);

            Function(instance, "roundtrip").Call(ComponentValue.U32(1))!.AsU32().Should().Be(2);

            captured.Should().NotBeNull();
            captured!.Invoking(c => c.AsHostResource(host.Type)).Should().Throw<ObjectDisposedException>();
            captured!.TryAsHostResource(host.Type, out _).Should().BeFalse();
        }

        [Fact]
        public void StashKeepsObjectUntilUnstash()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(3);

            Function(instance, "stash").Call(ComponentValue.Own(host.Type, counter));

            host.Destroyed.Should().BeEmpty();
            host.Type.IsHeldByGuest(store, counter).Should().BeTrue();
            host.Type.CountHeldByGuest(store).Should().Be(1);
            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(1);

            Function(instance, "unstash").Call();

            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);
            host.Type.IsHeldByGuest(store, counter).Should().BeFalse();
        }

        [Fact]
        public void SwapOwnAndBorrowOfDistinctObjects()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var owned = new Counter(3);
            var lent = new Counter(4);

            Function(instance, "swap").Call(ComponentValue.Own(host.Type, owned), ComponentValue.Borrow(host.Type, lent))!
                .AsU32().Should().Be(7);

            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(owned);
            store.GetComponentResourceStatistics().HostObjectsLent.Should().Be(0);
        }

        [Fact]
        public void SameObjectAsOwnAndBorrowRejected()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(3);

            Function(instance, "swap")
                .Invoking(f => f.Call(ComponentValue.Own(host.Type, counter), ComponentValue.Borrow(host.Type, counter)))
                .Should().Throw<InvalidOperationException>();

            Function(instance, "live").Call()!.AsU32().Should().Be(0);
            Function(instance, "roundtrip").Call(ComponentValue.U32(1))!.AsU32().Should().Be(2);
            host.Destroyed.Should().NotContain(counter);
        }

        [Fact]
        public void SecondOwnTransferRejected()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(3);
            Function(instance, "stash").Call(ComponentValue.Own(host.Type, counter));

            Function(instance, "take").Invoking(f => f.Call(ComponentValue.Own(host.Type, counter)))
                .Should().Throw<InvalidOperationException>();

            Function(instance, "live").Call()!.AsU32().Should().Be(0);
            Function(instance, "unstash").Call();
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);
        }

        [Fact]
        public void OwnTransferIntoSecondStoreRejected()
        {
            using var host = Host();
            using var first = fixture.CreateStore();
            using var second = fixture.CreateStore();
            var firstInstance = Instantiate(host, first);
            var secondInstance = Instantiate(host, second);
            var counter = new Counter(3);
            Function(firstInstance, "stash").Call(ComponentValue.Own(host.Type, counter));

            Function(secondInstance, "take").Invoking(f => f.Call(ComponentValue.Own(host.Type, counter)))
                .Should().Throw<InvalidOperationException>();

            Function(secondInstance, "live").Call()!.AsU32().Should().Be(0);

            // Once the first store releases the object, the second may own it.
            Function(firstInstance, "unstash").Call();
            Function(secondInstance, "stash").Call(ComponentValue.Own(host.Type, counter));
            host.Type.IsHeldByGuest(second, counter).Should().BeTrue();
        }

        [Fact]
        public void ExplicitOwnAtBorrowPositionRejected()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);

            Function(instance, "inspect").Invoking(f => f.Call(ComponentValue.Own(host.Type, new Counter(1))))
                .Should().Throw<ArgumentException>().WithMessage("*c:*");

            Function(instance, "live").Call()!.AsU32().Should().Be(0);
            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(0);
        }

        [Fact]
        public void ExplicitBorrowAtOwnPositionRejected()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);

            Function(instance, "take").Invoking(f => f.Call(ComponentValue.Borrow(host.Type, new Counter(1))))
                .Should().Throw<ArgumentException>();

            Function(instance, "live").Call()!.AsU32().Should().Be(0);
            store.GetComponentResourceStatistics().HostObjectsLent.Should().Be(0);
        }

        [Fact]
        public void WrongResourceTypeRejected()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var otherType = new ComponentHostResourceType<Counter>();

            Function(instance, "take").Invoking(f => f.Call(ComponentValue.Own(otherType, new Counter(1))))
                .Should().Throw<ArgumentException>();
            Function(instance, "take").Invoking(f => f.Call(ComponentValue.U32(1)))
                .Should().Throw<ArgumentException>();
            Function(instance, "roundtrip").Invoking(f => f.Call(ComponentValue.Own(host.Type, new Counter(1))))
                .Should().Throw<ArgumentException>();

            Function(instance, "live").Call()!.AsU32().Should().Be(0);
        }

        [Fact]
        public void ReturningGuestOwnedObjectTraps()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(3);
            Function(instance, "stash").Call(ComponentValue.Own(host.Type, counter));
            host.Constructor = (a, r) => r[0] = ComponentValue.Resource(host.Type, counter);

            var thrown = Function(instance, "roundtrip").Invoking(f => f.Call(ComponentValue.U32(1)))
                .Should().Throw<WasmtimeException>().Which;

            thrown.InnerException.Should().BeOfType<InvalidOperationException>();
            host.Destroyed.Should().BeEmpty();
            host.Type.IsHeldByGuest(store, counter).Should().BeTrue();
        }

        [Fact]
        public void FailedCallbackDestroysReturnedObjects()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var first = new Counter(1);
            var second = new Counter(2);
            host.Pair = (a, r) => r[0] = ComponentValue.Tuple(new[]
            {
                ComponentValue.Resource(host.Type, first),
                ComponentValue.Borrow(host.Type, second),
            });
            var instance = Instantiate(host, store, "host-resource-extras.wat");

            var thrown = Function(instance, "pair-sum").Invoking(f => f.Call())
                .Should().Throw<WasmtimeException>().Which;

            thrown.InnerException.Should().BeOfType<InvalidOperationException>();
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(first);
            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(0);
        }

        [Fact]
        public void CompositeResultMovesEveryObject()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store, "host-resource-extras.wat");

            Function(instance, "pair-sum").Call()!.AsU32().Should().Be(3);

            host.Destroyed.Should().HaveCount(2);
            host.Destroyed.Should().BeEquivalentTo(host.Created);
        }

        [Fact]
        public void DestructorExceptionBecomesInnerException()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var failure = new InvalidTimeZoneException("destructor failed");
            host.OnDestroy = _ => throw failure;
            var instance = Instantiate(host, store);

            var thrown = Function(instance, "roundtrip").Invoking(f => f.Call(ComponentValue.U32(1)))
                .Should().Throw<WasmtimeException>().Which;

            thrown.InnerException.Should().BeSameAs(failure);
            host.Destroyed.Should().ContainSingle();
        }

        [Fact]
        public void UnclaimedArgumentDestructorFailureTraps()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var failure = new InvalidTimeZoneException("destructor failed");
            host.OnDestroy = _ => throw failure;
            host.Consume = (a, r) => r[0] = ComponentValue.U32(1);
            var instance = Instantiate(host, store);

            var thrown = Function(instance, "give-back").Invoking(f => f.Call(ComponentValue.Own(host.Type, new Counter(1))))
                .Should().Throw<WasmtimeException>().Which;

            thrown.InnerException.Should().BeSameAs(failure);
            host.Destroyed.Should().ContainSingle();
        }

        [Fact]
        public void FailedArgumentDestructorDestroysReturnedObjectOnce()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var failure = new InvalidTimeZoneException("destructor failed");
            var argument = new Counter(1);
            var fresh = new Counter(2);
            host.OnDestroy = c =>
            {
                if (ReferenceEquals(c, argument))
                {
                    throw failure;
                }
            };

            // relay ignores its own argument (so the binding destroys it) and returns a new object.
            host.Relay = (a, r) => r[0] = ComponentValue.Resource(host.Type, fresh);
            var instance = Instantiate(host, store, "host-resource-extras.wat");

            var thrown = Function(instance, "relay-keep").Invoking(f => f.Call(ComponentValue.Own(host.Type, argument)))
                .Should().Throw<WasmtimeException>().Which;

            // The callback failed, so the guest never received fresh: it is destroyed at once, not kept.
            thrown.InnerException.Should().BeSameAs(failure);
            host.Destroyed.Should().Equal(argument, fresh);
            host.Type.IsHeldByGuest(store, fresh).Should().BeFalse();
            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(0);

            store.ReleaseComponentResources();
            host.Destroyed.Should().HaveCount(2);
        }

        [Fact]
        public void OwnershipStaysCommittedWhenAnIssuedCallTraps()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var first = new Counter(1);
            var second = new Counter(2);
            Function(instance, "stash").Call(ComponentValue.Own(host.Type, first));

            // The guest traps (something is already stashed) after second was lowered into its table.
            Function(instance, "stash").Invoking(f => f.Call(ComponentValue.Own(host.Type, second)))
                .Should().Throw<WasmtimeException>();

            host.Destroyed.Should().BeEmpty();
            host.Type.IsHeldByGuest(store, second).Should().BeTrue();
            store.GetComponentResourceStatistics().HostObjectsHeldByGuests.Should().Be(2);

            store.ReleaseComponentResources();
            host.Destroyed.Should().HaveCount(2).And.Contain(first).And.Contain(second);
        }

        [Fact]
        public void OwnArgumentMovedIntoAnotherStoreIsNotDestroyed()
        {
            using var host = Host();
            using var first = fixture.CreateStore();
            using var second = fixture.CreateStore();
            var firstInstance = Instantiate(host, first);
            var secondInstance = Instantiate(host, second);
            var counter = new Counter(4);
            host.Consume = (a, r) =>
            {
                // Moves the unclaimed argument into a guest of the second store.
                Function(secondInstance, "stash").Call(a[0]);
                a[0].Invoking(v => v.TakeHostResource(host.Type)).Should().Throw<InvalidOperationException>();
                r[0] = ComponentValue.U32(1);
            };

            Function(firstInstance, "give-back").Call(ComponentValue.Own(host.Type, counter))!.AsU32().Should().Be(1);

            host.Destroyed.Should().BeEmpty();
            host.Type.IsHeldByGuest(second, counter).Should().BeTrue();
            host.Type.IsHeldByGuest(first, counter).Should().BeFalse();

            Function(secondInstance, "unstash").Call();
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);
        }

        [Fact]
        public void OwnArgumentWhoseMoveFailsIsStillDestroyedAtCallbackEnd()
        {
            using var host = Host();
            using var first = fixture.CreateStore();
            using var second = fixture.CreateStore();
            var firstInstance = Instantiate(host, first);
            var secondInstance = Instantiate(host, second);
            var counter = new Counter(4);
            Exception? moveFailure = null;
            host.Consume = (a, r) =>
            {
                // swap reserves the own argument, then the borrow exceeds the limit: the call is never issued.
                second.MaxComponentHostResources = 1;
                try
                {
                    Function(secondInstance, "swap").Call(a[0], ComponentValue.Borrow(host.Type, new Counter(5)));
                }
                catch (InvalidOperationException exception)
                {
                    moveFailure = exception;
                }

                r[0] = ComponentValue.U32(1);
            };

            Function(firstInstance, "give-back").Call(ComponentValue.Own(host.Type, counter))!.AsU32().Should().Be(1);

            moveFailure.Should().NotBeNull();
            host.Type.IsHeldByGuest(second, counter).Should().BeFalse();
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);
        }

        [Fact]
        public void StoreDisposeRunsNoDestructors()
        {
            using var host = Host();
            var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(3);
            Function(instance, "stash").Call(ComponentValue.Own(host.Type, counter));

            store.Invoking(s => s.Dispose()).Should().NotThrow();

            host.Destroyed.Should().BeEmpty();
            host.Type.IsHeldByGuest(store, counter).Should().BeFalse();

            // The object is free to move into another store.
            using var other = fixture.CreateStore();
            Function(Instantiate(host, other), "take").Call(ComponentValue.Own(host.Type, counter))!.AsU32().Should().Be(3);
        }

        [Fact]
        public void ReleaseComponentResourcesDestroysGuestHeldObjects()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            var counter = new Counter(3);
            Function(instance, "stash").Call(ComponentValue.Own(host.Type, counter));

            store.ReleaseComponentResources();

            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(counter);
            Function(instance, "live").Invoking(f => f.Call()).Should().Throw<InvalidOperationException>();
            host.Linker.Invoking(l => l.Instantiate(store, fixture.LoadComponent("host-resources.wat")))
                .Should().Throw<InvalidOperationException>();
            store.Invoking(s => s.ReleaseComponentResources()).Should().NotThrow();
            host.Destroyed.Should().ContainSingle();
        }

        [Fact]
        public void ReleaseAggregatesDestructorFailures()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            host.OnDestroy = c => throw new InvalidOperationException($"destroy {c.Value}");
            var first = Instantiate(host, store);
            var second = Instantiate(host, store);
            Function(first, "stash").Call(ComponentValue.Own(host.Type, new Counter(1)));
            Function(second, "stash").Call(ComponentValue.Own(host.Type, new Counter(2)));

            var thrown = store.Invoking(s => s.ReleaseComponentResources()).Should().Throw<AggregateException>().Which;

            thrown.InnerExceptions.Should().HaveCount(2);
            host.Destroyed.Should().HaveCount(2);
        }

        [Fact]
        public void MaxComponentHostResourcesLimitsTransfers()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store);
            store.MaxComponentHostResources = 0;

            Function(instance, "take").Invoking(f => f.Call(ComponentValue.Own(host.Type, new Counter(1))))
                .Should().Throw<InvalidOperationException>().WithMessage("*MaxComponentHostResources*");
            Function(instance, "inspect").Invoking(f => f.Call(ComponentValue.Borrow(host.Type, new Counter(1))))
                .Should().Throw<InvalidOperationException>();

            Function(instance, "live").Call()!.AsU32().Should().Be(0);

            store.MaxComponentHostResources = 1;
            Function(instance, "take").Call(ComponentValue.Own(host.Type, new Counter(4)))!.AsU32().Should().Be(4);
            store.Invoking(s => s.MaxComponentHostResources = -1).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void DefineResourceUnderTwoNamesAliases()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store, "host-resource-extras.wat");

            var made = Function(instance, "make").Call(ComponentValue.U32(5))!.AsHostResource(host.Type);
            Function(instance, "other-take").Call(ComponentValue.Own(host.Type, made))!.AsU32().Should().Be(5);

            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(made);
        }

        [Fact]
        public void MisnamedResourceFailsInstantiation()
        {
            using var host = new CounterHost(fixture.Engine, resourceName: "Counter");
            using var store = fixture.CreateStore();
            using var component = fixture.LoadComponent("host-resources.wat");

            host.Linker.Invoking(l => l.Instantiate(store, component))
                .Should().Throw<WasmtimeException>().WithMessage("*counter*");
        }

        [Fact]
        public void TypeDefinedInTwoLinkersWorks()
        {
            var destroyed = new Dictionary<uint, int>();
            var type = new ComponentHostResourceType<Counter>(c =>
            {
                lock (destroyed)
                {
                    destroyed[c.Value] = destroyed.TryGetValue(c.Value, out var n) ? n + 1 : 1;
                }
            });
            using var firstHost = new CounterHost(fixture.Engine, type);
            using var secondHost = new CounterHost(fixture.Engine, type);
            using var firstStore = fixture.CreateStore();
            using var secondStore = fixture.CreateStore();
            var first = Instantiate(firstHost, firstStore);
            var second = Instantiate(secondHost, secondStore);

            Function(first, "roundtrip").Call(ComponentValue.U32(10))!.AsU32().Should().Be(11);
            Function(second, "stash").Call(ComponentValue.Own(type, new Counter(20)));
            Function(second, "take").Call(ComponentValue.Own(type, new Counter(30)))!.AsU32().Should().Be(30);

            destroyed.Should().BeEquivalentTo(new Dictionary<uint, int> { [11] = 1, [30] = 1 });
            type.CountHeldByGuest(firstStore).Should().Be(0);
            type.CountHeldByGuest(secondStore).Should().Be(1);

            secondStore.ReleaseComponentResources();
            destroyed.Should().ContainKey(20).WhoseValue.Should().Be(1);
            type.CountHeldByGuest(secondStore).Should().Be(0);

            // The first store is unaffected by the second one's release.
            Function(first, "take").Call(ComponentValue.Own(type, new Counter(40)))!.AsU32().Should().Be(40);
            destroyed.Should().ContainKey(40).WhoseValue.Should().Be(1);
        }

        [Fact]
        public void DefineResourceCreatesAndDefinesAType()
        {
            using var linker = new ComponentLinker(fixture.Engine);
            ComponentHostResourceType<Counter> type;
            using (var root = linker.Root())
            using (var host = root.AddInstance("host"))
            {
                type = host.DefineResource<Counter>("counter");
                host.Invoking(h => h.DefineResource("counter", type)).Should().Throw<WasmtimeException>();
                host.Invoking(h => h.DefineResource(null!, type)).Should().Throw<ArgumentNullException>();
                host.Invoking(h => h.DefineResource("other", null!)).Should().Throw<ArgumentNullException>();
            }

            type.Name.Should().Be("counter");
        }

        [Fact]
        public void UnclaimedArgumentRewrappedIntoAnotherStoreIsNotDestroyed()
        {
            using var host = Host();
            using var first = fixture.CreateStore();
            using var second = fixture.CreateStore();
            var source = Instantiate(host, first);
            var target = Instantiate(host, second);
            host.Consume = (a, r) =>
            {
                // A new value wrapping the same object, not the argument itself.
                var counter = a[0].AsHostResource(host.Type);
                Function(target, "stash").Call(ComponentValue.Own(host.Type, counter));
                r[0] = ComponentValue.U32(counter.Value);
            };
            var moved = new Counter(7);

            Function(source, "give-back").Call(ComponentValue.Own(host.Type, moved))!.AsU32().Should().Be(7);

            host.Destroyed.Should().BeEmpty();
            host.Type.IsHeldByGuest(second, moved).Should().BeTrue();
            Function(target, "unstash").Call();
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(moved);
        }

        [Fact]
        public void LentObjectCannotBeMovedIntoAnotherStore()
        {
            using var host = Host();
            using var first = fixture.CreateStore();
            using var second = fixture.CreateStore();
            var source = Instantiate(host, first);
            var target = Instantiate(host, second);
            host.Peek = (a, r) =>
            {
                var counter = a[0].AsHostResource(host.Type);
                Function(target, "take").Invoking(f => f.Call(ComponentValue.Own(host.Type, counter)))
                    .Should().Throw<InvalidOperationException>().WithMessage("*lent*");
                r[0] = ComponentValue.U32(counter.Value);
            };
            var lent = new Counter(3);

            Function(source, "inspect").Call(ComponentValue.Borrow(host.Type, lent))!.AsU32().Should().Be(6);

            host.Destroyed.Should().BeEmpty();
            Function(target, "take").Call(ComponentValue.Own(host.Type, lent))!.AsU32().Should().Be(3);
            host.Destroyed.Should().ContainSingle().Which.Should().BeSameAs(lent);
        }

        [Fact]
        public void GuestOwnedObjectCannotBeLentToAnotherStore()
        {
            using var host = Host();
            using var first = fixture.CreateStore();
            using var second = fixture.CreateStore();
            var owner = Instantiate(host, first);
            var other = Instantiate(host, second);
            var owned = new Counter(5);
            Function(owner, "stash").Call(ComponentValue.Own(host.Type, owned));

            Function(other, "inspect").Invoking(f => f.Call(ComponentValue.Borrow(host.Type, owned)))
                .Should().Throw<InvalidOperationException>().WithMessage("*owned by a component*");
        }

        [Fact]
        public void HostObjectReturnedWhereNoResourceIsExpectedStaysWithTheHost()
        {
            using var host = Host();
            using var first = fixture.CreateStore();
            using var second = fixture.CreateStore();
            var source = Instantiate(host, first);
            var target = Instantiate(host, second);
            var returned = new Counter(9);
            host.Peek = (a, r) => r[0] = ComponentValue.Resource(host.Type, returned);

            Function(source, "inspect").Invoking(f => f.Call(ComponentValue.Borrow(host.Type, new Counter(1))))
                .Should().Throw<WasmtimeException>().WithMessage("*does not return one*");

            host.Type.IsHeldByGuest(first, returned).Should().BeFalse();
            Function(target, "take").Call(ComponentValue.Own(host.Type, returned))!.AsU32().Should().Be(9);
        }

        [Fact]
        public void NonResourceArgumentMismatchIsRejectedBeforeOwnershipMoves()
        {
            using var host = Host();
            using var store = fixture.CreateStore();
            var instance = Instantiate(host, store, "resource-containers.wat");
            var counter = new Counter(4);

            Function(instance, "pair")
                .Invoking(f => f.Call(ComponentValue.Tuple(new[]
                {
                    ComponentValue.Own(host.Type, counter),
                    ComponentValue.String("oops"),
                })))
                .Should().Throw<ArgumentException>().WithMessage("*expected a U32 value*");

            host.Type.IsHeldByGuest(store, counter).Should().BeFalse();
            Function(instance, "pair")
                .Call(ComponentValue.Tuple(new[] { ComponentValue.Own(host.Type, counter), ComponentValue.U32(1) }))
                .Should().NotBeNull();
        }
    }
}
