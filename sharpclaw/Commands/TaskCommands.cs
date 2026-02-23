using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using sharpclaw.Core.TaskManagement;
using sharpclaw.UI;

namespace sharpclaw.Commands;

/// <summary>
/// Task management commands for background tasks.
/// </summary>
public class TaskCommands : CommandBase
{
    private readonly ConcurrentDictionary<string, int> _readCursors = new(StringComparer.OrdinalIgnoreCase);
    private const int TaskReadMaxLines = 50;
    private const int TaskReadScanChunkSize = 4096;

    public TaskCommands(TaskManager taskManager)
        : base(taskManager)
    {
    }

    [Description("Get the status of a background task")]
    public string TaskGetStatus([Description("Task ID")] string taskId)
    {
        if (!TaskManager.TryGetTask(taskId, out var t))
            return Serialize(new { ok = false, error = "Task not found", taskId });

        return Serialize(new
        {
            ok = true,
            taskId,
            kind = t.Kind.ToString().ToLowerInvariant(),
            status = t.GetStatus(),
            pid = t.Pid,
            startedAtUtc = t.StartedAt.ToString("O"),
            endedAtUtc = t.EndedAt?.ToString("O"),
            exitCode = t.ExitCode,
            timedOut = t.TimedOut,
            killed = t.Killed,
            displayCommand = t.DisplayCommand
        });
    }

