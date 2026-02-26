using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using sharpclaw.Core;
using sharpclaw.UI;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace sharpclaw.Agents;

/// <summary>
/// 对话归档器：三层记忆管线。
/// 窗口裁剪时：AI 生成详细摘要 → 追加到近期记忆 → 溢出时巩固到核心记忆。
/// 同时将被裁剪的完整对话保存为 Markdown 历史文件。
/// </summary>
public class ConversationArchiver
{
    /// <summary>向后兼容：用于剥离旧会话中残留的摘要注入消息。</summary>
    internal const string AutoSummaryKey = "__auto_summary__";

    private const int RecentMemoryMaxLength = 5000;

    private static readonly string SummarizerPrompt = """
        你是一个对话摘要助手。你的任务是为被裁剪的对话生成尽可能详细的摘要，最大限度保留信息。

        你会收到被裁剪掉的对话内容（即将从窗口中移除的旧消息）。

        摘要要求：
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

        直接输出摘要文本，不要加任何前缀或说明。
        """;

    private static readonly string ConsolidatorPrompt = """
        你是一个记忆巩固助手。你的任务是从近期记忆摘要中提炼核心信息，巩固到核心记忆中。

        你会收到：
        1. 需要巩固的近期记忆摘要（较旧的部分，即将从近期记忆中移除）
        2. 当前核心记忆的内容（已持久化的长期信息）

        请提炼摘要中的核心信息，调用 UpdateCoreMemory 工具更新核心记忆。

        巩固规则：
        - 只保留真正重要的、长期有价值的信息，大幅压缩细节
        - 重点提炼：用户身份信息、长期偏好、项目架构、关键技术决策、重要结论
        - 目标和任务：只保留仍在进行中的目标和待办，已完成的任务压缩为一句话结论或直接移除
        - 操作细节：不保留具体的文件修改步骤，只保留最终结论（如"已完成 X 功能"）
        - 临时性信息（调试过程、中间尝试、已解决的错误）不需要巩固
        - 将新信息与已有核心记忆合并，去重去旧，保持精炼
        - 如果已有信息被新信息更新或推翻，用新信息替换旧信息
        - 使用 Markdown 格式，按以下分区结构组织

        核心记忆格式示例：
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

        如果摘要中没有值得巩固的核心信息，则不需要调用工具，直接回复"无需更新"。
        """;

    private readonly IChatClient _client;
    private readonly string _recentMemoryPath;
    private readonly string _primaryMemoryPath;
    private readonly string _historyDir;

    public ConversationArchiver(IChatClient client, string recentMemoryPath, string primaryMemoryPath)
    {
        _client = new ChatClientBuilder(client)
            .UseFunctionInvocation()
            .Build();
        _recentMemoryPath = recentMemoryPath;
        _primaryMemoryPath = primaryMemoryPath;
        _historyDir = Path.Combine(
            Path.GetDirectoryName(SharpclawConfig.ConfigPath)!, "history");
    }

