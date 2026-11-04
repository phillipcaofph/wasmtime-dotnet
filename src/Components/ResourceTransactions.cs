using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Wasmtime.Components;

/// <summary>Turns a native <c>wasmtime_component_resource_any_t*</c> into a resource cell while reading values.</summary>
internal interface IResourceReader
{
    /// <summary>Called for a resource value at <paramref name="source"/> whose payload is <paramref name="any"/>.</summary>
    ResourceCell ReadResource(IntPtr source, IntPtr any);
}

internal static class ResourceMarshalling
{
    /// <summary>
    /// Finds the binding host type of a native resource, or null for a native type. Pure: it only
    /// reads the resource's type and compares it with <c>wasmtime_component_resource_type_equal</c>.
    /// <paramref name="lastMatch"/> is tried first, since neighbouring values (a list) usually share a type.
    /// </summary>
    public static ComponentHostResourceType? Classify(Store store, IntPtr any, ref ComponentHostResourceType? lastMatch)
    {
        var hostTypes = store.ComponentHostResourceTypes;
        if (hostTypes.Count == 0)
        {
            return null;
        }

        var type = ComponentResourceNative.wasmtime_component_resource_any_type(any);
        try
        {
            if (lastMatch is { } last &&
                ComponentResourceNative.wasmtime_component_resource_type_equal_raw(last.NativeHandle, type))
            {
                return last;
            }

            foreach (var candidate in hostTypes)
            {
                if (!ReferenceEquals(candidate, lastMatch) &&
                    ComponentResourceNative.wasmtime_component_resource_type_equal_raw(candidate.NativeHandle, type))
                {
                    lastMatch = candidate;
                    return candidate;
                }
            }

            return null;
        }
        finally
        {
            ComponentResourceNative.wasmtime_component_resource_type_delete(type);
        }
    }

    /// <summary>Writes the <c>bool(false)</c> placeholder a fix-up later replaces with a resource.</summary>
    public static void WritePlaceholder(IntPtr destination)
    {
        Marshal.WriteByte(destination, (byte)ComponentValueKind.Bool);
        Marshal.WriteInt64(destination + ComponentValueMarshaller.ValuePayloadOffset, 0);
    }

    /// <summary>Writes a resource value; <paramref name="any"/> must never be null.</summary>
    public static void WriteResource(IntPtr destination, IntPtr any)
    {
        System.Diagnostics.Debug.Assert(any != IntPtr.Zero, "A resource value must never carry a null pointer.");
        Marshal.WriteIntPtr(destination + ComponentValueMarshaller.ValuePayloadOffset, any);
        Marshal.WriteByte(destination, (byte)ComponentValueKind.Resource);
    }

    public static WasmtimeException ToException(IntPtr error) => WasmtimeException.FromOwnedError(error);

    public sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>
/// The resource side of one export call (<see cref="ComponentFunction.Call(ComponentValue[])"/>):
/// validation before any native call, placeholder writes, the commit that creates the native
/// handles, and the ownership changes once the call has been issued.
/// </summary>
/// <remarks>
/// Order: <see cref="Validate"/> (managed checks plus pure type comparisons) → writes through
/// <see cref="WriteResource"/> (no context) → <see cref="Commit"/> (<c>host_to_any</c> on the
/// caller's thread) → native call → <see cref="AfterCall"/>. A failure before the commit is undone
/// with <see cref="Abort"/> and leaves the Store untouched.
/// </remarks>
internal sealed class ResourceCallTransaction
{
    private readonly Store store;
    private readonly List<Fixup> fixups = new();
    private readonly List<NativeUse> natives = new();
    private HostResourceTable? table;
    private bool committed;
    private bool finished;

    public ResourceCallTransaction(Store store) => this.store = store;

    private struct Fixup
    {
        public IntPtr Slot;
        public IntPtr Host;
        public IntPtr Any;
        public uint Rep;
        public bool Own;

        /// <summary>An own callback argument moved by this call, which its callback must no longer release.</summary>
        public HostResourceCell? Moved;

        /// <summary>
        /// Whether the entry <c>host_to_any</c> creates for a moved object must be removed after the call:
        /// only when the call was never issued. A lent object is a <c>borrow</c> handle with no table entry.
        /// </summary>
        public bool RemoveAfterCall(bool issued) => Any != IntPtr.Zero && Own && !issued;
    }

    /// <summary>A native handle whose box this call borrows as an argument.</summary>
    private readonly struct NativeUse
    {
        public NativeUse(ComponentResource handle, bool own)
        {
            Handle = handle;
            Own = own;
        }

        public ComponentResource Handle { get; }

        public bool Own { get; }
    }

    private HostResourceTable Table => table ??= store.ComponentResourceTable;

    /// <summary>
    /// Checks every resource position of the arguments at every nesting level. Throws before any
    /// native call, so the Store stays usable.
    /// </summary>
    /// <exception cref="ArgumentException">A value does not fit its resource position.</exception>
    /// <exception cref="ObjectDisposedException">An expired callback-argument view was passed.</exception>
    /// <exception cref="InvalidOperationException">The same object appears in conflicting positions.</exception>
    public void Validate(IReadOnlyList<ComponentValue> arguments, ResourcePlan plan)
    {
        var state = new ValidationState(store);
        for (var i = 0; i < arguments.Count && i < plan.Parameters.Length; i++)
        {
            if (arguments[i] is not { } argument)
            {
                continue;
            }

            if (plan.Parameters[i] is { } node)
            {
                ValidateNode(argument, node, plan.ParameterNames[i], state);
            }
            else
            {
                RejectResources(argument, plan.ParameterNames[i]);
            }
        }
    }

