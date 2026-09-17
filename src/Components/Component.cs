using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wasmtime.Components;

/// <summary>
/// Representation of a component in the component model.
/// </summary>
public class Component
    : IDisposable
{
    private readonly Handle handle;

    internal Handle NativeHandle
    {
        get
        {
            if (handle.IsInvalid || handle.IsClosed)
            {
                throw new ObjectDisposedException(typeof(Module).FullName);
            }

            return handle;
        }
    }

    internal Component(IntPtr handle)
    {
        this.handle = new Handle(handle);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        handle.Dispose();
    }

    /// <summary>
    /// Creates a <see cref="Component"/> given bytes.
    /// </summary>
    /// <param name="engine">The engine to use for the Component.</param>
    /// <param name="bytes">The bytes of the Component.</param>
    /// <returns>Returns a new <see cref="Component"/>.</returns>
    public static Component FromBytes(Engine engine, ReadOnlySpan<byte> bytes)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        unsafe
        {
            fixed (byte* ptr = bytes)
            {
                var error = Native.wasmtime_component_new(engine.NativeHandle, ptr, (UIntPtr)bytes.Length, out var handle);
                if (error != IntPtr.Zero)
                {
                    throw new WasmtimeException($"WebAssembly component is not valid: {WasmtimeException.FromOwnedError(error).Message}");
                }

                return new Component(handle);
            }
        }
    }

    /// <summary>
    /// Creates a <see cref="Component"/> based on a WebAssembly text format representation.
    /// </summary>
    /// <param name="engine">The engine to use for the component.</param>
    /// <param name="text">The WebAssembly text format representation of the component.</param>
    /// <returns>Returns a new <see cref="Component"/>.</returns>
    public static Component FromText(Engine engine, string text)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        unsafe
        {
            var textBytes = Encoding.UTF8.GetBytes(text);
            fixed (byte* ptr = textBytes)
            {
                var error = Module.Native.wasmtime_wat2wasm(ptr, (nuint)textBytes.Length, out var componentBytes);
                if (error != IntPtr.Zero)
                {
                    throw WasmtimeException.FromOwnedError(error);
                }

                using (componentBytes)
                {
                    return FromBytes(engine, new ReadOnlySpan<byte>(componentBytes.data, checked((int)componentBytes.size)));
                }
            }
        }
    }

    /// <summary>
    /// Creates a <see cref="Component"/> from a file in the WebAssembly text format.
    /// </summary>
    /// <param name="engine">The engine to use for the component.</param>
    /// <param name="path">The path to the file.</param>
    /// <returns>Returns a new <see cref="Component"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if an argument is null.</exception>
    public static Component FromTextFile(Engine engine, string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        return FromText(engine, File.ReadAllText(path));
    }

    /// <summary>
    /// This function serializes compiled component artifacts as blob data.
    /// </summary>
    /// <returns>If the conversion is successful, the serialized compiled component.</returns>
    public byte[] Serialize()
    {
        var error = Native.wasmtime_component_serialize(NativeHandle, out var bytes);
        if (error != IntPtr.Zero)
        {
            throw WasmtimeException.FromOwnedError(error);
        }

        using (bytes)
            return bytes.ToArray();
    }

    /// <summary>
    /// Deserializes a previously serialized component from a span of bytes.
    /// </summary>
    /// <param name="engine">The engine to use to deserialize the component.</param>
    /// <param name="bytes">The previously serialized component bytes.</param>
    /// <returns>Returns the <see cref="Component" /> that was previously serialized.</returns>
    /// <remarks>The passed bytes must come from a previous call to <see cref="Component.Serialize" />.</remarks>
    public static Component Deserialize(Engine engine, ReadOnlySpan<byte> bytes)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        unsafe
        {
            fixed (byte* ptr = bytes)
            {
                var error = Native.wasmtime_component_deserialize(engine.NativeHandle, ptr, (UIntPtr)bytes.Length, out var handle);
                if (error != IntPtr.Zero)
                {
                    throw WasmtimeException.FromOwnedError(error);
                }

                return new Component(handle);
            }
        }
    }

    /// <summary>
    /// Deserializes a previously serialized component from a file.
    /// </summary>
    /// <param name="engine">The engine to deserialize the component with.</param>
    /// <param name="path">The path to the previously serialized component.</param>
    /// <returns>Returns the <see cref="Component" /> that was previously serialized.</returns>
    /// <remarks>The file's contents must come from a previous call to <see cref="Component.Serialize" />.</remarks>
    public static Component DeserializeFile(Engine engine, string path)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        var error = Native.wasmtime_component_deserialize_file(engine.NativeHandle, path, out var handle);
        if (error != IntPtr.Zero)
        {
            throw WasmtimeException.FromOwnedError(error);
        }

        return new Component(handle);
    }

    /// <summary>
    /// Looks up an export of this component by name.
    /// </summary>
    /// <param name="name">The name of the export.</param>
    /// <returns>The export index, or null if there is no such export.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> is null.</exception>
    public ComponentExport? GetExport(string name)
    {
        return GetExport(name, null);
    }

    /// <summary>
    /// Looks up an export of this component by name, within an exported instance.
    /// </summary>
    /// <param name="name">The name of the export.</param>
    /// <param name="instance_export_index">The instance export to look within, or null for the root.</param>
    /// <returns>The export index, or null if there is no such export.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> is null.</exception>
    public ComponentExport? GetExport(string name, ComponentExport? instance_export_index)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        // The parent is optional, and a null SafeHandle cannot be marshalled, so pass it as a raw pointer.
        var parent = instance_export_index?.NativeHandle.DangerousGetHandle() ?? IntPtr.Zero;
        var nameBytes = Encoding.UTF8.GetBytes(name);

        unsafe
        {
            fixed (byte* namePtr = nameBytes)
            {
                var ret = Native.wasmtime_component_get_export_index(
                    NativeHandle, parent, namePtr, (nuint)nameBytes.Length);

                GC.KeepAlive(instance_export_index);

                return ret == IntPtr.Zero ? null : new ComponentExport(ret);
            }
        }
    }

    internal class Handle
        : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Handle(IntPtr handle)
            : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            Native.wasmtime_component_delete(handle);
            return true;
        }
    }

    internal static class Native
    {
        [DllImport(Engine.LibraryName)]
        public static extern unsafe IntPtr wasmtime_component_new(Engine.Handle engine, byte* bytes, nuint size, out IntPtr handle);

        [DllImport(Engine.LibraryName)]
        public static extern void wasmtime_component_delete(IntPtr handle);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_serialize(Handle component, out ByteArray ret);

        [DllImport(Engine.LibraryName)]
        public static extern unsafe IntPtr wasmtime_component_deserialize(Engine.Handle engine, byte* bytes, nuint size, out IntPtr handle);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_deserialize_file(Engine.Handle engine, string path, out IntPtr handle);

        [DllImport(Engine.LibraryName)]
        public static extern unsafe IntPtr wasmtime_component_get_export_index(Handle component, IntPtr instance_export_index, byte* name, nuint name_len);

        [DllImport(Engine.LibraryName)]
        public static extern unsafe IntPtr wasmtime_wat2wasm(byte* text, nuint len, out ByteArray bytes);
    }
}