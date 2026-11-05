using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Wasmtime.Components;

/// <summary>The shape of a value type that matters for resources.</summary>
internal enum PlanKind
{
    Own,
    Borrow,
    List,
    Option,
    Result,
    Tuple,
    Record,
    Variant,
}

/// <summary>
/// A node of a <see cref="ResourcePlan"/>. A null child means no resource occurs below it.
/// </summary>
internal sealed class ResourcePlanNode
{
    public ResourcePlanNode(PlanKind kind) => Kind = kind;

    public PlanKind Kind { get; }

    /// <summary>Own/Borrow: the expected resource type, from the value type.</summary>
    public SafeResourceTypeHandle? Expected { get; set; }

    /// <summary>Own/Borrow: <see cref="Expected"/> matched against the Store's binding host types, or null.</summary>
    public ComponentHostResourceType? Host { get; set; }

    public ResourcePlanNode? Element { get; set; }

    public ResourcePlanNode? Some { get; set; }

    public ResourcePlanNode? Ok { get; set; }

    public ResourcePlanNode? Err { get; set; }

    /// <summary>Tuple elements, by position.</summary>
    public ResourcePlanNode?[]? Items { get; set; }

    /// <summary>Record fields or variant cases with resources below them, by name.</summary>
    public Dictionary<string, ResourcePlanNode?>? ByName { get; set; }

    /// <summary>Every record field or variant case name of the type, in declaration order.</summary>
    public string[]? Names { get; set; }

    public bool IsHandle => Kind is PlanKind.Own or PlanKind.Borrow;

    /// <summary>Describes the expected handle, for example <c>own&lt;counter#1&gt;</c>.</summary>
    public string DescribeHandle() =>
        $"{(Kind == PlanKind.Own ? "own" : "borrow")}<{Host?.DisplayName ?? "native resource"}>";

    public ResourcePlanNode? Child(PlanKind kind, int index) =>
        Kind == kind && Items is { } items && index < items.Length ? items[index] : null;

    public ResourcePlanNode? Named(PlanKind kind, string name) =>
        Kind == kind && ByName is { } byName && byName.TryGetValue(name, out var node) ? node : null;
}

/// <summary>
/// Where resources occur in a component function's parameters and result, built once per
/// <see cref="ComponentFunction"/> from its (instance-substituted) type.
/// </summary>
internal sealed class ResourcePlan
{
    private ResourcePlan(ResourcePlanNode?[] parameters, string[] names, ValueShape?[] shapes, ResourcePlanNode? result)
    {
        Parameters = parameters;
        ParameterNames = names;
        ParameterShapes = shapes;
        Result = result;
        Any = result is not null || Array.Exists(parameters, node => node is not null);
    }

    public ResourcePlanNode?[] Parameters { get; }

    public string[] ParameterNames { get; }

    /// <summary>The shape of each parameter's type, or null where the type could not be read.</summary>
    public ValueShape?[] ParameterShapes { get; }

    public ResourcePlanNode? Result { get; }

    /// <summary>False when no resource occurs anywhere, which keeps the plain marshalling path.</summary>
    public bool Any { get; }

    /// <summary>Builds the plan of a <c>wasmtime_component_func_type_t*</c>.</summary>
    /// <param name="functionType">The function type, borrowed.</param>
    /// <param name="hostTypes">The binding host types known to the Store.</param>
    /// <exception cref="NotSupportedException">A resource occurs inside a map, future or stream.</exception>
    public static unsafe ResourcePlan Build(IntPtr functionType, IReadOnlyList<ComponentHostResourceType> hostTypes)
    {
        var count = checked((int)ComponentFunction.Native.wasmtime_component_func_type_param_count(functionType));
        var parameters = new ResourcePlanNode?[count];
        var names = new string[count];
        var shapes = new ValueShape?[count];
        var buffer = stackalloc byte[ComponentResourceNative.ValueTypeSize];
        var type = (IntPtr)buffer;

        for (var i = 0; i < count; i++)
        {
            Clear(type);
            if (!ComponentResourceNative.wasmtime_component_func_type_param_nth(
                    functionType, (nuint)i, out var name, out var nameLength, type))
            {
                names[i] = $"args[{i}]";
                continue;
            }

            try
            {
                names[i] = ReadName(name, nameLength) is { Length: > 0 } text ? text : $"args[{i}]";
                parameters[i] = BuildNode(type, hostTypes);
                shapes[i] = ValueShape.Build(type);
            }
            finally
            {
                ComponentFunction.Native.wasmtime_component_valtype_delete(type);
            }
        }

        ResourcePlanNode? result = null;
        Clear(type);
        if (ComponentFunction.Native.wasmtime_component_func_type_result(functionType, type))
        {
            try
            {
                result = BuildNode(type, hostTypes);
            }
            finally
            {
                ComponentFunction.Native.wasmtime_component_valtype_delete(type);
            }
        }

        return new ResourcePlan(parameters, names, shapes, result);
    }

