using Microsoft.Extensions.AI;
using sharpclaw.Agents;
using sharpclaw.Commands;
using sharpclaw.Core;
using sharpclaw.Core.TaskManagement;

// ── 配置检测 ──
if (args.Contains("config") || !SharpclawConfig.Exists())
{
    await ConfigWizard.RunAsync();
    if (args.Contains("config"))
        return;
}

var config = SharpclawConfig.Load();

// ── 基础设施 ──
var taskManager = new TaskManager();

// ── 命令工具 ──
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

    processCommands.CommandDotnet,
    processCommands.CommandNodejs,
    processCommands.CommandDocker,

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

// ── AI 客户端 ──
var aiClient = ClientFactory.CreateChatClient(config);

// ── 记忆存储 ──
var memoryStore = ClientFactory.CreateMemoryStore(config);
if (memoryStore is null)
    Console.WriteLine("[Config] 向量记忆已禁用，记忆压缩将使用总结模式");

// ── 启动主智能体 ──
var agent = new MainAgent(aiClient, memoryStore, commandSkills);
await agent.RunAsync();
