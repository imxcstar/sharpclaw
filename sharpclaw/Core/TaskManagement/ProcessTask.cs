using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace sharpclaw.Core.TaskManagement;

/// <summary>
/// Manages an external process as a task.
/// </summary>
public sealed class ProcessTask : TaskBase
{
    public Process Process { get; }
    public override int Pid => SafePid(Process);

    private Task? _stdoutPump;
    private Task? _stderrPump;
    private readonly object _stdinLock = new();

    public ProcessTask(string taskId, Process process, string displayCommand)
        : base(taskId, displayCommand, ManagedTaskKind.Process)
    {
        Process = process;
    }

    public override (bool ok, int charsWritten, string? error) TryWriteStdin(string data, bool appendNewline)
    {
        try
        {
            if (Process.HasExited) return (false, 0, "Process already exited.");

            lock (_stdinLock)
            {
                if (Process.HasExited) return (false, 0, "Process already exited.");

                if (appendNewline)
                    Process.StandardInput.WriteLine(data);
                else
                    Process.StandardInput.Write(data);

                Process.StandardInput.Flush();
            }

            return (true, data.Length + (appendNewline ? Environment.NewLine.Length : 0), null);
        }
        catch (Exception ex)
        {
            return (false, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public override bool TryCloseStdin(out string? error)
    {
        try
        {
            if (Process.HasExited) { error = "Process already exited."; return false; }

            lock (_stdinLock)
            {
                if (Process.HasExited) { error = "Process already exited."; return false; }
                Process.StandardInput.Close();
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public override void Start(int timeoutMs)
    {
        _stdoutPump = PumpAsync(Process.StandardOutput, line => AppendStdoutAndCombined(line), _lifetimeCts.Token);
        _stderrPump = PumpAsync(Process.StandardError, line => AppendStderrAndCombined(line), _lifetimeCts.Token);

        _monitor = Task.Run(async () =>
        {
            try
            {
                if (timeoutMs > 0)
                {
                    var finished = await Task.WhenAny(
                        Process.WaitForExitAsync(_lifetimeCts.Token),
                        Task.Delay(timeoutMs)
                    ).ConfigureAwait(false);

                    if (finished is Task delayTask && delayTask.IsCompleted)
                    {
                        TimedOut = true;
                        TryKillTreeInternal();
                    }
                    else
                    {
                        await Process.WaitForExitAsync(_lifetimeCts.Token).ConfigureAwait(false);
                    }
                }
                else
                {
                    await Process.WaitForExitAsync(_lifetimeCts.Token).ConfigureAwait(false);
                }
            }
            catch { /* ignore */ }
            finally
            {
                try { if (_stdoutPump != null) await _stdoutPump.ConfigureAwait(false); } catch { }
                try { if (_stderrPump != null) await _stderrPump.ConfigureAwait(false); } catch { }

                if (Process.HasExited)
                    ExitCode = Process.ExitCode;

                EndedAt = DateTimeOffset.UtcNow;
            }
        });
    }

    public override bool TryKillTree()
    {
        Killed = true;
        return TryKillTreeInternal();
    }

    private bool TryKillTreeInternal()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
            catch { break; }

            if (line == null) break;
            onLine(line);
        }
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    public override void Dispose()
    {
        base.Dispose();
        try { Process.Dispose(); } catch { }
    }
}
