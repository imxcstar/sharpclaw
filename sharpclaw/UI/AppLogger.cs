using Terminal.Gui.Views;

namespace sharpclaw.UI;

/// <summary>
/// 全局日志路由：将日志消息线程安全地追加到 Terminal.Gui TextView。
/// 未绑定 UI 时静默丢弃。
/// </summary>
public static class AppLogger
{
    private static TextView? _logView;

    public static void SetLogView(TextView logView) => _logView = logView;

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
}
