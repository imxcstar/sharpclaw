using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using sharpclaw.Core;
using sharpclaw.UI;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace sharpclaw.Channels.Tui;

/// <summary>
/// TUI WebSocket 客户端。连接到 Sharpclaw Web 宿主，通过 Terminal.Gui 提供交互界面。
/// 协议参考: docs/WEBSOCKET_PROTOCOL.md
/// </summary>
public sealed class TuiClient : Runnable, IDisposable
{
    private readonly string _serverUrl;
    private ClientWebSocket? _ws;
    private readonly CancellationTokenSource _stopCts = new();

    private readonly FrameView _chatFrame;
    private readonly FrameView _logFrame;
    private readonly TextView _chatView;
    private readonly TextView _logView;
    private readonly TextField _inputField;
    private readonly Label _inputLabel;
    private readonly SpinnerView _spinner;
    private readonly Label _statusLabel;

    private bool _logCollapsed;
    private readonly TuiChannelConfig _tuiConfig;

    /// <summary>等待 getConfig 响应的回调。</summary>
    private Action<JsonElement>? _pendingConfigCallback;

    public TuiClient(string serverUrl, TuiChannelConfig? tuiConfig = null)
    {
        _tuiConfig = tuiConfig ?? new TuiChannelConfig();
        _serverUrl = serverUrl;
        _logCollapsed = _tuiConfig.LogCollapsed;

        Application.QuitKey = ChatWindow.ParseKey(_tuiConfig.QuitKey) ?? Key.Q.WithCtrl;
        Title = $"Sharpclaw ({Application.QuitKey} 退出)";

        // ── 对话区 ──
        _chatFrame = new FrameView
        {
            Title = "对话", X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = _logCollapsed ? Dim.Fill(1) : Dim.Percent(60),
        };
        _chatView = new TextView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ReadOnly = true, WordWrap = true, Text = "",
        };
        _chatFrame.Add(_chatView);

