# 🐾 Sharpclaw

[中文版](README_CN.md)

Sharpclaw is an advanced, highly capable AI assistant framework built on **.NET 10**. Its core distinctiveness lies in its robust **cross-conversation long-term memory system** and **system-level operation capabilities**.

By leveraging the `Microsoft.Extensions.AI` abstraction layer, Sharpclaw seamlessly integrates with multiple LLM providers (Anthropic, OpenAI, Gemini) and interacts with users through multiple frontend channels including a Terminal UI (TUI), a Web interface, and QQ Bots.

![Main Chat Window](preview/main.png)

## ✨ Key Features

* **🧠 Multi-Tier Long-Term Memory System:**
  * **Three-Layer Pipeline:** Automatically manages context through Working Memory (current session) → Recent Memory (detailed summaries) → Primary Memory (consolidated core facts).
  * **Agentic Memory Saver:** An autonomous background agent actively decides what to save, update, or delete after each conversation turn.
  * **Vector Database Integration:** Built-in vector search powered by [Sharc](https://github.com/revred/sharc.git) and SQLite, featuring semantic deduplication and a 2-stage retrieval process (Vector Search + DashScope Rerank).

* **🛠️ System Operation Capabilities (Tools/Commands):**
  * **File System:** Comprehensive file operations including searching, reading, appending, editing, and directory management.
  * **Process & Task Management:** Execute native OS commands, external processes, HTTP requests, and manage background tasks with a built-in multi-tier timing wheel scheduler.

* **📱 Multi-Channel Support:**
  * **TUI (Terminal.Gui):** A feature-rich terminal interface with collapsible logs, slash-command auto-completion, and configuration dialogs.
  * **Web (WebSocket):** A lightweight ASP.NET Core web server with a modern UI (Tokyo Night theme) and real-time streaming.
  * **QQ Bot:** Native integration with QQ channels, groups, and private messages.

* **🔒 Secure Configuration:**
  * Cross-platform secure credential storage (Windows Credential Manager, macOS Keychain, Linux libsecret) using AES-256-CBC encryption for API keys.
  * Automatic configuration version migration (up to v8).

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

2. Run the application via the CLI. Sharpclaw routes the startup based on the command provided:

* **Start Terminal UI (Default):**
```bash
dotnet run --project sharpclaw/sharpclaw.csproj -- tui
```
First run automatically launches the configuration wizard:

![Config Dialog](preview/config.png)

* **Start Web Server:**
```bash
dotnet run --project sharpclaw/sharpclaw.csproj -- web
```

![Web Chat Interface](preview/web.png)

* **Start QQ Bot:**
```bash
dotnet run --project sharpclaw/sharpclaw.csproj -- qqbot
```

* **Open Configuration UI:**
```bash
dotnet run --project sharpclaw/sharpclaw.csproj -- config
```

## 🏗️ Architecture Highlights

* **Abstracted Chat I/O:** The `IChatIO` interface unifies the I/O layer, allowing the core `MainAgent` to operate completely independently of the frontend channel.
* **Semantic Deduplication:** When storing new memories, the system calculates cosine distance (default distance 0.15) to merge highly similar context instead of duplicating it.
* **Graceful Degradation:** If embedding models are unavailable, the system gracefully falls back to a lightweight, in-memory keyword matching store (`InMemoryMemoryStore`).

## 📄 License

This project is licensed under the MIT License - see the LICENSE file for details. Copyright (c) 2025 sharpclaw.
