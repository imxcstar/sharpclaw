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
- **Multi-channel architecture** — TUI terminal interface (Terminal.Gui v2) + WebSocket web interface + QQ Bot, all sharing the same agent logic via `IChatIO` abstraction
- **Slash commands** — Type `/` to trigger autocomplete, supports `/exit`, `/quit`, `/config`, `/help`
- **Streaming output** — Real-time streaming AI responses with reasoning logs and tool call tracing
- **Configurable keybindings** — TUI quit key, log toggle key, and cancel key are all configurable
- **Config data encryption** — Sensitive data like API keys are encrypted with AES-256-CBC, with encryption keys stored in the OS credential manager (Windows Credential Manager / macOS Keychain / Linux libsecret)

## Requirements

- .NET 10 SDK

## Quick Start

### TUI Mode (Terminal Interface)

```bash
dotnet run --project sharpclaw tui
```

First run automatically launches the configuration wizard. Select your AI provider, enter API keys, and configure per-agent settings.

![Config Dialog](preview/config.png)

Config is saved to `~/.sharpclaw/config.json`. After that, launching goes straight to interactive chat. Type `/exit` or `/quit` to exit, `Ctrl+Q` to quit (default, configurable).

### Web Mode (WebSocket Server)

```bash
dotnet run --project sharpclaw web
dotnet run --project sharpclaw web --address 0.0.0.0 --port 8080
```

Visit `http://localhost:5000` (default) to open the web chat interface. The Web UI supports Markdown rendering, code highlighting, connection status indicators, and real-time status display.

![Web Chat Interface](preview/web.png)

> Note: Web mode requires completing configuration via TUI mode first. Only one client connection is supported at a time.

### QQ Bot Mode

```bash
dotnet run --project sharpclaw qqbot
```

Runs as a QQ Bot service, receiving messages from QQ channels, groups, and private chats. Requires QQ Bot AppId and ClientSecret configured in the config file.

> Note: QQ Bot mode requires completing configuration via TUI mode first and enabling QQ Bot in the channels config.

### Other Commands

```bash
dotnet run --project sharpclaw config    # Open config dialog
dotnet run --project sharpclaw help      # Show usage info
```

## Configuration

Run the config wizard:

```bash
dotnet run --project sharpclaw config
```

Config file structure (`~/.sharpclaw/config.json`):

```json
{
  "version": 8,
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
  },
  "channels": {
    "tui": {
      "logCollapsed": false,
      "quitKey": "Ctrl+Q",
      "toggleLogKey": "Ctrl+L",
      "cancelKey": "Esc"
    },
    "web": {
      "enabled": true,
      "listenAddress": "localhost",
      "port": 5000
    },
    "qqBot": {
      "enabled": false,
      "appId": "",
      "clientSecret": "",
      "sandbox": false
    }
  }
}
```

Each agent inherits from `default` unless overridden. Set `"enabled": false` to disable a sub-agent. Channel settings configure per-frontend behavior.

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

## Project Structure

```
sharpclaw/
├── Program.cs                  # Entry point: command dispatch (tui/web/qqbot/config/help)
├── Abstractions/               # Interface definitions
│   ├── IChatIO.cs              # Frontend I/O abstraction (shared by all channels)
│   └── IAppLogger.cs           # Logger abstraction
├── Agents/                     # Agents
│   ├── MainAgent.cs            # Main agent: conversation loop, streaming, tool calls
│   ├── MemoryRecaller.cs       # Memory recall: incremental injection of relevant memories
│   ├── MemorySaver.cs          # Memory save: analyze conversation, auto save/update/delete
│   └── ConversationSummarizer.cs # Conversation summarizer: incremental summary of trimmed content
├── Channels/                   # Multi-channel frontends
│   ├── Tui/                    # TUI frontend (Terminal.Gui v2)
│   │   ├── ChatWindow.cs       # Main chat window (chat + log + input areas)
│   │   ├── SlashCommandSuggestionGenerator.cs # Slash command autocomplete
│   │   └── TerminalGuiLogger.cs # TUI logger implementation
│   ├── Web/                    # WebSocket frontend (ASP.NET Core)
│   │   ├── WebServer.cs        # ASP.NET Core host with embedded index.html
│   │   ├── WebSocketChatIO.cs  # WebSocket IChatIO implementation
│   │   ├── WebSocketSender.cs  # WebSocket message sender
│   │   └── WebSocketLogger.cs  # WebSocket logger implementation
│   └── QQBot/                  # QQ Bot frontend
│       ├── QQBotServer.cs      # QQ Bot service host (channel/group/C2C messages)
│       └── QQBotChatIO.cs      # QQ Bot IChatIO implementation
├── Chat/
│   └── SlidingWindowChatReducer.cs # Sliding window reducer with integrated memory pipeline
├── Clients/
│   └── DashScopeRerankClient.cs    # Alibaba Cloud DashScope reranking client
├── Commands/                   # System tools (registered as AIFunction)
│   ├── FileCommands.cs         # File operations: read, write, search, edit
│   ├── HttpCommands.cs         # HTTP requests
│   ├── ProcessCommands.cs      # Process execution: dotnet/node/docker
│   ├── SystemCommands.cs       # System info, exit
│   └── TaskCommands.cs         # Background task management
├── Core/
│   ├── AgentBootstrap.cs       # Shared initialization logic (all channels)
│   ├── ClientFactory.cs        # Multi-provider AI client factory
│   ├── DataProtector.cs        # AES-256-CBC encryption/decryption
│   ├── KeyStore.cs             # OS credential manager key storage
│   ├── SharpclawConfig.cs      # Config management (schema, version migration, encryption)
│   ├── Serialization/          # JSON serialization
│   └── TaskManagement/         # Background tasks: process tasks, native tasks
├── Memory/
│   ├── IMemoryStore.cs         # Memory store interface
│   ├── MemoryEntry.cs          # Memory entry model
│   ├── VectorMemoryStore.cs    # Vector memory store (embedding + cosine + dedup + rerank)
│   └── InMemoryMemoryStore.cs  # In-memory store (for testing)
├── UI/                         # Shared UI utilities
│   ├── AppLogger.cs            # Global logger management
│   └── ConfigDialog.cs         # Config wizard dialog (TabView pages)
└── wwwroot/
    └── index.html              # Web chat interface (single-file SPA, embedded resource)
```

## Data Persistence

- `~/.sharpclaw/config.json` — Provider, agent, and channel configuration
- `history.json` — Session state, auto-restored on startup
- `memories.json` — Vector memory store
