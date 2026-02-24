using sharpclaw.Abstractions;

namespace sharpclaw.Channels.Web;

/// <summary>
/// IAppLogger 的 WebSocket 实现。通过共享的 WebSocketSender 发送。
/// </summary>
public sealed class WebSocketLogger : IAppLogger
{
    private readonly WebSocketSender _sender;

    public WebSocketLogger(WebSocketSender sender)
    {
        _sender = sender;
    }

    public void Log(string message)
    {
        _sender.Send(new { type = "log", message });
    }

    public void SetStatus(string status)
    {
        _sender.Send(new { type = "status", text = status });
    }
}
