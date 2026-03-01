using Microsoft.Extensions.AI;
using sharpclaw.Commands;
using sharpclaw.Core.TaskManagement;
using sharpclaw.Memory;

namespace sharpclaw.Core;

/// <summary>
/// 共享初始化逻辑：配置加载、命令工具创建、记忆存储创建。
/// TUI 和 WebServer 共用。
/// </summary>
public static class AgentBootstrap
{
    public record BootstrapResult(
        SharpclawConfig Config,
        TaskManager TaskManager,
        AIFunction[] CommandSkills,
        IMemoryStore? MemoryStore,
        IAgentContext AgentContext);

    public static BootstrapResult Initialize()
    {
        var config = SharpclawConfig.Load();
        var taskManager = new TaskManager();
        var agentContext = new AgentContext();

        var systemCommands = new SystemCommands(taskManager, agentContext);
        var fileCommands = new FileCommands(taskManager, agentContext);
        var httpCommands = new HttpCommands(taskManager, agentContext);
        var processCommands = new ProcessCommands(taskManager, agentContext);
        var taskCommands = new TaskCommands(taskManager, agentContext);

        var commandSkillDelegates = new List<Delegate>
        {
            systemCommands.GetSystemInfo,
            systemCommands.ExitProgram,

            fileCommands.CommandDir,
            fileCommands.CommandGetLineCount,
            fileCommands.CommandCat,
            fileCommands.FileExists,
            fileCommands.GetFileInfo,
            fileCommands.FindFiles,
            fileCommands.SearchInFiles,
            fileCommands.CommandCreateText,
            fileCommands.AppendToFile,
            fileCommands.CommandEditText,
            fileCommands.CommandRenameFile,
            fileCommands.CommandMkdir,
            fileCommands.CommandDelete,

            httpCommands.CommandHttp,

            taskCommands.TaskGetStatus,
            taskCommands.TaskRead,
            taskCommands.TaskWait,
            taskCommands.TaskTerminate,
            taskCommands.TaskList,
            taskCommands.TaskRemove,
            taskCommands.TaskWriteStdin,
            taskCommands.TaskCloseStdin,
        };

        if (OperatingSystem.IsWindows())
        {
            commandSkillDelegates.Add(processCommands.CommandPowershell);
        }
        else
        {
            commandSkillDelegates.Add(processCommands.CommandBash);
        }

        var commandSkills = commandSkillDelegates
        .Select(d => AIFunctionFactory.Create(d))
        .ToArray();

        var memoryStore = ClientFactory.CreateMemoryStore(config);

        return new BootstrapResult(config, taskManager, commandSkills, memoryStore, agentContext);
    }
}
