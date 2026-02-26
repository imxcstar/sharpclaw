using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

using sharpclaw.Memory;
using sharpclaw.UI;

namespace sharpclaw.Agents;

/// <summary>
/// 自主记忆助手：每轮对话后，通过工具自主查询已有记忆，决定保存/更新/删除。
/// 可访问所有记忆文件（只读参考），仅对向量记忆库有读写权限。
/// </summary>
public class MemorySaver
{
    private readonly IChatClient _client;
    private readonly IMemoryStore _memoryStore;
    private readonly AIFunction[] _fileTools;
    private readonly string _agentPrompt;

    public MemorySaver(
        IChatClient baseClient,
        IMemoryStore memoryStore,
        string workingMemoryPath,
        string recentMemoryPath,
        string primaryMemoryPath,
        AIFunction[] fileTools)
    {
        _client = new ChatClientBuilder(baseClient)
            .UseFunctionInvocation()
            .Build();
        _memoryStore = memoryStore;
        _fileTools = fileTools;

        _agentPrompt = $"""
            你是一个记忆管理助手。分析最近对话内容，自主查询记忆库，决定如何管理记忆。

            ## 可用的记忆源

            | 记忆类型 | 位置 | 权限 |
            |---------|------|------|
            | 工作记忆（上次对话快照） | {workingMemoryPath} | 只读 |
            | 近期记忆（对话摘要） | {recentMemoryPath} | 只读 |
            | 核心记忆（长期重要信息） | {primaryMemoryPath} | 只读 |
            | 向量记忆（细粒度记忆条目） | 通过 SearchMemory / GetRecentMemories / SaveMemory / UpdateMemory / RemoveMemory 管理 | 读写 |

            **重要：你只能通过工具管理向量记忆库，其他记忆文件仅供参考，禁止修改。**

            ## 流程

            1. 阅读下方提供的最近对话内容，识别其中值得记忆的重要信息
            2. 可选：读取其他记忆文件作为参考，了解已有上下文
            3. 如果发现值得记忆的信息，先搜索向量记忆库中是否已有相关记忆
            4. 根据搜索结果决定操作：
               - 记忆库中无相关记忆 → 保存新记忆
               - 记忆库中有相关但过时/不完整的记忆 → 更新
               - 记忆库中有完全过时的记忆 → 删除
               - 记忆库中已有准确的记忆 → 不操作
            5. 如果对话中没有值得记忆的信息，不调用任何工具

            ## 值得记忆的信息类型

            - fact: 事实（姓名、职业、项目信息等）
            - preference: 偏好（喜好、习惯、风格等）
            - decision: 决策或结论
            - todo: 待办事项、计划
            - lesson: 经验教训、技术要点

            ## 注意

            - 保存前务必先搜索向量记忆库，避免库内重复保存
            - 可以多次搜索不同关键词，确保全面检查
            - 有值得记忆的信息时，无论其他记忆文件中是否已有相同或类似内容，都必须保存到向量记忆库
            - 记忆内容应独立、自包含，脱离对话上下文也能理解
            - 每次最多保存/更新/删除共 3 条记忆
            - 关注用户透露的事实、偏好、决策，以及 AI 执行的重要操作和结果
            """;
    }

