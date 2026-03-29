using System.Runtime.InteropServices;

namespace sharpclaw.Services;

public sealed record WasmPythonExecutionResult(
    bool Success,
    int ExitCode,
    string Data,
    string Error,
    uint NativeResultCode,
    string NativeResultMessage,
    bool TimedOut);

public interface IRustPythonWasmRunner : IDisposable
{
    string WasmPath { get; }
    void Init(string? workspaceRoot = null);
    WasmPythonExecutionResult ExecuteCode(string code, string workingDirectory, int timeoutMs = 180000);
}

public sealed class RustPythonWasmRunner : IRustPythonWasmRunner
{
    private readonly WasmerWasiRuntime _runtime = new();
    private string? _wasmPath;
    private bool _isInitialized;

    public string WasmPath => _wasmPath ?? throw new InvalidOperationException("RustPython WASM 尚未初始化。");

    public void Init(string? workspaceRoot = null)
    {
        if (_isInitialized)
            return;

        var wasmerPath = ResolveExistingFile(
            Path.Combine(AppContext.BaseDirectory, "wasmer.dll"),
            Path.Combine(AppContext.BaseDirectory, "libs", "wasmer-windows-amd64", "lib", "wasmer.dll"),
            workspaceRoot is null ? string.Empty : Path.Combine(workspaceRoot, "libs", "wasmer-windows-amd64", "lib", "wasmer.dll"),
            Path.Combine(Directory.GetCurrentDirectory(), "libs", "wasmer-windows-amd64", "lib", "wasmer.dll"));

        if (wasmerPath is null)
            throw new FileNotFoundException("未找到 Wasmer 原生库 wasmer.dll。");

        _wasmPath = ResolveExistingFile(
            Path.Combine(AppContext.BaseDirectory, "rustpython.wasm"),
            Path.Combine(AppContext.BaseDirectory, "libs", "rustpython.wasm"),
            workspaceRoot is null ? string.Empty : Path.Combine(workspaceRoot, "libs", "rustpython.wasm"),
            Path.Combine(Directory.GetCurrentDirectory(), "libs", "rustpython.wasm"));

        if (_wasmPath is null)
            throw new FileNotFoundException("未找到 rustpython.wasm。");

        NativeLibrary.Load(wasmerPath);
        _isInitialized = true;
    }

    public WasmPythonExecutionResult ExecuteCode(string code, string workingDirectory, int timeoutMs = 180000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!_isInitialized)
            Init(workingDirectory);

        var commandResult = _runtime.ExecuteCode(
            WasmPath,
            PrepareCode(code),
            workingDirectory,
            timeoutMs,
            ["PWD=/workspace"]);

        return new WasmPythonExecutionResult(
            Success: commandResult.Success,
            ExitCode: commandResult.ExitCode,
            Data: commandResult.StdOut,
            Error: commandResult.StdErr,
            NativeResultCode: commandResult.NativeResultCode,
            NativeResultMessage: commandResult.NativeResultMessage,
            TimedOut: commandResult.TimedOut);
    }

    public void Dispose()
    {
    }

    private static string PrepareCode(string code)
    {
        return """
            try:
                import os
                os.chdir("/workspace")
            except Exception:
                pass

            """ + code.TrimStart();
    }

    private static string? ResolveExistingFile(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }
}
