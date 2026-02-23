using Terminal.Gui.App;
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
        if (_logView is null) return;

        Application.Invoke(() =>
        {
            var text = _logView.Text;
            _logView.Text = string.IsNullOrEmpty(text)
                ? message
                : text + "\n" + message;
            ScrollToEnd(_logView);
        });
    }

    private static void ScrollToEnd(TextView view)
    {
        var lines = view.Text?.Split('\n').Length ?? 0;
        if (lines > 0)
        {
            view.MoveEnd();
        }
    }
}
