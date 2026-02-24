using sharpclaw.UI;
using sharpclaw.Abstractions;
using sharpclaw.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace sharpclaw.Channels.Tui;

/// <summary>
/// 主聊天窗口：上方对话区、下方日志区、底部输入框/运行状态。
/// </summary>
public sealed class ChatWindow : Runnable, IChatIO
{
    private readonly FrameView _chatFrame;
    private readonly FrameView _logFrame;
    private readonly TextView _chatView;
    private readonly TextView _logView;
    private readonly TextField _inputField;
    private readonly Label _inputLabel;
    private readonly SpinnerView _spinner;
    private readonly Label _statusLabel;

    private TaskCompletionSource<string>? _inputTcs;
    private CancellationTokenSource? _aiCts;
    private readonly TaskCompletionSource _readyTcs = new();
    private bool _logCollapsed;
    private readonly TuiChannelConfig _tuiConfig;

    /// <summary>
    /// 等待窗口就绪（App 已绑定）。
    /// </summary>
    public Task WaitForReadyAsync() => _readyTcs.Task;

    public ChatWindow(TuiChannelConfig? tuiConfig = null)
    {
        _tuiConfig = tuiConfig ?? new TuiChannelConfig();
        _logCollapsed = _tuiConfig.LogCollapsed;

        Application.QuitKey = ParseKey(_tuiConfig.QuitKey) ?? Key.Q.WithCtrl;
        Title = $"Sharpclaw ({Application.QuitKey} 退出)";

        // ── 对话区 ──
        _chatFrame = new FrameView
        {
            Title = "对话",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = _logCollapsed ? Dim.Fill(1) : Dim.Percent(60),
        };

        _chatView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
            Text = "",
        };
        _chatFrame.Add(_chatView);

        // ── 日志区 ──
        _logFrame = new FrameView
        {
            Title = "日志",
            X = 0,
            Y = Pos.Bottom(_chatFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            Visible = !_logCollapsed,
        };

        _logView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
            Text = "",
        };
        _logFrame.Add(_logView);

        // ── 输入区 ──
        _inputLabel = new Label
        {
            Text = "> ",
            X = 0,
            Y = Pos.AnchorEnd(1),
        };

        _inputField = new TextField
        {
            X = Pos.Right(_inputLabel),
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
        };
        _inputField.Autocomplete.SuggestionGenerator = new SlashCommandSuggestionGenerator(
            ["/exit", "/quit", "/config", "/help"]);
        _inputField.Autocomplete.SelectionKey = Key.Tab;
        _inputField.Accepting += OnInputAccepting;

