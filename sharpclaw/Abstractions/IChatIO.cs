namespace sharpclaw.Abstractions;

/// <summary>
/// AI 引擎与前端之间的 I/O 抽象。
/// TUI、WebSocket、REST 等前端各自实现此接口。
/// </summary>
public interface IChatIO
{
    /// <summary>等待前端就绪。</summary>
    Task WaitForReadyAsync();

    /// <summary>等待用户输入一条消息。</summary>
    Task<string> ReadInputAsync(CancellationToken cancellationToken = default);

    /// <summary>追加流式文本（AI 输出片段）。</summary>
    void AppendChat(string text);

    /// <summary>追加一行完整文本（如用户输入回显）。</summary>
    void AppendChatLine(string text);

    /// <summary>切换到"AI 运行中"状态。</summary>
    void ShowRunning();

    /// <summary>获取用于取消当前 AI 运行的 token。</summary>
    CancellationToken GetAiCancellationToken();

    /// <summary>请求停止整个应用。</summary>
    void RequestStop();
}
