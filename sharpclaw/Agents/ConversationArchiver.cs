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

        _summarizerPrompt = @$"""
            你是一个面向任务、进度和决策的高级记忆摘要助手。你的任务是提取被裁剪对话中的核心信息，并以高信息密度的结构化格式保存到“近期记忆”文件中。这能确保 AI 在漫长的开发过程中，准确记住目标、执行细节、决策原因以及下一步动作，同时避免重复踩坑。

            ## 可用的记忆文件

            | 记忆类型 | 文件路径 | 权限 |
            |---------|---------|------|
            | 工作记忆（被裁剪对话） | {_workingMemoryPath} | 只读 |
            | 近期记忆（进度摘要） | {_recentMemoryPath} | 读写 |
            | 核心记忆（长期偏好） | {_primaryMemoryPath} | 只读 |

            **重要：你只能写入近期记忆文件，禁止修改其他文件。**

            ## 提取与写入规则

            每次生成摘要时，你必须严格按照以下维度提炼内容。**注意控制篇幅：禁止大段粘贴代码，必须使用函数名、文件路径或关键的 1~2 行代码指代。**

            1. **用户需求 (Goal)**：当前阶段的核心目标或用户的原始指令。
            2. **执行与决策 (Execution & Rationale)**：
               - **干了什么**：具体修改了哪些文件（带路径）、运行了什么关键命令。
               - **关键细节**：核心报错信息、重要的配置项变更。
               - **决策与避坑**：为什么采用方案 A 而不是方案 B？排除了哪些错误方向？用户的负面反馈（绝对不要做什么）。
            3. **遗留问题 (Caveats)**：当前代码中存在的临时处理（Mock 数据、Hardcode）、已知的副作用或未来需要重构的技术债。如果没有则写“无”。
            4. **任务状态 (Status)**：[已完成 / 进行中 / 被阻塞] - [简短说明当前卡点或进度]。
            5. **下一步计划 (Next Steps)**：紧接着需要执行的 1~3 个具体动作。

            ## 写入格式（严格遵循）

            将摘要追加到近期记忆文件末尾，使用以下结构：

            ### yyyy-MM-dd HH:mm
            - **🎯 需求**：[一句话概括]
            - **🛠️ 执行与决策**：
              - [操作]：...
              - [决策/避坑]：...
            - **⚠️ 遗留**：[说明临时妥协或技术债 / 无]
            - **📌 状态**：[已完成 / 进行中 / 被阻塞] - ...
            - **🚀 下一步**：
              - [动作1]
              - [动作2]

            （末尾空两行\n\n）

            ## 去重与优化
            - 写入前必须读取近期记忆。若只是状态更新，不要重复记录冗长的历史操作，重点更新“状态”和“下一步”。
            - 提取真正有信息量的决策和代码定位，过滤无用的寒暄和过程性废话。

            ## 示例

            ```markdown
            ### 2026-03-01 15:20
            - **🎯 需求**：优化后端 `/api/products` 接口的响应速度，用户反馈查询太慢（超过 2 秒）。
            - **🛠️ 执行与决策**：
              - [操作]：分析了 `src/services/productService.js`，发现慢查询是因为关联了 5 张表且没有建立索引。
              - [操作]：通过 TypeORM 迁移文件 `src/migration/170928391_AddProductIndex.ts` 为 `category_id` 和 `status` 字段添加了复合索引。
              - [决策/避坑]：原本考虑引入 Redis 做缓存，但**被用户否决**。用户指出当前服务器内存吃紧，且商品数据实时性要求高，决定优先通过数据库索引解决。绝对不要在此接口引入外部缓存组件。
            - **⚠️ 遗留**：无。
            - **📌 状态**：进行中 - 索引迁移文件已生成，但尚未在测试库运行 `migration:run`。
            - **🚀 下一步**：
              - 执行 TypeORM 迁移命令同步数据库结构。
              - 使用压测脚本或 Postman 重新测试接口响应时间，验证索引是否生效。


            ### 2026-03-01 16:05
            - **🎯 需求**：对接第三方支付接口（Stripe），实现基础的 Checkout Session 创建逻辑。
            - **🛠️ 执行与决策**：
              - [操作]：安装 `stripe` SDK，在 `src/controllers/paymentController.js` 中实现了 `createCheckoutSession` 方法。
              - [操作]：配置了成功跳转的 `success_url` 和取消的 `cancel_url`。
              - [决策/避坑]：在测试 Webhook 签名验证时一直报 `400 Invalid Signature`。排查发现是 Express 的全局 `express.json()` 中间件破坏了 Stripe 需要的 raw body。**解决方案**：在路由层将 webhook 接口抽离，单独使用 `express.raw({{type: 'application/json'}})` 进行解析。这个坑以后对接任何 Webhook 都要注意。
            - **⚠️ 遗留**：当前 `.env` 中的 `STRIPE_SECRET_KEY` 使用的是测试环境的 Key（`sk_test_...`），且 Webhook 的本地联调依赖 Stripe CLI 转发。
            - **📌 状态**：已完成 - Checkout 流程及 Webhook 签名验证跑通，订单状态可正常更新为 `PAID`。
            - **🚀 下一步**：
              - 完善 Webhook 中的业务逻辑（如：支付成功后给用户发送通知邮件）。
              - （询问用户）是否需要开始开发前端的支付按钮和跳转页面？
            ```
            """;

        _consolidatorPrompt = $"""
            你是一个记忆巩固助手。你的任务是从近期记忆摘要中提炼核心信息，巩固到核心记忆文件中。

            ## 可用的记忆文件

            | 记忆类型 | 文件路径 | 权限 |
            |---------|---------|------|
            | 工作记忆（当前对话） | {_workingMemoryPath} | 只读 |
            | 近期记忆（对话摘要） | {_recentMemoryPath} | 只读 |
            | 核心记忆（长期重要信息） | {_primaryMemoryPath} | 读写 |

            向量记忆可通过 SearchMemory / GetRecentMemories 查询（只读）。

            **重要：你只能写入核心记忆文件，其他记忆文件仅供参考，禁止修改。**

            ## 流程

            1. 阅读下方提供的需要巩固的近期记忆摘要
            2. 读取核心记忆文件，了解已有的长期信息（文件可能不存在，说明是首次巩固）
            3. 可选：读取工作记忆了解当前对话上下文，或搜索向量记忆补充上下文
            4. 提炼摘要中的核心信息，与已有核心记忆合并
            5. 将更新后的完整核心记忆写入核心记忆文件（覆盖）

            ## 巩固规则

            - 只保留真正重要的、长期有价值的信息，大幅压缩细节
            - 重点提炼：用户身份信息、长期偏好、项目架构、关键技术决策、重要结论
            - 目标和任务：只保留仍在进行中的目标和待办，已完成的任务压缩为一句话结论或直接移除
            - 操作细节：不保留具体的文件修改步骤，只保留最终结论（如"已完成 X 功能"）
            - 临时性信息（调试过程、中间尝试、已解决的错误）不需要巩固
            - 将新信息与已有核心记忆合并，去重去旧，保持精炼
            - 如果已有信息被新信息更新或推翻，用新信息替换旧信息
            - 使用 Markdown 格式，按以下分区结构组织

            ## 核心记忆格式示例

            ```markdown
            ## 当前目标
            - 正在开发电商平台的订单模块，需要支持退款流程

            ## 待办事项
            - 编写退款 API 的单元测试

            ## 用户偏好
            - 偏好使用中文交流
            - 代码风格：简洁，不喜欢过度注释

            ## 关键事实
            - 项目使用 monorepo 结构
            - 部署环境：阿里云 ECS + Docker Compose

            ## 重要决策
            - API 统一使用 RESTful 风格
            - 前端状态管理选择了 Zustand
            ```

            ## 注意

            - 必须先查看已有核心记忆内容再决定如何合并
            - 有值得巩固的信息时，无论其他记忆中是否已有相同或类似内容，都必须写入核心记忆
            - 只有摘要中完全没有值得巩固的核心信息时，才可以不写入
            """;
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
