using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Wasmtime.Components;

/// <summary>
/// The shape of a component value type: its kind, and for compound types the shapes inside. Export calls that
/// carry resources check every argument against it before the call, because a value Wasmtime fails to lower
/// after the resources were committed would leave them moved into a guest that never received them.
/// </summary>
internal sealed class ValueShape
{
    private ValueShape(ComponentValueKind? kind) => Kind = kind;

    /// <summary>The expected kind, or null for a type this binding does not check (future, stream, error-context).</summary>
    public ComponentValueKind? Kind { get; }

    private ValueShape? Element { get; set; }

    private ValueShape?[]? Items { get; set; }

    private string[]? Names { get; set; }

    private HashSet<string>? NameSet { get; set; }

    private Dictionary<string, (bool HasPayload, ValueShape? Payload)>? Cases { get; set; }

    private bool HasOk { get; set; }

    private bool HasErr { get; set; }

    private ValueShape? Ok { get; set; }

    private ValueShape? Err { get; set; }

    /// <summary>Builds the shape of the <c>wasmtime_component_valtype_t</c> at <paramref name="type"/>.</summary>
    public static unsafe ValueShape Build(IntPtr type)
    {
        var kind = Marshal.ReadByte(type);
        var payload = Marshal.ReadIntPtr(type + ComponentResourceNative.ValueTypePayloadOffset);
        switch (kind)
        {
            case ValTypeBool: return new ValueShape(ComponentValueKind.Bool);
            case ValTypeS8: return new ValueShape(ComponentValueKind.S8);
            case ValTypeS16: return new ValueShape(ComponentValueKind.S16);
            case ValTypeS32: return new ValueShape(ComponentValueKind.S32);
            case ValTypeS64: return new ValueShape(ComponentValueKind.S64);
            case ValTypeU8: return new ValueShape(ComponentValueKind.U8);
            case ValTypeU16: return new ValueShape(ComponentValueKind.U16);
            case ValTypeU32: return new ValueShape(ComponentValueKind.U32);
            case ValTypeU64: return new ValueShape(ComponentValueKind.U64);
            case ValTypeF32: return new ValueShape(ComponentValueKind.F32);
            case ValTypeF64: return new ValueShape(ComponentValueKind.F64);
            case ValTypeChar: return new ValueShape(ComponentValueKind.Char);
            case ValTypeString: return new ValueShape(ComponentValueKind.String);

            case ComponentResourceNative.ValTypeList:
                return new ValueShape(ComponentValueKind.List)
                {
                    Element = Child(child => ComponentResourceNative.wasmtime_component_list_type_element(payload, child)),
                };

            case ComponentResourceNative.ValTypeTuple:
            {
                var count = checked((int)ComponentResourceNative.wasmtime_component_tuple_type_types_count(payload));
                var items = new ValueShape?[count];
                for (var i = 0; i < count; i++)
                {
                    var index = (nuint)i;
                    items[i] = OptionalChild(
                        child => ComponentResourceNative.wasmtime_component_tuple_type_types_nth(payload, index, child));
                }

                return new ValueShape(ComponentValueKind.Tuple) { Items = items };
            }

            case ComponentResourceNative.ValTypeRecord:
            {
                var count = checked((int)ComponentResourceNative.wasmtime_component_record_type_field_count(payload));
                var names = new string[count];
                var fields = new ValueShape?[count];
                var buffer = stackalloc byte[ComponentResourceNative.ValueTypeSize];
                var child = (IntPtr)buffer;
                for (var i = 0; i < count; i++)
                {
                    Clear(child);
                    if (!ComponentResourceNative.wasmtime_component_record_type_field_nth(
                            payload, (nuint)i, out var name, out var nameLength, child))
                    {
                        names[i] = string.Empty;
                        continue;
                    }

                    try
                    {
                        names[i] = ReadName(name, nameLength);
                        fields[i] = Build(child);
                    }
                    finally
                    {
                        ComponentFunction.Native.wasmtime_component_valtype_delete(child);
                    }
                }

                return new ValueShape(ComponentValueKind.Record) { Names = names, Items = fields };
            }

            case ComponentResourceNative.ValTypeVariant:
            {
                var count = checked((int)ComponentResourceNative.wasmtime_component_variant_type_case_count(payload));
                var cases = new Dictionary<string, (bool, ValueShape?)>(StringComparer.Ordinal);
                var buffer = stackalloc byte[ComponentResourceNative.ValueTypeSize];
                var child = (IntPtr)buffer;
                for (var i = 0; i < count; i++)
                {
                    Clear(child);
                    if (!ComponentResourceNative.wasmtime_component_variant_type_case_nth(
                            payload, (nuint)i, out var name, out var nameLength, out var hasPayload, child))
                    {
                        continue;
                    }

                    if (!hasPayload)
                    {
                        cases[ReadName(name, nameLength)] = (false, null);
                        continue;
                    }

                    try
                    {
                        cases[ReadName(name, nameLength)] = (true, Build(child));
                    }
                    finally
                    {
                        ComponentFunction.Native.wasmtime_component_valtype_delete(child);
                    }
                }

                return new ValueShape(ComponentValueKind.Variant) { Cases = cases };
            }

            case ValTypeEnum:
                return new ValueShape(ComponentValueKind.Enum) { NameSet = ReadNames(payload, enumNames: true) };

            case ComponentResourceNative.ValTypeOption:
                return new ValueShape(ComponentValueKind.Option)
                {
                    Element = Child(child => ComponentResourceNative.wasmtime_component_option_type_ty(payload, child)),
                };

            case ComponentResourceNative.ValTypeResult:
            {
                var ok = OptionalChild(child => ComponentResourceNative.wasmtime_component_result_type_ok(payload, child));
                var err = OptionalChild(child => ComponentResourceNative.wasmtime_component_result_type_err(payload, child));
                return new ValueShape(ComponentValueKind.Result) { HasOk = ok is not null, Ok = ok, HasErr = err is not null, Err = err };
            }

            case ValTypeFlags:
                return new ValueShape(ComponentValueKind.Flags) { NameSet = ReadNames(payload, enumNames: false) };

            case ComponentResourceNative.ValTypeOwn:
            case ComponentResourceNative.ValTypeBorrow:
                return new ValueShape(ComponentValueKind.Resource);

            case ComponentResourceNative.ValTypeMap:
                return new ValueShape(ComponentValueKind.Map);

            default:
                return new ValueShape(null);
        }
    }

