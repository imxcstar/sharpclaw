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
/// 自主记忆助手：每轮对话后，通过工具自主查询已有记忆，决定保存/更新/删除。
/// 可访问所有记忆文件（只读参考），仅对向量记忆库有读写权限。
/// </summary>
public class MemorySaver
{
    internal const string AutoSaverKey = "__auto_saver__";

    private readonly IChatClient _client;
    private readonly IMemoryStore _memoryStore;
    private readonly AITool[] _fileTools;
    private readonly string _agentPrompt;

    private readonly string _workingMemoryPath;
    private readonly string _recentMemoryPath;
    private readonly string _primaryMemoryPath;

    public MemorySaver(
        IChatClient baseClient,
        IMemoryStore memoryStore,
        string workingMemoryPath,
        string recentMemoryPath,
        string primaryMemoryPath,
        AITool[] fileTools)
    {
        _client = baseClient;
        _memoryStore = memoryStore;
        _fileTools = fileTools;
        _workingMemoryPath = workingMemoryPath;
        _recentMemoryPath = recentMemoryPath;
        _primaryMemoryPath = primaryMemoryPath;

        _agentPrompt = @"你是系统的**长期记忆淬炼助手 (Long-term Memory Consolidation Assistant)**。
**触发背景**：当前的对话上下文即将达到 Token 上限，早期的原始对话日志即将被永久裁剪。
**你的核心使命**：作为记忆流失前的最后一道防线，提取对话中的高价值长效信息，并持久化到向量记忆库中，确保系统在未来的漫长交互中不会“失忆”、不会前后矛盾、不重复踩坑。

## 🧠 淬炼与提取准则 (What to Save)

不要把垃圾塞进向量记忆库！忽略无营养的寒暄、临时的语法纠错、或单次生效的普通问答。你**只关注**以下五类具有“长期复用价值”的目标：

1. **偏好与约束 (Preferences & Constraints)**：用户明确提出的长期规则。例如：“回复强制使用 Markdown 表格”、“不要推荐任何海鲜类食物”、“预算严格控制在 1 万以内”。
2. **核心事实与设定 (Core Context & Facts)**：跨对话有效的客观背景。例如：“主角的性格是社恐且多疑”、“当前项目的核心受众是中老年人”、“前端技术栈使用的是 React”。
3. **经验与避坑指南 (Insights & Lessons)**：经过多次试错才得出的宝贵经验。例如：“用户极度反感被连续反问，必须直接给出方案”、“办理申根签证至少需要提前 3 个月，规划行程需预留此时间”。（**极其重要，这是 AI 进化的关键**）
4. **关键决策 (Key Decisions)**：经过讨论后确立的重大方向变更。例如：“最终决定放弃线下推广，全面转向小红书运营”。
5. **长期待办 (Long-term Todos)**：因上下文截断被迫中断、或约定在未来处理的事项。例如：“第一阶段大纲已定，下一步需要构思反派的背景故事”。

## 🔄 记忆更新法则 (How to Update - 极其重要)

**向量库极易发生“知识污染”。在保存任何新信息前，你必须严格执行查重与冲突覆盖！**

1. **先搜后写**：在提取出某个知识点后，必须先调用 `Search` 搜索相关关键词（可多次尝试不同关键词）。
2. **状态覆写（解决冲突）**：如果发现用户改变了主意（旧记忆：“旅行目的地是巴黎” -> 新对话：“我们改去伦敦了”），**必须调用 `Update` 或 `Remove` 抹除旧记忆**，再保存新记忆。绝对不能让两条互相冲突的设定同时存在！
3. **合并同类项**：如果库里已有“世界观设定”的记忆，今天又新增了“魔法体系的具体规则”，请将它们合并后用 `Update` 更新为一条更完整的记忆。
4. **清理过期待办**：如果当前对话显示某个之前存入的待办（如“待确认预算”）已经完成，请主动将其从向量库中 `Remove`。

## ✍️ 格式要求（严格遵循）

- 保存的记忆文本必须**高度浓缩、独立且自包含**（脱离当前上下文也能看懂）。
- ❌ **错误示范**：“用户说他不喜欢这个方案。”（缺乏主语和前因后果，未来检索出来完全是一头雾水）。
- ✅ **正确示范**：“【偏好】用户不希望在旅行规划中安排过于紧凑的行程，偏好每天只深度游览一个核心景点。”
- ✅ **正确示范**：“【决策】项目营销方案已放弃传统的线下发传单模式，全面改为线上短视频引流。”
";
    }

