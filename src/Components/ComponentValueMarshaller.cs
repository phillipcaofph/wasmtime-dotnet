using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Wasmtime.Components;

/// <summary>
/// Converts between <see cref="ComponentValue"/> and the native <c>wasmtime_component_val_t</c>
/// layout.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is asymmetric. Arguments are declared <c>const</c> by the C API, so Wasmtime never
/// takes them: everything written here is allocated by us and released by the caller's
/// <see cref="AllocationScope"/>. Results are the opposite, being allocated by Wasmtime's own
/// allocator, so their contents must be released with
/// <c>wasmtime_component_val_delete</c> rather than freed directly. Mixing the two allocators
/// corrupts the heap.
/// </para>
/// <para>
/// The native structures are written field by field rather than through the
/// <c>wasmtime_component_vallist_new</c> family of helpers, because a value must be built in
/// place inside an array element or a record entry.
/// </para>
/// </remarks>
internal static class ComponentValueMarshaller
{
    /// <summary>Size of <c>wasmtime_component_val_t</c>: a 1-byte kind padded to 8, plus a 24-byte union.</summary>
    public const int ValueSize = 32;

    /// <summary>Offset of the <c>of</c> union within <c>wasmtime_component_val_t</c>.</summary>
    public const int ValuePayloadOffset = 8;

    /// <summary>Size of <c>wasmtime_component_valrecord_entry_t</c>: a 16-byte name plus a 32-byte value.</summary>
    public const int RecordEntrySize = 48;

    /// <summary>Offset of the value within <c>wasmtime_component_valrecord_entry_t</c>.</summary>
    public const int RecordEntryValueOffset = 16;

    /// <summary>Offset of the <c>data</c> pointer within a vec or a <c>wasm_name_t</c>.</summary>
    private const int VectorDataOffset = 8;

    /// <summary>
    /// Tracks the native allocations made while writing arguments, so they can be released
    /// together once the call has returned.
    /// </summary>
    public sealed class AllocationScope : IDisposable
    {
        private readonly List<IntPtr> allocations = new List<IntPtr>();
        private bool disposed;

