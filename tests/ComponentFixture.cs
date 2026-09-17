using System;
using System.IO;
using Wasmtime.Components;

namespace Wasmtime.Tests
{
    public abstract class ComponentFixture : IDisposable
    {
        public ComponentFixture()
        {
            Engine = new Engine(GetEngineConfig());

            Store = new Store(Engine);
        }

        public virtual Config GetEngineConfig()
        {
            return new Config()
                .WithComponentModel(true);
        }

        public Component LoadComponent(string fileName) =>
            Component.FromText(Engine, File.ReadAllText(Path.Combine("Components", fileName)));

        public Store CreateStore() => new Store(Engine);

        public Component Strings() => LoadComponent("strings.wat");

        public void Dispose()
        {
            if (!(Store is null))
            {
                Store.Dispose();
                Store = null;
            }

            if (!(Engine is null))
            {
                Engine.Dispose();
                Engine = null;
            }
        }

        public Engine Engine { get; set; }
        public Store Store { get; set; }
    }
}