using System.Text;
using Microsoft.Extensions.AI;
using sharpclaw.Agents;
using sharpclaw.UI;

namespace sharpclaw.Chat;

/// <summary>
/// 记忆管线聊天裁剪器。
/// 当对话消息超过阈值时，清空对话并归档到记忆系统。
///
/// 流程：
/// 1. 剥离旧注入：移除上一轮自动注入的系统消息（向量记忆、近期记忆、核心记忆等）
/// 2. 保存工作记忆：将当前对话持久化到工作记忆文件
/// 3. 溢出检测：消息数超过阈值时触发清空
/// 4. 记忆保存：通过 MemorySaver 分析对话，决定保存/更新/删除向量记忆
/// 5. 归档：对话摘要追加到近期记忆，近期记忆溢出时巩固到核心记忆
/// 6. 注入记忆：将核心记忆和近期记忆作为系统消息注入上下文
/// </summary>
public class MemoryPipelineChatReducer : IChatReducer
{
    internal const string AutoMemoryKey = "__auto_memories__";
    internal const string AutoRecentMemoryKey = "__auto_recent_memory__";
    internal const string AutoPrimaryMemoryKey = "__auto_primary_memory__";
    internal const string AutoWorkingMemoryKey = "__auto_working_memory__";

    private readonly int _resetThreshold;
    private readonly string? _systemPrompt;
    private readonly ConversationArchiver? _archiver;
    private readonly MemorySaver? _memorySaver;

    private readonly Dictionary<string, ChatMessage> _archivedMessages = new();

    public static List<ChatMessage> LastMessages = new List<ChatMessage>();

    /// <summary>由 MainAgent 每轮设置，用户本轮发起对话的原始输入。</summary>
    public string? UserInput { get; set; }

    /// <summary>工作记忆文件路径，由 MainAgent 设置。</summary>
    public string? WorkingMemoryPath { get; set; }

    /// <summary>
    /// 上一次的工作记忆内容快照（对话结束时保存），供 MainAgent 在新会话开始时注入。
    /// </summary>
    public string? OldWorkingMemoryContent { get; set; }

    /// <summary>由 MainAgent 在流式输出时累积的工作记忆内容。</summary>
    public StringBuilder WorkingMemoryBuffer { get; } = new();

    /// <param name="resetThreshold">消息数超过此阈值时触发清空和归档</param>
    public MemoryPipelineChatReducer(
        int resetThreshold,
        string? systemPrompt = null,
        ConversationArchiver? archiver = null,
        MemorySaver? memorySaver = null)
    {
        _resetThreshold = resetThreshold;
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
            if (!string.IsNullOrWhiteSpace(msg.MessageId) && _archivedMessages.ContainsKey(msg.MessageId))
                continue;
            if (msg.AdditionalProperties?.ContainsKey(AutoMemoryKey) == true)
                continue;
            if (msg.AdditionalProperties?.ContainsKey(ConversationArchiver.AutoSummaryKey) == true)
                continue;
            if (msg.AdditionalProperties?.ContainsKey(AutoWorkingMemoryKey) == true)
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

        // ── 2. 保存工作记忆（从 MainAgent 流式累积的 WorkingMemoryBuffer）──
        SaveWorkingMemory();

        // ── 3. 溢出时清空对话并归档 ──
        ArchiveResult? archiveResult = null;

        if (conversationMessages.Sum(x => x.Contents.Count()) > _resetThreshold && WorkingMemoryBuffer.Length > 200000)
        {
            // 进入裁剪，清空累积的工作记忆
            OldWorkingMemoryContent = string.Empty;
            WorkingMemoryBuffer.Clear();

            // 向量记忆保存（归档前，确保对话内容被向量记忆捕获）
            if (_memorySaver is not null && UserInput is not null)
            {
                try
                {
                    await _memorySaver.SaveAsync(conversationMessages, UserInput, cancellationToken);
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[AutoSave] 记忆保存失败: {ex.Message}");
                }
            }

            // 标记所有消息为已归档
            foreach (var msg in conversationMessages)
            {
                if (string.IsNullOrWhiteSpace(msg.MessageId))
                    msg.MessageId = Guid.NewGuid().ToString();
                _archivedMessages[msg.MessageId] = msg;
            }

            // 归档（摘要 → 近期记忆 → 溢出巩固核心记忆）
            if (_archiver is not null)
            {
                try
                {
                    archiveResult = await _archiver.ArchiveAsync(
                        conversationMessages, cancellationToken);
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Archive] 归档失败: {ex.Message}");
                }
            }

            // 清空对话
            conversationMessages = [];
        }

