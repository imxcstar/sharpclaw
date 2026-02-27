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
/// 主智能体：集成记忆管线（保存、回忆、总结、主要记忆）和命令工具，通过 ChatWindow 进行 I/O。
/// </summary>
public class MainAgent
{
    private static readonly string SystemPrompt = """
        你是 Sharpclaw，一个拥有长期记忆和系统操作能力的 AI 助手。

        你的核心能力：
        - 长期记忆：你能跨对话记住用户的偏好、事实、决策等重要信息，不会因为对话窗口滑动而遗忘
        - 系统操作：你可以执行文件管理、运行程序、发起网络请求等操作，帮助用户完成实际任务
        - 任务管理：你可以在后台运行长时间任务，并随时查看进度

        关于记忆系统：
        - 系统会自动记录对话中的重要信息到记忆库，你无需手动保存
        - 系统会自动注入相关记忆到上下文中，你可以直接参考这些信息
        - 当你需要主动搜索记忆时，可以使用 SearchMemory 工具
        - 当你需要浏览最近记忆时，可以使用 GetRecentMemories 工具
        """;

    private readonly ChatClientAgent _agent;
    private readonly IChatIO _chatIO;
    private readonly string _workingMemoryPath;
    private readonly MemoryPipelineChatReducer _reducer;
    private InMemoryChatHistoryProvider? _historyProvider;
    private AgentSession? _session;

