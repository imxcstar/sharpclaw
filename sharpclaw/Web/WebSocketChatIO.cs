using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using sharpclaw.Abstractions;

namespace sharpclaw.Web;

/// <summary>
/// IChatIO 的 WebSocket 实现。
/// 通过 WebSocketSender 发送消息，内部用 Channel 桥接异步输入。
/// </summary>
public sealed class WebSocketChatIO : IChatIO, IAsyncDisposable
{
    private readonly WebSocketSender _sender;
    private readonly Channel<string> _inputChannel = Channel.CreateUnbounded<string>();
    private CancellationTokenSource? _aiCts;
    private readonly CancellationTokenSource _connectionCts = new();
    private Task? _receiveLoop;

    private WebSocket Ws => _sender.WebSocket;

    public WebSocketChatIO(WebSocketSender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// 启动接收循环，解析客户端消息。
    /// </summary>
    public void StartReceiving()
    {
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (Ws.State == WebSocketState.Open && !_connectionCts.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await Ws.ReceiveAsync(buffer, _connectionCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _connectionCts.Cancel();
                        _inputChannel.Writer.TryComplete();
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                HandleClientMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _inputChannel.Writer.TryComplete();
        }
    }

    private void HandleClientMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "input":
                    var text = doc.RootElement.GetProperty("text").GetString() ?? "";
                    _inputChannel.Writer.TryWrite(text);
                    break;
                case "cancel":
                    _aiCts?.Cancel();
                    break;
            }
        }
        catch (JsonException) { }
    }

    public Task WaitForReadyAsync() => Task.CompletedTask;

    public async Task<string> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        _sender.Send(new { type = "state", state = "input" });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _connectionCts.Token);
        return await _inputChannel.Reader.ReadAsync(linked.Token);
    }

    public void AppendChat(string text)
    {
        _sender.Send(new { type = "chat", text });
    }

    public void AppendChatLine(string text)
    {
        _sender.Send(new { type = "chatLine", text });
    }

    public void ShowRunning()
    {
        _sender.Send(new { type = "state", state = "running" });
    }

    public CancellationToken GetAiCancellationToken()
    {
        _aiCts = new CancellationTokenSource();
        return _aiCts.Token;
    }

    public void RequestStop()
    {
        _connectionCts.Cancel();
        if (Ws.State == WebSocketState.Open)
        {
            _ = Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stopped", CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _connectionCts.Cancel();
        if (_receiveLoop is not null)
            await _receiveLoop;
        _connectionCts.Dispose();
        _aiCts?.Dispose();
    }
}
