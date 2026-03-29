using sharpclaw.Commands;
using sharpclaw.Core;
using System.ComponentModel;
using System.Text;

namespace sharpclaw.Services;

/// <summary>
/// 基于 Wasmer + rustpython.wasm 的 Python 服务。
/// </summary>
public sealed class WasmPythonService : CommandBase, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<IRustPythonWasmRunner> _runnerFactory;
    private IRustPythonWasmRunner? _runner;
    private string? _workingDirectory;
    private bool _isInitialized;

    public WasmPythonService(IAgentContext agentContext, Func<IRustPythonWasmRunner>? runnerFactory = null)
        : base(agentContext.TaskManager, agentContext)
    {
        _runnerFactory = runnerFactory ?? (() => new RustPythonWasmRunner());
    }

    public void Init()
    {
        if (_isInitialized)
            return;

        _workingDirectory = AgentContext.GetWorkspaceDirPath();
        _runner ??= _runnerFactory();
        _runner.Init(_workingDirectory);
        _isInitialized = true;
        Console.WriteLine($"[WasmPythonService] RustPython 路径: {_runner.WasmPath}");
    }

    [Description("执行 Python 代码。代码会在 /workspace 中运行。")]
    public string RunPython(
        [Description("python 代码，建议打印出字符串，方便接收信息")] string code,
        [Description("执行这个代码的目的，需要达成什么效果（必填）")] string purpose,
        [Description("运行超时时间，单位：秒，默认：180 秒 (选填)")] int timeOut = 180,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        return RunNative("RunWasmPython", (ctx, ct) =>
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                ctx.WriteStderrLine("ERROR: empty code");
                return Task.FromResult(1);
            }

            Init();

            var executionDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? _workingDirectory ?? GetDefaultWorkspace()
                : workingDirectory;

            _lock.Wait(ct);
            try
            {
                var result = _runner!.ExecuteCode(code, executionDirectory, timeOut * 1000);
                if (result.Success)
                {
                    var builder = new StringBuilder();
                    if (!string.IsNullOrEmpty(result.Data))
                        builder.Append(result.Data);
                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        if (builder.Length > 0)
                            builder.AppendLine();

                        builder.Append("STDERR:\n").Append(result.Error);
                    }

                    ctx.WriteStdoutLine(builder.Length == 0 ? "OK" : builder.ToString());
                    return Task.FromResult(0);
                }

                var errorBuilder = new StringBuilder();
                if (result.TimedOut)
                    errorBuilder.Append(result.NativeResultMessage);
                else
                    errorBuilder.Append($"Exit code: {result.ExitCode}, Runtime code: {result.NativeResultCode}, Message: {result.NativeResultMessage}");

                if (!string.IsNullOrEmpty(result.Error))
                    errorBuilder.Append("\n\nSTDERR:\n").Append(result.Error);

                ctx.WriteStderrLine($"ERROR: {errorBuilder}");
                return Task.FromResult(1);
            }
            finally
            {
                _lock.Release();
            }
        }, true, timeOut * 1000);
    }

    public void Dispose()
    {
        _runner?.Dispose();
        _lock.Dispose();
    }
}
