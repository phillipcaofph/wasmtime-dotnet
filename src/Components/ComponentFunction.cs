using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Wasmtime.Components;

/// <summary>
/// An exported function of a <see cref="ComponentInstance"/>.
/// </summary>
public class ComponentFunction
{
    /// <summary>Size of <c>wasmtime_component_valtype_t</c>: a 1-byte kind padded to 8, plus an 8-byte union.</summary>
    private const int ValueTypeSize = 16;

    private readonly Store store;
    private readonly Native.Func func;

    internal ComponentFunction(Store store, Native.Func func)
    {
        this.store = store;
        this.func = func;

        var type = Native.wasmtime_component_func_type(in func, store.Context.handle);
        GC.KeepAlive(store);

        try
        {
            ParameterCount = (int)Native.wasmtime_component_func_type_param_count(type);
            HasResult = ReadHasResult(type);
        }
        finally
        {
            Native.wasmtime_component_func_type_delete(type);
        }
    }

    /// <summary>
    /// Gets the number of parameters this function takes.
    /// </summary>
    public int ParameterCount { get; }

    /// <summary>
    /// Gets whether this function returns a value.
    /// </summary>
    /// <remarks>
    /// A component function has at most one result. Note that a WIT function declared to return
    /// <c>result&lt;_, E&gt;</c> still has one result here, even though bindings generators
    /// usually surface it as returning nothing.
    /// </remarks>
    public bool HasResult { get; }

    /// <summary>
    /// Invokes the function.
    /// </summary>
    /// <param name="arguments">The arguments, which must match <see cref="ParameterCount"/>.</param>
    /// <returns>The result, or null if the function does not return one.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="arguments"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the wrong number of arguments is given.</exception>
    /// <exception cref="WasmtimeException">Thrown if the function traps or fails.</exception>
    public ComponentValue? Call(params ComponentValue[] arguments)
    {
        return Call((IReadOnlyList<ComponentValue>)arguments);
    }

    /// <summary>
    /// Invokes the function.
    /// </summary>
    /// <param name="arguments">The arguments, which must match <see cref="ParameterCount"/>.</param>
    /// <returns>The result, or null if the function does not return one.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="arguments"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the wrong number of arguments is given.</exception>
    /// <exception cref="WasmtimeException">Thrown if the function traps or fails.</exception>
    /// <remarks>
    /// A trap leaves the whole <see cref="Store"/> unusable for further component calls, which
    /// then fail with "cannot enter component instance". This applies to every instance in the
    /// store, not just this one, so a store cannot be reused after a trap.
    /// </remarks>
    public ComponentValue? Call(IReadOnlyList<ComponentValue> arguments)
    {
        if (arguments is null)
        {
            throw new ArgumentNullException(nameof(arguments));
        }

        store.BeginComponentOperation();
        try
        {
            return CallCore(arguments);
        }
        finally
        {
            store.EndComponentOperation();
        }
    }

    private ComponentValue? CallCore(IReadOnlyList<ComponentValue> arguments)
    {
        var argumentCount = arguments.Count;
        if (argumentCount != ParameterCount)
        {
            throw new ArgumentException(
                $"The function takes {ParameterCount} argument(s) but {argumentCount} were given.",
                nameof(arguments));
        }

        var resultCount = HasResult ? 1 : 0;

        using (var scope = new ComponentValueMarshaller.AllocationScope())
        {
            var argumentBuffer = IntPtr.Zero;
            if (argumentCount > 0)
            {
                argumentBuffer = scope.Allocate(argumentCount * ComponentValueMarshaller.ValueSize);
                for (var i = 0; i < argumentCount; i++)
                {
                    if (arguments[i] is null)
                    {
                        throw new ArgumentException($"Argument {i} is null.", nameof(arguments));
                    }

                    ComponentValueMarshaller.Write(
                        arguments[i],
                        argumentBuffer + (i * ComponentValueMarshaller.ValueSize),
                        scope);
                }
            }

            // Wasmtime allocates the contents of the results, so the buffer must start zeroed and
            // must only be deleted if the call actually wrote to it.
            var resultBuffer = resultCount == 0
                ? IntPtr.Zero
                : scope.Allocate(resultCount * ComponentValueMarshaller.ValueSize);

            var context = store.Context.handle;
            var error = Native.wasmtime_component_func_call(
                in func,
                context,
                argumentBuffer,
                (nuint)argumentCount,
                resultBuffer,
                (nuint)resultCount);

            GC.KeepAlive(store);

            if (error != IntPtr.Zero)
            {
                throw HostCallbackDispatcher.AttachCause(WasmtimeException.FromOwnedError(error), context);
            }

            if (resultCount == 0)
            {
                return null;
            }

            try
            {
                return ComponentValueMarshaller.Read(resultBuffer);
            }
            finally
            {
                ComponentValueNative.wasmtime_component_val_delete(resultBuffer);
            }
        }
    }

