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

    /// <summary>
    /// 等待窗口就绪（App 已绑定）。
    /// </summary>
    public Task WaitForReadyAsync() => _readyTcs.Task;

    public ChatWindow()
    {
        Application.QuitKey = Key.Q.WithCtrl;
        Title = $"Sharpclaw ({Application.QuitKey} 退出)";

        // ── 对话区 ──
        _chatFrame = new FrameView
        {
            Title = "对话",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(60),
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
            Title = "日志 (Ctrl+L 收起)",
            X = 0,
            Y = Pos.Bottom(_chatFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
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
            ["/exit", "/quit", "/config"]);
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

    /// <summary>
    /// 在 TUI 中弹出配置对话框，完成后返回聊天界面。
    /// </summary>
    public Task<bool> ShowConfigAsync()
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
        if (key == Key.Esc && _aiCts is { IsCancellationRequested: false })
        {
            _aiCts.Cancel();
            AppLogger.Log("[用户] 已取消 AI 运行");
            return true;
        }

        if (key == Key.L.WithCtrl)
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
        _logFrame.Title = _logCollapsed ? "日志 (Ctrl+L 展开)" : "日志 (Ctrl+L 收起)";
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
}
