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
        IMemoryStore? MemoryStore);

    public static BootstrapResult Initialize()
    {
        var config = SharpclawConfig.Load();
        var taskManager = new TaskManager();

        var systemCommands = new SystemCommands(taskManager);
        var fileCommands = new FileCommands(taskManager);
        var httpCommands = new HttpCommands(taskManager);
        var processCommands = new ProcessCommands(taskManager);
        var taskCommands = new TaskCommands(taskManager);

        var commandSkills = new List<Delegate>
        {
            systemCommands.GetSystemInfo,
            systemCommands.ExitProgram,

            fileCommands.CommandDir,
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

            processCommands.CommandPowershell,

            taskCommands.TaskGetStatus,
            taskCommands.TaskRead,
            taskCommands.TaskWait,
            taskCommands.TaskTerminate,
            taskCommands.TaskList,
            taskCommands.TaskRemove,
            taskCommands.TaskWriteStdin,
            taskCommands.TaskCloseStdin,
        }
        .Select(d => AIFunctionFactory.Create(d))
        .ToArray();

        var memoryStore = ClientFactory.CreateMemoryStore(config);

        return new BootstrapResult(config, taskManager, commandSkills, memoryStore);
    }
}