    /// <summary>
    /// 归档被裁剪的消息：生成详细摘要 → 追加到近期记忆 → 溢出时巩固到核心记忆。
    /// </summary>
    /// <returns>归档结果，包含近期记忆和核心记忆的当前内容</returns>
    public async Task<ArchiveResult> ArchiveAsync(
        IReadOnlyList<ChatMessage> trimmedMessages,
        IReadOnlyList<ChatMessage> retainedMessages,
        CancellationToken cancellationToken = default)
    {
        if (trimmedMessages.Count == 0)
            return new ArchiveResult(ReadFile(_recentMemoryPath), ReadFile(_primaryMemoryPath));

        // 并行：保存历史文件 + AI 生成详细摘要
        var saveTask = SaveHistoryFileAsync(trimmedMessages, cancellationToken);
        var summaryTask = SummarizeAsync(trimmedMessages, cancellationToken);

        await Task.WhenAll(saveTask, summaryTask);

        var summary = summaryTask.Result;

        // 追加摘要到近期记忆
        if (!string.IsNullOrWhiteSpace(summary))
        {
            AppendRecentMemory(summary);
            AppLogger.Log($"[Archive] 已追加近期记忆（{summary.Length}字）");
        }

        // 检查近期记忆是否溢出，溢出则巩固到核心记忆
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

    #region 第一层：AI 生成详细摘要

    private async Task<string?> SummarizeAsync(
        IReadOnlyList<ChatMessage> trimmedMessages,
        CancellationToken cancellationToken)
    {
        try
        {
            AppLogger.SetStatus("生成对话摘要...");

            var trimmedText = FormatMessages(trimmedMessages);
            if (trimmedText.Length == 0)
                return null;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, trimmedText.ToString())
            };

            var options = new ChatOptions { Instructions = SummarizerPrompt };
            var response = await _client.GetResponseAsync(messages, options, cancellationToken);

            var summary = response.Text?.Trim();
            AppLogger.Log($"[Archive] 摘要生成完成（{summary?.Length ?? 0}字）");
            return string.IsNullOrWhiteSpace(summary) ? null : summary;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Archive] 摘要生成失败: {ex.Message}");
            return null;
        }
    }

    private void AppendRecentMemory(string summary)
    {
        var dir = Path.GetDirectoryName(_recentMemoryPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var entry = $"### {DateTime.Now:yyyy-MM-dd HH:mm}\n{summary}\n\n";
        File.AppendAllText(_recentMemoryPath, entry);
    }

    #endregion

    #region 第二层：近期记忆溢出 → 巩固到核心记忆

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

        var primaryMemory = ReadFile(_primaryMemoryPath) ?? "";

        var sb = new StringBuilder();
        sb.AppendLine("## 需要巩固的近期记忆摘要");
        sb.AppendLine(toConsolidate);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(primaryMemory))
        {
            sb.AppendLine("## 当前核心记忆内容");
            sb.AppendLine(primaryMemory);
        }
        else
        {
            sb.AppendLine("## 当前无核心记忆");
        }

        string? updatedPrimary = null;

        [Description("更新核心记忆。将巩固后的信息写入持久化存储。传入完整的新内容（应包含已有内容的合并）。")]
        async Task<string> UpdateCoreMemory(
            [Description("核心记忆的完整新内容（Markdown 格式）")] string content)
        {
            try
            {
                var dir = Path.GetDirectoryName(_primaryMemoryPath);
                if (dir is not null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(_primaryMemoryPath, content, cancellationToken);
                updatedPrimary = content;
                AppLogger.Log($"[Archive] 已更新核心记忆（{content.Length}字）");
                return $"已更新核心记忆（{content.Length}字）";
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Archive] 核心记忆写入失败: {ex.Message}");
                return $"写入失败: {ex.Message}";
            }
        }

        var options = new ChatOptions
        {
            Instructions = ConsolidatorPrompt,
            Tools = [AIFunctionFactory.Create(UpdateCoreMemory)]
        };

        var agent = _client.AsBuilder().UseFunctionInvocation().BuildAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = options
        });

        var messages = new List<ChatMessage> { new(ChatRole.User, sb.ToString()) };
        await agent.RunAsync(messages, cancellationToken: cancellationToken);

        // 无论巩固是否成功，都裁剪近期记忆（移除已巩固的部分）
        await File.WriteAllTextAsync(_recentMemoryPath, toRetain, cancellationToken);

        var consolidated = updatedPrimary is not null ? "已巩固" : "无需巩固";
        AppLogger.Log($"[Archive] 近期记忆裁剪完成（{consolidated}，保留{toRetain.Length}字）");
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
            sb.AppendLine($"# 对话历史 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            foreach (var msg in messages)
            {
                var role = msg.Role == ChatRole.User ? "用户"
                         : msg.Role == ChatRole.Assistant ? "助手"
                         : "工具";
                sb.AppendLine($"### {role}");

                foreach (var content in msg.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                            sb.AppendLine(text.Text.Trim());
                            break;
                        case FunctionCallContent call:
                            var args = call.Arguments is not null
                                ? JsonSerializer.Serialize(call.Arguments)
                                : "";
                            sb.AppendLine($"**调用工具** `{call.Name}`");
                            if (!string.IsNullOrEmpty(args))
                                sb.AppendLine($"```json\n{args}\n```");
                            break;
                        case FunctionResultContent result:
                            var resultText = result.Result?.ToString() ?? "";
                            sb.AppendLine($"**工具结果**");
                            sb.AppendLine($"```\n{resultText}\n```");
                            break;
                    }
                }
                sb.AppendLine();
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

    public static StringBuilder FormatMessages(IReadOnlyList<ChatMessage> messages, int? maxResultLength = null)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var parts = new List<string>();
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        parts.Add(text.Text.Trim());
                        break;
                    case FunctionCallContent call:
                        var args = call.Arguments is not null
                            ? JsonSerializer.Serialize(call.Arguments)
                            : "";
                        parts.Add($"[调用工具 {call.Name}({args})]");
                        break;
                    case FunctionResultContent result:
                        var resultText = result.Result?.ToString() ?? "";
                        if (maxResultLength.HasValue)
                        {
                            if (resultText.Length > maxResultLength)
                                resultText = resultText[..maxResultLength.Value] + "...";
                        }
                        parts.Add($"[工具结果: {resultText}]");
                        break;
                }
            }

            if (parts.Count == 0) continue;

            var role = msg.Role == ChatRole.User ? "用户"
                     : msg.Role == ChatRole.Assistant ? "助手"
                     : "工具";
            sb.AppendLine($"{role}: {string.Join(" ", parts)}");
        }
        return sb;
    }

    #endregion
}

/// <summary>归档结果：近期记忆和核心记忆的当前内容。</summary>
public record ArchiveResult(string? RecentMemory, string? PrimaryMemory);
