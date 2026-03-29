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

public sealed class WasmerWasiRuntime
{
    private const string WorkspaceGuestPath = "/workspace";
    private const string ScratchGuestPath = "/sharpclaw_tmp";
    private const string ScriptFileName = "__sharpclaw_exec.py";
    private const string UserCodeFileName = "__sharpclaw_user_code.py";
    private const string StdOutFileName = "__sharpclaw_stdout.txt";
    private const string StdErrFileName = "__sharpclaw_stderr.txt";
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

        var scratchDir = Path.Combine(Path.GetTempPath(), "sharpclaw-wasmer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        var userCodeHostPath = Path.Combine(scratchDir, UserCodeFileName);
        var scriptHostPath = Path.Combine(scratchDir, ScriptFileName);
        File.WriteAllText(userCodeHostPath, code, Utf8NoBom);
        File.WriteAllText(scriptHostPath, BuildWrapperScript(), Utf8NoBom);

        if (timeoutMs <= 0)
            return ExecuteModule(fullWasmPath, fullWorkingDirectory, scratchDir, environmentVariables);

        // Wasmer 的 C API 没有公开的强制取消接口，所以超时只能由宿主侧抢先返回。
        var executionTask = Task.Run(() => ExecuteModule(fullWasmPath, fullWorkingDirectory, scratchDir, environmentVariables));
        var completedTask = Task.WhenAny(executionTask, Task.Delay(timeoutMs)).GetAwaiter().GetResult();
        if (!ReferenceEquals(completedTask, executionTask))
        {
            return new WasmCommandResult(
                Success: false,
                ExitCode: -1,
                StdOut: string.Empty,
                StdErr: string.Empty,
                NativeResultCode: 0,
                NativeResultMessage: $"Execution timeout ({timeoutMs}ms). Wasmer C API does not expose force-cancel for a running WASI instance.",
                TimedOut: true);
        }

        return executionTask.GetAwaiter().GetResult();
    }

