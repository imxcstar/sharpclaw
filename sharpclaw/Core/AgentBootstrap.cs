using AgentSkillsDotNet;
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
        AITool[] CommandSkills,
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

            fileCommands.CommandDir,
            fileCommands.CommandGetLineCount,
            fileCommands.FileExists,
            fileCommands.GetFileInfo,
            fileCommands.AppendToFile,
            fileCommands.CommandRenameFile,
            fileCommands.CommandMkdir,
            fileCommands.CommandDelete,

            // Claude Code 风格工具
            fileCommands.EditByMatch,
            fileCommands.Grep,
            fileCommands.GlobFiles,
            fileCommands.WriteFile,
            fileCommands.ReadFile,

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

        var skillsDirPath = agentContext.GetSkillsDirPath();
        if (!Directory.Exists(skillsDirPath))
            Directory.CreateDirectory(skillsDirPath);

        var agentSkillsFactory = new AgentSkillsFactory();
        var agentSkills = agentSkillsFactory.GetAgentSkills(skillsDirPath);
        var skillTools = agentSkills.GetAsTools();

        var memoryStore = ClientFactory.CreateMemoryStore(config);

        return new BootstrapResult(config, taskManager, [.. commandSkills, .. skillTools], memoryStore, agentContext);
    }
}
