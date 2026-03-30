using sharpclaw.Commands;
using sharpclaw.Core;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace sharpclaw.Services;

/// <summary>
/// 基于 Wasmtime + rustpython.wasm 的 Python 服务。
/// 使用 epoch 中断实现真正的执行超时取消。
/// 通过通用 <see cref="WasmIpcBridge"/> IPC 机制与宿主通信，内置 <c>call_agent()</c> 支持，
/// 并可通过 <see cref="RegisterIpcHandler"/> 扩展更多通信功能。
/// </summary>
public sealed class WasmtimePythonService : CommandBase, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<IRustPythonWasmRunner> _runnerFactory;
    private readonly WasmIpcBridge _bridge = new();
    private readonly Dictionary<string, string> _pythonSnippets = new(StringComparer.OrdinalIgnoreCase);
    private IRustPythonWasmRunner? _runner;
    private string? _workingDirectory;
    private bool _isInitialized;

    // ── Python preamble: core IPC infrastructure ────────────────────

    /// <summary>
    /// 核心 Python IPC 前导代码，定义通用传输函数 <c>_sharpclaw_ipc_call(msg_type, payload)</c>。
    /// <para>
    /// 所有具体功能（如 <c>call_agent</c>）都是在此基础上的薄封装。
    /// 该函数负责：写请求文件（原子写入）→ 轮询等待响应文件 → 读取响应 → 错误检查 → 返回。
    /// </para>
    /// </summary>
    private const string IpcCorePreamble =
        "import json as _json, os as _os\n" +
        "try:\n" +
        "    import time as _time\n" +
        "    _sharpclaw_sleep = _time.sleep\n" +
        "except Exception:\n" +
        "    def _sharpclaw_sleep(s):\n" +
        "        pass\n" +
        "\n" +
        "_SHARPCLAW_IPC = \"/workspace/" + WasmIpcBridge.DefaultIpcDirName + "\"\n" +
        "_sharpclaw_call_idx = 0\n" +
        "\n" +
        "def _sharpclaw_ipc_call(_type, _payload):\n" +
        "    \"\"\"Send a typed IPC request to the host and block until the response arrives.\"\"\"\n" +
        "    global _sharpclaw_call_idx\n" +
        "    _idx = _sharpclaw_call_idx\n" +
        "    _sharpclaw_call_idx += 1\n" +
        "    _os.makedirs(_SHARPCLAW_IPC, exist_ok=True)\n" +
        "    _req = _os.path.join(_SHARPCLAW_IPC, \"request_\" + str(_idx) + \".json\")\n" +
        "    _resp = _os.path.join(_SHARPCLAW_IPC, \"response_\" + str(_idx) + \".json\")\n" +
        "    _tmp = _req + \".tmp\"\n" +
        "    with open(_tmp, \"w\") as _f:\n" +
        "        _json.dump({\"type\": _type, \"payload\": _payload, \"call_index\": _idx}, _f)\n" +
        "    _os.rename(_tmp, _req)\n" +
        "    while not _os.path.exists(_resp):\n" +
        "        _sharpclaw_sleep(0.1)\n" +
        "    with open(_resp, \"r\") as _f:\n" +
        "        _resp_data = _json.load(_f)\n" +
        "    try:\n" +
        "        _os.remove(_resp)\n" +
        "    except Exception:\n" +
        "        pass\n" +
        "    if \"_error\" in _resp_data:\n" +
        "        raise RuntimeError(\"IPC error [\" + _type + \"]: \" + str(_resp_data[\"_error\"]))\n" +
        "    return _resp_data\n" +
        "\n";

    // ── Python preamble: call_agent wrapper ─────────────────────────

    /// <summary>
    /// <c>call_agent(prompt, system_prompt="")</c> 的 Python 封装，基于 <c>_sharpclaw_ipc_call</c>。
    /// </summary>
    private const string AgentCallPreamble =
        "def call_agent(prompt, system_prompt=\"\"):\n" +
        "    \"\"\"Call AI agent with a prompt and get a text response.\"\"\"\n" +
        "    _resp = _sharpclaw_ipc_call(\"call_agent\", {\"prompt\": prompt, \"system_prompt\": system_prompt})\n" +
        "    return _resp.get(\"result\", \"\")\n" +
        "\n";

    // ── Construction & initialization ───────────────────────────────

    public WasmtimePythonService(IAgentContext agentContext, Func<IRustPythonWasmRunner>? runnerFactory = null)
        : base(agentContext.TaskManager, agentContext)
    {
        _runnerFactory = runnerFactory ?? (() => new WasmtimeRustPythonRunner());
    }

    /// <summary>
    /// 配置 AI 客户端并注册 <c>call_agent</c> IPC handler。
    /// 注册后 Python 代码即可调用 <c>call_agent(prompt, system_prompt)</c>。
    /// </summary>
    public void SetAgentClient(IChatClient client)
    {
        _bridge.RegisterHandler("call_agent", async (payload, ct) =>
        {
            var prompt = payload.ValueKind != JsonValueKind.Undefined
                         && payload.TryGetProperty("prompt", out var p)
                ? p.GetString() ?? "" : "";
            var systemPrompt = payload.ValueKind != JsonValueKind.Undefined
                               && payload.TryGetProperty("system_prompt", out var sp)
                ? sp.GetString() ?? "" : "";

            string responseText;
            try
            {
                var messages = new List<ChatMessage>();
                if (!string.IsNullOrEmpty(systemPrompt))
                    messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
                messages.Add(new ChatMessage(ChatRole.User, prompt));

                var completion = await client.GetResponseAsync(messages, cancellationToken: ct);
                responseText = completion.Text ?? "";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                responseText = $"[Agent Error: {ex.GetType().Name}: {ex.Message}]";
            }

            return new { result = responseText };
        });

        _pythonSnippets["call_agent"] = AgentCallPreamble;
    }

    /// <summary>
    /// 注册自定义 IPC handler 并附带可选的 Python 封装函数。
    /// <para>扩展示例：
    /// <code>
    /// service.RegisterIpcHandler("search_web",
    ///     async (payload, ct) =&gt; new { results = new[] { "..." } },
    ///     "def search_web(query):\n" +
    ///     "    return _sharpclaw_ipc_call(\"search_web\", {\"query\": query})\n\n");
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="messageType">IPC 消息类型标识，与 Python 端 <c>_sharpclaw_ipc_call</c> 的第一个参数对应。</param>
    /// <param name="handler">C# 端的处理委托，接收 <see cref="JsonElement"/> payload 并返回可序列化的响应对象。</param>
    /// <param name="pythonWrapper">
    /// 可选的 Python 封装函数代码。会被追加到 IPC 前导代码之后，供 Python 用户直接调用。
    /// 应以换行符结尾。
    /// </param>
    public void RegisterIpcHandler(
        string messageType,
        WasmIpcBridge.IpcRequestHandler handler,
        string? pythonWrapper = null)
    {
        _bridge.RegisterHandler(messageType, handler);
        if (!string.IsNullOrEmpty(pythonWrapper))
            _pythonSnippets[messageType] = pythonWrapper;
    }

    public void Init()
    {
        if (_isInitialized)
            return;

        _workingDirectory = AgentContext.GetWorkspaceDirPath();
        _runner ??= _runnerFactory();
        _runner.Init(_workingDirectory);
        _isInitialized = true;
        Console.WriteLine($"[WasmtimePythonService] RustPython 路径: {_runner.WasmPath}");
    }

    // ── Main execution ──────────────────────────────────────────────

    [Description("执行 Python 代码 (Wasmtime 运行时)。代码会在 /workspace 中运行，支持 epoch 超时中断。代码中可直接调用 call_agent(prompt, system_prompt=\"\") 函数来请求 AI 智能体处理问题并获取文本回复。")]
    public string RunPython(
        [Description("python 代码，建议打印出字符串，方便接收信息。可使用内置函数 call_agent(prompt, system_prompt) 调用 AI 智能体。")] string code,
        [Description("执行这个代码的目的，需要达成什么效果（必填）")] string purpose,
        [Description("运行超时时间，单位：秒，默认：180 秒 (选填)")] int timeOut = 180,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        return RunNative("RunWasmtimePython", async (ctx, ct) =>
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                ctx.WriteStderrLine("ERROR: empty code");
                return 1;
            }

            Init();

            var executionDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? _workingDirectory ?? GetDefaultWorkspace()
                : workingDirectory;

            // Build Python code: IPC core preamble + feature wrappers + user code
            var preparedCode = _bridge.HasHandlers
                ? BuildPreamble() + code.TrimStart()
                : code;

            await _lock.WaitAsync(ct);
            try
            {
                var ipcDir = Path.Combine(executionDirectory, WasmIpcBridge.DefaultIpcDirName);

                try
                {
                    // ── Run Python in background thread (ExecuteCode is blocking) ──
                    var pythonTask = Task.Run(
                        () => _runner!.ExecuteCode(preparedCode, executionDirectory, timeOut * 1000), ct);

                    // ── Concurrently monitor IPC directory via bridge ────────────
                    if (_bridge.HasHandlers)
                    {
                        await _bridge.MonitorAsync(ipcDir, pythonTask,
                            (count, type, payload) =>
                            {
                                var preview = type;
                                if (payload.ValueKind != JsonValueKind.Undefined
                                    && payload.TryGetProperty("prompt", out var p))
                                    preview = Truncate(p.GetString(), 120);

                                ctx.WriteStdoutLine($"[IPC #{count} {type}] {preview}");
                            }, ct);
                    }

                    // ── Await Python completion ─────────────────────────────────
                    WasmPythonExecutionResult result;
                    try
                    {
                        result = await pythonTask;
                    }
                    catch (OperationCanceledException)
                    {
                        ctx.WriteStderrLine("ERROR: Execution cancelled.");
                        return 1;
                    }

                    // ── Normal completion ────────────────────────────────────────
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
                        return 0;
                    }

                    // ── Error ────────────────────────────────────────────────────
                    var errorBuilder = new StringBuilder();
                    if (result.TimedOut)
                        errorBuilder.Append(result.NativeResultMessage);
                    else
                        errorBuilder.Append($"Exit code: {result.ExitCode}, Runtime code: {result.NativeResultCode}, Message: {result.NativeResultMessage}");

                    if (!string.IsNullOrEmpty(result.Error))
                        errorBuilder.Append("\n\nSTDERR:\n").Append(result.Error);

                    ctx.WriteStderrLine($"ERROR: {errorBuilder}");
                    return 1;
                }
                finally
                {
                    WasmIpcBridge.CleanupIpcDir(ipcDir);
                }
            }
            finally
            {
                _lock.Release();
            }
        }, true, timeOut * 1000);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    public void Dispose()
    {
        _runner?.Dispose();
        _lock.Dispose();
    }

    /// <summary>
    /// 组合 IPC 核心前导 + 所有已注册功能的 Python 封装代码。
    /// </summary>
    private string BuildPreamble()
    {
        var sb = new StringBuilder(IpcCorePreamble);
        foreach (var snippet in _pythonSnippets.Values)
            sb.Append(snippet);
        return sb.ToString();
    }

    private static string Truncate(string? s, int maxLength)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length <= maxLength ? s : s[..maxLength] + "...";
    }
}
