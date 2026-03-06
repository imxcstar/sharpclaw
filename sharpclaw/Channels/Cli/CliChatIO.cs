// Authors: sml2 <admin@sml2.com>
// Co-Authors: Claude Sonnet 4.6 <claude@anthropic.com>

using sharpclaw.Abstractions;
using System.Threading.Channels;

namespace sharpclaw.Channels.Cli;

/// <summary>
/// IChatIO 的纯 CLI 实现。
/// 使用标准 Console I/O，无需 Terminal.Gui，适合快速开发和通用环境。
/// </summary>
public sealed class CliChatIO : IChatIO
{
    private CancellationTokenSource? _aiCts;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Channel<string> _inputChannel = Channel.CreateUnbounded<string>();
    private readonly Thread _inputThread;
    private static readonly bool SupportsColor = !Console.IsOutputRedirected;

    private static void SetColor(ConsoleColor color)
    {
        if (SupportsColor) Console.ForegroundColor = color;
    }

    private static void ResetColor()
    {
        if (SupportsColor) Console.ResetColor();
    }

    public CliChatIO()
    {
        // 后台线程持续读取 Console 输入，以便支持 CancellationToken
        _inputThread = new Thread(ReadInputLoop) { IsBackground = true };
        _inputThread.Start();
    }

    private void ReadInputLoop()
    {
        while (!_stopCts.IsCancellationRequested)
        {
            var line = Console.ReadLine();
            if (line is null)
            {
                // stdin 关闭（管道/重定向结束）
                _inputChannel.Writer.TryComplete();
                break;
            }
            _inputChannel.Writer.TryWrite(line);
        }
    }

    /// <inheritdoc/>
    public Task WaitForReadyAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task<string> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        ResetColor();
        SetColor(ConsoleColor.Cyan);
        Console.Write("> ");
        ResetColor();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _stopCts.Token);
        return await _inputChannel.Reader.ReadAsync(linked.Token);
    }

    /// <inheritdoc/>
    public Task<CommandResult> HandleCommandAsync(string input)
    {
        var trimmed = input.Trim();
        if (trimmed is "/exit" or "/quit")
        {
            RequestStop();
            return Task.FromResult(CommandResult.Exit);
        }

        if (trimmed is "/help")
        {
            SetColor(ConsoleColor.DarkGray);
            Console.WriteLine("""
                内置指令：
                  /help    显示此帮助信息
                  /exit    退出程序
                  /quit    退出程序
                """);
            ResetColor();
            return Task.FromResult(CommandResult.Handled);
        }

        return Task.FromResult(CommandResult.NotACommand);
    }

    /// <inheritdoc/>
    public void EchoUserInput(string input)
    {
        // CLI 模式下用户已看到自己的输入，无需回显
    }

    /// <inheritdoc/>
    public void BeginAiResponse()
    {
        SetColor(ConsoleColor.Green);
        Console.Write("\nAI: ");
    }

    /// <inheritdoc/>
    public void AppendChat(string text)
    {
        Console.Write(text);
    }

    /// <inheritdoc/>
    public void AppendChatLine(string text)
    {
        Console.WriteLine(text);
    }

    /// <inheritdoc/>
    public void ShowRunning()
    {
        SetColor(ConsoleColor.DarkYellow);
        Console.Write("\n思考中...");
        ResetColor();
    }

    /// <inheritdoc/>
    public CancellationToken GetAiCancellationToken()
    {
        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        return _aiCts.Token;
    }

    /// <inheritdoc/>
    public void RequestStop()
    {
        _stopCts.Cancel();
        _inputChannel.Writer.TryComplete();
    }
}