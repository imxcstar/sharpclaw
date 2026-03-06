using sharpclaw.Abstractions;
using System.Text;
using System.Threading.Channels;

namespace sharpclaw.Channels.Cli;

/// <summary>
/// IChatIO 的纯 CLI 实现。
/// 动画贯穿整个 AI 回合，文本输出时暂停，工具调用时恢复。
/// </summary>
public sealed class CliChatIO : IChatIO, IDisposable
{
    private CancellationTokenSource? _aiCts;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Channel<string> _inputChannel = Channel.CreateUnbounded<string>();
    private readonly Thread _inputThread;

    private readonly object _consoleLock = new();

    private CancellationTokenSource? _animationCts;
    private Task? _animationTask;

    /// <summary>当前是否处于文本输出模式（动画暂停）。</summary>
    private bool _inTextMode;

    /// <summary>本轮是否已经输出过 AI 前缀。</summary>
    private bool _hasOutputPrefix;

    /// <summary>当前状态文本，由 AppLogger.SetStatus 更新。</summary>
    private volatile string? _status;

    /// <summary>是否正在接受用户输入（false = AI 运行中，只监听 ESC）。</summary>
    private volatile bool _acceptingInput;

    private static readonly bool SupportsColor = !Console.IsOutputRedirected;

    public CliChatIO()
    {
        Console.OutputEncoding = Encoding.UTF8;
        _inputThread = new Thread(ReadInputLoop) { IsBackground = true, Name = "CliInputThread" };
        _inputThread.Start();
    }

