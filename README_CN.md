# 🐾 Sharpclaw

[English](README.md)

Sharpclaw 是一个基于 **.NET 10** 开发的高级 AI 助手框架。它的核心特色在于拥有**跨对话长期记忆系统**和**系统级底层操作能力**。

底层通过 `Microsoft.Extensions.AI` 抽象层，Sharpclaw 可以无缝对接多家主流大模型供应商（Anthropic、OpenAI、Gemini），并支持通过终端 TUI、Web 浏览器以及 QQ 机器人等多种渠道与用户进行交互。

![主聊天窗口](preview/main.png)

## ✨ 核心特性

* **🧠 多级长期记忆系统:**
  * **三层记忆管线:** 自动管理对话上下文的衰减与提炼，工作记忆（当前会话） → 近期记忆（详细摘要） → 核心记忆（精华提炼）。
  * **自主记忆智能体 (Memory Saver):** 独立的后台 Agent 会在每轮对话后，自主决定需要保存、更新或删除哪些关键记忆（事实、偏好、决策等）。
  * **向量记忆库:** 集成 [Sharc](https://github.com/revred/sharc.git) 向量搜索引擎与 SQLite，支持语义去重，并采用二阶段检索架构（向量粗排 + 阿里云 DashScope Rerank 精排）。

* **🛠️ 系统级操作能力 (工具链):**
  * **文件系统操作:** 提供极强的文件读写能力，支持通配符搜索、文件内容搜索（类 grep）、长文本分页读取和行级编辑。
  * **进程与任务管理:** 能够执行系统级命令、外部进程（Docker/Node/Dotnet）、发送 HTTP 请求，并内置多层级时间轮调度器（支持 Cron）用于后台任务管理。

* **📱 多端渠道接入:**
  * **TUI 终端模式 (Terminal.Gui):** 功能完备的命令行图形界面，支持日志折叠、快捷键、斜杠命令补全和可视化的配置向导。
  * **Web 模式 (WebSocket):** 轻量级 ASP.NET Core 服务，内置现代化网页终端（Tokyo Night 暗色主题），支持流式打字机输出。
  * **QQ Bot 模式:** 原生接入 QQ 机器人体系，支持频道、群聊及私聊环境。

* **🔒 高安全性配置:**
  * 跨平台凭据安全存储：支持 Windows 凭据管理器、macOS 钥匙串和 Linux libsecret，API Key 等敏感信息默认通过 AES-256-CBC 加密存储。
  * 平滑升级：内置 8 个版本的配置文件自动迁移逻辑。

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

2. 通过命令行参数启动不同的前端模式：

* **启动 TUI 终端模式 (默认):**
```bash
dotnet run --project sharpclaw/sharpclaw.csproj -- tui
```
首次运行会自动进入配置引导：

![配置引导](preview/config.png)

* **启动 Web 服务模式:**
```bash
dotnet run --project sharpclaw/sharpclaw.csproj -- web
```

![Web 聊天界面](preview/web.png)

* **启动 QQ 机器人:**
```bash
dotnet run --project sharpclaw/sharpclaw.csproj -- qqbot
```

* **打开配置向导 UI:**
```bash
dotnet run --project sharpclaw/sharpclaw.csproj -- config
```

## 🏗️ 架构设计亮点

* **渠道抽象解耦:** 核心的 `MainAgent` 对话智能体完全不感知外部环境，所有输入输出通过 `IChatIO` 接口抹平了 TUI、Web 与 QQ 的平台差异。
* **记忆语义去重:** 在写入向量数据库前，会通过余弦距离阈值（默认 0.15）检测相似度，自动将相似度极高的记忆进行合并更新，防止记忆库臃肿。
* **优雅降级策略:** 在未配置 Embedding 向量模型时，系统会自动降级使用 `InMemoryMemoryStore`，通过词频和时间衰减算法提供基于关键字的轻量级记忆匹配。

## 📄 开源协议

本项目采用 MIT 开源协议 - 详情请查看 LICENSE 文件。Copyright (c) 2025 sharpclaw。