    private static unsafe ResourcePlanNode? BuildNode(IntPtr type, IReadOnlyList<ComponentHostResourceType> hostTypes)
    {
        var kind = Marshal.ReadByte(type);
        var payload = Marshal.ReadIntPtr(type + ComponentResourceNative.ValueTypePayloadOffset);

        switch (kind)
        {
            case ComponentResourceNative.ValTypeOwn:
            case ComponentResourceNative.ValTypeBorrow:
            {
                var expected = new SafeResourceTypeHandle(
                    ComponentResourceNative.wasmtime_component_resource_type_clone(payload));
                ComponentHostResourceType? host = null;
                foreach (var candidate in hostTypes)
                {
                    if (candidate.NativeHandle.NativeEquals(expected))
                    {
                        host = candidate;
                        break;
                    }
                }

                return new ResourcePlanNode(kind == ComponentResourceNative.ValTypeOwn ? PlanKind.Own : PlanKind.Borrow)
                {
                    Expected = expected,
                    Host = host,
                };
            }

            case ComponentResourceNative.ValTypeList:
            {
                var element = WithChild(child => ComponentResourceNative.wasmtime_component_list_type_element(payload, child),
                    hostTypes);
                return element is null ? null : new ResourcePlanNode(PlanKind.List) { Element = element };
            }

            case ComponentResourceNative.ValTypeOption:
            {
                var some = WithChild(child => ComponentResourceNative.wasmtime_component_option_type_ty(payload, child),
                    hostTypes);
                return some is null ? null : new ResourcePlanNode(PlanKind.Option) { Some = some };
            }

            case ComponentResourceNative.ValTypeResult:
            {
                var ok = WithOptionalChild(child => ComponentResourceNative.wasmtime_component_result_type_ok(payload, child),
                    hostTypes);
                var err = WithOptionalChild(child => ComponentResourceNative.wasmtime_component_result_type_err(payload, child),
                    hostTypes);
                return ok is null && err is null ? null : new ResourcePlanNode(PlanKind.Result) { Ok = ok, Err = err };
            }

            case ComponentResourceNative.ValTypeTuple:
            {
                var count = checked((int)ComponentResourceNative.wasmtime_component_tuple_type_types_count(payload));
                var items = new ResourcePlanNode?[count];
                var any = false;
                for (var i = 0; i < count; i++)
                {
                    var index = (nuint)i;
                    items[i] = WithOptionalChild(
                        child => ComponentResourceNative.wasmtime_component_tuple_type_types_nth(payload, index, child),
                        hostTypes);
                    any |= items[i] is not null;
                }

                return any ? new ResourcePlanNode(PlanKind.Tuple) { Items = items } : null;
            }

            case ComponentResourceNative.ValTypeRecord:
            {
                var count = checked((int)ComponentResourceNative.wasmtime_component_record_type_field_count(payload));
                Dictionary<string, ResourcePlanNode?>? fields = null;
                var fieldNames = new string[count];
                var buffer = stackalloc byte[ComponentResourceNative.ValueTypeSize];
                var child = (IntPtr)buffer;
                for (var i = 0; i < count; i++)
                {
                    Clear(child);
                    if (!ComponentResourceNative.wasmtime_component_record_type_field_nth(
                            payload, (nuint)i, out var name, out var nameLength, child))
                    {
                        fieldNames[i] = string.Empty;
                        continue;
                    }

                    try
                    {
                        fieldNames[i] = ReadName(name, nameLength);
                        if (BuildNode(child, hostTypes) is { } node)
                        {
                            fields ??= new Dictionary<string, ResourcePlanNode?>(StringComparer.Ordinal);
                            fields[fieldNames[i]] = node;
                        }
                    }
                    finally
                    {
                        ComponentFunction.Native.wasmtime_component_valtype_delete(child);
                    }
                }

                return fields is null ? null : new ResourcePlanNode(PlanKind.Record) { ByName = fields, Names = fieldNames };
            }

            case ComponentResourceNative.ValTypeVariant:
            {
                var count = checked((int)ComponentResourceNative.wasmtime_component_variant_type_case_count(payload));
                Dictionary<string, ResourcePlanNode?>? cases = null;
                var caseNames = new string[count];
                var buffer = stackalloc byte[ComponentResourceNative.ValueTypeSize];
                var child = (IntPtr)buffer;
                for (var i = 0; i < count; i++)
                {
                    Clear(child);
                    if (!ComponentResourceNative.wasmtime_component_variant_type_case_nth(
                            payload, (nuint)i, out var name, out var nameLength, out var hasPayload, child))
                    {
                        caseNames[i] = string.Empty;
                        continue;
                    }

                    caseNames[i] = ReadName(name, nameLength);
                    if (!hasPayload)
                    {
                        continue;
                    }

                    try
                    {
                        if (BuildNode(child, hostTypes) is { } node)
                        {
                            cases ??= new Dictionary<string, ResourcePlanNode?>(StringComparer.Ordinal);
                            cases[caseNames[i]] = node;
                        }
                    }
                    finally
                    {
                        ComponentFunction.Native.wasmtime_component_valtype_delete(child);
                    }
                }

                return cases is null ? null : new ResourcePlanNode(PlanKind.Variant) { ByName = cases, Names = caseNames };
            }

            case ComponentResourceNative.ValTypeFuture:
                ThrowIfResourceInside("future",
                    WithOptionalChild(child => ComponentResourceNative.wasmtime_component_future_type_ty(payload, child),
                        hostTypes));
                return null;

            case ComponentResourceNative.ValTypeStream:
                ThrowIfResourceInside("stream",
                    WithOptionalChild(child => ComponentResourceNative.wasmtime_component_stream_type_ty(payload, child),
                        hostTypes));
                return null;

            case ComponentResourceNative.ValTypeMap:
                ThrowIfResourceInside("map",
                    WithChild(child => ComponentResourceNative.wasmtime_component_map_type_key(payload, child), hostTypes));
                ThrowIfResourceInside("map",
                    WithChild(child => ComponentResourceNative.wasmtime_component_map_type_value(payload, child), hostTypes));
                return null;

            default:
                return null;
        }
    }

