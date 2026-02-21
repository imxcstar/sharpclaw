
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using sharpclaw.Agents;
using sharpclaw.Chat;
using sharpclaw.Clients;
using sharpclaw.Commands;
using sharpclaw.Core.TaskManagement;
using sharpclaw.Memory;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

// Initialize task manager
var taskManager = new TaskManager();

// Initialize command handlers
var systemCommands = new SystemCommands(taskManager);
var fileCommands = new FileCommands(taskManager);
var httpCommands = new HttpCommands(taskManager);
var processCommands = new ProcessCommands(taskManager);
var taskCommands = new TaskCommands(taskManager);

// Register all command functions
List<Delegate> commandDelegates = new()
        {
            // System commands
            systemCommands.GetSystemInfo,
            //systemCommands.GetCurrentDirectory,
            //systemCommands.GetEnvironmentVariable,
            //systemCommands.CommandCalc,
            systemCommands.ExitProgram,

            // File commands
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

            // HTTP commands
            httpCommands.CommandHttp,

            // Process commands
            processCommands.CommandDotnet,
            processCommands.CommandNodejs,
            processCommands.CommandDocker,

            // Task management commands
            taskCommands.TaskGetStatus,
            taskCommands.TaskRead,
            taskCommands.TaskWait,
            taskCommands.TaskTerminate,
            taskCommands.TaskList,
            taskCommands.TaskRemove,
            taskCommands.TaskWriteStdin,
            taskCommands.TaskCloseStdin,
        };

var commandSkills = commandDelegates
                .Select(d => AIFunctionFactory.Create(d))
                .ToArray();

var aiClient = new AnthropicClient
{
    AuthToken = Environment.GetEnvironmentVariable("OPENAI_API_KEY")!,
    BaseUrl = "https://api.routin.ai/plan",
}.AsIChatClient("claude-opus-4.6");

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

AIFunction[] tools = [
                AIFunctionFactory.Create(SearchMemory),
                AIFunctionFactory.Create(GetRecentMemories),
                .. commandSkills
            ];

var memorySaver = new MemorySaver(aiClient, memoryStore);
var memoryRecaller = new MemoryRecaller(aiClient, memoryStore);
var summarizer = new ConversationSummarizer(aiClient);

var slidingWindowReducer = new SlidingWindowChatReducer(
    windowSize: 20,
    systemPrompt: """
        你是一个智能助手，拥有长期记忆能力。

        - 系统会自动记录对话中的重要信息到记忆库，你无需手动保存
        - 系统会自动注入相关记忆到上下文中，你可以直接参考这些信息
        - 当你需要主动搜索记忆时，可以使用 SearchMemory 工具
        - 当你需要浏览最近记忆时，可以使用 GetRecentMemories 工具
        """,
    memorySaver: memorySaver,
    summarizer: summarizer);

var agent = new ChatClientBuilder(aiClient)
    .UseFunctionInvocation()
    .BuildAIAgent(new ChatClientAgentOptions()
    {
        ChatOptions = new ChatOptions()
        {
            Tools = tools
        },
        ChatHistoryProviderFactory = (ctx, ct) => new ValueTask<ChatHistoryProvider>(
            new InMemoryChatHistoryProvider(
                slidingWindowReducer,
                ctx.SerializedState,
                ctx.JsonSerializerOptions,
                InMemoryChatHistoryProvider.ChatReducerTriggerEvent.AfterMessageAdded))
    });

var options = new AgentRunOptions()
{
    AllowBackgroundResponses = true
};

AgentSession session;

if (File.Exists("history.json"))
    session = await agent.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(File.ReadAllText("history.json")));
else
    session = await agent.CreateSessionAsync();

Console.OutputEncoding = System.Text.Encoding.UTF8;

while (true)
{
    Console.Write(">");
    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input))
        continue;

    if (input == "/exit" || input == "/quit")
        break;

    // ── 输入消息时触发记忆回忆器，注入相关记忆 ──
    var inputMessages = new List<ChatMessage>();
    try
    {
        var memoryMsg = await memoryRecaller.RecallAsync(input);
        if (memoryMsg is not null)
            inputMessages.Add(memoryMsg);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[AutoRecall] 回忆失败: {ex.Message}");
    }
    inputMessages.Add(new ChatMessage(ChatRole.User, input));

    Console.Write("AI: ");
    await foreach (var update in agent.RunStreamingAsync(inputMessages, session))
    {
        foreach (var content in update.Contents)
        {
            if (content is TextContent text)
            {
                Console.Write(text.Text);
            }
            else if (content is TextReasoningContent textReasoningContent)
            {
                Console.WriteLine($"\n[Reasoning] {textReasoningContent.Text}");
            }
            else if (content is FunctionCallContent functionCall)
            {
                var callId = functionCall.CallId;
                var functionName = functionCall.Name;
                var arguments = JsonSerializer.Serialize(functionCall.Arguments);
                Console.WriteLine($"\n[Function Call({callId})] {functionName}({arguments})");
            }
            else if (content is FunctionResultContent functionResult)
            {
                var callId = functionResult.CallId;
                var result = JsonSerializer.Serialize(functionResult.Result);
                Console.WriteLine($"\n[Function Result({callId})] {result}");
            }
        }
    }

    File.WriteAllText("history.json", JsonSerializer.Serialize(await agent.SerializeSessionAsync(session)));
    Console.WriteLine();
}

[Description("搜索长期记忆库，查找与查询相关的记忆。当用户提到之前讨论过的话题、或你需要回顾历史信息时使用。")]
async Task<string> SearchMemory(
    [Description("搜索关键词或语义查询")] string query,
    [Description("最多返回几条结果")] int count = 10)
{
    var results = await memoryStore.SearchAsync(query, count);
    if (results.Count == 0)
        return "没有找到相关记忆。";

    var sb = new StringBuilder();
    sb.AppendLine($"找到 {results.Count} 条相关记忆：");
    foreach (var m in results)
    {
        var age = FormatAge(m.CreatedAt);
        sb.AppendLine($"- [{m.Category}](重要度:{m.Importance}, {age}) {m.Content}");
    }
    return sb.ToString();
}

[Description("查看最近保存的记忆。当需要浏览记忆库内容但没有明确搜索词时使用。")]
async Task<string> GetRecentMemories(
    [Description("返回最近几条记忆")] int count = 10)
{
    var results = await memoryStore.GetRecentAsync(count);
    if (results.Count == 0)
        return "记忆库为空。";

    var sb = new StringBuilder();
    sb.AppendLine($"最近 {results.Count} 条记忆：");
    foreach (var m in results)
    {
        var age = FormatAge(m.CreatedAt);
        sb.AppendLine($"- [{m.Category}](重要度:{m.Importance}, {age}) {m.Content}");
    }
    return sb.ToString();
}

static string FormatAge(DateTimeOffset created)
{
    var age = DateTimeOffset.UtcNow - created;
    if (age.TotalMinutes < 1) return "刚刚";
    if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}分钟前";
    if (age.TotalHours < 24) return $"{(int)age.TotalHours}小时前";
    return $"{(int)age.TotalDays}天前";
}