    /// <summary>
    /// Invokes this function using Wasmtime's asynchronous component API.
    /// </summary>
    /// <param name="arguments">The arguments, which must match <see cref="ParameterCount"/>.</param>
    /// <returns>The result, or null if the function does not return one.</returns>
    /// <exception cref="InvalidOperationException">The engine was not configured for async components.</exception>
    /// <exception cref="ArgumentException">The wrong number of arguments was given.</exception>
    /// <exception cref="WasmtimeException">The function traps or fails.</exception>
    /// <remarks>
    /// The engine must be configured with <see cref="Config.WithComponentModelAsync(bool)"/>.
    /// Do not use this store for any other operation until the returned task completes.
    /// Cancellation disposes the native call future; it does not roll back guest side effects.
    /// The guest runs on a Wasmtime fiber stack. Host functions are isolated from it by default;
    /// see <see cref="ComponentLinker.IsolateHostCallbacks"/>.
    /// </remarks>
    public Task<ComponentValue?> CallAsync(params ComponentValue[] arguments) =>
        CallAsync((IReadOnlyList<ComponentValue>)arguments, CancellationToken.None);

    /// <summary>
    /// Invokes this function using Wasmtime's asynchronous component API.
    /// </summary>
    /// <param name="arguments">The arguments, which must match <see cref="ParameterCount"/>.</param>
    /// <param name="cancellationToken">A token that cancels polling and disposes the native call future.</param>
    /// <returns>The result, or null if the function does not return one.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="arguments"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the wrong number of arguments is given.</exception>
    /// <exception cref="OperationCanceledException">The call was canceled while polling.</exception>
    /// <exception cref="WasmtimeException">The function traps or fails.</exception>
    /// <remarks>
    /// Do not use this store for any other operation until the returned task completes.
    /// The guest runs on a Wasmtime fiber stack. Host functions are isolated from it by default;
    /// see <see cref="ComponentLinker.IsolateHostCallbacks"/>.
    /// </remarks>
    public async Task<ComponentValue?> CallAsync(
        IReadOnlyList<ComponentValue> arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments is null)
        {
            throw new ArgumentNullException(nameof(arguments));
        }

