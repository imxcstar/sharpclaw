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
internal struct WasmtimeInstance
{
    public ulong StoreId;
    public nuint Private;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WasmtimeFunc
{
    public ulong StoreId;
    public IntPtr Private;
}

/// <summary>
/// Wasmtime extern tagged union. Size = 32 bytes on 64-bit:
/// kind (1 byte) + 7 padding + union (24 bytes, largest member is wasmtime_global_t).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct WasmtimeExtern
{
    [FieldOffset(0)]
    public byte Kind;

    [FieldOffset(8)]
    public WasmtimeFunc Func;

    public const byte KindFunc = 0;
}

internal static class WasmtimeNative
{
    internal const string LibraryName = "wasmtime";

    // ── wasm.h: Config / Engine ─────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasm_config_new();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasm_engine_new();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasm_engine_new_with_config(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_engine_delete(IntPtr engine);

    // ── wasm.h: Byte vectors ────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_byte_vec_delete(ref WasmByteVec buffer);

    // ── wasm.h: Traps ───────────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_trap_message(IntPtr trap, out WasmByteVec message);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasm_trap_delete(IntPtr trap);

    // ── wasmtime/config.h ───────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasmtime_config_epoch_interruption_set(
        IntPtr config, [MarshalAs(UnmanagedType.I1)] bool enable);

    // ── wasmtime/engine.h ───────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasmtime_engine_increment_epoch(IntPtr engine);

    // ── wasmtime/store.h ────────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasmtime_store_new(IntPtr engine, IntPtr data, IntPtr finalizer);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasmtime_store_delete(IntPtr store);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasmtime_store_context(IntPtr store);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasmtime_context_set_wasi(IntPtr context, IntPtr wasi);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasmtime_context_set_epoch_deadline(IntPtr context, ulong ticksBeyondCurrent);

