# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run --project sharpclaw            # TUI mode (Terminal.Gui)
dotnet run --project sharpclaw config     # Re-run config dialog
dotnet run --project sharpclaw serve      # Web mode (WebSocket server, default port 5000)
dotnet run --project sharpclaw serve --port 8080
```

First run auto-launches the config dialog (Terminal.Gui `ConfigDialog`) which writes `~/.sharpclaw/config.json`. Web mode requires config to exist already.

No test project exists. Target framework is .NET 10 (`net10.0`).

## Configuration

All settings live in `~/.sharpclaw/config.json`. Supports three providers: Anthropic, OpenAI, Gemini. Each sub-agent (main, recaller, saver, summarizer) can override the default provider/endpoint/model or inherit from `default`.

API keys are encrypted at rest with AES-256-CBC (`DataProtector`). The encryption key is stored in the OS credential manager via `KeyStore` (Windows Credential Manager / macOS Keychain / Linux libsecret).

See `Core/SharpclawConfig.cs` for the schema and `Core/ClientFactory.cs` for client instantiation.

## Architecture

Sharpclaw is a console/web AI agent with long-term memory, built on `Microsoft.Agents.AI` and `Microsoft.Extensions.AI`.

### Dual Frontend via IChatIO

The AI engine is decoupled from the frontend through `Abstractions/IChatIO.cs`. Two implementations exist:
- `UI/ChatWindow.cs` — TUI frontend using Terminal.Gui v2 (chat area + log area + input field)
- `Web/WebSocketChatIO.cs` — WebSocket frontend, served by `Web/WebServer.cs` (ASP.NET Core), single-client only

Both frontends share the same agent logic. `Abstractions/IAppLogger.cs` + `AppLogger` provide a similar abstraction for logging.

### Entry Point (`Program.cs`)

1. `args.Contains("serve")` → dispatches to `WebServer.RunAsync()` (web mode)
2. Otherwise → Terminal.Gui init → `ConfigDialog` if config missing → `AgentBootstrap.Initialize()` → `ChatWindow` + `MainAgent` → TUI event loop

`Core/AgentBootstrap.Initialize()` is the shared bootstrap used by both TUI and Web mode: loads config, creates `TaskManager`, registers all command tools as `AIFunction[]`, creates `IMemoryStore`.

### MainAgent and Memory Pipeline

`Agents/MainAgent.cs` owns the conversation loop. It takes `IChatIO` for I/O and wires up:
- `MemoryRecaller` — recalls relevant memories before each turn, injects as system message (`AutoMemoryKey`)
- `MemorySaver` — saves/updates/removes memories after each message (inside `SlidingWindowChatReducer`)
- `ConversationSummarizer` — summarizes trimmed messages when sliding window overflows (`AutoSummaryKey`)

Each sub-agent wraps its own `IChatClient` (via `ClientFactory.CreateAgentClient`) with `UseFunctionInvocation()` for tool calling.

The main agent also exposes `SearchMemory` and `GetRecentMemories` as tools the AI can call directly.

### Memory Store

`VectorMemoryStore` implements `IMemoryStore`:
- Persists to `memories.json`
- Two-phase search: vector embedding recall → optional `DashScopeRerankClient` rerank
- Semantic dedup: cosine similarity > 0.85 triggers merge instead of insert
- `UpdateAsync` re-generates embedding when content changes

`InMemoryMemoryStore` is a simpler keyword-based alternative.

### Command System (`Commands/`)

Agent tools registered via `AIFunctionFactory.Create(delegate)`:
- `FileCommands` — dir, cat, create, edit, rename, delete, find, search, mkdir, append
- `ProcessCommands` — dotnet, nodejs, docker execution
- `HttpCommands` — HTTP requests
- `SystemCommands` — system info, exit
- `TaskCommands` — background task management (status, read, wait, terminate, list, remove, stdin)

All extend `CommandBase` which provides `RunProcess`/`RunNative` for foreground/background execution via `TaskManager`.

### Config Dialog (`UI/ConfigDialog.cs`)

Terminal.Gui dialog with `TabView` pages: default provider, per-agent overrides (main/recaller/saver/summarizer), and vector memory settings (embedding + rerank). Replaces the old console-based `ConfigWizard`.

## Conventions

- Language: Chinese prompts and UI strings, English code identifiers
- All sub-agents use tool calling (not text parsing) for structured output
- Injected messages use `AdditionalProperties` dictionary keys (`AutoMemoryKey`, `AutoSummaryKey`) for identification and stripping
- `ChatMessage` alias: `using ChatMessage = Microsoft.Extensions.AI.ChatMessage` (disambiguates from OpenAI's type)
- Session persistence: `history.json` (agent state), `memories.json` (vector memory store)
- WebSocket protocol: JSON messages with `type` field (`input`, `cancel`, `chat`, `chatLine`, `state`)