        if (!store.IsComponentModelAsyncEnabled)
        {
            throw new InvalidOperationException(
                "Asynchronous component calls require an engine configured with WithComponentModelAsync(true).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        store.BeginComponentOperation();
        try
        {
            var argumentCount = arguments.Count;
            if (argumentCount != ParameterCount)
            {
                throw new ArgumentException(
                    $"The function takes {ParameterCount} argument(s) but {argumentCount} were given.",
                    nameof(arguments));
            }

            using var scope = new ComponentValueMarshaller.AllocationScope();
            // The native future borrows this struct until deletion, beyond the P/Invoke's pin.
            var functionBuffer = scope.Allocate(Marshal.SizeOf<Native.Func>());
            Marshal.StructureToPtr(func, functionBuffer, false);
            var argumentBuffer = argumentCount == 0
                ? IntPtr.Zero
                : scope.Allocate(checked(argumentCount * ComponentValueMarshaller.ValueSize));
            for (var i = 0; i < argumentCount; i++)
            {
                if (arguments[i] is null)
                {
                    throw new ArgumentException($"Argument {i} is null.", nameof(arguments));
                }

                ComponentValueMarshaller.Write(
                    arguments[i],
                    argumentBuffer + (i * ComponentValueMarshaller.ValueSize),
                    scope);
            }

            var resultCount = HasResult ? 1 : 0;
            var resultBuffer = resultCount == 0
                ? IntPtr.Zero
                : scope.Allocate(ComponentValueMarshaller.ValueSize);
            var errorBuffer = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(errorBuffer, IntPtr.Zero);

            IntPtr future;
            var context = store.Context.handle;
            try
            {
                future = Native.wasmtime_component_func_call_async(
                    functionBuffer,
                    context,
                    argumentBuffer,
                    (nuint)argumentCount,
                    resultBuffer,
                    (nuint)resultCount,
                    errorBuffer);
            }
            catch
            {
                var error = Marshal.ReadIntPtr(errorBuffer);
                if (error != IntPtr.Zero)
                {
                    Native.wasmtime_error_delete(error);
                }
                Marshal.FreeHGlobal(errorBuffer);
                throw;
            }

            if (future == IntPtr.Zero)
            {
                var error = Marshal.ReadIntPtr(errorBuffer);
                Marshal.FreeHGlobal(errorBuffer);
                if (error != IntPtr.Zero)
                {
                    throw WasmtimeException.FromOwnedError(error);
                }

                throw new InvalidOperationException("Wasmtime failed to create an asynchronous component call.");
            }

            try
            {
                await PollFutureAsync(future, store, context, cancellationToken).ConfigureAwait(false);
                Native.wasmtime_call_future_delete(future);
                future = IntPtr.Zero;
                var error = Marshal.ReadIntPtr(errorBuffer);
                if (error != IntPtr.Zero)
                {
                    Marshal.WriteIntPtr(errorBuffer, IntPtr.Zero);
                    throw HostCallbackDispatcher.AttachCause(WasmtimeException.FromOwnedError(error), context);
                }

                if (resultCount == 0)
                {
                    HostCallbackDispatcher.ThrowIfHostFailed(context);
                    return null;
                }

                ComponentValue result;
                try
                {
                    result = ComponentValueMarshaller.Read(resultBuffer);
                }
                finally
                {
                    ComponentValueNative.wasmtime_component_val_delete(resultBuffer);
                }
                HostCallbackDispatcher.ThrowIfHostFailed(context);
                return result;
            }
            finally
            {
                if (future != IntPtr.Zero)
                {
                    Native.wasmtime_call_future_delete(future);
                }
                var error = Marshal.ReadIntPtr(errorBuffer);
                if (error != IntPtr.Zero)
                {
                    Native.wasmtime_error_delete(error);
                }
                Marshal.FreeHGlobal(errorBuffer);
                GC.KeepAlive(this);
                GC.KeepAlive(store);
            }
        }
        finally
        {
            store.EndComponentOperation();
        }
    }

    internal static async Task PollFutureAsync(IntPtr future, Store store, IntPtr context, CancellationToken cancellationToken)
    {
        using var owner = HostCallbackDispatcher.BeginPolling(context);
        while (true)
        {
            // Wasmtime does not borrow the Store between polls, so callbacks' Store operations run here.
            owner?.Serve();
            if (Native.wasmtime_call_future_poll(future))
            {
                return;
            }

            if (store.AsyncYieldsEnabled && !HostCallbackDispatcher.HasPendingWork(context))
            {
                // The guest suspended at an epoch or fuel yield point and can resume immediately;
                // hop through the thread pool so other work gets a turn first.
                cancellationToken.ThrowIfCancellationRequested();
                await default(ThreadPoolHop);
                continue;
            }

            await HostCallbackDispatcher.WaitForProgressAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly struct ThreadPoolHop : ICriticalNotifyCompletion
    {
        public ThreadPoolHop GetAwaiter() => this;

        public bool IsCompleted => false;

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation) =>
            ThreadPool.QueueUserWorkItem(static state => ((Action)state!)(), continuation);

        public void UnsafeOnCompleted(Action continuation) =>
            ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!)(), continuation);
    }

    private static bool ReadHasResult(IntPtr type)
    {
        var buffer = Marshal.AllocHGlobal(ValueTypeSize);
        try
        {
            unsafe
            {
                new Span<byte>((void*)buffer, ValueTypeSize).Clear();
            }

            if (!Native.wasmtime_component_func_type_result(type, buffer))
            {
                return false;
            }

            Native.wasmtime_component_valtype_delete(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static class Native
    {
        /// <summary>
        /// Mirrors <c>wasmtime_component_func_t</c>. The first two fields sit in an anonymous
        /// struct, so the trailing fields land at offsets 16 and 24 and the whole thing is 32 bytes.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        internal struct Func
        {
            [FieldOffset(0)]
            public ulong StoreId;

            [FieldOffset(8)]
            public uint Private1;

            [FieldOffset(16)]
            public uint Private2;

            [FieldOffset(24)]
            public IntPtr Private3;
        }

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_func_call(
            in Func func,
            IntPtr context,
            IntPtr args,
            nuint args_size,
            IntPtr results,
            nuint results_size);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_func_call_async(
            IntPtr func,
            IntPtr context,
            IntPtr args,
            nuint args_size,
            IntPtr results,
            nuint results_size,
            IntPtr error_ret);

        [DllImport(Engine.LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool wasmtime_call_future_poll(IntPtr future);

        [DllImport(Engine.LibraryName)]
        public static extern void wasmtime_call_future_delete(IntPtr future);

        [DllImport(Engine.LibraryName)]
        public static extern void wasmtime_error_delete(IntPtr error);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_func_type(in Func func, IntPtr context);

        [DllImport(Engine.LibraryName)]
        public static extern void wasmtime_component_func_type_delete(IntPtr ty);

        [DllImport(Engine.LibraryName)]
        public static extern nuint wasmtime_component_func_type_param_count(IntPtr ty);

        [DllImport(Engine.LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool wasmtime_component_func_type_result(IntPtr ty, IntPtr type_ret);

        [DllImport(Engine.LibraryName)]
        public static extern void wasmtime_component_valtype_delete(IntPtr ptr);
    }
}