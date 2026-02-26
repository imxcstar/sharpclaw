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
        你是一个对话摘要助手。你的任务是为被裁剪的对话生成详细摘要，保留关键细节。

        你会收到被裁剪掉的对话内容（即将从窗口中移除的旧消息）。

        摘要要求：
        - 保留具体的操作细节（分析了哪个文件、发现了什么问题、做了什么修改）
        - 保留目标进展信息（完成了哪些步骤、还有哪些待完成、当前进度如何）
        - 保留关键的数据、结论和决策
        - 保留用户表达的偏好和需求
        - 按时间顺序组织，使用简洁但信息密度高的语言
        - 忽略寒暄、确认等无信息量的内容
        - 工具调用只保留关键结论，不需要记录调用细节

        直接输出摘要文本，不要加任何前缀或说明。
        """;

    private static readonly string ConsolidatorPrompt = """
        你是一个记忆巩固助手。你的任务是将近期记忆中较旧的摘要巩固到核心记忆中。

        你会收到：
        1. 需要巩固的近期记忆摘要（较旧的部分，即将从近期记忆中移除）
        2. 当前核心记忆的内容（已持久化的长期信息）

        请分析这些摘要，提取核心信息，然后调用 UpdateCoreMemory 工具更新核心记忆。

        巩固规则：
        - 提取重要的事实、决策、用户偏好、待办事项、讨论结论
        - **重点关注目标信息**：用户正在进行的目标、计划、任务进展，这些是最重要的记忆内容
        - 如果摘要中体现了目标的阶段性进展，务必记录到核心记忆的"当前目标"分区
        - 如果目标已完成，将其从进行中移到已完成或直接移除
        - 将新信息与已有核心记忆合并，不要丢失已有信息
        - 核心记忆是高度压缩的，只保留最重要的信息
        - 使用 Markdown 格式，按以下分区结构组织，简洁精炼

        核心记忆格式示例：
        ```markdown
        ## 当前目标
        - 正在开发电商平台的订单模块，需要支持退款流程
        - 计划将用户认证从 Session 迁移到 JWT

        ## 待办事项
        - 订单表需要新增 refund_status 字段
        - 编写退款 API 的单元测试

        ## 用户偏好
        - 偏好使用中文交流
        - 代码风格：简洁，不喜欢过度注释
        - 常用技术栈：React + Node.js + PostgreSQL

        ## 关键事实
        - 项目使用 monorepo 结构，前端在 packages/web，后端在 packages/api
        - 数据库已有 users、orders、products 三张核心表
        - 部署环境：阿里云 ECS + Docker Compose

        ## 重要决策
        - API 统一使用 RESTful 风格，不用 GraphQL
        - 前端状态管理选择了 Zustand 而非 Redux

        ## 讨论结论
        - 退款流程确定为：用户申请 → 客服审核 → 自动退款到原支付方式
        - 并发问题决定用乐观锁处理，不用分布式锁
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
