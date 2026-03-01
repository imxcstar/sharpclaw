using sharpclaw.Core;
using sharpclaw.Core.Serialization;
using sharpclaw.Core.TaskManagement;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace sharpclaw.Commands;

/// <summary>
/// Base class for all command implementations.
/// Provides access to task manager and common utilities.
/// </summary>
public abstract class CommandBase
{
    protected readonly TaskManager TaskManager;
    private readonly IAgentContext _agentContext;

    protected CommandBase(TaskManager taskManager, IAgentContext agentContext)
    {
        TaskManager = taskManager;
        _agentContext = agentContext;
    }

    protected string GetDefaultWorkspace() => _agentContext.GetWorkspacePath();

    protected string Serialize(object obj) => JsonSerializer.Serialize(obj);

    protected string RunProcess(
        string fileName,
        string[] args,
        string displayCommand,
        bool runInBackground,
        string? workingDirectory,
        int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? GetDefaultWorkspace() : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args ?? Array.Empty<string>())
            psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi };

        try { p.Start(); }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}", displayCommand });
        }

        var taskId = TaskManager.GenerateTaskId();
        var mt = new ProcessTask(taskId, p, displayCommand);
        TaskManager.AddTask(mt);

        mt.Start(timeoutMs);

        if (runInBackground)
        {
            return Serialize(new
            {
                ok = true,
                mode = "background",
                kind = "process",
                taskId,
                pid = mt.Pid,
                displayCommand,
                startedAtUtc = mt.StartedAt.ToString("O")
            });
        }

        return RunForegroundAndAutoRemove(mt);
    }

    protected string RunNative(
        string displayCommand,
        Func<NativeTaskContext, CancellationToken, Task<int>> runner,
        bool runInBackground,
        int timeoutMs)
    {
        var taskId = TaskManager.GenerateTaskId();
        var mt = new NativeTask(taskId, displayCommand, runner);
        TaskManager.AddTask(mt);

        mt.Start(timeoutMs);

        if (runInBackground)
        {
            return Serialize(new
            {
                ok = true,
                mode = "background",
                kind = "native",
                taskId,
                pid = mt.Pid,
                displayCommand,
                startedAtUtc = mt.StartedAt.ToString("O")
            });
        }

        return RunForegroundAndAutoRemove(mt);
    }

    private string RunForegroundAndAutoRemove(ITask mt)
    {
        try
        {
            mt.WaitForCompletionAsync().GetAwaiter().GetResult();
            var combined = mt.ReadChunk(OutputStreamKind.Combined, 0, mt.GetLength(OutputStreamKind.Combined));
            return combined;
        }
        finally
        {
            TaskManager.RemoveTask(mt.TaskId);
        }
    }

    protected static DateTimeOffset SafeLastWriteTimeUtc(FileSystemInfo fsi)
    {
        try { return new DateTimeOffset(fsi.LastWriteTimeUtc, TimeSpan.Zero); }
        catch { return DateTimeOffset.MinValue; }
    }

    protected static OutputStreamKind ParseStream(string s)
    {
        return (s ?? "combined").Trim().ToLowerInvariant() switch
        {
            "stdout" => OutputStreamKind.Stdout,
            "stderr" => OutputStreamKind.Stderr,
            _ => OutputStreamKind.Combined
        };
    }
}