        // ── 运行状态区（默认隐藏）──
        _spinner = new SpinnerView
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Sequence = ["/", "-", "\\"],
            Visible = false,
        };

        _statusLabel = new Label
        {
            Text = " AI 思考中... (Esc 取消)",
            X = Pos.Right(_spinner),
            Y = Pos.AnchorEnd(1),
            Visible = false,
        };

        Add(_chatFrame, _logFrame, _inputLabel, _inputField, _spinner, _statusLabel);

        // 初始化 TerminalGuiLogger 并绑定到全局 AppLogger
        AppLogger.SetInstance(new TerminalGuiLogger(_logView, _statusLabel));

        Initialized += (_, _) => _readyTcs.TrySetResult();
    }

    /// <summary>
    /// 获取用于取消当前 AI 运行的 CancellationToken。
    /// </summary>
    public CancellationToken GetAiCancellationToken()
    {
        _aiCts = new CancellationTokenSource();
        return _aiCts.Token;
    }

    /// <summary>
    /// 等待用户输入一行文本。由 MainAgent 调用。
    /// </summary>
    public Task<string> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        _inputTcs = new TaskCompletionSource<string>();
        cancellationToken.Register(() => _inputTcs.TrySetCanceled());

        App!.Invoke(() =>
        {
            ShowInput();
            _inputField.Text = "";
            _inputField.SetFocus();
        });

        return _inputTcs.Task;
    }

    /// <summary>
    /// 回显用户输入，带 "> " 前缀。
    /// </summary>
    public void EchoUserInput(string input) => AppendChatLine($"> {input}\n");

    /// <summary>
    /// 开始 AI 回复，显示 "AI: " 前缀。
    /// </summary>
    public void BeginAiResponse() => AppendChat("AI: ");

    /// <summary>
    /// 追加对话文本（流式输出用）。
    /// </summary>
    public void AppendChat(string text)
    {
        if (App is not { } app) return;

        app.Invoke(() =>
        {
            _chatView.Text += text.Replace('\t', ' ');
            _chatView.MoveEnd();
        });
    }

    /// <summary>
    /// 追加一行对话文本。
    /// </summary>
    public void AppendChatLine(string text)
    {
        if (App is not { } app) return;

        app.Invoke(() =>
        {
            var current = _chatView.Text;
            _chatView.Text = string.IsNullOrEmpty(current)
                ? text.Replace('\t', ' ')
                : current + "\n" + text.Replace('\t', ' ');
            _chatView.MoveEnd();
        });
    }

    /// <summary>
    /// 切换到运行状态：隐藏输入框，显示 spinner。
    /// </summary>
    public void ShowRunning()
    {
        App!.Invoke(() =>
        {
            _inputLabel.Visible = false;
            _inputField.Visible = false;
            _spinner.Visible = true;
            _spinner.AutoSpin = true;
            _statusLabel.Visible = true;
        });
    }

    /// <summary>
    /// 切换到输入状态：隐藏 spinner，显示输入框。
    /// </summary>
    private void ShowInput()
    {
        _spinner.AutoSpin = false;
        _spinner.Visible = false;
        _statusLabel.Visible = false;
        _inputLabel.Visible = true;
        _inputField.Visible = true;
        _inputField.Enabled = true;
    }

    /// <summary>
    /// IChatIO.RequestStop：线程安全地请求停止应用。
    /// </summary>
    void IChatIO.RequestStop()
    {
        App?.Invoke(() => base.RequestStop());
    }

    public async Task<CommandResult> HandleCommandAsync(string input)
    {
        switch (input)
        {
            case "/exit" or "/quit":
                ((IChatIO)this).RequestStop();
                return CommandResult.Exit;

            case "/config":
                var saved = await ShowConfigAsync();
                if (saved)
                    AppendChatLine("配置已保存，重启后生效。\n");
                return CommandResult.Handled;

            case "/help":
                AppendChatLine($"""
                    可用指令:
                      /help   - 显示帮助
                      /config - 打开配置界面
                      /exit   - 退出程序
                    快捷键:
                      {_tuiConfig.ToggleLogKey}  - 收起/展开日志
                      {_tuiConfig.QuitKey}  - 退出程序
                      {_tuiConfig.CancelKey}     - 取消 AI 运行

                    """);
                return CommandResult.Handled;

            default:
                return input.StartsWith('/') ? CommandResult.Handled : CommandResult.NotACommand;
        }
    }

    /// <summary>
    /// 在 TUI 中弹出配置对话框，完成后返回聊天界面。
    /// </summary>
    private Task<bool> ShowConfigAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        App!.Invoke(() =>
        {
            var configDialog = new ConfigDialog();
            if (SharpclawConfig.Exists())
                configDialog.LoadFrom(SharpclawConfig.Load());
            App!.Run(configDialog);
            var saved = configDialog.Saved;
            configDialog.Dispose();
            tcs.SetResult(saved);
        });
        return tcs.Task;
    }

    protected override bool OnKeyDown(Key key)
    {
        var cancelKey = ParseKey(_tuiConfig.CancelKey) ?? Key.Esc;
        if (key == cancelKey && _aiCts is { IsCancellationRequested: false })
        {
            _aiCts.Cancel();
            AppLogger.Log("[用户] 已取消 AI 运行");
            return true;
        }

        var toggleLogKey = ParseKey(_tuiConfig.ToggleLogKey) ?? Key.L.WithCtrl;
        if (key == toggleLogKey)
        {
            ToggleLog();
            return true;
        }

        return base.OnKeyDown(key);
    }

    private void ToggleLog()
    {
        _logCollapsed = !_logCollapsed;
        _logFrame.Visible = !_logCollapsed;
        _chatFrame.Height = _logCollapsed ? Dim.Fill(1) : Dim.Percent(60);
    }

    private void OnInputAccepting(object? sender, CommandEventArgs e)
    {
        var text = _inputField.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        e.Handled = true;
        _inputField.Text = "";
        _inputField.Enabled = false;

        _inputTcs?.TrySetResult(text);
    }

    /// <summary>
    /// 解析快捷键字符串（如 "Ctrl+Q"、"Esc"、"Ctrl+L"）为 Terminal.Gui Key。
    /// </summary>
    private static Key? ParseKey(string keyStr)
    {
        if (string.IsNullOrWhiteSpace(keyStr))
            return null;

        var parts = keyStr.Split('+', StringSplitOptions.TrimEntries);
        var ctrl = false;
        var alt = false;
        var shift = false;
        string? keyPart = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default: keyPart = part; break;
            }
        }

        if (keyPart is null)
            return null;

        Key? key = keyPart.ToLowerInvariant() switch
        {
            "esc" or "escape" => Key.Esc,
            "enter" or "return" => Key.Enter,
            "tab" => Key.Tab,
            "space" => Key.Space,
            "backspace" => Key.Backspace,
            "delete" or "del" => Key.Delete,
            "up" => Key.CursorUp,
            "down" => Key.CursorDown,
            "left" => Key.CursorLeft,
            "right" => Key.CursorRight,
            "f1" => Key.F1, "f2" => Key.F2, "f3" => Key.F3, "f4" => Key.F4,
            "f5" => Key.F5, "f6" => Key.F6, "f7" => Key.F7, "f8" => Key.F8,
            "f9" => Key.F9, "f10" => Key.F10, "f11" => Key.F11, "f12" => Key.F12,
            _ when keyPart.Length == 1 && char.IsLetterOrDigit(keyPart[0]) =>
                (Key)char.ToLower(keyPart[0]),
            _ => null,
        };

        if (key is not { } k)
            return null;

        if (ctrl) k = k.WithCtrl;
        if (alt) k = k.WithAlt;
        if (shift) k = k.WithShift;

        return k;
    }
}
