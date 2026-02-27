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
    private readonly string _workingMemoryPath;
    private readonly string _recentMemoryPath;
    private readonly string _primaryMemoryPath;
    private readonly string _historyDir;
    private readonly AIFunction[] _tools;
    private readonly string _summarizerPrompt;
    private readonly string _consolidatorPrompt;

    public ConversationArchiver(
        IChatClient client,
        string workingMemoryPath,
        string recentMemoryPath,
        string primaryMemoryPath,
        AIFunction[] tools)
    {
        _client = new ChatClientBuilder(client)
            .UseFunctionInvocation()
            .Build();
        _workingMemoryPath = workingMemoryPath;
        _recentMemoryPath = recentMemoryPath;
        _primaryMemoryPath = primaryMemoryPath;
        _tools = tools;
        _historyDir = Path.Combine(
            Path.GetDirectoryName(SharpclawConfig.ConfigPath)!, "history");

        _summarizerPrompt = $"""
            你是一个对话摘要助手。你的任务是为被裁剪的对话生成尽可能详细的摘要，并保存到近期记忆文件中。

            ## 可用的记忆文件

            | 记忆类型 | 文件路径 | 权限 |
            |---------|---------|------|
            | 工作记忆（被裁剪的对话） | {_workingMemoryPath} | 只读 |
            | 近期记忆（对话摘要） | {_recentMemoryPath} | 读写 |
            | 核心记忆（长期重要信息） | {_primaryMemoryPath} | 只读 |

            向量记忆可通过 SearchMemory / GetRecentMemories 查询（只读）。

            **重要：你只能写入近期记忆文件，其他记忆文件仅供参考，禁止修改。**

            ## 流程

            1. 读取工作记忆文件，获取被裁剪的对话内容
            2. 可选：读取其他记忆文件或搜索向量记忆作为参考，了解已有上下文，避免重复
            3. 生成详细摘要
            4. 将摘要追加到近期记忆文件末尾，内容格式必须为：
               ### yyyy-MM-dd HH:mm（当前时间）
               摘要正文

               （末尾空一行）

            ## 摘要要求

            - 尽量保留对话中的所有有意义的信息，宁多勿少
            - 保留具体的操作细节：分析了哪个文件、发现了什么问题、做了什么修改、修改的具体内容
            - 保留完整的上下文链：用户提出了什么需求 → 讨论了哪些方案 → 最终选择了什么 → 执行了什么操作 → 结果如何
            - 保留目标和进展：当前在做什么、完成了哪些步骤、还有哪些待完成、遇到了什么阻碍
            - 保留用户表达的偏好、需求、反馈（包括否定的反馈，如"不要这样做"）
            - 保留关键的代码片段、文件路径、命令、配置项、错误信息
            - 保留数据、数值、结论、决策及其理由
            - 按对话的时间顺序组织，保持事件的因果关系
            - 忽略纯寒暄、重复确认等零信息量内容
            - 工具调用保留调用目的和关键结果，省略冗长的原始输出

            ## 注意

            - 摘要必须写入近期记忆文件，不要只输出文本
            - 如果对话内容有任何有意义的信息，无论其他记忆文件中是否已有相同或类似内容，都必须写入近期记忆
            - 只有对话内容完全没有任何有意义信息时，才可以不保存
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

        // 并行：保存历史文件 + 摘要 Agent 读取工作记忆并生成摘要
        var saveTask = SaveHistoryFileAsync(trimmedMessages, cancellationToken);
        var summaryTask = SummarizeAsync(cancellationToken);

        await Task.WhenAll(saveTask, summaryTask);

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

    #region 历史文件保存

    private async Task SaveHistoryFileAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_historyDir);

            var fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.md";
            var filePath = Path.Combine(_historyDir, fileName);

            var sb = new StringBuilder();
            sb.Append($"# 对话历史 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n");

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
                            sb.Append($"<details>\n<summary>执行结果</summary>\n\n```\n{resultText}\n```\n\n</details>\n\n");
                            break;
                    }
                }
            }

            await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
            AppLogger.Log($"[Archive] 已保存历史文件: {fileName}（{messages.Count} 条消息）");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Archive] 历史文件保存失败: {ex.Message}");
        }
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
