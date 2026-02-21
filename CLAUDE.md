# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run --project sharpclaw
```

First run (or `dotnet run --project sharpclaw config`) launches an interactive config wizard that writes `~/.sharpclaw/config.json`.

No test project exists. Target framework is .NET 10 (`net10.0`).

## Configuration

All client settings live in `~/.sharpclaw/config.json` (created by `ConfigWizard`). Supports three providers: Anthropic, OpenAI, Gemini. See `Core/SharpclawConfig.cs` for the schema and `Core/ClientFactory.cs` for how clients are instantiated per provider.

## Architecture

Sharpclaw is a console-based AI agent with long-term memory, built on `Microsoft.Agents.AI` and `Microsoft.Extensions.AI`.

### Main Loop (`Program.cs`)

Top-level statements wire everything together:
1. Detects config: runs `ConfigWizard` if `~/.sharpclaw/config.json` missing or `config` arg passed
2. Loads `SharpclawConfig` → `ClientFactory` creates `IChatClient` + `VectorMemoryStore`
3. Memory pipeline agents (`MemorySaver`, `MemoryRecaller`, `ConversationSummarizer`) are created inside `MainAgent`
4. Builds a `ChatClientAgent` with `UseFunctionInvocation()` and tool functions
5. REPL loop: recall → send → stream response → persist session to `history.json`

### Memory Pipeline (4 agents, 3 phases)

The memory system uses separate AI sub-agents, each wrapping their own `IChatClient` with `UseFunctionInvocation()`:

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
- Embedding model + rerank client are configurable via `~/.sharpclaw/config.json`
- Two-phase search: vector recall → optional `DashScopeRerankClient` rerank
- Semantic dedup on add: cosine similarity > `SimilarityThreshold` (0.85) triggers merge instead of insert
- `UpdateAsync` re-generates embedding when content changes

`InMemoryMemoryStore` exists as a simpler in-memory alternative (keyword-based search).

### Command System (`sharpclaw.Commands`)

Agent tools for system interaction, registered via `AIFunctionFactory.Create(delegate)`:
- `FileCommands` — dir, cat, create, edit, rename, delete, find, search
- `ProcessCommands` — dotnet, nodejs, docker execution
- `HttpCommands` — HTTP requests
- `SystemCommands` — system info, exit
- `TaskCommands` — background task management (status, read, wait, terminate)

All commands extend `CommandBase` which provides `RunProcess`/`RunNative` for foreground/background execution via `TaskManager`.

### Key Types

- `Memory/MemoryEntry.cs` — `MemoryEntry`, `MemoryStats`
- `Memory/IMemoryStore.cs` — `IMemoryStore` interface
- `Core/SharpclawConfig.cs` — `SharpclawConfig`, `MemoryConfig` (config model, load/save to `~/.sharpclaw/config.json`)
- `Core/ClientFactory.cs` — Creates `IChatClient`, `IEmbeddingGenerator`, `DashScopeRerankClient`, `VectorMemoryStore` from config
- `Core/ConfigWizard.cs` — Interactive console wizard for first-run setup

## Conventions

- Language: Chinese prompts and UI, English code identifiers
- All sub-agents use tool calling (not text format parsing) for structured output
- Injected messages use `AdditionalProperties` dictionary keys (`AutoMemoryKey`, `AutoSummaryKey`) for identification and stripping
- `ChatMessage` alias: `using ChatMessage = Microsoft.Extensions.AI.ChatMessage` (disambiguates from OpenAI's type)
- Session persistence: `history.json` (agent state), `memories.json` (vector memory store)
