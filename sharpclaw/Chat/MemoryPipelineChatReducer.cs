using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using sharpclaw.Agents;
using sharpclaw.Core;
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
    private readonly IAgentContext _agentContext;

    private readonly Dictionary<string, ChatMessage> _archivedMessages = new();

    public static List<ChatMessage> LastMessages = new List<ChatMessage>();

    /// <summary>由 MainAgent 每轮设置，用户本轮发起对话的原始输入。</summary>
    public string? UserInput { get; set; }

    /// <summary>
    /// 上一次的工作记忆内容快照（对话结束时保存），供 MainAgent 在新会话开始时注入。
    /// </summary>
    public List<ChatMessage> OldWorkingMemoryContent { get; set; } = [];

    /// <summary>由 MainAgent 在流式输出时累积的工作记忆内容。</summary>
    public List<ChatMessage> WorkingMemoryBuffer { get; } = [];

    /// <param name="resetThreshold">消息数超过此阈值时触发清空和归档</param>
    public MemoryPipelineChatReducer(
        IAgentContext agentContext,
        int resetThreshold,
        string? systemPrompt = null,
        ConversationArchiver? archiver = null,
        MemorySaver? memorySaver = null)
    {
        _agentContext = agentContext;
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
                continue;
            if (msg.AdditionalProperties?.ContainsKey(AutoPrimaryMemoryKey) == true)
                continue;

            if (msg.Role == ChatRole.System)
                systemMessages.Add(msg);
            else
                conversationMessages.Add(msg);
        }

        if (_systemPrompt is not null && systemMessages.Count == 0)
            systemMessages.Add(new ChatMessage(ChatRole.System, _systemPrompt));

        // 注入当前时间
        systemMessages.Add(new ChatMessage(ChatRole.System,
            $"[当前时间] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
        {
            AdditionalProperties = new() { [AutoMemoryKey] = true }
        });

        // ── 2. 保存工作记忆（从 MainAgent 流式累积的 WorkingMemoryBuffer）──
        SaveWorkingMemory();

        // ── 3. 溢出时清空对话并归档 ──
        ArchiveResult? archiveResult = null;

        List<ChatMessage> allMessages = [.. OldWorkingMemoryContent, .. conversationMessages];
        var messageText = ConvertMessagesToText(allMessages).Replace(" ", "");
        if (messageText.Length > 150000)
        {
            // 进入裁剪，清空累积的工作记忆
            OldWorkingMemoryContent.Clear();
            var workingMemoryBuffer = WorkingMemoryBuffer.ToList();
            WorkingMemoryBuffer.Clear();

            // 向量记忆保存（归档前，确保对话内容被向量记忆捕获）
            if (_memorySaver is not null && UserInput is not null)
            {
                try
                {
                    await _memorySaver.SaveAsync(allMessages, cancellationToken);
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
            await SaveHistoryFileAsync([.. workingMemoryBuffer, .. conversationMessages], cancellationToken);
            if (_archiver is not null)
            {
                try
                {
                    archiveResult = await _archiver.ArchiveAsync(allMessages, cancellationToken);
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Archive] 归档失败: {ex.Message}");
                }
            }

            // 清空对话
            conversationMessages = [];
        }

        var memoryMessages = new List<ChatMessage>();
        // ── 4. 注入记忆 ──
        InjectMemories(systemMessages, memoryMessages, archiveResult);

        // ── 5. 注入工作记忆（上次会话的对话快照）──
        if (OldWorkingMemoryContent.Count > 0)
        {
            foreach (var msg in OldWorkingMemoryContent)
            {
                var taggedMsg = new ChatMessage(msg.Role, msg.Contents.ToList())
                {
                    AdditionalProperties = new() { [AutoWorkingMemoryKey] = true }
                };
                memoryMessages.Add(taggedMsg);
            }
            AppLogger.Log($"[Reducer] 已注入工作记忆（{OldWorkingMemoryContent.Count}条）");
        }

        // ── 6. 确保对话以用户消息开头 ──
        if (conversationMessages.FirstOrDefault()?.Role != ChatRole.User)
            conversationMessages.Insert(0, new ChatMessage(ChatRole.User, UserInput)
            {
                AdditionalProperties = new() { [AutoMemoryKey] = true }
            });

        LastMessages = conversationMessages;
        IEnumerable<ChatMessage> result = [.. systemMessages, .. memoryMessages, .. LastMessages];
        return result;
    }

    private string ConvertMessagesToText(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.User)
            {
                var text = string.Join("", msg.Contents.OfType<TextContent>()
                    .Where(t => !string.IsNullOrWhiteSpace(t.Text))
                    .Select(t => t.Text.Trim()));
                if (!string.IsNullOrWhiteSpace(text))
                    sb.Append($"### 用户\n\n{text}\n\n");
                continue;
            }

            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        sb.Append($"### 助手\n\n{text.Text.Trim()}\n\n");
                        break;
                    case FunctionCallContent call:
                        var args = call.Arguments is not null
                            ? JsonSerializer.Serialize(call.Arguments)
                            : "";
                        sb.Append($"#### 工具调用: {call.Name}\n\n参数: `{args}`\n\n");
                        break;
                    case FunctionResultContent result:
                        var resultText = result.Result?.ToString() ?? "";
                        sb.Append($"<details>\n<summary>执行结果</summary>\n\n```\n{resultText}\n```\n\n</details>\n\n");
                        break;
                }
            }
        }
        return sb.ToString();
    }

    private async Task SaveHistoryFileAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            var historyDir = _agentContext.GetSessionHistoryDirPath();
            if (!Directory.Exists(historyDir))
                Directory.CreateDirectory(historyDir);

            var fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.md";
            var filePath = Path.Combine(historyDir, fileName);

            var sb = new StringBuilder();
            sb.Append($"# 对话历史 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n");
            sb.Append(ConvertMessagesToText(messages));

            await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
            AppLogger.Log($"[Archive] 已保存历史文件: {fileName}（{messages.Count} 条消息）");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Archive] 历史文件保存失败: {ex.Message}");
        }
    }

    private void SaveWorkingMemory()
    {
        try
        {
            var content = JsonSerializer.Serialize(WorkingMemoryBuffer);
            File.WriteAllText(_agentContext.GetSessionWorkingMemoryFilePath(), content);
            AppLogger.Log($"[Reducer] 已保存工作记忆（{WorkingMemoryBuffer.Count}条）");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Reducer] 工作记忆保存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 向对话列表中注入一条假的 CommandCat 工具调用和结果
    /// </summary>
    /// <param name="messages">当前对话的消息列表</param>
    /// <param name="filePath">假装读取的文件路径</param>
    /// <param name="fileContent">你已经提前在后台读取好的文件内容（建议自带行号，与真实工具保持一致）</param>
    /// <param name="startLine">起始行号</param>
    /// <param name="endLine">结束行号</param>
    public static void InjectFakeReadFile(
        List<ChatMessage> messages,
        string filePath,
        string fileContent,
        string messageKey,
        int startLine = 1,
        int endLine = -1)
    {
        var callId = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 10);

        var assistantMessage = new ChatMessage(ChatRole.Assistant, [
            new TextContent($"正在读取文件 {Path.GetFileName(filePath)} 的内容...（行 {startLine} 到 {endLine}）"),
            new FunctionCallContent(
                callId: callId,
                name: "ReadFile",
                arguments: new Dictionary<string, object?>
                {
                    { "filePath", filePath }
                }
            )
        ])
        {
            AdditionalProperties = new()
            {
                { messageKey, true }
            }
        };
        var toolMessage = new ChatMessage(ChatRole.Tool, [
            new FunctionResultContent(
                    callId: callId,
                    result: $"--- File: {Path.GetFileName(filePath)} (Reading from line {startLine}) ---\n" +
                            $"{fileContent}\n" +
                            $"--- End of Read ---"
                )
            ])
        {
            AdditionalProperties = new()
            {
                { messageKey, true }
            }
        };

        messages.Add(new ChatMessage(ChatRole.User, "查询最近记忆")
        {
            AdditionalProperties = new() { [messageKey] = true }
        });
        messages.Add(assistantMessage);
        messages.Add(toolMessage);
    }

    private void InjectMemories(
        List<ChatMessage> systemMessages,
        List<ChatMessage> memoryMessages,
        ArchiveResult? archiveResult)
    {
        string? recentMemory;
        string? primaryMemory;

        if (archiveResult is not null)
        {
            recentMemory = archiveResult.RecentMemory;
            primaryMemory = archiveResult.PrimaryMemory;
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
            InjectFakeReadFile(memoryMessages, _agentContext.GetSessionPrimaryMemoryFilePath(), primaryMemory, AutoPrimaryMemoryKey);
            AppLogger.Log($"[Reducer] 已注入核心记忆（{primaryMemory.Length}字）");
        }

        if (!string.IsNullOrWhiteSpace(recentMemory))
        {
            InjectFakeReadFile(memoryMessages, _agentContext.GetSessionRecentMemoryFilePath(), recentMemory, AutoRecentMemoryKey);
            AppLogger.Log($"[Reducer] 已注入近期记忆（{recentMemory.Length}字）");
        }
    }
}
