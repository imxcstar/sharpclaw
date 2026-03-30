using sharpclaw.Interop;
using System.Runtime.InteropServices;
using System.Text;

namespace sharpclaw.Services;

public sealed record WasmCommandResult(
    bool Success,
    int ExitCode,
    string StdOut,
    string StdErr,
    uint NativeResultCode,
    string NativeResultMessage,
    bool TimedOut);

public sealed class WasmtimeWasiRuntime
{
    private const string WorkspaceGuestPath = "/workspace";
    private const string ScratchGuestPath = "/sharpclaw_tmp";
    private const string UserCodeFileName = "__sharpclaw_user_code.py";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public WasmCommandResult ExecuteCode(
        string wasmPath,
        string code,
        string workingDirectory,
        int timeoutMs,
        IReadOnlyList<string>? environmentVariables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wasmPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var fullWasmPath = Path.GetFullPath(wasmPath);
        if (!File.Exists(fullWasmPath))
            throw new FileNotFoundException("未找到 rustpython.wasm。", fullWasmPath);

        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(fullWorkingDirectory))
            throw new DirectoryNotFoundException($"工作目录不存在: {fullWorkingDirectory}");

        var scratchDir = Path.Combine(Path.GetTempPath(), "sharpclaw-wasmtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        File.WriteAllText(Path.Combine(scratchDir, UserCodeFileName), code, Utf8NoBom);

        return ExecuteModule(fullWasmPath, fullWorkingDirectory, scratchDir, timeoutMs, environmentVariables);
    }

