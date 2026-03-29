using System.Runtime.InteropServices;
using System.Text;

namespace sharpclaw.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct WasmByteVec
{
    public nuint Size;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WasmExternVec
{
    public nuint Size;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WasmValVec
{
    public nuint Size;
    public IntPtr Data;

    public static WasmValVec Empty => new()
    {
        Size = 0,
        Data = IntPtr.Zero
    };
}

internal static class WasmerNative
{
    private const string LibraryName = "wasmer.dll";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasm_engine_new();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_engine_delete(IntPtr engine);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasm_store_new(IntPtr engine);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_store_delete(IntPtr store);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_byte_vec_new_uninitialized(out WasmByteVec buffer, nuint size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_byte_vec_delete(ref WasmByteVec buffer);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasm_module_new(IntPtr store, in WasmByteVec binary);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_module_delete(IntPtr module);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasm_instance_new(
        IntPtr store,
        IntPtr module,
        in WasmExternVec imports,
        out IntPtr trap);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_instance_delete(IntPtr instance);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_extern_vec_delete(ref WasmExternVec exports);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasm_func_call(IntPtr function, in WasmValVec arguments, ref WasmValVec results);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_func_delete(IntPtr function);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_trap_message(IntPtr trap, out WasmByteVec message);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_trap_delete(IntPtr trap);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasi_config_new([MarshalAs(UnmanagedType.LPUTF8Str)] string programName);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_arg(IntPtr config, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_capture_stderr(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_capture_stdout(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_env(
        IntPtr config,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_inherit_stderr(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_inherit_stdin(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_inherit_stdout(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool wasi_config_mapdir(
        IntPtr config,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string alias,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string directory);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasi_env_new(IntPtr store, IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_env_delete(IntPtr state);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool wasi_env_initialize_instance(IntPtr wasiEnv, IntPtr store, IntPtr instance);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wasi_env_read_stderr(IntPtr env, IntPtr buffer, nuint bufferLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wasi_env_read_stdout(IntPtr env, IntPtr buffer, nuint bufferLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool wasi_get_imports(IntPtr store, IntPtr wasiEnv, IntPtr module, out WasmExternVec imports);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasi_get_start_function(IntPtr instance);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wasmer_last_error_length();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wasmer_last_error_message(IntPtr buffer, int length);

    internal static string GetLastErrorMessage()
    {
        var errorLength = wasmer_last_error_length();
        if (errorLength <= 0)
            return string.Empty;

        var buffer = Marshal.AllocHGlobal(errorLength);
        try
        {
            var written = wasmer_last_error_message(buffer, errorLength);
            if (written <= 0)
                return string.Empty;

            return Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string GetTrapMessageAndDelete(IntPtr trap)
    {
        if (trap == IntPtr.Zero)
            return string.Empty;

        WasmByteVec message = default;
        try
        {
            wasm_trap_message(trap, out message);
            return DecodeByteVec(message);
        }
        finally
        {
            if (message.Data != IntPtr.Zero)
                wasm_byte_vec_delete(ref message);

            wasm_trap_delete(trap);
        }
    }

    internal static string DecodeByteVec(WasmByteVec buffer)
    {
        if (buffer.Data == IntPtr.Zero || buffer.Size == 0)
            return string.Empty;

        var byteCount = checked((int)buffer.Size);
        var bytes = new byte[byteCount];
        Marshal.Copy(buffer.Data, bytes, 0, byteCount);

        if (byteCount > 0 && bytes[byteCount - 1] == 0)
            byteCount--;

        return Encoding.UTF8.GetString(bytes, 0, byteCount);
    }
}
