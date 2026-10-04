using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    public class ComponentAsyncFixture : ComponentFixture
    {
        public override Config GetEngineConfig() =>
            new Config()
                .WithComponentModel(true)
                .WithComponentModelAsync(true);
    }

    public sealed class ComponentAsyncTests : IClassFixture<ComponentAsyncFixture>
    {
        private readonly ComponentAsyncFixture fixture;

        public ComponentAsyncTests(ComponentAsyncFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task ItInstantiatesAndCallsAComponentAsynchronously()
        {
            using var component = fixture.LoadComponent("tiny.wat");
            using var linker = new ComponentLinker(fixture.Engine);

            var instance = await linker.InstantiateAsync(fixture.Store, component);
            var add = instance.GetFunction("add")!;

            (await add.CallAsync(ComponentValue.S32(20), ComponentValue.S32(22)))!
                .AsS32().Should().Be(42);
        }

        [Fact]
        public async Task ItRejectsAsyncCallsWhenTheEngineWasNotConfiguredForAsync()
        {
            using var engine = new Engine(new Config().WithComponentModel(true));
            using var store = new Store(engine);
            using var component = Component.FromTextFile(engine, "Components/tiny.wat");
            using var linker = new ComponentLinker(engine);
            var instance = linker.Instantiate(store, component);

            await FluentActions.Awaiting(() => linker.InstantiateAsync(store, component))
                .Should().ThrowAsync<System.InvalidOperationException>();

            var add = instance.GetFunction("add")!;
            await FluentActions.Awaiting(() => add.CallAsync(ComponentValue.S32(1), ComponentValue.S32(2)))
                .Should().ThrowAsync<System.InvalidOperationException>();
        }

        [Fact]
        public async Task ItHonorsCancellationBeforeStartingAnAsyncCall()
        {
            using var component = fixture.LoadComponent("tiny.wat");
            using var linker = new ComponentLinker(fixture.Engine);
            var instance = await linker.InstantiateAsync(fixture.Store, component);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await FluentActions.Awaiting(() => instance.GetFunction("add")!
                    .CallAsync(new[] { ComponentValue.S32(1), ComponentValue.S32(2) }, cancellation.Token))
                .Should().ThrowAsync<System.OperationCanceledException>();
        }

        [Fact]
        public async Task ItReleasesTheStoreAfterAnAsyncCallFailsValidation()
        {
            using var component = fixture.LoadComponent("tiny.wat");
            using var linker = new ComponentLinker(fixture.Engine);
            var instance = await linker.InstantiateAsync(fixture.Store, component);
            var add = instance.GetFunction("add")!;

            await FluentActions.Awaiting(() => add.CallAsync(ComponentValue.S32(1)))
                .Should().ThrowAsync<System.ArgumentException>();

            add.Call(ComponentValue.S32(20), ComponentValue.S32(22))!
                .AsS32().Should().Be(42);
        }

        [Fact]
        public async Task ItSurfacesAsyncInstantiationErrorsAndReleasesTheStore()
        {
            using var componentWithMissingImports = fixture.LoadComponent("host-import-declarations.wat");
            using var tinyComponent = fixture.LoadComponent("tiny.wat");
            using var linker = new ComponentLinker(fixture.Engine);

            await FluentActions.Awaiting(() =>
                    linker.InstantiateAsync(fixture.Store, componentWithMissingImports))
                .Should().ThrowAsync<WasmtimeException>();

            var instance = await linker.InstantiateAsync(fixture.Store, tinyComponent);
            (await instance.GetFunction("add")!.CallAsync(ComponentValue.S32(20), ComponentValue.S32(22)))!
                .AsS32().Should().Be(42);
        }
    }
}
