using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text;

using sharpclaw.Memory;
using sharpclaw.UI;

namespace sharpclaw.Agents;

/// <summary>
/// 记忆回忆器：作为 AIContextProvider，在每次智能体调用前自动注入相关记忆。
/// 使用回忆智能体通过工具调用来管理记忆的增量更新。
/// </summary>
public class MemoryRecaller : AIContextProvider
{
    private static readonly string RecallAgentPrompt = """
        你是一个记忆注入助手。根据当前对话内容和已注入的记忆，决定如何更新注入给主智能体的记忆。

        你的任务：
        1. 判断已注入的记忆中哪些仍然与当前对话相关
        2. 根据最新对话内容，搜索可能需要的新记忆

        使用工具：
        - KeepMemories: 声明要保留哪些已注入的记忆（不调用则默认全部保留）
        - SearchMemory: 搜索记忆库获取新的相关记忆（可多次调用）

        注意：
        - 只保留与当前对话主题相关的记忆
        - 搜索查询应该是简短的关键词或短语
        - 如果对话很简单（问候、闲聊），可以移除所有记忆且不搜索
        - 不需要每次都搜索，只在话题变化或需要新信息时搜索
        """;

    private readonly IChatClient _client;
    private readonly IMemoryStore _memoryStore;
    private readonly string? _primaryMemoryPath;

    private List<MemoryEntry> _currentMemories = [];
    private readonly List<ChatMessage> _conversationHistory = [];

    public int MaxMemories { get; set; } = 10;

    public MemoryRecaller(IChatClient baseClient, IMemoryStore memoryStore, string? primaryMemoryPath = null)
        : base("MemoryRecaller")
    {
        _client = new ChatClientBuilder(baseClient)
            .UseFunctionInvocation()
            .Build();
        _memoryStore = memoryStore;
        _primaryMemoryPath = primaryMemoryPath;
    }

