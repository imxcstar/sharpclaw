# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run --project sharpclaw tui                          # TUI mode (Terminal.Gui)
dotnet run --project sharpclaw config                       # Re-run config dialog
dotnet run --project sharpclaw web                          # Web mode (WebSocket server)
dotnet run --project sharpclaw web --address 0.0.0.0 --port 8080
dotnet run --project sharpclaw qqbot                        # QQ Bot mode
```

First run of `tui` auto-launches the config dialog (Terminal.Gui `ConfigDialog`) which writes `~/.sharpclaw/config.json`. Web and QQ Bot modes require config to exist already. Running with no command (or `help`) prints usage info.

No test project exists. Target framework is .NET 10 (`net10.0`).

## Configuration

All settings live in `~/.sharpclaw/config.json`. Supports three providers: Anthropic, OpenAI, Gemini. Each sub-agent (main, saver, summarizer) can override the default provider/endpoint/model or inherit from `default`. A `channels` section configures per-frontend settings (TUI, Web listen address/port, QQ Bot credentials).

API keys are encrypted at rest with AES-256-CBC (`DataProtector`). The encryption key is stored in the OS credential manager via `KeyStore` (Windows Credential Manager / macOS Keychain / Linux libsecret). Config has auto-migration (current version 8, see `ConfigMigrator` inside `SharpclawConfig.cs`).

See `Core/SharpclawConfig.cs` for the schema and `Core/ClientFactory.cs` for client instantiation.

## Architecture

Sharpclaw is a console/web AI agent with long-term memory, built on `Microsoft.Agents.AI` and `Microsoft.Extensions.AI`.

### Multi-Channel Frontend via IChatIO

The AI engine is decoupled from the frontend through `Abstractions/IChatIO.cs`. Three implementations exist under `Channels/`:
- `Channels/Tui/ChatWindow.cs` — TUI frontend using Terminal.Gui v2 (chat area + log area + input field)
- `Channels/Web/WebSocketChatIO.cs` — WebSocket frontend, served by `Channels/Web/WebServer.cs` (ASP.NET Core), single-client only
- `Channels/QQBot/QQBotChatIO.cs` — QQ Bot frontend via `Luolan.QQBot`, served by `Channels/QQBot/QQBotServer.cs`

All frontends share the same agent logic. `Abstractions/IAppLogger.cs` + `AppLogger` provide a similar abstraction for logging.

### Entry Point (`Program.cs`)

`switch` on the first CLI arg:
1. `tui` → Terminal.Gui init → `ConfigDialog` if config missing → `AgentBootstrap.Initialize()` → `ChatWindow` + `MainAgent` → TUI event loop
2. `web` → dispatches to `WebServer.RunAsync()` (WebSocket server)
3. `qqbot` → dispatches to `QQBotServer.RunAsync()` (QQ Bot service)
4. `config` → opens `ConfigDialog` only
5. Default / `help` → prints usage info

`Core/AgentBootstrap.Initialize()` is the shared bootstrap used by all channels: loads config, creates `TaskManager`, registers all command tools as `AIFunction[]`, creates `IMemoryStore`.

### MainAgent and Three-Tier Memory Pipeline

`Agents/MainAgent.cs` owns the conversation loop. It takes `IChatIO` for I/O and wires up:
- `MemorySaver` — after each turn, analyzes conversation and saves/updates/removes entries in the vector memory store (uses file tools + vector memory tools)
- `ConversationArchiver` — when the sliding window overflows, runs a two-phase archive pipeline:
  1. Summarizer Agent: reads `working_memory.md`, generates detailed summary → appends to `recent_memory.md`
  2. Consolidator Agent: when `recent_memory.md` exceeds 30k chars, extracts core info → overwrites `primary_memory.md`, trims recent memory
  3. Also saves trimmed messages as Markdown history files in `~/.sharpclaw/history/`

`Chat/MemoryPipelineChatReducer.cs` orchestrates the pipeline: strips old injected messages → triggers MemorySaver + ConversationArchiver on overflow → re-injects memory as system messages using `AdditionalProperties` keys (`AutoMemoryKey`, `AutoRecentMemoryKey`, `AutoPrimaryMemoryKey`, `AutoWorkingMemoryKey`).

Each sub-agent wraps its own `IChatClient` (via `ClientFactory.CreateAgentClient`) with `UseFunctionInvocation()` for tool calling. Sub-agents use file command tools (cat, create, edit, append, etc.) to read/write memory files, with prompt-enforced write permissions per agent.

The main agent also exposes `SearchMemory` and `GetRecentMemories` as tools the AI can call directly.

### Memory Files (`~/.sharpclaw/`)

| File | Purpose | Written by |
|------|---------|------------|
| `working_memory.md` | Current conversation snapshot (persisted each turn) | MainAgent |
| `recent_memory.md` | Conversation summaries (append-only, trimmed on consolidation) | ConversationArchiver (Summarizer) |
| `primary_memory.md` | Consolidated core memories (overwritten on consolidation) | ConversationArchiver (Consolidator) |
| `history/*.md` | Archived full conversation history as Markdown | ConversationArchiver |
| `memories.json` | Vector memory store (embeddings + metadata) | VectorMemoryStore |

### Vector Memory Store

`VectorMemoryStore` implements `IMemoryStore`:
- Persists to `memories.json`
- Two-phase search: vector embedding recall → optional `DashScopeRerankClient` rerank
- Semantic dedup: cosine similarity > 0.85 triggers merge instead of insert
- `UpdateAsync` re-generates embedding when content changes

`InMemoryMemoryStore` is a simpler keyword-based alternative.

### Command System (`Commands/`)

Agent tools registered via `AIFunctionFactory.Create(delegate)`:
- `FileCommands` — dir, cat, create, edit, rename, delete, find, search, mkdir, append, fileExists, getFileInfo
- `ProcessCommands` — dotnet, nodejs, docker execution
- `HttpCommands` — HTTP requests
- `SystemCommands` — system info, exit
- `TaskCommands` — background task management (status, read, wait, terminate, list, remove, stdin, closeStdin)

All extend `CommandBase` which provides `RunProcess`/`RunNative` for foreground/background execution via `TaskManager`.

### Config Dialog (`UI/ConfigDialog.cs`)

Terminal.Gui dialog with `TabView` pages: default provider, per-agent overrides (main/saver/summarizer), and vector memory settings (embedding + rerank). Replaces the old console-based `ConfigWizard`.

## Key Dependencies

- `Microsoft.Agents.AI` / `Microsoft.Extensions.AI` — AI agent framework and chat client abstractions
- `Anthropic`, `OpenAI`, `GeminiDotnet.Extensions.AI` — multi-provider support
- `Terminal.Gui` v2 (develop) — TUI framework
- `Luolan.QQBot` — QQ Bot SDK
- `Sharc.Vector` (project reference from `sharc/`) — vector operations for memory store
- `Microsoft.Data.Sqlite` — SQLite support
- `ToonSharp` — Lua scripting engine
- `Cronos` — cron expression parsing

## Conventions

- Language: Chinese prompts and UI strings, English code identifiers
- All sub-agents use tool calling (not text parsing) for structured output
- Sub-agents access memory files via standard file command tools, with prompt-level write restrictions (each agent can only write to its designated files)
- Injected messages use `AdditionalProperties` dictionary keys for identification and stripping during reducer passes
- Session persistence: `working_memory.md` (conversation state), `memories.json` (vector memory store), `recent_memory.md` / `primary_memory.md` (tiered memory)
- WebSocket protocol: JSON messages with `type` field (`input`, `cancel`, `chat`, `chatLine`, `state`)
