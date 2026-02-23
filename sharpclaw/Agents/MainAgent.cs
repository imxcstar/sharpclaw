using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using sharpclaw.Chat;
using sharpclaw.Memory;
using sharpclaw.UI;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace sharpclaw.Agents;

/// <summary>
/// 主智能体：集成记忆管线（保存、回忆、总结）和命令工具，通过 ChatWindow 进行 I/O。
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
    private readonly MemoryRecaller? _memoryRecaller;
    private readonly ChatWindow _chatWindow;
    private readonly string _historyPath;
    private AgentSession? _session;

    public MainAgent(
        IChatClient aiClient,
        IMemoryStore? memoryStore,
        AIFunction[] commandSkills,
        ChatWindow chatWindow,
        string historyPath = "history.json")
    {
        _historyPath = historyPath;
        _chatWindow = chatWindow;

        MemorySaver? memorySaver = null;
        AIFunction[] memoryTools = [];

        if (memoryStore is not null)
        {
            _memoryRecaller = new MemoryRecaller(aiClient, memoryStore);
            memorySaver = new MemorySaver(aiClient, memoryStore);
            memoryTools = CreateMemoryTools(memoryStore);
        }

        var summarizer = new ConversationSummarizer(aiClient);

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
    /// 启动对话循环：等待 ChatWindow 输入 → 处理 → 输出。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _session = File.Exists(_historyPath)
            ? await _agent.DeserializeSessionAsync(
                JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(_historyPath)))
            : await _agent.CreateSessionAsync();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var input = await _chatWindow.ReadInputAsync(cancellationToken);
                if (string.IsNullOrEmpty(input))
                    continue;

                if (input is "/exit" or "/quit")
                {
                    _chatWindow.App?.Invoke(() => _chatWindow.RequestStop());
                    break;
                }

                await ProcessTurnAsync(input, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Error] {ex.Message}");
            }
        }
    }

    private async Task ProcessTurnAsync(string input, CancellationToken cancellationToken)
    {
        _chatWindow.AppendChatLine($"> {input}\n");
        _chatWindow.DisableInput();

        // 记忆回忆
        var inputMessages = new List<ChatMessage>();
        if (_memoryRecaller is not null)
        {
            try
            {
                var memoryMsg = await _memoryRecaller.RecallAsync(input, cancellationToken: cancellationToken);
                if (memoryMsg is not null)
                    inputMessages.Add(memoryMsg);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AutoRecall] 回忆失败: {ex.Message}");
            }
        }
        inputMessages.Add(new ChatMessage(ChatRole.User, input));

        // 流式输出
        _chatWindow.AppendChat("AI: ");
        await foreach (var update in _agent.RunStreamingAsync(inputMessages, _session!).WithCancellation(cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        _chatWindow.AppendChat(text.Text);
                        break;
                    case TextReasoningContent reasoning:
                        AppLogger.Log($"[Reasoning] {reasoning.Text}");
                        break;
                    case FunctionCallContent call:
                        AppLogger.Log($"[Call] {call.Name}({JsonSerializer.Serialize(call.Arguments)})");
                        break;
                    case FunctionResultContent result:
                        AppLogger.Log($"[Result({result.CallId})] {JsonSerializer.Serialize(result.Result)}");
                        break;
                }
            }
        }
        _chatWindow.AppendChat("\n");

        // 持久化会话
        var serialized = JsonSerializer.Serialize(await _agent.SerializeSessionAsync(_session!));
        File.WriteAllText(_historyPath, serialized);
    }

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
