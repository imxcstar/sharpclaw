using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Tinvo.Core.TaskManagement;

/// <summary>
/// Interface for native task output operations.
/// </summary>
public interface INativeTaskOutput
{
    void WriteStdoutLine(string line);
    void WriteStderrLine(string line);
}

/// <summary>
/// Represents a line item in native stdin pipe.
/// </summary>
public readonly struct NativeLineItem
{
    public readonly string Text;
    public readonly bool HasNewline;

    public NativeLineItem(string text, bool hasNewline)
    {
        Text = text;
        HasNewline = hasNewline;
    }
}

/// <summary>
/// Native stdin pipe for reading input in native tasks.
/// </summary>
public sealed class NativeStdinPipe : IDisposable
{
    private readonly Channel<NativeLineItem?> _lines = Channel.CreateUnbounded<NativeLineItem?>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly object _gate = new();
    private bool _closed;
    private readonly StringBuilder _pending = new();

    public TextReader Reader { get; }

    public NativeStdinPipe()
    {
        Reader = new NativeStdinTextReader(_lines.Reader);
    }

    public (bool ok, int charsWritten, string? error) Write(string data, bool appendNewline)
    {
        try
        {
            lock (_gate)
            {
                if (_closed) return (false, 0, "stdin already closed.");

                var toWrite = appendNewline ? (data + Environment.NewLine) : data;
                _pending.Append(toWrite);

                DrainCompleteLines_NoLock();
                return (true, toWrite.Length, null);
            }
        }
        catch (Exception ex)
        {
            return (false, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public bool Close(out string? error)
    {
        try
        {
            lock (_gate)
            {
                if (_closed) { error = "stdin already closed."; return false; }
                _closed = true;

                if (_pending.Length > 0)
                {
                    var last = _pending.ToString();
                    _pending.Clear();
                    _lines.Writer.TryWrite(new NativeLineItem(last, hasNewline: false));
                }

                _lines.Writer.TryWrite(null); // EOF
                _lines.Writer.TryComplete();
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

    private void DrainCompleteLines_NoLock()
    {
        while (true)
        {
            int idx = IndexOfNewline(_pending);
            if (idx < 0) break;

            var lineSpan = _pending.ToString(0, idx);
            if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                lineSpan = lineSpan.Substring(0, lineSpan.Length - 1);

            _lines.Writer.TryWrite(new NativeLineItem(lineSpan, hasNewline: true));
            _pending.Remove(0, idx + 1);
        }
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] == '\n') return i;
        return -1;
    }

    public void Dispose()
    {
        try { Close(out _); } catch { }
    }

    private sealed class NativeStdinTextReader : TextReader
    {
        private readonly ChannelReader<NativeLineItem?> _reader;
        private string? _cur;
        private int _curPos;

        public NativeStdinTextReader(ChannelReader<NativeLineItem?> reader)
        {
            _reader = reader;
        }

        public override string? ReadLine()
        {
            return ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_cur != null && _curPos < _cur.Length)
            {
                int nl = _cur.IndexOf('\n', _curPos);
                if (nl >= 0)
                {
                    var seg = _cur.Substring(_curPos, nl - _curPos);
                    if (seg.EndsWith("\r", StringComparison.Ordinal)) seg = seg[..^1];
                    _curPos = nl + 1;
                    if (_curPos >= _cur.Length) { _cur = null; _curPos = 0; }
                    return seg;
                }
                else
                {
                    var seg = _cur.Substring(_curPos);
                    if (seg.EndsWith("\r", StringComparison.Ordinal)) seg = seg[..^1];
                    _cur = null; _curPos = 0;
                    return seg;
                }
            }

            NativeLineItem? item;
            try
            {
                item = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }

            if (item == null) return null;
            return item.Value.Text;
        }

        public override int Read(char[] buffer, int index, int count)
        {
            return ReadAsync(buffer, index, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            return await ReadAsync(buffer, index, count, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<int> ReadAsync(char[] buffer, int index, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (index < 0 || count < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException();

            if (count == 0) return 0;

            while (_cur == null || _curPos >= _cur.Length)
            {
                NativeLineItem? item;
                try
                {
                    item = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }

                if (item == null) return 0;

                _cur = item.Value.HasNewline ? (item.Value.Text + "\n") : item.Value.Text;
                _curPos = 0;

                if (_cur.Length == 0) break;
            }

            if (_cur == null) return 0;

            int available = _cur.Length - _curPos;
            int toCopy = Math.Min(count, available);
            _cur.CopyTo(_curPos, buffer, index, toCopy);
            _curPos += toCopy;

            if (_curPos >= _cur.Length)
            {
                _cur = null;
                _curPos = 0;
            }

            return toCopy;
        }
    }
}

/// <summary>
/// Context for native task execution.
/// </summary>
public sealed class NativeTaskContext : INativeTaskOutput
{
    private readonly NativeTask _task;
    public TextReader In { get; }

    public NativeTaskContext(NativeTask task, TextReader stdin)
    {
        _task = task;
        In = stdin;
    }

    public void WriteStdoutLine(string line) => _task.WriteStdoutLine(line);
    public void WriteStderrLine(string line) => _task.WriteStderrLine(line);
}

/// <summary>
/// Manages a native C# async task.
/// </summary>
public sealed class NativeTask : TaskBase
{
    private readonly Func<NativeTaskContext, CancellationToken, Task<int>> _runner;
    private readonly NativeStdinPipe _stdinPipe = new();

    public override int Pid => -1;

    public NativeTask(
        string taskId,
        string displayCommand,
        Func<NativeTaskContext, CancellationToken, Task<int>> runner)
        : base(taskId, displayCommand, ManagedTaskKind.Native)
    {
        _runner = runner;
    }

    internal void WriteStdoutLine(string line) => AppendStdoutAndCombined(line);
    internal void WriteStderrLine(string line) => AppendStderrAndCombined(line);

    public override (bool ok, int charsWritten, string? error) TryWriteStdin(string data, bool appendNewline)
    {
        return _stdinPipe.Write(data ?? string.Empty, appendNewline);
    }

    public override bool TryCloseStdin(out string? error)
    {
        return _stdinPipe.Close(out error);
    }

    public override bool TryKillTree()
    {
        Killed = true;
        try
        {
            _stdinPipe.Close(out _);
            _lifetimeCts.Cancel();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public override void Start(int timeoutMs)
    {
        var ctx = new NativeTaskContext(this, _stdinPipe.Reader);

        var runTask = Task.Run(async () =>
        {
            try
            {
                var code = await _runner(ctx, _lifetimeCts.Token).ConfigureAwait(false);
                ExitCode = code;
            }
            catch (OperationCanceledException)
            {
                // timeout/terminate
            }
            catch (Exception ex)
            {
                ExitCode = 1;
                WriteStderrLine($"{ex.GetType().Name}: {ex.Message}");
            }
        });

        _monitor = Task.Run(async () =>
        {
            try
            {
                if (timeoutMs > 0)
                {
                    var finished = await Task.WhenAny(runTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                    if (finished != runTask)
                    {
                        TimedOut = true;
                        _stdinPipe.Close(out _);
                        try { _lifetimeCts.Cancel(); } catch { }
                    }
                }

                await runTask.ConfigureAwait(false);
            }
            catch { /* ignore */ }
            finally
            {
                if (ExitCode == null)
                {
                    if (TimedOut) ExitCode = 124;
                    else if (Killed) ExitCode = 137;
                    else ExitCode = 0;
                }

                EndedAt = DateTimeOffset.UtcNow;
            }
        });
    }

    public override void Dispose()
    {
        base.Dispose();
        try { _stdinPipe.Dispose(); } catch { }
    }
}