    /// <summary>Throws when <paramref name="value"/> does not have this shape.</summary>
    /// <exception cref="ArgumentException">The value does not fit; the message names its path.</exception>
    public void Validate(ComponentValue value, string path)
    {
        if (Kind is not { } kind)
        {
            return;
        }

        if (value.Kind != kind)
        {
            throw new ArgumentException($"{path}: expected a {kind} value, got {value.Kind}.", "arguments");
        }

        switch (kind)
        {
            case ComponentValueKind.List:
                for (var i = 0; i < value.Items.Count; i++)
                {
                    Element?.Validate(value.Items[i], $"{path}[{i}]");
                }

                break;

            case ComponentValueKind.Tuple:
                if (value.Items.Count != Items!.Length)
                {
                    throw new ArgumentException(
                        $"{path}: expected a tuple of {Items.Length} element(s), got {value.Items.Count}.", "arguments");
                }

                for (var i = 0; i < Items.Length; i++)
                {
                    Items[i]?.Validate(value.Items[i], $"{path}[{i}]");
                }

                break;

            case ComponentValueKind.Record:
                var fields = value.Fields;
                if (fields.Count != Names!.Length)
                {
                    throw new ArgumentException(
                        $"{path}: expected a record with {Names.Length} field(s) ({string.Join(", ", Names)}), got {fields.Count}.",
                        "arguments");
                }

                for (var i = 0; i < Names.Length; i++)
                {
                    if (!string.Equals(fields[i].Key, Names[i], StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            $"{path}: expected field '{Names[i]}' at position {i}, got '{fields[i].Key}'.", "arguments");
                    }

                    Items![i]?.Validate(fields[i].Value, $"{path}.{Names[i]}");
                }

                break;

            case ComponentValueKind.Variant:
                if (value.Text is null || !Cases!.TryGetValue(value.Text, out var @case))
                {
                    throw new ArgumentException($"{path}: the variant has no case named '{value.Text}'.", "arguments");
                }

                ValidatePayload(value.Payload, @case.HasPayload, @case.Payload, $"{path}.{value.Text}");
                break;

            case ComponentValueKind.Option:
                if (value.Flag)
                {
                    Element?.Validate(value.Payload!, $"{path}.some");
                }

                break;

            case ComponentValueKind.Result:
                ValidatePayload(
                    value.Payload,
                    value.Flag ? HasOk : HasErr,
                    value.Flag ? Ok : Err,
                    $"{path}.{(value.Flag ? "ok" : "err")}");
                break;

            case ComponentValueKind.Enum:
                if (value.Text is null || !NameSet!.Contains(value.Text))
                {
                    throw new ArgumentException($"{path}: the enum has no case named '{value.Text}'.", "arguments");
                }

                break;

            case ComponentValueKind.Flags:
                foreach (var name in value.Names)
                {
                    if (!NameSet!.Contains(name))
                    {
                        throw new ArgumentException($"{path}: the flags type has no flag named '{name}'.", "arguments");
                    }
                }

                break;
        }
    }

