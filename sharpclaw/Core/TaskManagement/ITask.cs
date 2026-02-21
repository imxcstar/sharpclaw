using System;
using System.Threading.Tasks;

namespace Tinvo.Core.TaskManagement;

/// <summary>
/// Represents the kind of managed task.
/// </summary>
public enum ManagedTaskKind
{
    Process,
    Native
}

/// <summary>
/// Represents the output stream type.
/// </summary>
public enum OutputStreamKind
{
    Combined,
    Stdout,
    Stderr
}

/// <summary>
/// Interface for managed tasks (both process and native).
/// </summary>
public interface ITask : IDisposable
{
    string TaskId { get; }
    string DisplayCommand { get; }
    ManagedTaskKind Kind { get; }
    DateTimeOffset StartedAt { get; }
    DateTimeOffset? EndedAt { get; }
    bool TimedOut { get; }
    bool Killed { get; }
    int? ExitCode { get; }
    int Pid { get; }

    string GetStatus();
    int GetLength(OutputStreamKind kind);
    string ReadChunk(OutputStreamKind kind, int offset, int length);
    Task WaitForCompletionAsync();
    void Start(int timeoutMs);
    (bool ok, int charsWritten, string? error) TryWriteStdin(string data, bool appendNewline);
    bool TryCloseStdin(out string? error);
    bool TryKillTree();
}
