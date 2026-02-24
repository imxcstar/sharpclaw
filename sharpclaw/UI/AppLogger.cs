using sharpclaw.Abstractions;

namespace sharpclaw.UI;

/// <summary>
/// 全局日志路由和状态更新。静态门面，委托给可替换的 IAppLogger 实例。
/// </summary>
public static class AppLogger
{
    private static IAppLogger _instance = NullLogger.Instance;

    public static void SetInstance(IAppLogger logger) => _instance = logger;

    public static void Log(string message) => _instance.Log(message);

    public static void SetStatus(string status) => _instance.SetStatus(status);

    private sealed class NullLogger : IAppLogger
    {
        public static readonly NullLogger Instance = new();
        public void Log(string message) { }
        public void SetStatus(string status) { }
    }
}
