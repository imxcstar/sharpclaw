using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using sharpclaw.UI;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace sharpclaw.Agents;

/// <summary>
/// 总结性回忆助手：当滑动窗口裁剪掉旧消息时，将被裁剪的内容总结为精简摘要注入回上下文，
/// 避免主智能体丢失早期对话的关键信息。支持增量总结：在上一轮摘要基础上融合新裁剪的内容。
/// 当工作摘要过长无法再压缩时，将稳定的长期信息提升到主要记忆（primary_memory.md）。
/// </summary>
public class ConversationSummarizer
{
    internal const string AutoSummaryKey = "__auto_summary__";

    private static readonly string SummarizerPrompt = """
        你是一个对话总结助手。你的任务是维护一份对话历史的精简摘要。

        你会收到：
        1. 上一轮的摘要（可能为空，表示首次总结）
        2. 本次被裁剪掉的对话内容（即将从窗口中移除的旧消息）
        3. 当前保留在窗口中的对话内容（不会被移除，供你参考避免重复）
        4. 当前主要记忆的内容（已持久化的长期信息）

        请生成一份更新后的摘要，要求：
        - 融合上一轮摘要和新裁剪内容中的关键信息
        - 保留重要的事实、决策、用户偏好、待办事项、讨论结论
        - 去除已过时或不再相关的信息
        - 不要重复保留窗口中已有的内容
        - 摘要应简洁精炼，控制在 300 字以内
        - 使用要点列表格式，便于快速浏览

        关于主要记忆：
        - 主要记忆是持久化的长期信息存储，不会随对话窗口滑动而丢失
        - 当工作摘要过长（接近或超过 300 字）且包含稳定的长期信息时，应将这些信息通过 UpdatePrimaryMemory 工具移入主要记忆
        - 适合移入主要记忆的信息：用户偏好、关键事实、重要决策、长期有效的结论
        - 不适合移入的信息：临时状态、进行中的任务细节、可能很快变化的信息
        - 移入主要记忆后，从工作摘要中移除对应内容以保持摘要精简
        - 更新主要记忆时，应将新内容与已有内容合并，不要丢失已有信息
        - 调用 UpdatePrimaryMemory 后，再输出更新后的工作摘要（不含已移入的内容）

        最终输出：直接输出更新后的摘要内容，不要加任何前缀或解释
        """;

    private readonly IChatClient _client;
    private readonly string? _primaryMemoryPath;
    private string _currentSummary = "";

    public ConversationSummarizer(IChatClient client, string? primaryMemoryPath = null)
    {
        _client = new ChatClientBuilder(client)
            .UseFunctionInvocation()
            .Build();
        _primaryMemoryPath = primaryMemoryPath;
    }