    public async Task SaveAsync(
        IReadOnlyList<ChatMessage> history,
        string userInput,
        CancellationToken cancellationToken = default)
    {
        if (history.Count == 0)
            return;

        AppLogger.SetStatus("记忆保存中...");
        var fullText = ConversationArchiver.FormatMessages(history).ToString();

        // ── 向量记忆工具 ──

        [Description("搜索向量记忆库，查找与查询相关的已有记忆。保存或更新前应先搜索，避免重复。")]
        async Task<string> SearchMemory(
            [Description("搜索关键词或语义查询")] string query,
            [Description("最多返回几条结果")] int count = 5)
        {
            var results = await _memoryStore.SearchAsync(query, Math.Min(count, 10), cancellationToken);
            if (results.Count == 0)
                return "未找到相关记忆。";

            var sb = new StringBuilder();
            sb.AppendLine($"找到 {results.Count} 条相关记忆：");
            foreach (var m in results)
                sb.AppendLine($"- ID={m.Id} [{m.Category}](重要度:{m.Importance}) {m.Content}");
            return sb.ToString();
        }

        [Description("查看最近保存的向量记忆，了解记忆库近况。")]
        async Task<string> GetRecentMemories(
            [Description("返回最近几条记忆")] int count = 5)
        {
            var results = await _memoryStore.GetRecentAsync(Math.Min(count, 10), cancellationToken);
            if (results.Count == 0)
                return "记忆库为空。";

            var sb = new StringBuilder();
            sb.AppendLine($"最近 {results.Count} 条记忆：");
            foreach (var m in results)
                sb.AppendLine($"- ID={m.Id} [{m.Category}](重要度:{m.Importance}) {m.Content}");
            return sb.ToString();
        }

        [Description("保存一条新的记忆到向量记忆库。")]
        async Task<string> SaveMemory(
            [Description("记忆内容，应独立自包含")] string content,
            [Description("类别：fact/preference/decision/todo/lesson")] string category,
            [Description("重要度 1-10")] int importance,
            [Description("关键词列表")] string[] keywords)
        {
            var entry = new MemoryEntry
            {
                Content = content,
                Category = category,
                Importance = Math.Clamp(importance, 1, 10),
                Keywords = keywords.ToList()
            };
            await _memoryStore.AddAsync(entry, cancellationToken);
            AppLogger.Log($"[AutoSave] 新增: [{category}](重要度:{importance}) {content}");
            return $"已保存: {content}";
        }

        [Description("更新向量记忆库中已有的一条记忆。")]
        async Task<string> UpdateMemory(
            [Description("要更新的记忆 ID")] string id,
            [Description("新的记忆内容")] string content,
            [Description("类别：fact/preference/decision/todo/lesson")] string category,
            [Description("重要度 1-10")] int importance,
            [Description("关键词列表")] string[] keywords)
        {
            var entry = new MemoryEntry
            {
                Id = id,
                Content = content,
                Category = category,
                Importance = Math.Clamp(importance, 1, 10),
                Keywords = keywords.ToList()
            };
            await _memoryStore.UpdateAsync(entry, cancellationToken);
            AppLogger.Log($"[AutoSave] 更新 {id}: [{category}](重要度:{importance}) {content}");
            return $"已更新: {content}";
        }

        [Description("从向量记忆库中删除一条过时的记忆。")]
        async Task<string> RemoveMemory(
            [Description("要删除的记忆 ID")] string id)
        {
            await _memoryStore.RemoveAsync(id, cancellationToken);
            AppLogger.Log($"[AutoSave] 删除: {id}");
            return $"已删除: {id}";
        }

        // ── 构建输入 ──

        var memoryCount = await _memoryStore.CountAsync(cancellationToken);

        var sb2 = new StringBuilder();
        sb2.AppendLine($"## 向量记忆库状态：已存 {memoryCount} 条");
        sb2.AppendLine();
        sb2.AppendLine("## 用户本轮输入");
        sb2.AppendLine(userInput);
        sb2.AppendLine();
        sb2.AppendLine("## 最近对话内容");
        sb2.Append(fullText);

        AIFunction[] vectorTools =
        [
            AIFunctionFactory.Create(SearchMemory),
            AIFunctionFactory.Create(GetRecentMemories),
            AIFunctionFactory.Create(SaveMemory),
            AIFunctionFactory.Create(UpdateMemory),
            AIFunctionFactory.Create(RemoveMemory),
        ];

        var options = new ChatOptions
        {
            Instructions = _agentPrompt,
            Tools = [.. _fileTools, .. vectorTools]
        };

        var agent = _client.AsBuilder().UseFunctionInvocation().BuildAIAgent(new ChatClientAgentOptions()
        {
            ChatOptions = options
        });

        await RunAgentStreamingAsync(agent,
            new ChatMessage(ChatRole.User, sb2.ToString()),
            "MemorySaver", cancellationToken);
    }

    private static async Task RunAgentStreamingAsync(
        ChatClientAgent agent, ChatMessage input, string logPrefix, CancellationToken cancellationToken)
    {
        var session = await agent.CreateSessionAsync();
        await foreach (var update in agent.RunStreamingAsync([input], session).WithCancellation(cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        AppLogger.Log($"[{logPrefix}] {text.Text}");
                        break;
                    case TextReasoningContent reasoning:
                        AppLogger.Log($"[{logPrefix}:Reasoning] {reasoning.Text}");
                        break;
                    case FunctionCallContent call:
                        AppLogger.SetStatus($"{logPrefix}: {call.Name}");
                        AppLogger.Log($"[{logPrefix}:Call] {call.Name}({JsonSerializer.Serialize(call.Arguments)})");
                        break;
                    case FunctionResultContent result:
                        AppLogger.Log($"[{logPrefix}:Result] {JsonSerializer.Serialize(result.Result)}");
                        break;
                }
            }
        }
    }
}
