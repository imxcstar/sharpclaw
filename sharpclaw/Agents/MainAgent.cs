using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using sharpclaw.Chat;
using sharpclaw.Memory;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace sharpclaw.Agents;

/// <summary>
/// 主智能体：集成记忆管线（保存、回忆、总结）和命令工具，提供流式对话能力。
/// </summary>
public class MainAgent
{
    private static readonly string SystemPrompt = """
        你是一个智能助手，拥有长期记忆能力。

        - 系统会自动记录对话中的重要信息到记忆库，你无需手动保存
        - 系统会自动注入相关记忆到上下文中，你可以直接参考这些信息
        - 当你需要主动搜索记忆时，可以使用 SearchMemory 工具
        - 当你需要浏览最近记忆时，可以使用 GetRecentMemories 工具
        """;

    private readonly ChatClientAgent _agent;
    private readonly MemoryRecaller _memoryRecaller;
    private readonly string _historyPath;
    private AgentSession? _session;

    /// <summary>
    /// 创建主智能体。
    /// </summary>
    /// <param name="aiClient">AI 聊天客户端</param>
    /// <param name="memoryStore">记忆存储</param>
    /// <param name="commandSkills">命令工具数组</param>
    /// <param name="historyPath">会话历史持久化路径</param>
    public MainAgent(
        IChatClient aiClient,
        IMemoryStore memoryStore,
        AIFunction[] commandSkills,
        string historyPath = "history.json")
    {
        _historyPath = historyPath;
        _memoryRecaller = new MemoryRecaller(aiClient, memoryStore);

        var memorySaver = new MemorySaver(aiClient, memoryStore);
        var summarizer = new ConversationSummarizer(aiClient);

        // 记忆工具：供主智能体主动搜索/浏览记忆
        var memoryTools = CreateMemoryTools(memoryStore);

        AIFunction[] tools = [.. memoryTools, .. commandSkills];

        var reducer = new SlidingWindowChatReducer(
            windowSize: 20,
            systemPrompt: SystemPrompt,
            memorySaver: memorySaver,
            summarizer: summarizer);

        _agent = new ChatClientBuilder(aiClient)
            .UseFunctionInvocation()
            .BuildAIAgent(new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { Tools = tools },
                ChatHistoryProviderFactory = (ctx, ct) => new ValueTask<ChatHistoryProvider>(
                    new InMemoryChatHistoryProvider(
                        reducer,
                        ctx.SerializedState,
                        ctx.JsonSerializerOptions,
                        InMemoryChatHistoryProvider.ChatReducerTriggerEvent.AfterMessageAdded))
            });
    }

    /// <summary>
    /// 启动对话循环。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _session = File.Exists(_historyPath)
            ? await _agent.DeserializeSessionAsync(
                JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(_historyPath)))
            : await _agent.CreateSessionAsync();

        Console.OutputEncoding = Encoding.UTF8;

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write(">");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                continue;

            if (input is "/exit" or "/quit")
                break;

            await ProcessTurnAsync(input, cancellationToken);
        }
    }

    /// <summary>
    /// 处理单轮对话：回忆注入 → 流式输出 → 持久化会话。
    /// </summary>
    private async Task ProcessTurnAsync(string input, CancellationToken cancellationToken)
    {
        // 输入消息时触发记忆回忆器
        var inputMessages = new List<ChatMessage>();
        try
        {
            var memoryMsg = await _memoryRecaller.RecallAsync(input, cancellationToken: cancellationToken);
            if (memoryMsg is not null)
                inputMessages.Add(memoryMsg);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AutoRecall] 回忆失败: {ex.Message}");
        }
        inputMessages.Add(new ChatMessage(ChatRole.User, input));

        // 流式输出
        Console.Write("AI: ");
        await foreach (var update in _agent.RunStreamingAsync(inputMessages, _session!).WithCancellation(cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        Console.Write(text.Text);
                        break;
                    case TextReasoningContent reasoning:
                        Console.WriteLine($"\n[Reasoning] {reasoning.Text}");
                        break;
                    case FunctionCallContent call:
                        Console.WriteLine($"\n[Function Call({call.CallId})] {call.Name}({JsonSerializer.Serialize(call.Arguments)})");
                        break;
                    case FunctionResultContent result:
                        Console.WriteLine($"\n[Function Result({result.CallId})] {JsonSerializer.Serialize(result.Result)}");
                        break;
                }
            }
        }
        Console.WriteLine();

        // 持久化会话
        var serialized = JsonSerializer.Serialize(await _agent.SerializeSessionAsync(_session!));
        File.WriteAllText(_historyPath, serialized);
    }

    /// <summary>
    /// 创建记忆相关的工具函数。
    /// </summary>
    private static AIFunction[] CreateMemoryTools(IMemoryStore memoryStore)
    {
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
                sb.AppendLine($"- [{m.Category}](重要度:{m.Importance}, {FormatAge(m.CreatedAt)}) {m.Content}");
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
                sb.AppendLine($"- [{m.Category}](重要度:{m.Importance}, {FormatAge(m.CreatedAt)}) {m.Content}");
            return sb.ToString();
        }

        return
        [
            AIFunctionFactory.Create(SearchMemory),
            AIFunctionFactory.Create(GetRecentMemories),
        ];
    }

    private static string FormatAge(DateTimeOffset created)
    {
        var age = DateTimeOffset.UtcNow - created;
        if (age.TotalMinutes < 1) return "刚刚";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}分钟前";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}小时前";
        return $"{(int)age.TotalDays}天前";
    }
}
