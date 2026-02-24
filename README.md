# Sharpclaw

[中文版](README_CN.md)

A .NET 10 AI agent with long-term memory and system operation tools. Supports Anthropic Claude, OpenAI, and Gemini multi-provider switching, with Alibaba Cloud DashScope for vector embedding and reranking.

![Main Chat Window](preview/main.png)

## Features

- **Multi-provider support** — Anthropic / OpenAI / Gemini, switch via config without code changes
- **Per-agent configuration** — Each agent (main, memory recall, memory save, summarizer) can use different API endpoints, keys, and models
- **Long-term memory** — Automatically saves, retrieves, and injects important information from conversations, persisted across sessions
- **Memory pipeline** — Four independent sub-agents collaborate: proactive memory saving, memory recall injection, sliding window trimming, conversation summarization
- **Vector semantic search** — Two-phase retrieval: vector embedding recall + optional reranking
- **Semantic deduplication** — Automatically merges memories when cosine similarity exceeds threshold, avoiding redundancy
- **System tools** — File operations, process execution (dotnet/node/docker), HTTP requests, background task management
- **TUI interface** — Terminal.Gui v2 based interface with separate chat, log, and input areas
- **Config data encryption** — Sensitive data like API keys are encrypted with AES-256-CBC, with encryption keys stored in the OS credential manager (Windows Credential Manager / macOS Keychain / Linux libsecret)

## Requirements

- .NET 10 SDK

## Quick Start

```bash
dotnet run --project sharpclaw
```

First run automatically launches the configuration wizard. Select your AI provider, enter API keys, and configure per-agent settings.

![Config Dialog](preview/config.png)

Config is saved to `~/.sharpclaw/config.json`. After that, launching goes straight to interactive chat. Type `/exit` or `/quit` to exit, `Ctrl+Q` to quit.

## Configuration

Run the config wizard:

```bash
dotnet run --project sharpclaw config
```

Config file structure (`~/.sharpclaw/config.json`):

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

Each agent inherits from `default` unless overridden. Set `"enabled": false` to disable a sub-agent.

## Memory Pipeline

Processing flow per conversation turn:

```
User Input
  |
  +- MemoryRecaller    Recall: retrieve relevant memories, inject into context
  |
  +- Agent Response    Main agent processes (can call SearchMemory/GetRecentMemories tools)
  |
  +- SlidingWindowChatReducer (AfterMessageAdded)
       +- MemorySaver            Proactive save: analyze conversation, save/update/delete memories
       +- Strip old injected messages
       +- Sliding window trim    Triggered when exceeding windowSize + buffer
       +- ConversationSummarizer Summarize trimmed conversation, inject summary
```

## Data Persistence

- `~/.sharpclaw/config.json` — Provider and agent configuration
- `history.json` — Session state, auto-restored on startup
- `memories.json` — Vector memory store