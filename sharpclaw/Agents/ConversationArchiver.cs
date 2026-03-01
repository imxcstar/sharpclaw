using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
    private readonly string _sessionDirPath;
    private readonly string _workingMemoryPath;
    private readonly string _recentMemoryPath;
    private readonly string _primaryMemoryPath;
    private readonly string _historyDir;
    private readonly AIFunction[] _tools;
    private readonly string _summarizerPrompt;
    private readonly string _consolidatorPrompt;

    public ConversationArchiver(
        IChatClient client,
        string sessionDirPath,
        string workingMemoryPath,
        string recentMemoryPath,
        string primaryMemoryPath,
        AIFunction[] tools)
    {
        _client = client;
        _sessionDirPath = sessionDirPath;
        _workingMemoryPath = workingMemoryPath;
        _recentMemoryPath = recentMemoryPath;
        _primaryMemoryPath = primaryMemoryPath;
        _tools = tools;
        _historyDir = Path.Combine(_sessionDirPath, "history");

        _summarizerPrompt = @$"你是 Sharpclaw 的**战术执行记录员 (Tactical Progress Tracker)**。
你的任务是提取即将被裁剪的对话（工作记忆）中的核心执行过程，并以高信息密度、结构化的格式追加到“近期记忆（开发日志）”中。
这能确保 Sharpclaw 在漫长或被打断的自主执行过程中，准确记住“最初的目标”、“踩过的坑与决策”以及“紧接着要干什么”。

## 可用的记忆文件

| 记忆类型 | 文件路径 | 权限 |
|---------|---------|------|
| 工作记忆（被裁剪对话） | {_workingMemoryPath} | 只读 |
| 近期记忆（进度摘要） | {_recentMemoryPath} | 读写 |
| 核心记忆（全局状态） | {_primaryMemoryPath} | 只读 |

**🚨 严禁越权：你只能写入近期记忆文件，禁止修改其他文件。**

## 📝 提取与追加规则 (Extraction & Append Protocol)

作为自主型 Agent 的记录员，你必须过滤掉大模型盲目的“试错过程”和“无意义的寒暄”，只提炼**真正的实质性推进和有价值的排错经验**：

1. **🎯 目标 (Goal)**：当前这组操作是为了解决什么原始需求？（如果是承接上文的旧任务，简述即可）。
2. **🛠️ 执行与决策 (Execution & Rationale)**：
    - **关键操作**：精简记录查阅了哪些核心文件、修改了什么关键逻辑、运行了什么命令。（禁止大段粘贴代码，必须使用函数名、文件路径或关键 1~2 行代码指代）。
    - **排错与决策**：重点记录自主排错过程和避坑指南！例如：“运行测试遇到 `ECONNREFUSED`，查明是端口冲突，已将会话服务端口改为 8081”。记录为什么选 A 方案不选 B 方案。
3. **⚠️ 遗留 (Caveats)**：当前产生的技术债、临时硬编码（Hardcode）数据、或尚未处理的边缘情况（Edge Cases）。若无则写“无”。
4. **📌 状态 (Status)**：[已完成 / 进行中 / 被阻塞] - [简短说明当前卡点或实际进度]。
5. **🚀 下一步 (Next Steps)**：紧接着需要执行的 1~3 个具体动作，必须具有极强的可执行性。

## ✍️ 写入格式（严格遵循）

将摘要**追加 (Append)** 到近期记忆文件末尾，必须使用以下 Markdown 结构：

### yyyy-MM-dd HH:mm
- **🎯 目标**：[一句话概括]
- **🛠️ 执行与决策**：
    - [操作]：...
    - [排错/避坑]：...
- **⚠️ 遗留**：[说明临时妥协 / 无]
- **📌 状态**：[已完成 / 进行中 / 被阻塞] - ...
- **🚀 下一步**：
    - [动作1]
    - [动作2]

（末尾空两行\n\n）

## ✂️ 冗余修剪机制 (Crucial: Deduplication)
- **禁止流水账**：写入前必须读取已有的近期记忆。如果当前对话只是上一个步骤的微小推进（比如：刚才写错了一个变量名导致编译失败，当前对话只是改了这个错别字），**合并语义**，不要把这种低级错误当作重大执行决策长篇大论记录。
- **动态接续**：如果发现任务状态没有本质改变，重点只更新“📌 状态”和“🚀 下一步”，保持日志的清爽。";

        _consolidatorPrompt = @$"你是 Sharpclaw 的**全局状态架构师 (Global State Architect)**。
你的任务是从近期记忆（开发日志）中提取具有长期价值的“规则、架构、教训和宏观进度”，并将它们巩固到“核心记忆”文件中。
        
核心记忆是 Sharpclaw 每次行动的最高纲领。它必须保持极度精简、绝对准确，且**没有任何冗余废话**。

## 可用的记忆文件

| 记忆类型 | 文件路径 | 权限 |
|---------|---------|------|
| 工作记忆（当前对话） | {_workingMemoryPath} | 只读 |
| 近期记忆（进度摘要） | {_recentMemoryPath} | 只读 |
| 核心记忆（全局状态） | {_primaryMemoryPath} | 读写 |

**🚨 严禁越权：你只能写入核心记忆文件，其他记忆文件仅供参考，禁止修改。**

## 🔄 核心巩固法则 (Consolidation Protocol)

1. **全量覆写，拒绝流水账**：核心记忆不是日志！不要记录“今天做了什么”，只记录“现在的系统是什么样”、“规矩是什么”。在写入时，你必须将新提取的信息与旧的核心记忆**融合成一份全新的文档**，并全量覆盖写入。
2. **无情清理过期状态**：
    - 如果近期记忆显示某项技术被替换（如 Vue 换成了 React），**必须在核心记忆中抹除旧技术栈**，绝对不能让冲突信息同时存在。
    - 已经完成的“下一步计划”，将其从核心记忆的“待办”中剔除；如果产生了有价值的结论，将其转移到“决策与教训”中。
3. **提炼血泪教训 (Lessons Learned)**：如果近期记忆中记录了踩过的坑（如“某中间件会导致跨域失败”），必须将其高度浓缩后升格为核心记忆，防止 AI 未来重蹈覆辙。
4. **剥离执行细节**：扔掉具体的行号、临时 Bug 修复过程和普通的文件名。只保留全局性的架构文件路径（如“全局路由在 `src/router.ts`”）或硬性约束。

## ✍️ 核心记忆标准格式（严格遵循）

将融合后的完整状态写入核心记忆文件，**必须**使用以下 Markdown 分区结构（使用 Emoji 作为视觉锚点）：

```markdown
## 🎯 全局目标 (Global Goal)
- [当前项目的终极目标或核心产品定位]

## 👤 偏好与禁忌 (Preferences & Taboos)
- [代码风格：如“强制使用 TypeScript，禁用 any”]
- [行为禁忌：如“绝对不要在 Controller 层写业务逻辑”]

## 🏗️ 架构与基础设施 (Architecture & Infra)
- [技术栈：如 Next.js + Tailwind + PostgreSQL]
- [部署/环境设定：如“运行在 Docker 中，本地端口 8080”]
- [核心目录/文件指引：如“数据库迁移文件统一放在 `/prisma`”]

## 📜 关键决策与教训 (Decisions & Lessons)
- [决策]：放弃 Redis 缓存，改用数据库复合索引以节省内存 (2026-03-01)。
- [教训]：Stripe Webhook 签名验证必须使用 raw body，已在路由层单独拦截处理，未来对接其他 Webhook 需注意此坑。

## 📋 宏观待办 (Macro Todos)
- [仅保留大颗粒度的里程碑或核心未完成模块，不要写“修复第50行的报错”]
- ...
```

## 🚀 执行流程

1. 读取核心记忆文件（若不存在则视为空白）。
2. 仔细阅读需要巩固的近期记忆摘要。
3. 识别出状态变更（哪些任务完成了？哪些技术栈变了？新增了什么规矩？）。
4. 在脑海中对原有核心记忆进行“增、删、改”合并。
5. 将最终的完整 Markdown 输出并覆盖写入核心记忆文件。";
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

            var options = new ChatOptions
            {
                Instructions = _summarizerPrompt,
                Tools = _tools
            };

            var agent = _client.AsBuilder().UseFunctionInvocation().BuildAIAgent(new ChatClientAgentOptions
            {
                ChatOptions = options
            });

            await RunAgentStreamingAsync(agent,
                new ChatMessage(ChatRole.User, "请读取工作记忆文件，为被裁剪的对话生成摘要并保存到近期记忆。"),
                "Summarizer", cancellationToken);
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

        // 按 ### 分段，取前半部分巩固，保留后半部分
        var sections = SplitSections(recentMemory);
        if (sections.Count <= 1)
            return;

        var splitIndex = sections.Count / 2;
        var toConsolidate = string.Join("", sections.Take(splitIndex));
        var toRetain = string.Join("", sections.Skip(splitIndex));

        var sb = new StringBuilder();
        sb.AppendLine("## 需要巩固的近期记忆摘要");
        sb.AppendLine();
        sb.AppendLine(toConsolidate);

        var options = new ChatOptions
        {
            Instructions = _consolidatorPrompt,
            Tools = _tools
        };

        var agent = _client.AsBuilder().UseFunctionInvocation().BuildAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = options
        });

        await RunAgentStreamingAsync(agent,
            new ChatMessage(ChatRole.User, sb.ToString()),
            "Consolidator", cancellationToken);

        // 无论巩固是否成功，都裁剪近期记忆（移除已巩固的部分）
        await File.WriteAllTextAsync(_recentMemoryPath, toRetain, cancellationToken);

        AppLogger.Log($"[Archive] 近期记忆裁剪完成（保留{toRetain.Length}字）");
    }

    /// <summary>按 ### 标题分段。</summary>
    private static List<string> SplitSections(string text)
    {
        var sections = new List<string>();
        var lines = text.Split('\n');
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("### ") && current.Length > 0)
            {
                sections.Add(current.ToString());
                current.Clear();
            }
            current.AppendLine(line);
        }

        if (current.Length > 0)
            sections.Add(current.ToString());

        return sections;
    }

    #endregion

    #region 工具方法

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