        /// <summary>
        /// Allocates zeroed native memory whose lifetime is bound to this scope.
        /// </summary>
        /// <param name="bytes">The number of bytes to allocate.</param>
        /// <returns>A pointer to the allocation.</returns>
        public IntPtr Allocate(int bytes)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(AllocationScope));
            }

            var pointer = Marshal.AllocHGlobal(bytes);
            allocations.Add(pointer);

            unsafe
            {
                new Span<byte>((void*)pointer, bytes).Clear();
            }

            return pointer;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            FreeAllocations();
        }

        private void FreeAllocations()
        {
            foreach (var pointer in allocations)
            {
                Marshal.FreeHGlobal(pointer);
            }

            allocations.Clear();
        }
    }

    /// <summary>
    /// Writes a value into caller-owned native storage.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <param name="destination">A pointer to <see cref="ValueSize"/> bytes of storage.</param>
    /// <param name="scope">The scope owning any secondary allocations the value needs.</param>
    public static void Write(ComponentValue value, IntPtr destination, AllocationScope scope)
    {
        Marshal.WriteByte(destination, (byte)value.Kind);
        var payload = destination + ValuePayloadOffset;

        switch (value.Kind)
        {
            case ComponentValueKind.Bool:
                Marshal.WriteByte(payload, (byte)(value.Integer != 0 ? 1 : 0));
                break;

            case ComponentValueKind.S8:
            case ComponentValueKind.U8:
                Marshal.WriteByte(payload, unchecked((byte)value.Integer));
                break;

            case ComponentValueKind.S16:
            case ComponentValueKind.U16:
                Marshal.WriteInt16(payload, unchecked((short)value.Integer));
                break;

            case ComponentValueKind.S32:
            case ComponentValueKind.U32:
                Marshal.WriteInt32(payload, unchecked((int)value.Integer));
                break;

            case ComponentValueKind.F32:
                Marshal.WriteInt32(payload, Extensions.SingleToInt32Bits((float)value.Real));
                break;

            case ComponentValueKind.S64:
            case ComponentValueKind.U64:
                Marshal.WriteInt64(payload, value.Integer);
                break;

            case ComponentValueKind.F64:
                Marshal.WriteInt64(payload, BitConverter.DoubleToInt64Bits(value.Real));
                break;

            case ComponentValueKind.String:
            case ComponentValueKind.Enum:
                WriteName(value.Text ?? string.Empty, payload, scope);
                break;

            case ComponentValueKind.List:
            case ComponentValueKind.Tuple:
                WriteVector(value.Items, payload, scope);
                break;

            case ComponentValueKind.Record:
                WriteRecord(value.Fields, payload, scope);
                break;

            case ComponentValueKind.Option:
                Marshal.WriteIntPtr(payload, value.Flag ? WriteBoxed(value.Payload!, scope) : IntPtr.Zero);
                break;

            case ComponentValueKind.Result:
                Marshal.WriteByte(payload, (byte)(value.Flag ? 1 : 0));
                Marshal.WriteIntPtr(
                    payload + VectorDataOffset,
                    value.Payload is null ? IntPtr.Zero : WriteBoxed(value.Payload, scope));
                break;

            default:
                throw new NotSupportedException(
                    $"Writing component values of kind {value.Kind} is not supported.");
        }
    }

    /// <summary>
    /// Writes a value into storage whose contents Wasmtime will free, as required for the
    /// results of a host-defined function.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <param name="destination">A pointer to <see cref="ValueSize"/> bytes of storage.</param>
    /// <remarks>
    /// The value is first built with our own allocator and then deep-copied by Wasmtime, so that
    /// every heap payload it ends up owning came from its own allocator. The extra copy is the
    /// price of not having to mirror each vec constructor.
    /// </remarks>
    public static void WriteOwned(ComponentValue value, IntPtr destination)
    {
        using (var scope = new AllocationScope())
        {
            var scratch = scope.Allocate(ValueSize);
            Write(value, scratch, scope);
            ComponentValueNative.wasmtime_component_val_clone(scratch, destination);
        }
    }

    /// <summary>
    /// Reads a value from native storage.
    /// </summary>
    /// <param name="source">A pointer to a <c>wasmtime_component_val_t</c>.</param>
    /// <returns>The value that was read.</returns>
    public static ComponentValue Read(IntPtr source)
    {
        var kind = (ComponentValueKind)Marshal.ReadByte(source);
        var payload = source + ValuePayloadOffset;

        switch (kind)
        {
            case ComponentValueKind.Bool:
                return ComponentValue.Bool(Marshal.ReadByte(payload) != 0);

            case ComponentValueKind.S8:
                return ComponentValue.S8(unchecked((sbyte)Marshal.ReadByte(payload)));

            case ComponentValueKind.U8:
                return ComponentValue.U8(Marshal.ReadByte(payload));

            case ComponentValueKind.S16:
                return ComponentValue.S16(Marshal.ReadInt16(payload));

            case ComponentValueKind.U16:
                return ComponentValue.U16(unchecked((ushort)Marshal.ReadInt16(payload)));

            case ComponentValueKind.S32:
                return ComponentValue.S32(Marshal.ReadInt32(payload));

            case ComponentValueKind.U32:
                return ComponentValue.U32(unchecked((uint)Marshal.ReadInt32(payload)));

            case ComponentValueKind.S64:
                return ComponentValue.S64(Marshal.ReadInt64(payload));

            case ComponentValueKind.U64:
                return ComponentValue.U64(unchecked((ulong)Marshal.ReadInt64(payload)));

            case ComponentValueKind.F32:
                return ComponentValue.F32(Extensions.Int32BitsToSingle(Marshal.ReadInt32(payload)));

            case ComponentValueKind.F64:
                return ComponentValue.F64(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(payload)));

            case ComponentValueKind.String:
                return ComponentValue.String(ReadName(payload));

            case ComponentValueKind.Enum:
                return ComponentValue.Enum(ReadName(payload));

            case ComponentValueKind.List:
                return ComponentValue.OwnedList(ReadVector(payload));

            case ComponentValueKind.Tuple:
                return ComponentValue.OwnedTuple(ReadVector(payload));

            case ComponentValueKind.Record:
                return ComponentValue.OwnedRecord(ReadRecord(payload));

            case ComponentValueKind.Option:
                var some = Marshal.ReadIntPtr(payload);
                return some == IntPtr.Zero ? ComponentValue.None() : ComponentValue.Some(Read(some));

            case ComponentValueKind.Result:
                var isOk = Marshal.ReadByte(payload) != 0;
                var inner = Marshal.ReadIntPtr(payload + VectorDataOffset);
                var result = inner == IntPtr.Zero ? null : Read(inner);
                return isOk ? ComponentValue.Ok(result) : ComponentValue.Err(result);

            default:
                throw new NotSupportedException(
                    $"Reading component values of kind {kind} is not supported.");
        }
    }

    private static IntPtr WriteBoxed(ComponentValue value, AllocationScope scope)
    {
        var boxed = scope.Allocate(ValueSize);
        Write(value, boxed, scope);
        return boxed;
    }

    private static void WriteName(string text, IntPtr destination, AllocationScope scope)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);

        // A zero-length allocation would still need a non-null pointer, so always take at least one byte.
        var buffer = scope.Allocate(Math.Max(byteCount, 1));
        unsafe
        {
            fixed (char* textPtr = text)
            {
                Encoding.UTF8.GetBytes(textPtr, text.Length, (byte*)buffer, byteCount);
            }
        }

        Marshal.WriteIntPtr(destination, (IntPtr)byteCount);
        Marshal.WriteIntPtr(destination + VectorDataOffset, buffer);
    }

    private static void WriteVector(IReadOnlyList<ComponentValue> items, IntPtr destination, AllocationScope scope)
    {
        // Read once: the buffer size, loop bound and written length must agree even if the list changes.
        var count = items.Count;

        // checked: an overflowing size would otherwise under-allocate and be written past.
        var buffer = scope.Allocate(Math.Max(checked(count * ValueSize), 1));
        var element = buffer;

        for (var i = 0; i < count; i++)
        {
            Write(items[i], element, scope);
            element += ValueSize;
        }

        Marshal.WriteIntPtr(destination, (IntPtr)count);
        Marshal.WriteIntPtr(destination + VectorDataOffset, buffer);
    }

    private static void WriteRecord(
        IReadOnlyList<KeyValuePair<string, ComponentValue>> fields,
        IntPtr destination,
        AllocationScope scope)
    {
        var count = fields.Count;
        var buffer = scope.Allocate(Math.Max(checked(count * RecordEntrySize), 1));
        var entry = buffer;

        for (var i = 0; i < count; i++)
        {
            WriteName(fields[i].Key, entry, scope);
            Write(fields[i].Value, entry + RecordEntryValueOffset, scope);
            entry += RecordEntrySize;
        }

        Marshal.WriteIntPtr(destination, (IntPtr)count);
        Marshal.WriteIntPtr(destination + VectorDataOffset, buffer);
    }

    private static string ReadName(IntPtr source)
    {
        var size = ReadLength(source);
        var data = Marshal.ReadIntPtr(source + VectorDataOffset);

        return data == IntPtr.Zero || size == 0 ? string.Empty : Extensions.PtrToStringUTF8(data, size);
    }

    private static List<ComponentValue> ReadVector(IntPtr source)
    {
        var size = ReadLength(source);
        var element = Marshal.ReadIntPtr(source + VectorDataOffset);
        var items = new List<ComponentValue>(size);

        for (var i = 0; i < size; i++)
        {
            items.Add(Read(element));
            element += ValueSize;
        }

        return items;
    }

    private static List<KeyValuePair<string, ComponentValue>> ReadRecord(IntPtr source)
    {
        var size = ReadLength(source);
        var entry = Marshal.ReadIntPtr(source + VectorDataOffset);
        var fields = new List<KeyValuePair<string, ComponentValue>>(size);

        for (var i = 0; i < size; i++)
        {
            fields.Add(new KeyValuePair<string, ComponentValue>(
                ReadName(entry),
                Read(entry + RecordEntryValueOffset)));

            entry += RecordEntrySize;
        }

        return fields;
    }

    /// <summary>
    /// Reads a native <c>size_t</c> length, rejecting values too large to be represented
    /// managed-side rather than silently truncating them.
    /// </summary>
    private static int ReadLength(IntPtr source)
    {
        // Reinterpreted rather than converted, because size_t is unsigned.
        var size = unchecked((ulong)Marshal.ReadIntPtr(source).ToInt64());
        if (size > int.MaxValue)
        {
            throw new NotSupportedException(
                $"A component value with {size} elements exceeds the maximum supported length of {int.MaxValue}.");
        }

        return (int)size;
    }
}
