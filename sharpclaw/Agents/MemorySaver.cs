using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

using sharpclaw.Memory;
using sharpclaw.UI;

namespace sharpclaw.Agents;

/// <summary>
/// 自主记忆助手：每轮对话后，通过工具自主查询已有记忆，决定保存/更新/删除。
/// 可访问所有记忆文件（只读参考），仅对向量记忆库有读写权限。
/// </summary>
public class MemorySaver
{
    private readonly IChatClient _client;
    private readonly IMemoryStore _memoryStore;
    private readonly AIFunction[] _fileTools;
    private readonly string _agentPrompt;

    public MemorySaver(
        IChatClient baseClient,
        IMemoryStore memoryStore,
        string workingMemoryPath,
        string recentMemoryPath,
        string primaryMemoryPath,
        AIFunction[] fileTools)
    {
        _client = baseClient;
        _memoryStore = memoryStore;
        _fileTools = fileTools;

        _agentPrompt = @$"你是 Sharpclaw 的**记忆淬炼专家 (Memory Consolidation Specialist)**。
**触发背景**：当前的对话上下文即将达到 Token 上限，早期的原始对话日志即将被永久裁剪（遗忘）。
**你的核心使命**：在上下文被销毁前，作为最后一道防线，提取对话中的高价值信息并持久化到向量记忆库中，确保 Sharpclaw 在未来的对话中不会“失忆”或重复踩坑。

## 可用的记忆源

| 记忆类型 | 位置 | 权限 |
|---------|------|------|
| 工作记忆（即将被裁剪的对话） | {workingMemoryPath} | 只读 |
| 近期记忆（进度摘要看板） | {recentMemoryPath} | 只读 |
| 核心记忆（全局硬性约束） | {primaryMemoryPath} | 只读 |
| 向量记忆（细粒度长期知识库） | 通过 SearchMemory / Save / Update / Remove 管理 | 读写 |

**🚨 严禁越权：你只能通过工具管理向量记忆库，其他记忆文件仅供参考，禁止修改。**

## 🧠 淬炼与提取准则 (What to Save)

不要把垃圾塞进记忆库！忽略毫无营养的寒暄、拼写错误、以及简单的“写一段基础代码”的临时过程。你**只关注**以下五类高价值目标：

1. **Preference (偏好与禁忌)**：用户明确提出的规则。例如：“以后强制使用 TypeScript”、“绝不要在 Controller 层写业务逻辑”、“缩进必须是 4 个空格”。
2. **Architecture (架构与事实)**：项目的核心设定。例如：“前端技术栈是 Next.js + Tailwind”、“数据库配置了主从分离”、“当前支付网关用的是 Stripe”。
3. **Lesson (血泪教训/避坑指南)**：花费了大量轮次才排查出的 Bug 及其根本原因。例如：“因为中间件拦截了 raw body 导致 Webhook 签名失败，必须单独放行”。**（极其重要，这是 AI 进化的关键）**
4. **Decision (关键决策)**：经过讨论后确定的方案。例如：“最终决定放弃 Redis，改用数据库复合索引解决慢查询”。
5. **Todo (遗留状态)**：因为上下文截断而被迫中断的未竟事业。例如：“正在重构 auth 模块，下一步需要测试 JWT 续期逻辑”。

## 🔄 记忆更新法则 (How to Update - 极其重要)

**向量记忆库极易发生“知识污染”。在保存任何新信息前，你必须严格执行查重与冲突覆盖！**

1. **先搜后写**：提取出知识点后，先用 `SearchMemory` 搜索相关关键词（可多次使用不同关键词）。
2. **状态更新**：如果发现用户改变了主意（旧记忆：“前端用 Vue” -> 新对话：“我们要全部迁移到 React”），**必须调用 `UpdateMemory` 或 `RemoveMemory` 抹除旧记忆**，再保存新记忆。绝对不能让两条冲突的设定同时存在！
3. **合并同类项**：如果库里已经有关于“数据库配置”的记忆，而今天新增了“Redis 端口号”，请将它们合并更新为一条完整记忆。
4. **清理过期 Todo**：如果对话显示某个之前存入的 Todo（如“待修复登录 Bug”）已经完成，请主动将其从向量库中 `RemoveMemory`。

## 格式要求
- 保存的记忆文本必须**高度浓缩、独立且自包含**。
- 错误示范：“用户说他不喜欢这个方案”。（缺乏主语和上下文，未来读取时完全看不懂）
- 正确示范：“【偏好】用户不希望在项目中引入任何重量级的 ORM 框架（如 Entity Framework），偏好使用 Dapper 进行轻量级数据库操作。”";
    }

    public async Task SaveAsync(
        IReadOnlyList<ChatMessage> history,
        string userInput,
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

        var memoryCount = await _memoryStore.CountAsync(cancellationToken);

        var sb2 = new StringBuilder();
        sb2.AppendLine($"## 向量记忆库状态：已存 {memoryCount} 条");
        sb2.AppendLine();
        sb2.AppendLine("## 用户本轮输入");
        sb2.AppendLine();
        sb2.AppendLine(userInput);
        sb2.AppendLine();
        sb2.AppendLine("## 最近对话内容");
        sb2.AppendLine();
        sb2.Append(fullText);

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
            new ChatMessage(ChatRole.User, sb2.ToString()),
            "MemorySaver", cancellationToken);
    }

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
        ChatClientAgent agent, ChatMessage input, string logPrefix, CancellationToken cancellationToken)
    {
        var session = await agent.CreateSessionAsync();

        await foreach (var update in agent.RunStreamingAsync([input], session).WithCancellation(cancellationToken))
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
