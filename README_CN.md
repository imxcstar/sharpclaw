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
- **TUI 界面** — 基于 Terminal.Gui v2 的终端界面，对话区、日志区、输入区分离

## 环境要求

- .NET 10 SDK

## 快速开始

```bash
dotnet run --project sharpclaw
```

首次运行会自动进入配置引导，按提示选择 AI 供应商、填写 API Key，并可按智能体单独配置。

![配置引导](preview/config.png)

配置保存到 `~/.sharpclaw/config.json`。之后启动直接进入交互式对话，输入 `/exit` 或 `/quit` 退出，`Ctrl+Q` 退出程序。

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

## 数据持久化

- `~/.sharpclaw/config.json` — 供应商和智能体配置
- `history.json` — 会话状态，启动时自动恢复
- `memories.json` — 向量记忆库
