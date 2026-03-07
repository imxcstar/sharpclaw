using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace sharpclaw.Channels.Cli;

/// <summary>
/// CLI WebSocket 客户端。连接到 Sharpclaw Web 宿主的 /ws 端点。
/// 协议参考: docs/WEBSOCKET_PROTOCOL.md
/// </summary>
public sealed class CliClient : IDisposable
{
    private readonly string _serverUrl;
    private ClientWebSocket? _ws;
    private readonly CancellationTokenSource _stopCts = new();

    private readonly object _consoleLock = new();
    private CancellationTokenSource? _animationCts;
    private Task? _animationTask;

    private bool _inTextMode;
    private bool _hasOutputPrefix;
    private volatile string? _status;
    private volatile bool _acceptingInput;

    private static readonly bool SupportsColor = !Console.IsOutputRedirected;

    public CliClient(string serverUrl) { _serverUrl = serverUrl; }

    public async Task RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;

        _ws = new ClientWebSocket();
        try
        {
            Console.WriteLine($"[Cli] 正在连接到 {_serverUrl} ...");
            await _ws.ConnectAsync(new Uri(_serverUrl), _stopCts.Token);
            Console.WriteLine("[Cli] 已连接到 Sharpclaw 服务");
            Console.WriteLine("Sharpclaw CLI 模式（输入 /help 查看指令，/exit 退出）");
            Console.WriteLine(new string('-', 48));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cli] 连接失败: {ex.Message}");
            Console.WriteLine("[Cli] 请确保 Sharpclaw Web 服务正在运行（sharpclaw web）");
            return;
        }

        var receiveTask = Task.Run(ReceiveLoopAsync);
        await InputLoopAsync();
        try { await receiveTask; } catch { }

        StopAnimation();
        if (_ws.State == WebSocketState.Open)
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }

        Console.WriteLine("\n[Cli] 已退出。");
    }

    // ─────────────────────────────────────────────────
    // 输入循环
    // ─────────────────────────────────────────────────

    private async Task InputLoopAsync()
    {
        if (Console.IsInputRedirected) { await InputLoopRedirectedAsync(); return; }

        var sb = new StringBuilder();
        try
        {
            while (!_stopCts.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                if (!Console.KeyAvailable) { await Task.Delay(20); continue; }
                var key = Console.ReadKey(true);

                if (!_acceptingInput)
                {
                    if (key.Key == ConsoleKey.Escape)
                        await SendAsync(new { type = "cancel" });
                    continue;
                }

                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        var text = sb.ToString();
                        sb.Clear();

                        if (text is "/exit" or "/quit")
                        {
                            await SendAsync(new { type = "input", text });
                            _stopCts.Cancel();
                            return;
                        }

                        if (string.IsNullOrWhiteSpace(text)) break;
                        await SendAsync(new { type = "input", text });
                        break;

                    case ConsoleKey.Backspace:
                        if (sb.Length > 0)
                        {
                            var removed = sb[sb.Length - 1];
                            sb.Remove(sb.Length - 1, 1);
                            Console.Write(IsWideChar(removed) ? "\b\b  \b\b" : "\b \b");
                        }
                        break;

                    case ConsoleKey.Escape:
                        for (int j = 0; j < sb.Length; j++)
                            Console.Write(IsWideChar(sb[j]) ? "\b\b  \b\b" : "\b \b");
                        sb.Clear();
                        break;

                    default:
                        if (key.KeyChar >= ' ') { sb.Append(key.KeyChar); Console.Write(key.KeyChar); }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task InputLoopRedirectedAsync()
    {
        try
        {
            while (!_stopCts.IsCancellationRequested)
            {
                var line = Console.ReadLine();
                if (line is null) break;
                if (line is "/exit" or "/quit") { _stopCts.Cancel(); return; }
                if (!string.IsNullOrWhiteSpace(line))
                    await SendAsync(new { type = "input", text = line });
            }
        }
        catch { }
    }

    // ─────────────────────────────────────────────────
    // 接收循环 & 消息处理
    // ─────────────────────────────────────────────────

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (_ws?.State == WebSocketState.Open && !_stopCts.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _stopCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("\n[Cli] 服务端已断开连接");
                        _stopCts.Cancel();
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                HandleServerMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { Console.WriteLine($"\n[Cli] 接收异常: {ex.Message}"); }
    }

    private void HandleServerMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "aiChunk":
                    AppendChat(doc.RootElement.GetProperty("text").GetString() ?? "");
                    break;

                case "echo":
                    // 用户输入回显 — CLI 本地已显示，跳过
                    break;

                case "aiStart":
                    BeginAiResponse();
                    break;

                case "aiEnd":
                    // AI 回复结束，不需要额外处理
                    break;

                case "running":
                    ShowRunning();
                    break;

                case "inputReady":
                    ShowInput();
                    break;

                case "log":
                    // CLI 不显示日志
                    break;

                case "status":
                    UpdateStatus(doc.RootElement.GetProperty("text").GetString() ?? "");
                    break;

                case "commandResult":
                    HandleCommandResult(doc.RootElement);
                    break;

                case "error":
                    lock (_consoleLock) { Console.WriteLine($"\n[Error] {doc.RootElement.GetProperty("text").GetString()}"); }
                    break;
            }
        }
        catch (JsonException) { }
    }

    private void HandleCommandResult(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
        {
            lock (_consoleLock) { Console.WriteLine($"\n[{name}] 错误: {errEl.GetString()}"); }
            return;
        }

        if (root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
        {
            lock (_consoleLock) { Console.WriteLine($"\n{textEl.GetString()}"); }
            return;
        }

        if (root.TryGetProperty("data", out var dataEl))
        {
            lock (_consoleLock)
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                Console.WriteLine($"\n[{name}] {dataEl.GetRawText()}");
            }
        }
    }

    // ─────────────────────────────────────────────────
    // 终端 UI
    // ─────────────────────────────────────────────────

    private void BeginAiResponse()
    {
        lock (_consoleLock)
        {
            if (!_inTextMode)
            {
                _inTextMode = true;
                SetColor(ConsoleColor.Green);
                Console.Write("\r🤖 AI: ");
                EraseToEnd();
                ResetColor();
                _hasOutputPrefix = true;
            }
        }
    }

    private void ShowRunning()
    {
        StopAnimation();
        _acceptingInput = false;

        _animationCts = new CancellationTokenSource();
        var token = _animationCts.Token;

        lock (_consoleLock)
        {
            _inTextMode = false;
            _hasOutputPrefix = false;
            _status = null;
            RenderSpinnerLine("⠋");
        }

        _animationTask = Task.Run(async () =>
        {
            string[] frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
            int i = 1;
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(80, token); } catch (TaskCanceledException) { break; }
                lock (_consoleLock)
                {
                    if (_inTextMode) continue;
                    RenderSpinnerLine(frames[i++ % frames.Length]);
                }
            }
        }, token);
    }

    private void ShowInput()
    {
        StopAnimation();
        lock (_consoleLock)
        {
            _inTextMode = false;
            _hasOutputPrefix = false;
            _acceptingInput = true;
            ResetColor();
            Console.WriteLine();
            Console.WriteLine("────────────────────────────────────────");
            SetColor(ConsoleColor.Cyan);
            Console.Write("👤 User > ");
            Console.Out.Flush();
            ResetColor();
        }
    }

    private void UpdateStatus(string status)
    {
        lock (_consoleLock)
        {
            _status = status;
            if (_inTextMode)
            {
                Console.WriteLine();
                _inTextMode = false;
                RenderSpinnerLine("⠋");
            }
        }
    }

    private void AppendChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_consoleLock)
        {
            if (!_inTextMode)
            {
                _inTextMode = true;
                if (!_hasOutputPrefix)
                {
                    SetColor(ConsoleColor.Green);
                    Console.Write("\r🤖 AI: ");
                    EraseToEnd();
                    ResetColor();
                    _hasOutputPrefix = true;
                }
                else
                {
                    Console.Write('\r');
                    EraseToEnd();
                }
            }
            Console.Write(text);
            Console.Out.Flush();
        }
    }

    private void StopAnimation()
    {
        var cts = _animationCts; if (cts == null) return;
        cts.Cancel();
        try { _animationTask?.Wait(); } catch { }
        cts.Dispose();
        _animationCts = null;
        _animationTask = null;
    }

    private void RenderSpinnerLine(string frame)
    {
        SetColor(ConsoleColor.DarkYellow);
        var s = _status;
        Console.Write(string.IsNullOrEmpty(s) ? $"\r🤖 思考中 {frame} (Esc 取消)" : $"\r🤖 思考中 {frame} ({s}) (Esc 取消)");
        EraseToEnd();
        Console.Out.Flush();
        ResetColor();
    }

    private static bool IsWideChar(char c) =>
        (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) ||
        (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) ||
        (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF01 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6);

    private static void SetColor(ConsoleColor c) { if (SupportsColor) Console.ForegroundColor = c; }
    private static void ResetColor() { if (SupportsColor) Console.ResetColor(); }
    private static void EraseToEnd() { if (SupportsColor) Console.Write("\x1b[K"); }

    private async Task SendAsync(object message)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _stopCts.Token); } catch { }
    }

    public void Dispose()
    {
        _stopCts.Cancel();
        StopAnimation();
        _ws?.Dispose();
        _stopCts.Dispose();
    }
}
