using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

using sharpclaw.Memory;
using sharpclaw.UI;

namespace sharpclaw.Agents;

/// <summary>
/// 主动记忆助手：每轮对话后，先检索相关记忆，再通过工具调用决定保存/更新/删除。
/// 工具支持正则模板：content 中用 {0},{1}... 占位，patterns 数组提供正则从原文提取内容填充。
/// </summary>
public class MemorySaver
{
    private static readonly string AgentPrompt = """
        你是一个记忆管理助手。分析对话内容，结合已有记忆，决定如何管理记忆库。

        流程：
        1. 查看最近对话内容和当前记忆库状态
        2. 查看系统检索到的相关已有记忆
        3. 参考核心记忆（长期重要信息）和近期记忆（最近对话摘要），了解已有上下文
        4. 重点检查：对话中是否有重要信息尚未保存到记忆库？
        5. 使用工具执行操作：保存新记忆 / 更新已有记忆 / 删除过时记忆

        记忆体系说明：
        - 记忆库（向量记忆）：通过语义检索的细粒度记忆条目，由你管理（保存/更新/删除）
        - 核心记忆：持久化的长期重要信息摘要（如用户偏好、项目背景、关键决策），由归档系统自动维护
        - 近期记忆：最近被裁剪对话的详细摘要，由归档系统自动维护
        - 核心记忆和近期记忆仅供参考，帮助你判断哪些信息已有上下文覆盖、哪些需要额外保存到记忆库

        重要：
        - "最近对话内容"只是聊天记录，不代表这些内容已经保存到记忆库中
        - 只有"相关已有记忆"部分列出的才是记忆库中实际存在的记忆
        - 判断是否需要保存时，以"相关已有记忆"为准，不要因为对话中提到过就认为已经记住了
        - 核心记忆和近期记忆是滑动的临时摘要，会随对话推进被覆盖或巩固，不能替代记忆库的持久存储。如果对话中有重要信息，即使核心记忆或近期记忆中已提及，也应保存到记忆库
        - 相关已有记忆中已存在且准确的信息无需重复保存，只有当其内容与最近对话、核心记忆或近期记忆存在差异时才需要更新
        - 对话窗口有大小限制，较早的对话会被裁剪丢失。如果对话中有重要但尚未保存的信息，应尽快保存，否则窗口滑动后这些信息将永久丢失

        值得记忆的信息类型：
        - fact: 事实（姓名、职业、项目信息等）
        - preference: 偏好（喜好、习惯、风格等）
        - decision: 决策或结论
        - todo: 待办事项、计划
        - lesson: 经验教训、技术要点

        工具的 content 参数支持两种写法：
        1. 直接写完整内容，patterns 传空数组：content="用户喜欢xx", patterns=[]
        2. 模板 + 正则提取：content 中用 {0},{1},... 作为占位符，patterns 数组中对应位置的正则表达式会从对话原文中提取第一个捕获组的内容填入
           例如：content="用户的名字叫{0}", patterns=["我叫(\\S+)"]
           系统会用正则在对话原文中匹配，将捕获组的值替换 {0}
           这样可以精确引用原文内容，避免转述错误

        注意：
        - 优先保存对话中尚未存入记忆库的重要信息，避免窗口裁剪后丢失
        - 如果已有记忆中有相关但过时的信息，用更新工具而非重复保存
        - 如果已有记忆完全准确且无需变更，不要重复操作
        - 如果对话中没有值得记忆的信息，不调用任何工具
        - 记忆内容应该是独立的、自包含的，脱离对话上下文也能理解
        - 每次最多操作 3 条记忆
        """;

    private readonly IChatClient _client;
    private readonly IMemoryStore _memoryStore;

    public int SearchCount { get; set; } = 10;

    public MemorySaver(IChatClient baseClient, IMemoryStore memoryStore)
    {
        _client = new ChatClientBuilder(baseClient)
            .UseFunctionInvocation()
            .Build();
        _memoryStore = memoryStore;
    }

