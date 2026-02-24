using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace sharpclaw.Web;

/// <summary>
/// 线程安全的 WebSocket JSON 消息发送器。
/// 所有发送通过 Channel 排队，由单一后台循环串行写入，避免并发写入异常。
/// </summary>
public sealed class WebSocketSender : IAsyncDisposable
{
    private readonly WebSocket _ws;
    private readonly Channel<string> _sendQueue = Channel.CreateUnbounded<string>();
    private readonly Task _sendLoop;
    private readonly CancellationTokenSource _cts = new();

    public WebSocket WebSocket => _ws;

    public WebSocketSender(WebSocket ws)
    {
        _ws = ws;
        _sendLoop = Task.Run(SendLoopAsync);
    }

    public void Send(object message)
    {
        var json = JsonSerializer.Serialize(message);
        _sendQueue.Writer.TryWrite(json);
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var json in _sendQueue.Reader.ReadAllAsync(_cts.Token))
            {
                if (_ws.State != WebSocketState.Open) break;

                var bytes = Encoding.UTF8.GetBytes(json);
                try
                {
                    await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
                }
                catch (WebSocketException) { break; }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _sendQueue.Writer.TryComplete();
        _cts.Cancel();
        await _sendLoop;
        _cts.Dispose();
    }
}
