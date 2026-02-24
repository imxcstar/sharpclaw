using sharpclaw.Abstractions;
using Terminal.Gui.Views;

namespace sharpclaw.UI;

/// <summary>
/// Terminal.Gui 实现的日志和状态更新。
/// </summary>
public sealed class TerminalGuiLogger : IAppLogger
{
    private readonly TextView _logView;
    private readonly Label _statusLabel;

    public TerminalGuiLogger(TextView logView, Label statusLabel)
    {
        _logView = logView;
        _statusLabel = statusLabel;
    }

    public void Log(string message)
    {
        if (_logView.App is not { } app) return;

        app.Invoke(() =>
        {
            var tmessage = message.Replace('\t', ' ');
            var text = _logView.Text;
            _logView.Text = string.IsNullOrEmpty(text)
                ? tmessage
                : text + "\n" + tmessage;
            _logView.MoveEnd();
        });
    }

    public void SetStatus(string status)
    {
        if (_statusLabel.App is not { } app) return;

        app.Invoke(() =>
        {
            _statusLabel.Text = $" {status} (Esc 取消)";
        });
    }
}
