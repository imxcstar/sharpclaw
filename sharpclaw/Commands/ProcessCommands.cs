using System;
using System.ComponentModel;
using sharpclaw.Core.TaskManagement;

namespace sharpclaw.Commands;

/// <summary>
/// Process execution commands.
/// </summary>
public class ProcessCommands : CommandBase
{
    public ProcessCommands(TaskManager taskManager)
        : base(taskManager)
    {
    }

    [Description("Execute Powershell commands")]
    public string CommandPowershell(
    [Description("Arguments to pass to Powershell")] string[] args,
    [Description("Working directory (optional)")] string workingDirectory = "")
    {
        return RunProcess("Powershell", args ?? Array.Empty<string>(), "Powershell " + string.Join(" ", args ?? Array.Empty<string>()),
            true, workingDirectory, 0);
    }
}