    [Description("Read task output with pagination (50 lines per page). Automatically manages cursor position.")]
    public string TaskRead(
        [Description("Task ID")] string taskId,
        [Description("Output stream: combined/stdout/stderr")] string stream = "combined")
    {
        if (!TaskManager.TryGetTask(taskId, out var t))
            return Serialize(new { ok = false, error = "Task not found", taskId });

        var kind = ParseStream(stream);
        var key = CursorKey(taskId, kind);

        var offset = _readCursors.GetOrAdd(key, 0);

        var totalLen = t.GetLength(kind);
        if (offset > totalLen) offset = totalLen;

        var sb = new StringBuilder(capacity: 2048);
        int pos = offset;
        int newlineCount = 0;

        while (pos < totalLen && newlineCount < TaskReadMaxLines)
        {
            int take = Math.Min(TaskReadScanChunkSize, totalLen - pos);
            if (take <= 0) break;

            var buf = t.ReadChunk(kind, pos, take);
            if (string.IsNullOrEmpty(buf)) break;

            int cutIndexExclusive = buf.Length;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i] == '\n')
                {
                    newlineCount++;
                    if (newlineCount >= TaskReadMaxLines)
                    {
                        cutIndexExclusive = i + 1;
                        break;
                    }
                }
            }

            sb.Append(buf, 0, cutIndexExclusive);
            pos += cutIndexExclusive;

            if (cutIndexExclusive < buf.Length) break;
        }

        var chunk = sb.ToString();
        var newOffset = pos;

        _readCursors[key] = newOffset;

        var nowLen = t.GetLength(kind);
        var hasMoreNow = nowLen > newOffset;

        var status = t.GetStatus();

        bool cleanedUp = false;

        if (!hasMoreNow)
        {
            if (status == "running")
            {
                _readCursors[key] = 0;
            }
            else
            {
                TaskManager.RemoveTask(taskId);
                cleanedUp = true;
            }
        }

        int returnedNewlines = 0;
        for (int i = 0; i < chunk.Length; i++)
            if (chunk[i] == '\n') returnedNewlines++;

        int returnedLines = returnedNewlines;
        if (chunk.Length > 0 && chunk[^1] != '\n') returnedLines += 1;

        var moreLine = hasMoreNow ? "\n--more--" : string.Empty;

        AppLogger.Log("------------[Log]---------------");
        var ret = $"""
ok: true
taskId: {taskId}
stream: {kind.ToString().ToLowerInvariant()}
status: {status}
offset: {offset}
maxLines: {TaskReadMaxLines}
returnedLines: {returnedLines}
chunkLength: {chunk.Length}
cleanedUp: {cleanedUp}
chunk: {chunk}{moreLine}
""";
        AppLogger.Log(ret);
        AppLogger.Log("------------[Log]---------------");
        return ret;
    }

    [Description("Terminate a running background task")]
    public string TaskTerminate([Description("Task ID")] string taskId)
    {
        if (!TaskManager.TryGetTask(taskId, out var t))
            return Serialize(new { ok = false, error = "Task not found", taskId });

        var killed = t.TryKillTree();
        return Serialize(new { ok = killed, taskId, action = "terminate", killed });
    }

    [Description("List all background tasks")]
    public string TaskList()
    {
        var list = new List<object>();
        foreach (var t in TaskManager.GetAllTasks())
        {
            list.Add(new
            {
                taskId = t.TaskId,
                kind = t.Kind.ToString().ToLowerInvariant(),
                status = t.GetStatus(),
                pid = t.Pid,
                exitCode = t.ExitCode,
                startedAtUtc = t.StartedAt.ToString("O"),
                endedAtUtc = t.EndedAt?.ToString("O"),
                displayCommand = t.DisplayCommand
            });
        }

        return Serialize(new { ok = true, count = list.Count, tasks = list });
    }

    [Description("Remove a task record and free resources")]
    public string TaskRemove([Description("Task ID")] string taskId)
    {
        if (!TaskManager.RemoveTask(taskId))
            return Serialize(new { ok = false, error = "Task not found", taskId });

        return Serialize(new { ok = true, taskId, action = "remove" });
    }

    [Description("Write data to a task's stdin")]
    public string TaskWriteStdin(
        [Description("Task ID")] string taskId,
        [Description("Data to write (supports escape sequences)")] string payloadEscaped,
        [Description("Don't append newline")] bool noNewline = false)
    {
        if (!TaskManager.TryGetTask(taskId, out var t))
            return Serialize(new { ok = false, error = "Task not found", taskId });

        var status = t.GetStatus();
        if (status != "running")
            return Serialize(new { ok = false, error = "Task is not running", taskId, status });

        var payload = UnescapePayload(payloadEscaped ?? string.Empty);
        var appendNewline = !noNewline;
        var result = t.TryWriteStdin(payload, appendNewline);

        return Serialize(new
        {
            ok = result.ok,
            taskId,
            action = "writeStdin",
            appendedNewline = appendNewline,
            charsWritten = result.charsWritten,
            error = result.error
        });
    }

    [Description("Close a task's stdin stream")]
    public string TaskCloseStdin([Description("Task ID")] string taskId)
    {
        if (!TaskManager.TryGetTask(taskId, out var t))
            return Serialize(new { ok = false, error = "Task not found", taskId });

        var status = t.GetStatus();
        if (status != "running")
            return Serialize(new { ok = false, error = "Task is not running", taskId, status });

        var ok = t.TryCloseStdin(out var err);
        return Serialize(new { ok, taskId, action = "closeStdin", error = err });
    }

    [Description("Wait for task output to match keywords or regex patterns")]
    public string TaskWait(
        [Description("Task IDs to monitor")] string[] taskIds,
        [Description("Keywords to match (can be repeated)")] string[] keywords = null,
        [Description("Regex patterns to match (can be repeated)")] string[] regexes = null,
        [Description("Output stream to monitor: combined/stdout/stderr")] string stream = "combined",
        [Description("Maximum wait time in milliseconds")] int timeoutMs = 30000,
        [Description("Polling interval in milliseconds")] int pollMs = 200,
        [Description("Start monitoring from: tail (new output) or begin (from start)")] string from = "tail",
        [Description("Case-insensitive matching")] bool ignoreCase = false,
        [Description("Maximum characters to scan per iteration")] int maxScanChars = 65536,
        [Description("Context characters before match")] int contextBefore = 80,
        [Description("Context characters after match")] int contextAfter = 120)
    {
        if (taskIds == null || taskIds.Length == 0)
            return Serialize(new { ok = false, error = "taskIds is empty" });

        taskIds = taskIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (taskIds.Length == 0)
            return Serialize(new { ok = false, error = "taskIds is empty" });

        keywords ??= Array.Empty<string>();
        regexes ??= Array.Empty<string>();

        keywords = keywords.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        regexes = regexes.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

        if (keywords.Length == 0 && regexes.Length == 0)
            return Serialize(new { ok = false, error = "No matchers provided. Use -k/--keyword and/or -r/--regex." });

        var missing = new List<string>();
        var tasks = new Dictionary<string, ITask>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in taskIds)
        {
            if (!TaskManager.TryGetTask(id, out var t)) missing.Add(id);
            else tasks[id] = t!;
        }
        if (missing.Count > 0)
            return Serialize(new { ok = false, error = "Some tasks not found", missing });

        var compiledRegexes = new List<Regex>();
        try
        {
            var opts = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (ignoreCase) opts |= RegexOptions.IgnoreCase;

            foreach (var r in regexes)
                compiledRegexes.Add(new Regex(r, opts));
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"Invalid regex: {ex.GetType().Name}: {ex.Message}" });
        }

        var kind = ParseStream(stream);

        pollMs = Math.Max(10, pollMs);
        maxScanChars = Math.Clamp(maxScanChars, 1024, 1024 * 1024);
        contextBefore = Math.Clamp(contextBefore, 0, 4096);
        contextAfter = Math.Clamp(contextAfter, 0, 4096);

        var fromLower = (from ?? "tail").Trim().ToLowerInvariant();
        bool fromBegin = fromLower == "begin";

        int maxNeedleLen = 0;
        foreach (var k in keywords) maxNeedleLen = Math.Max(maxNeedleLen, k.Length);
        foreach (var r in regexes) maxNeedleLen = Math.Max(maxNeedleLen, Math.Min(256, r.Length));
        int carryLen = Math.Clamp(maxNeedleLen - 1, 0, 2048);

        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var carries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in tasks)
        {
            var t = kv.Value;
            offsets[kv.Key] = fromBegin ? 0 : t.GetLength(kind);
            carries[kv.Key] = "";
        }

        var sw = Stopwatch.StartNew();
        TimeSpan deadline = timeoutMs <= 0 ? sw.Elapsed : TimeSpan.FromMilliseconds(timeoutMs);

        bool singleShot = timeoutMs <= 0;

        while (true)
        {
            foreach (var kv in tasks)
            {
                var taskId = kv.Key;
                var t = kv.Value;

                var status = t.GetStatus();
                var totalLen = t.GetLength(kind);

                int offset = offsets[taskId];
                string carry = carries[taskId];

                while (offset < totalLen)
                {
                    int take = Math.Min(maxScanChars, totalLen - offset);
                    var chunk = t.ReadChunk(kind, offset, take) ?? "";
                    if (chunk.Length == 0) break;

                    var window = (carryLen > 0 ? carry : "") + chunk;

                    if (TryFindMatch(window, keywords, compiledRegexes, ignoreCase, out var match))
                    {
                        int matchOffset = (offset - carry.Length) + match.Index;

                        int ctxStart = Math.Max(0, matchOffset - contextBefore);
                        int ctxTake = Math.Min(contextBefore + contextAfter, t.GetLength(kind) - ctxStart);
                        var context = ctxTake > 0 ? t.ReadChunk(kind, ctxStart, ctxTake) : "";

                        return Serialize(new
                        {
                            ok = true,
                            matched = true,
                            taskId,
                            stream = kind.ToString().ToLowerInvariant(),
                            status,
                            exitCode = t.ExitCode,
                            matchType = match.Type,
                            pattern = match.Pattern,
                            matchedText = match.MatchedText,
                            matchOffset,
                            elapsedMs = (long)sw.Elapsed.TotalMilliseconds,
                            context
                        });
                    }

                    offset += chunk.Length;

                    if (carryLen > 0)
                    {
                        if (window.Length <= carryLen) carry = window;
                        else carry = window.Substring(window.Length - carryLen);
                    }
                }

                offsets[taskId] = offset;
                carries[taskId] = carry;
            }

            bool anyRunning = tasks.Values.Any(t => t.GetStatus() == "running");
            if (!anyRunning)
            {
                return Serialize(new
                {
                    ok = true,
                    matched = false,
                    reason = "all_tasks_finished",
                    taskIds,
                    stream = kind.ToString().ToLowerInvariant(),
                    elapsedMs = (long)sw.Elapsed.TotalMilliseconds
                });
            }

            if (singleShot)
            {
                return Serialize(new
                {
                    ok = true,
                    matched = false,
                    reason = "no_match_single_shot",
                    taskIds,
                    stream = kind.ToString().ToLowerInvariant(),
                    elapsedMs = (long)sw.Elapsed.TotalMilliseconds
                });
            }

            if (sw.Elapsed >= deadline)
            {
                return Serialize(new
                {
                    ok = true,
                    matched = false,
                    reason = "timeout",
                    taskIds,
                    stream = kind.ToString().ToLowerInvariant(),
                    elapsedMs = (long)sw.Elapsed.TotalMilliseconds
                });
            }

            Thread.Sleep(pollMs);
        }
    }

    private static string CursorKey(string taskId, OutputStreamKind kind)
        => $"{taskId}|{kind.ToString().ToLowerInvariant()}";

    private sealed class WaitMatch
    {
        public int Index { get; set; }
        public string Type { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string MatchedText { get; set; } = "";
    }

    private static bool TryFindMatch(
        string haystack,
        string[] keywords,
        List<Regex> regexes,
        bool ignoreCase,
        out WaitMatch match)
    {
        match = new WaitMatch { Index = int.MaxValue };

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var k in keywords)
        {
            if (string.IsNullOrEmpty(k)) continue;
            int i = haystack.IndexOf(k, comp);
            if (i >= 0 && i < match.Index)
            {
                match.Index = i;
                match.Type = "keyword";
                match.Pattern = k;
                match.MatchedText = k;
            }
        }

        foreach (var rx in regexes)
        {
            var m = rx.Match(haystack);
            if (m.Success && m.Index < match.Index)
            {
                match.Index = m.Index;
                match.Type = "regex";
                match.Pattern = rx.ToString();
                match.MatchedText = m.Value;
            }
        }

        return match.Index != int.MaxValue;
    }

    protected static string UnescapePayload(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i == s.Length - 1)
            {
                sb.Append(c);
                continue;
            }

            char n = s[++i];
            switch (n)
            {
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;

                case 'u':
                    if (i + 4 <= s.Length - 1)
                    {
                        var hex = s.Substring(i + 1, 4);
                        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code))
                        {
                            sb.Append((char)code);
                            i += 4;
                            break;
                        }
                    }
                    sb.Append("\\u");
                    break;

                case 'x':
                    if (i + 2 <= s.Length - 1)
                    {
                        var hex2 = s.Substring(i + 1, 2);
                        if (int.TryParse(hex2, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var b))
                        {
                            sb.Append((char)b);
                            i += 2;
                            break;
                        }
                    }
                    sb.Append("\\x");
                    break;

                default:
                    sb.Append('\\').Append(n);
                    break;
            }
        }

        return sb.ToString();
    }
}
