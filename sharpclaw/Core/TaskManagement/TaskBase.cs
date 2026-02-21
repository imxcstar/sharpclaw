using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace sharpclaw.Core.TaskManagement;

/// <summary>
/// Base class for managed tasks providing common functionality.
/// </summary>
public abstract class TaskBase : ITask
{
    public string TaskId { get; }
    public string DisplayCommand { get; }
    public ManagedTaskKind Kind { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; protected set; }
    public bool TimedOut { get; protected set; }
    public bool Killed { get; protected set; }
    public int? ExitCode { get; protected set; }
    public abstract int Pid { get; }

    protected readonly object _lock = new();
    protected readonly StringBuilder _stdout = new();
    protected readonly StringBuilder _stderr = new();
    protected readonly StringBuilder _combined = new();
    protected readonly CancellationTokenSource _lifetimeCts = new();
    protected Task? _monitor;

    protected TaskBase(string taskId, string displayCommand, ManagedTaskKind kind)
    {
        TaskId = taskId;
        DisplayCommand = displayCommand;
        Kind = kind;
    }

    public string GetStatus()
    {
        if (TimedOut) return "timedOut";
        if (Killed) return "killed";
        if (EndedAt == null) return "running";
        return "exited";
    }

    public int GetLength(OutputStreamKind kind)
    {
        lock (_lock)
        {
            return kind switch
            {
                OutputStreamKind.Stdout => _stdout.Length,
                OutputStreamKind.Stderr => _stderr.Length,
                _ => _combined.Length
            };
        }
    }

    public string ReadChunk(OutputStreamKind kind, int offset, int length)
    {
        if (offset < 0) offset = 0;
        if (length < 0) length = 0;

        lock (_lock)
        {
            var sb = kind switch
            {
                OutputStreamKind.Stdout => _stdout,
                OutputStreamKind.Stderr => _stderr,
                _ => _combined
            };

            if (offset >= sb.Length || length == 0) return string.Empty;

            var take = Math.Min(length, sb.Length - offset);
            return sb.ToString(offset, take);
        }
    }

    public virtual async Task WaitForCompletionAsync()
    {
        if (_monitor != null) await _monitor.ConfigureAwait(false);
    }

    public abstract void Start(int timeoutMs);
    public abstract (bool ok, int charsWritten, string? error) TryWriteStdin(string data, bool appendNewline);
    public abstract bool TryCloseStdin(out string? error);
    public abstract bool TryKillTree();

    protected void AppendStdoutLine(string line) => Append(_stdout, line + Environment.NewLine);
    protected void AppendStderrLine(string line) => Append(_stderr, line + Environment.NewLine);
    protected void AppendCombinedLine(string line) => Append(_combined, line + Environment.NewLine);

    protected void AppendStdoutAndCombined(string line)
    {
        AppendStdoutLine(line);
        AppendCombinedLine(line);
    }

    protected void AppendStderrAndCombined(string line)
    {
        AppendStderrLine(line);
        AppendCombinedLine(line);
    }

    private void Append(StringBuilder sb, string chunk)
    {
        lock (_lock)
        {
            sb.Append(chunk);
        }
    }

    public virtual void Dispose()
    {
        try { _lifetimeCts.Cancel(); } catch { }
        try { _lifetimeCts.Dispose(); } catch { }
    }
}