    /// <summary>
    /// Throws when <paramref name="value"/> carries a resource anywhere, for a position whose type has none.
    /// </summary>
    /// <exception cref="ArgumentException">A resource occurs in <paramref name="value"/>; the message names its path.</exception>
    public static void RejectResources(ComponentValue value, string path)
    {
        switch (value.Kind)
        {
            case ComponentValueKind.Resource:
                throw new ArgumentException($"{path}: the function does not take a resource here.", "arguments");

            case ComponentValueKind.List:
            case ComponentValueKind.Tuple:
                for (var i = 0; i < value.Items.Count; i++)
                {
                    RejectResources(value.Items[i], $"{path}[{i}]");
                }

                break;

            case ComponentValueKind.Record:
                foreach (var field in value.Fields)
                {
                    RejectResources(field.Value, $"{path}.{field.Key}");
                }

                break;

            case ComponentValueKind.Option:
                if (value.Flag)
                {
                    RejectResources(value.Payload!, $"{path}.some");
                }

                break;

            case ComponentValueKind.Result:
                if (value.Payload is { } armValue)
                {
                    RejectResources(armValue, $"{path}.{(value.Flag ? "ok" : "err")}");
                }

                break;

            case ComponentValueKind.Variant:
                if (value.Payload is { } caseValue)
                {
                    RejectResources(caseValue, $"{path}.{value.Text}");
                }

                break;
        }
    }

    private static void ValidateChild(ComponentValue value, ResourcePlanNode? node, string path, ValidationState state)
    {
        if (node is null)
        {
            RejectResources(value, path);
        }
        else
        {
            ValidateNode(value, node, path, state);
        }
    }

    private sealed class ValidationState
    {
        public ValidationState(Store store) => Store = store;

        public readonly Store Store;
        public readonly HashSet<object> Owned = new(ResourceMarshalling.ReferenceComparer.Instance);
        public readonly HashSet<object> Borrowed = new(ResourceMarshalling.ReferenceComparer.Instance);
    }

    private static void ValidateNode(ComponentValue value, ResourcePlanNode node, string path, ValidationState state)
    {
        switch (node.Kind)
        {
            case PlanKind.Own:
            case PlanKind.Borrow:
                ValidateHandle(value, node, path, state);
                break;

            case PlanKind.List:
                ExpectKind(value, ComponentValueKind.List, path);
                for (var i = 0; i < value.Items.Count; i++)
                {
                    ValidateNode(value.Items[i], node.Element!, $"{path}[{i}]", state);
                }

                break;

            case PlanKind.Option:
                ExpectKind(value, ComponentValueKind.Option, path);
                if (value.Flag)
                {
                    ValidateNode(value.Payload!, node.Some!, $"{path}.some", state);
                }

                break;

            case PlanKind.Result:
                ExpectKind(value, ComponentValueKind.Result, path);
                var arm = value.Flag ? node.Ok : node.Err;
                var armPath = $"{path}.{(value.Flag ? "ok" : "err")}";
                if (arm is not null)
                {
                    if (value.Payload is null)
                    {
                        throw new ArgumentException($"{armPath}: expected a value carrying a resource.", "arguments");
                    }

                    ValidateNode(value.Payload, arm, armPath, state);
                }
                else if (value.Payload is { } armValue)
                {
                    RejectResources(armValue, armPath);
                }

                break;

            case PlanKind.Tuple:
                ExpectKind(value, ComponentValueKind.Tuple, path);
                if (value.Items.Count != node.Items!.Length)
                {
                    throw new ArgumentException(
                        $"{path}: expected a tuple of {node.Items.Length} element(s), got {value.Items.Count}.", "arguments");
                }

                for (var i = 0; i < node.Items.Length; i++)
                {
                    ValidateChild(value.Items[i], node.Items[i], $"{path}[{i}]", state);
                }

                break;

            case PlanKind.Record:
                ExpectKind(value, ComponentValueKind.Record, path);
                ValidateRecordFields(value, node, path);
                foreach (var field in value.Fields)
                {
                    node.ByName!.TryGetValue(field.Key, out var fieldNode);
                    ValidateChild(field.Value, fieldNode, $"{path}.{field.Key}", state);
                }

                break;

            case PlanKind.Variant:
                ExpectKind(value, ComponentValueKind.Variant, path);
                if (Array.IndexOf(node.Names!, value.Text) < 0)
                {
                    throw new ArgumentException(
                        $"{path}: the variant has no case named '{value.Text}'.", "arguments");
                }

                if (node.ByName!.TryGetValue(value.Text!, out var caseNode) && caseNode is not null)
                {
                    if (value.Payload is null)
                    {
                        throw new ArgumentException($"{path}.{value.Text}: expected a value carrying a resource.", "arguments");
                    }

                    ValidateNode(value.Payload, caseNode, $"{path}.{value.Text}", state);
                }
                else if (value.Payload is { } caseValue)
                {
                    RejectResources(caseValue, $"{path}.{value.Text}");
                }

                break;
        }
    }

