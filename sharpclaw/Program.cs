using sharpclaw.Commands;
using sharpclaw.Core;
using sharpclaw.Core.TaskManagement;
using sharpclaw.UI;
using Microsoft.Extensions.AI;
using Terminal.Gui.App;

// ── Terminal.Gui 初始化 ──
using var app = Application.Create().Init();

// ── 配置检测 ──
if (args.Contains("config") || !SharpclawConfig.Exists())
{
    var configDialog = new ConfigDialog();
    app.Run(configDialog);
    configDialog.Dispose();

    if (!configDialog.Saved || args.Contains("config"))
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
    AppLogger.Log("[Config] 向量记忆已禁用，记忆压缩将使用总结模式");

// ── 创建 ChatWindow 并启动主智能体 ──
var chatWindow = new ChatWindow();
var agent = new sharpclaw.Agents.MainAgent(aiClient, memoryStore, commandSkills, chatWindow);

// 在后台线程启动智能体循环
_ = Task.Run(() => agent.RunAsync());

// 运行 Terminal.Gui 主循环（阻塞直到退出）
app.Run(chatWindow);
chatWindow.Dispose();
