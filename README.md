# Sharpclaw

基于 .NET 10 的 AI 智能体，具备长期记忆能力和系统操作工具。使用 Anthropic Claude 作为主模型，阿里云 DashScope 提供向量嵌入和重排序。

## 特性

- **长期记忆** — 自动保存、检索、注入对话中的重要信息，跨会话持久化
- **记忆管线** — 四个独立子智能体协作：主动记忆保存、记忆回忆注入、滑动窗口裁剪、对话摘要
- **向量语义搜索** — 基于 DashScope text-embedding-v4 嵌入 + qwen3-vl-rerank 重排序的两阶段检索
- **语义去重** — 余弦相似度超过阈值时自动合并记忆，避免冗余
- **系统工具** — 文件操作、进程执行（dotnet/node/docker）、HTTP 请求、后台任务管理

## 环境要求

- .NET 10 SDK
- `OPENAI_API_KEY` — Anthropic API 密钥
- `DASHSCOPE_API_KEY` — 阿里云 DashScope API 密钥

## 快速开始

```bash
dotnet run --project sharpclaw
```

启动后进入交互式对话，输入 `/exit` 或 `/quit` 退出。

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

- `history.json` — 会话状态，启动时自动恢复
- `memories.json` — 向量记忆库