    /// <summary>
    /// Checks a record carries exactly the type's fields, in order, as Wasmtime's lowering requires. Wasmtime
    /// would otherwise fail inside the call, after the resources were committed, and poison the Store.
    /// </summary>
    private static void ValidateRecordFields(ComponentValue value, ResourcePlanNode node, string path)
    {
        var names = node.Names!;
        var fields = value.Fields;
        if (fields.Count != names.Length)
        {
            throw new ArgumentException(
                $"{path}: expected a record with {names.Length} field(s) ({string.Join(", ", names)}), got {fields.Count}.",
                "arguments");
        }

        for (var i = 0; i < names.Length; i++)
        {
            if (!string.Equals(fields[i].Key, names[i], StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{path}: expected field '{names[i]}' at position {i}, got '{fields[i].Key}'.", "arguments");
            }
        }
    }

    private static void ExpectKind(ComponentValue value, ComponentValueKind kind, string path)
    {
        if (value.Kind != kind)
        {
            throw new ArgumentException($"{path}: expected a {kind} value, got {value.Kind}.", "arguments");
        }
    }

    private static void ValidateHandle(ComponentValue value, ResourcePlanNode node, string path, ValidationState state)
    {
        if (value.Kind != ComponentValueKind.Resource || value.ResourceCell is not { } cell)
        {
            throw new ArgumentException($"{path}: expected {node.DescribeHandle()}, got {value.Kind}.", "arguments");
        }

        var own = node.Kind == PlanKind.Own;
        if (cell.Origin == ResourceCellOrigin.Value)
        {
            // Values built with Own/Borrow state their ownership; it must match the position.
            if (cell.Ownership == ComponentResourceOwnership.Own && !own)
            {
                throw new ArgumentException(
                    $"{path}: a value built with Own cannot be passed where the function expects {node.DescribeHandle()}.",
                    "arguments");
            }

            if (cell.Ownership == ComponentResourceOwnership.Borrow && own)
            {
                throw new ArgumentException(
                    $"{path}: a value built with Borrow cannot be passed where the function expects {node.DescribeHandle()}.",
                    "arguments");
            }
        }

        if (cell is NativeResourceCell native)
        {
            ValidateNative(native, node, path, state, own);
            return;
        }

        var host = (HostResourceCell)cell;
        if (node.Host is null || !ReferenceEquals(node.Host, host.HostType))
        {
            throw new ArgumentException(
                $"{path}: expected {node.DescribeHandle()}, got a resource of type {host.HostType.DisplayName}.",
                "arguments");
        }

        // Throws ObjectDisposedException for an expired callback-argument view.
        var target = host.Value;
        if (own)
        {
            if (state.Owned.Contains(target) || state.Borrowed.Contains(target))
            {
                throw new InvalidOperationException(
                    $"{path}: the same {host.HostType.DisplayName} object cannot be passed as own more than once, " +
                    "or as both own and borrow, in one call.");
            }

            state.Owned.Add(target);
        }
        else
        {
            if (state.Owned.Contains(target))
            {
                throw new InvalidOperationException(
                    $"{path}: the same {host.HostType.DisplayName} object cannot be passed as both own and borrow in one call.");
            }

            state.Borrowed.Add(target);
        }
    }

    /// <summary>
    /// Checks a native handle against its position: an owned, live handle of this Store, of the expected type,
    /// not used by another call and not passed twice in conflicting positions.
    /// </summary>
    private static void ValidateNative(
        NativeResourceCell cell,
        ResourcePlanNode node,
        string path,
        ValidationState state,
        bool own)
    {
        // Throws ObjectDisposedException for an expired callback-argument view.
        var handle = cell.Handle;
        if (!handle.IsOwned)
        {
            throw new ArgumentException(
                $"{path}: a borrowed resource received by a host callback cannot be passed to a component function.",
                "arguments");
        }

        handle.ThrowIfNotUsable(path);
        if (!ReferenceEquals(handle.Store, state.Store))
        {
            throw new ArgumentException($"{path}: the resource belongs to another Store.", "arguments");
        }

        if (node.Host is not null || node.Expected is null || !node.Expected.NativeEquals(handle.Type.NativeHandle))
        {
            throw new ArgumentException(
                $"{path}: expected {node.DescribeHandle()}, got a native resource of another type.", "arguments");
        }

        if (handle.InUse)
        {
            throw new InvalidOperationException($"{path}: the resource is in use by another component call.");
        }

        if (own)
        {
            if (state.Owned.Contains(handle) || state.Borrowed.Contains(handle))
            {
                throw new InvalidOperationException(
                    $"{path}: the same resource cannot be passed as own more than once, or as both own and borrow, in one call.");
            }

            state.Owned.Add(handle);
        }
        else
        {
            if (state.Owned.Contains(handle))
            {
                throw new InvalidOperationException(
                    $"{path}: the same resource cannot be passed as both own and borrow in one call.");
            }

            state.Borrowed.Add(handle);
        }
    }

    /// <summary>
    /// Writes a resource argument as a <c>bool(false)</c> placeholder and records the fix-up that
    /// <see cref="Commit"/> turns into a native handle. Needs no context.
    /// </summary>
    public void WriteResource(ComponentValue value, IntPtr destination, ResourcePlanNode? node)
    {
        if (node is null || !node.IsHandle)
        {
            throw new ArgumentException(
                "A resource value was passed at a position where the component function does not expect a resource.",
                "arguments");
        }

        var own = node.Kind == PlanKind.Own;
        if (value.ResourceCell is NativeResourceCell native)
        {
            // The handle's own box is borrowed by the call; Wasmtime copies the resource out of it.
            var handle = native.Handle;
            handle.AcquireUse();
            natives.Add(new NativeUse(handle, own));
            ResourceMarshalling.WriteResource(destination, handle.BoxPointer);
            return;
        }

        var cell = (HostResourceCell)value.ResourceCell!;
        var target = cell.Value;
        var rep = own ? Table.Reserve(cell.HostType, target) : Table.Lend(cell.HostType, target);

        // An own callback argument moved into a guest (an export of another Store called from inside the
        // callback) is no longer the callback's to release; Abort undoes this if the call is never issued.
        var moved = own && cell.Origin == ResourceCellOrigin.Argument && cell.Ownership == ComponentResourceOwnership.Own
            ? cell
            : null;
        moved?.SetTransferred(true);
        var index = fixups.Count;
        fixups.Add(new Fixup { Slot = destination, Rep = rep, Own = own, Moved = moved });
        var host = ComponentResourceNative.wasmtime_component_resource_host_new(own, rep, cell.HostType.Id);
        var fixup = fixups[index];
        fixup.Host = host;
        fixups[index] = fixup;
        ResourceMarshalling.WritePlaceholder(destination);
    }

    /// <summary>
    /// Creates the native handles on the caller's thread. On failure every handle created so far is
    /// removed again (without running destructors), every reservation is undone, and the error is thrown.
    /// </summary>
    public void Commit(IntPtr context, ComponentValueMarshaller.AllocationScope scope)
    {
        for (var k = 0; k < fixups.Count; k++)
        {
            var fixup = fixups[k];
            var error = ComponentResourceNative.wasmtime_component_resource_host_to_any(context, fixup.Host, out var any);
            ComponentResourceNative.wasmtime_component_resource_host_delete(fixup.Host);
            fixup.Host = IntPtr.Zero;
            if (error == IntPtr.Zero)
            {
                fixup.Any = any;
                scope.TrackNative(any, ComponentResourceNative.wasmtime_component_resource_any_delete);
            }

            fixups[k] = fixup;
            if (error != IntPtr.Zero)
            {
                RemoveNativeEntries(context, issued: false);
                Abort();
                throw ResourceMarshalling.ToException(error);
            }

            ResourceMarshalling.WriteResource(fixup.Slot, any);
        }

        committed = true;
    }

    /// <summary>Undoes everything before the commit: deletes host boxes, reservations and lends.</summary>
    public void Abort()
    {
        if (finished)
        {
            return;
        }

        finished = true;
        for (var i = 0; i < fixups.Count; i++)
        {
            var fixup = fixups[i];
            if (fixup.Host != IntPtr.Zero)
            {
                ComponentResourceNative.wasmtime_component_resource_host_delete(fixup.Host);
            }

            if (fixup.Own)
            {
                Table.Unreserve(fixup.Rep);
            }
            else
            {
                Table.EndLend(fixup.Rep);
            }

            fixup.Moved?.SetTransferred(false);
        }

        fixups.Clear();
        foreach (var use in natives)
        {
            use.Handle.ReleaseUse();
        }

        natives.Clear();
    }

    /// <summary>
    /// Runs after the native call, whether it succeeded or failed. Once issued, ownership stays
    /// committed: own arguments belong to the guest and lends end.
    /// </summary>
    public void AfterCall(IntPtr context, bool issued)
    {
        if (finished)
        {
            return;
        }

        if (!committed)
        {
            Abort();
            return;
        }

        RemoveNativeEntries(context, issued);
        if (!issued)
        {
            Abort();
            return;
        }

        finished = true;
        foreach (var fixup in fixups)
        {
            if (fixup.Own)
            {
                Table.Commit(fixup.Rep);
            }
            else
            {
                Table.EndLend(fixup.Rep);
            }
        }

        fixups.Clear();
        foreach (var use in natives)
        {
            use.Handle.ReleaseUse();

            // Lowering may have consumed an own argument even if the call then failed: it is the guest's now.
            if (use.Own)
            {
                use.Handle.CompleteTransfer();
            }
        }

        natives.Clear();
    }

    /// <summary>
    /// Removes the host table entries <c>host_to_any</c> created for moved objects when the call was never
    /// issued, without a destructor, and drops the borrows it created for lent objects so Wasmtime stops
    /// tracking them.
    /// </summary>
    private void RemoveNativeEntries(IntPtr context, bool issued)
    {
        for (var j = 0; j < fixups.Count; j++)
        {
            if (!issued && !fixups[j].Own && fixups[j].Any != IntPtr.Zero)
            {
                var dropError = ComponentResourceNative.wasmtime_component_resource_any_drop(context, fixups[j].Any);
                if (dropError != IntPtr.Zero)
                {
                    ComponentResourceNative.wasmtime_error_delete(dropError);
                }

                continue;
            }

            if (fixups[j].RemoveAfterCall(issued))
            {
                var error = ComponentResourceNative.wasmtime_component_resource_any_to_host(context, fixups[j].Any, out var host);
                if (error != IntPtr.Zero)
                {
                    ComponentResourceNative.wasmtime_error_delete(error);
                }
                else
                {
                    ComponentResourceNative.wasmtime_component_resource_host_delete(host);
                }
            }
        }
    }
}

/// <summary>
/// Converts resource results of an export call into host objects (binding host types) or owned native
/// handles (every other type). Runs on the caller's thread after the call, inside its component operation, so
/// it may use the context directly.
/// </summary>
internal sealed class ExportResultReader : IResourceReader
{
    private readonly Store store;
    private readonly IntPtr context;
    private List<ComponentResource>? stolen;
    private List<KeyValuePair<ComponentHostResourceType, object>>? takenBack;
    private ComponentHostResourceType? lastHostType;

