
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;
using sharpclaw.Agents;
using sharpclaw.Clients;
using sharpclaw.Commands;
using sharpclaw.Core.TaskManagement;
using sharpclaw.Memory;

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
var aiClient = new AnthropicClient
{
    AuthToken = Environment.GetEnvironmentVariable("OPENAI_API_KEY")!,
    BaseUrl = "https://api.routin.ai/plan",
}.AsIChatClient("claude-opus-4.6");

// ── 记忆存储 ──
var dashScopeApiKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY")!;

var embeddingGenerator = new OpenAIClient(
        new System.ClientModel.ApiKeyCredential(dashScopeApiKey),
        new OpenAIClientOptions
        {
            Endpoint = new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1")
        })
    .GetEmbeddingClient("text-embedding-v4")
    .AsIEmbeddingGenerator();

var rerankClient = new DashScopeRerankClient(
    new HttpClient(), dashScopeApiKey, "qwen3-vl-rerank");

var memoryStore = new VectorMemoryStore(
    embeddingGenerator,
    filePath: "memories.json",
    rerankClient: rerankClient);

// ── 启动主智能体 ──
var agent = new MainAgent(aiClient, memoryStore, commandSkills);
await agent.RunAsync();
