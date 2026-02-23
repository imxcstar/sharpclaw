using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace sharpclaw.UI;

/// <summary>
/// 主聊天窗口：上方对话区、下方日志区、底部输入框。
/// </summary>
public sealed class ChatWindow : Runnable
{
    private readonly TextView _chatView;
    private readonly TextView _logView;
    private readonly TextField _inputField;
    private readonly Label _inputLabel;

    private TaskCompletionSource<string>? _inputTcs;

    public ChatWindow()
    {
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

        _inputField.Accepting += OnInputAccepting;

        Add(chatFrame, logFrame, _inputLabel, _inputField);

        // 绑定日志
        AppLogger.SetLogView(_logView);
    }

    /// <summary>
    /// 等待用户输入一行文本。由 MainAgent 调用。
    /// </summary>
    public Task<string> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        _inputTcs = new TaskCompletionSource<string>();
        cancellationToken.Register(() => _inputTcs.TrySetCanceled());

        Application.Invoke(() =>
        {
            _inputField.Enabled = true;
            _inputField.Text = "";
            _inputField.SetFocus();
        });

        return _inputTcs.Task;
    }

    /// <summary>
    /// 追加对话文本（流式输出用，不换行）。
    /// </summary>
    public void AppendChat(string text)
    {
        Application.Invoke(() =>
        {
            _chatView.Text += text;
            _chatView.MoveEnd();
        });
    }

    /// <summary>
    /// 追加一行对话文本。
    /// </summary>
    public void AppendChatLine(string text)
    {
        Application.Invoke(() =>
        {
            var current = _chatView.Text;
            _chatView.Text = string.IsNullOrEmpty(current)
                ? text
                : current + "\n" + text;
            _chatView.MoveEnd();
        });
    }

    /// <summary>
    /// 禁用输入（AI 回复期间）。
    /// </summary>
    public void DisableInput()
    {
        Application.Invoke(() =>
        {
            _inputField.Enabled = false;
        });
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