    /// <summary>
    /// 将被裁剪的消息融合到摘要中。返回注入用的系统消息，无内容时返回 null。
    /// </summary>
    /// <param name="trimmedMessages">被裁剪掉的消息</param>
    /// <param name="retainedMessages">保留在窗口中的消息</param>
    /// <param name="cancellationToken"></param>
    public async Task<ChatMessage?> SummarizeAsync(
        IReadOnlyList<ChatMessage> trimmedMessages,
        IReadOnlyList<ChatMessage> retainedMessages,
        CancellationToken cancellationToken = default)
    {
        if (trimmedMessages.Count == 0 && _currentSummary.Length == 0)
            return null;

        AppLogger.SetStatus("对话总结中...");
        // 提取被裁剪消息的文本（包括工具调用和结果）
        var trimmedText = new StringBuilder();
        foreach (var msg in trimmedMessages)
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
                        if (resultText.Length > 200)
                            resultText = resultText[..200] + "...";
                        parts.Add($"[工具结果: {resultText}]");
                        break;
                }
            }

            if (parts.Count == 0) continue;

            var role = msg.Role == ChatRole.User ? "用户"
                     : msg.Role == ChatRole.Assistant ? "助手"
                     : "工具";
            trimmedText.AppendLine($"{role}: {string.Join(" ", parts)}");
        }

        // 没有新裁剪内容且已有摘要，直接返回现有摘要
        if (trimmedText.Length == 0)
            return _currentSummary.Length > 0 ? FormatSummaryMessage(_currentSummary) : null;

        // 读取当前主要记忆
        var primaryMemory = "";
        if (_primaryMemoryPath is not null && File.Exists(_primaryMemoryPath))
        {
            try { primaryMemory = await File.ReadAllTextAsync(_primaryMemoryPath, cancellationToken); }
            catch { /* ignore read errors */ }
        }

        var sb = new StringBuilder();
        if (_currentSummary.Length > 0)
        {
            sb.AppendLine("## 上一轮摘要");
            sb.AppendLine(_currentSummary);
            sb.AppendLine();
        }
        sb.AppendLine("## 本次被裁剪的对话内容");
        sb.Append(trimmedText);
        sb.AppendLine();

        // 提取保留消息的文本（供总结助手参考，避免重复）
        var retainedText = new StringBuilder();
        foreach (var msg in retainedMessages)
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
                        parts.Add($"[调用工具 {call.Name}]");
                        break;
                    case FunctionResultContent result:
                        var resultText = result.Result?.ToString() ?? "";
                        if (resultText.Length > 100)
                            resultText = resultText[..100] + "...";
                        parts.Add($"[工具结果: {resultText}]");
                        break;
                }
            }
            if (parts.Count == 0) continue;

            var role = msg.Role == ChatRole.User ? "用户"
                     : msg.Role == ChatRole.Assistant ? "助手"
                     : "工具";
            retainedText.AppendLine($"{role}: {string.Join(" ", parts)}");
        }
        if (retainedText.Length > 0)
        {
            sb.AppendLine("## 当前保留在窗口中的对话内容（不要重复这些内容）");
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

        // UpdatePrimaryMemory 工具闭包
        [Description("更新主要记忆文件。将稳定的长期信息（用户偏好、关键事实、重要决策）写入持久化存储。传入完整的新内容（应包含已有内容的合并）。")]
        async Task<string> UpdatePrimaryMemory(
            [Description("主要记忆的完整新内容（Markdown 格式）")] string content)
        {
            if (_primaryMemoryPath is null)
                return "主要记忆未启用";

            try
            {
                var dir = Path.GetDirectoryName(_primaryMemoryPath);
                if (dir is not null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(_primaryMemoryPath, content, cancellationToken);
                AppLogger.Log($"[AutoSummary] 已更新主要记忆（{content.Length}字）");
                return $"已更新主要记忆（{content.Length}字）";
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AutoSummary] 主要记忆写入失败: {ex.Message}");
                return $"写入失败: {ex.Message}";
            }
        }

        var options = new ChatOptions
        {
            Instructions = SummarizerPrompt,
            Tools = _primaryMemoryPath is not null
                ? [AIFunctionFactory.Create(UpdatePrimaryMemory)]
                : []
        };

        var agent = _client.AsBuilder().UseFunctionInvocation().BuildAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = options
        });

        var messages = new List<ChatMessage> { new(ChatRole.User, sb.ToString()) };
        var ret = await agent.RunAsync(messages, cancellationToken: cancellationToken);

        _currentSummary = ret?.Text?.Trim() ?? "";

        AppLogger.Log($"[AutoSummary] 已更新摘要（{_currentSummary.Length}字）");

        return _currentSummary.Length > 0 ? FormatSummaryMessage(_currentSummary) : null;
    }

    private static ChatMessage FormatSummaryMessage(string summary)
    {
        var content = $"[对话历史摘要] 以下是早期对话的精简总结，供你参考：\n\n{summary}";
        var msg = new ChatMessage(ChatRole.System, content);
        (msg.AdditionalProperties ??= [])[AutoSummaryKey] = "true";
        return msg;
    }
}
