using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using sharpclaw.Core;
using sharpclaw.UI;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace sharpclaw.Agents;

/// <summary>
/// 对话归档器：当滑动窗口裁剪掉旧消息时，用 AI 提取核心信息更新主要记忆，
/// 并将被裁剪的完整对话保存为 Markdown 历史文件。
/// </summary>
public class ConversationArchiver
{
    /// <summary>向后兼容：用于剥离旧会话中残留的摘要注入消息。</summary>
    internal const string AutoSummaryKey = "__auto_summary__";

    private static readonly string ArchiverPrompt = """
        你是一个记忆提取助手。你的任务是从被裁剪的对话内容中提取核心信息，更新主要记忆。

        你会收到：
        1. 本次被裁剪掉的对话内容（即将从窗口中移除的旧消息）
        2. 当前保留在窗口中的对话内容（供你参考上下文，避免提取重复信息）
        3. 当前主要记忆的内容（已持久化的长期信息）

        请分析被裁剪的对话，提取其中的核心信息，然后调用 UpdatePrimaryMemory 工具更新主要记忆。

        提取规则：
        - 提取重要的事实、决策、用户偏好、待办事项、讨论结论
        - 提取用户明确表达的长期有效信息
        - 不要提取保留窗口中已有的信息（避免重复）
        - 忽略临时性的对话内容（寒暄、确认、临时状态）
        - 忽略工具调用的具体细节（只保留关键结论）
        - 将新提取的信息与已有主要记忆合并，不要丢失已有信息
        - 使用 Markdown 格式，要点列表，简洁精炼

        如果被裁剪的内容中没有值得保留的核心信息，则不需要调用工具，直接回复"无需更新"。
        """;

    private readonly IChatClient _client;
    private readonly string? _primaryMemoryPath;
    private readonly string _historyDir;

    public ConversationArchiver(IChatClient client, string? primaryMemoryPath = null)
    {
        _client = new ChatClientBuilder(client)
            .UseFunctionInvocation()
            .Build();
        _primaryMemoryPath = primaryMemoryPath;
        _historyDir = Path.Combine(
            Path.GetDirectoryName(SharpclawConfig.ConfigPath)!, "history");
    }

    /// <summary>
    /// 归档被裁剪的消息：提取核心信息到主要记忆，保存完整对话为历史文件。
    /// </summary>
    /// <param name="trimmedMessages">被裁剪掉的消息</param>
    /// <param name="retainedMessages">保留在窗口中的消息，供 AI 参考避免提取重复信息</param>
    /// <param name="cancellationToken"></param>
    public async Task ArchiveAsync(
        IReadOnlyList<ChatMessage> trimmedMessages,
        IReadOnlyList<ChatMessage> retainedMessages,
        CancellationToken cancellationToken = default)
    {
        if (trimmedMessages.Count == 0)
            return;

        // 并行执行：保存历史文件 + AI 提取核心信息
        var saveTask = SaveHistoryFileAsync(trimmedMessages, cancellationToken);
        var extractTask = ExtractCoreInfoAsync(trimmedMessages, retainedMessages, cancellationToken);

        await Task.WhenAll(saveTask, extractTask);
    }

    /// <summary>
    /// 将被裁剪的消息保存为 Markdown 历史文件。
    /// </summary>
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

    /// <summary>
    /// 用 AI 提取被裁剪消息中的核心信息，更新主要记忆。
    /// </summary>
    private async Task ExtractCoreInfoAsync(
        IReadOnlyList<ChatMessage> trimmedMessages,
        IReadOnlyList<ChatMessage> retainedMessages,
        CancellationToken cancellationToken)
    {
        if (_primaryMemoryPath is null)
            return;

        try
        {
            AppLogger.SetStatus("提取核心记忆...");

            // 提取被裁剪消息的文本
            var trimmedText = FormatMessages(trimmedMessages);
            if (trimmedText.Length == 0)
                return;

            // 读取当前主要记忆
            var primaryMemory = "";
            if (File.Exists(_primaryMemoryPath))
            {
                try { primaryMemory = await File.ReadAllTextAsync(_primaryMemoryPath, cancellationToken); }
                catch { /* ignore */ }
            }

            var sb = new StringBuilder();
            sb.AppendLine("## 本次被裁剪的对话内容");
            sb.Append(trimmedText);
            sb.AppendLine();

            // 保留消息供参考
            var retainedText = FormatMessages(retainedMessages, maxResultLength: 100);
            if (retainedText.Length > 0)
            {
                sb.AppendLine("## 当前保留在窗口中的对话内容（不要重复提取这些信息）");
                sb.Append(retainedText);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(primaryMemory))
            {
                sb.AppendLine("## 当前主要记忆内容");
                sb.AppendLine(primaryMemory);
            }
            else
            {
                sb.AppendLine("## 当前无主要记忆");
            }

            // UpdatePrimaryMemory 工具
            [Description("更新主要记忆文件。将核心信息（用户偏好、关键事实、重要决策）写入持久化存储。传入完整的新内容（应包含已有内容的合并）。")]
            async Task<string> UpdatePrimaryMemory(
                [Description("主要记忆的完整新内容（Markdown 格式）")] string content)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_primaryMemoryPath);
                    if (dir is not null && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    await File.WriteAllTextAsync(_primaryMemoryPath, content, cancellationToken);
                    AppLogger.Log($"[Archive] 已更新主要记忆（{content.Length}字）");
                    return $"已更新主要记忆（{content.Length}字）";
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Archive] 主要记忆写入失败: {ex.Message}");
                    return $"写入失败: {ex.Message}";
                }
            }

            var options = new ChatOptions
            {
                Instructions = ArchiverPrompt,
                Tools = [AIFunctionFactory.Create(UpdatePrimaryMemory)]
            };

            var agent = _client.AsBuilder().UseFunctionInvocation().BuildAIAgent(new ChatClientAgentOptions
            {
                ChatOptions = options
            });

            var messages = new List<ChatMessage> { new(ChatRole.User, sb.ToString()) };
            await agent.RunAsync(messages, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Archive] 核心信息提取失败: {ex.Message}");
        }
    }

    private static StringBuilder FormatMessages(IReadOnlyList<ChatMessage> messages, int maxResultLength = 200)
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
                        if (resultText.Length > maxResultLength)
                            resultText = resultText[..maxResultLength] + "...";
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
}
