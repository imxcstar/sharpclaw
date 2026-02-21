using System;
using System.ComponentModel;
using sharpclaw.Core.TaskManagement;

namespace sharpclaw.Commands;

/// <summary>
/// Process execution commands (dotnet, node, docker).
/// </summary>
public class ProcessCommands : CommandBase
{
    public ProcessCommands(TaskManager taskManager)
        : base(taskManager)
    {
    }

    [Description("Execute dotnet commands")]
    public string CommandDotnet(
        [Description("Arguments to pass to dotnet")] string[] args,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        return RunProcess("dotnet", args ?? Array.Empty<string>(), "dotnet " + string.Join(" ", args ?? Array.Empty<string>()),
            true, workingDirectory, 0);
    }

    [Description("Execute Node.js commands")]
    public string CommandNodejs(
        [Description("Arguments to pass to node")] string[] args,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        return RunProcess("node", args ?? Array.Empty<string>(), "node " + string.Join(" ", args ?? Array.Empty<string>()),
            true, workingDirectory, 0);
    }

    [Description("Execute Docker commands")]
    public string CommandDocker(
        [Description("Arguments to pass to docker")] string[] args,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        return RunProcess("docker", args ?? Array.Empty<string>(), "docker " + string.Join(" ", args ?? Array.Empty<string>()),
            true, workingDirectory, 0);
    }
}