    private void ReadInputLoop()
    {
        // 如果 stdin 被重定向（管道输入），回退到 ReadLine
        if (Console.IsInputRedirected)
        {
            ReadInputLoopRedirected();
            return;
        }

        var sb = new StringBuilder();
        try
        {
            while (!_stopCts.IsCancellationRequested)
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(20);
                    continue;
                }

                var key = Console.ReadKey(true);

                if (!_acceptingInput)
                {
                    // AI 运行中：只响应 ESC 取消
                    if (key.Key == ConsoleKey.Escape)
                        _aiCts?.Cancel();
                    continue;
                }

                // 用户输入模式：逐键组建一行
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        _inputChannel.Writer.TryWrite(sb.ToString());
                        sb.Clear();
                        break;

                    case ConsoleKey.Backspace:
                        if (sb.Length > 0)
                        {
                            var removed = sb[sb.Length - 1];
                            sb.Remove(sb.Length - 1, 1);
                            if (IsWideChar(removed))
                                Console.Write("\b\b  \b\b");
                            else
                                Console.Write("\b \b");
                        }
                        break;

                    case ConsoleKey.Escape:
                        // 清除当前输入
                        for (int j = 0; j < sb.Length; j++)
                        {
                            if (IsWideChar(sb[j]))
                                Console.Write("\b\b  \b\b");
                            else
                                Console.Write("\b \b");
                        }
                        sb.Clear();
                        break;

                    default:
                        if (key.KeyChar >= ' ')
                        {
                            sb.Append(key.KeyChar);
                            Console.Write(key.KeyChar);
                        }
                        break;
                }
            }
        }
        catch { /* 忽略读取异常 */ }
    }

    /// <summary>管道输入回退模式。</summary>
    private void ReadInputLoopRedirected()
    {
        try
        {
            while (!_stopCts.IsCancellationRequested)
            {
                var line = Console.ReadLine();
                if (line is null)
                {
                    _inputChannel.Writer.TryComplete();
                    break;
                }
                _inputChannel.Writer.TryWrite(line);
            }
        }
        catch { }
    }

    /// <summary>判断字符是否为宽字符（CJK / 全角，占 2 列）。</summary>
    private static bool IsWideChar(char c) =>
        (c >= 0x1100 && c <= 0x115F) ||
        (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) ||
        (c >= 0xAC00 && c <= 0xD7A3) ||
        (c >= 0xF900 && c <= 0xFAFF) ||
        (c >= 0xFE30 && c <= 0xFE6F) ||
        (c >= 0xFF01 && c <= 0xFF60) ||
        (c >= 0xFFE0 && c <= 0xFFE6);

    private static void SetColor(ConsoleColor color)
    {
        if (SupportsColor) Console.ForegroundColor = color;
    }

    private static void ResetColor()
    {
        if (SupportsColor) Console.ResetColor();
    }

    /// <summary>擦除光标到行尾的残留字符。</summary>
    private static void EraseToEnd()
    {
        if (SupportsColor)
            Console.Write("\x1b[K");
    }

    /// <summary>停止动画任务（线程安全，可重复调用）。不持有 _consoleLock。</summary>
    private void StopAnimation()
    {
        var cts = _animationCts;
        if (cts == null) return;

        cts.Cancel();
        try { _animationTask?.Wait(); } catch { }
        cts.Dispose();
        _animationCts = null;
        _animationTask = null;
    }

    /// <summary>渲染一帧动画。必须在 _consoleLock 内调用。</summary>
    private void RenderSpinnerLine(string frame)
    {
        SetColor(ConsoleColor.DarkYellow);
        var status = _status;
        if (string.IsNullOrEmpty(status))
            Console.Write($"\r🤖 思考中 {frame} (Esc 取消)");
        else
            Console.Write($"\r🤖 思考中 {frame} ({status}) (Esc 取消)");
        EraseToEnd();
        Console.Out.Flush();
        ResetColor();
    }

    /// <inheritdoc/>
    public Task WaitForReadyAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task<string> ReadInputAsync(CancellationToken cancellationToken = default)
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

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCts.Token);
        try { return await _inputChannel.Reader.ReadAsync(linked.Token); }
        catch (OperationCanceledException) { return string.Empty; }
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
            lock (_consoleLock)
            {
                SetColor(ConsoleColor.DarkGray);
                Console.WriteLine("""
                    内置指令：
                      /help    显示此帮助信息
                      /exit    退出程序
                      /quit    退出程序
                    """);
                ResetColor();
            }
            return Task.FromResult(CommandResult.Handled);
        }

        return Task.FromResult(CommandResult.NotACommand);
    }

    /// <inheritdoc/>
    public void EchoUserInput(string input) { }

    /// <inheritdoc/>
    public void ShowRunning()
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
                try { await Task.Delay(80, token); }
                catch (TaskCanceledException) { break; }

                lock (_consoleLock)
                {
                    // 文本输出中，跳过渲染（但不退出循环）
                    if (_inTextMode) continue;

                    RenderSpinnerLine(frames[i++ % frames.Length]);
                }
            }
        }, token);
    }

    /// <inheritdoc/>
    public void ShowStop()
    {
        StopAnimation();

        lock (_consoleLock)
        {
            if (!_inTextMode)
            {
                // 动画还在显示，清除动画行
                Console.Write('\r');
                EraseToEnd();
            }
        }
    }

    /// <inheritdoc/>
    public void BeginAiResponse()
    {
        // 空操作：动画持续运行，直到文本到达时才暂停。
    }

    /// <summary>
    /// 由 CliAppLogger 调用，更新状态文本。
    /// 如果当前在文本输出模式，自动恢复动画。
    /// </summary>
    internal void UpdateStatus(string status)
    {
        lock (_consoleLock)
        {
            _status = status;

            if (_inTextMode)
            {
                // 文本输出模式 → 恢复动画（表示 AI 在做工具调用等非文本工作）
                Console.WriteLine(); // 结束当前文本行
                _inTextMode = false;
                // 立即渲染一帧，动画任务的循环会在下一个 tick 继续
                RenderSpinnerLine("⠋");
            }
            // 如果已经在动画模式，状态文本会在下一帧自动更新
        }
    }

    /// <inheritdoc/>
    public void AppendChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_consoleLock)
        {
            if (!_inTextMode)
            {
                // 从动画模式切换到文本输出模式
                _inTextMode = true;

                if (!_hasOutputPrefix)
                {
                    // 首次文本：动画行原地变成 AI 前缀
                    SetColor(ConsoleColor.Green);
                    Console.Write("\r🤖 AI: ");
                    EraseToEnd();
                    ResetColor();
                    _hasOutputPrefix = true;
                }
                else
                {
                    // 工具调用后恢复文本：清除动画行，继续输出
                    Console.Write('\r');
                    EraseToEnd();
                }
            }

            Console.Write(text);
            Console.Out.Flush();
        }
    }

    /// <inheritdoc/>
    public void AppendChatLine(string text)
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

            Console.WriteLine(text);
        }
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
        _aiCts?.Cancel();
        StopAnimation();
    }

    public void Dispose()
    {
        RequestStop();
        _stopCts.Dispose();
        _aiCts?.Dispose();
    }
}