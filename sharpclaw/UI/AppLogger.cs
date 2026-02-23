using Terminal.Gui.Views;

namespace sharpclaw.UI;

/// <summary>
/// 全局日志路由和状态更新。带缓冲机制降低 TextView 更新频率。
/// </summary>
public static class AppLogger
{
    private static TextView? _logView;
    private static Label? _statusLabel;

    private static readonly object LogBufferLock = new();
    private static string _logBuffer = "";
    private static bool _logFlushScheduled;

    public static void SetLogView(TextView logView) => _logView = logView;
    public static void SetStatusLabel(Label label) => _statusLabel = label;

    public static void Log(string message)
    {
        if (_logView?.App is null) return;

        lock (LogBufferLock)
        {
            _logBuffer = string.IsNullOrEmpty(_logBuffer)
                ? message
                : _logBuffer + "\n" + message;

            if (_logFlushScheduled) return;
            _logFlushScheduled = true;
        }

        Task.Delay(100).ContinueWith(_ => FlushLogBuffer());
    }

    private static void FlushLogBuffer()
    {
        string pending;
        lock (LogBufferLock)
        {
            pending = _logBuffer;
            _logBuffer = "";
            _logFlushScheduled = false;
        }

        if (string.IsNullOrEmpty(pending)) return;

        _logView?.App?.Invoke(() =>
        {
            try
            {
                var text = _logView.Text;
                _logView.Text = string.IsNullOrEmpty(text)
                    ? pending
                    : text + "\n" + pending;
                _logView.MoveEnd();
            }
            catch (ArgumentOutOfRangeException) { }
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
