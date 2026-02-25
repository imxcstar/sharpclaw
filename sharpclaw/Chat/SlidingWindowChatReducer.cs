using Microsoft.Extensions.AI;

using sharpclaw.Agents;
using sharpclaw.UI;

namespace sharpclaw.Chat;

/// <summary>
/// 滑动窗口聊天裁剪器：集成归档管线。
/// 流程：剥离旧注入 → 滑动窗口裁剪 → 归档被裁剪的消息
/// 记忆回忆注入由主循环在输入消息时触发。MemorySaver 由 MainAgent 在流式输出完成后调用。
/// </summary>
public class SlidingWindowChatReducer : IChatReducer
{
    internal const string AutoMemoryKey = "__auto_memories__";

    private readonly int _windowSize;
    private readonly int _overflowBuffer;
    private readonly string? _systemPrompt;
    private readonly ConversationArchiver? _archiver;

    /// <param name="windowSize">裁剪后保留的消息数</param>
    /// <param name="overflowBuffer">超出 windowSize 多少条后才触发裁剪。默认 5。</param>
    public SlidingWindowChatReducer(
        int windowSize,
        int overflowBuffer = 5,
        string? systemPrompt = null,
        ConversationArchiver? archiver = null)
    {
        _windowSize = windowSize;
        _overflowBuffer = overflowBuffer;
        _systemPrompt = systemPrompt;
        _archiver = archiver;
    }

    public async Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var all = messages.ToList();

        // ── 1. 剥离旧的自动注入消息（记忆 + 旧版摘要兼容）──
        var systemMessages = new List<ChatMessage>();
        var conversationMessages = new List<ChatMessage>();

        foreach (var msg in all)
        {
            if (msg.AdditionalProperties?.ContainsKey(AutoMemoryKey) == true)
                continue;
            if (msg.AdditionalProperties?.ContainsKey(ConversationArchiver.AutoSummaryKey) == true)
                continue;

            if (msg.Role == ChatRole.System)
                systemMessages.Add(msg);
            else
                conversationMessages.Add(msg);
        }

        if (_systemPrompt is not null && systemMessages.Count == 0)
            systemMessages.Add(new ChatMessage(ChatRole.System, _systemPrompt));

        // ── 2. 滑动窗口裁剪（超出 windowSize + overflowBuffer 才触发，裁剪到 windowSize）──
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

            // 如果仍然没找到合适的 User 边界（例如工具调用循环中只有一条 User 消息在最前面），
            // 回退到原始 cutIndex 强制裁剪，避免消息无限增长
            if (cutIndex <= 0)
                cutIndex = originalCutIndex;

            trimmedMessages = conversationMessages.Take(cutIndex).ToList();
            conversationMessages = conversationMessages.Skip(cutIndex).ToList();
        }

        // ── 3. 归档被裁剪的消息（提取核心信息到主记忆 + 保存历史文件）──
        if (_archiver is not null && trimmedMessages.Count > 0)
        {
            try
            {
                await _archiver.ArchiveAsync(trimmedMessages, conversationMessages, cancellationToken);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Archive] 归档失败: {ex.Message}");
            }
        }

        IEnumerable<ChatMessage> result = [.. systemMessages, .. conversationMessages];
        return result;
    }
}
