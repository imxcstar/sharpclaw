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
    private const int CatReadPageLines = 2000;

    public FileCommands(TaskManager taskManager)
        : base(taskManager)
    {
    }

    [Description("Find files by name pattern (e.g., *.cs, test*.txt). BEST PRACTICE: Use this to quickly locate files, then use CommandCat to read their contents.")]
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
                        ctx.WriteStderrLine($"Error: Directory not found: {searchDir}");
                        return 2;
                    }

                    var enumerationOptions = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = recursive,
                        MatchCasing = MatchCasing.PlatformDefault
                    };

                    string[] ignoredDirs = {
                    $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                    $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"
                    };

                    var results = new List<string>();

                    try
                    {
                        var files = Directory.EnumerateFiles(searchDir, pattern, enumerationOptions);
                        foreach (var file in files)
                        {
                            ct.ThrowIfCancellationRequested();

                            if (ignoredDirs.Any(dir => file.Contains(dir, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            results.Add(file);
                            if (results.Count >= maxResults)
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.WriteStderrLine($"Warning during search: {ex.Message}");
                    }

                    // --- 构建友好的纯文本输出 ---
                    var sb = new StringBuilder();
                    sb.AppendLine($"--- Search Results for '{pattern}' ---");
                    sb.AppendLine($"Directory: {searchDir}");
                    sb.AppendLine($"Total Found: {results.Count}{(results.Count >= maxResults ? " (TRUNCATED - reached limit)" : "")}");
                    sb.AppendLine("----------------------------------------");

                    if (results.Count == 0)
                    {
                        sb.AppendLine("No files found matching the pattern.");
                    }
                    else
                    {
                        for (int i = 0; i < results.Count; i++)
                        {
                            sb.AppendLine($"{i + 1}. {results[i]}");
                        }
                    }

                    sb.AppendLine("----------------------------------------");
                    if (results.Count > 0)
                    {
                        sb.AppendLine("💡 TIP: Use CommandCat to read the content of the files you are interested in.");
                    }

                    ctx.WriteStdoutLine(sb.ToString());
                    return 0;
                }
                catch (Exception ex)
                {
                    ctx.WriteStderrLine($"Error ({ex.GetType().Name}): {ex.Message}");
                    return 1;
                }
            },
            runInBackground: false,
            timeoutMs: 30000
        );
    }

    [Description("Search for text content within files (like grep). BEST PRACTICE: Use this to find where variables or functions are defined. Once you have the file and line number, use CommandCat with startLine and endLine to see the full context.")]
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
                        ctx.WriteStderrLine($"Error: Directory not found: {searchDir}");
                        return 2;
                    }

                    var enumerationOptions = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = recursive,
                        MatchCasing = MatchCasing.PlatformDefault
                    };

                    string[] ignoredDirs = {
                    $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                    $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}"
                    };

                    // 改用字典按文件分组存放匹配结果，对 LLM 阅读极其友好
                    var groupedMatches = new Dictionary<string, List<(int Line, string Content)>>();
                    int totalMatches = 0;
                    bool isTruncated = false;

                    var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    Regex? regex = null;

                    if (useRegex)
                    {
                        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
                        regex = new Regex(searchText, options | RegexOptions.Compiled);
                    }

                    try
                    {
                        var files = Directory.EnumerateFiles(searchDir, filePattern, enumerationOptions);
                        foreach (var file in files)
                        {
                            ct.ThrowIfCancellationRequested();

                            if (ignoredDirs.Any(dir => file.Contains(dir, StringComparison.OrdinalIgnoreCase))) continue;

                            try
                            {
                                int lineNum = 0;
                                foreach (var line in File.ReadLines(file))
                                {
                                    lineNum++;
                                    bool found = useRegex
                                        ? regex!.IsMatch(line)
                                        : line.Contains(searchText, comparison);

                                    if (found)
                                    {
                                        if (!groupedMatches.ContainsKey(file))
                                        {
                                            groupedMatches[file] = new List<(int, string)>();
                                        }

                                        // 截断过长的单行文本 (如压缩过的 JS 代码)，防止刷屏
                                        string displayContent = line.Length > 200 ? line.Substring(0, 200) + "..." : line.Trim();
                                        groupedMatches[file].Add((lineNum, displayContent));
                                        totalMatches++;

                                        if (totalMatches >= maxMatches)
                                        {
                                            isTruncated = true;
                                            goto done;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                ctx.WriteStderrLine($"Skipped {Path.GetFileName(file)}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.WriteStderrLine($"Warning during search: {ex.Message}");
                    }

                done:
                    // --- 构建高度结构化的纯文本输出 ---
                    var sb = new StringBuilder();
                    sb.AppendLine($"--- Search Results for '{searchText}' ---");
                    sb.AppendLine($"File Pattern: {filePattern}");
                    sb.AppendLine($"Total Matches: {totalMatches}{(isTruncated ? $" (TRUNCATED at {maxMatches} limits)" : "")}");
                    sb.AppendLine("========================================");

                    if (totalMatches == 0)
                    {
                        sb.AppendLine("No matches found. Try modifying your search text or file pattern.");
                    }
                    else
                    {
                        // 遍历分组打印，视觉上非常清晰
                        foreach (var kvp in groupedMatches)
                        {
                            sb.AppendLine($"\n📄 File: {kvp.Key}");
                            foreach (var match in kvp.Value)
                            {
                                // 使用和 CommandCat 完全一致的行号格式： "   1 | code"
                                sb.AppendLine($"  {match.Line,4} | {match.Content}");
                            }
                        }
                    }

                    sb.AppendLine("\n========================================");
                    if (totalMatches > 0)
                    {
                        sb.AppendLine("💡 TIP: Match found! Next step: Use CommandCat with startLine/endLine to read the surrounding code context.");
                    }

                    ctx.WriteStdoutLine(sb.ToString());
                    return 0;
                }
                catch (Exception ex)
                {
                    ctx.WriteStderrLine($"Error ({ex.GetType().Name}): {ex.Message}");
                    return 1;
                }
            },
            runInBackground: false,
            timeoutMs: 60000
        );
    }

    [Description("Get the total number of lines in a file. Extremely useful to check file size before using CommandCat to avoid reading huge files all at once.")]
    public string CommandGetLineCount(
    [Description("Absolute or relative file path to check")] string filePath,
    [Description("Working directory (optional)")] string workingDirectory = "")
    {
        var display = $"wc -l {filePath}";

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
                        ctx.WriteStderrLine($"Error: File not found: {full}");
                        return 2;
                    }

                    int lineCount = 0;

                    // 使用与 CommandCat 相同的高效异步流读取，不把整个文件加载进内存
                    await using var fs = new FileStream(
                        full,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan
                    );

                    using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                    // 逐行快速统计，不保留字符串内容
                    while (await sr.ReadLineAsync().ConfigureAwait(false) != null)
                    {
                        ct.ThrowIfCancellationRequested();
                        lineCount++;
                    }

                    // 给出清晰、结构化的输出
                    ctx.WriteStdoutLine($"File: {Path.GetFileName(full)}");
                    ctx.WriteStdoutLine($"Total Lines: {lineCount}");

                    // 附带一个贴心的提示，引导 LLM 进行下一步决策
                    if (lineCount > 500)
                    {
                        ctx.WriteStdoutLine($"Tip: This is a large file. Please use CommandCat with 'startLine' and 'endLine' to read it in smaller chunks.");
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
                    ctx.WriteStderrLine($"Error ({ex.GetType().Name}): {ex.Message}");
                    return 1;
                }
            },
            runInBackground: false,
            timeoutMs: 0
        );
    }

    [Description("Read file contents with pagination and line numbers. BEST PRACTICE: For unknown files, use CommandGetLineCount first to check file size. Then use startLine and endLine here to read specific blocks of code.")]
    public string CommandCat(
    [Description("Absolute or relative file path to read")] string filePath,
    [Description("Starting line number (1-based, default: 1)")] int startLine = 1,
    [Description("Ending line number (inclusive). Specify this to read a precise chunk! Leave as -1 to read up to max limit.")] int endLine = -1,
    [Description("Working directory (optional)")] string workingDirectory = "")
    {
        var display = endLine > 0
            ? $"cat {filePath} --lines {startLine}-{endLine}"
            : $"cat {filePath} --from-line {startLine}";

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
                        ctx.WriteStderrLine($"Error: File not found: {full}");
                        return 2;
                    }

                    if (startLine < 1) startLine = 1;

                    int maxLinesToRead = CatReadPageLines; // 默认分页大小 (例如 500)
                    bool isCapped = false;

                    // 动态防御：如果 LLM 贪心请求了过大的范围（比如 1 到 10000），强制截断并警告
                    if (endLine > 0 && endLine >= startLine)
                    {
                        int requestedLines = endLine - startLine + 1;
                        if (requestedLines <= maxLinesToRead)
                        {
                            maxLinesToRead = requestedLines;
                        }
                        else
                        {
                            isCapped = true; // 标记请求被截断
                        }
                    }

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

                    while (currentLineNo < startLine - 1)
                    {
                        ct.ThrowIfCancellationRequested();
                        var skip = await sr.ReadLineAsync().ConfigureAwait(false);
                        if (skip == null)
                        {
                            ctx.WriteStderrLine($"Warning: Start line {startLine} exceeds file length ({currentLineNo} lines). Tip: Use CommandGetLineCount to check the actual file size.");
                            return 0;
                        }
                        currentLineNo++;
                    }

                    ctx.WriteStdoutLine($"--- File: {Path.GetFileName(full)} (Reading from line {startLine}) ---");

                    int emitted = 0;
                    while (emitted < maxLinesToRead)
                    {
                        ct.ThrowIfCancellationRequested();
                        var line = await sr.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;

                        currentLineNo++;
                        ctx.WriteStdoutLine($"{currentLineNo,4} | {line}");
                        emitted++;
                    }

                    ct.ThrowIfCancellationRequested();

                    var lookahead = await sr.ReadLineAsync().ConfigureAwait(false);

                    ctx.WriteStdoutLine("--- End of Read ---");

                    // 动态引导逻辑：根据不同的情况给予 LLM 不同的下一步建议
                    if (isCapped)
                    {
                        ctx.WriteStdoutLine($"\n[System Warning]: Requested line range was too large and has been capped at {CatReadPageLines} lines to save context limit.");
                        ctx.WriteStdoutLine($"👉 Tip: Use CommandGetLineCount to understand the file structure, then use CommandCat with precise startLine and endLine.");
                    }
                    else if (lookahead != null && (endLine <= 0 || currentLineNo < endLine))
                    {
                        ctx.WriteStdoutLine($"\n--more-- (File continues below line {currentLineNo})");
                        ctx.WriteStdoutLine($"👉 Tip: To see the rest, use CommandCat with startLine={currentLineNo + 1}. Unsure how big the file is? Use CommandGetLineCount first!");
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
                    ctx.WriteStderrLine($"Error ({ex.GetType().Name}): {ex.Message}");
                    return 1;
                }
            },
            runInBackground: false,
            timeoutMs: 0
        );
    }

    [Description("Edit text file with insert/replace/delete/append operations. BEST PRACTICE: ALWAYS use CommandCat to check line numbers before editing!")]
    public string CommandEditText(
    [Description("Absolute or relative file path to edit")] string filePath,
    [Description("Starting line number (1-based). Where to start replacing/deleting, or where to insert BEFORE.")] int line = 1,
    [Description("Edit mode: 'insert' (before line), 'replace' (lines to endLine), 'delete' (lines to endLine), 'append' (at EOF)")] string mode = "insert",
    [Description("Ending line number (inclusive) for replace/delete operations. E.g., line=10, endLine=12 deletes/replaces lines 10, 11, and 12.")] int endLine = -1,
    [Description("Text content to insert/replace/append. (Not needed for delete)")] string text = "",
    [Description("Working directory (optional)")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory!;

            var full = Path.GetFullPath(filePath, baseDir);

            if (!File.Exists(full))
                return $"Error: File not found: {full}\n💡 TIP: Use FindFiles to check the correct file path.";

            if (line < 1) line = 1;
            // 如果没有提供 endLine，默认只操作 startLine 这一行
            if (endLine < 1) endLine = line;

            var modeLower = (mode ?? "insert").Trim().ToLowerInvariant();

            if ((modeLower == "insert" || modeLower == "replace" || modeLower == "append") && string.IsNullOrEmpty(text))
                return $"Error: 'text' parameter is required for mode '{modeLower}'.";

            var lines = new List<string>();
            Encoding encoding;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                string? l;
                while ((l = sr.ReadLine()) != null) lines.Add(l);
                encoding = sr.CurrentEncoding;
            }

            // 假设 ToLinesFromEscaped 是当前类中已有的方法
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
                        return $"Error: Start line {line} is out of range. The file only has {lines.Count} lines.";

                    // 计算需要替换的行数 (endLine 包含在内)
                    int linesToReplace = endLine - line + 1;
                    if (linesToReplace < 1) return $"Error: endLine ({endLine}) cannot be less than start line ({line}).";

                    removed = Math.Min(linesToReplace, lines.Count - idx);
                    lines.RemoveRange(idx, removed);
                    lines.InsertRange(idx, newLines);
                    inserted = newLines.Count;
                    break;

                case "delete":
                    if (idx < 0 || idx >= lines.Count)
                        return $"Error: Start line {line} is out of range. The file only has {lines.Count} lines.";

                    int linesToDelete = endLine - line + 1;
                    if (linesToDelete < 1) return $"Error: endLine ({endLine}) cannot be less than start line ({line}).";

                    removed = Math.Min(linesToDelete, lines.Count - idx);
                    lines.RemoveRange(idx, removed);
                    break;

                default:
                    return $"Error: Unknown mode '{modeLower}'. Valid modes are: insert, replace, delete, append.";
            }

            // 写入文件
            var tmp = full + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, string.Join(Environment.NewLine, lines), encoding);
            File.Move(tmp, full, overwrite: true);

            // --- 构建包含代码预览的纯文本输出 ---
            var sb = new StringBuilder();
            sb.AppendLine($"--- Edit Successful ---");
            sb.AppendLine($"File: {full}");
            sb.AppendLine($"Mode: {modeLower}");

            if (modeLower == "replace" || modeLower == "delete")
                sb.AppendLine($"Lines Removed: {removed} (Line {line} to {line + removed - 1})");

            if (modeLower == "insert" || modeLower == "replace" || modeLower == "append")
                sb.AppendLine($"Lines Inserted: {inserted}");

            sb.AppendLine("\n[Preview of changes]:");

            // 智能截取修改位置的上下文（上下各多看 2 行）
            int previewStartIdx = Math.Max(0, idx - 2);
            int previewEndIdx = Math.Min(lines.Count - 1, idx + inserted + 1);

            for (int i = previewStartIdx; i <= previewEndIdx; i++)
            {
                // 如果是刚才插入的行，在前面加一个 '+' 号给予强烈视觉提示
                bool isNew = (modeLower != "delete") && (i >= idx && i < idx + inserted);
                string marker = isNew ? "+" : " ";
                sb.AppendLine($"{marker} {i + 1,4} | {lines[i]}");
            }

            sb.AppendLine("-----------------------");
            sb.AppendLine("💡 TIP: Verify the preview above. If it's wrong, you can immediately use CommandEditText to fix it.");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [Description("Create a directory (recursively creates parent directories).")]
    public string CommandMkdir(
    [Description("Directory path to create")] string path,
    [Description("Ignore if directory already exists")] bool existOk = true, // 将默认值改为 true 对 LLM 更友好
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
                    return $"✅ Success: Directory already exists (ignored): {full}";

                return $"❌ Error: Directory already exists: {full}\n💡 TIP: Set 'existOk' to true to ignore this error.";
            }

            Directory.CreateDirectory(full);

            return $"✅ Success: Directory created at: {full}";
        }
        catch (Exception ex)
        {
            return $"❌ Error ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [Description("Delete files or directories. BEST PRACTICE: Double check the path before deleting!")]
    public string CommandDelete(
    [Description("Path to delete")] string path,
    [Description("Allow recursive directory deletion (required for non-empty directories)")] bool recursive = false,
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
                    return $"✅ Success: Path did not exist (ignored due to force=true): {full}";
                }

                return $"❌ Error: Path not found: {full}\n💡 TIP: Use FindFiles to verify the exact name/path, or set force=true to ignore.";
            }

            if (File.Exists(full))
            {
                if (force)
                {
                    try
                    {
                        var attrs = File.GetAttributes(full);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(full, attrs & ~FileAttributes.ReadOnly);
                    }
                    catch { /* ignore */ }
                }

                File.Delete(full);
                return $"✅ Success: File deleted: {full}";
            }

            // 走到这里说明是目录
            if (!recursive)
            {
                return $"❌ Error: Target is a directory: {full}\n💡 TIP: You MUST set 'recursive' to true to delete a directory and all its contents.";
            }

            Directory.Delete(full, recursive: true);
            return $"✅ Success: Directory recursively deleted: {full}";
        }
        catch (Exception ex)
        {
            return $"❌ Error ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [Description("Rename or move a file. Automatically creates destination parent directories if they don't exist.")]
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
                    return $"❌ Error: Source is a directory, but file was expected: {srcFull}";

                return $"❌ Error: Source file not found: {srcFull}\n💡 TIP: Use FindFiles to verify the source file exists.";
            }

            if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
            {
                return $"✅ Success: Source and destination are the same (no changes made): {srcFull}";
            }

            // 自动创建目标文件夹，防止 LLM 在 move 之前忘记调用 Mkdir 导致报错
            var dstDir = Path.GetDirectoryName(dstFull);
            if (!string.IsNullOrWhiteSpace(dstDir))
                Directory.CreateDirectory(dstDir);

            var dstExists = File.Exists(dstFull);
            if (dstExists && !overwrite)
            {
                return $"❌ Error: Destination file already exists: {dstFull}\n💡 TIP: Set 'overwrite' to true if you want to replace it.";
            }

            File.Move(srcFull, dstFull, overwrite);

            var sb = new StringBuilder();
            sb.AppendLine($"✅ Success: File {(dstExists ? "overwritten" : "moved/renamed")}");
            sb.AppendLine($"   From: {srcFull}");
            sb.AppendLine($"   To:   {dstFull}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"❌ Error ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [Description("Create a new text file (UTF-8 encoding). Overwrites if file exists (when force=true).")]
    public string CommandCreateText(
    [Description("File path to create")] string filePath,
    [Description("File content to write")] string content = "",
    [Description("Overwrite if file already exists")] bool force = false,
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

            bool existed = File.Exists(full);
            if (existed && !force)
                return $"❌ Error: File already exists: {full}\n💡 TIP: Use 'force=true' to overwrite, or use 'AppendToFile' to add content.";

            content = content ?? string.Empty;
            File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return $"✅ Success: File {(existed ? "overwritten" : "created")} at {full} ({content.Length} characters written).";
        }
        catch (Exception ex)
        {
            return $"❌ Error ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [Description("Append text to the end of a file (creates file if it doesn't exist).")]
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

            return $"✅ Success: Appended {content.Length} characters to {full}\n💡 TIP: Use CommandCat to verify the updated file content if needed.";
        }
        catch (Exception ex)
        {
            return $"❌ Error ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [Description("Check if a file or directory exists.")]
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

            if (File.Exists(full)) return $"✅ Exists: [File] {full}";
            if (Directory.Exists(full)) return $"✅ Exists: [Directory] {full}";

            return $"❌ Not Found: Neither file nor directory exists at {full}";
        }
        catch (Exception ex)
        {
            return $"❌ Error ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [Description("Get detailed file or directory metadata (size, timestamps, attributes).")]
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
            var sb = new StringBuilder();

            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                sb.AppendLine($"--- File Information ---");
                sb.AppendLine($"Path: {full}");
                sb.AppendLine($"Type: File ({fi.Extension})");
                // 友好的文件大小展示
                sb.AppendLine($"Size: {fi.Length} bytes ({(fi.Length / 1024.0):F2} KB)");
                sb.AppendLine($"Created:  {fi.CreationTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"Modified: {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"Attributes: {fi.Attributes}");
                sb.AppendLine($"Read-Only: {fi.IsReadOnly}");
                return sb.ToString();
            }
            else if (Directory.Exists(full))
            {
                var di = new DirectoryInfo(full);
                sb.AppendLine($"--- Directory Information ---");
                sb.AppendLine($"Path: {full}");
                sb.AppendLine($"Type: Directory");
                sb.AppendLine($"Created:  {di.CreationTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"Modified: {di.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"Attributes: {di.Attributes}");
                return sb.ToString();
            }

            return $"❌ Not Found: {full}";
        }
        catch (Exception ex)
        {
            return $"❌ Error ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [Description("List directory contents. BEST PRACTICE: Use this to explore project structure. Output is limited to prevent context overflow.")]
    public string CommandDir(
    [Description("Path to list (defaults to current directory)")] string path = "",
    [Description("Working directory (optional)")] string workingDirectory = "",
    [Description("Recursively list all subdirectories (Warning: use carefully on large directories)")] bool recursive = false)
    {
        var display = "dir " + (string.IsNullOrWhiteSpace(path) ? "." : path) + (recursive ? " -r" : "");

        // 目录查询极快，建议 runInBackground 设为 false，确保 LLM 立即拿到结果
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
                        ctx.WriteStderrLine($"❌ Error: Directory not found: {full}");
                        return 2;
                    }

                    var root = new DirectoryInfo(full);

                    ctx.WriteStdoutLine($"--- Directory Listing ---");
                    ctx.WriteStdoutLine($"📁 Path: {root.FullName}");
                    ctx.WriteStdoutLine($"=========================");

                    // 防爆栈机制：限制最大输出条目数
                    int maxEntries = 300;
                    int entryCount = 0;
                    bool isTruncated = false;

                    // 干扰目录黑名单
                    string[] ignoredDirs = { ".git", "node_modules", "bin", "obj", ".vs", "dist", "out" };

                    void SortEntries(FileSystemInfo[] entries)
                    {
                        Array.Sort(entries, (a, b) =>
                        {
                            var ad = a is DirectoryInfo;
                            var bd = b is DirectoryInfo;
                            if (ad != bd) return ad ? -1 : 1; // 文件夹排在前面
                            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                        });
                    }

                    void Traverse(DirectoryInfo dir, int depth)
                    {
                        if (isTruncated) return;
                        ct.ThrowIfCancellationRequested();

                        FileSystemInfo[] entries;
                        try
                        {
                            entries = dir.GetFileSystemInfos();
                        }
                        catch (Exception ex)
                        {
                            ctx.WriteStderrLine($"  [Access Denied]: {dir.Name} ({ex.Message})");
                            return;
                        }

                        SortEntries(entries);

                        foreach (var e in entries)
                        {
                            if (isTruncated) break;
                            ct.ThrowIfCancellationRequested();

                            string indent = new string(' ', depth * 4);
                            bool isDir = (e.Attributes & FileAttributes.Directory) != 0;

                            // 递归模式下跳过黑名单目录
                            if (recursive && isDir && ignoredDirs.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                            {
                                ctx.WriteStdoutLine($"{indent}📁 {e.Name}/ (Ignored)");
                                entryCount++;
                                continue;
                            }

                            if (isDir)
                            {
                                ctx.WriteStdoutLine($"{indent}📁 {e.Name}/");
                                entryCount++;

                                if (recursive)
                                {
                                    var isReparsePoint = (e.Attributes & FileAttributes.ReparsePoint) != 0;
                                    if (!isReparsePoint) // 防止符号链接死循环
                                    {
                                        Traverse((DirectoryInfo)e, depth + 1);
                                    }
                                }
                            }
                            else
                            {
                                // 文件
                                long size = 0;
                                try { size = (e as FileInfo)?.Length ?? 0; } catch { }
                                string sizeStr = size > 1024 * 1024 ? $"{(size / 1048576.0):F1} MB" :
                                                 size > 1024 ? $"{(size / 1024.0):F1} KB" : $"{size} B";

                                ctx.WriteStdoutLine($"{indent}📄 {e.Name,-30} [{sizeStr}]");
                                entryCount++;
                            }

                            if (entryCount >= maxEntries)
                            {
                                isTruncated = true;
                            }
                        }
                    }

                    Traverse(root, 0);

                    ctx.WriteStdoutLine($"=========================");
                    if (isTruncated)
                    {
                        ctx.WriteStdoutLine($"⚠️ WARNING: Output truncated at {maxEntries} entries to prevent context overflow.");
                        ctx.WriteStdoutLine($"💡 TIP: If you need to search for a specific file, use 'FindFiles' instead of exploring manually.");
                    }
                    else
                    {
                        ctx.WriteStdoutLine($"Total entries: {entryCount}");
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
                    ctx.WriteStderrLine($"❌ Error ({ex.GetType().Name}): {ex.Message}");
                    return 1;
                }
            },
            runInBackground: false, // 设为 false 等待执行完毕
            timeoutMs: 30000
        );
    }

    private static List<string> ToLinesFromEscaped(string? textEscaped)
    {
        var text = textEscaped ?? string.Empty;
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        if (text.Length == 0) return new List<string>();

        return text.Split('\n', StringSplitOptions.None).ToList();
    }
}