    public async Task SaveAsync(
        IReadOnlyList<ChatMessage> history,
        string latestUserInput,
        string? recentMemory = null,
        string? primaryMemory = null,
        CancellationToken cancellationToken = default)
    {
        if (history.Count == 0)
            return;

        AppLogger.SetStatus("记忆保存中...");
        // 格式化完整对话原文，供正则提取和提示构建
        var fullText = ConversationArchiver.FormatMessages(history).ToString();

        var relatedMemories = await _memoryStore.SearchAsync(
            latestUserInput, SearchCount, cancellationToken);

        // ── 定义工具 ──

        [Description("保存一条新的记忆到记忆库。content 支持 {0},{1},... 占位符，配合 patterns 正则数组从对话原文中提取内容填充。")]
        async Task<string> SaveMemory(
            [Description("记忆内容模板。可直接写完整内容，或用 {0},{1},... 占位符配合 patterns 提取原文")] string content,
            [Description("类别：fact/preference/decision/todo/lesson")] string category,
            [Description("重要度 1-10")] int importance,
            [Description("关键词列表")] string[] keywords,
            [Description("正则表达式数组，按顺序对应 {0},{1},... 占位符。每个正则需包含一个捕获组。不需要提取时传空数组")] string[] patterns)
        {
            var resolvedContent = ResolveTemplate(content, patterns, fullText);
            if (resolvedContent is null)
                return $"正则匹配失败，未能提取内容";

            var entry = new MemoryEntry
            {
                Content = resolvedContent,
                Category = category,
                Importance = Math.Clamp(importance, 1, 10),
                Keywords = keywords.ToList()
            };
            await _memoryStore.AddAsync(entry, cancellationToken);
            AppLogger.Log($"[AutoSave] 新增: [{category}](重要度:{importance}) {resolvedContent}");
            return $"已保存: {resolvedContent}";
        }

        [Description("更新记忆库中已有的一条记忆。content 支持 {0},{1},... 占位符，配合 patterns 正则数组从对话原文中提取内容填充。")]
        async Task<string> UpdateMemory(
            [Description("要更新的记忆 ID")] string id,
            [Description("新的记忆内容模板。可直接写完整内容，或用 {0},{1},... 占位符配合 patterns 提取原文")] string content,
            [Description("类别：fact/preference/decision/todo/lesson")] string category,
            [Description("重要度 1-10")] int importance,
            [Description("关键词列表")] string[] keywords,
            [Description("正则表达式数组，按顺序对应 {0},{1},... 占位符。每个正则需包含一个捕获组。不需要提取时传空数组")] string[] patterns)
        {
            var resolvedContent = ResolveTemplate(content, patterns, fullText);
            if (resolvedContent is null)
                return $"正则匹配失败，未能提取内容";

            var entry = new MemoryEntry
            {
                Id = id,
                Content = resolvedContent,
                Category = category,
                Importance = Math.Clamp(importance, 1, 10),
                Keywords = keywords.ToList()
            };
            await _memoryStore.UpdateAsync(entry, cancellationToken);
            AppLogger.Log($"[AutoSave] 更新 {id}: [{category}](重要度:{importance}) {resolvedContent}");
            return $"已更新: {resolvedContent}";
        }

        [Description("从记忆库中删除一条过时的记忆")]
        async Task<string> RemoveMemory(
            [Description("要删除的记忆 ID")] string id)
        {
            await _memoryStore.RemoveAsync(id, cancellationToken);
            AppLogger.Log($"[AutoSave] 删除: {id}");
            return $"已删除: {id}";
        }

        // ── 构建输入 ──

        var memoryCount = await _memoryStore.CountAsync(cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"## 记忆库状态：已存 {memoryCount} 条，对话窗口剩余 {history.Count} 条记录");
        sb.AppendLine();
        sb.AppendLine("## 最近对话内容");
        sb.Append(fullText);
        sb.AppendLine();

        if (relatedMemories.Count > 0)
        {
            sb.AppendLine("## 相关已有记忆");
            for (var i = 0; i < relatedMemories.Count; i++)
            {
                var m = relatedMemories[i];
                sb.AppendLine($"[{i + 1}] ID={m.Id} [{m.Category}](重要度:{m.Importance}) {m.Content}");
            }
        }
        else
        {
            sb.AppendLine("## 无相关已有记忆");
        }

        if (!string.IsNullOrWhiteSpace(primaryMemory))
        {
            sb.AppendLine();
            sb.AppendLine("## 核心记忆（长期重要信息）");
            sb.AppendLine(primaryMemory);
        }

        if (!string.IsNullOrWhiteSpace(recentMemory))
        {
            sb.AppendLine();
            sb.AppendLine("## 近期记忆（最近对话摘要）");
            sb.AppendLine(recentMemory);
        }

        var options = new ChatOptions
        {
            Instructions = AgentPrompt,
            Tools =
            [
                AIFunctionFactory.Create(SaveMemory),
                AIFunctionFactory.Create(UpdateMemory),
                AIFunctionFactory.Create(RemoveMemory),
            ]
        };

        var agent = _client.AsBuilder().UseFunctionInvocation().BuildAIAgent(new Microsoft.Agents.AI.ChatClientAgentOptions()
        {
            ChatOptions = options
        });

        var ret = await agent.RunAsync(new ChatMessage(ChatRole.User, sb.ToString()), cancellationToken: cancellationToken);
        AppLogger.Log($"[AutoSave] 完成: {ret.Text}");
    }

    /// <summary>
    /// 将模板中的 {0},{1},... 占位符用正则从原文中提取的捕获组替换。
    /// patterns 为空时直接返回 content。任一正则匹配失败返回 null。
    /// </summary>
    private static string? ResolveTemplate(string content, string[] patterns, string fullText)
    {
        if (patterns.Length == 0)
            return content;

        var values = new string[patterns.Length];
        for (var i = 0; i < patterns.Length; i++)
        {
            try
            {
                var match = Regex.Match(fullText, patterns[i]);
                if (!match.Success || match.Groups.Count < 2)
                {
                    AppLogger.Log($"[AutoSave] 正则 [{i}] 匹配失败: {patterns[i]}");
                    return null;
                }
                values[i] = match.Groups[1].Value;
            }
            catch (RegexParseException ex)
            {
                AppLogger.Log($"[AutoSave] 正则 [{i}] 解析错误: {ex.Message}");
                return null;
            }
        }

        try
        {
            return string.Format(content, values);
        }
        catch (FormatException)
        {
            // 占位符数量与 values 不匹配，回退直接返回模板
            return content;
        }
    }
}
