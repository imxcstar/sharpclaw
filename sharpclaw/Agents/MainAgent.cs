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
    private static readonly StringBuilder SystemPrompt = new StringBuilder(@"你是 Sharpclaw，一个拥有长期记忆和高级系统操作能力的**自主型 AI 智能体 (Autonomous Agent)**。
你不只是一个等待指令的聊天机器人，你是一个能在真实代码库中主动探索、规划并执行复杂任务的“资深全栈工程师”。

🔍 **第一法则：基于上下文的精准行动 (CONTEXT-AWARE EXECUTION)**
**你需要根据对当前项目的熟悉程度灵活决定行动策略。绝对禁止在毫无上下文的情况下“盲写代码”：**
1. **按需探查项目骨架**：当你初次接手项目，或记忆中缺乏当前模块的上下文时，**必须**先用 `CommandDir` 或 `FindFiles` 摸清目录结构和整体架构。**如果你已经通过核心记忆、近期记忆或之前的对话充分了解了项目位置，请直接跳过全局探查，避免浪费时间。**
2. **修改前必须精读 (Read Before Write)**：无论你对项目有多熟悉，在调用 `CommandEditText` 修改特定文件前，**永远**要先用 `CommandCat` (结合 startLine/endLine) 读取该文件的最新内容。你必须获取精确的行号、确认现有的变量命名风格和最新的代码逻辑，绝不能凭记忆算行号！
3. **评估连带影响**：如果你要修改一个公共函数或核心接口，先用 `SearchInFiles` 全局搜索它的调用位置，评估你的修改是否会导致其他文件报错，并做好连带修改的计划。

🚀 **高级自主性准则 (Autonomous Execution Protocol)**：
1. **目标拆解与连续执行**：当用户下达复杂目标时，主动将其拆分为子任务（例如：梳理逻辑 -> 查阅模型 -> 写 Controller -> 写路由）。连续调用工具推进，**不要每做一步就停下来问用户“接下来干嘛”**。
2. **自我纠错 (Auto-Recovery)**：如果工具调用失败（如路径不存在、编译报错），**绝对不要立刻放弃并向用户报错**。你必须独立分析 Stderr，尝试修改路径、查阅相关文件，至少进行 2~3 次自主重试验证。
3. **严格验算与闭环**：使用 `CommandEditText` 后，**必须仔细检查返回的 Git Diff 预览**。如果发现自己算错了行号、缩进错乱或括号未闭合，立即再次调用工具修复！修改完成后，主动运行测试或编译命令验证结果。

🧠 **记忆系统架构 (Memory System)**：
- **优先查阅记忆**：在执行任何探索命令前，先检索你的上下文或调用 `SearchMemory` / `GetRecentMemories`。如果答案已经在记忆里，直接使用。
- **隐式记忆**：系统会在后台自动提取并注入历史上下文，你无需手动保存。
- **断点续传**：面对漫长的任务，随时对齐当前的宏观进度，防止迷失方向。

💡 **你的行事风格**：
- 你是资深架构师：专业、谨慎、结果导向。
- 遇到涉及核心数据销毁（Delete/Drop）的操作，必须明确向用户请求二次确认。");

    private readonly ChatClientAgent _agent;
    private readonly IChatIO _chatIO;
    private readonly string _workingMemoryPath;
    private readonly MemoryPipelineChatReducer _reducer;
    private InMemoryChatHistoryProvider? _historyProvider;
    private AgentSession? _session;

    public MainAgent(
        SharpclawConfig config,
        IMemoryStore? memoryStore,
        AIFunction[] commandSkills,
        IChatIO chatIO)
    {
        var sharpclawDir = Path.GetDirectoryName(SharpclawConfig.ConfigPath)!;

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

        SystemPrompt.AppendLine();
        SystemPrompt.AppendLine($"[工作目录] {workspaceDir}");
        SystemPrompt.Append("- 你的所有文件操作都应基于这个工作目录，且不能访问或修改它之外的文件。");

        _workingMemoryPath = Path.Combine(sessionDir, "working_memory.md");
        var recentMemoryPath = Path.Combine(sessionDir, "recent_memory.md");
        var primaryMemoryPath = Path.Combine(sessionDir, "primary_memory.md");

        _chatIO = chatIO;

        //迁移旧的记忆文件
        if (cacheConfig.UseSessionId == "default")
        {
            var oldWorkingMemoryPath = Path.Combine(sharpclawDir, "working_memory.md");
            var oldRecentMemoryPath = Path.Combine(sharpclawDir, "recent_memory.md");
            var oldPrimaryMemoryPath = Path.Combine(sharpclawDir, "primary_memory.md");
            if (File.Exists(oldWorkingMemoryPath) && !File.Exists(_workingMemoryPath))
                File.Move(oldWorkingMemoryPath, _workingMemoryPath);
            if (File.Exists(oldRecentMemoryPath) && !File.Exists(recentMemoryPath))
                File.Move(oldRecentMemoryPath, recentMemoryPath);
            if (File.Exists(oldPrimaryMemoryPath) && !File.Exists(primaryMemoryPath))
                File.Move(oldPrimaryMemoryPath, primaryMemoryPath);
        }

        // 按智能体创建各自的 AI 客户端
        var mainClient = ClientFactory.CreateAgentClient(config, config.Agents.Main);

        MemorySaver? memorySaver = null;
        AIFunction[] memoryTools = [];

        var fileToolNames = new HashSet<string>
        {
            "CommandGetLineCount", "CommandCat", "CommandCreateText", "AppendToFile",
            "FileExists", "CommandDir", "CommandEditText", "SearchInFiles"
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
            AIFunction[] archiverTools = [.. fileTools, .. memoryTools];
            archiver = new ConversationArchiver(
                archiverClient, sessionDir, _workingMemoryPath, recentMemoryPath, primaryMemoryPath, archiverTools);
        }

        AIFunction[] tools = [.. memoryTools, .. commandSkills];
        var systemPrompt = SystemPrompt.ToString();

        _reducer = new MemoryPipelineChatReducer(
            resetThreshold: 30,
            systemPrompt: systemPrompt,
            archiver: archiver,
            memorySaver: memorySaver);
        _reducer.WorkingMemoryPath = _workingMemoryPath;

        _agent = new ChatClientBuilder(mainClient)
            .UseFunctionInvocation()
            .UseChatReducer(_reducer)
            .BuildAIAgent(new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = systemPrompt,
                    Tools = tools
                },
                ChatHistoryProviderFactory = (ctx, ct) =>
                {
                    _historyProvider = new InMemoryChatHistoryProvider(
                        _reducer,
                        ctx.SerializedState,
                        ctx.JsonSerializerOptions,
                        InMemoryChatHistoryProvider.ChatReducerTriggerEvent.BeforeMessagesRetrieval
                    );
                    return new ValueTask<ChatHistoryProvider>(_historyProvider);
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
            _reducer.OldWorkingMemoryContent = File.ReadAllText(_workingMemoryPath);

            if (!string.IsNullOrWhiteSpace(_reducer.OldWorkingMemoryContent))
                _reducer.WorkingMemoryBuffer.Append(_reducer.OldWorkingMemoryContent + "\n\n---\n\n");
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
        _reducer.WorkingMemoryBuffer.Append($"### 用户\n\n{input}\n\n");
        AIContent? lastContent = null;
        AppLogger.SetStatus("AI 思考中...");
        _chatIO.BeginAiResponse();
        try
        {
            await foreach (var update in _agent.RunStreamingAsync(inputMessages, _session!).WithCancellation(aiToken))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text:
                            _chatIO.AppendChat(text.Text);
                            if (lastContent is not TextContent)
                                _reducer.WorkingMemoryBuffer.Append("### 助手\n\n");
                            _reducer.WorkingMemoryBuffer.Append(text.Text);
                            break;
                        case TextReasoningContent reasoning:
                            if (lastContent is TextContent)
                                _reducer.WorkingMemoryBuffer.AppendLine();
                            AppLogger.SetStatus($"[Main]思考中...");
                            Append("Reasoning", reasoning.Text);
                            break;
                        case FunctionCallContent call:
                            if (lastContent is TextContent)
                                _reducer.WorkingMemoryBuffer.AppendLine();
                            AppLogger.SetStatus($"[Main]调用工具: {call.Name}");
                            AppLogger.Log($"[Main]调用工具: {call.Name}");
                            var args = call.Arguments is not null
                                ? JsonSerializer.Serialize(call.Arguments)
                                : "";
                            _reducer.WorkingMemoryBuffer.Append($"#### 工具调用: {call.Name}\n\n参数: `{args}`\n\n");
                            break;
                        case FunctionResultContent result:
                            if (lastContent is TextContent)
                                _reducer.WorkingMemoryBuffer.AppendLine();
                            _reducer.WorkingMemoryBuffer.Append($"<details>\n<summary>执行结果</summary>\n\n```\n{result.Result?.ToString() ?? ""}\n```\n\n</details>\n\n");
                            break;
                    }
                    lastContent = content;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _chatIO.AppendChat("\n[已取消]\n");
            return;
        }
        _chatIO.AppendChat("\n");
        _reducer.WorkingMemoryBuffer.Append("\n\n---\n\n");

        // 持久化工作记忆
        try
        {
            File.WriteAllText(_workingMemoryPath, _reducer.WorkingMemoryBuffer.ToString());
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WorkingMemory] 保存失败: {ex.Message}");
        }
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