    public ExportResultReader(Store store, IntPtr context)
    {
        this.store = store;
        this.context = context;
    }

    public ResourceCell ReadResource(IntPtr source, IntPtr any)
    {
        var type = ResourceMarshalling.Classify(store, any, ref lastHostType);
        if (type is null)
        {
            return StealNative(source, any);
        }

        // Removes Wasmtime's table entry without running the linker destructor; the host owns the object again.
        var error = ComponentResourceNative.wasmtime_component_resource_any_to_host(context, any, out var host);
        if (error != IntPtr.Zero)
        {
            throw ResourceMarshalling.ToException(error);
        }

        uint rep;
        bool owned;
        try
        {
            rep = ComponentResourceNative.wasmtime_component_resource_host_rep(host);
            owned = ComponentResourceNative.wasmtime_component_resource_host_owned(host);
        }
        finally
        {
            ComponentResourceNative.wasmtime_component_resource_host_delete(host);
        }

        var table = store.ComponentResourceTable;
        var value = owned ? table.TakeBack(type, rep) : table.Get(type, rep);
        if (owned)
        {
            (takenBack ??= new List<KeyValuePair<ComponentHostResourceType, object>>())
                .Add(new KeyValuePair<ComponentHostResourceType, object>(type, value));
        }

        return new HostResourceCell(
            type,
            owned ? ComponentResourceOwnership.Own : ComponentResourceOwnership.Borrow,
            ResourceCellOrigin.Result,
            value);
    }

