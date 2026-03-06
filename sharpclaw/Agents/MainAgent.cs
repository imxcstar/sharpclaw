using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using sharpclaw.Abstractions;
using sharpclaw.Chat;
using sharpclaw.Core;
using sharpclaw.Memory;
using sharpclaw.UI;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;

namespace sharpclaw.Agents;

/// <summary>
/// 主智能体：集成记忆管线（保存、回忆、总结、主要记忆）和命令工具，通过 ChatWindow 进行 I/O。
/// </summary>
public class MainAgent
{
    private static readonly StringBuilder SystemPrompt = new StringBuilder(@"你是 Sharpclaw，一个拥有长期记忆、系统操作能力和丰富知识的**通用型 AI 助手**。
你既能与用户自然地聊天、解答问题、提供建议，也能在需要时深入操作系统执行复杂任务——从文件管理、信息检索到代码编写，无所不能。

🎯 **核心定位：智能通用助手**
- **日常对话**：友好、自然、有温度地与用户交流。回答知识问题、提供建议、进行头脑风暴、翻译、写作等。
- **任务执行**：当用户需要你实际操作时（编辑文件、搜索内容、运行命令等），切换为高效的执行模式，利用你的工具链完成任务。
- **灵活切换**：根据用户意图自动判断是该聊天还是该动手。不要对简单的问候或闲聊使用工具。

🔍 **操作准则 (When Taking Actions)**
当你需要操作文件或执行系统命令时：
1. **先了解再行动**：对不熟悉的目录或文件，先用 `CommandDir` 或 `GlobFiles` 探查结构。如果记忆中已有足够信息，直接行动。
2. **读后再改**：修改文件前，先用 `ReadFile` 读取最新内容，从中精确复制要修改的文本作为 `EditByMatch` 的 oldString。
3. **评估影响**：修改公共接口或关键文件前，用 `Grep` 搜索相关引用，评估连带影响。
4. **技能自省**：用户询问你的能力时，查阅实际的工具列表如实汇报，不虚构不存在的功能。

🚀 **自主执行准则 (Autonomous Execution)**
1. **目标拆解**：复杂任务主动拆分为子步骤，连续推进，不要每步都停下来问用户。
2. **自我纠错**：工具调用失败时，独立分析原因并重试 2~3 次，而非立即报错。
3. **验证闭环**：编辑文件后检查 Diff 预览，发现问题立即修复。

🧠 **记忆系统**
- **优先查阅记忆**：行动前先检索上下文或调用 `SearchMemory` / `GetRecentMemories`，避免重复探索。
- **隐式记忆**：系统在后台自动提取并注入历史上下文，你无需手动保存。
- **断点续传**：长任务中随时对齐宏观进度，防止迷失方向。

💡 **行事风格**
- 专业、友好、高效。对话时像一个博学的朋友，执行时像一个严谨的专家。
- 涉及核心数据销毁（Delete/Drop）的操作，必须向用户请求二次确认。
- 回答问题时简洁明了，避免不必要的冗长。");

    private readonly ChatClientAgent _agent;
    private readonly IChatIO _chatIO;
    private readonly string _workingMemoryPath;
    private readonly MemoryPipelineChatReducer _reducer;
    private readonly IAgentContext _agentContext;
    private AgentSession? _session;

    public MainAgent(
        SharpclawConfig config,
        IMemoryStore? memoryStore,
        AITool[] commandSkills,
        IChatIO chatIO,
        IAgentContext agentContext)
    {
        var sharpclawDir = SharpclawConfig.SharpclawDir;

        var cacheConfigPath = Path.Combine(sharpclawDir, "cache_config.json");
        SharpclawCacheConfig? cacheConfig = null;
        if (File.Exists(cacheConfigPath))
            cacheConfig = JsonSerializer.Deserialize<SharpclawCacheConfig>(File.ReadAllText(cacheConfigPath));
        if (cacheConfig == null)
            cacheConfig = new SharpclawCacheConfig();
        File.WriteAllText(cacheConfigPath, JsonSerializer.Serialize(cacheConfig, new JsonSerializerOptions { WriteIndented = true }));

        var sessionDir = Path.Combine(sharpclawDir, "sessions", cacheConfig.UseSessionId);
        if (!Directory.Exists(sessionDir))
            Directory.CreateDirectory(sessionDir);

        var workspaceDir = Path.Combine(sessionDir, "workspace");
        if (!Directory.Exists(workspaceDir))
            Directory.CreateDirectory(workspaceDir);

        _agentContext = agentContext;
        _agentContext.SetWorkspaceDirPath(workspaceDir);
        _agentContext.SetSessionDirPath(sessionDir);

        SystemPrompt.AppendLine();
        SystemPrompt.AppendLine($"[工作目录] {workspaceDir}");
        SystemPrompt.Append("- 你的所有文件操作都应基于这个工作目录，且不能访问或修改它之外的文件。");

        _workingMemoryPath = _agentContext.GetSessionWorkingMemoryFilePath();
        var recentMemoryPath = _agentContext.GetSessionRecentMemoryFilePath();
        var primaryMemoryPath = _agentContext.GetSessionPrimaryMemoryFilePath();

        _chatIO = chatIO;

        // 按智能体创建各自的 AI 客户端
        var mainClient = ClientFactory.CreateAgentClient(config, config.Agents.Main);

        MemorySaver? memorySaver = null;
        AIFunction[] memoryTools = [];

        var fileToolNames = new HashSet<string>
        {
            "CommandGetLineCount", "ReadFile", "WriteFile", "AppendToFile",
            "FileExists", "CommandDir", "EditByMatch", "Grep", "GlobFiles"
        };
        var fileTools = commandSkills.Where(t => fileToolNames.Contains(t.Name)).ToArray();

        if (memoryStore is not null)
        {
            if (config.Agents.Saver.Enabled)
            {
                var saverClient = ClientFactory.CreateAgentClient(config, config.Agents.Saver);
                memorySaver = new MemorySaver(saverClient, memoryStore,
                    _workingMemoryPath, recentMemoryPath, primaryMemoryPath, fileTools);
            }

            memoryTools = CreateMemoryTools(memoryStore);
        }

        ConversationArchiver? archiver = null;
        if (config.Agents.Summarizer.Enabled)
        {
            var archiverClient = ClientFactory.CreateAgentClient(config, config.Agents.Summarizer);
            archiver = new ConversationArchiver(
                archiverClient, sessionDir, _workingMemoryPath, recentMemoryPath, primaryMemoryPath);
        }

        AITool[] tools = [.. memoryTools, .. commandSkills];
        var systemPrompt = SystemPrompt.ToString();

        _reducer = new MemoryPipelineChatReducer(
            agentContext,
            resetThreshold: 30,
            systemPrompt: systemPrompt,
            archiver: archiver,
            memorySaver: memorySaver);

        _agent = new ChatClientBuilder(mainClient)
            .UseFunctionInvocation()
            .UseChatReducer(_reducer)
            .BuildAIAgent(new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = systemPrompt,
                    Tools = tools
                }
            });
    }

    /// <summary>
    /// 启动对话循环：等待 ChatWindow 输入 → 处理 → 输出。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _chatIO.WaitForReadyAsync();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var input = await _chatIO.ReadInputAsync(cancellationToken);
                if (string.IsNullOrEmpty(input))
                    continue;

                var cmdResult = await _chatIO.HandleCommandAsync(input);
                if (cmdResult == CommandResult.Exit)
                    break;
                if (cmdResult == CommandResult.Handled)
                    continue;

                await ProcessTurnAsync(input, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Error] {ex.Message}");
            }
        }
    }

    private async Task ProcessTurnAsync(string input, CancellationToken cancellationToken)
    {
        _chatIO.EchoUserInput(input);
        _chatIO.ShowRunning();

        _session = await _agent.CreateSessionAsync();
        _reducer.WorkingMemoryBuffer.Clear();
        if (File.Exists(_workingMemoryPath))
        {
            _reducer.OldWorkingMemoryContent = JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(_workingMemoryPath)) ?? [];

            _reducer.WorkingMemoryBuffer.AddRange(_reducer.OldWorkingMemoryContent);
        }

        using var aiCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _chatIO.GetAiCancellationToken());
        var aiToken = aiCts.Token;

        var inputMessages = new List<ChatMessage>
        {
            new(ChatRole.User, input)
        };

        var buffer = new StringBuilder();
        string? bufferType = null;
        void Flush()
        {
            if (buffer.Length == 0) return;
            AppLogger.Log($"[Main]: {buffer}");
            buffer.Clear();
            bufferType = null;
        }

        void Append(string type, string text)
        {
            if (bufferType != type)
                Flush();
            bufferType = type;
            buffer.Append(text);
        }

        // 流式输出
        _reducer.UserInput = input;
        _reducer.WorkingMemoryBuffer.Add(new ChatMessage(ChatRole.User, input));
        var turnStartIndex = _reducer.WorkingMemoryBuffer.Count; // 当前轮次流式消息的起始索引
        AIContent? lastContent = null;
        AppLogger.SetStatus("AI 思考中...");
        _chatIO.BeginAiResponse();
        var message = new ChatMessage { Role = ChatRole.Assistant };
        bool cancelled = false;
        try
        {
            await foreach (var update in _agent.RunStreamingAsync(inputMessages, _session!).WithCancellation(aiToken))
            {
                foreach (var content in update.Contents)
                {
                    // 根据内容类型决定目标角色：FunctionResultContent → Tool，其余 → Assistant
                    var expectedRole = content is FunctionResultContent
                        ? ChatRole.Tool : ChatRole.Assistant;

                    if (message.Contents.Count > 0 && message.Role != expectedRole)
                    {
                        // 角色切换：保存当前消息，开始新消息
                        _reducer.WorkingMemoryBuffer.Add(message);
                        message = new ChatMessage { Role = expectedRole };
                    }
                    else if (message.Contents.Count == 0)
                    {
                        message.Role = expectedRole;
                    }

                    switch (content)
                    {
                        case TextContent text:
                            var ltext = message.Contents.LastOrDefault() as TextContent;
                            if (ltext == null)
                            {
                                ltext = new TextContent("");
                                message.Contents.Add(ltext);
                            }
                            ltext.Text += text.Text;
                            _chatIO.AppendChat(text.Text);
                            break;
                        case TextReasoningContent reasoning:
                            var lreasoning = message.Contents.LastOrDefault() as TextReasoningContent;
                            if (lreasoning == null)
                            {
                                lreasoning = new TextReasoningContent("");
                                message.Contents.Add(lreasoning);
                            }
                            lreasoning.Text += reasoning.Text;
                            AppLogger.SetStatus($"[Main]思考中...");
                            Append("Reasoning", reasoning.Text);
                            break;
                        case FunctionCallContent call:
                            message.Contents.Add(call);
                            AppLogger.SetStatus($"[Main]调用工具: {call.Name}");
                            AppLogger.Log($"[Main]调用工具: {call.Name}");
                            break;
                        case FunctionResultContent functionResult:
                            message.Contents.Add(functionResult);
                            break;
                    }
                    lastContent = content;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _chatIO.AppendChat("\n[已取消]\n");
        }

        // 将最后一条累积的消息加入工作记忆
        if (message.Contents.Count > 0)
            _reducer.WorkingMemoryBuffer.Add(message);

        // 检查当前轮次中未返回结果的工具调用，补充取消标记
        var turnMessages = _reducer.WorkingMemoryBuffer.Skip(turnStartIndex);

        var allCallIds = turnMessages
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToHashSet();

        var answeredCallIds = turnMessages
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.CallId)
            .ToHashSet();

        allCallIds.ExceptWith(answeredCallIds);
        if (allCallIds.Count > 0)
        {
            var unmatchedCalls = turnMessages
                .Where(m => m.Role == ChatRole.Assistant)
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .Where(c => allCallIds.Contains(c.CallId))
                .ToList();

            _reducer.WorkingMemoryBuffer.Add(new ChatMessage(ChatRole.Tool,
                unmatchedCalls.Select(call => (AIContent)new FunctionResultContent(
                    callId: call.CallId,
                    result: "[已取消]"
                )).ToList()));
        }

        if (!cancelled)
            _chatIO.AppendChat("\n");

        // 持久化工作记忆
        try
        {
            File.WriteAllText(_workingMemoryPath, JsonSerializer.Serialize(_reducer.WorkingMemoryBuffer));
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WorkingMemory] 保存失败: {ex.Message}");
        }

        _chatIO.ShowStop();
    }

    private static AIFunction[] CreateMemoryTools(IMemoryStore memoryStore)
    {
        [Description("搜索长期记忆库，查找与查询相关的记忆。当用户提到之前讨论过的话题、或你需要回顾历史信息时使用。")]
        async Task<string> SearchMemory(
            [Description("搜索关键词或语义查询")] string query,
            [Description("最多返回几条结果")] int count = 10)
        {
            var results = await memoryStore.SearchAsync(query, count);
            if (results.Count == 0)
                return "没有找到相关记忆。";

            var sb = new StringBuilder();
            sb.AppendLine($"找到 {results.Count} 条相关记忆：");
            foreach (var m in results)
                sb.AppendLine($"- [{m.Category}](重要度:{m.Importance}, {FormatAge(m.CreatedAt)}) {m.Content}");
            return sb.ToString();
        }

        [Description("查看最近保存的记忆。当需要浏览记忆库内容但没有明确搜索词时使用。")]
        async Task<string> GetRecentMemories(
            [Description("返回最近几条记忆")] int count = 10)
        {
            var results = await memoryStore.GetRecentAsync(count);
            if (results.Count == 0)
                return "记忆库为空。";

            var sb = new StringBuilder();
            sb.AppendLine($"最近 {results.Count} 条记忆：");
            foreach (var m in results)
                sb.AppendLine($"- [{m.Category}](重要度:{m.Importance}, {FormatAge(m.CreatedAt)}) {m.Content}");
            return sb.ToString();
        }

        return
        [
            AIFunctionFactory.Create(SearchMemory),
            AIFunctionFactory.Create(GetRecentMemories),
        ];
    }

    private static string FormatAge(DateTimeOffset created)
    {
        var age = DateTimeOffset.UtcNow - created;
        if (age.TotalMinutes < 1) return "刚刚";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}分钟前";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}小时前";
        return $"{(int)age.TotalDays}天前";
    }
}
