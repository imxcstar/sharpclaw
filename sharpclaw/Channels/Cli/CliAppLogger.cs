using sharpclaw.Abstractions;

namespace sharpclaw.Channels.Cli;

public class CliAppLogger : IAppLogger
{
    private readonly CliChatIO _chatIO;

    public CliAppLogger(CliChatIO chatIO)
    {
        _chatIO = chatIO;
    }

    public void Log(string message)
    {
        // CLI 模式下日志不在主控台输出，避免干扰动画和对话
    }

    public void SetStatus(string status)
    {
        _chatIO.UpdateStatus(status);
    }
}