    // ── wasmtime/module.h ───────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasmtime_module_new(
        IntPtr engine, byte[] wasm, nuint wasmLen, out IntPtr module);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasmtime_module_delete(IntPtr module);

    // ── wasmtime/linker.h ───────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasmtime_linker_new(IntPtr engine);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasmtime_linker_delete(IntPtr linker);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasmtime_linker_define_wasi(IntPtr linker);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasmtime_linker_instantiate(
        IntPtr linker, IntPtr context, IntPtr module,
        out WasmtimeInstance instance, out IntPtr trap);

    // ── wasmtime/instance.h ─────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool wasmtime_instance_export_get(
        IntPtr context,
        in WasmtimeInstance instance,
        IntPtr name,
        nuint nameLen,
        out WasmtimeExtern item);

    // ── wasmtime/func.h ─────────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasmtime_func_call(
        IntPtr context,
        in WasmtimeFunc func,
        IntPtr args,
        nuint nargs,
        IntPtr results,
        nuint nresults,
        out IntPtr trap);

    // ── wasmtime/error.h ────────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasmtime_error_delete(IntPtr error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasmtime_error_message(IntPtr error, out WasmByteVec message);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool wasmtime_error_exit_status(IntPtr error, out int status);

    // ── wasmtime/trap.h ─────────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool wasmtime_trap_code(IntPtr trap, out byte code);

    internal const byte TrapCodeInterrupt = 10;

    // ── wasi.h ──────────────────────────────────────────────────────────

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wasi_config_new();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool wasi_config_set_argv(IntPtr config, nuint argc, IntPtr argv);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool wasi_config_set_env(IntPtr config, nuint envc, IntPtr names, IntPtr values);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_inherit_stdin(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_inherit_stdout(IntPtr config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_inherit_stderr(IntPtr config);

    /// <summary>
    /// WASI output callback: ptrdiff_t (*)(void* data, const unsigned char* buf, size_t len).
    /// Returns bytes written (positive) or negative OS error code.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint WasiOutputCallback(IntPtr data, IntPtr buffer, nuint length);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_set_stdout_custom(
        IntPtr config, WasiOutputCallback callback, IntPtr data, IntPtr finalizer);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wasi_config_set_stderr_custom(
        IntPtr config, WasiOutputCallback callback, IntPtr data, IntPtr finalizer);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool wasi_config_preopen_dir(
        IntPtr config,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string hostPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string guestPath,
        nuint dirPerms,
        nuint filePerms);

    // ── WASI permission constants ───────────────────────────────────────

    internal const nuint WasiDirPermsRead = 1;
    internal const nuint WasiDirPermsWrite = 2;
    internal const nuint WasiFilePermsRead = 1;
    internal const nuint WasiFilePermsWrite = 2;
    internal const nuint WasiPermsReadWrite = 3; // READ | WRITE

    // ── Managed helpers ─────────────────────────────────────────────────

    internal static string GetErrorMessageAndDelete(IntPtr error)
    {
        if (error == IntPtr.Zero)
            return string.Empty;

        try
        {
            wasmtime_error_message(error, out var message);
            try
            {
                return DecodeByteVec(message);
            }
            finally
            {
                if (message.Data != IntPtr.Zero)
                    wasm_byte_vec_delete(ref message);
            }
        }
        finally
        {
            wasmtime_error_delete(error);
        }
    }

    internal static string GetTrapMessageAndDelete(IntPtr trap)
    {
        if (trap == IntPtr.Zero)
            return string.Empty;

        try
        {
            wasm_trap_message(trap, out var message);
            try
            {
                return DecodeByteVec(message);
            }
            finally
            {
                if (message.Data != IntPtr.Zero)
                    wasm_byte_vec_delete(ref message);
            }
        }
        finally
        {
            wasm_trap_delete(trap);
        }
    }

    internal static string PeekErrorMessage(IntPtr error)
    {
        if (error == IntPtr.Zero)
            return string.Empty;

        wasmtime_error_message(error, out var message);
        try
        {
            return DecodeByteVec(message);
        }
        finally
        {
            if (message.Data != IntPtr.Zero)
                wasm_byte_vec_delete(ref message);
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

    /// <summary>
    /// Sets WASI argv. Each string is marshalled to UTF-8.
    /// </summary>
    internal static bool SetArgv(IntPtr config, params string[] argv)
    {
        if (argv.Length == 0)
            return wasi_config_set_argv(config, 0, IntPtr.Zero);

        var ptrs = new IntPtr[argv.Length];
        var arrayPtr = Marshal.AllocHGlobal(IntPtr.Size * argv.Length);
        try
        {
            for (var i = 0; i < argv.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(argv[i]);

            Marshal.Copy(ptrs, 0, arrayPtr, argv.Length);
            return wasi_config_set_argv(config, (nuint)argv.Length, arrayPtr);
        }
        finally
        {
            for (var i = 0; i < argv.Length; i++)
                if (ptrs[i] != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(ptrs[i]);

            Marshal.FreeHGlobal(arrayPtr);
        }
    }

    /// <summary>
    /// Sets WASI environment variables in batch.
    /// </summary>
    internal static bool SetEnv(IntPtr config, (string Name, string Value)[] envVars)
    {
        if (envVars.Length == 0)
            return wasi_config_set_env(config, 0, IntPtr.Zero, IntPtr.Zero);

        var namePtrs = new IntPtr[envVars.Length];
        var valuePtrs = new IntPtr[envVars.Length];
        var namesArray = Marshal.AllocHGlobal(IntPtr.Size * envVars.Length);
        var valuesArray = Marshal.AllocHGlobal(IntPtr.Size * envVars.Length);
        try
        {
            for (var i = 0; i < envVars.Length; i++)
            {
                namePtrs[i] = Marshal.StringToCoTaskMemUTF8(envVars[i].Name);
                valuePtrs[i] = Marshal.StringToCoTaskMemUTF8(envVars[i].Value);
            }

            Marshal.Copy(namePtrs, 0, namesArray, envVars.Length);
            Marshal.Copy(valuePtrs, 0, valuesArray, envVars.Length);
            return wasi_config_set_env(config, (nuint)envVars.Length, namesArray, valuesArray);
        }
        finally
        {
            for (var i = 0; i < envVars.Length; i++)
            {
                if (namePtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(namePtrs[i]);
                if (valuePtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(valuePtrs[i]);
            }

            Marshal.FreeHGlobal(namesArray);
            Marshal.FreeHGlobal(valuesArray);
        }
    }

    /// <summary>
    /// Gets an export by name from an instance (handles UTF-8 encoding of the name).
    /// </summary>
    internal static bool GetExport(
        IntPtr context,
        in WasmtimeInstance instance,
        string name,
        out WasmtimeExtern item)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var namePtr = Marshal.AllocHGlobal(nameBytes.Length);
        try
        {
            Marshal.Copy(nameBytes, 0, namePtr, nameBytes.Length);
            return wasmtime_instance_export_get(
                context, in instance, namePtr, (nuint)nameBytes.Length, out item);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }
}
