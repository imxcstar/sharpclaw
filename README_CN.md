# Sharpclaw

[English](README.md)

基于 .NET 10 的 AI 智能体，具备长期记忆能力和系统操作工具。支持 Anthropic Claude、OpenAI、Gemini 多供应商切换，阿里云 DashScope 提供向量嵌入和重排序。

![主聊天窗口](preview/main.png)

## 特性

- **多供应商支持** — Anthropic / OpenAI / Gemini，通过配置文件切换，无需改代码
- **按智能体独立配置** — 每个智能体（主智能体、记忆回忆、记忆保存、对话总结）可单独配置 API 地址、密钥和模型
- **长期记忆** — 自动保存、检索、注入对话中的重要信息，跨会话持久化
- **记忆管线** — 四个独立子智能体协作：主动记忆保存、记忆回忆注入、滑动窗口裁剪、对话摘要
- **向量语义搜索** — 两阶段检索：向量嵌入召回 + 可选重排序
- **语义去重** — 余弦相似度超过阈值时自动合并记忆，避免冗余
- **系统工具** — 文件操作、进程执行（dotnet/node/docker）、HTTP 请求、后台任务管理
- **双前端模式** — TUI 终端界面（Terminal.Gui v2）+ WebSocket Web 界面，共享同一套智能体逻辑
- **斜杠命令** — 输入 `/` 触发自动补全，支持 `/exit`、`/quit` 等快捷指令
- **流式输出** — AI 响应实时流式显示，支持推理过程日志和工具调用追踪
- **配置数据加密** — API Key 等敏感信息使用 AES-256-CBC 加密存储，密钥托管于操作系统凭据管理器（Windows Credential Manager / macOS Keychain / Linux libsecret）

## 环境要求

- .NET 10 SDK

## 快速开始

### TUI 模式（终端界面）

```bash
dotnet run --project sharpclaw
```

首次运行会自动进入配置引导，按提示选择 AI 供应商、填写 API Key，并可按智能体单独配置。

![配置引导](preview/config.png)

配置保存到 `~/.sharpclaw/config.json`。之后启动直接进入交互式对话，输入 `/exit` 或 `/quit` 退出，`Ctrl+Q` 退出程序。

### Web 模式（WebSocket 服务）

```bash
dotnet run --project sharpclaw serve
dotnet run --project sharpclaw serve --port 8080
```

启动后访问 `http://localhost:5000`（默认端口）打开 Web 聊天界面。Web UI 支持 Markdown 渲染、代码高亮、连接状态指示和实时状态显示。

![Web 聊天界面](preview/web.png)

> 注意：Web 模式需要先通过 TUI 模式完成配置。同一时间仅支持单客户端连接。

## 配置

运行配置引导：

```bash
dotnet run --project sharpclaw config
```

配置文件结构（`~/.sharpclaw/config.json`）：

```json
{
  "version": 4,
  "default": {
    "provider": "anthropic",
    "endpoint": "https://api.anthropic.com",
    "apiKey": "sk-xxx",
    "model": "claude-opus-4-6"
  },
  "agents": {
    "main": {},
    "recaller": { "enabled": true },
    "saver": { "enabled": true, "model": "claude-haiku-4-5-20251001" },
    "summarizer": { "enabled": true, "model": "claude-haiku-4-5-20251001" }
  },
  "memory": {
    "enabled": true,
    "embeddingEndpoint": "https://dashscope.aliyuncs.com/compatible-mode/v1",
    "embeddingApiKey": "sk-xxx",
    "embeddingModel": "text-embedding-v4",
    "rerankEnabled": true,
    "rerankEndpoint": "https://dashscope.aliyuncs.com/compatible-api/v1/reranks",
    "rerankApiKey": "sk-xxx",
    "rerankModel": "qwen3-vl-rerank"
  }
}
```

每个智能体默认继承 `default` 配置，可单独覆盖。设置 `"enabled": false` 可禁用子智能体。

## 记忆管线

