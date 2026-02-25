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
    internal const string AutoPrimaryMemoryKey = "__auto_primary_memory__";

    private readonly int _windowSize;
    private readonly int _overflowBuffer;
    private readonly string? _systemPrompt;
    private readonly ConversationArchiver? _archiver;

    private Dictionary<string, ChatMessage> _trimmedMessages = new Dictionary<string, ChatMessage>();

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

        // ── 1. 剥离旧的自动注入消息（记忆 + 主要记忆 + 旧版摘要兼容）──
        var systemMessages = new List<ChatMessage>();
        var conversationMessages = new List<ChatMessage>();
        ChatMessage? existingPrimaryMemory = null;

        foreach (var msg in all)
        {
            if (!string.IsNullOrWhiteSpace(msg.MessageId) && _trimmedMessages.ContainsKey(msg.MessageId))
                continue;
            if (msg.AdditionalProperties?.ContainsKey(AutoMemoryKey) == true)
                continue;
            if (msg.AdditionalProperties?.ContainsKey(ConversationArchiver.AutoSummaryKey) == true)
                continue;
            if (msg.AdditionalProperties?.ContainsKey(AutoPrimaryMemoryKey) == true)
            {
                existingPrimaryMemory = msg; // 保留引用，裁剪时更新而非新增
                continue;
            }

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

            trimmedMessages = conversationMessages.Take(cutIndex).ToList();
            foreach (var msg in trimmedMessages)
            {
                if (string.IsNullOrWhiteSpace(msg.MessageId))
                    msg.MessageId = Guid.NewGuid().ToString();
                _trimmedMessages[msg.MessageId] = msg;
            }
            conversationMessages = conversationMessages.Skip(cutIndex).ToList();
        }

        // ── 3. 归档被裁剪的消息（提取核心信息到主记忆 + 保存历史文件）──
        string? primaryMemory = null;
        if (_archiver is not null && trimmedMessages.Count > 0)
        {
            try
            {
                primaryMemory = await _archiver.ArchiveAsync(trimmedMessages, conversationMessages, cancellationToken);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Archive] 归档失败: {ex.Message}");
            }
        }

        // ── 4. 注入主要记忆（归档产生新内容则更新，否则保留已有的）──
        if (!string.IsNullOrWhiteSpace(primaryMemory))
        {
            systemMessages.Add(new ChatMessage(ChatRole.System,
                $"[主要记忆] 以下是持久化的长期重要信息：\n\n{primaryMemory}")
            {
                AdditionalProperties = new() { [AutoPrimaryMemoryKey] = true }
            });
            conversationMessages.Add(new ChatMessage(ChatRole.User,
                $"继续目标（如果有或未完成）")
            {
                AdditionalProperties = new() { [AutoMemoryKey] = true }
            });
            AppLogger.Log($"[Reducer] 已注入主要记忆（{primaryMemory.Length}字）");
        }
        else if (existingPrimaryMemory is not null)
        {
            systemMessages.Add(existingPrimaryMemory);
        }

        IEnumerable<ChatMessage> result = [.. systemMessages, .. conversationMessages];
        return result;
    }
}
