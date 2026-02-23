# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run --project sharpclaw
```

First run (or `dotnet run --project sharpclaw config`) launches a TUI config dialog that writes `~/.sharpclaw/config.json`.

No test project exists. Target framework is .NET 10 (`net10.0`).

## Configuration

Config lives in `~/.sharpclaw/config.json` (created by `UI/ConfigDialog.cs`). Version 4 structure with auto-migration from older versions.

Per-agent config model: a `default` section provides fallback values (provider/endpoint/apiKey/model), and each agent in `agents` (main/recaller/saver/summarizer) can override any field or be disabled. `memory` section configures embedding and rerank independently.

Three providers supported: Anthropic, OpenAI, Gemini. See `Core/ClientFactory.cs` for client instantiation. API keys are encrypted at rest via `Core/DataProtector.cs` (AES-256-CBC, key from OS credential store via `Core/KeyStore.cs`).

## Architecture

Sharpclaw is a TUI-based AI agent with long-term memory, built on `Microsoft.Agents.AI`, `Microsoft.Extensions.AI`, and `Terminal.Gui` v2.

### Main Loop (`Program.cs`)

Top-level statements wire everything together:
1. `Application.Create().Init()` — Terminal.Gui lifecycle
2. Detects config: runs `ConfigDialog` if `~/.sharpclaw/config.json` missing or `config` arg passed
3. Loads `SharpclawConfig` → passes to `MainAgent` which creates per-agent `IChatClient` instances via `ClientFactory.CreateAgentClient()`
4. `MainAgent` creates sub-agents internally based on each agent's `Enabled` flag
5. Agent runs in `Task.Run()`, TUI runs in `app.Run(chatWindow)` on main thread

### UI Layer (`sharpclaw.UI`)

Terminal.Gui v2 (develop build). Key patterns:
- `ChatWindow` (extends `Runnable`): chat area (60%), log area, input field, spinner/status. All text writes are buffered with 100ms flush + lock to avoid `WordWrap` index crashes.
- `AppLogger`: static global log router with same buffered write pattern. All agents log via `AppLogger.Log()` and update status via `AppLogger.SetStatus()`.
- `ConfigDialog` (extends `Dialog`): TabView with 6 tabs (默认/主智能体/记忆回忆/记忆保存/对话总结/记忆).
- Thread safety: background agent threads must use `App.Invoke()` for UI updates (not `Application.Invoke` which is obsolete in v2).

### Memory Pipeline (4 agents, 3 phases)

Each sub-agent wraps its own `IChatClient` (potentially different provider/model) with `UseFunctionInvocation()`:

**Phase 1 — Recall (main loop, before input):**
- `MemoryRecaller` — Tools: `KeepMemories`, `SearchMemory`. Maintains `_currentMemories` state for incremental injection. Injects a system message tagged with `AutoMemoryKey`.

**Phase 2 — Save (inside `SlidingWindowChatReducer`, after message added):**
- `MemorySaver` — Tools: `SaveMemory`, `UpdateMemory`, `RemoveMemory`. Searches existing memories first, then decides save/update/remove. Supports regex template extraction from conversation text (`{0}` placeholders + `patterns[]`).

**Phase 3 — Trim + Summarize (inside `SlidingWindowChatReducer`):**
- Strips old injected messages (`AutoMemoryKey`, `AutoSummaryKey`)
- Sliding window with overflow buffer: trims when `count > windowSize + overflowBuffer`, cuts back to `windowSize`
- `ConversationSummarizer` — Incrementally summarizes trimmed messages, injects as system message tagged with `AutoSummaryKey`.

### Memory Store

`VectorMemoryStore` implements `IMemoryStore`:
- Persists to `memories.json`
- Two-phase search: vector recall → optional `DashScopeRerankClient` rerank
- Semantic dedup on add: cosine similarity > `SimilarityThreshold` (0.85) triggers merge instead of insert

`InMemoryMemoryStore` exists as a simpler in-memory alternative (keyword-based search).

### Command System (`sharpclaw.Commands`)

Agent tools registered via `AIFunctionFactory.Create(delegate)` in `Program.cs`. All commands extend `CommandBase` which provides `RunProcess`/`RunNative` for foreground/background execution via `TaskManager`.

### Key Types

- `Core/SharpclawConfig.cs` — `SharpclawConfig`, `DefaultAgentConfig`, `AgentConfig`, `AgentsConfig`, `MemoryConfig`, `ConfigMigrator`
- `Core/ClientFactory.cs` — `CreateChatClient(DefaultAgentConfig)`, `CreateAgentClient(config, agent)`, embedding/rerank/memory store creation
- `Memory/IMemoryStore.cs` — `IMemoryStore` interface
- `Memory/MemoryEntry.cs` — `MemoryEntry`, `MemoryStats`

## Conventions

- Language: Chinese prompts and UI text, English code identifiers
- All sub-agents use tool calling (not text format parsing) for structured output
- Injected messages use `AdditionalProperties` dictionary keys (`AutoMemoryKey`, `AutoSummaryKey`) for identification and stripping
- Session persistence: `history.json` (agent state), `memories.json` (vector memory store)
- Config versioning: `ConfigMigrator` runs sequential migrations (v1→v2→v3→v4) on load
