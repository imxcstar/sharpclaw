# 🐾 Sharpclaw

[中文版](README_CN.md)

Sharpclaw is an advanced, highly capable **autonomous AI agent framework** built on **.NET 10**. Its core distinctiveness lies in its robust **cross-conversation long-term memory system** and **system-level operation capabilities**.

By leveraging the `Microsoft.Extensions.AI` abstraction layer, Sharpclaw seamlessly integrates with multiple LLM providers (Anthropic, OpenAI, Gemini) and interacts with users through multiple frontend channels including a Terminal UI (TUI), a Web interface, and QQ Bots.

![Main Chat Window](preview/main.png)

---

## ✨ Key Features

### 🧠 Multi-Tier Long-Term Memory System

* **Three-Layer Pipeline:** Automatically manages context through Working Memory (current session) → Recent Memory (detailed summaries) → Primary Memory (consolidated core facts).
* **Agentic Memory Saver:** An autonomous background agent actively decides what to save, update, or delete after each conversation turn.
* **Vector Database Integration:** Built-in vector search powered by [Sharc](https://github.com/revred/sharc.git) and SQLite, featuring semantic deduplication and a 2-stage retrieval process (Vector Search + DashScope Rerank).

### 🛠️ System Operation Capabilities (Tools/Commands)

* **File System:** Comprehensive file operations including searching, reading, appending, editing, and directory management.
* **Process & Task Management:** Execute native OS commands, external processes, HTTP requests, and manage background tasks. Tasks support foreground (blocking) and background modes, with full lifecycle management including output streaming (stdout/stderr/combined), stdin writing, keyword/regex-based output waiting, and process tree termination. All background tasks are automatically killed and cleaned up on application exit.

### 📱 Multi-Channel Support

* **TUI (Terminal.Gui):** A feature-rich terminal interface with collapsible logs, slash-command auto-completion, and configuration dialogs.
* **Web (WebSocket):** A lightweight ASP.NET Core web server with a modern UI (Tokyo Night theme) and real-time streaming.
* **QQ Bot:** Native integration with QQ channels, groups, and private messages.

### 🔒 Secure Configuration

* Cross-platform secure credential storage (Windows Credential Manager, macOS Keychain, Linux libsecret) using AES-256-CBC encryption for API keys.
* Automatic configuration version migration (up to v8).
* Per-provider custom request body injection (e.g. `"thinking"`, `"reasoning_split"`) — configurable globally or per-agent via the Config Dialog.

---

## 🚀 Getting Started

### Prerequisites

* [.NET 10.0 SDK (Preview)](https://dotnet.microsoft.com/)
* Git (for cloning submodules)

### Build and Run

1. Clone the repository with its submodules:
```bash
git clone --recursive https://github.com/yourusername/sharpclaw.git
cd sharpclaw
```

2. Build the entire solution:
```bash
dotnet build
```

3. Run the application via the CLI. Sharpclaw routes the startup based on the command provided:

* **Start Terminal UI (Default):**
```bash
dotnet run --project sharpclaw tui
```
First run automatically launches the configuration wizard:

![Config Dialog](preview/config.png)

* **Start Web Server:**
```bash
dotnet run --project sharpclaw web
```

![Web Chat Interface](preview/web.png)

* **Start QQ Bot:**
```bash
dotnet run --project sharpclaw qqbot
```

* **Open Configuration UI:**
```bash
dotnet run --project sharpclaw config
```

---

## 🏗️ Architecture

### System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  Frontend Layer (Channels/)                                  │
│  ├── Tui/ — Terminal.Gui v2 (ChatWindow, ConfigDialog)      │
│  ├── Web/ — ASP.NET Core WebSocket server                   │
│  └── QQBot/ — QQ Bot integration (Luolan.QQBot)             │
├─────────────────────────────────────────────────────────────┤
│  Agent Layer (Agents/)                                       │
│  ├── MainAgent — Conversation loop, tool orchestration      │
│  ├── MemorySaver — Autonomous memory management             │
│  └── ConversationArchiver — Two-phase memory consolidation  │
├─────────────────────────────────────────────────────────────┤
│  Memory Pipeline (Chat/, Memory/)                            │
│  ├── MemoryPipelineChatReducer — Context window management  │
│  ├── VectorMemoryStore — Sharc + SQLite vector search       │
│  └── InMemoryMemoryStore — Keyword-based fallback           │
├─────────────────────────────────────────────────────────────┤
│  Command System (Commands/)                                  │
│  ├── FileCommands — File operations (cat, edit, find, etc.) │
│  ├── ProcessCommands — Bash/PowerShell execution            │
│  ├── HttpCommands — HTTP requests                           │
│  ├── TaskCommands — Background task management              │
│  └── SystemCommands — System info, exit                     │
├─────────────────────────────────────────────────────────────┤
│  Core Infrastructure (Core/)                                 │
│  ├── AgentBootstrap — Shared initialization                 │
│  ├── SharpclawConfig — Configuration with encryption        │
│  ├── ClientFactory — LLM client creation                    │
│  ├── DataProtector/KeyStore — AES-256-CBC encryption        │
│  └── TaskManager — Background process management            │
└─────────────────────────────────────────────────────────────┘
```

### Memory System

Sharpclaw implements a sophisticated three-layer memory pipeline:

| Layer | File | Purpose |
|-------|------|---------|
| **Working Memory** | `working_memory.md` | Current conversation snapshot |
| **Recent Memory** | `recent_memory.md` | Detailed summaries (append-only) |
| **Primary Memory** | `primary_memory.md` | Consolidated core facts |
| **Vector Store** | `memories.db` | Semantic embeddings + metadata |
| **History** | `history/*.md` | Archived full conversations |

**Pipeline Flow:**
1. After each turn → MemorySaver analyzes and updates vector store
2. When context window overflows → Summarizer generates detailed summary → appends to recent memory
3. When recent memory > 30k chars → Consolidator extracts core info → overwrites primary memory

### IChatIO Abstraction

The AI engine is decoupled from frontend through `IChatIO` interface:
- **TUI:** `Channels/Tui/ChatWindow.cs` — Terminal.Gui v2 interface
- **Web:** `Channels/Web/WebSocketChatIO.cs` — WebSocket frontend
- **QQ Bot:** `Channels/QQBot/QQBotServer.cs` — QQ Bot interface

All frontends share the same `MainAgent` logic.

---

## 📁 Project Structure

```
sharpclaw/
├── sharpclaw/                   ← Main project
│   ├── Program.cs               ← Entry point (tui/web/qqbot/config)
│   ├── sharpclaw.csproj         ← Project file (net10.0)
│   ├── Abstractions/            ← IChatIO, IAppLogger interfaces
│   ├── Agents/                  ← MainAgent, MemorySaver, ConversationArchiver
│   ├── Channels/                ← Tui, Web, QQBot frontends
│   ├── Chat/                    ← MemoryPipelineChatReducer
│   ├── Clients/                 ← DashScopeRerankClient, ExtraFieldsPolicy
│   ├── Commands/                ← All tool implementations
│   ├── Core/                    ← Config, Bootstrap, TaskManager
│   ├── Memory/                  ← IMemoryStore, VectorMemoryStore
│   ├── UI/                      ← ConfigDialog, AppLogger
│   └── wwwroot/                 ← Web UI (index.html)
├── preview/                     ← Screenshots
├── sharc/                       ← Submodule: high-performance SQLite library
│   ├── src/                     ← 9 project folders (Sharc, Sharc.Vector, etc.)
│   ├── tests/                   ← 11 test projects (3,467 tests)
│   ├── bench/                   ← BenchmarkDotNet suites
│   └── docs/                    ← Architecture & feature docs
├── CLAUDE.md                    ← AI assistant instructions
├── README.md / README_CN.md     ← Documentation
└── sharpclaw.slnx               ← Solution file
```

---

## 🔧 Configuration

Configuration is stored in `~/.sharpclaw/config.json` (version 8):

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

- **API keys** encrypted at rest with AES-256-CBC
- **Encryption key** stored in OS credential manager
- **Per-agent overrides** can specify different provider/model
- **ExtraRequestBody** supports custom fields (e.g., `thinking`)

---

## 🧩 Sharc Submodule

Sharpclaw includes [Sharc](https://github.com/revred/sharc.git) as a submodule — a high-performance, pure managed C# library for reading/writing SQLite files:

- **Pure managed C#** — zero native dependencies
- **609x faster** B-tree seeks than Microsoft.Data.Sqlite
- **Zero allocation** per-row reads via `Span<T>`
- **Built-in features:** Encryption, Graph queries (Cypher), Vector search, SQL pipeline

See `sharc/README.md` and `sharc/CLAUDE.md` for details.

---

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

Copyright (c) 2025 sharpclaw.