    private static WasmCommandResult ExecuteModule(
        string wasmPath,
        string workingDirectory,
        string scratchDir,
        IReadOnlyList<string>? environmentVariables)
    {
        IntPtr engine = IntPtr.Zero;
        IntPtr store = IntPtr.Zero;
        IntPtr module = IntPtr.Zero;
        IntPtr config = IntPtr.Zero;
        IntPtr wasiEnv = IntPtr.Zero;
        IntPtr instance = IntPtr.Zero;
        IntPtr startFunction = IntPtr.Zero;
        WasmByteVec binary = default;
        WasmExternVec imports = default;

        try
        {
            var wasmBytes = File.ReadAllBytes(wasmPath);
            WasmerNative.wasm_byte_vec_new_uninitialized(out binary, checked((nuint)wasmBytes.Length));
            Marshal.Copy(wasmBytes, 0, binary.Data, wasmBytes.Length);

            engine = WasmerNative.wasm_engine_new();
            if (engine == IntPtr.Zero)
                return CreateFailure("创建 Wasmer engine 失败。");

            store = WasmerNative.wasm_store_new(engine);
            if (store == IntPtr.Zero)
                return CreateFailure("创建 Wasmer store 失败。");

            config = WasmerNative.wasi_config_new("rustpython");
            if (config == IntPtr.Zero)
                return CreateFailure("创建 WASI 配置失败。");

            if (!WasmerNative.wasi_config_mapdir(config, WorkspaceGuestPath, workingDirectory))
                return CreateFailure("映射 /workspace 失败。");

            if (!WasmerNative.wasi_config_mapdir(config, ScratchGuestPath, scratchDir))
                return CreateFailure("映射临时脚本目录失败。");

            foreach (var environmentVariable in environmentVariables ?? [])
            {
                if (string.IsNullOrWhiteSpace(environmentVariable))
                    continue;

                var separatorIndex = environmentVariable.IndexOf('=');
                var key = separatorIndex >= 0 ? environmentVariable[..separatorIndex] : environmentVariable;
                var value = separatorIndex >= 0 ? environmentVariable[(separatorIndex + 1)..] : string.Empty;
                WasmerNative.wasi_config_env(config, key, value);
            }

            // Wasmer capture pipes trigger RustPython's shutdown flush failure on Windows.
            // Redirect Python stdio to scratch files and let WASI inherit the host handles instead.
            WasmerNative.wasi_config_inherit_stdin(config);
            WasmerNative.wasi_config_inherit_stdout(config);
            WasmerNative.wasi_config_inherit_stderr(config);
            WasmerNative.wasi_config_arg(config, $"{ScratchGuestPath}/{ScriptFileName}");

            module = WasmerNative.wasm_module_new(store, in binary);
            if (module == IntPtr.Zero)
                return CreateFailure("编译 rustpython.wasm 失败。");

            wasiEnv = WasmerNative.wasi_env_new(store, config);
            config = IntPtr.Zero;
            if (wasiEnv == IntPtr.Zero)
                return CreateFailure("创建 WASI 环境失败。");

            if (!WasmerNative.wasi_get_imports(store, wasiEnv, module, out imports))
                return CreateFailure("获取 WASI imports 失败。");

            instance = WasmerNative.wasm_instance_new(store, module, in imports, out var instantiateTrap);
            if (instance == IntPtr.Zero)
            {
                var trapMessage = WasmerNative.GetTrapMessageAndDelete(instantiateTrap);
                return CreateFailure("实例化 rustpython.wasm 失败。", trapMessage);
            }

            if (!WasmerNative.wasi_env_initialize_instance(wasiEnv, store, instance))
                return CreateFailure("初始化 WASI 实例失败。");

            startFunction = WasmerNative.wasi_get_start_function(instance);
            if (startFunction == IntPtr.Zero)
                return CreateFailure("未找到 WASI `_start` 入口。");

            var arguments = WasmValVec.Empty;
            var results = WasmValVec.Empty;
            var startTrap = WasmerNative.wasm_func_call(startFunction, in arguments, ref results);

            var stdout = ReadOutputFile(Path.Combine(scratchDir, StdOutFileName));
            var stderr = ReadOutputFile(Path.Combine(scratchDir, StdErrFileName));

            if (startTrap != IntPtr.Zero)
            {
                var trapMessage = WasmerNative.GetTrapMessageAndDelete(startTrap);
                var exitCode = ParseWasiExitCode(trapMessage);
                if (exitCode == 0)
                {
                    return new WasmCommandResult(
                        Success: true,
                        ExitCode: 0,
                        StdOut: stdout,
                        StdErr: stderr,
                        NativeResultCode: 0,
                        NativeResultMessage: "OK",
                        TimedOut: false);
                }

                return CreateFailure("执行 `_start` 失败。", trapMessage, stdout, stderr, exitCode ?? 1);
            }

            return new WasmCommandResult(
                Success: true,
                ExitCode: 0,
                StdOut: stdout,
                StdErr: stderr,
                NativeResultCode: 0,
                NativeResultMessage: "OK",
                TimedOut: false);
        }
        catch (Exception ex)
        {
            return new WasmCommandResult(
                Success: false,
                ExitCode: 1,
                StdOut: string.Empty,
                StdErr: string.Empty,
                NativeResultCode: 1,
                NativeResultMessage: $"{ex.GetType().Name}: {ex.Message}",
                TimedOut: false);
        }
        finally
        {
            if (startFunction != IntPtr.Zero)
                WasmerNative.wasm_func_delete(startFunction);

            if (imports.Data != IntPtr.Zero)
                WasmerNative.wasm_extern_vec_delete(ref imports);

            if (wasiEnv != IntPtr.Zero)
                WasmerNative.wasi_env_delete(wasiEnv);

            if (instance != IntPtr.Zero)
                WasmerNative.wasm_instance_delete(instance);

            if (module != IntPtr.Zero)
                WasmerNative.wasm_module_delete(module);

            if (binary.Data != IntPtr.Zero)
                WasmerNative.wasm_byte_vec_delete(ref binary);

            if (store != IntPtr.Zero)
                WasmerNative.wasm_store_delete(store);

            if (engine != IntPtr.Zero)
                WasmerNative.wasm_engine_delete(engine);

            try
            {
                Directory.Delete(scratchDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string BuildWrapperScript()
    {
        return $$"""
            import os
            import sys

            os.chdir("{{WorkspaceGuestPath}}")

            with open("{{ScratchGuestPath}}/{{StdOutFileName}}", "w", encoding="utf-8", newline="") as __sharpclaw_stdout, open("{{ScratchGuestPath}}/{{StdErrFileName}}", "w", encoding="utf-8", newline="") as __sharpclaw_stderr:
                sys.stdout = __sharpclaw_stdout
                sys.stderr = __sharpclaw_stderr
                __globals = {"__name__": "__main__", "__file__": "{{ScratchGuestPath}}/{{UserCodeFileName}}"}
                with open(__globals["__file__"], "r", encoding="utf-8") as __sharpclaw_source:
                    __code = __sharpclaw_source.read()
                exec(compile(__code, __globals["__file__"], "exec"), __globals)
            """;
    }

    private static string ReadOutputFile(string path)
    {
        if (!File.Exists(path))
            return string.Empty;

        return File.ReadAllText(path, Utf8NoBom).TrimEnd();
    }

    private static int? ParseWasiExitCode(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        const string prefix = "WASI exited with code:";
        var prefixIndex = message.IndexOf(prefix, StringComparison.Ordinal);
        if (prefixIndex < 0)
            return null;

        var exitCodeText = message[(prefixIndex + prefix.Length)..].Trim();
        const string exitCodePrefix = "ExitCode::";
        if (exitCodeText.StartsWith(exitCodePrefix, StringComparison.Ordinal))
            exitCodeText = exitCodeText[exitCodePrefix.Length..];

        var digitCount = 0;
        while (digitCount < exitCodeText.Length && char.IsDigit(exitCodeText[digitCount]))
            digitCount++;

        if (digitCount == 0)
            return null;

        return int.TryParse(exitCodeText[..digitCount], out var exitCode) ? exitCode : null;
    }

    private static WasmCommandResult CreateFailure(
        string fallbackMessage,
        string? nativeMessage = null,
        string stdout = "",
        string stderr = "",
        int exitCode = 1)
    {
        var message = string.IsNullOrWhiteSpace(nativeMessage)
            ? WasmerNative.GetLastErrorMessage()
            : nativeMessage;

        if (string.IsNullOrWhiteSpace(message))
            message = fallbackMessage;

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