    /// <summary>
    /// Takes the result's box into a new owned handle and overwrites the slot with <c>bool(false)</c>, so
    /// the result's <c>wasmtime_component_val_delete</c> no longer frees it.
    /// </summary>
    private ResourceCell StealNative(IntPtr source, IntPtr any)
    {
        if (!ComponentResourceNative.wasmtime_component_resource_any_owned(any))
        {
            throw new InvalidOperationException("A component function returned a borrowed resource.");
        }

        ResourceMarshalling.WritePlaceholder(source);
        var handle = ComponentResource.CreateOwned(store, any);
        (stolen ??= new List<ComponentResource>()).Add(handle);
        return new NativeResourceCell(handle, ComponentResourceOwnership.Own, ResourceCellOrigin.Result);
    }

    /// <summary>
    /// The result could not be delivered: disposes the handles taken so far. A component operation is in
    /// progress, so their drops are deferred rather than run here. Owned host objects already taken back from
    /// the guest reach nobody, so their destructors run.
    /// </summary>
    public void Abort()
    {
        if (takenBack is not null)
        {
            foreach (var entry in takenBack)
            {
                try
                {
                    entry.Key.Destroy(entry.Value);
                }
                catch (Exception)
                {
                    // Reading the result already failed; that error is the one reported.
                }
            }

            takenBack = null;
        }

        if (stolen is null)
        {
            return;
        }

        foreach (var handle in stolen)
        {
            handle.Dispose();
        }

        stolen = null;
    }
}

/// <summary>
/// The resource side of one host callback: converts resource arguments into cells before user code
/// runs, turns resource results into native handles after it, and at the end of the callback expires
/// argument views and releases owned arguments nobody claimed.
/// </summary>
/// <remarks>
/// Binding host types become managed objects (<c>any_to_host</c>); every other type becomes a
/// <see cref="ComponentResource"/>: an owned argument is cloned into a handle the binding holds until
/// the user claims it, a borrowed argument is released at once and seen as an opaque view.
/// </remarks>
internal sealed class CallbackResources : IResourceReader
{
    private readonly IntPtr context;
    private Store? store;
    private List<ResourceCell>? arguments;
    private List<PendingOp>? pending;
    private List<ResultFixup>? fixups;
    private List<ComponentResource>? moves;
    private HashSet<object>? destroyed;
    private ComponentHostResourceType? lastHostType;
    private bool resultsFinished;
    private bool ended;

    /// <param name="context">The Store context Wasmtime passed to the callback.</param>
    public CallbackResources(IntPtr context) => this.context = context;

    private struct ResultFixup
    {
        public IntPtr Slot;
        public IntPtr Host;
        public uint Rep;
        public ComponentHostResourceType Type;
        public object Value;
    }

    /// <summary>
    /// An argument whose conversion needs the Store's context: an owned or borrowed host object to look up
    /// (<see cref="Cell"/> is a <see cref="HostResourceCell"/>), or a borrow of a native type to release.
    /// </summary>
    private readonly struct PendingOp
    {
        public PendingOp(ResourceCell cell, IntPtr any)
        {
            Cell = cell;
            Any = any;
        }

        public ResourceCell Cell { get; }

        /// <summary>The argument's <c>wasmtime_component_resource_any_t*</c>, owned by Wasmtime.</summary>
        public IntPtr Any { get; }
    }

    private Store Store => store ??= new StoreContext(context).Store;

    public ResourceCell ReadResource(IntPtr source, IntPtr any)
    {
        var type = ResourceMarshalling.Classify(Store, any, ref lastHostType);
        var owned = ComponentResourceNative.wasmtime_component_resource_any_owned(any);
        var ownership = owned ? ComponentResourceOwnership.Own : ComponentResourceOwnership.Borrow;
        ResourceCell cell;
        if (type is not null)
        {
            cell = new HostResourceCell(type, ownership, ResourceCellOrigin.Argument, null);
            (pending ??= new List<PendingOp>()).Add(new PendingOp(cell, any));
        }
        else if (owned)
        {
            // Wasmtime frees its box after the callback; the clone (pure) is the binding's.
            var clone = ComponentResourceNative.wasmtime_component_resource_any_clone(any);
            cell = new NativeResourceCell(ComponentResource.CreateOwned(Store, clone), ownership, ResourceCellOrigin.Argument);
        }
        else
        {
            cell = new NativeResourceCell(ComponentResource.CreateBorrowView(Store, any), ownership, ResourceCellOrigin.Argument);
            (pending ??= new List<PendingOp>()).Add(new PendingOp(cell, any));
        }

        (arguments ??= new List<ResourceCell>()).Add(cell);
        return cell;
    }

