using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using sharpclaw.Core.TaskManagement;

namespace sharpclaw.Commands;

/// <summary>
/// File system operation commands.
/// </summary>
public class FileCommands : CommandBase
{
    private const int CatReadPageLines = 500;

    public FileCommands(TaskManager taskManager)
        : base(taskManager)
    {
    }

    [Description("Check if a file or directory exists")]
    public string FileExists(
        [Description("Path to check")] string path,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory!;

            var full = Path.GetFullPath(path, baseDir);
            var fileExists = File.Exists(full);
            var dirExists = Directory.Exists(full);

            return Serialize(new
            {
                ok = true,
                path = full,
                exists = fileExists || dirExists,
                isFile = fileExists,
                isDirectory = dirExists
            });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}", path });
        }
    }

    [Description("Get file or directory metadata (size, timestamps, attributes)")]
    public string GetFileInfo(
        [Description("Path to file or directory")] string path,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory!;

            var full = Path.GetFullPath(path, baseDir);

            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                return Serialize(new
                {
                    ok = true,
                    path = full,
                    type = "file",
                    exists = true,
                    size = fi.Length,
                    createdUtc = fi.CreationTimeUtc.ToString("O"),
                    modifiedUtc = fi.LastWriteTimeUtc.ToString("O"),
                    accessedUtc = fi.LastAccessTimeUtc.ToString("O"),
                    attributes = fi.Attributes.ToString(),
                    isReadOnly = fi.IsReadOnly,
                    extension = fi.Extension
                });
            }
            else if (Directory.Exists(full))
            {
                var di = new DirectoryInfo(full);
                return Serialize(new
                {
                    ok = true,
                    path = full,
                    type = "directory",
                    exists = true,
                    createdUtc = di.CreationTimeUtc.ToString("O"),
                    modifiedUtc = di.LastWriteTimeUtc.ToString("O"),
                    accessedUtc = di.LastAccessTimeUtc.ToString("O"),
                    attributes = di.Attributes.ToString()
                });
            }
            else
            {
                return Serialize(new
                {
                    ok = true,
                    path = full,
                    type = "none",
                    exists = false
                });
            }
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}", path });
        }
    }

    [Description("Find files by name pattern (supports wildcards like *.txt)")]
    public string FindFiles(
        [Description("Search pattern (e.g., *.cs, test*.txt, or exact filename)")] string pattern,
        [Description("Directory to search in (defaults to current directory)")] string searchPath = "",
        [Description("Search recursively in subdirectories")] bool recursive = true,
        [Description("Maximum number of results to return")] int maxResults = 100,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        return RunNative(
            displayCommand: $"find {pattern}",
            runner: async (ctx, ct) =>
            {
                try
                {
                    await Task.Yield();

                    var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                        ? Environment.CurrentDirectory
                        : workingDirectory!;

                    var searchDir = string.IsNullOrWhiteSpace(searchPath)
                        ? baseDir
                        : Path.GetFullPath(searchPath, baseDir);

                    if (!Directory.Exists(searchDir))
                    {
                        ctx.WriteStderrLine($"Directory not found: {searchDir}");
                        return 2;
                    }

                    var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    var results = new List<string>();

                    try
                    {
                        var files = Directory.EnumerateFiles(searchDir, pattern, searchOption);
                        foreach (var file in files)
                        {
                            ct.ThrowIfCancellationRequested();
                            results.Add(file);
                            if (results.Count >= maxResults)
                                break;
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        ctx.WriteStderrLine($"Access denied: {ex.Message}");
                    }

                    var output = Serialize(new
                    {
                        ok = true,
                        pattern,
                        searchPath = searchDir,
                        recursive,
                        count = results.Count,
                        truncated = results.Count >= maxResults,
                        files = results
                    });

                    ctx.WriteStdoutLine(output);
                    return 0;
                }
                catch (Exception ex)
                {
                    ctx.WriteStderrLine($"{ex.GetType().Name}: {ex.Message}");
                    return 1;
                }
            },
            runInBackground: false,
            timeoutMs: 30000
        );
    }

    [Description("Search for text content within files (like grep)")]
    public string SearchInFiles(
        [Description("Text or regex pattern to search for")] string searchText,
        [Description("File pattern to search in (e.g., *.cs, *.txt)")] string filePattern = "*.*",
        [Description("Directory to search in (defaults to current directory)")] string searchPath = "",
        [Description("Search recursively in subdirectories")] bool recursive = true,
        [Description("Use regex pattern matching")] bool useRegex = false,
        [Description("Case-insensitive search")] bool ignoreCase = true,
        [Description("Maximum number of matches to return")] int maxMatches = 50,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        return RunNative(
            displayCommand: $"search '{searchText}' in {filePattern}",
            runner: async (ctx, ct) =>
            {
                try
                {
                    await Task.Yield();

                    var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                        ? Environment.CurrentDirectory
                        : workingDirectory!;

                    var searchDir = string.IsNullOrWhiteSpace(searchPath)
                        ? baseDir
                        : Path.GetFullPath(searchPath, baseDir);

                    if (!Directory.Exists(searchDir))
                    {
                        ctx.WriteStderrLine($"Directory not found: {searchDir}");
                        return 2;
                    }

                    var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    var matches = new List<object>();
                    var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    Regex? regex = null;

                    if (useRegex)
                    {
                        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
                        regex = new Regex(searchText, options);
                    }

                    try
                    {
                        var files = Directory.EnumerateFiles(searchDir, filePattern, searchOption);
                        foreach (var file in files)
                        {
                            ct.ThrowIfCancellationRequested();

                            try
                            {
                                var lines = File.ReadAllLines(file);
                                for (int i = 0; i < lines.Length; i++)
                                {
                                    var line = lines[i];
                                    bool found = useRegex
                                        ? regex!.IsMatch(line)
                                        : line.Contains(searchText, comparison);

                                    if (found)
                                    {
                                        matches.Add(new
                                        {
                                            file,
                                            line = i + 1,
                                            content = line.Length > 200 ? line.Substring(0, 200) + "..." : line
                                        });

                                        if (matches.Count >= maxMatches)
                                            goto done;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // Skip files that can't be read
                                ctx.WriteStderrLine($"Skipped {file}: {ex.Message}");
                            }
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        ctx.WriteStderrLine($"Access denied: {ex.Message}");
                    }

                done:
                    var output = Serialize(new
                    {
                        ok = true,
                        searchText,
                        filePattern,
                        searchPath = searchDir,
                        recursive,
                        useRegex,
                        ignoreCase,
                        count = matches.Count,
                        truncated = matches.Count >= maxMatches,
                        matches
                    });

                    ctx.WriteStdoutLine(output);
                    return 0;
                }
                catch (Exception ex)
                {
                    ctx.WriteStderrLine($"{ex.GetType().Name}: {ex.Message}");
                    return 1;
                }
            },
            runInBackground: false,
            timeoutMs: 60000
        );
    }

    [Description("Append text to the end of a file (creates file if it doesn't exist)")]
    public string AppendToFile(
        [Description("File path")] string filePath,
        [Description("Text content to append")] string content,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory!;

            var full = Path.GetFullPath(filePath, baseDir);

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.AppendAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return Serialize(new
            {
                ok = true,
                action = "append",
                file = full,
                bytesAppended = Encoding.UTF8.GetByteCount(content)
            });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}", filePath });
        }
    }

    [Description("List directory contents with optional recursive listing")]
    public string CommandDir(
        [Description("Path to list (defaults to current directory)")] string path = "",
        [Description("Working directory (optional)")] string workingDirectory = "",
        [Description("Recursively list all subdirectories")] bool recursive = false)
    {
        var display = "dir " + (string.IsNullOrWhiteSpace(path) ? "." : path) + (recursive ? " -r" : "");

        return RunNative(
            displayCommand: display,
            runner: async (ctx, ct) =>
            {
                try
                {
                    await Task.Yield();

                    var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                        ? Environment.CurrentDirectory
                        : workingDirectory!;

                    var target = string.IsNullOrWhiteSpace(path) ? "." : path!;
                    var full = Path.GetFullPath(target, baseDir);

                    if (!Directory.Exists(full))
                    {
                        ctx.WriteStderrLine($"Directory not found: {full}");
                        return 2;
                    }

                    var root = new DirectoryInfo(full);

                    ctx.WriteStdoutLine($"Directory: {root.FullName}");
                    ctx.WriteStdoutLine($"UTC Now : {DateTimeOffset.UtcNow:O}");
                    ctx.WriteStdoutLine("");

                    void SortEntries(FileSystemInfo[] entries)
                    {
                        Array.Sort(entries, (a, b) =>
                        {
                            var ad = a is DirectoryInfo;
                            var bd = b is DirectoryInfo;
                            if (ad != bd) return ad ? -1 : 1;
                            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                        });
                    }

                    void WriteEntry(FileSystemInfo e, int depth)
                    {
                        bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
                        long size = 0;

                        if (!isDir)
                        {
                            try { size = (e as FileInfo)?.Length ?? 0; } catch { }
                        }

                        var lastUtc = SafeLastWriteTimeUtc(e);
                        var type = isDir ? "d" : "-";
                        var indent = new string(' ', depth * 2);

                        ctx.WriteStdoutLine($"{type}\t{size}\t{lastUtc:O}\t{indent}{e.Name}");
                    }

                    void Traverse(DirectoryInfo dir, int depth)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (depth == 0)
                            ctx.WriteStdoutLine($"d\t0\t{SafeLastWriteTimeUtc(dir):O}\t{dir.Name}");
                        else
                            ctx.WriteStdoutLine($"d\t0\t{SafeLastWriteTimeUtc(dir):O}\t{new string(' ', depth * 2)}{dir.Name}");

                        FileSystemInfo[] entries;
                        try
                        {
                            entries = dir.GetFileSystemInfos();
                        }
                        catch (Exception ex)
                        {
                            ctx.WriteStderrLine($"{ex.GetType().Name}: {ex.Message} ({dir.FullName})");
                            return;
                        }

                        SortEntries(entries);

                        foreach (var e in entries)
                        {
                            ct.ThrowIfCancellationRequested();

                            WriteEntry(e, depth + 1);

                            if (recursive && e is DirectoryInfo subDir)
                            {
                                var isReparsePoint = (subDir.Attributes & FileAttributes.ReparsePoint) != 0;
                                if (!isReparsePoint)
                                {
                                    Traverse(subDir, depth + 1);
                                }
                            }
                        }
                    }

                    if (recursive)
                    {
                        Traverse(root, depth: 0);
                    }
                    else
                    {
                        FileSystemInfo[] entries;
                        try { entries = root.GetFileSystemInfos(); }
                        catch (Exception ex)
                        {
                            ctx.WriteStderrLine($"{ex.GetType().Name}: {ex.Message}");
                            return 1;
                        }

                        SortEntries(entries);

                        foreach (var e in entries)
                        {
                            ct.ThrowIfCancellationRequested();
                            WriteEntry(e, depth: 0);
                        }
                    }

                    return 0;
                }
                catch (OperationCanceledException)
                {
                    ctx.WriteStderrLine("Operation canceled.");
                    return 137;
                }
                catch (Exception ex)
                {
                    ctx.WriteStderrLine($"{ex.GetType().Name}: {ex.Message}");
                    return 1;
                }
            },
            runInBackground: true,
            timeoutMs: 0
        );
    }

    [Description("Read file contents with pagination (500 lines per page)")]
    public string CommandCat(
        [Description("File path to read")] string filePath,
        [Description("Starting line number (1-based)")] int fromLine = 1,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        var display = $"cat {filePath} --from-line {fromLine}";

        return RunNative(
            displayCommand: display,
            runner: async (ctx, ct) =>
            {
                try
                {
                    var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                        ? Environment.CurrentDirectory
                        : workingDirectory!;

                    var full = Path.GetFullPath(filePath, baseDir);

                    if (!File.Exists(full))
                    {
                        ctx.WriteStderrLine($"File not found: {full}");
                        return 2;
                    }

                    if (fromLine < 1) fromLine = 1;

                    await using var fs = new FileStream(
                        full,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan
                    );

                    using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                    int currentLineNo = 0;
                    while (currentLineNo < fromLine - 1)
                    {
                        ct.ThrowIfCancellationRequested();
                        var skip = await sr.ReadLineAsync().ConfigureAwait(false);
                        if (skip == null)
                        {
                            return 0;
                        }
                        currentLineNo++;
                    }

                    int emitted = 0;
                    while (emitted < CatReadPageLines)
                    {
                        ct.ThrowIfCancellationRequested();
                        var line = await sr.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;

                        currentLineNo++;
                        ctx.WriteStdoutLine($"[{currentLineNo}]: {line}");
                        emitted++;
                    }

                    ct.ThrowIfCancellationRequested();
                    var lookahead = await sr.ReadLineAsync().ConfigureAwait(false);
                    if (lookahead != null)
                    {
                        ctx.WriteStdoutLine("--more--");
                    }

                    return 0;
                }
                catch (OperationCanceledException)
                {
                    ctx.WriteStderrLine("Operation canceled.");
                    return 137;
                }
                catch (Exception ex)
                {
                    ctx.WriteStderrLine($"{ex.GetType().Name}: {ex.Message}");
                    return 1;
                }
            },
            runInBackground: false,
            timeoutMs: 0
        );
    }

    [Description("Create a new text file (UTF-8 encoding)")]
    public string CommandCreateText(
        [Description("File path to create")] string filePath,
        [Description("File content")] string content = "",
        [Description("Overwrite if file exists")] bool force = false,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory!;

            var full = Path.GetFullPath(filePath, baseDir);

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(full) && !force)
                return Serialize(new { ok = false, error = "File already exists (use --force to overwrite)", file = full });

            content = content ?? string.Empty;
            File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return Serialize(new { ok = true, action = "create-text", file = full, overwritten = force && File.Exists(full) });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    [Description("Edit text file with insert/replace/delete/append operations")]
    public string CommandEditText(
        [Description("File path to edit")] string filePath,
        [Description("Starting line number (1-based)")] int line = 1,
        [Description("Edit mode: insert|replace|delete|append")] string mode = "insert",
        [Description("Number of lines to affect (for replace/delete)")] int count = 1,
        [Description("Text content to insert/replace/append")] string text = "",
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory!;

            var full = Path.GetFullPath(filePath, baseDir);

            if (!File.Exists(full))
                return Serialize(new { ok = false, error = "File not found", file = full });

            if (line < 1) line = 1;
            if (count < 1) count = 1;

            var modeLower = (mode ?? "insert").Trim().ToLowerInvariant();

            if ((modeLower == "insert" || modeLower == "replace" || modeLower == "append") && string.IsNullOrEmpty(text))
                return Serialize(new { ok = false, error = "--text is required for insert/replace/append", mode = modeLower, file = full });

            var lines = new List<string>();
            Encoding encoding;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                string? l;
                while ((l = sr.ReadLine()) != null) lines.Add(l);
                encoding = sr.CurrentEncoding;
            }

            var newLines = ToLinesFromEscaped(text);

            int idx = line - 1;
            int removed = 0;
            int inserted = 0;

            switch (modeLower)
            {
                case "append":
                    idx = lines.Count;
                    lines.InsertRange(idx, newLines);
                    inserted = newLines.Count;
                    break;

                case "insert":
                    if (idx < 0) idx = 0;
                    if (idx > lines.Count) idx = lines.Count;
                    lines.InsertRange(idx, newLines);
                    inserted = newLines.Count;
                    break;

                case "replace":
                    if (idx < 0 || idx >= lines.Count)
                        return Serialize(new { ok = false, error = "Line out of range for replace", line, file = full, lineCount = lines.Count });

                    removed = Math.Min(count, lines.Count - idx);
                    lines.RemoveRange(idx, removed);
                    lines.InsertRange(idx, newLines);
                    inserted = newLines.Count;
                    break;

                case "delete":
                    if (idx < 0 || idx >= lines.Count)
                        return Serialize(new { ok = false, error = "Line out of range for delete", line, file = full, lineCount = lines.Count });

                    removed = Math.Min(count, lines.Count - idx);
                    lines.RemoveRange(idx, removed);
                    break;

                default:
                    return Serialize(new { ok = false, error = "Unknown mode (use insert|replace|delete|append)", mode = modeLower, file = full });
            }

            var tmp = full + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, string.Join(Environment.NewLine, lines), encoding);
            File.Move(tmp, full, overwrite: true);

            return Serialize(new
            {
                ok = true,
                action = "edit-text",
                file = full,
                mode = modeLower,
                line,
                count,
                removedLines = removed,
                insertedLines = inserted
            });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    [Description("Create a directory (recursively creates parent directories)")]
    public string CommandMkdir(
        [Description("Directory path to create")] string path,
        [Description("Ignore if directory already exists")] bool existOk = false,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory!;

            var full = Path.GetFullPath(path, baseDir);

            if (Directory.Exists(full))
            {
                if (existOk)
                    return Serialize(new { ok = true, action = "mkdir", path = full, existed = true });

                return Serialize(new { ok = false, error = "Directory already exists (use --exist-ok to ignore)", path = full });
            }

            Directory.CreateDirectory(full);

            return Serialize(new { ok = true, action = "mkdir", path = full, existed = false });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    [Description("Delete files or directories")]
    public string CommandDelete(
        [Description("Path to delete")] string path,
        [Description("Allow recursive directory deletion")] bool recursive = false,
        [Description("Ignore if path doesn't exist; remove read-only attributes")] bool force = false,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory!;

            var full = Path.GetFullPath(path, baseDir);

            if (!File.Exists(full) && !Directory.Exists(full))
            {
                if (force)
                {
                    return Serialize(new
                    {
                        ok = true,
                        action = "delete",
                        path = full,
                        existed = false,
                        deleted = false
                    });
                }

                return Serialize(new { ok = false, error = "Path not found", path = full });
            }

            if (File.Exists(full))
            {
                try
                {
                    if (force)
                    {
                        var attrs = File.GetAttributes(full);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(full, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                catch { /* ignore */ }

                File.Delete(full);

                return Serialize(new
                {
                    ok = true,
                    action = "delete",
                    kind = "file",
                    path = full,
                    existed = true,
                    deleted = true
                });
            }

            if (!recursive)
            {
                return Serialize(new
                {
                    ok = false,
                    error = "Target is a directory (use --recursive to delete directories)",
                    path = full
                });
            }

            Directory.Delete(full, recursive: true);

            return Serialize(new
            {
                ok = true,
                action = "delete",
                kind = "directory",
                path = full,
                existed = true,
                deleted = true,
                recursive
            });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    [Description("Rename or move a file")]
    public string CommandRenameFile(
        [Description("Source file path")] string src,
        [Description("Destination file path")] string dst,
        [Description("Overwrite destination if it exists")] bool overwrite = false,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory!;

            var srcFull = Path.GetFullPath(src, baseDir);
            var dstFull = Path.GetFullPath(dst, baseDir);

            if (!File.Exists(srcFull))
            {
                if (Directory.Exists(srcFull))
                    return Serialize(new { ok = false, error = "Source is a directory (file expected)", src = srcFull });

                return Serialize(new { ok = false, error = "Source file not found", src = srcFull });
            }

            if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
            {
                return Serialize(new
                {
                    ok = true,
                    action = "rename",
                    src = srcFull,
                    dst = dstFull,
                    changed = false,
                    overwritten = false
                });
            }

            var dstDir = Path.GetDirectoryName(dstFull);
            if (!string.IsNullOrWhiteSpace(dstDir))
                Directory.CreateDirectory(dstDir);

            var dstExists = File.Exists(dstFull);
            if (dstExists && !overwrite)
            {
                return Serialize(new
                {
                    ok = false,
                    error = "Destination file already exists (use --overwrite to replace)",
                    src = srcFull,
                    dst = dstFull
                });
            }

            File.Move(srcFull, dstFull, overwrite);

            return Serialize(new
            {
                ok = true,
                action = "rename",
                src = srcFull,
                dst = dstFull,
                overwritten = dstExists && overwrite
            });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    private static List<string> ToLinesFromEscaped(string? textEscaped)
    {
        var text = textEscaped ?? string.Empty;
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        if (text.Length == 0) return new List<string>();

        return text.Split('\n', StringSplitOptions.None).ToList();
    }
}
