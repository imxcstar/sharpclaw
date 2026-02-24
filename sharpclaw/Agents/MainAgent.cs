using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using sharpclaw.Abstractions;
using sharpclaw.Chat;
using sharpclaw.Core;
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
    private readonly IChatIO _chatIO;
    private readonly string _historyPath;
    private AgentSession? _session;

    public MainAgent(
        SharpclawConfig config,
        IMemoryStore? memoryStore,
        AIFunction[] commandSkills,
        IChatIO chatIO,
        string historyPath = "history.json")
    {
        _historyPath = historyPath;
        _chatIO = chatIO;

        // 按智能体创建各自的 AI 客户端
        var mainClient = ClientFactory.CreateAgentClient(config, config.Agents.Main);

        MemorySaver? memorySaver = null;
        AIFunction[] memoryTools = [];

        if (memoryStore is not null)
        {
            if (config.Agents.Recaller.Enabled)
            {
                var recallerClient = ClientFactory.CreateAgentClient(config, config.Agents.Recaller);
                _memoryRecaller = new MemoryRecaller(recallerClient, memoryStore);
            }

            if (config.Agents.Saver.Enabled)
            {
                var saverClient = ClientFactory.CreateAgentClient(config, config.Agents.Saver);
                memorySaver = new MemorySaver(saverClient, memoryStore);
            }

            memoryTools = CreateMemoryTools(memoryStore);
        }

        ConversationSummarizer? summarizer = null;
        if (config.Agents.Summarizer.Enabled)
        {
            var summarizerClient = ClientFactory.CreateAgentClient(config, config.Agents.Summarizer);
            summarizer = new ConversationSummarizer(summarizerClient);
        }

        AIFunction[] tools = [.. memoryTools, .. commandSkills];

        var reducer = new SlidingWindowChatReducer(
            windowSize: 20,
            systemPrompt: SystemPrompt,
            memorySaver: memorySaver,
            summarizer: summarizer);

        _agent = new ChatClientBuilder(mainClient)
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
        await _chatIO.WaitForReadyAsync();

        _session = File.Exists(_historyPath)
            ? await _agent.DeserializeSessionAsync(
                JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(_historyPath)))
            : await _agent.CreateSessionAsync();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var input = await _chatIO.ReadInputAsync(cancellationToken);
                if (string.IsNullOrEmpty(input))
                    continue;

                if (input is "/exit" or "/quit")
                {
                    _chatIO.RequestStop();
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
        _chatIO.AppendChatLine($"> {input}\n");
        _chatIO.ShowRunning();

        using var aiCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _chatIO.GetAiCancellationToken());
        var aiToken = aiCts.Token;

        // 记忆回忆
        AppLogger.SetStatus("记忆回忆中...");
        var inputMessages = new List<ChatMessage>();
        if (_memoryRecaller is not null)
        {
            try
            {
                var memoryMsg = await _memoryRecaller.RecallAsync(input, cancellationToken: aiToken);
                if (memoryMsg is not null)
                    inputMessages.Add(memoryMsg);
            }
            catch (OperationCanceledException)
            {
                _chatIO.AppendChat("\n[已取消]\n");
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AutoRecall] 回忆失败: {ex.Message}");
            }
        }
        inputMessages.Add(new ChatMessage(ChatRole.User, input));

        // 流式输出
        AppLogger.SetStatus("AI 思考中...");
        _chatIO.AppendChat("AI: ");
        try
        {
            await foreach (var update in _agent.RunStreamingAsync(inputMessages, _session!).WithCancellation(aiToken))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text:
                            _chatIO.AppendChat(text.Text);
                            break;
                        case TextReasoningContent reasoning:
                            AppLogger.Log($"[Reasoning] {reasoning.Text}");
                            break;
                        case FunctionCallContent call:
                            AppLogger.SetStatus($"调用工具: {call.Name}");
                            AppLogger.Log($"[Call] {call.Name}({JsonSerializer.Serialize(call.Arguments)})");
                            break;
                        case FunctionResultContent result:
                            AppLogger.Log($"[Result({result.CallId})] {JsonSerializer.Serialize(result.Result)}");
                            AppLogger.SetStatus("AI 思考中...");
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _chatIO.AppendChat("\n[已取消]\n");
            return;
        }
        _chatIO.AppendChat("\n");

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