    private static void ThrowIfResourceInside(string container, ResourcePlanNode? node)
    {
        if (node is not null)
        {
            throw new NotSupportedException(
                $"Component functions with resources inside a {container} are not supported.");
        }
    }

    private static ResourcePlanNode? WithChild(Action<IntPtr> read, IReadOnlyList<ComponentHostResourceType> hostTypes) =>
        WithOptionalChild(child =>
        {
            read(child);
            return true;
        }, hostTypes);

    private static unsafe ResourcePlanNode? WithOptionalChild(
        Func<IntPtr, bool> read,
        IReadOnlyList<ComponentHostResourceType> hostTypes)
    {
        var buffer = stackalloc byte[ComponentResourceNative.ValueTypeSize];
        var child = (IntPtr)buffer;
        Clear(child);
        if (!read(child))
        {
            return null;
        }

        try
        {
            return BuildNode(child, hostTypes);
        }
        finally
        {
            ComponentFunction.Native.wasmtime_component_valtype_delete(child);
        }
    }

    private static unsafe void Clear(IntPtr type) =>
        new Span<byte>((void*)type, ComponentResourceNative.ValueTypeSize).Clear();

    private static unsafe string ReadName(byte* name, nuint length) =>
        name == null || length == 0 ? string.Empty : Extensions.PtrToStringUTF8((IntPtr)name, checked((int)length));
}
