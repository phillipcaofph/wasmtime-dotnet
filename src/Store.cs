using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wasmtime
{
    /// <summary>
    /// Represents context about a <see cref="Wasmtime.Store"/>.
    /// </summary>
    internal readonly ref struct StoreContext
    {
        internal StoreContext(IntPtr handle)
        {
            this.handle = handle;
        }

        internal void GC()
        {
            var error = Native.wasmtime_context_gc(handle);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }
        }

        internal ulong GetFuel()
        {
            var error = Native.wasmtime_context_get_fuel(handle, out ulong fuel);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }

            return fuel;
        }

        internal void SetFuel(ulong fuel)
        {
            var error = Native.wasmtime_context_set_fuel(handle, fuel);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }
        }

        internal Store Store
        {
            get
            {
                var data = Native.wasmtime_context_get_data(handle);

                // Since this is a weak handle, it could be `null` if the target object (`Store`)
                // was already collected. However, this would be an error in wasmtime-dotnet
                // itself because the `Store` must be kept alive when this is called, and
                // therefore this should never happen (otherwise, when the `Store` was already
                // GCed, its `Handle` might also be GCed and have run its finalizer, which
                // would already have freed the `GCHandle` (from the Finalize callback) and thus
                // it would already be undefined behavior to try to get the `GCHandle` from the
                // `IntPtr` value).
                var targetStore = (Store?)GCHandle.FromIntPtr(data).Target!;

                return targetStore;
            }
        }

        internal void SetWasiConfiguration(WasiConfiguration config)
        {
            var wasi = config.Build();
            var error = Native.wasmtime_context_set_wasi(handle, wasi.DangerousGetHandle());
            wasi.SetHandleAsInvalid();

            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }
        }

        /// <summary>
        /// Configures the relative deadline at which point WebAssembly code will trap.
        /// </summary>
        /// <param name="deadline"></param>
        public void SetEpochDeadline(ulong deadline)
        {
            Native.wasmtime_context_set_epoch_deadline(handle, deadline);
        }

        internal void SetEpochDeadlineAsyncYieldAndUpdate(ulong ticksBeyondCurrent)
        {
            Native.wasmtime_context_epoch_deadline_async_yield_and_update(handle, ticksBeyondCurrent);
        }

        internal void SetFuelAsyncYieldInterval(ulong interval)
        {
            var error = Native.wasmtime_context_fuel_async_yield_interval(handle, interval);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }
        }

        private static class Native
        {
            // async.h declares a wasmtime_error_t* result, but the implementation returns nothing.
            [DllImport(Engine.LibraryName)]
            public static extern void wasmtime_context_epoch_deadline_async_yield_and_update(IntPtr handle, ulong delta);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_context_fuel_async_yield_interval(IntPtr handle, ulong interval);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_context_gc(IntPtr handle);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_context_set_fuel(IntPtr handle, ulong fuel);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_context_get_fuel(IntPtr handle, out ulong fuel);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_context_set_wasi(IntPtr handle, IntPtr config);

            [DllImport(Engine.LibraryName)]
            public static extern void wasmtime_context_set_epoch_deadline(IntPtr handle, ulong ticksBeyondCurrent);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_context_get_data(IntPtr handle);
        }

        internal readonly IntPtr handle;
    }

    /// <summary>
    /// Represents a Wasmtime store.
    /// </summary>
    /// <remarks>
    /// A Wasmtime store may be sent between threads but cannot be used from more than one thread
    /// simultaneously.
    /// </remarks>
    public class Store : IDisposable
    {
        /// <summary>
        /// Constructs a new store.
        /// </summary>
        /// <param name="engine">The engine to use for the store.</param>
        public Store(Engine engine) : this(engine, null) { }

        /// <summary>
        /// Constructs a new store with the given context data.
        /// </summary>
        /// <param name="engine">The engine to use for the store.</param>
        /// <param name="data">The data to initialize the store with; this can later be accessed with the GetData function.</param>
        public Store(Engine engine, object? data)
        {
            if (engine is null)
            {
                throw new ArgumentNullException(nameof(engine));
            }

            IsComponentModelAsyncEnabled = engine.IsComponentModelAsyncEnabled;
            this.data = data;

            // Allocate a weak GCHandle, so that it does not participate in keeping the Store alive.
            // Otherwise, the circular reference would prevent the Store from being finalized even
            // if it's no longer referenced by user code.
            // The weak handle will be used to get the originating Store object from a Caller's
            // context in host callbacks.
            var storeHandle = GCHandle.Alloc(this, GCHandleType.Weak);

            handle = new Handle(Native.wasmtime_store_new(engine.NativeHandle, (IntPtr)storeHandle, Finalizer));

            contextHandle = Native.wasmtime_store_context(NativeHandle);
        }

        /// <summary>
        /// Gets or sets the fuel available for WebAssembly code to consume while executing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For this property to work fuel consumption must be enabled via <see cref="Config.WithFuelConsumption(bool)"/>.
        /// </para>
        /// <para>
        /// WebAssembly execution will automatically consume fuel but if so desired the embedder can also consume fuel manually
        /// to account for relative costs of host functions, for example.
        /// </para>
        /// </remarks>
        /// <value>The fuel available for WebAssembly code to consume while executing.</value>
        public ulong Fuel
        {
            get
            {
                ulong fuel = 0;
                if (Components.HostCallbackDispatcher.TryOwnerOperation(
                        contextHandle, Components.StoreOperation.GetFuel, ref fuel))
                {
                    System.GC.KeepAlive(this);
                    return fuel;
                }

                fuel = Context.GetFuel();
                System.GC.KeepAlive(this);
                return fuel;
            }

            set
            {
                if (!Components.HostCallbackDispatcher.TryOwnerOperation(
                        contextHandle, Components.StoreOperation.SetFuel, ref value))
                {
                    Context.SetFuel(value);
                }

                System.GC.KeepAlive(this);
            }
        }

        /// <summary>
        /// Limit the resources that this store may consume. Note that the limits are only used to limit the creation/growth of resources in the future,
        /// this does not retroactively attempt to apply limits to the store.
        /// </summary>
        /// <param name="memorySize">the maximum number of bytes a linear memory can grow to. Growing a linear memory beyond this limit will fail.
        /// Pass in a null value to use the default value (unlimited)</param>
        /// <param name="tableElements">the maximum number of elements in a table. Growing a table beyond this limit will fail.
        /// Pass in a null value to use the default value (unlimited)</param>
        /// <param name="instances">the maximum number of instances that can be created for a Store. Module instantiation will fail if this limit is exceeded.
        /// Pass in a null value to use the default value (10000)</param>
        /// <param name="tables">the maximum number of tables that can be created for a Store. Module instantiation will fail if this limit is exceeded.
        /// Pass in a null value to use the default value (10000)</param>
        /// <param name="memories">the maximum number of linear memories that can be created for a Store. Instantiation will fail with an error if this limit is exceeded.
        /// Pass in a null value to use the default value (10000)</param>
        public void SetLimits(long? memorySize = null, uint? tableElements = null, long? instances = null, long? tables = null, long? memories = null)
        {
            if (memorySize.HasValue && memorySize.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(memorySize));
            }

            if (instances.HasValue && instances.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(instances));
            }

            if (tables.HasValue && tables.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tables));
            }

            if (memories.HasValue && memories.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(memories));
            }

            long tableElements64 = -1;
            if (tableElements.HasValue)
            {
                tableElements64 = tableElements.Value;
            }

            Native.wasmtime_store_limiter(NativeHandle, memorySize ?? -1, tableElements64, instances ?? -1, tables ?? -1, memories ?? -1);
        }

        /// <summary>
        /// Perform garbage collection within the given store.
        /// </summary>
        public void GC()
        {
            ulong unused = 0;
            if (!Components.HostCallbackDispatcher.TryOwnerOperation(
                    contextHandle, Components.StoreOperation.GC, ref unused))
            {
                Context.GC();
            }

            System.GC.KeepAlive(this);
        }

        /// <summary>
        /// Configures WASI within the store.
        /// </summary>
        /// <param name="config">The WASI configuration to use.</param>
        public void SetWasiConfiguration(WasiConfiguration config)
        {
            Context.SetWasiConfiguration(config);
            System.GC.KeepAlive(this);
        }

        /// <summary>
        /// Configures the relative deadline at which point WebAssembly code will trap.
        /// </summary>
        /// <param name="ticksBeyondCurrent"></param>
        public void SetEpochDeadline(ulong ticksBeyondCurrent)
        {
            if (!Components.HostCallbackDispatcher.TryOwnerOperation(
                    contextHandle, Components.StoreOperation.SetEpochDeadline, ref ticksBeyondCurrent))
            {
                Context.SetEpochDeadline(ticksBeyondCurrent);
            }

            System.GC.KeepAlive(this);
        }

        /// <summary>
        /// Configures the epoch deadline so that reaching it suspends an asynchronous call
        /// instead of trapping, then moves the deadline <paramref name="ticksBeyondCurrent"/>
        /// ticks beyond the current epoch.
        /// </summary>
        /// <param name="ticksBeyondCurrent">The number of epoch ticks until the next suspension.</param>
        /// <remarks>
        /// <para>
        /// This time-slices asynchronous component calls (<c>ComponentFunction.CallAsync</c> and
        /// <c>ComponentLinker.InstantiateAsync</c>): each suspension returns control to the .NET
        /// caller, which resumes the call on the next poll. Unlike
        /// <see cref="SetEpochDeadlineCallback"/>, no managed code runs on the WebAssembly
        /// stack, so it is safe with Wasmtime's fiber stacks.
        /// </para>
        /// <para>
        /// Requires an engine configured with epoch interruption and <see cref="Config.WithComponentModelAsync"/>. It
        /// replaces any epoch deadline callback. Synchronous calls that reach the deadline trap.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">The engine was not configured with <see cref="Config.WithComponentModelAsync"/>.</exception>
        public void SetEpochDeadlineAsyncYieldAndUpdate(ulong ticksBeyondCurrent)
        {
            ThrowIfNotAsync();
            Context.SetEpochDeadlineAsyncYieldAndUpdate(ticksBeyondCurrent);
            epochAsyncYields = true;
            System.GC.KeepAlive(this);
        }

        /// <summary>
        /// Configures asynchronous calls to suspend each time the given amount of fuel has been
        /// consumed, so long-running WebAssembly periodically returns control to the .NET caller.
        /// </summary>
        /// <param name="interval">The amount of fuel consumed between suspensions, or 0 to disable.</param>
        /// <remarks>
        /// Requires an engine configured with fuel consumption and <see cref="Config.WithComponentModelAsync"/>. No
        /// managed code runs on the WebAssembly stack. Every suspension costs a round trip
        /// through the .NET scheduler, so prefer intervals of at least a few hundred thousand.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The engine was not configured with <see cref="Config.WithComponentModelAsync"/>.</exception>
        /// <exception cref="WasmtimeException">The engine does not consume fuel.</exception>
        public void SetFuelAsyncYieldInterval(ulong interval)
        {
            ThrowIfNotAsync();
            Context.SetFuelAsyncYieldInterval(interval);
            fuelAsyncYields = interval != 0;
            System.GC.KeepAlive(this);
        }

        /// <summary>
        /// True when asynchronous calls may suspend at epoch or fuel yield points, so a poll that
        /// makes no progress can be resumed immediately.
        /// </summary>
        internal bool AsyncYieldsEnabled => epochAsyncYields || fuelAsyncYields;

        private void ThrowIfNotAsync()
        {
            if (!IsComponentModelAsyncEnabled)
            {
                throw new InvalidOperationException(
                    "Yielding requires an engine configured with Config.WithComponentModelAsync(true).");
            }
        }

        /// <summary>
        /// Retrieves the data stored in the Store context
        /// </summary>
        public object? GetData() => data;

        /// <summary>
        /// Replaces the data stored in the Store context 
        /// </summary>
        public void SetData(object? data) => this.data = data;

        /// <summary>
        /// Represents a callback invoked when the epoch deadline of a store is reached.
        /// </summary>
        /// <param name="store">The store whose epoch deadline was reached.</param>
        /// <returns>The new deadline, in ticks beyond the current epoch, after which execution resumes.</returns>
        /// <remarks>
        /// <para>
        /// The callback normally runs on the thread executing the WebAssembly code. Throwing from it terminates
        /// the execution; the exception becomes the InnerException of the resulting <see cref="WasmtimeException"/>.
        /// </para>
        /// <para>
        /// When the engine enables asynchronous component support and
        /// <see cref="Components.HostCallbackIsolation.IsSupported"/> is true, the callback instead runs on an
        /// isolated host callback thread while the WebAssembly stack waits in native code, because that stack
        /// may be a Wasmtime fiber the .NET runtime cannot safely scan. The store passed to the callback then
        /// only supports <see cref="Fuel"/>, <see cref="GC()"/> and <see cref="SetEpochDeadline"/>; return the
        /// new deadline rather than touching other store state. Without isolation, use
        /// <see cref="SetEpochDeadlineAsyncYieldAndUpdate"/> for asynchronous calls instead.
        /// </para>
        /// </remarks>
        public delegate ulong EpochDeadlineCallback(Store store);

        /// <summary>
        /// Sets the callback invoked when WebAssembly code running in this store reaches its epoch
        /// deadline, instead of trapping. Replaces any previously set callback.
        /// </summary>
        /// <param name="callback">The callback to invoke.</param>
        /// <remarks>
        /// <para>
        /// For this to work epoch interruption must be enabled via <see cref="Config.WithEpochInterruption(bool)"/>.
        /// </para>
        /// <para>
        /// The callback is kept alive until it is replaced or the store is disposed, so a callback that
        /// captures this store keeps the store alive as well; prefer the store passed to the callback.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown if callback is null</exception>
        public void SetEpochDeadlineCallback(EpochDeadlineCallback callback)
        {
            if (callback is null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            if (IsComponentModelAsyncEnabled && Components.HostCallbackDispatcher.IsSupported &&
                Components.HostCallbackDispatcher.SetEpochDeadlineCallback(NativeHandle, callback))
            {
                epochAsyncYields = false;
                return;
            }

            unsafe
            {
                Native.WasmtimeEpochDeadlineCallback trampoline =
                    (context, data, epochDeadlineDelta, updateKind) =>
                        InvokeEpochDeadlineCallback(callback, context, epochDeadlineDelta);

                // The GCHandle passed as callback data keeps the trampoline alive; Wasmtime runs the
                // finalizer when the callback is replaced or the store is deleted.
                Native.wasmtime_store_epoch_deadline_callback(
                    NativeHandle,
                    trampoline,
                    GCHandle.ToIntPtr(GCHandle.Alloc(trampoline)),
                    Finalizer
                );
            }

            epochAsyncYields = false;
        }

        internal static unsafe IntPtr InvokeEpochDeadlineCallback(EpochDeadlineCallback callback, IntPtr context, ulong* epochDeadlineDelta)
        {
            try
            {
                // The update kind is left at "continue". Async calls that should yield use
                // SetEpochDeadlineAsyncYieldAndUpdate, which runs no managed code on the fiber.
                *epochDeadlineDelta = callback(new StoreContext(context).Store);
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                return CreateEpochDeadlineError(ex);
            }
        }

        private static IntPtr CreateEpochDeadlineError(Exception ex)
        {
            try
            {
                // Store the exception as error cause, so that it becomes the WasmtimeException's
                // InnerException when the error bubbles up. See Function.HandleCallbackException.
                Function.CallbackErrorCause = ex is WasmtimeException wasmtimeException ? wasmtimeException.InnerException : ex;

                return Native.wasmtime_error_new(ex.Message);
            }
            catch (Exception separateException)
            {
                // We never must let .NET exceptions bubble through the native-to-managed transition;
                // see Function.HandleCallbackException.
                Environment.FailFast(separateException.Message, separateException);

                // Satisfy the control-flow analyzer; this line is never reached.
                throw;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            var state = System.Threading.Interlocked.CompareExchange(ref componentOperationState, 2, 0);
            if (state == 1)
            {
                throw new InvalidOperationException(
                    "A store cannot be disposed while a component operation is in progress.");
            }

            if (state == 2)
            {
                return;
            }

            Components.HostCallbackDispatcher.ReleaseContext(contextHandle);
            handle.Dispose();
        }

        internal Handle NativeHandle
        {
            get
            {
                if (handle.IsInvalid || handle.IsClosed)
                {
                    throw new ObjectDisposedException(typeof(Store).FullName);
                }

                Components.HostCallbackDispatcher.ThrowIfServing(contextHandle);
                return handle;
            }
        }

        internal bool IsComponentModelAsyncEnabled { get; }

        private bool epochAsyncYields;
        private bool fuelAsyncYields;

        internal void BeginComponentOperation()
        {
            var state = System.Threading.Interlocked.CompareExchange(ref componentOperationState, 1, 0);
            if (state == 2)
            {
                throw new ObjectDisposedException(typeof(Store).FullName);
            }

            if (state != 0)
            {
                throw new InvalidOperationException(
                    "A component operation is already in progress on this store.");
            }

            Components.HostCallbackDispatcher.BeginOperation(contextHandle);
        }

        internal void EndComponentOperation()
        {
            System.Threading.Volatile.Write(ref componentOperationState, 0);
        }

        /// <summary>Gets the native context handle without dispatcher access checks.</summary>
        internal IntPtr ContextHandleUnchecked => contextHandle;

        /// <summary>
        /// Gets the context of the store.
        /// </summary>
        /// <remarks>
        /// Note: Generally, you must keep the <see cref="Store"/> alive (by using
        /// <see cref="GC.KeepAlive(object)"/>) until the <see cref="StoreContext"/> is no longer
        /// used, to prevent the the <see cref="Handle"/> finalizer from prematurely deleting the
        /// store handle in the GC finalizer thread while the <see cref="StoreContext"/> is still
        /// in use.
        /// </remarks>
        internal StoreContext Context
        {
            get
            {
                if (handle.IsClosed)
                {
                    throw new ObjectDisposedException(typeof(Store).FullName);
                }

                Components.HostCallbackDispatcher.ThrowIfServing(contextHandle);
                return new StoreContext(contextHandle);
            }
        }

        internal class Handle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public Handle(IntPtr handle)
                : base(true)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle()
            {
                Native.wasmtime_store_delete(handle);
                return true;
            }
        }

        private static class Native
        {
            public delegate void Finalizer(IntPtr data);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_store_new(Engine.Handle engine, IntPtr data, Finalizer? finalizer);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_store_context(Handle store);

            [DllImport(Engine.LibraryName)]
            public static extern void wasmtime_store_delete(IntPtr store);

            [DllImport(Engine.LibraryName)]
            public static extern void wasmtime_store_limiter(Handle store, long memory_size, long table_elements, long instances, long tables, long memories);

            public unsafe delegate IntPtr WasmtimeEpochDeadlineCallback(IntPtr context, IntPtr data, ulong* epochDeadlineDelta, byte* updateKind);

            [DllImport(Engine.LibraryName)]
            public static extern void wasmtime_store_epoch_deadline_callback(Handle store, WasmtimeEpochDeadlineCallback callback, IntPtr data, Finalizer? finalizer);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_error_new([MarshalAs(Extensions.LPUTF8Str)] string message);
        }

        private readonly IntPtr contextHandle;
        private readonly Handle handle;

        private object? data;
        private int componentOperationState;

        private static readonly Native.Finalizer Finalizer = (p) => GCHandle.FromIntPtr(p).Free();
        
        // The caches below use the external struct type as key type. These structs contain
        // __private fields, but these are not interpreted and merely used for value comparison.
        private readonly ConcurrentDictionary<ExternFunc, Function> _externFunctionCache = new();
        private readonly ConcurrentDictionary<ExternMemory, Memory> _externMemoryCache = new();
        private readonly ConcurrentDictionary<ExternGlobal, Global> _externGlobalCache = new();

        internal Function GetCachedExtern(ExternFunc @extern)
        {
            if (!_externFunctionCache.TryGetValue(@extern, out var func))
            {
                func = new Function(this, @extern);
                func = _externFunctionCache.GetOrAdd(@extern, func);
            }

            return func;
        }

        internal Memory GetCachedExtern(ExternMemory @extern)
        {
            if (!_externMemoryCache.TryGetValue(@extern, out var mem))
            {
                mem = new Memory(this, @extern);
                mem = _externMemoryCache.GetOrAdd(@extern, mem);
            }

            return mem;
        }

        internal Global GetCachedExtern(ExternGlobal @extern)
        {
            if (!_externGlobalCache.TryGetValue(@extern, out var global))
            {
                global = new Global(this, @extern);
                global = _externGlobalCache.GetOrAdd(@extern, global);
            }

            return global;
        }
    }
}
