using Microsoft.Extensions.AI;
using System.Text;

using sharpclaw.Agents;
using sharpclaw.UI;

namespace sharpclaw.Chat;

/// <summary>
/// 滑动窗口聊天裁剪器：集成记忆管线。
/// 流程：主动记忆(保存) → 剥离旧注入 → 滑动窗口裁剪 → 总结性回忆注入
/// 记忆回忆注入由主循环在输入消息时触发。
/// </summary>
public class SlidingWindowChatReducer : IChatReducer
{
    internal const string AutoMemoryKey = "__auto_memories__";

    private readonly int _windowSize;
    private readonly int _overflowBuffer;
    private readonly string? _systemPrompt;
    private readonly MemorySaver? _memorySaver;
    private readonly ConversationSummarizer? _summarizer;

    /// <param name="windowSize">裁剪后保留的消息数</param>
    /// <param name="overflowBuffer">超出 windowSize 多少条后才触发裁剪。默认 5。</param>
    public SlidingWindowChatReducer(
        int windowSize,
        int overflowBuffer = 5,
        string? systemPrompt = null,
        MemorySaver? memorySaver = null,
        ConversationSummarizer? summarizer = null)
    {
        _windowSize = windowSize;
        _overflowBuffer = overflowBuffer;
        _systemPrompt = systemPrompt;
        _memorySaver = memorySaver;
        _summarizer = summarizer;
    }

    public async Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var all = messages.ToList();

        // 从消息中提取对话日志和最新用户输入（供主动记忆使用）
        var conversationLog = new List<string>();

        foreach (var msg in all)
        {
            if (msg.AdditionalProperties?.ContainsKey(AutoMemoryKey) == true)
                continue;
            if (msg.AdditionalProperties?.ContainsKey(ConversationSummarizer.AutoSummaryKey) == true)
                continue;
            if (msg.Role == ChatRole.System)
                continue;

            var text = msg.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            if (msg.Role == ChatRole.User)
            {
                conversationLog.Add($"用户: {text}");
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                conversationLog.Add($"助手: {text}");
            }
        }

        // ── 1. 主动记忆：分析对话内容，保存重要信息 ──
        if (_memorySaver is not null)
        {
            try
            {
                await _memorySaver.SaveAsync(conversationLog, cancellationToken);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AutoSave] 记忆保存失败: {ex.Message}");
            }
        }

        // ── 2. 剥离旧的自动注入消息（记忆 + 摘要）──
        var systemMessages = new List<ChatMessage>();
        var conversationMessages = new List<ChatMessage>();

        foreach (var msg in all)
        {
            if (msg.AdditionalProperties?.ContainsKey(AutoMemoryKey) == true)
                continue;
            if (msg.AdditionalProperties?.ContainsKey(ConversationSummarizer.AutoSummaryKey) == true)
                continue;

            if (msg.Role == ChatRole.System)
                systemMessages.Add(msg);
            else
                conversationMessages.Add(msg);
        }

        if (_systemPrompt is not null && systemMessages.Count == 0)
            systemMessages.Add(new ChatMessage(ChatRole.System, _systemPrompt));

        // ── 3. 滑动窗口裁剪（超出 windowSize + overflowBuffer 才触发，裁剪到 windowSize）──
        var trimmedMessages = new List<ChatMessage>();

        if (conversationMessages.Count > _windowSize + _overflowBuffer)
        {
            var cutIndex = Math.Max(0, conversationMessages.Count - _windowSize);
            var originalCutIndex = cutIndex;
            var searchLimit = Math.Min(cutIndex + _overflowBuffer, conversationMessages.Count - 1);

            // 向前搜索最近的 User 消息边界，但限制搜索范围避免裁剪过多
            while (cutIndex < searchLimit &&
                   conversationMessages[cutIndex].Role != ChatRole.User)
            {
                cutIndex++;
            }

            // 如果向前没找到，向后搜索
            if (conversationMessages[cutIndex].Role != ChatRole.User)
            {
                cutIndex = originalCutIndex;
                while (cutIndex > 0 &&
                       conversationMessages[cutIndex].Role != ChatRole.User)
                {
                    cutIndex--;
                }
            }

            trimmedMessages = conversationMessages.Take(cutIndex).ToList();
            conversationMessages = conversationMessages.Skip(cutIndex).ToList();
        }

        // ── 4. 总结性回忆：将被裁剪的内容总结后注入 ──
        var summaryMessages = new List<ChatMessage>();
        if (_summarizer is not null && (trimmedMessages.Count > 0 || conversationMessages.Count > 0))
        {
            try
            {
                var summaryMsg = await _summarizer.SummarizeAsync(trimmedMessages, cancellationToken);
                if (summaryMsg is not null)
                    summaryMessages.Add(summaryMsg);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AutoSummary] 总结失败: {ex.Message}");
            }
        }

        IEnumerable<ChatMessage> result = [.. systemMessages, .. summaryMessages, .. conversationMessages];
        return result;
    }
}
