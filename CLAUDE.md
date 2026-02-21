# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run --project sharpclaw
```

No test project exists. Target framework is .NET 10 (`net10.0`).

## Environment Variables

- `OPENAI_API_KEY` — Anthropic API key (routed via `api.routin.ai/plan`)
- `DASHSCOPE_API_KEY` — Alibaba DashScope API key (embeddings + rerank)

## Architecture

Sharpclaw is a console-based AI agent with long-term memory, built on `Microsoft.Agents.AI` and `Microsoft.Extensions.AI`.

### Main Loop (`Program.cs`)

Top-level statements wire everything together:
1. Creates `AnthropicClient` → `IChatClient` for the main agent and sub-agents
2. Creates `VectorMemoryStore` (DashScope embeddings + rerank)
3. Creates three memory pipeline agents: `MemorySaver`, `MemoryRecaller`, `ConversationSummarizer`
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
- Uses DashScope `text-embedding-v4` for vector embeddings
- Two-phase search: vector recall → optional `DashScopeRerankClient` (`qwen3-vl-rerank`)
- Semantic dedup on add: cosine similarity > `SimilarityThreshold` (0.85) triggers merge instead of insert
- `UpdateAsync` re-generates embedding when content changes

`InMemoryMemoryStore` exists as a simpler in-memory alternative (keyword-based search).

### Command System (`Tinvo.Commands` / `Tinvo.Core`)

Agent tools for system interaction, registered via `AIFunctionFactory.Create(delegate)`:
- `FileCommands` — dir, cat, create, edit, rename, delete, find, search
- `ProcessCommands` — dotnet, nodejs, docker execution
- `HttpCommands` — HTTP requests
- `SystemCommands` — system info, exit
- `TaskCommands` — background task management (status, read, wait, terminate)

All commands extend `CommandBase` which provides `RunProcess`/`RunNative` for foreground/background execution via `TaskManager`.

### Key Types in `MemoryRecaller.cs`

This file also defines shared types: `MemoryEntry`, `MemoryStats`, `IMemoryStore`, `InMemoryMemoryStore`.

## Conventions

- Language: Chinese prompts and UI, English code identifiers
- All sub-agents use tool calling (not text format parsing) for structured output
- Injected messages use `AdditionalProperties` dictionary keys (`AutoMemoryKey`, `AutoSummaryKey`) for identification and stripping
- `ChatMessage` alias: `using ChatMessage = Microsoft.Extensions.AI.ChatMessage` (disambiguates from OpenAI's type)
- Session persistence: `history.json` (agent state), `memories.json` (vector memory store)
