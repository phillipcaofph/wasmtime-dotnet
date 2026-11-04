using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
    private ResourcePlan? resourcePlan;

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
    /// <para>
    /// A trap leaves the whole <see cref="Store"/> unusable for further component calls, which
    /// then fail with "cannot enter component instance". This applies to every instance in the
    /// store, not just this one, so a store cannot be reused after a trap.
    /// </para>
    /// <para>
    /// Resource arguments are validated before the call starts, so a mismatch throws without touching the
    /// Store. Owned resources passed to the function belong to the guest once the call has started, even if it
    /// then fails.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Another thread is running a component operation on the Store, or
    /// <see cref="Store.ReleaseComponentResources"/> was called. A host callback may call exports of its own Store.
    /// </exception>
    public ComponentValue? Call(IReadOnlyList<ComponentValue> arguments)
    {
        if (arguments is null)
        {
            throw new ArgumentNullException(nameof(arguments));
        }

        store.BeginComponentOperation(nested: true);
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

        // Read the arguments once, so validation and marshalling see the same values.
        arguments = Snapshot(arguments, argumentCount);
        var resultCount = HasResult ? 1 : 0;
        var context = store.Context.handle;
        var plan = GetResourcePlan(context);
        var transaction = BeginResourceTransaction(plan, arguments);

        using (var scope = new ComponentValueMarshaller.AllocationScope())
        {
            var issued = false;
            try
            {
                var argumentBuffer = IntPtr.Zero;
                if (argumentCount > 0)
                {
                    argumentBuffer = scope.Allocate(argumentCount * ComponentValueMarshaller.ValueSize);
                    WriteArguments(arguments, argumentBuffer, scope, plan, transaction);
                }

                // Wasmtime allocates the contents of the results, so the buffer must start zeroed and
                // must only be deleted if the call actually wrote to it.
                var resultBuffer = resultCount == 0
                    ? IntPtr.Zero
                    : scope.Allocate(resultCount * ComponentValueMarshaller.ValueSize);

                transaction?.Commit(context, scope);

                issued = true;
                var error = Native.wasmtime_component_func_call(
                    in func,
                    context,
                    argumentBuffer,
                    (nuint)argumentCount,
                    resultBuffer,
                    (nuint)resultCount);

                GC.KeepAlive(store);

                // Ownership moved when the call was issued, whether or not it succeeded.
                transaction?.AfterCall(context, issued: true);
                transaction = null;

                if (error != IntPtr.Zero)
                {
                    store.MarkComponentPoisoned();
                    throw WasmtimeException.FromOwnedError(error);
                }

                if (resultCount == 0)
                {
                    return null;
                }

                try
                {
                    return ReadResult(resultBuffer, ResultReader(plan, context));
                }
                finally
                {
                    ComponentValueNative.wasmtime_component_val_delete(resultBuffer);
                }
            }
            finally
            {
                transaction?.AfterCall(context, issued);
            }
        }
    }

    private static ComponentValue[] Snapshot(IReadOnlyList<ComponentValue> arguments, int count)
    {
        if (count == 0)
        {
            return Array.Empty<ComponentValue>();
        }

        var snapshot = new ComponentValue[count];
        for (var i = 0; i < count; i++)
        {
            snapshot[i] = arguments[i] ?? throw new ArgumentException($"Argument {i} is null.", nameof(arguments));
        }

        return snapshot;
    }

    /// <summary>
    /// Builds, once, where resources occur in this function's type. Runs inside a component operation, because
    /// the type is read with the Store's context.
    /// </summary>
    private ResourcePlan GetResourcePlan(IntPtr context)
    {
        if (resourcePlan is { } plan)
        {
            return plan;
        }

        var type = Native.wasmtime_component_func_type(in func, context);
        try
        {
            plan = ResourcePlan.Build(type, store.ComponentHostResourceTypes);
        }
        finally
        {
            Native.wasmtime_component_func_type_delete(type);
        }

        resourcePlan = plan;
        return plan;
    }

    /// <summary>
    /// Validates the arguments' resources before anything native happens, and returns the transaction
    /// that writes them, or null when the function has no resources.
    /// </summary>
    private ResourceCallTransaction? BeginResourceTransaction(ResourcePlan plan, IReadOnlyList<ComponentValue> arguments)
    {
        if (!plan.Any)
        {
            // A resource anywhere in these arguments is a mistake; report it with its path.
            for (var i = 0; i < arguments.Count; i++)
            {
                ResourceCallTransaction.RejectResources(arguments[i], plan.ParameterNames[i]);
            }

            return null;
        }

        // Wasmtime would also reject a mismatch elsewhere in the arguments, but only after the resources were
        // committed, leaving owned ones moved into a guest that never received them. Check everything first.
        for (var i = 0; i < arguments.Count; i++)
        {
            plan.ParameterShapes[i]?.Validate(arguments[i], plan.ParameterNames[i]);
        }

        var transaction = new ResourceCallTransaction(store);
        transaction.Validate(arguments, plan);
        return transaction;
    }

    private static void WriteArguments(
        IReadOnlyList<ComponentValue> arguments,
        IntPtr buffer,
        ComponentValueMarshaller.AllocationScope scope,
        ResourcePlan plan,
        ResourceCallTransaction? transaction)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            ComponentValueMarshaller.Write(
                arguments[i],
                buffer + (i * ComponentValueMarshaller.ValueSize),
                scope,
                transaction is null ? null : plan.Parameters[i],
                transaction);
        }
    }

    private ExportResultReader? ResultReader(ResourcePlan plan, IntPtr context) =>
        plan.Result is null ? null : new ExportResultReader(store, context);

    /// <summary>
    /// Reads the result; on failure the native handles taken from it so far are disposed (their drops are
    /// deferred, because the call's component operation is still in progress).
    /// </summary>
    private static ComponentValue ReadResult(IntPtr buffer, ExportResultReader? reader)
    {
        try
        {
            return ComponentValueMarshaller.Read(buffer, reader);
        }
        catch
        {
            reader?.Abort();
            throw;
        }
    }

    private static bool ReadHasResult(IntPtr type)
    {
        Span<byte> buffer = stackalloc byte[ValueTypeSize];
        buffer.Clear();

        unsafe
        {
            fixed (void* bufferPtr = buffer)
            {
                if (!Native.wasmtime_component_func_type_result(type, (IntPtr)bufferPtr))
                {
                    return false;
                }

                Native.wasmtime_component_valtype_delete((IntPtr)bufferPtr);
                return true;
            }
        }
    }

    internal static class Native
    {
        /// <summary>
        /// Mirrors <c>wasmtime_component_func_t</c>. The first two fields sit in an anonymous
        /// struct, so the next field lands at offset 16, the trailing pointer at offset 24 and the
        /// whole thing is 32 bytes.
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