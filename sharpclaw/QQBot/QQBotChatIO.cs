using System.Text;
using System.Threading.Channels;
using Luolan.QQBot;
using Luolan.QQBot.Models;
using sharpclaw.Abstractions;
using Channel = System.Threading.Channels.Channel;

namespace sharpclaw.QQBot;

/// <summary>
/// IChatIO 的 QQ Bot 实现。
/// 通过 Channel 桥接 QQ 消息事件到 MainAgent 的对话循环，
/// 缓冲 AI 输出并在轮次结束时作为完整消息回复。
/// </summary>
public sealed class QQBotChatIO : IChatIO
{
    private readonly QQBotClient _bot;
    private readonly Channel<PendingMessage> _inputChannel = Channel.CreateUnbounded<PendingMessage>();
    private readonly CancellationTokenSource _stopCts = new();
    private CancellationTokenSource? _aiCts;

    // 当前轮次的回复上下文和输出缓冲
    private PendingMessage? _currentMessage;
    private readonly StringBuilder _outputBuffer = new();
    private string _status = "空闲";

    public QQBotChatIO(QQBotClient bot)
    {
        _bot = bot;
    }

    /// <summary>
    /// 将一条 QQ 消息入队。由事件处理器调用。
    /// 返回 true 表示已入队，false 表示是 /stop 或 /status 等指令已就地处理。
    /// </summary>
    public async Task<bool> EnqueueMessageAsync(Message message, MessageSource source)
    {
        var content = CleanContent(message.Content ?? "");

        // 处理指令
        if (content.Equals("/stop", StringComparison.OrdinalIgnoreCase))
        {
            _aiCts?.Cancel();
            await ReplyToMessageAsync(message, source, "已发送停止指令。");
            return false;
        }

        if (content.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            var statusText = $"状态: {_status}\n连接: {(_bot.IsConnected ? "已连接" : "未连接")}";
            await ReplyToMessageAsync(message, source, statusText);
            return false;
        }

        if (string.IsNullOrWhiteSpace(content))
            return false;

        _inputChannel.Writer.TryWrite(new PendingMessage(content, message, source));
        return true;
    }

    public Task WaitForReadyAsync() => Task.CompletedTask;

    public async Task<string> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        // 如果有上一轮的缓冲输出，先发送
        await FlushOutputAsync();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _stopCts.Token);

        var pending = await _inputChannel.Reader.ReadAsync(linked.Token);
        _currentMessage = pending;
        return pending.Text;
    }

    public void AppendChat(string text)
    {
        _outputBuffer.Append(text);
    }

    public void AppendChatLine(string text)
    {
        _outputBuffer.AppendLine(text);
    }

    public void ShowRunning()
    {
        _status = "AI 处理中...";
    }

    public CancellationToken GetAiCancellationToken()
    {
        _aiCts = new CancellationTokenSource();
        return _aiCts.Token;
    }

    public void RequestStop()
    {
        _stopCts.Cancel();
    }

    /// <summary>
    /// 刷新输出缓冲区，将累积的 AI 回复发送到 QQ。
    /// </summary>
    private async Task FlushOutputAsync()
    {
        if (_currentMessage is null || _outputBuffer.Length == 0)
        {
            _outputBuffer.Clear();
            return;
        }

        // 清理输出：去掉 "> xxx" 回显行和 "AI: " 前缀
        var output = _outputBuffer.ToString().Trim();
        _outputBuffer.Clear();

        // 去掉开头的用户输入回显行
        if (output.StartsWith("> "))
        {
            var idx = output.IndexOf('\n');
            if (idx >= 0)
                output = output[(idx + 1)..].TrimStart();
            else
                output = "";
        }

        // 去掉 "AI: " 前缀
        if (output.StartsWith("AI: "))
            output = output[4..];

        output = output.Trim();
        if (string.IsNullOrEmpty(output))
            return;

        // QQ 消息有长度限制，分段发送，每段递增 msgSeq
        const int maxLen = 2000;
        var msg = _currentMessage;
        var seq = 1;
        for (var i = 0; i < output.Length; i += maxLen)
        {
            var segment = output.Substring(i, Math.Min(maxLen, output.Length - i));
            await ReplyToMessageAsync(msg.Message, msg.Source, segment, seq++);
        }

        _status = "空闲";
    }

    private async Task ReplyToMessageAsync(Message message, MessageSource source, string text, int msgSeq = 1)
    {
        try
        {
            switch (source)
            {
                case MessageSource.Channel:
                    await _bot.ReplyAsync(message, text);
                    break;
                case MessageSource.Group:
                    await _bot.ReplyGroupAsync(message, text, msgSeq);
                    break;
                case MessageSource.C2C:
                    await _bot.ReplyC2CAsync(message, text, msgSeq);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QQBot] 回复失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清理消息内容：去除 @机器人 的 mention 标记。
    /// </summary>
    private static string CleanContent(string content)
    {
        // QQ 频道 @消息格式: <@!botId> 实际内容
        // 群 @消息通常已经去掉了 @前缀，但可能有残留空格
        var cleaned = System.Text.RegularExpressions.Regex.Replace(content, @"<@!\d+>\s*", "");
        return cleaned.Trim();
    }

    public record PendingMessage(string Text, Message Message, MessageSource Source);
}

public enum MessageSource
{
    Channel,
    Group,
    C2C
}