    public async Task SaveAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (history.Count == 0)
            return;

        AppLogger.SetStatus("记忆保存中...");
        var fullText = FormatMessages(history).ToString();

        // ── 向量记忆工具 ──

        [Description("搜索向量记忆库，查找与查询相关的已有记忆。保存或更新前应先搜索，避免重复。")]
        async Task<string> SearchMemory(
            [Description("搜索关键词或语义查询")] string query,
            [Description("最多返回几条结果")] int count = 5)
        {
            var results = await _memoryStore.SearchAsync(query, Math.Min(count, 10), cancellationToken);
            if (results.Count == 0)
                return "未找到相关记忆。";

            var sb = new StringBuilder();
            sb.AppendLine($"找到 {results.Count} 条相关记忆：");
            foreach (var m in results)
                sb.AppendLine($"- ID={m.Id} [{m.Category}](重要度:{m.Importance}) {m.Content}");
            return sb.ToString();
        }

        [Description("查看最近保存的向量记忆，了解记忆库近况。")]
        async Task<string> GetRecentMemories(
            [Description("返回最近几条记忆")] int count = 5)
        {
            var results = await _memoryStore.GetRecentAsync(Math.Min(count, 10), cancellationToken);
            if (results.Count == 0)
                return "记忆库为空。";

            var sb = new StringBuilder();
            sb.AppendLine($"最近 {results.Count} 条记忆：");
            foreach (var m in results)
                sb.AppendLine($"- ID={m.Id} [{m.Category}](重要度:{m.Importance}) {m.Content}");
            return sb.ToString();
        }

        [Description("保存一条新的记忆到向量记忆库。")]
        async Task<string> SaveMemory(
            [Description("记忆内容，应独立自包含")] string content,
            [Description("类别：fact/preference/decision/todo/lesson")] string category,
            [Description("重要度 1-10")] int importance,
            [Description("关键词列表")] string[] keywords)
        {
            var entry = new MemoryEntry
            {
                Content = content,
                Category = category,
                Importance = Math.Clamp(importance, 1, 10),
                Keywords = keywords.ToList()
            };
            await _memoryStore.AddAsync(entry, cancellationToken);
            return $"已保存: {content}";
        }

        [Description("更新向量记忆库中已有的一条记忆。")]
        async Task<string> UpdateMemory(
            [Description("要更新的记忆 ID")] string id,
            [Description("新的记忆内容")] string content,
            [Description("类别：fact/preference/decision/todo/lesson")] string category,
            [Description("重要度 1-10")] int importance,
            [Description("关键词列表")] string[] keywords)
        {
            var entry = new MemoryEntry
            {
                Id = id,
                Content = content,
                Category = category,
                Importance = Math.Clamp(importance, 1, 10),
                Keywords = keywords.ToList()
            };
            await _memoryStore.UpdateAsync(entry, cancellationToken);
            return $"已更新: {content}";
        }

        [Description("从向量记忆库中删除一条过时的记忆。")]
        async Task<string> RemoveMemory(
            [Description("要删除的记忆 ID")] string id)
        {
            await _memoryStore.RemoveAsync(id, cancellationToken);
            return $"已删除: {id}";
        }

        // ── 构建输入 ──

        var messages = new List<ChatMessage>();

        var workingMemoryContent = ReadWorkingMemory() ?? "";
        if (!string.IsNullOrWhiteSpace(workingMemoryContent))
        {
            var workingMemoryMessages = JsonSerializer.Deserialize<List<ChatMessage>>(workingMemoryContent);
            if (workingMemoryMessages != null && workingMemoryMessages.Count > 0)
                messages.AddRange(workingMemoryMessages);
        }

