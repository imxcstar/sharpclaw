using Microsoft.Extensions.AI;
using System.Text;
using sharpclaw.UI;

namespace sharpclaw.Agents;

/// <summary>
/// 总结性回忆助手：当滑动窗口裁剪掉旧消息时，将被裁剪的内容总结为精简摘要注入回上下文，
/// 避免主智能体丢失早期对话的关键信息。支持增量总结：在上一轮摘要基础上融合新裁剪的内容。
/// </summary>
public class ConversationSummarizer
{
    internal const string AutoSummaryKey = "__auto_summary__";

    private static readonly string SummarizerPrompt = """
        你是一个对话总结助手。你的任务是维护一份对话历史的精简摘要。

        你会收到：
        1. 上一轮的摘要（可能为空，表示首次总结）
        2. 本次被裁剪掉的对话内容（即将从窗口中移除的旧消息）

        请生成一份更新后的摘要，要求：
        - 融合上一轮摘要和新裁剪内容中的关键信息
        - 保留重要的事实、决策、用户偏好、待办事项、讨论结论
        - 去除已过时或不再相关的信息
        - 摘要应简洁精炼，控制在 300 字以内
        - 使用要点列表格式，便于快速浏览
        - 直接输出摘要内容，不要加任何前缀或解释
        """;

    private readonly IChatClient _client;
    private string _currentSummary = "";

    public ConversationSummarizer(IChatClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 将被裁剪的消息融合到摘要中。返回注入用的系统消息，无内容时返回 null。
    /// </summary>
    public async Task<ChatMessage?> SummarizeAsync(
        IReadOnlyList<ChatMessage> trimmedMessages,
        CancellationToken cancellationToken = default)
    {
        if (trimmedMessages.Count == 0 && _currentSummary.Length == 0)
            return null;

        AppLogger.SetStatus("对话总结中...");
        // 提取被裁剪消息的文本
        var trimmedText = new StringBuilder();
        foreach (var msg in trimmedMessages)
        {
            var text = msg.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            var role = msg.Role == ChatRole.User ? "用户" : "助手";
            trimmedText.AppendLine($"{role}: {text}");
        }

        // 没有新裁剪内容且已有摘要，直接返回现有摘要
        if (trimmedText.Length == 0)
            return _currentSummary.Length > 0 ? FormatSummaryMessage(_currentSummary) : null;

        var sb = new StringBuilder();
        if (_currentSummary.Length > 0)
        {
            sb.AppendLine("## 上一轮摘要");
            sb.AppendLine(_currentSummary);
            sb.AppendLine();
        }
        sb.AppendLine("## 本次被裁剪的对话内容");
        sb.Append(trimmedText);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SummarizerPrompt),
            new(ChatRole.User, sb.ToString())
        };

        var response = await _client.GetResponseAsync(messages, cancellationToken: cancellationToken);
        _currentSummary = response.Text?.Trim() ?? "";

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