每轮对话的处理流程：

```
用户输入
  │
  ├─ MemoryRecaller    回忆注入：检索相关记忆，注入到上下文
  │
  ├─ Agent 响应        主智能体处理（可调用 SearchMemory/GetRecentMemories 工具）
  │
  └─ SlidingWindowChatReducer (AfterMessageAdded)
       ├─ MemorySaver            主动记忆：分析对话，保存/更新/删除记忆
       ├─ 剥离旧注入消息
       ├─ 滑动窗口裁剪           超出 windowSize + buffer 时触发
       └─ ConversationSummarizer 总结被裁剪的对话，注入摘要
```

## 项目结构

```
sharpclaw/
├── Program.cs                  # 入口：TUI / Web 模式分发
├── Abstractions/               # 接口定义
│   ├── IChatIO.cs              # 前端 I/O 抽象（TUI 和 WebSocket 共用）
│   └── IAppLogger.cs           # 日志抽象
├── Agents/                     # 智能体
│   ├── MainAgent.cs            # 主智能体：对话循环、流式输出、工具调用
│   ├── MemoryRecaller.cs       # 记忆回忆：增量注入相关记忆到上下文
│   ├── MemorySaver.cs          # 记忆保存：分析对话，自动保存/更新/删除
│   └── ConversationSummarizer.cs # 对话总结：裁剪内容增量摘要
├── Chat/
│   └── SlidingWindowChatReducer.cs # 滑动窗口裁剪器，集成记忆管线
├── Clients/
│   └── DashScopeRerankClient.cs    # 阿里云 DashScope 重排序客户端
├── Commands/                   # 系统工具（注册为 AIFunction）
│   ├── FileCommands.cs         # 文件操作：读写、搜索、编辑
│   ├── HttpCommands.cs         # HTTP 请求
│   ├── ProcessCommands.cs      # 进程执行：dotnet/node/docker
│   ├── SystemCommands.cs       # 系统信息、退出
│   └── TaskCommands.cs         # 后台任务管理
├── Core/
│   ├── AgentBootstrap.cs       # 共享初始化逻辑
│   ├── ClientFactory.cs        # 多供应商 AI 客户端工厂
│   ├── DataProtector.cs        # AES-256-CBC 加解密
│   ├── KeyStore.cs             # OS 凭据管理器密钥存储
│   ├── SharpclawConfig.cs      # 配置管理（版本迁移、加密）
│   ├── Serialization/          # JSON 序列化
│   └── TaskManagement/         # 后台任务：进程任务、原生任务
├── Memory/
│   ├── IMemoryStore.cs         # 记忆存储接口
│   ├── MemoryEntry.cs          # 记忆条目模型
│   ├── VectorMemoryStore.cs    # 向量记忆存储（嵌入 + 余弦 + 去重 + 重排序）
│   └── InMemoryMemoryStore.cs  # 内存记忆存储（测试用）
├── UI/                         # TUI 前端
│   ├── ChatWindow.cs           # 主聊天窗口（对话区 + 日志区 + 输入框）
│   ├── ConfigDialog.cs         # 配置引导对话框（TabView 分页）
│   ├── SlashCommandSuggestionGenerator.cs # 斜杠命令自动补全
│   ├── AppLogger.cs            # 日志管理
│   └── TerminalGuiLogger.cs    # TUI 日志实现
├── Web/                        # WebSocket 前端
│   ├── WebServer.cs            # ASP.NET Core 主机
│   ├── WebSocketChatIO.cs      # WebSocket IChatIO 实现
│   ├── WebSocketSender.cs      # WebSocket 消息发送
│   └── WebSocketLogger.cs      # WebSocket 日志实现
└── wwwroot/
    └── index.html              # Web 聊天界面（单文件 SPA）
```

## 数据持久化

- `~/.sharpclaw/config.json` — 供应商和智能体配置
- `history.json` — 会话状态，启动时自动恢复
- `memories.json` — 向量记忆库
