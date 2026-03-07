using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using sharpclaw.Abstractions;
using sharpclaw.Core;

namespace sharpclaw.Channels.Web;

/// <summary>
/// IChatIO 的 WebSocket 实现。
/// 通过 WebSocketSender 发送消息，内部用 Channel 桥接异步输入。
/// 协议参考: docs/WEBSOCKET_PROTOCOL.md
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

    /// <summary>启动接收循环，解析客户端消息。</summary>
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

    // ─────────────────────────────────────────────────
    // 客户端消息处理
    // ─────────────────────────────────────────────────

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

                case "command":
                    HandleCommand(doc.RootElement);
                    break;
            }
        }
        catch (JsonException) { }
    }

    private void HandleCommand(JsonElement root)
    {
        var name = root.GetProperty("name").GetString() ?? "";
        JsonElement args = default;
        root.TryGetProperty("args", out args);

        switch (name)
        {
            case "help":
                SendCommandResult("help", text: """
                    可用指令:
                      /help                          - 显示帮助
                      /config                        - 查看配置摘要
                      /config set <路径> <值>          - 修改单个配置项
                      /exit                          - 断开连接

                    编程指令 (command):
                      help, config, getConfig, setConfig, configSet, exit

                    configSet 示例:
                      /config set agents.recaller.enabled false
                      /config set channels.web.port 8080
                      /config set default.model claude-opus-4-6
                    """);
                break;

            case "config":
                SendCommandResult("config", text: FormatConfigSummary());
                break;

            case "getConfig":
                HandleGetConfig();
                break;

            case "setConfig":
                HandleSetConfig(args);
                break;

            case "configSet":
                HandleConfigSet(args);
                break;

            case "exit":
                RequestStop();
                break;

            default:
                SendCommandResult(name, error: $"未知指令: {name}");
                break;
        }
    }

    // ─────────────────────────────────────────────────
    // 配置指令
    // ─────────────────────────────────────────────────

    private void HandleGetConfig()
    {
        try
        {
            if (!File.Exists(SharpclawConfig.ConfigPath))
            {
                SendCommandResult("getConfig", error: "配置文件不存在");
                return;
            }
            var rawJson = File.ReadAllText(SharpclawConfig.ConfigPath);
            var configNode = JsonNode.Parse(rawJson);
            SendCommandResult("getConfig", data: configNode);
        }
        catch (Exception ex)
        {
            SendCommandResult("getConfig", error: ex.Message);
        }
    }

    private void HandleSetConfig(JsonElement args)
    {
        try
        {
            if (args.ValueKind == JsonValueKind.Undefined || !args.TryGetProperty("config", out var configEl))
            {
                SendCommandResult("setConfig", error: "缺少 config 字段");
                return;
            }

            var dir = SharpclawConfig.SharpclawDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(SharpclawConfig.ConfigPath, configEl.GetRawText());
            SendCommandResult("setConfig", data: JsonNode.Parse("{\"success\":true}"));
        }
        catch (Exception ex)
        {
            SendCommandResult("setConfig", error: ex.Message);
        }
    }

    private void HandleConfigSet(JsonElement args)
    {
        try
        {
            if (args.ValueKind == JsonValueKind.Undefined
                || !args.TryGetProperty("path", out var pathEl)
                || !args.TryGetProperty("value", out var valueEl))
            {
                SendCommandResult("configSet", error: "缺少 path 或 value 字段");
                return;
            }

            var path = pathEl.GetString() ?? "";
            if (string.IsNullOrEmpty(path))
            {
                SendCommandResult("configSet", error: "path 不能为空");
                return;
            }

            // 读取 → 修改 → 写回
            if (!File.Exists(SharpclawConfig.ConfigPath))
            {
                SendCommandResult("configSet", error: "配置文件不存在");
                return;
            }

            var root = JsonNode.Parse(File.ReadAllText(SharpclawConfig.ConfigPath));
            if (root is null)
            {
                SendCommandResult("configSet", error: "配置文件解析失败");
                return;
            }

            // 按点分路径定位节点
            var segments = path.Split('.');
            JsonNode current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                // 不区分大小写查找属性
                var obj = current.AsObject();
                JsonNode? child = null;
                string? matchedKey = null;
                foreach (var kv in obj)
                {
                    if (string.Equals(kv.Key, seg, StringComparison.OrdinalIgnoreCase))
                    {
                        child = kv.Value;
                        matchedKey = kv.Key;
                        break;
                    }
                }

                if (child is null)
                {
                    SendCommandResult("configSet", error: $"路径不存在: {string.Join('.', segments[..(i + 1)])}");
                    return;
                }
                current = child;
            }

            // 设置最后一个属性
            var lastSeg = segments[^1];
            var parentObj = current.AsObject();
            string? lastKey = null;
            foreach (var kv in parentObj)
            {
                if (string.Equals(kv.Key, lastSeg, StringComparison.OrdinalIgnoreCase))
                {
                    lastKey = kv.Key;
                    break;
                }
            }

            if (lastKey is null)
            {
                SendCommandResult("configSet", error: $"属性不存在: {path}");
                return;
            }

            // 解析值：尝试 JSON 解析，失败则作为字符串
            JsonNode? newValue;
            var rawValue = valueEl.GetRawText().Trim('"');
            try
            {
                newValue = JsonNode.Parse(rawValue);
            }
            catch
            {
                newValue = System.Text.Json.Nodes.JsonValue.Create(rawValue);
            }

            parentObj[lastKey] = newValue;

            // 写回
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(SharpclawConfig.ConfigPath, root.ToJsonString(opts));

            SendCommandResult("configSet", text: $"已设置 {path} = {rawValue}（重启后生效）");
        }
        catch (Exception ex)
        {
            SendCommandResult("configSet", error: ex.Message);
        }
    }

    private static string FormatConfigSummary()
    {
        if (!File.Exists(SharpclawConfig.ConfigPath))
            return "配置文件不存在。请使用 'sharpclaw config' 完成初始配置。";

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(SharpclawConfig.ConfigPath));
            if (json is null) return "配置文件解析失败。";

            var sb = new StringBuilder();
            sb.AppendLine("── 当前配置 ──");
            sb.AppendLine($"版本: v{json["version"]}");

            var def = json["default"];
            if (def is not null)
            {
                sb.AppendLine($"默认提供商: {def["provider"]}  模型: {def["model"]}");
                sb.AppendLine($"端点: {(string.IsNullOrEmpty(def["endpoint"]?.GetValue<string>()) ? "(默认)" : def["endpoint"])}");
            }

            var agents = json["agents"];
            if (agents is not null)
            {
                sb.Append("智能体: ");
                foreach (var kv in agents.AsObject())
                    sb.Append($"{kv.Key}={IsEnabled(kv.Value)} ");
                sb.AppendLine();
            }

            var mem = json["memory"];
            if (mem is not null)
                sb.AppendLine($"向量记忆: {(mem["enabled"]?.GetValue<bool>() == true ? "启用" : "禁用")}");

            var ch = json["channels"];
            if (ch is not null)
            {
                var web = ch["web"];
                if (web is not null)
                    sb.AppendLine($"Web: {web["listenAddress"]}:{web["port"]}");
                var qq = ch["qqBot"];
                if (qq is not null)
                    sb.AppendLine($"QQBot: {(qq["enabled"]?.GetValue<bool>() == true ? "启用" : "禁用")}");
            }

            return sb.ToString();

            static string IsEnabled(JsonNode? node) =>
                node?["enabled"]?.GetValue<bool>() != false ? "✓" : "✗";
        }
        catch (Exception ex)
        {
            return $"读取配置失败: {ex.Message}";
        }
    }

    // ─────────────────────────────────────────────────
    // 发送消息
    // ─────────────────────────────────────────────────

    private void SendCommandResult(string name, object? data = null, string? text = null, string? error = null)
    {
        _sender.Send(new { type = "commandResult", name, data, text, error });
    }

    public Task WaitForReadyAsync() => Task.CompletedTask;

    public async Task<string> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        _sender.Send(new { type = "inputReady" });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _connectionCts.Token);
        return await _inputChannel.Reader.ReadAsync(linked.Token);
    }

    public void AppendChat(string text)
    {
        _sender.Send(new { type = "aiChunk", text });
    }

    public void AppendChatLine(string text)
    {
        // 用于 MainAgent 内部非命令文本输出
        _sender.Send(new { type = "echo", text });
    }

    public void EchoUserInput(string input)
    {
        _sender.Send(new { type = "echo", text = $"> {input}\n" });
    }

    public void BeginAiResponse()
    {
        _sender.Send(new { type = "aiStart" });
    }

    public void ShowRunning()
    {
        _sender.Send(new { type = "running" });
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

    public Task<CommandResult> HandleCommandAsync(string input)
    {
        // 服务端斜杠命令 → 转为 command 处理
        if (input.StartsWith('/'))
        {
            var parts = input[1..].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Task.FromResult(CommandResult.Handled);

            var cmdName = parts[0].ToLowerInvariant();

            switch (cmdName)
            {
                case "exit" or "quit":
                    RequestStop();
                    return Task.FromResult(CommandResult.Exit);

                case "help":
                    HandleCommand(JsonDocument.Parse("{\"name\":\"help\"}").RootElement);
                    return Task.FromResult(CommandResult.Handled);

                case "config":
                    if (parts.Length >= 3 && parts[1].Equals("set", StringComparison.OrdinalIgnoreCase))
                    {
                        // /config set path value
                        var pathAndValue = input[(input.IndexOf("set", StringComparison.OrdinalIgnoreCase) + 4)..].Trim();
                        var spaceIdx = pathAndValue.IndexOf(' ');
                        if (spaceIdx > 0)
                        {
                            var path = pathAndValue[..spaceIdx].Trim();
                            var value = pathAndValue[(spaceIdx + 1)..].Trim();
                            // 构造 configSet 调用
                            var argsJson = $"{{\"path\":\"{path}\",\"value\":{ToJsonLiteral(value)}}}";
                            var fakeRoot = JsonDocument.Parse($"{{\"name\":\"configSet\",\"args\":{argsJson}}}").RootElement;
                            HandleCommand(fakeRoot);
                        }
                        else
                        {
                            SendCommandResult("configSet", error: "用法: /config set <路径> <值>");
                        }
                    }
                    else
                    {
                        HandleCommand(JsonDocument.Parse("{\"name\":\"config\"}").RootElement);
                    }
                    return Task.FromResult(CommandResult.Handled);

                default:
                    SendCommandResult(cmdName, error: $"未知指令: /{cmdName}");
                    return Task.FromResult(CommandResult.Handled);
            }
        }

        return Task.FromResult(CommandResult.NotACommand);
    }

    /// <summary>将用户输入的值转为 JSON 值字面量。</summary>
    private static string ToJsonLiteral(string raw)
    {
        if (bool.TryParse(raw, out _) || int.TryParse(raw, out _) || double.TryParse(raw, out _))
            return raw.ToLowerInvariant();
        if (raw is "null") return "null";
        return $"\"{raw.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    public async ValueTask DisposeAsync()
    {
        _connectionCts.Cancel();
        if (_receiveLoop is not null)
            await _receiveLoop;
        _connectionCts.Dispose();
        _aiCts?.Dispose();
    }

    public void ShowStop()
    {
    }
}