    private static void ValidatePayload(ComponentValue? payload, bool expected, ValueShape? shape, string path)
    {
        if (!expected)
        {
            if (payload is not null)
            {
                throw new ArgumentException($"{path}: expected no value.", "arguments");
            }

            return;
        }

        if (payload is null)
        {
            throw new ArgumentException($"{path}: expected a value.", "arguments");
        }

        shape?.Validate(payload, path);
    }

    private const byte ValTypeBool = 0;
    private const byte ValTypeS8 = 1;
    private const byte ValTypeS16 = 2;
    private const byte ValTypeS32 = 3;
    private const byte ValTypeS64 = 4;
    private const byte ValTypeU8 = 5;
    private const byte ValTypeU16 = 6;
    private const byte ValTypeU32 = 7;
    private const byte ValTypeU64 = 8;
    private const byte ValTypeF32 = 9;
    private const byte ValTypeF64 = 10;
    private const byte ValTypeChar = 11;
    private const byte ValTypeString = 12;
    private const byte ValTypeEnum = 17;
    private const byte ValTypeFlags = 20;

    private static ValueShape? Child(Action<IntPtr> read) => OptionalChild(child =>
    {
        read(child);
        return true;
    });

    private static unsafe ValueShape? OptionalChild(Func<IntPtr, bool> read)
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
            return Build(child);
        }
        finally
        {
            ComponentFunction.Native.wasmtime_component_valtype_delete(child);
        }
    }

    private static unsafe HashSet<string> ReadNames(IntPtr type, bool enumNames)
    {
        var count = enumNames
            ? ComponentResourceNative.wasmtime_component_enum_type_names_count(type)
            : ComponentResourceNative.wasmtime_component_flags_type_names_count(type);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (nuint i = 0; i < count; i++)
        {
            byte* name;
            nuint length;
            var found = enumNames
                ? ComponentResourceNative.wasmtime_component_enum_type_names_nth(type, i, out name, out length)
                : ComponentResourceNative.wasmtime_component_flags_type_names_nth(type, i, out name, out length);
            if (found)
            {
                names.Add(ReadName(name, length));
            }
        }

        return names;
    }

    private static unsafe void Clear(IntPtr type) =>
        new Span<byte>((void*)type, ComponentResourceNative.ValueTypeSize).Clear();

    private static unsafe string ReadName(byte* name, nuint length) =>
        name == null || length == 0 ? string.Empty : Extensions.PtrToStringUTF8((IntPtr)name, checked((int)length));
}
