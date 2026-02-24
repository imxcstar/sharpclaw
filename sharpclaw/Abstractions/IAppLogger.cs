namespace sharpclaw.Abstractions;

/// <summary>
/// 应用日志和状态更新抽象。
/// </summary>
public interface IAppLogger
{
    void Log(string message);
    void SetStatus(string status);
}
