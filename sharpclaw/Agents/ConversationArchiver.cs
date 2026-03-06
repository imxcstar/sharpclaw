using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using sharpclaw.Chat;
using sharpclaw.Core;
using sharpclaw.UI;
using System.Text;
using System.Text.Json;

namespace sharpclaw.Agents;

/// <summary>
/// 对话归档器：三层记忆管线。
/// 窗口裁剪时：摘要 Agent 生成详细摘要并保存到近期记忆 → 溢出时巩固 Agent 提炼核心信息到核心记忆。
/// 同时将被裁剪的完整对话保存为 Markdown 历史文件。
/// 子 Agent 使用标准文件命令工具操作记忆文件，通过 prompt 限制各自的写入范围。
/// </summary>
public class ConversationArchiver
{
    /// <summary>向后兼容：用于剥离旧会话中残留的摘要注入消息。</summary>
    internal const string AutoSummaryKey = "__auto_summary__";

    private const int RecentMemoryMaxLength = 30000;

    private readonly IChatClient _client;
    private readonly string _workingMemoryPath;
    private readonly string _recentMemoryPath;
    private readonly string _primaryMemoryPath;
    private readonly string _summarizerPrompt;
    private readonly string _consolidatorPrompt;

    public ConversationArchiver(
        IChatClient client,
        string sessionDirPath,
        string workingMemoryPath,
        string recentMemoryPath,
        string primaryMemoryPath)
    {
        _client = client;
        _workingMemoryPath = workingMemoryPath;
        _recentMemoryPath = recentMemoryPath;
        _primaryMemoryPath = primaryMemoryPath;

        _summarizerPrompt = @"你是系统的**对话记忆压缩助手 (Context Compression Assistant)**。
你的任务是对即将超长的历史对话进行结构化总结，提取出最重要的上下文信息，以确保系统在后续交互中能够无缝衔接，保持记忆连贯。

## 📝 总结原则
1. **聚焦核心事实**：过滤掉无意义的寒暄和已放弃的无效方案，重点保留有价值的讨论内容、技术细节（如关键变量、报错信息）或业务逻辑。
2. **结果导向提取**：避免按时间顺序记录对话流水账，应直接归纳出探讨的最终结果和确定的处理方式。
3. **分块记录**：长对话通常包含多个不同的任务或讨论点。请根据对话的实际内容，将不同主题的上下文拆分成独立的事项逐条记录。

## ✍️ 输出格式（严格遵循）
请根据对话涉及的主题数量，输出一条或多条记录：

### 💬 记忆快照
**1. [事项/主题名称]**
- **📌 执行过程**：[客观陈述该事项执行了哪些核心操作、排查了什么问题]
- **✅ 结论与待办**：[总结该事项达成的共识、确认的方案或遗留的下一步]

**2. [事项/主题名称]** (若对话涉及多个独立事项，则继续罗列)
- **📌 执行过程**：...
- **✅ 结论与待办**：...

## 💡 参考示例

### 💬 记忆快照
**1. MongoDB 连接超时问题排查**
- **📌 执行过程**：排查 `UserService` 中 `TimeoutException` 报错。检查了网络配置和环境变量。
- **✅ 结论与待办**：确认原因是 `appsettings.json` 遗漏了 `authSource=admin` 参数。已修复并测试通过。遗留：尚未实现异常重试机制。

**2. 用户登录鉴权 API 设计**
- **📌 执行过程**：讨论了 JWT 的使用方案，并编写了 Token 签发的核心逻辑。
- **✅ 结论与待办**：确认 Access Token 有效期设为 2 小时。下一步需接续开发对应的 Refresh Token 刷新接口。
";

        _consolidatorPrompt = @"你是系统的**核心记忆整合助手 (Core Memory Consolidator)**。
你的任务是将近期产生的“记忆快照”提炼、过滤，并无缝合并到长期的“核心记忆”中。

无论当前进行的是文学创作、旅行规划、学术研究还是项目管理，核心记忆都是系统每次交互的最高全局上下文。它必须保持高度结构化、绝对准确，且**没有任何冗余的微观执行细节**。

## 🔄 核心整合法则 (Consolidation Protocol)

1. **状态更新，而非日志追加**：核心记忆不是历史记录本！不要记录“刚刚做了什么”，而是要总结“当前的全局状态是什么”、“确立了什么新规矩”。在输出时，你必须将新提取的信息与旧的核心记忆**融合成一份全新的状态文档**。
2. **无情清理与覆写过期信息**：
    - 如果新快照显示某项决策被推翻（例如：旅行目的地从巴黎改为伦敦，或文章主角设定从男性改为女性），**必须在输出时彻底抹除旧设定**，绝对不能让冲突信息同时存在。
    - 如果宏观待办中的某项任务已在快照中显示完成，必须将其剔除；若其执行过程产生了通用经验，应将其转移到“经验与避坑”中。
3. **剥离微观细节**：舍弃具体的遣词造句修改、临时的试错过程或单次对话的琐碎细节。只保留全局性的框架设计、硬性约束条件或高价值的结论。

## ✍️ 输出格式（严格遵循）

请综合现有的核心记忆与新的记忆快照，提取并重新输出一份**完整且最新的**核心记忆。必须严格使用以下 Markdown 结构罗列项（若某项为空则写“无”）：

### 🧠 核心记忆 (Core Memory)

**🎯 全局目标与当前阶段 (Goal & Phase)**
- [当前探讨的终极目标、核心项目或所处的宏观进度阶段]

**👤 偏好与禁忌 (Preferences & Taboos)**
- [用户明确要求的偏好：如“回答必须精简”、“规划行程时只考虑高铁”、“文章风格要幽默”]
- [行为禁忌：如“绝对不要输出长篇大论的免责声明”、“不要推荐海鲜类食物”]

**🧩 核心设定与关键事实 (Core Context & Facts)**
- [已确认的核心框架、大纲设计、关键背景信息]
- [重要的前置条件或全局设定（如：小说的人物关系图、项目的预算限制、活动的核心受众）]

**📜 经验与避坑 (Lessons & Insights)**
- [经过试错得出的宝贵经验，防止未来重蹈覆辙，如：“用户不喜欢被反问”、“办理某签证至少需要提前3个月，以后需优先提醒”]

**📋 宏观待办 (Macro Todos)**
- [仅保留大颗粒度的里程碑、核心未完成模块或下一步重大方向]
- [不要写入“修改第三段的错别字”这类微观任务]
";
    }

