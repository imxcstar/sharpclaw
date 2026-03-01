# 🐾 Sharpclaw

[English](README.md)

Sharpclaw 是一个基于 **.NET 10** 开发的**自主型 AI 智能体框架**。它的核心特色在于拥有**跨对话长期记忆系统**和**系统级底层操作能力**。

底层通过 `Microsoft.Extensions.AI` 抽象层，Sharpclaw 可以无缝对接多家主流大模型供应商（Anthropic、OpenAI、Gemini），并支持通过终端 TUI、Web 浏览器以及 QQ 机器人等多种渠道与用户进行交互。

![主聊天窗口](preview/main.png)

---

## ✨ 核心特性

### 🧠 多级长期记忆系统

* **三层记忆管线:** 自动管理对话上下文的衰减与提炼，工作记忆（当前会话） → 近期记忆（详细摘要） → 核心记忆（精华提炼）。
* **自主记忆智能体 (Memory Saver):** 独立的后台 Agent 会在每轮对话后，自主决定需要保存、更新或删除哪些关键记忆（事实、偏好、决策等）。
* **向量记忆库:** 集成 [Sharc](https://github.com/revred/sharc.git) 向量搜索引擎与 SQLite，支持语义去重，并采用二阶段检索架构（向量粗排 + 阿里云 DashScope Rerank 精排）。

### 🛠️ 系统级操作能力 (工具链)

* **文件系统操作:** 提供极强的文件读写能力，支持通配符搜索、文件内容搜索（类 grep）、长文本分页读取和行级编辑。
* **进程与任务管理:** 能够执行系统级命令、外部进程（Docker/Node/Dotnet）、发送 HTTP 请求。任务支持前台（阻塞）和后台两种模式，提供完整的生命周期管理：输出流读取（stdout/stderr/combined）、stdin 写入、基于关键字/正则的输出等待、进程树终止。应用退出时自动清理并终止所有后台任务。

### 📱 多端渠道接入

* **TUI 终端模式 (Terminal.Gui):** 功能完备的命令行图形界面，支持日志折叠、快捷键、斜杠命令补全和可视化的配置向导。
* **Web 模式 (WebSocket):** 轻量级 ASP.NET Core 服务，内置现代化网页终端（Tokyo Night 暗色主题），支持流式打字机输出。
* **QQ Bot 模式:** 原生接入 QQ 机器人体系，支持频道、群聊及私聊环境。

### 🔒 高安全性配置

* 跨平台凭据安全存储：支持 Windows 凭据管理器、macOS 钥匙串和 Linux libsecret，API Key 等敏感信息默认通过 AES-256-CBC 加密存储。
* 平滑升级：内置 8 个版本的配置文件自动迁移逻辑。
* 支持为 OpenAI 兼容供应商自定义请求体注入字段（如 `"thinking"`、`"reasoning_split"` 等），可在全局或单个智能体级别通过配置界面设置。

---

## 🚀 快速开始

### 环境要求

* [.NET 10.0 SDK (Preview)](https://dotnet.microsoft.com/)
* Git (用于拉取子模块)

### 编译与运行

1. 克隆仓库及子模块：
```bash
git clone --recursive https://github.com/yourusername/sharpclaw.git
cd sharpclaw
```

2. 构建整个解决方案：
```bash
dotnet build
```

3. 通过命令行参数启动不同的前端模式：

* **启动 TUI 终端模式 (默认):**
```bash
dotnet run --project sharpclaw tui
```
首次运行会自动进入配置引导：

![配置引导](preview/config.png)

* **启动 Web 服务模式:**
```bash
dotnet run --project sharpclaw web
```

![Web 聊天界面](preview/web.png)

* **启动 QQ 机器人:**
```bash
dotnet run --project sharpclaw qqbot
```

* **打开配置向导 UI:**
```bash
dotnet run --project sharpclaw config
```

---

## 🏗️ 架构设计

### 系统架构

```
┌─────────────────────────────────────────────────────────────┐
│  前端层 (Channels/)                                          │
│  ├── Tui/ — Terminal.Gui v2 (ChatWindow, ConfigDialog)      │
│  ├── Web/ — ASP.NET Core WebSocket 服务                     │
│  └── QQBot/ — QQ 机器人集成 (Luolan.QQBot)                  │
├─────────────────────────────────────────────────────────────┤
│  智能体层 (Agents/)                                          │
│  ├── MainAgent — 对话循环、工具编排                         │
│  ├── MemorySaver — 自主记忆管理                             │
│  └── ConversationArchiver — 两阶段记忆整合                  │
├─────────────────────────────────────────────────────────────┤
│  记忆管线 (Chat/, Memory/)                                   │
│  ├── MemoryPipelineChatReducer — 上下文窗口管理             │
│  ├── VectorMemoryStore — Sharc + SQLite 向量搜索            │
│  └── InMemoryMemoryStore — 关键字匹配降级方案               │
├─────────────────────────────────────────────────────────────┤
│  命令系统 (Commands/)                                        │
│  ├── FileCommands — 文件操作（cat、edit、find 等）          │
│  ├── ProcessCommands — Bash/PowerShell 执行                 │
│  ├── HttpCommands — HTTP 请求                               │
│  ├── TaskCommands — 后台任务管理                            │
│  └── SystemCommands — 系统信息、退出                        │
├─────────────────────────────────────────────────────────────┤
│  核心基础设施 (Core/)                                        │
│  ├── AgentBootstrap — 共享初始化逻辑                        │
│  ├── SharpclawConfig — 带加密的配置管理                     │
│  ├── ClientFactory — LLM 客户端创建                         │
│  ├── DataProtector/KeyStore — AES-256-CBC 加密              │
│  └── TaskManager — 后台进程管理                             │
└─────────────────────────────────────────────────────────────┘
```

### 记忆系统

Sharpclaw 实现了复杂的三层记忆管线：

| 层级 | 文件 | 用途 |
|------|------|------|
| **工作记忆** | `working_memory.md` | 当前会话快照 |
| **近期记忆** | `recent_memory.md` | 详细摘要（追加模式） |
| **核心记忆** | `primary_memory.md` | 整合后的核心事实 |
| **向量库** | `memories.db` | 语义嵌入 + 元数据 |
| **历史记录** | `history/*.md` | 归档的完整对话 |

**管线流程：**
1. 每轮对话后 → MemorySaver 分析并更新向量库
2. 上下文窗口溢出时 → Summarizer 生成详细摘要 → 追加到近期记忆
3. 近期记忆超过 30k 字符 → Consolidator 提取核心信息 → 覆写核心记忆

### IChatIO 抽象层

AI 引擎通过 `IChatIO` 接口与前端解耦：
- **TUI:** `Channels/Tui/ChatWindow.cs` — Terminal.Gui v2 界面
- **Web:** `Channels/Web/WebSocketChatIO.cs` — WebSocket 前端
- **QQ Bot:** `Channels/QQBot/QQBotServer.cs` — QQ 机器人界面

所有前端共享同一个 `MainAgent` 逻辑。

---

## 📁 项目结构

```
sharpclaw/
├── sharpclaw/                   ← 主项目
│   ├── Program.cs               ← 入口点 (tui/web/qqbot/config)
│   ├── sharpclaw.csproj         ← 项目文件 (net10.0)
│   ├── Abstractions/            ← IChatIO、IAppLogger 接口
│   ├── Agents/                  ← MainAgent、MemorySaver、ConversationArchiver
│   ├── Channels/                ← Tui、Web、QQBot 前端
│   ├── Chat/                    ← MemoryPipelineChatReducer
│   ├── Clients/                 ← DashScopeRerankClient、ExtraFieldsPolicy
│   ├── Commands/                ← 所有工具实现
│   ├── Core/                    ← 配置、引导、任务管理
│   ├── Memory/                  ← IMemoryStore、VectorMemoryStore
│   ├── UI/                      ← ConfigDialog、AppLogger
│   └── wwwroot/                 ← Web UI (index.html)
├── preview/                     ← 截图
├── sharc/                       ← 子模块：高性能 SQLite 库
│   ├── src/                     ← 9 个项目文件夹 (Sharc、Sharc.Vector 等)
│   ├── tests/                   ← 11 个测试项目 (3,467 个测试)
│   ├── bench/                   ← BenchmarkDotNet 套件
│   └── docs/                    ← 架构与功能文档
├── CLAUDE.md                    ← AI 助手指令
├── README.md / README_CN.md     ← 文档
└── sharpclaw.slnx               ← 解决方案文件
```

---

## 🔧 配置说明

配置文件存储在 `~/.sharpclaw/config.json`（版本 8）：

```json
{
  "version": 8,
  "default": {
    "provider": "anthropic",
    "apiKey": "...",
    "model": "claude-3-5-sonnet-20241022"
  },
  "agents": {
    "main": { "enabled": true },
    "saver": { "enabled": true },
    "summarizer": { "enabled": true }
  },
  "memory": {
    "embeddingProvider": "openai",
    "embeddingModel": "text-embedding-3-small"
  },
  "channels": {
    "web": { "address": "127.0.0.1", "port": 5000 }
  }
}
```

- **API 密钥** 使用 AES-256-CBC 加密存储
- **加密密钥** 存储在操作系统凭据管理器中
- **智能体级覆盖** 可指定不同的提供商/模型
- **ExtraRequestBody** 支持自定义字段（如 `thinking`）

---

## 🧩 Sharc 子模块

Sharpclaw 包含 [Sharc](https://github.com/revred/sharc.git) 作为子模块 —— 一个高性能的纯托管 C# SQLite 读写库：

- **纯托管 C#** —— 零原生依赖
- **609 倍更快** 的 B-tree 查找速度（相比 Microsoft.Data.Sqlite）
- **零分配** 每行读取（通过 `Span<T>`）
- **内置功能：** 加密、图查询（Cypher）、向量搜索、SQL 管线

详见 `sharc/README.md` 和 `sharc/CLAUDE.md`。

---

## 📝 开源协议

本项目采用 MIT 开源协议 - 详情请查看 [LICENSE](LICENSE) 文件。

Copyright (c) 2025 sharpclaw。
