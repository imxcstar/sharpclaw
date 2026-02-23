using Terminal.Gui.Views;

namespace sharpclaw.UI;

/// <summary>
/// 全局日志路由和状态更新。
/// </summary>
public static class AppLogger
{
    private static TextView? _logView;
    private static Label? _statusLabel;

    public static void SetLogView(TextView logView) => _logView = logView;
    public static void SetStatusLabel(Label label) => _statusLabel = label;

    public static void Log(string message)
    {
        if (_logView?.App is not { } app) return;

        app.Invoke(() =>
        {
            var text = _logView.Text;
            _logView.Text = string.IsNullOrEmpty(text)
                ? message
                : text + "\n" + message;
            _logView.MoveEnd();
        });
    }

    /// <summary>
    /// 更新底部状态栏文本（各代理调用）。
    /// </summary>
    public static void SetStatus(string status)
    {
        if (_statusLabel?.App is not { } app) return;

        app.Invoke(() =>
        {
            _statusLabel.Text = $" {status} (Esc 取消)";
        });
    }
}