    protected override async ValueTask<AIContext> InvokingCoreAsync(
        AIContextProvider.InvokingContext context, CancellationToken cancellationToken = default)
    {
        // 提取用户输入
        var userMessage = context.RequestMessages.LastOrDefault(m => m.Role == ChatRole.User);
        var userInput = userMessage?.Text ?? "";

        var memoryCount = await _memoryStore.CountAsync(cancellationToken);
        if (memoryCount == 0 && _currentMemories.Count == 0)
        {
            AppLogger.Log("[AutoRecall] 记忆库为空，跳过");
            return new AIContext();
        }

        // ── 工具闭包状态 ──
        var keepCalled = false;
        List<int>? keepIndices = null;
        var searchedMemories = new List<MemoryEntry>();
        var seenIds = new HashSet<string>();

        [Description("声明要保留哪些已注入的记忆。传入要保留的记忆编号列表（从1开始）。传空数组表示全部移除。不调用此工具则默认全部保留。")]
        string KeepMemories(
            [Description("要保留的记忆编号列表，如 [1, 3]。传空数组 [] 表示全部移除")] int[] indices)
        {
            keepCalled = true;
            keepIndices = indices.Select(i => i - 1).ToList();
            var kept = indices.Length == 0 ? "无" : string.Join(",", indices);
            AppLogger.Log($"[AutoRecall] 保留记忆: {kept}");
            return $"已记录，保留 {indices.Length} 条记忆";
        }

        [Description("搜索记忆库，查找与查询相关的记忆用于注入。可多次调用以搜索不同主题。")]
        async Task<string> SearchMemory(
            [Description("搜索关键词或短语")] string query)
        {
            AppLogger.Log($"[AutoRecall] 搜索: {query}");
            var results = await _memoryStore.SearchAsync(query, MaxMemories, cancellationToken);
            foreach (var m in results)
            {
                if (seenIds.Add(m.Id))
                    searchedMemories.Add(m);
            }
            if (results.Count == 0)
                return "未找到相关记忆";

            var sb = new StringBuilder();
            sb.AppendLine($"找到 {results.Count} 条相关记忆：");
            foreach (var m in results)
                sb.AppendLine($"- [{m.Category}](重要度:{m.Importance}) {m.Content}");
            return sb.ToString();
        }

        // ── 构建回忆智能体输入 ──
        AppLogger.SetStatus("记忆回忆中...");
        var recallMessages = new List<ChatMessage>
        {
            new(ChatRole.System, RecallAgentPrompt),
        };

        foreach (var msg in _conversationHistory)
            recallMessages.Add(new ChatMessage(msg.Role, msg.Text));

        var sb2 = new StringBuilder();
        sb2.AppendLine(userInput);
        sb2.AppendLine();
        if (_currentMemories.Count > 0)
        {
            sb2.AppendLine("## 当前已注入的记忆");
            for (var i = 0; i < _currentMemories.Count; i++)
            {
                var m = _currentMemories[i];
                sb2.AppendLine($"[{i + 1}] [{m.Category}](重要度:{m.Importance}) {m.Content}");
            }
        }
        else
        {
            sb2.AppendLine("## 当前无已注入记忆");
        }
        recallMessages.Add(new ChatMessage(ChatRole.User, sb2.ToString()));

        var options = new ChatOptions
        {
            Instructions = RecallAgentPrompt,
            Tools =
            [
                AIFunctionFactory.Create(KeepMemories),
                AIFunctionFactory.Create(SearchMemory),
            ]
        };

        var agent = _client.AsBuilder().UseFunctionInvocation().BuildAIAgent(new Microsoft.Agents.AI.ChatClientAgentOptions()
        {
            ChatOptions = options
        });

        await agent.RunAsync(recallMessages, cancellationToken: cancellationToken);

        // ── 处理结果 ──
        List<MemoryEntry> keptMemories;
        if (!keepCalled)
        {
            keptMemories = new List<MemoryEntry>(_currentMemories);
        }
        else if (keepIndices is null or [])
        {
            keptMemories = [];
        }
        else
        {
            keptMemories = keepIndices
                .Where(i => i >= 0 && i < _currentMemories.Count)
                .Select(i => _currentMemories[i])
                .ToList();
        }

        AppLogger.Log($"[AutoRecall] 保留 {keptMemories.Count}/{_currentMemories.Count} 条旧记忆");

        var keptIds = new HashSet<string>(keptMemories.Select(m => m.Id));
        var remaining = MaxMemories - keptMemories.Count;
        var topNew = searchedMemories
            .Where(m => !keptIds.Contains(m.Id))
            .OrderByDescending(m => m.Importance)
            .Take(Math.Max(0, remaining))
            .ToList();

        _currentMemories = [.. keptMemories, .. topNew];

        // 加载主要记忆
        var primaryMemory = "";
        if (_primaryMemoryPath is not null && File.Exists(_primaryMemoryPath))
        {
            try
            {
                primaryMemory = await File.ReadAllTextAsync(_primaryMemoryPath, cancellationToken);
            }
            catch { /* ignore read errors */ }
        }

        if (_currentMemories.Count == 0 && string.IsNullOrWhiteSpace(primaryMemory))
        {
            AppLogger.Log("[AutoRecall] 无记忆需要注入");
            return new AIContext();
        }

        AppLogger.Log($"[AutoRecall] 最终注入 {_currentMemories.Count} 条记忆（保留{keptMemories.Count} + 新增{topNew.Count}）：");
        foreach (var m in _currentMemories)
            AppLogger.Log($"  - [{m.Category}] {m.Content}");

        // 合并主要记忆和工作记忆
        var instructions = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(primaryMemory))
        {
            instructions.AppendLine("[主要记忆] 以下是持久化的长期重要信息：");
            instructions.AppendLine(primaryMemory);
            instructions.AppendLine();
        }
        if (_currentMemories.Count > 0)
        {
            instructions.Append(FormatMemoryInstructions(_currentMemories));
        }

        return new AIContext { Instructions = instructions.ToString() };
    }

    protected override ValueTask InvokedCoreAsync(
        AIContextProvider.InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null)
            return default;

        // 记录对话历史供下次回忆使用
        foreach (var msg in context.RequestMessages)
        {
            if (msg.Role == ChatRole.User && !string.IsNullOrEmpty(msg.Text))
                _conversationHistory.Add(new ChatMessage(ChatRole.User, msg.Text));
        }

        if (context.ResponseMessages is not null)
        {
            foreach (var msg in context.ResponseMessages)
            {
                if (msg.Role == ChatRole.Assistant && !string.IsNullOrEmpty(msg.Text))
                    _conversationHistory.Add(new ChatMessage(ChatRole.Assistant, msg.Text));
            }
        }

        return default;
    }

    private static string FormatMemoryInstructions(IReadOnlyList<MemoryEntry> memories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[长期记忆] 以下是从记忆库中自动检索到的相关信息，请在回复时自然地参考：");
        sb.AppendLine();
        foreach (var m in memories)
        {
            var age = FormatAge(m.CreatedAt);
            sb.AppendLine($"- [{m.Category}](重要度:{m.Importance}, {age}) {m.Content}");
        }
        return sb.ToString();
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