        if (messages.Count == 0)
            return;

        var primaryMemoryContent = ReadPrimaryMemory() ?? "";
        if (!string.IsNullOrWhiteSpace(primaryMemoryContent))
        {
            messages.Add(new ChatMessage(ChatRole.User, "查询核心记忆"));
            MemoryPipelineChatReducer.InjectFakeReadFile(messages, _primaryMemoryPath, primaryMemoryContent, AutoSaverKey);
        }

        var recentMemoryContent = ReadRecentMemory() ?? "";
        if (!string.IsNullOrWhiteSpace(recentMemoryContent))
        {
            messages.Add(new ChatMessage(ChatRole.User, "查询记忆快照"));
            MemoryPipelineChatReducer.InjectFakeReadFile(messages, _recentMemoryPath, recentMemoryContent, AutoSaverKey);
        }

        AIFunction[] vectorTools =
        [
            AIFunctionFactory.Create(SearchMemory),
            AIFunctionFactory.Create(GetRecentMemories),
            AIFunctionFactory.Create(SaveMemory),
            AIFunctionFactory.Create(UpdateMemory),
            AIFunctionFactory.Create(RemoveMemory),
        ];

        var options = new ChatOptions
        {
            Instructions = _agentPrompt,
            Tools = [.. _fileTools, .. vectorTools]
        };

        var agent = _client.AsBuilder().UseFunctionInvocation().BuildAIAgent(new ChatClientAgentOptions()
        {
            ChatOptions = options
        });

        await RunAgentStreamingAsync(agent,
            [.. messages, new ChatMessage(ChatRole.User, "根据以上对话历史，写入或整理向量记忆。")],
            "MemorySaver", cancellationToken);
    }
    private static string? ReadFile(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            var content = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch { return null; }
    }

    /// <summary>读取工作记忆。</summary>
    private string? ReadWorkingMemory() => ReadFile(_workingMemoryPath);

    /// <summary>读取近期记忆。</summary>
    private string? ReadRecentMemory() => ReadFile(_recentMemoryPath);

    /// <summary>读取核心记忆。</summary>
    private string? ReadPrimaryMemory() => ReadFile(_primaryMemoryPath);

    private static StringBuilder FormatMessages(IReadOnlyList<ChatMessage> messages, int? maxResultLength = null)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.User)
            {
                var text = string.Join("", msg.Contents.OfType<TextContent>()
                    .Where(t => !string.IsNullOrWhiteSpace(t.Text))
                    .Select(t => t.Text.Trim()));
                if (!string.IsNullOrWhiteSpace(text))
                    sb.Append($"### 用户\n\n{text}\n\n");
                continue;
            }

            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        sb.Append($"### 助手\n\n{text.Text.Trim()}\n\n");
                        break;
                    case FunctionCallContent call:
                        var args = call.Arguments is not null
                            ? JsonSerializer.Serialize(call.Arguments)
                            : "";
                        sb.Append($"#### 工具调用: {call.Name}\n\n参数: `{args}`\n\n");
                        break;
                    case FunctionResultContent result:
                        var resultText = result.Result?.ToString() ?? "";
                        if (maxResultLength.HasValue && resultText.Length > maxResultLength)
                            resultText = resultText[..maxResultLength.Value] + "...";
                        sb.Append($"<details>\n<summary>执行结果</summary>\n\n```\n{resultText}\n```\n\n</details>\n\n");
                        break;
                }
            }
        }
        return sb;
    }

    private static async Task RunAgentStreamingAsync(
        ChatClientAgent agent, IEnumerable<ChatMessage> input, string logPrefix, CancellationToken cancellationToken)
    {
        var session = await agent.CreateSessionAsync();

        await foreach (var update in agent.RunStreamingAsync(input, session).WithCancellation(cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        AppLogger.SetStatus($"[{logPrefix}]调用工具: {call.Name}");
                        AppLogger.Log($"[{logPrefix}]调用工具: {call.Name}");
                        break;
                }
            }
        }
    }
}
