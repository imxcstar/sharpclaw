using sharpclaw.Abstractions;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace sharpclaw.UI;

/// <summary>
/// 主聊天窗口：上方对话区、下方日志区、底部输入框/运行状态。
/// </summary>
public sealed class ChatWindow : Runnable, IChatIO
{
    private readonly TextView _chatView;
    private readonly TextView _logView;
    private readonly TextField _inputField;
    private readonly Label _inputLabel;
    private readonly SpinnerView _spinner;
    private readonly Label _statusLabel;

    private TaskCompletionSource<string>? _inputTcs;
    private CancellationTokenSource? _aiCts;
    private readonly TaskCompletionSource _readyTcs = new();

    /// <summary>
    /// 等待窗口就绪（App 已绑定）。
    /// </summary>
    public Task WaitForReadyAsync() => _readyTcs.Task;

    public ChatWindow()
    {
        Application.QuitKey = Key.Q.WithCtrl;
        Title = $"Sharpclaw ({Application.QuitKey} 退出)";

        // ── 对话区 ──
        var chatFrame = new FrameView
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
        chatFrame.Add(_chatView);

        // ── 日志区 ──
        var logFrame = new FrameView
        {
            Title = "日志",
            X = 0,
            Y = Pos.Bottom(chatFrame),
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
        logFrame.Add(_logView);

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
            ["/exit", "/quit"]);
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

        Add(chatFrame, logFrame, _inputLabel, _inputField, _spinner, _statusLabel);

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

    protected override bool OnKeyDown(Key key)
    {
        if (key == Key.Esc && _aiCts is { IsCancellationRequested: false })
        {
            _aiCts.Cancel();
            AppLogger.Log("[用户] 已取消 AI 运行");
            return true;
        }

        return base.OnKeyDown(key);
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