    public MainAgent(
        SharpclawConfig config,
        IMemoryStore? memoryStore,
        AIFunction[] commandSkills,
        IChatIO chatIO)
    {
        _workingMemoryPath = Path.Combine(
            Path.GetDirectoryName(SharpclawConfig.ConfigPath)!, "working_memory.md");
        _chatIO = chatIO;

        // 主要记忆文件路径
        var sharpclawDir = Path.GetDirectoryName(SharpclawConfig.ConfigPath)!;
        var recentMemoryPath = Path.Combine(sharpclawDir, "recent_memory.md");
        var primaryMemoryPath = Path.Combine(sharpclawDir, "primary_memory.md");

        // 按智能体创建各自的 AI 客户端
        var mainClient = ClientFactory.CreateAgentClient(config, config.Agents.Main);

        MemorySaver? memorySaver = null;
        AIFunction[] memoryTools = [];

        var fileToolNames = new HashSet<string>
        {
            "CommandCat", "CommandCreateText", "AppendToFile",
            "FileExists", "CommandDir", "CommandEditText", "SearchInFiles"
        };
        var fileTools = commandSkills.Where(t => fileToolNames.Contains(t.Name)).ToArray();

        if (memoryStore is not null)
        {
            if (config.Agents.Saver.Enabled)
            {
                var saverClient = ClientFactory.CreateAgentClient(config, config.Agents.Saver);
                memorySaver = new MemorySaver(saverClient, memoryStore,
                    _workingMemoryPath, recentMemoryPath, primaryMemoryPath, fileTools);
            }

            memoryTools = CreateMemoryTools(memoryStore);
        }

        ConversationArchiver? archiver = null;
        if (config.Agents.Summarizer.Enabled)
        {
            var archiverClient = ClientFactory.CreateAgentClient(config, config.Agents.Summarizer);
            AIFunction[] archiverTools = [.. fileTools, .. memoryTools];
            archiver = new ConversationArchiver(
                archiverClient, _workingMemoryPath, recentMemoryPath, primaryMemoryPath, archiverTools);
        }

        AIFunction[] tools = [.. memoryTools, .. commandSkills];

        _reducer = new MemoryPipelineChatReducer(
            resetThreshold: 30,
            systemPrompt: SystemPrompt,
            archiver: archiver,
            memorySaver: memorySaver);
        _reducer.WorkingMemoryPath = _workingMemoryPath;

        _agent = new ChatClientBuilder(mainClient)
            .UseFunctionInvocation()
            .UseChatReducer(_reducer)
            .BuildAIAgent(new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = SystemPrompt,
                    Tools = tools
                },
                ChatHistoryProviderFactory = (ctx, ct) =>
                {
                    _historyProvider = new InMemoryChatHistoryProvider(
                        _reducer,
                        ctx.SerializedState,
                        ctx.JsonSerializerOptions,
                        InMemoryChatHistoryProvider.ChatReducerTriggerEvent.BeforeMessagesRetrieval
                    );
                    return new ValueTask<ChatHistoryProvider>(_historyProvider);
                }
            });
    }

    /// <summary>
    /// 启动对话循环：等待 ChatWindow 输入 → 处理 → 输出。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _chatIO.WaitForReadyAsync();

        _session = await _agent.CreateSessionAsync();
        if (File.Exists(_workingMemoryPath))
        {
            _reducer.OldWorkingMemoryContent = File.ReadAllText(_workingMemoryPath);

            if (!string.IsNullOrWhiteSpace(_reducer.OldWorkingMemoryContent))
                _reducer.WorkingMemoryBuffer.Append(_reducer.OldWorkingMemoryContent + "\n\n---\n\n");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var input = await _chatIO.ReadInputAsync(cancellationToken);
                if (string.IsNullOrEmpty(input))
                    continue;

                var cmdResult = await _chatIO.HandleCommandAsync(input);
                if (cmdResult == CommandResult.Exit)
                    break;
                if (cmdResult == CommandResult.Handled)
                    continue;

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
        _chatIO.EchoUserInput(input);
        _chatIO.ShowRunning();

        using var aiCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _chatIO.GetAiCancellationToken());
        var aiToken = aiCts.Token;

        var inputMessages = new List<ChatMessage>
        {
            new(ChatRole.User, input)
        };

        var buffer = new StringBuilder();
        string? bufferType = null;
        void Flush()
        {
            if (buffer.Length == 0) return;
            AppLogger.Log($"[Main]: {buffer}");
            buffer.Clear();
            bufferType = null;
        }

        void Append(string type, string text)
        {
            if (bufferType != type)
                Flush();
            bufferType = type;
            buffer.Append(text);
        }

        // 流式输出
        _reducer.UserInput = input;
        _reducer.WorkingMemoryBuffer.Append($"### 用户\n\n{input}\n\n");
        AIContent? lastContent = null;
        AppLogger.SetStatus("AI 思考中...");
        _chatIO.BeginAiResponse();
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
                            if (lastContent is not TextContent)
                                _reducer.WorkingMemoryBuffer.Append("### 助手\n\n");
                            _reducer.WorkingMemoryBuffer.Append(text.Text);
                            break;
                        case TextReasoningContent reasoning:
                            AppLogger.SetStatus($"[Main]思考中...");
                            Append("Reasoning", reasoning.Text);
                            break;
                        case FunctionCallContent call:
                            AppLogger.SetStatus($"[Main]调用工具: {call.Name}");
                            AppLogger.Log($"[Main]调用工具: {call.Name}");
                            var args = call.Arguments is not null
                                ? JsonSerializer.Serialize(call.Arguments)
                                : "";
                            _reducer.WorkingMemoryBuffer.Append($"#### 工具调用: {call.Name}\n\n参数: `{args}`\n\n");
                            break;
                        case FunctionResultContent result:
                            _reducer.WorkingMemoryBuffer.Append($"<details>\n<summary>执行结果</summary>\n\n```\n{result.Result?.ToString() ?? ""}\n```\n\n</details>\n\n");
                            break;
                    }
                    lastContent = content;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _chatIO.AppendChat("\n[已取消]\n");
            return;
        }
        _chatIO.AppendChat("\n");
        _reducer.WorkingMemoryBuffer.Append("\n\n---\n\n");

        // 持久化工作记忆
        try
        {
            File.WriteAllText(_workingMemoryPath, _reducer.WorkingMemoryBuffer.ToString());
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WorkingMemory] 保存失败: {ex.Message}");
        }
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
