using sharpclaw.Commands;
using sharpclaw.Core;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sharpclaw.Services;

/// <summary>
/// 基于 Wasmtime + rustpython.wasm 的 Python 服务。
/// 相比 WasmPythonService (Wasmer)，使用 epoch 中断实现真正的执行超时取消。
/// 支持 Python 代码通过 call_agent() 调用 AI 智能体。
/// </summary>
public sealed class WasmtimePythonService : CommandBase, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<IRustPythonWasmRunner> _runnerFactory;
    private IRustPythonWasmRunner? _runner;
    private string? _workingDirectory;
    private bool _isInitialized;
    private IChatClient? _agentClient;

    /// <summary>Exit code used by the Python <c>call_agent()</c> function to signal an agent call request.</summary>
    private const int AgentCallExitCode = 42;

    /// <summary>Maximum number of agent calls allowed within a single <see cref="RunPython"/> invocation.</summary>
    private const int MaxAgentCallsPerExecution = 10;

    /// <summary>IPC directory name created under the workspace for agent call communication.</summary>
    private const string AgentIpcDirName = ".sharpclaw_ipc";

    /// <summary>
    /// Python preamble that defines the <c>call_agent(prompt, system_prompt="")</c> function.
    /// Uses file-based IPC with iterative re-execution to bridge the WASM sandbox and the host agent.
    /// <para>
    /// Protocol:
    /// <list type="number">
    ///   <item>On first call to <c>call_agent()</c>, writes a request to <c>/workspace/.sharpclaw_ipc/request.json</c> and exits with code 42.</item>
    ///   <item>The C# host detects exit code 42, reads the request, calls the LLM, and writes the response to <c>responses.json</c>.</item>
    ///   <item>The Python code is re-executed. On re-execution, <c>call_agent()</c> reads the cached response and returns it.</item>
    ///   <item>Multiple <c>call_agent()</c> calls are supported via an index-based response accumulation pattern.</item>
    /// </list>
    /// </para>
    /// </summary>
    private const string AgentPreamble =
        "import json as _json, os as _os, sys as _sys\n" +
        "\n" +
        "_SHARPCLAW_IPC = \"/workspace/.sharpclaw_ipc\"\n" +
        "_SHARPCLAW_REQ = _os.path.join(_SHARPCLAW_IPC, \"request.json\")\n" +
        "_SHARPCLAW_RESP = _os.path.join(_SHARPCLAW_IPC, \"responses.json\")\n" +
        "_sharpclaw_call_idx = 0\n" +
        "_sharpclaw_responses = []\n" +
        "if _os.path.exists(_SHARPCLAW_RESP):\n" +
        "    try:\n" +
        "        with open(_SHARPCLAW_RESP, \"r\") as _f:\n" +
        "            _sharpclaw_responses = _json.load(_f)\n" +
        "    except Exception:\n" +
        "        _sharpclaw_responses = []\n" +
        "\n" +
        "def call_agent(prompt, system_prompt=\"\"):\n" +
        "    # Call AI agent with a prompt and get a text response.\n" +
        "    # prompt: the question or instruction for the agent\n" +
        "    # system_prompt: optional system prompt to set agent behavior\n" +
        "    global _sharpclaw_call_idx\n" +
        "    if _sharpclaw_call_idx < len(_sharpclaw_responses):\n" +
        "        _r = _sharpclaw_responses[_sharpclaw_call_idx][\"result\"]\n" +
        "        _sharpclaw_call_idx += 1\n" +
        "        return _r\n" +
        "    _os.makedirs(_SHARPCLAW_IPC, exist_ok=True)\n" +
        "    with open(_SHARPCLAW_REQ, \"w\") as _f:\n" +
        "        _json.dump({\"prompt\": prompt, \"system_prompt\": system_prompt, \"call_index\": _sharpclaw_call_idx}, _f)\n" +
        "    _sys.exit(42)\n" +
        "\n";

    public WasmtimePythonService(IAgentContext agentContext, Func<IRustPythonWasmRunner>? runnerFactory = null)
        : base(agentContext.TaskManager, agentContext)
    {
        _runnerFactory = runnerFactory ?? (() => new WasmtimeRustPythonRunner());
    }

    /// <summary>
    /// Sets the AI client used for <c>call_agent()</c> calls from Python code.
    /// When set, the <c>call_agent(prompt, system_prompt)</c> function is injected into the Python environment.
    /// </summary>
    public void SetAgentClient(IChatClient client) => _agentClient = client;

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

            // Prepend agent call_agent() preamble if agent client is configured
            var preparedCode = _agentClient != null
                ? AgentPreamble + code.TrimStart()
                : code;

            await _lock.WaitAsync(ct);
            try
            {
                var ipcDir = Path.Combine(executionDirectory, AgentIpcDirName);
                var agentCallCount = 0;

                try
                {
                    while (true)
                    {
                        var result = _runner!.ExecuteCode(preparedCode, executionDirectory, timeOut * 1000);

                        // ── Check for agent call request (exit code 42 + request file) ──
                        if (!result.Success
                            && result.ExitCode == AgentCallExitCode
                            && _agentClient != null)
                        {
                            var requestFile = Path.Combine(ipcDir, "request.json");
                            if (File.Exists(requestFile))
                            {
                                if (agentCallCount >= MaxAgentCallsPerExecution)
                                {
                                    ctx.WriteStderrLine(
                                        $"ERROR: Maximum agent call count ({MaxAgentCallsPerExecution}) exceeded in single Python execution.");
                                    return 1;
                                }

                                agentCallCount++;

                                var request = JsonSerializer.Deserialize<AgentCallRequest>(
                                    await File.ReadAllTextAsync(requestFile, ct));

                                ctx.WriteStdoutLine(
                                    $"[Agent Call #{agentCallCount}] {Truncate(request?.Prompt, 120)}");

                                // Call the LLM
                                string responseText;
                                try
                                {
                                    var messages = new List<ChatMessage>();
                                    if (!string.IsNullOrEmpty(request?.SystemPrompt))
                                        messages.Add(new ChatMessage(ChatRole.System, request.SystemPrompt));
                                    messages.Add(new ChatMessage(ChatRole.User, request?.Prompt ?? ""));

                                    var completion = await _agentClient.GetResponseAsync(messages, cancellationToken: ct);
                                    responseText = completion.Text ?? "";
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    responseText = $"[Agent Error: {ex.GetType().Name}: {ex.Message}]";
                                }

                                // Append response to accumulated responses file
                                var responsesFile = Path.Combine(ipcDir, "responses.json");
                                List<AgentCallResponse> responses = [];
                                if (File.Exists(responsesFile))
                                {
                                    responses = JsonSerializer.Deserialize<List<AgentCallResponse>>(
                                        await File.ReadAllTextAsync(responsesFile, ct)) ?? [];
                                }
                                responses.Add(new AgentCallResponse { Result = responseText });
                                await File.WriteAllTextAsync(responsesFile,
                                    JsonSerializer.Serialize(responses), ct);

                                File.Delete(requestFile);
                                continue; // Re-execute Python code with updated responses
                            }
                        }

                        // ── Normal completion ───────────────────────────────────────
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

                        // ── Error ───────────────────────────────────────────────────
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
                }
                finally
                {
                    // Clean up IPC directory
                    try
                    {
                        if (Directory.Exists(ipcDir))
                            Directory.Delete(ipcDir, true);
                    }
                    catch { /* best effort */ }
                }
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

    private static string Truncate(string? s, int maxLength)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length <= maxLength ? s : s[..maxLength] + "...";
    }

    private sealed class AgentCallRequest
    {
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
        [JsonPropertyName("system_prompt")] public string SystemPrompt { get; set; } = "";
        [JsonPropertyName("call_index")] public int CallIndex { get; set; }
    }

    private sealed class AgentCallResponse
    {
        [JsonPropertyName("result")] public string Result { get; set; } = "";
    }
}
