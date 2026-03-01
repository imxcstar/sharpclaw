# CLAUDE.md — Sharpclaw Project Instructions

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Sharpclaw is an advanced AI assistant framework built on **.NET 10**, featuring a robust **cross-conversation long-term memory system** and **system-level operation capabilities**. It serves as an autonomous AI agent that can explore, plan, and execute complex tasks in real codebases.

The project consists of two main components:
1. **Sharpclaw** — The AI agent framework with multi-channel frontend support
2. **Sharc** — A high-performance, pure managed C# library for reading/writing SQLite files (submodule)

---

## Build & Run Commands

```bash
# Build everything (including sharc submodule)
dotnet build

# Run Sharpclaw in different modes
dotnet run --project sharpclaw tui                          # TUI mode (Terminal.Gui)
dotnet run --project sharpclaw config                       # Re-run config dialog
dotnet run --project sharpclaw web                          # Web mode (WebSocket server)
dotnet run --project sharpclaw web --address 0.0.0.0 --port 8080
dotnet run --project sharpclaw qqbot                        # QQ Bot mode
dotnet run --project sharpclaw help                         # Show usage info

# First run of `tui` auto-launches the config dialog
# Web and QQ Bot modes require config to exist already
```

### Sharc Submodule Commands

```bash
# Build sharc only
dotnet build sharpclaw/sharc/Sharc.sln

# Run sharc tests
dotnet test sharpclaw/sharc/tests/Sharc.Tests
dotnet test sharpclaw/sharc/tests/Sharc.IntegrationTests
dotnet test sharpclaw/sharc                              # All tests

# Run sharc benchmarks (NEVER run full suite, use small chunks)
dotnet run -c Release --project sharpclaw/sharc/bench/Sharc.Comparisons -- --filter '*CoreBenchmarks*SequentialScan*'
dotnet run -c Release --project sharpclaw/sharc/bench/Sharc.Comparisons -- --tier mini
```

---

## Architecture

### Sharpclaw Architecture

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

### Multi-Tier Memory System

Sharpclaw implements a sophisticated three-layer memory pipeline:

| Layer | File | Purpose | Written By |
|-------|------|---------|------------|
| **Working Memory** | `working_memory.md` | Current conversation snapshot | MainAgent (each turn) |
| **Recent Memory** | `recent_memory.md` | Detailed summaries (append-only) | ConversationArchiver (Summarizer) |
| **Primary Memory** | `primary_memory.md` | Consolidated core facts | ConversationArchiver (Consolidator) |
| **Vector Store** | `memories.db` | Semantic embeddings + metadata | VectorMemoryStore |
| **History** | `history/*.md` | Archived full conversations | ConversationArchiver |

**Memory Pipeline Flow:**
1. After each turn → MemorySaver analyzes and updates vector store
2. When context window overflows → Summarizer generates detailed summary → appends to recent memory
3. When recent memory > 30k chars → Consolidator extracts core info → overwrites primary memory

### IChatIO Abstraction

The AI engine is decoupled from frontend through `Abstractions/IChatIO.cs`:
- `Channels/Tui/ChatWindow.cs` — Terminal.Gui v2 interface
- `Channels/Web/WebSocketChatIO.cs` — WebSocket frontend
- `Channels/QQBot/QQBotServer.cs` — QQ Bot interface

All frontends share the same `MainAgent` logic.

---

## Configuration

Configuration stored in `~/.sharpclaw/config.json` (version 8):

```json
{
  "version": 8,
  "default": { "provider": "anthropic", "endpoint": "", "apiKey": "...", "model": "claude-3-5-sonnet-20241022" },
  "agents": {
    "main": { "enabled": true, "provider": null, "model": null },
    "recaller": { "enabled": true },
    "saver": { "enabled": true },
    "summarizer": { "enabled": true }
  },
  "memory": { "embeddingProvider": "openai", "embeddingModel": "text-embedding-3-small" },
  "channels": { "tui": {}, "web": { "address": "127.0.0.1", "port": 5000 }, "qqBot": {} }
}
```

- **API keys** encrypted at rest with AES-256-CBC
- **Encryption key** stored in OS credential manager (Windows/macOS/Linux)
- **Per-agent overrides** can specify different provider/model from default
- **ExtraRequestBody** supports custom fields (e.g., `thinking`, `reasoning_split`)

---

## Key Dependencies

### Sharpclaw
- `Microsoft.Agents.AI.OpenAI` / `Microsoft.Extensions.AI.OpenAI` — AI agent framework
- `Anthropic` / `GeminiDotnet.Extensions.AI` — Multi-provider LLM support
- `Terminal.Gui` v2 (develop) — TUI framework
- `Luolan.QQBot` — QQ Bot SDK
- `Microsoft.Data.Sqlite` — SQLite for vector store
- `Sharc.Vector` — Vector operations (project reference)

### Sharc (Submodule)
- Pure managed C# — zero external dependencies
- `Sharc` — Core engine
- `Sharc.Crypto` — AES-256-GCM encryption
- `Sharc.Graph` — Cypher graph queries
- `Sharc.Vector` — SIMD vector search
- `Sharc.Query` — SQL pipeline
- `Sharc.Arc` — Cross-arc distributed sync