    /// <summary>
    /// Converts every resource argument before user code runs: owned host objects are taken back from their
    /// guest (no destructor runs), borrowed ones are looked up, and borrows of native types are released at
    /// once. A failure does not stop the conversion, so every owned argument is still resolved and the end of
    /// the callback releases it; the first failure is thrown afterwards.
    /// </summary>
    public void ResolveArguments()
    {
        if (pending is null || pending.Count == 0)
        {
            return;
        }

        var ops = pending;
        pending = null;
        HostResourceTable? table = null;
        Exception? failure = null;
        foreach (var op in ops)
        {
            if (op.Cell is not HostResourceCell cell)
            {
                // A borrow of a native type: the callback only sees an opaque view, so release it now.
                var dropError = ComponentResourceNative.wasmtime_component_resource_any_drop(context, op.Any);
                if (dropError != IntPtr.Zero)
                {
                    failure ??= ResourceMarshalling.ToException(dropError);
                }

                continue;
            }

            var error = ComponentResourceNative.wasmtime_component_resource_any_to_host(context, op.Any, out var host);
            if (error != IntPtr.Zero)
            {
                failure ??= ResourceMarshalling.ToException(error);
                continue;
            }

            uint rep;
            uint typeId;
            bool owned;
            try
            {
                rep = ComponentResourceNative.wasmtime_component_resource_host_rep(host);
                typeId = ComponentResourceNative.wasmtime_component_resource_host_type(host);
                owned = ComponentResourceNative.wasmtime_component_resource_host_owned(host);
            }
            finally
            {
                ComponentResourceNative.wasmtime_component_resource_host_delete(host);
            }

            try
            {
                if (typeId != cell.HostType.Id)
                {
                    throw new InvalidOperationException(
                        $"A host resource of type {typeId} arrived where {cell.HostType.DisplayName} was expected.");
                }

                table ??= Store.ComponentResourceTable;
                cell.Resolve(owned ? table.TakeBack(cell.HostType, rep) : table.Get(cell.HostType, rep));
            }
            catch (Exception exception)
            {
                // Keep converting: an owned argument taken back from its guest must still be resolved, so the
                // end of the callback can release it.
                failure ??= exception;
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    /// <summary>
    /// Checks every resource in the callback's results (host objects and native handles) against the result type,
    /// before any result is written, so a resource of the wrong type (or at a position without a resource) fails
    /// the callback while it is still the host's, rather than after it was committed to the guest. Builds the
    /// function's resource plan only when a resource is returned.
    /// </summary>
    /// <param name="functionType">The callback's <c>wasmtime_component_func_type_t*</c>, borrowed.</param>
    /// <param name="results">The results the user callback produced; unassigned (null) entries are skipped.</param>
    /// <param name="planCache">The function's plan, built on first use and kept for later calls.</param>
    /// <exception cref="InvalidOperationException">A resource does not fit its result position.</exception>
    public void CheckResultResources(
        IntPtr functionType,
        ReadOnlySpan<ComponentValue> results,
        StrongBox<ResourcePlan?> planCache)
    {
        var any = false;
        foreach (var result in results)
        {
            if (result is not null && ContainsResource(result))
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return;
        }

        var plan = planCache.Value ??= ResourcePlan.Build(functionType, Store.ComponentHostResourceTypes);
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is { } result)
            {
                CheckResultResource(result, i == 0 ? plan.Result : null, $"results[{i}]");
            }
        }
    }

    private static bool ContainsResource(ComponentValue value)
    {
        switch (value.Kind)
        {
            case ComponentValueKind.Resource:
                return value.ResourceCell is not null;

            case ComponentValueKind.List:
            case ComponentValueKind.Tuple:
                foreach (var item in value.Items)
                {
                    if (item is not null && ContainsResource(item))
                    {
                        return true;
                    }
                }

                return false;

            case ComponentValueKind.Record:
                foreach (var field in value.Fields)
                {
                    if (field.Value is not null && ContainsResource(field.Value))
                    {
                        return true;
                    }
                }

                return false;

            case ComponentValueKind.Option:
            case ComponentValueKind.Result:
            case ComponentValueKind.Variant:
                return value.Payload is { } payload && ContainsResource(payload);

            default:
                return false;
        }
    }

    private static void CheckResultResource(ComponentValue value, ResourcePlanNode? node, string path)
    {
        switch (value.Kind)
        {
            case ComponentValueKind.Resource:
                if (value.ResourceCell is NativeResourceCell native &&
                    (node is null || node.Kind != PlanKind.Own || node.Host is not null || node.Expected is null ||
                     !node.Expected.NativeEquals(native.RawHandle.Type.NativeHandle)))
                {
                    throw new InvalidOperationException(
                        $"{path}: the callback returned a native resource of a type the function does not return here" +
                        (node is { IsHandle: true } ? $" (expected {node.DescribeHandle()})." : "."));
                }

                if (value.ResourceCell is HostResourceCell host &&
                    (node is null || node.Kind != PlanKind.Own || !ReferenceEquals(node.Host, host.HostType)))
                {
                    throw new InvalidOperationException(
                        $"{path}: the callback returned a {host.HostType.DisplayName} object where the function does not return one" +
                        (node is { IsHandle: true } ? $" (expected {node.DescribeHandle()})." : "."));
                }

                break;

            case ComponentValueKind.List:
                for (var i = 0; i < value.Items.Count; i++)
                {
                    CheckResultResource(value.Items[i], node?.Kind == PlanKind.List ? node.Element : null, $"{path}[{i}]");
                }

                break;

            case ComponentValueKind.Tuple:
                for (var i = 0; i < value.Items.Count; i++)
                {
                    CheckResultResource(value.Items[i], node?.Child(PlanKind.Tuple, i), $"{path}[{i}]");
                }

                break;

            case ComponentValueKind.Record:
                foreach (var field in value.Fields)
                {
                    CheckResultResource(field.Value, node?.Named(PlanKind.Record, field.Key), $"{path}.{field.Key}");
                }

                break;

            case ComponentValueKind.Option:
                if (value.Flag && value.Payload is { } some)
                {
                    CheckResultResource(some, node?.Kind == PlanKind.Option ? node.Some : null, $"{path}.some");
                }

                break;

            case ComponentValueKind.Result:
                if (value.Payload is { } arm)
                {
                    var armNode = node?.Kind == PlanKind.Result ? (value.Flag ? node.Ok : node.Err) : null;
                    CheckResultResource(arm, armNode, $"{path}.{(value.Flag ? "ok" : "err")}");
                }

                break;

            case ComponentValueKind.Variant:
                if (value.Payload is { } payload)
                {
                    CheckResultResource(payload, node?.Named(PlanKind.Variant, value.Text!), $"{path}.{value.Text}");
                }

                break;
        }
    }

    /// <summary>
    /// Writes a resource result. A host object becomes a placeholder plus a reservation and a host box,
    /// turned into a native handle by <see cref="CompleteResults"/> once every result is written. A native
    /// handle is cloned (pure) and reads Transferred from now on; its box is freed when the results are completed,
    /// and <see cref="AbortResults"/> makes it live again.
    /// </summary>
    public void WriteResult(ComponentValue value, IntPtr destination)
    {
        if (value.ResourceCell is NativeResourceCell native)
        {
            WriteNativeResult(native, destination);
            return;
        }

        var cell = (HostResourceCell)value.ResourceCell!;
        if (cell.Ownership == ComponentResourceOwnership.Borrow)
        {
            throw new InvalidOperationException(
                "A callback result can only carry an owned resource; a borrow cannot be returned.");
        }

        var target = cell.Value;
        var rep = Store.ComponentResourceTable.Reserve(cell.HostType, target);
        var host = ComponentResourceNative.wasmtime_component_resource_host_new(true, rep, cell.HostType.Id);
        (fixups ??= new List<ResultFixup>()).Add(new ResultFixup
        {
            Slot = destination,
            Host = host,
            Rep = rep,
            Type = cell.HostType,
            Value = target,
        });
        ResourceMarshalling.WritePlaceholder(destination);
    }

    private void WriteNativeResult(NativeResourceCell cell, IntPtr destination)
    {
        if (cell.Ownership == ComponentResourceOwnership.Borrow || !cell.RawHandle.IsOwned)
        {
            throw new InvalidOperationException(
                "A callback result can only carry an owned resource; a borrow cannot be returned.");
        }

        var handle = cell.Handle;
        if (!ReferenceEquals(handle.Store, Store))
        {
            throw new InvalidOperationException("A callback result cannot carry a resource of another Store.");
        }

        if (moves is not null && moves.Contains(handle))
        {
            throw new InvalidOperationException("A callback cannot return the same resource more than once.");
        }

        if (handle.State != ComponentResourceState.Live)
        {
            throw new InvalidOperationException(
                $"A callback result cannot carry a resource that is {handle.State}.");
        }

        if (handle.InUse)
        {
            throw new InvalidOperationException("A callback result cannot carry a resource that a component call is using.");
        }

        // From here the handle reads Transferred, so nothing else can pass or drop it while Wasmtime holds the
        // clone; AbortResults makes it live again.
        if (!handle.BeginResultTransfer())
        {
            throw new InvalidOperationException(
                $"A callback result cannot carry a resource that is {handle.State}.");
        }

        (moves ??= new List<ComponentResource>()).Add(handle);
        var clone = ComponentResourceNative.wasmtime_component_resource_any_clone(handle.BoxPointer);
        ResourceMarshalling.WriteResource(destination, clone);
    }

    /// <summary>Number of result fix-ups recorded so far.</summary>
    public int FixupCount => fixups?.Count ?? 0;

    /// <summary>
    /// Moves fix-ups recorded at <paramref name="from"/> onwards whose slot lies in the value that
    /// <c>wasmtime_component_val_new</c> just moved from <paramref name="oldValue"/> to <paramref name="newValue"/>.
    /// </summary>
    public void Relocate(int from, IntPtr oldValue, IntPtr newValue)
    {
        if (fixups is null)
        {
            return;
        }

        var start = oldValue.ToInt64();
        var end = start + ComponentValueMarshaller.ValueSize;
        for (var i = from; i < fixups.Count; i++)
        {
            var slot = fixups[i].Slot.ToInt64();
            if (slot >= start && slot < end)
            {
                var fixup = fixups[i];
                fixup.Slot = new IntPtr(newValue.ToInt64() + (slot - start));
                fixups[i] = fixup;
            }
        }
    }

    /// <summary>
    /// Applies the result fix-ups with the callback's context and commits the reservations and the moves of
    /// native handles. On failure the handles already created are removed, the results are aborted and the
    /// error is thrown.
    /// </summary>
    /// <remarks>
    /// Wasmtime lowers the results only after the callback returns. If that lowering fails anyway (the call then
    /// traps and the Store is marked failed), the native handles committed here stay
    /// <see cref="ComponentResourceState.Transferred"/>; their resources stay in the Store until it is disposed.
    /// <see cref="CheckResultResources"/> rules out the resource type mismatches that would cause it.
    /// </remarks>
    public void CompleteResults()
    {
        if (fixups is not null && fixups.Count > 0)
        {
            ApplyFixups();
        }

        CompleteMoves();
    }

    private void CompleteMoves()
    {
        if (moves is not null)
        {
            // Wasmtime moves each clone into the guest when it lowers the results; the originals are spent.
            foreach (var handle in moves)
            {
                handle.FinishResultTransfer();
            }

            moves = null;
        }

        resultsFinished = true;
    }

    private void ApplyFixups()
    {
        var anys = new IntPtr[fixups!.Count];
        for (var k = 0; k < fixups.Count; k++)
        {
            var fixup = fixups[k];
            var error = ComponentResourceNative.wasmtime_component_resource_host_to_any(context, fixup.Host, out var any);
            ComponentResourceNative.wasmtime_component_resource_host_delete(fixup.Host);
            fixup.Host = IntPtr.Zero;
            fixups[k] = fixup;
            if (error != IntPtr.Zero)
            {
                // The slots already written hold their boxes, which Wasmtime frees with the results;
                // only the table entries need removing, without a destructor.
                for (var j = 0; j < k; j++)
                {
                    var removeError = ComponentResourceNative.wasmtime_component_resource_any_to_host(
                        context, anys[j], out var host);
                    if (removeError != IntPtr.Zero)
                    {
                        ComponentResourceNative.wasmtime_error_delete(removeError);
                    }
                    else
                    {
                        ComponentResourceNative.wasmtime_component_resource_host_delete(host);
                    }
                }

                AbortResults();
                throw ResourceMarshalling.ToException(error);
            }

            anys[k] = any;
            ResourceMarshalling.WriteResource(fixup.Slot, any);
        }

        var table = Store.ComponentResourceTable;
        foreach (var fixup in fixups)
        {
            table.Commit(fixup.Rep);
        }

        fixups.Clear();
    }

    /// <summary>
    /// The callback failed before its results were committed: deletes the host boxes, undoes the
    /// reservations and runs the destructor once for each object the callback returned. Native handles the
    /// results would have moved stay with the host (Wasmtime frees the clones with the results); an unclaimed
    /// owned argument among them is released like any other once the callback has ended.
    /// </summary>
    public void AbortResults()
    {
        if (resultsFinished)
        {
            return;
        }

        resultsFinished = true;
        var abandoned = moves;
        moves = null;
        if (abandoned is not null)
        {
            foreach (var handle in abandoned)
            {
                handle.CancelResultTransfer();
            }

            if (ended)
            {
                ReleaseUnclaimedNatives(abandoned);
            }
        }

        if (fixups is not null)
        {
            var table = Store.ComponentResourceTable;
            foreach (var fixup in fixups)
            {
                if (fixup.Host != IntPtr.Zero)
                {
                    ComponentResourceNative.wasmtime_component_resource_host_delete(fixup.Host);
                }

                // Null when the reservation is gone, in which case the object is no longer this callback's to destroy.
                var reserved = table.Unreserve(fixup.Rep);
                DestroyOnce(fixup.Type, fixup.Value, reserved is not null);
            }

            fixups.Clear();
        }

    }

    /// <summary>
    /// Records that the failure path dealt with <paramref name="value"/>, and runs its destructor the first time
    /// when <paramref name="destroy"/> is true. Later attempts (an owned argument returned in the results) skip it.
    /// </summary>
    private void DestroyOnce(ComponentHostResourceType type, object value, bool destroy)
    {
        destroyed ??= new HashSet<object>(ResourceMarshalling.ReferenceComparer.Instance);
        if (!destroyed.Add(value) || !destroy)
        {
            return;
        }

        try
        {
            type.Destroy(value);
        }
        catch (Exception)
        {
            // The callback has already failed; its own error is the one reported.
        }
    }

    /// <summary>
    /// Whether <paramref name="value"/> is among the host objects the results carry and that are not committed
    /// yet. Their reservation decides their fate (commit or AbortResults), so <see cref="EndCallback"/> leaves them
    /// alone.
    /// </summary>
    private bool IsPendingResult(object value)
    {
        if (fixups is not null)
        {
            foreach (var fixup in fixups)
            {
                if (ReferenceEquals(fixup.Value, value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Expires every argument view and releases owned arguments that were neither claimed, moved into a
    /// guest by an export call made inside the callback, nor handed back in the results: host objects are
    /// destroyed, native handles disposed (their drop is deferred, because the callback's component operation
    /// is still in progress). Runs before <see cref="CompleteResults"/>, so a destructor failure can still
    /// abort the results. Returns the first destructor failure, or null.
    /// </summary>
    public Exception? EndCallback()
    {
        if (ended)
        {
            return null;
        }

        ended = true;
        if (arguments is null)
        {
            return null;
        }

        Exception? failure = null;
        foreach (var argument in arguments)
        {
            argument.Expire();
            if (argument is NativeResourceCell native)
            {
                if (!native.RawHandle.IsOwned)
                {
                    native.RawHandle.Expire();
                }

                continue;
            }

            var cell = (HostResourceCell)argument;
            if (cell.Ownership != ComponentResourceOwnership.Own || cell.Claimed || cell.Transferred ||
                cell.RawValue is not { } value)
            {
                continue;
            }

            if ((destroyed is not null && destroyed.Contains(value)) || IsPendingResult(value))
            {
                continue;
            }

            // Passed back to a guest: in this callback's results, or into an export of any Store called meanwhile
            // (possibly wrapped in a new value, so the cell does not know).
            if (HostResourceTable.IsOwnedByAnyGuest(value))
            {
                continue;
            }

            try
            {
                cell.HostType.Destroy(value);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        ReleaseUnclaimedNatives(null);
        return failure;
    }

    /// <summary>
    /// Disposes the owned native arguments nobody claimed and that are not about to move into the guest in
    /// the results. With <paramref name="only"/>, considers only those handles.
    /// </summary>
    private void ReleaseUnclaimedNatives(List<ComponentResource>? only)
    {
        if (arguments is null)
        {
            return;
        }

        foreach (var argument in arguments)
        {
            if (argument is not NativeResourceCell { Claimed: false } native || !native.RawHandle.IsOwned)
            {
                continue;
            }

            var handle = native.RawHandle;
            if (only is not null ? !only.Contains(handle) : moves is not null && moves.Contains(handle))
            {
                continue;
            }

            handle.Dispose();
        }
    }
}