    private static WasmCommandResult ExecuteModule(
        string wasmPath,
        string workingDirectory,
        string scratchDir,
        int timeoutMs,
        IReadOnlyList<string>? environmentVariables)
    {
        var useEpoch = timeoutMs > 0;
        IntPtr engine = IntPtr.Zero;
        IntPtr store = IntPtr.Zero;
        IntPtr module = IntPtr.Zero;
        IntPtr linker = IntPtr.Zero;
        CancellationTokenSource? epochCts = null;

        // Output capture via WASI custom callbacks (no temp files)
        var stdoutMs = new MemoryStream();
        var stderrMs = new MemoryStream();
        var stdoutGch = GCHandle.Alloc(stdoutMs);
        var stderrGch = GCHandle.Alloc(stderrMs);
        WasmtimeNative.WasiOutputCallback stdoutCb = CaptureOutput;
        WasmtimeNative.WasiOutputCallback stderrCb = CaptureOutput;

        try
        {
            // ── Engine ──────────────────────────────────────────────────
            if (useEpoch)
            {
                var config = WasmtimeNative.wasm_config_new();
                if (config == IntPtr.Zero)
                    return CreateFailure("创建 Wasmtime 配置失败。");

                WasmtimeNative.wasmtime_config_epoch_interruption_set(config, true);
                engine = WasmtimeNative.wasm_engine_new_with_config(config);
                // config is consumed by wasm_engine_new_with_config
            }
            else
            {
                engine = WasmtimeNative.wasm_engine_new();
            }

            if (engine == IntPtr.Zero)
                return CreateFailure("创建 Wasmtime engine 失败。");

            // ── Store / Context ─────────────────────────────────────────
            store = WasmtimeNative.wasmtime_store_new(engine, IntPtr.Zero, IntPtr.Zero);
            if (store == IntPtr.Zero)
                return CreateFailure("创建 Wasmtime store 失败。");

            var context = WasmtimeNative.wasmtime_store_context(store);
            if (context == IntPtr.Zero)
                return CreateFailure("获取 Wasmtime context 失败。");

            if (useEpoch)
                WasmtimeNative.wasmtime_context_set_epoch_deadline(context, 1);

            // ── WASI Config ─────────────────────────────────────────────
            var wasiConfig = WasmtimeNative.wasi_config_new();
            if (wasiConfig == IntPtr.Zero)
                return CreateFailure("创建 WASI 配置失败。");

            if (!WasmtimeNative.wasi_config_preopen_dir(
                    wasiConfig, workingDirectory, WorkspaceGuestPath,
                    WasmtimeNative.WasiPermsReadWrite, WasmtimeNative.WasiPermsReadWrite))
                return CreateFailure("映射 /workspace 失败。");

            if (!WasmtimeNative.wasi_config_preopen_dir(
                    wasiConfig, scratchDir, ScratchGuestPath,
                    WasmtimeNative.WasiPermsReadWrite, WasmtimeNative.WasiPermsReadWrite))
                return CreateFailure("映射临时脚本目录失败。");

            WasmtimeNative.wasi_config_inherit_stdin(wasiConfig);
            WasmtimeNative.wasi_config_set_stdout_custom(
                wasiConfig, stdoutCb, GCHandle.ToIntPtr(stdoutGch), IntPtr.Zero);
            WasmtimeNative.wasi_config_set_stderr_custom(
                wasiConfig, stderrCb, GCHandle.ToIntPtr(stderrGch), IntPtr.Zero);

            WasmtimeNative.SetArgv(wasiConfig,
                "rustpython", $"{ScratchGuestPath}/{UserCodeFileName}");

            var envVars = BuildEnvVars(environmentVariables);
            if (envVars.Length > 0)
                WasmtimeNative.SetEnv(wasiConfig, envVars);

            // wasmtime_context_set_wasi takes ownership of wasiConfig
            var wasiError = WasmtimeNative.wasmtime_context_set_wasi(context, wasiConfig);
            if (wasiError != IntPtr.Zero)
                return CreateFailure("设置 WASI 失败。", WasmtimeNative.GetErrorMessageAndDelete(wasiError));

            // ── Linker ──────────────────────────────────────────────────
            linker = WasmtimeNative.wasmtime_linker_new(engine);
            if (linker == IntPtr.Zero)
                return CreateFailure("创建 Wasmtime linker 失败。");

            var defineError = WasmtimeNative.wasmtime_linker_define_wasi(linker);
            if (defineError != IntPtr.Zero)
                return CreateFailure("定义 WASI imports 失败。", WasmtimeNative.GetErrorMessageAndDelete(defineError));

            // ── Module ──────────────────────────────────────────────────
            var wasmBytes = File.ReadAllBytes(wasmPath);
            var moduleError = WasmtimeNative.wasmtime_module_new(
                engine, wasmBytes, (nuint)wasmBytes.Length, out module);
            if (moduleError != IntPtr.Zero)
                return CreateFailure("编译 rustpython.wasm 失败。", WasmtimeNative.GetErrorMessageAndDelete(moduleError));

            // ── Instantiate ─────────────────────────────────────────────
            var instError = WasmtimeNative.wasmtime_linker_instantiate(
                linker, context, module, out var instance, out var instTrap);
            if (instError != IntPtr.Zero)
                return CreateFailure("实例化 rustpython.wasm 失败。", WasmtimeNative.GetErrorMessageAndDelete(instError));
            if (instTrap != IntPtr.Zero)
                return CreateFailure("实例化 rustpython.wasm 陷入 trap。", WasmtimeNative.GetTrapMessageAndDelete(instTrap));

            // ── Get _start ──────────────────────────────────────────────
            if (!WasmtimeNative.GetExport(context, in instance, "_start", out var startExtern))
                return CreateFailure("未找到 WASI `_start` 入口。");

            if (startExtern.Kind != WasmtimeExtern.KindFunc)
                return CreateFailure("`_start` 不是函数类型。");

            // ── Epoch-based timeout timer ───────────────────────────────
            if (useEpoch)
            {
                epochCts = new CancellationTokenSource();
                var capturedEngine = engine;
                var token = epochCts.Token;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(timeoutMs, token); }
                    catch (OperationCanceledException) { return; }
                    WasmtimeNative.wasmtime_engine_increment_epoch(capturedEngine);
                });
            }

            // ── Call _start ─────────────────────────────────────────────
            var callError = WasmtimeNative.wasmtime_func_call(
                context, in startExtern.Func,
                IntPtr.Zero, 0, IntPtr.Zero, 0,
                out var callTrap);

            epochCts?.Cancel();

            var stdout = Encoding.UTF8.GetString(stdoutMs.ToArray()).TrimEnd();
            var stderr = Encoding.UTF8.GetString(stderrMs.ToArray()).TrimEnd();

            // ── Handle results ──────────────────────────────────────────
            if (callError != IntPtr.Zero)
            {
                // May be a WASI proc_exit
                if (WasmtimeNative.wasmtime_error_exit_status(callError, out var exitStatus))
                {
                    WasmtimeNative.wasmtime_error_delete(callError);
                    if (exitStatus == 0)
                    {
                        return new WasmCommandResult(
                            Success: true, ExitCode: 0,
                            StdOut: stdout, StdErr: stderr,
                            NativeResultCode: 0, NativeResultMessage: "OK",
                            TimedOut: false);
                    }

                    return CreateFailure("执行 `_start` 失败。",
                        $"WASI exit code: {exitStatus}", stdout, stderr, exitStatus);
                }

                return CreateFailure("执行 `_start` 失败。",
                    WasmtimeNative.GetErrorMessageAndDelete(callError), stdout, stderr);
            }

            if (callTrap != IntPtr.Zero)
            {
                var isTimeout = false;
                if (WasmtimeNative.wasmtime_trap_code(callTrap, out var trapCode))
                    isTimeout = trapCode == WasmtimeNative.TrapCodeInterrupt;

                var trapMessage = WasmtimeNative.GetTrapMessageAndDelete(callTrap);

                if (isTimeout)
                {
                    return new WasmCommandResult(
                        Success: false, ExitCode: -1,
                        StdOut: stdout, StdErr: stderr,
                        NativeResultCode: 0,
                        NativeResultMessage: $"Execution timeout ({timeoutMs}ms). Wasmtime epoch interruption triggered.",
                        TimedOut: true);
                }

                return CreateFailure("执行 `_start` 陷入 trap。", trapMessage, stdout, stderr);
            }

            // Success with no proc_exit call
            return new WasmCommandResult(
                Success: true, ExitCode: 0,
                StdOut: stdout, StdErr: stderr,
                NativeResultCode: 0, NativeResultMessage: "OK",
                TimedOut: false);
        }
        catch (Exception ex)
        {
            return new WasmCommandResult(
                Success: false, ExitCode: 1,
                StdOut: string.Empty, StdErr: string.Empty,
                NativeResultCode: 1,
                NativeResultMessage: $"{ex.GetType().Name}: {ex.Message}",
                TimedOut: false);
        }
        finally
        {
            epochCts?.Dispose();

            if (linker != IntPtr.Zero)
                WasmtimeNative.wasmtime_linker_delete(linker);

            if (module != IntPtr.Zero)
                WasmtimeNative.wasmtime_module_delete(module);

            if (store != IntPtr.Zero)
                WasmtimeNative.wasmtime_store_delete(store);

            if (engine != IntPtr.Zero)
                WasmtimeNative.wasm_engine_delete(engine);

            GC.KeepAlive(stdoutCb);
            GC.KeepAlive(stderrCb);
            if (stdoutGch.IsAllocated) stdoutGch.Free();
            if (stderrGch.IsAllocated) stderrGch.Free();
            stdoutMs.Dispose();
            stderrMs.Dispose();

            try { Directory.Delete(scratchDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// WASI output callback — invoked by Wasmtime for each fd_write on stdout/stderr.
    /// The <paramref name="data"/> is a GCHandle pointing to a MemoryStream.
    /// </summary>
    private static nint CaptureOutput(IntPtr data, IntPtr buffer, nuint length)
    {
        if (length == 0)
            return 0;

        try
        {
            var handle = GCHandle.FromIntPtr(data);
            var stream = (MemoryStream)handle.Target!;
            var count = checked((int)length);
            var bytes = new byte[count];
            Marshal.Copy(buffer, bytes, 0, count);
            stream.Write(bytes, 0, count);
            return (nint)length;
        }
        catch
        {
            return -1;
        }
    }

    private static (string Name, string Value)[] BuildEnvVars(IReadOnlyList<string>? environmentVariables)
    {
        if (environmentVariables is null || environmentVariables.Count == 0)
            return [];

        var result = new List<(string, string)>();
        foreach (var envVar in environmentVariables)
        {
            if (string.IsNullOrWhiteSpace(envVar))
                continue;

            var sep = envVar.IndexOf('=');
            var key = sep >= 0 ? envVar[..sep] : envVar;
            var value = sep >= 0 ? envVar[(sep + 1)..] : string.Empty;
            result.Add((key, value));
        }

        return result.ToArray();
    }

    private static WasmCommandResult CreateFailure(
        string fallbackMessage,
        string? nativeMessage = null,
        string stdout = "",
        string stderr = "",
        int exitCode = 1)
    {
        var message = string.IsNullOrWhiteSpace(nativeMessage)
            ? fallbackMessage
            : nativeMessage;

        return new WasmCommandResult(
            Success: false,
            ExitCode: exitCode,
            StdOut: stdout,
            StdErr: stderr,
            NativeResultCode: (uint)Math.Max(exitCode, 1),
            NativeResultMessage: message,
            TimedOut: false);
    }
}