---

## Project Structure

```
sharpclaw/
├── CLAUDE.md                    ← You are here
├── README.md / README_CN.md     ← User-facing documentation
├── sharpclaw.slnx               ← Solution file
├── sharpclaw/                   ← Main project
│   ├── Program.cs               ← Entry point (tui/web/qqbot/config)
│   ├── sharpclaw.csproj         ← Project file (net10.0)
│   ├── Abstractions/            ← IChatIO, IAppLogger interfaces
│   ├── Agents/                  ← MainAgent, MemorySaver, ConversationArchiver
│   ├── Channels/                ← Tui, Web, QQBot frontends
│   ├── Chat/                    ← MemoryPipelineChatReducer
│   ├── Clients/                 ─ DashScopeRerankClient, ExtraFieldsPolicy
│   ├── Commands/                ← All tool implementations
│   ├── Core/                    ← Config, Bootstrap, TaskManager
│   ├── Memory/                  ← IMemoryStore, VectorMemoryStore
│   ├── UI/                      ← ConfigDialog, AppLogger
│   └── wwwroot/                 ← Web UI (index.html)
├── preview/                     ← Screenshots (main.png, web.png, config.png)
└── sharc/                       ← Submodule: high-performance SQLite library
    ├── README.md
    ├── Sharc.sln
    ├── src/                     ← 9 project folders
    ├── tests/                   ← 11 test projects (3,467 tests)
    ├── bench/                   ← BenchmarkDotNet suites
    ├── docs/                    ← Architecture & feature docs
    ├── PRC/                     ← Design decisions & specs
    └── samples/                 ← Usage examples
```

---

## Conventions

### Code Style
- Language: **Chinese prompts and UI strings**, **English code identifiers**
- Target framework: `.NET 10` (`net10.0`)
- All sub-agents use **tool calling** (not text parsing) for structured output
- Sub-agents access memory files via standard file command tools with prompt-level write restrictions
- Injected messages use `AdditionalProperties` dictionary keys for identification
- Session persistence via `working_memory.md`, `memories.db`, tiered memory files

### Agent Behavior (From MainAgent System Prompt)
1. **Context-Aware Execution**: Use `CommandDir`/`FindFiles` when unfamiliar with project; skip if memory sufficient
2. **Read Before Write**: Always `CommandCat` before `CommandEditText` to get exact line numbers
3. **Evaluate Impact**: Use `SearchInFiles` before modifying public APIs
4. **Goal Decomposition**: Break complex tasks into subtasks, execute continuously
5. **Auto-Recovery**: On failure, analyze stderr, retry 2-3 times before reporting
6. **Strict Verification**: Check Git Diff after edits, fix immediately if wrong

---

## Current Status

- **Target Framework**: .NET 10 (`net10.0`)
- **Test Status**: No test project in main sharpclaw (sharc has 3,467 tests)
- **Memory System**: Vector store with semantic deduplication (cosine distance 0.15 threshold)
- **Channels**: TUI, Web, QQBot all functional
- **Config Version**: 8 (with auto-migration)

---

## Key Files for Understanding

| To understand... | Read... |
|-----------------|---------|
| What Sharpclaw does | `README.md`, `README_CN.md` |
| Main agent logic | `sharpclaw/Agents/MainAgent.cs` |
| Memory pipeline | `sharpclaw/Chat/MemoryPipelineChatReducer.cs` |
| Available tools | `sharpclaw/Commands/*.cs` |
| Configuration schema | `sharpclaw/Core/SharpclawConfig.cs` |
| Bootstrap flow | `sharpclaw/Core/AgentBootstrap.cs` |
| Sharc library | `sharc/README.md`, `sharc/CLAUDE.md` |
| Sharc architecture | `sharc/PRC/ArchitectureOverview.md` |

---

## What NOT To Do

- **Do not add dependencies** without explicit approval (zero external dependencies is a core value for sharc)
- **Do not break the public API surface** without updating all docs and tests
- **Do not allocate in hot paths** — use spans, stackalloc, ArrayPool (sharc convention)
- **Do not bypass the Trust layer** in sharc — all agent operations must go through `AgentRegistry` and `LedgerManager`
- **Do not run full benchmark suites** — always use small chunks (2-6 benchmarks)

---

## For AI Assistants

When working with this codebase:

1. **Check memory first** — Use `SearchMemory`/`GetRecentMemories` before exploring
2. **Respect workspace boundaries** — All file operations stay within the session workspace
3. **Follow the agent protocol** — Context-aware execution, read before write, evaluate impact
4. **Understand the dual nature** — Sharpclaw is the agent framework, Sharc is the SQLite engine
5. **Use appropriate tools** — File commands for exploration, Task commands for background processes

---

## Quick Reference

```bash
# Build and run TUI
dotnet build && dotnet run --project sharpclaw tui

# Run sharc tests
dotnet test sharpclaw/sharc

# Run specific benchmark chunk
dotnet run -c Release --project sharpclaw/sharc/bench/Sharc.Comparisons -- --filter '*CoreBenchmarks*'
```
