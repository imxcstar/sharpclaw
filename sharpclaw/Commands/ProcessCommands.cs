using sharpclaw.Core;
using sharpclaw.Core.TaskManagement;
using System;
using System.ComponentModel;

namespace sharpclaw.Commands;

/// <summary>
/// Process execution commands.
/// </summary>
public class ProcessCommands : CommandBase
{
    public ProcessCommands(TaskManager taskManager, IAgentContext agentContext)
        : base(taskManager, agentContext)
    {
    }

    [Description("Execute pwsh commands")]
    public string CommandPowershell(
    [Description("Arguments to pass to pwsh")] string[] args,
    [Description("Working directory (optional)")] string workingDirectory = "")
    {
        return RunProcess("pwsh", args ?? Array.Empty<string>(), "pwsh " + string.Join(" ", args ?? Array.Empty<string>()),
            true, workingDirectory, 0);
    }

    [Description("Execute Bash commands")]
    public string CommandBash(
    [Description("Arguments to pass to Bash")] string[] args,
    [Description("Working directory (optional)")] string workingDirectory = "")
    {
        return RunProcess("bash", args ?? Array.Empty<string>(), "bash " + string.Join(" ", args ?? Array.Empty<string>()),
            true, workingDirectory, 0);
    }
}
