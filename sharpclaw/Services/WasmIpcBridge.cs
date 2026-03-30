using System.Text.Json;
using System.Text.Json.Serialization;

namespace sharpclaw.Services;

/// <summary>
/// 通用的文件 IPC 桥接器，用于 WASM 沙箱 (guest) 与 C# 宿主 (host) 之间的双向通信。
/// <para>
/// 协议约定：
/// <list type="bullet">
///   <item>请求文件: <c>request_{index}.json</c> → <c>{"type": "...", "payload": {...}, "call_index": N}</c></item>
///   <item>响应文件: <c>response_{index}.json</c> → handler 定义的 JSON 对象</item>
///   <item>双端使用原子写入 (tmp + rename) 防止读取到不完整的文件。</item>
///   <item>未注册的 type 或 handler 异常统一返回 <c>{"_error": "..."}</c>，guest 端可据此抛出异常。</item>
/// </list>
/// </para>
/// </summary>
public sealed class WasmIpcBridge
{
    /// <summary>
    /// IPC 请求处理委托。
    /// </summary>
    /// <param name="payload">请求载荷（原始 <see cref="JsonElement"/>，由 handler 自行解析）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>响应对象，序列化为 JSON 写入响应文件。返回 <c>null</c> 等价于 <c>{}</c>。</returns>
    public delegate Task<object?> IpcRequestHandler(JsonElement payload, CancellationToken ct);

    private readonly Dictionary<string, IpcRequestHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>IPC 目录默认名称（位于工作区根目录下）。</summary>
    public const string DefaultIpcDirName = ".sharpclaw_ipc";

    /// <summary>单次 guest 执行允许的最大 IPC 调用次数，默认 10。</summary>
    public int MaxCallsPerExecution { get; set; } = 10;

    /// <summary>轮询新请求文件的间隔（毫秒），默认 50。</summary>
    public int PollIntervalMs { get; set; } = 50;

    /// <summary>是否已注册至少一个 handler。</summary>
    public bool HasHandlers => _handlers.Count > 0;

    // ── Handler registration ────────────────────────────────────────

    /// <summary>
    /// 注册（或替换）指定 <paramref name="messageType"/> 的 IPC 处理函数。
    /// </summary>
    public void RegisterHandler(string messageType, IpcRequestHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[messageType] = handler;
    }

    /// <summary>
    /// 移除指定 <paramref name="messageType"/> 的 IPC 处理函数。
    /// </summary>
    public bool RemoveHandler(string messageType) => _handlers.Remove(messageType);

    // ── Monitor loop ────────────────────────────────────────────────

    /// <summary>
    /// 在 <paramref name="guestTask"/> 运行期间持续监控 <paramref name="ipcDir"/>，
    /// 分发请求到已注册的 handler 并写回响应。
    /// <para>当 guest 任务完成、调用次数达到上限或 <paramref name="ct"/> 被取消时返回。</para>
    /// </summary>
    /// <param name="ipcDir">宿主侧 IPC 目录绝对路径。</param>
    /// <param name="guestTask">正在运行的 WASM guest 任务（用于检测完成状态）。</param>
    /// <param name="onRequest">
    /// 可选回调，每检测到一个请求时触发，参数依次为：调用序号、消息类型、载荷。
    /// 用于日志或 UI 反馈，不影响处理流程。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    public async Task MonitorAsync(
        string ipcDir,
        Task guestTask,
        Action<int, string, JsonElement>? onRequest,
        CancellationToken ct)
    {
        Directory.CreateDirectory(ipcDir);

        var callCount = 0;
        var nextIndex = 0;

        while (!guestTask.IsCompleted)
        {
            if (callCount >= MaxCallsPerExecution)
                break;

            var requestFile = Path.Combine(ipcDir, $"request_{nextIndex}.json");
            if (File.Exists(requestFile))
            {
                callCount++;

                // ── Read request (with one retry for atomic-write race) ─────
                IpcRequest? request = null;
                try
                {
                    var json = await File.ReadAllTextAsync(requestFile, ct);
                    request = JsonSerializer.Deserialize<IpcRequest>(json);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Task.Delay(100, ct);
                    try
                    {
                        var json = await File.ReadAllTextAsync(requestFile, ct);
                        request = JsonSerializer.Deserialize<IpcRequest>(json);
                    }
                    catch { /* give up on this request */ }
                }

                var msgType = request?.Type ?? "unknown";
                onRequest?.Invoke(callCount, msgType, request?.Payload ?? default);

                // ── Dispatch to handler ─────────────────────────────────────
                object? responseData;
                if (_handlers.TryGetValue(msgType, out var handler))
                {
                    try
                    {
                        responseData = await handler(request?.Payload ?? default, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        responseData = new Dictionary<string, string>
                        {
                            ["_error"] = $"{ex.GetType().Name}: {ex.Message}"
                        };
                    }
                }
                else
                {
                    responseData = new Dictionary<string, string>
                    {
                        ["_error"] = $"Unknown IPC message type: {msgType}"
                    };
                }

                // ── Write response atomically (tmp → rename) ────────────────
                var responseFile = Path.Combine(ipcDir, $"response_{nextIndex}.json");
                var tempFile = responseFile + ".tmp";
                await File.WriteAllTextAsync(tempFile,
                    JsonSerializer.Serialize(responseData ?? new object()), ct);
                File.Move(tempFile, responseFile, overwrite: true);

                // Clean up request file
                try { File.Delete(requestFile); }
                catch { /* best effort */ }

                nextIndex++;
                continue; // Check next index immediately
            }

            try { await Task.Delay(PollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Cleanup ─────────────────────────────────────────────────────

    /// <summary>尽力删除 IPC 目录及其内容。</summary>
    public static void CleanupIpcDir(string ipcDir)
    {
        try
        {
            if (Directory.Exists(ipcDir))
                Directory.Delete(ipcDir, true);
        }
        catch { /* best effort */ }
    }

    // ── Protocol model ──────────────────────────────────────────────

    /// <summary>Guest 发出的 IPC 请求。</summary>
    public sealed class IpcRequest
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("payload")] public JsonElement Payload { get; set; }
        [JsonPropertyName("call_index")] public int CallIndex { get; set; }
    }
}
