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

    /// <summary>Maximum number of agent calls allowed within a single <see cref="RunPython"/> invocation.</summary>
    private const int MaxAgentCallsPerExecution = 10;

    /// <summary>IPC directory name created under the workspace for agent call communication.</summary>
    private const string AgentIpcDirName = ".sharpclaw_ipc";

    /// <summary>
    /// Python preamble that defines the <c>call_agent(prompt, system_prompt="")</c> function.
    /// Uses file-based IPC with in-process polling to bridge the WASM sandbox and the host agent.
    /// <para>
    /// Protocol:
    /// <list type="number">
    ///   <item><c>call_agent()</c> writes a request to <c>/workspace/.sharpclaw_ipc/request_{index}.json</c> (atomic via tmp+rename).</item>
    ///   <item>Python polls for <c>response_{index}.json</c> in a sleep loop (100ms interval).</item>
    ///   <item>The C# host concurrently monitors the IPC directory, reads the request, calls the LLM, and writes the response atomically.</item>
    ///   <item>Python detects the response file, reads the result, cleans up, and returns — no re-execution needed.</item>
    /// </list>
    /// </para>
    /// </summary>
    private const string AgentPreamble =
        "import json as _json, os as _os\n" +
        "try:\n" +
        "    import time as _time\n" +
        "    _sharpclaw_sleep = _time.sleep\n" +
        "except Exception:\n" +
        "    def _sharpclaw_sleep(s):\n" +
        "        pass\n" +
        "\n" +
        "_SHARPCLAW_IPC = \"/workspace/.sharpclaw_ipc\"\n" +
        "_sharpclaw_call_idx = 0\n" +
        "\n" +
        "def call_agent(prompt, system_prompt=\"\"):\n" +
        "    # Call AI agent with a prompt and get a text response.\n" +
        "    # prompt: the question or instruction for the agent\n" +
        "    # system_prompt: optional system prompt to set agent behavior\n" +
        "    global _sharpclaw_call_idx\n" +
        "    _idx = _sharpclaw_call_idx\n" +
        "    _sharpclaw_call_idx += 1\n" +
        "    _os.makedirs(_SHARPCLAW_IPC, exist_ok=True)\n" +
        "    _req = _os.path.join(_SHARPCLAW_IPC, \"request_\" + str(_idx) + \".json\")\n" +
        "    _resp = _os.path.join(_SHARPCLAW_IPC, \"response_\" + str(_idx) + \".json\")\n" +
        "    _tmp = _req + \".tmp\"\n" +
        "    with open(_tmp, \"w\") as _f:\n" +
        "        _json.dump({\"prompt\": prompt, \"system_prompt\": system_prompt, \"call_index\": _idx}, _f)\n" +
        "    _os.rename(_tmp, _req)\n" +
        "    while not _os.path.exists(_resp):\n" +
        "        _sharpclaw_sleep(0.1)\n" +
        "    with open(_resp, \"r\") as _f:\n" +
        "        _result = _json.load(_f)[\"result\"]\n" +
        "    try:\n" +
        "        _os.remove(_resp)\n" +
        "    except Exception:\n" +
        "        pass\n" +
        "    return _result\n" +
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

                try
                {
                    // Ensure IPC directory exists before Python starts
                    if (_agentClient != null)
                        Directory.CreateDirectory(ipcDir);

                    // ── Run Python in background thread (ExecuteCode is blocking) ──
                    var pythonTask = Task.Run(
                        () => _runner!.ExecuteCode(preparedCode, executionDirectory, timeOut * 1000), ct);

                    // ── Concurrently monitor IPC directory for agent call requests ──
                    if (_agentClient != null)
                    {
                        var agentCallCount = 0;
                        var nextExpectedIndex = 0;

                        while (!pythonTask.IsCompleted)
                        {
                            if (agentCallCount >= MaxAgentCallsPerExecution)
                                break; // Stop monitoring; Python will eventually timeout waiting for response

                            var requestFile = Path.Combine(ipcDir, $"request_{nextExpectedIndex}.json");
                            if (File.Exists(requestFile))
                            {
                                agentCallCount++;

                                // Read request (with one retry in case of partial write)
                                AgentCallRequest? request = null;
                                try
                                {
                                    var json = await File.ReadAllTextAsync(requestFile, ct);
                                    request = JsonSerializer.Deserialize<AgentCallRequest>(json);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    await Task.Delay(100, ct);
                                    try
                                    {
                                        var json = await File.ReadAllTextAsync(requestFile, ct);
                                        request = JsonSerializer.Deserialize<AgentCallRequest>(json);
                                    }
                                    catch { /* give up on this request */ }
                                }

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

                                // Write response atomically (tmp + rename) so Python never reads a partial file
                                var responseFile = Path.Combine(ipcDir, $"response_{nextExpectedIndex}.json");
                                var tempFile = responseFile + ".tmp";
                                await File.WriteAllTextAsync(tempFile,
                                    JsonSerializer.Serialize(new AgentCallResponse { Result = responseText }), ct);
                                File.Move(tempFile, responseFile, overwrite: true);

                                // Clean up request file
                                try { File.Delete(requestFile); } catch { /* best effort */ }

                                nextExpectedIndex++;
                                continue; // Check for next request immediately
                            }

                            // Poll interval
                            try { await Task.Delay(50, ct); }
                            catch (OperationCanceledException) { break; }
                        }
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