    /// <summary>
    /// 归档被裁剪的消息：保存工作记忆 → 摘要 Agent 保存到近期记忆 → 溢出时巩固 Agent 提炼到核心记忆。
    /// </summary>
    public async Task<ArchiveResult> ArchiveAsync(
        IReadOnlyList<ChatMessage> trimmedMessages,
        CancellationToken cancellationToken = default)
    {
        if (trimmedMessages.Count == 0)
            return new ArchiveResult(ReadFile(_recentMemoryPath), ReadFile(_primaryMemoryPath));

        // 摘要 Agent 读取工作记忆并生成摘要
        await SummarizeAsync(cancellationToken);

        // 检查近期记忆是否溢出，溢出则巩固 Agent 提炼到核心记忆
        var recentMemory = ReadFile(_recentMemoryPath) ?? "";
        if (recentMemory.Length > RecentMemoryMaxLength)
        {
            try
            {
                await ConsolidateAsync(recentMemory, cancellationToken);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Archive] 记忆巩固失败: {ex.Message}");
            }
        }

        return new ArchiveResult(
            ReadFile(_recentMemoryPath),
            ReadFile(_primaryMemoryPath));
    }

    /// <summary>读取工作记忆。</summary>
    public string? ReadWorkingMemory() => ReadFile(_workingMemoryPath);

    /// <summary>读取近期记忆。</summary>
    public string? ReadRecentMemory() => ReadFile(_recentMemoryPath);

    /// <summary>读取核心记忆。</summary>
    public string? ReadPrimaryMemory() => ReadFile(_primaryMemoryPath);

    #region 第一层：摘要 Agent

    private async Task SummarizeAsync(CancellationToken cancellationToken)
    {
        try
        {
            AppLogger.SetStatus("生成对话摘要...");

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
                MemoryPipelineChatReducer.InjectFakeReadFile(messages, _primaryMemoryPath, primaryMemoryContent, AutoSummaryKey);
            }

            var recentMemoryContent = ReadRecentMemory() ?? "";
            if (!string.IsNullOrWhiteSpace(recentMemoryContent))
            {
                messages.Add(new ChatMessage(ChatRole.User, "查询原来的记忆快照"));
                MemoryPipelineChatReducer.InjectFakeReadFile(messages, _recentMemoryPath, recentMemoryContent, AutoSummaryKey);
            }
            var ret = await _client.AsAIAgent(_summarizerPrompt).RunAsync([.. messages, new ChatMessage(ChatRole.User, "根据以上对话历史，生成完成的记忆快照。")], cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(ret.Text))
            {
                File.WriteAllText(_recentMemoryPath, ret.Text);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Archive] 摘要生成失败: {ex.Message}");
        }
    }

    #endregion

    #region 第二层：巩固 Agent

    private async Task ConsolidateAsync(string recentMemory, CancellationToken cancellationToken)
    {
        AppLogger.SetStatus("巩固核心记忆...");

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
            MemoryPipelineChatReducer.InjectFakeReadFile(messages, _primaryMemoryPath, primaryMemoryContent, AutoSummaryKey);
        }

        var recentMemoryContent = ReadRecentMemory() ?? "";
        if (!string.IsNullOrWhiteSpace(recentMemoryContent))
        {
            messages.Add(new ChatMessage(ChatRole.User, "查询记忆快照"));
            MemoryPipelineChatReducer.InjectFakeReadFile(messages, _recentMemoryPath, recentMemoryContent, AutoSummaryKey);
        }

        var ret = await _client.AsAIAgent(_consolidatorPrompt).RunAsync([.. messages, new ChatMessage(ChatRole.User, "根据以上对话历史，生成完成的核心记忆。")], cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(ret.Text))
        {
            File.WriteAllText(_primaryMemoryPath, ret.Text);
        }
    }

    #endregion

    #region 工具方法

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

    #endregion
}

/// <summary>归档结果：近期记忆和核心记忆的当前内容。</summary>
public record ArchiveResult(string? RecentMemory, string? PrimaryMemory);
