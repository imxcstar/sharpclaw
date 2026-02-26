using Microsoft.Extensions.AI;

using sharpclaw.Agents;
using sharpclaw.UI;

namespace sharpclaw.Chat;

/// <summary>
/// 滑动窗口聊天裁剪器：集成记忆管线。
/// 流程：剥离旧注入 → 记忆保存检测（MemorySaver）→ 滑动窗口裁剪 → 归档（摘要→近期记忆→巩固核心记忆）→ 注入记忆
///
/// 记忆管道：
/// 1. 剥离旧注入：移除上一轮自动注入的系统消息（向量记忆、近期记忆、核心记忆等）
/// 2. 记忆保存检测：每轮通过 MemorySaver 分析完整对话，结合核心记忆和近期记忆上下文，决定保存/更新/删除向量记忆
/// 3. 滑动窗口裁剪：超出窗口大小时裁剪旧消息
/// 4. 归档：对被裁剪的消息生成摘要追加到近期记忆，近期记忆溢出时巩固到核心记忆
/// 5. 注入记忆：将核心记忆和近期记忆作为系统消息注入上下文
/// </summary>
public class SlidingWindowChatReducer : IChatReducer
{
    internal const string AutoMemoryKey = "__auto_memories__";
    internal const string AutoRecentMemoryKey = "__auto_recent_memory__";
    internal const string AutoPrimaryMemoryKey = "__auto_primary_memory__";

    private readonly int _windowSize;
    private readonly int _overflowBuffer;
    private readonly string? _systemPrompt;
    private readonly ConversationArchiver? _archiver;
    private readonly MemorySaver? _memorySaver;

    private readonly Dictionary<string, ChatMessage> _trimmedMessages = new();

    /// <summary>由 MainAgent 每轮设置，供 MemorySaver 检索相关记忆。</summary>
    public string? LatestUserInput { get; set; }

    /// <param name="windowSize">裁剪后保留的消息数</param>
    /// <param name="overflowBuffer">超出 windowSize 多少条后才触发裁剪。默认 5。</param>
    public SlidingWindowChatReducer(
        int windowSize,
        int overflowBuffer = 5,
        string? systemPrompt = null,
        ConversationArchiver? archiver = null,
        MemorySaver? memorySaver = null)
    {
        _windowSize = windowSize;
        _overflowBuffer = overflowBuffer;
        _systemPrompt = systemPrompt;
        _archiver = archiver;
        _memorySaver = memorySaver;
    }

    public async Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var all = messages.ToList();

        // ── 1. 剥离旧的自动注入消息 ──
        var systemMessages = new List<ChatMessage>();
        var conversationMessages = new List<ChatMessage>();
        ChatMessage? existingRecentMemory = null;
        ChatMessage? existingPrimaryMemory = null;

        foreach (var msg in all)
        {
            if (!string.IsNullOrWhiteSpace(msg.MessageId) && _trimmedMessages.ContainsKey(msg.MessageId))
                continue;
            if (msg.AdditionalProperties?.ContainsKey(AutoMemoryKey) == true)
                continue;
            if (msg.AdditionalProperties?.ContainsKey(ConversationArchiver.AutoSummaryKey) == true)
                continue;
            if (msg.AdditionalProperties?.ContainsKey(AutoRecentMemoryKey) == true)
            {
                existingRecentMemory = msg;
                continue;
            }
            if (msg.AdditionalProperties?.ContainsKey(AutoPrimaryMemoryKey) == true)
            {
                existingPrimaryMemory = msg;
                continue;
            }

            if (msg.Role == ChatRole.System)
                systemMessages.Add(msg);
            else
                conversationMessages.Add(msg);
        }

        if (_systemPrompt is not null && systemMessages.Count == 0)
            systemMessages.Add(new ChatMessage(ChatRole.System, _systemPrompt));

        // ── 2. 记忆保存检测（裁剪前，确保即将被裁剪的消息也能被记忆）──
        if (_memorySaver is not null && conversationMessages.Count > 0 && LatestUserInput is not null)
        {
            try
            {
                var recentMem = existingRecentMemory?.Text ?? _archiver?.ReadRecentMemory();
                var primaryMem = existingPrimaryMemory?.Text ?? _archiver?.ReadPrimaryMemory();
                await _memorySaver.SaveAsync(conversationMessages, LatestUserInput,
                    recentMem, primaryMem, cancellationToken);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AutoSave] 记忆保存失败: {ex.Message}");
            }
        }

        // ── 3. 滑动窗口裁剪 ──
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

        // ── 4. 归档被裁剪的消息（摘要 → 近期记忆 → 溢出巩固核心记忆）──
        ArchiveResult? archiveResult = null;
        if (_archiver is not null && trimmedMessages.Count > 0)
        {
            try
            {
                archiveResult = await _archiver.ArchiveAsync(
                    trimmedMessages, conversationMessages, cancellationToken);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Archive] 归档失败: {ex.Message}");
            }
        }

        // ── 5. 注入记忆 ──
        InjectMemories(systemMessages, conversationMessages,
            archiveResult, existingRecentMemory, existingPrimaryMemory);

        IEnumerable<ChatMessage> result = [.. systemMessages, .. conversationMessages];
        return result;
    }

    private void InjectMemories(
        List<ChatMessage> systemMessages,
        List<ChatMessage> conversationMessages,
        ArchiveResult? archiveResult,
        ChatMessage? existingRecentMemory,
        ChatMessage? existingPrimaryMemory)
    {
        string? recentMemory;
        string? primaryMemory;

        if (archiveResult is not null)
        {
            // 归档刚发生，使用最新内容
            recentMemory = archiveResult.RecentMemory;
            primaryMemory = archiveResult.PrimaryMemory;
        }
        else if (existingRecentMemory is not null || existingPrimaryMemory is not null)
        {
            // 无归档，保留已有注入
            if (existingRecentMemory is not null)
                systemMessages.Add(existingRecentMemory);
            if (existingPrimaryMemory is not null)
                systemMessages.Add(existingPrimaryMemory);
            return;
        }
        else if (_archiver is not null)
        {
            // 首次加载，从文件读取
            recentMemory = _archiver.ReadRecentMemory();
            primaryMemory = _archiver.ReadPrimaryMemory();
        }
        else
        {
            return;
        }

        var injected = false;

        // 注入核心记忆
        if (!string.IsNullOrWhiteSpace(primaryMemory))
        {
            systemMessages.Add(new ChatMessage(ChatRole.System,
                $"[核心记忆] 以下是持久化的长期重要信息：\n\n{primaryMemory}")
            {
                AdditionalProperties = new() { [AutoPrimaryMemoryKey] = true }
            });
            injected = true;
            AppLogger.Log($"[Reducer] 已注入核心记忆（{primaryMemory.Length}字）");
        }

        // 注入近期记忆
        if (!string.IsNullOrWhiteSpace(recentMemory))
        {
            systemMessages.Add(new ChatMessage(ChatRole.System,
                $"[近期记忆] 以下是最近对话的详细摘要：\n\n{recentMemory}")
            {
                AdditionalProperties = new() { [AutoRecentMemoryKey] = true }
            });
            injected = true;
            AppLogger.Log($"[Reducer] 已注入近期记忆（{recentMemory.Length}字）");
        }

        // 归档后提示继续目标
        if (injected && archiveResult is not null)
        {
            conversationMessages.Add(new ChatMessage(ChatRole.User,
                "继续目标（如果有或未完成）")
            {
                AdditionalProperties = new() { [AutoMemoryKey] = true }
            });
        }
    }
}