        // ── 4. 注入记忆 ──
        InjectMemories(systemMessages, archiveResult, existingRecentMemory, existingPrimaryMemory);

        // ── 5. 注入工作记忆（上次会话的对话快照）──
        if (!string.IsNullOrWhiteSpace(OldWorkingMemoryContent))
        {
            systemMessages.Add(new ChatMessage(ChatRole.System,
                $"[工作记忆] 以下是上次会话的对话记录，供你参考延续上下文：\n\n{OldWorkingMemoryContent}")
            {
                AdditionalProperties = new() { [AutoWorkingMemoryKey] = true }
            });
            AppLogger.Log($"[Reducer] 已注入工作记忆（{OldWorkingMemoryContent.Length}字）");
        }

        // ── 6. 确保对话以用户消息开头 ──
        if (conversationMessages.FirstOrDefault()?.Role != ChatRole.User)
            conversationMessages.Insert(0, new ChatMessage(ChatRole.User, UserInput)
            {
                AdditionalProperties = new() { [AutoMemoryKey] = true }
            });

        LastMessages = conversationMessages;
        IEnumerable<ChatMessage> result = [.. systemMessages, .. LastMessages];
        return result;
    }

    private void SaveWorkingMemory()
    {
        if (WorkingMemoryPath is null)
            return;

        try
        {
            var dir = Path.GetDirectoryName(WorkingMemoryPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var content = WorkingMemoryBuffer.ToString();
            File.WriteAllText(WorkingMemoryPath, content);
            AppLogger.Log($"[Reducer] 已保存工作记忆（{content.Length}字）");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Reducer] 工作记忆保存失败: {ex.Message}");
        }
    }

    private void InjectMemories(
        List<ChatMessage> systemMessages,
        ArchiveResult? archiveResult,
        ChatMessage? existingRecentMemory,
        ChatMessage? existingPrimaryMemory)
    {
        string? recentMemory;
        string? primaryMemory;

        if (archiveResult is not null)
        {
            recentMemory = archiveResult.RecentMemory;
            primaryMemory = archiveResult.PrimaryMemory;
        }
        else if (existingRecentMemory is not null || existingPrimaryMemory is not null)
        {
            if (existingRecentMemory is not null)
                systemMessages.Add(existingRecentMemory);
            if (existingPrimaryMemory is not null)
                systemMessages.Add(existingPrimaryMemory);
            return;
        }
        else if (_archiver is not null)
        {
            recentMemory = _archiver.ReadRecentMemory();
            primaryMemory = _archiver.ReadPrimaryMemory();
        }
        else
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(primaryMemory))
        {
            systemMessages.Add(new ChatMessage(ChatRole.System,
                $"[核心记忆] 以下是持久化的长期重要信息：\n\n{primaryMemory}")
            {
                AdditionalProperties = new() { [AutoPrimaryMemoryKey] = true }
            });
            AppLogger.Log($"[Reducer] 已注入核心记忆（{primaryMemory.Length}字）");
        }

        if (!string.IsNullOrWhiteSpace(recentMemory))
        {
            systemMessages.Add(new ChatMessage(ChatRole.System,
                $"[近期记忆] 以下是最近对话的详细摘要：\n\n{recentMemory}")
            {
                AdditionalProperties = new() { [AutoRecentMemoryKey] = true }
            });
            AppLogger.Log($"[Reducer] 已注入近期记忆（{recentMemory.Length}字）");
        }
    }
}
