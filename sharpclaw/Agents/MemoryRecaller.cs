using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

using sharpclaw.Chat;
using sharpclaw.Memory;

namespace sharpclaw.Agents;

/// <summary>
/// 记忆回忆器：使用回忆智能体通过工具调用来管理记忆注入。
/// 支持增量更新：智能体可以选择保留/移除已注入的记忆，并搜索新记忆。
/// </summary>
public class MemoryRecaller
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

    private List<MemoryEntry> _currentMemories = [];

    public int MaxMemories { get; set; } = 10;

    public MemoryRecaller(IChatClient baseClient, IMemoryStore memoryStore)
    {
        _client = new ChatClientBuilder(baseClient)
            .UseFunctionInvocation()
            .Build();
        _memoryStore = memoryStore;
    }

    public async Task<ChatMessage?> RecallAsync(
        string userInput,
        IReadOnlyList<string>? conversationLog = null,
        CancellationToken cancellationToken = default)
    {
        var memoryCount = await _memoryStore.CountAsync(cancellationToken);
        if (memoryCount == 0 && _currentMemories.Count == 0)
        {
            Console.WriteLine("[AutoRecall] 记忆库为空，跳过");
            return null;
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
            keepIndices = indices.Select(i => i - 1).ToList(); // 1-indexed → 0-indexed
            var kept = indices.Length == 0 ? "无" : string.Join(",", indices);
            Console.WriteLine($"[AutoRecall] 保留记忆: {kept}");
            return $"已记录，保留 {indices.Length} 条记忆";
        }

        [Description("搜索记忆库，查找与查询相关的记忆用于注入。可多次调用以搜索不同主题。")]
        async Task<string> SearchMemory(
            [Description("搜索关键词或短语")] string query)
        {
            Console.WriteLine($"[AutoRecall] 搜索: {query}");
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

        // ── 构建输入 ──
        var sb2 = new StringBuilder();
        sb2.AppendLine("## 当前对话内容");
        if (conversationLog is { Count: > 0 })
        {
            foreach (var line in conversationLog)
                sb2.AppendLine(line);
        }
        sb2.AppendLine($"用户: {userInput}");
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

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, RecallAgentPrompt),
            new(ChatRole.User, sb2.ToString())
        };

        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(KeepMemories),
                AIFunctionFactory.Create(SearchMemory),
            ]
        };

        await _client.GetResponseAsync(messages, options, cancellationToken);

        // ── 处理结果 ──

        // 保留的旧记忆
        List<MemoryEntry> keptMemories;
        if (!keepCalled)
        {
            keptMemories = new List<MemoryEntry>(_currentMemories); // 默认全部保留
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

        Console.WriteLine($"[AutoRecall] 保留 {keptMemories.Count}/{_currentMemories.Count} 条旧记忆");

        // 合并：保留的 + 新搜索的（排除已保留的，按重要度排序，限制总数）
        var keptIds = new HashSet<string>(keptMemories.Select(m => m.Id));
        var remaining = MaxMemories - keptMemories.Count;
        var topNew = searchedMemories
            .Where(m => !keptIds.Contains(m.Id))
            .OrderByDescending(m => m.Importance)
            .Take(Math.Max(0, remaining))
            .ToList();

        _currentMemories = [.. keptMemories, .. topNew];

        if (_currentMemories.Count == 0)
        {
            Console.WriteLine("[AutoRecall] 无记忆需要注入");
            return null;
        }

        Console.WriteLine($"[AutoRecall] 最终注入 {_currentMemories.Count} 条记忆（保留{keptMemories.Count} + 新增{topNew.Count}）：");
        foreach (var m in _currentMemories)
            Console.WriteLine($"  - [{m.Category}] {m.Content}");

        return FormatMemoryMessage(_currentMemories);
    }

    private static ChatMessage FormatMemoryMessage(IReadOnlyList<MemoryEntry> memories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[长期记忆] 以下是从记忆库中自动检索到的相关信息，请在回复时自然地参考：");
        sb.AppendLine();
        foreach (var m in memories)
        {
            var age = FormatAge(m.CreatedAt);
            sb.AppendLine($"- [{m.Category}](重要度:{m.Importance}, {age}) {m.Content}");
        }

        var msg = new ChatMessage(ChatRole.System, sb.ToString());
        (msg.AdditionalProperties ??= [])[SlidingWindowChatReducer.AutoMemoryKey] = "true";
        return msg;
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