        // ── 日志区 ──
        _logFrame = new FrameView
        {
            Title = "日志", X = 0, Y = Pos.Bottom(_chatFrame),
            Width = Dim.Fill(), Height = Dim.Fill(1),
            Visible = !_logCollapsed,
        };
        _logView = new TextView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ReadOnly = true, WordWrap = true, Text = "",
        };
        _logFrame.Add(_logView);

        // ── 输入区 ──
        _inputLabel = new Label { Text = "> ", X = 0, Y = Pos.AnchorEnd(1) };
        _inputField = new TextField
        {
            X = Pos.Right(_inputLabel), Y = Pos.AnchorEnd(1), Width = Dim.Fill(),
        };
        _inputField.Autocomplete.SuggestionGenerator = new SlashCommandSuggestionGenerator(
            ["/exit", "/quit", "/help", "/config"]);
        _inputField.Autocomplete.SelectionKey = Key.Tab;
        _inputField.Accepting += OnInputAccepting;

        // ── 运行状态区 ──
        _spinner = new SpinnerView
        {
            X = 0, Y = Pos.AnchorEnd(1),
            Sequence = ["/", "-", "\\"], Visible = false,
        };
        _statusLabel = new Label
        {
            Text = " 连接中...", X = Pos.Right(_spinner),
            Y = Pos.AnchorEnd(1), Visible = false,
        };

        Add(_chatFrame, _logFrame, _inputLabel, _inputField, _spinner, _statusLabel);
        Initialized += (_, _) => _ = Task.Run(ConnectAndReceiveAsync);
    }

    // ─────────────────────────────────────────────────
    // WebSocket 连接 & 接收
    // ─────────────────────────────────────────────────

    private async Task ConnectAndReceiveAsync()
    {
        _ws = new ClientWebSocket();
        try
        {
            await _ws.ConnectAsync(new Uri(_serverUrl), _stopCts.Token);
            Invoke(() => AppendLog("[连接] 已连接到 Sharpclaw 服务"));
        }
        catch (Exception ex)
        {
            Invoke(() => AppendLog($"[连接] 连接失败: {ex.Message}"));
            return;
        }

        var buffer = new byte[4096];
        try
        {
            while (_ws.State == WebSocketState.Open && !_stopCts.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _stopCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Invoke(() => AppendLog("[连接] 服务端已断开"));
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                HandleServerMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    // ─────────────────────────────────────────────────
    // 消息处理
    // ─────────────────────────────────────────────────

    private void HandleServerMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "aiChunk":
                    var chunkText = doc.RootElement.GetProperty("text").GetString() ?? "";
                    Invoke(() => AppendChat(chunkText));
                    break;

                case "echo":
                    var echoText = doc.RootElement.GetProperty("text").GetString() ?? "";
                    Invoke(() => AppendChatLine(echoText));
                    break;

                case "aiStart":
                    Invoke(() => AppendChat("AI: "));
                    break;

                case "aiEnd":
                    break;

                case "running":
                    Invoke(ShowRunning);
                    break;

                case "inputReady":
                    Invoke(ShowInput);
                    break;

                case "log":
                    var logText = doc.RootElement.GetProperty("text").GetString() ?? "";
                    Invoke(() => AppendLog(logText));
                    break;

                case "status":
                    var statusText = doc.RootElement.GetProperty("text").GetString() ?? "";
                    Invoke(() => { _statusLabel.Text = $" {statusText} (Esc 取消)"; });
                    break;

                case "commandResult":
                    HandleCommandResult(doc.RootElement);
                    break;

                case "error":
                    var errText = doc.RootElement.GetProperty("text").GetString() ?? "";
                    Invoke(() => AppendLog($"[Error] {errText}"));
                    break;
            }
        }
        catch (JsonException) { }
    }

    private void HandleCommandResult(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        // 如果有 pending 的 getConfig 回调，触发它
        // 必须 Clone，因为 JsonDocument 在 HandleServerMessage 的 using 块结束后会被释放
        if (name == "getConfig" && _pendingConfigCallback is not null)
        {
            var cb = _pendingConfigCallback;
            _pendingConfigCallback = null;
            var cloned = root.Clone();
            Invoke(() => cb(cloned));
            return;
        }

        // 在 Invoke 之前提取所有值，因为 JsonDocument 将在方法返回后被释放
        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
        {
            var errMsg = errEl.GetString();
            Invoke(() => AppendLog($"[{name}] 错误: {errMsg}"));
            return;
        }

        if (root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
        {
            var textMsg = textEl.GetString() ?? "";
            Invoke(() => AppendChatLine(textMsg));
            return;
        }

        if (root.TryGetProperty("data", out var dataEl))
        {
            var dataStr = dataEl.GetRawText();
            Invoke(() => AppendLog($"[{name}] {dataStr}"));
        }
    }

    // ─────────────────────────────────────────────────
    // 输入处理
    // ─────────────────────────────────────────────────

    private void OnInputAccepting(object? sender, CommandEventArgs e)
    {
        var text = _inputField.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        e.Handled = true;
        _inputField.Text = "";
        _inputField.Enabled = false;

        if (text is "/exit" or "/quit")
        {
            RequestStop();
            return;
        }

        // /config (无参数) → 弹出 ConfigDialog 编辑远程配置
        if (text == "/config")
        {
            OpenRemoteConfigDialog();
            return;
        }

        _ = SendAsync(new { type = "input", text });
    }

    /// <summary>
    /// 通过 WebSocket 获取远程配置，弹出 ConfigDialog 编辑，保存时回写。
    /// </summary>
    private void OpenRemoteConfigDialog()
    {
        AppendLog("[Config] 正在获取远程配置...");

        _pendingConfigCallback = (root) =>
        {
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            {
                AppendLog($"[Config] 获取失败: {err.GetString()}");
                _inputField.Enabled = true;
                _inputField.SetFocus();
                return;
            }

            if (!root.TryGetProperty("data", out var dataEl))
            {
                AppendLog("[Config] 获取失败: 无配置数据");
                _inputField.Enabled = true;
                _inputField.SetFocus();
                return;
            }

            // 反序列化
            SharpclawConfig config;
            try
            {
                var jsonStr = dataEl.GetRawText();
                config = SharpclawConfig.Deserialize(jsonStr);
            }
            catch (Exception ex)
            {
                AppendLog($"[Config] 解析失败: {ex.Message}");
                _inputField.Enabled = true;
                _inputField.SetFocus();
                return;
            }

            // 弹出 ConfigDialog
            var dialog = new ConfigDialog();
            dialog.LoadFrom(config);
            dialog.SaveAction = savedConfig =>
            {
                // 序列化并通过 WebSocket 发送 setConfig
                try
                {
                    var savedJson = SharpclawConfig.SerializeToNode(savedConfig);
                    _ = SendAsync(new { type = "command", name = "setConfig", args = new { config = savedJson } });
                    AppendLog("[Config] 配置已发送到服务端保存");
                }
                catch (Exception ex)
                {
                    AppendLog($"[Config] 发送失败: {ex.Message}");
                }
            };

            App!.Run(dialog);
            dialog.Dispose();

            // 恢复输入
            _inputField.Enabled = true;
            _inputField.SetFocus();
        };

        // 发送 getConfig 请求
        _ = SendAsync(new { type = "command", name = "getConfig" });
    }

    // ─────────────────────────────────────────────────
    // UI 操作
    // ─────────────────────────────────────────────────

    private void Invoke(Action action)
    {
        if (App is { } app) app.Invoke(action);
    }

    private void ShowRunning()
    {
        _inputLabel.Visible = false;
        _inputField.Visible = false;
        _spinner.Visible = true;
        _spinner.AutoSpin = true;
        _statusLabel.Text = " AI 思考中... (Esc 取消)";
        _statusLabel.Visible = true;
    }

    private void ShowInput()
    {
        _spinner.AutoSpin = false;
        _spinner.Visible = false;
        _statusLabel.Visible = false;
        _inputLabel.Visible = true;
        _inputField.Visible = true;
        _inputField.Enabled = true;
        _inputField.Text = "";
        _inputField.SetFocus();
    }

    private void AppendChat(string text)
    {
        _chatView.Text += text.Replace('\t', ' ');
        _chatView.MoveEnd();
    }

    private void AppendChatLine(string text)
    {
        var current = _chatView.Text;
        _chatView.Text = string.IsNullOrEmpty(current) ? text.Replace('\t', ' ') : current + "\n" + text.Replace('\t', ' ');
        _chatView.MoveEnd();
    }

    private void AppendLog(string text)
    {
        var current = _logView.Text;
        _logView.Text = string.IsNullOrEmpty(current) ? text : current + "\n" + text;
        _logView.MoveEnd();
    }

    protected override bool OnKeyDown(Key key)
    {
        var cancelKey = ChatWindow.ParseKey(_tuiConfig.CancelKey) ?? Key.Esc;
        if (key == cancelKey)
        {
            _ = SendAsync(new { type = "cancel" });
            AppendLog("[用户] 已发送取消指令");
            return true;
        }

        var toggleLogKey = ChatWindow.ParseKey(_tuiConfig.ToggleLogKey) ?? Key.L.WithCtrl;
        if (key == toggleLogKey)
        {
            _logCollapsed = !_logCollapsed;
            _logFrame.Visible = !_logCollapsed;
            _chatFrame.Height = _logCollapsed ? Dim.Fill(1) : Dim.Percent(60);
            return true;
        }

        return base.OnKeyDown(key);
    }

    private async Task SendAsync(object message)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _stopCts.Token); } catch { }
    }

    public new void Dispose()
    {
        _stopCts.Cancel();
        _ws?.Dispose();
        _stopCts.Dispose();
    }
}
