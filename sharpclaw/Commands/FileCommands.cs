using sharpclaw.Core;
using sharpclaw.Core.TaskManagement;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace sharpclaw.Commands;

/// <summary>
/// File system operation commands.
/// </summary>
public class FileCommands : CommandBase
{
    private const int CatReadPageLines = 2000;

    public FileCommands(TaskManager taskManager, IAgentContext agentContext)
        : base(taskManager, agentContext)
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
                        ? GetDefaultWorkspace()
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
                        ? GetDefaultWorkspace()
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
                        ? GetDefaultWorkspace()
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
                        ? GetDefaultWorkspace()
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
                ? GetDefaultWorkspace()
                : workingDirectory!;

            var full = Path.GetFullPath(filePath, baseDir);

            if (!File.Exists(full))
                return $"❌ Error: File not found: {full}\n💡 TIP: Use FindFiles to check the correct file path.";

            if (line < 1) line = 1;
            if (endLine < 1) endLine = line;

            var modeLower = (mode ?? "insert").Trim().ToLowerInvariant();

            if ((modeLower == "insert" || modeLower == "replace" || modeLower == "append") && string.IsNullOrEmpty(text))
                return $"❌ Error: 'text' parameter is required for mode '{modeLower}'.";

            var lines = new List<string>();
            Encoding encoding;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                string? l;
                while ((l = sr.ReadLine()) != null) lines.Add(l);
                encoding = sr.CurrentEncoding;
            }

            // 保存一份修改前的副本，用于后续生成精准的 Diff
            var originalLines = new List<string>(lines);

            // 处理输入文本的多行分割 (兼容各种换行符)
            var newLines = string.IsNullOrEmpty(text)
                ? new List<string>()
                : text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();

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
                        return $"❌ Error: Start line {line} is out of range. The file only has {lines.Count} lines.";

                    int linesToReplace = endLine - line + 1;
                    if (linesToReplace < 1) return $"❌ Error: endLine ({endLine}) cannot be less than start line ({line}).";

                    removed = Math.Min(linesToReplace, lines.Count - idx);
                    lines.RemoveRange(idx, removed);
                    lines.InsertRange(idx, newLines);
                    inserted = newLines.Count;
                    break;

                case "delete":
                    if (idx < 0 || idx >= lines.Count)
                        return $"❌ Error: Start line {line} is out of range. The file only has {lines.Count} lines.";

                    int linesToDelete = endLine - line + 1;
                    if (linesToDelete < 1) return $"❌ Error: endLine ({endLine}) cannot be less than start line ({line}).";

                    removed = Math.Min(linesToDelete, lines.Count - idx);
                    lines.RemoveRange(idx, removed);
                    break;

                default:
                    return $"❌ Error: Unknown mode '{modeLower}'. Valid modes are: insert, replace, delete, append.";
            }

            var tmp = full + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, string.Join(Environment.NewLine, lines), encoding);
            File.Move(tmp, full, overwrite: true);

            // ==========================================
            // 构建 Git 风格的 Diff 输出 (核心魔法)
            // ==========================================
            var sb = new StringBuilder();
            sb.AppendLine($"✅ --- Edit Successful ---");
            sb.AppendLine($"File: {full}");
            sb.AppendLine($"Mode: {modeLower}");

            if (modeLower == "replace" || modeLower == "delete")
                sb.AppendLine($"Lines Removed: {removed} (Line {line} to {line + removed - 1})");

            if (modeLower == "insert" || modeLower == "replace" || modeLower == "append")
                sb.AppendLine($"Lines Inserted: {inserted}");

            sb.AppendLine("\n[Diff Preview]:");
            sb.AppendLine("```diff");
            sb.AppendLine($"--- a/{Path.GetFileName(full)}");
            sb.AppendLine($"+++ b/{Path.GetFileName(full)}");

            int contextLines = 3; // 显示修改处上下 3 行的上下文

            // 1. 打印上半部分的上下文 (保留原行号)
            int topContextStart = Math.Max(0, idx - contextLines);
            for (int i = topContextStart; i < idx; i++)
            {
                sb.AppendLine($"   {i + 1,4} | {originalLines[i]}");
            }

            // 2. 打印被删除的行 (标记为 '-'，使用旧行号)
            for (int i = 0; i < removed; i++)
            {
                sb.AppendLine($"-  {idx + i + 1,4} | {originalLines[idx + i]}");
            }

            // 3. 打印新增的行 (标记为 '+'，使用新行号)
            for (int i = 0; i < newLines.Count; i++)
            {
                sb.AppendLine($"+  {idx + i + 1,4} | {newLines[i]}");
            }

            // 4. 打印下半部分的上下文 (使用新文件的行号)
            int bottomContextStartOld = idx + removed;
            int bottomContextStartNew = idx + inserted;
            int bottomContextCount = Math.Min(originalLines.Count - bottomContextStartOld, contextLines);

            for (int i = 0; i < bottomContextCount; i++)
            {
                sb.AppendLine($"   {bottomContextStartNew + i + 1,4} | {originalLines[bottomContextStartOld + i]}");
            }

            sb.AppendLine("```");
            sb.AppendLine("💡 TIP: Verify the diff preview above. If the indentation is wrong or lines are messed up, use CommandEditText again to fix it immediately.");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"❌ Error ({ex.GetType().Name}): {ex.Message}";
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
                ? GetDefaultWorkspace()
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
                ? GetDefaultWorkspace()
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
                ? GetDefaultWorkspace()
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
                ? GetDefaultWorkspace()
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
                ? GetDefaultWorkspace()
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
                ? GetDefaultWorkspace()
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
                ? GetDefaultWorkspace()
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
                        ? GetDefaultWorkspace()
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
                        if (recursive)
                        {
                            ctx.WriteStdoutLine($"💡 TIP: The directory is too large for recursive listing. Please explore layer by layer (recursive=false) or use 'FindFiles' to locate specific files.");
                        }
                        else
                        {
                            ctx.WriteStdoutLine($"💡 TIP: If you need to search for a specific file, use 'FindFiles' instead of exploring manually.");
                        }
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

    // =========================================================================
    // 以下为基于 Claude Code 工具范式实现的新命令
    // =========================================================================

    [Description("通过精确文本匹配来编辑文件。提供要替换的原始文本(oldString)和新文本(newString)。" +
        "比行号编辑更可靠，因为不需要计算行号。" +
        "重要：oldString 必须与文件中的内容完全匹配（包括空白和缩进）。" +
        "如果 oldString 在文件中不唯一，需要提供更长的上下文使其唯一，或使用 replaceAll=true 替换所有匹配。")]
    public string EditByMatch(
        [Description("要编辑的文件路径")] string filePath,
        [Description("要查找并替换的原始文本（必须与文件内容完全匹配，包括缩进和空白）")] string oldString,
        [Description("替换后的新文本（必须与 oldString 不同）")] string newString,
        [Description("是否替换所有匹配项（默认 false，仅替换第一个匹配；如果存在多个匹配且为 false 则报错）")] bool replaceAll = false,
        [Description("工作目录（可选）")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? GetDefaultWorkspace()
                : workingDirectory!;

            var full = Path.GetFullPath(filePath, baseDir);

            if (!File.Exists(full))
                return $"❌ Error: File not found: {full}\n💡 TIP: Use ReadFile or GlobFiles to verify the file path.";

            if (string.IsNullOrEmpty(oldString))
                return "❌ Error: oldString cannot be empty. Provide the exact text to find and replace.";

            if (oldString == newString)
                return "❌ Error: oldString and newString are identical. No changes needed.";

            // 读取文件，保留原始编码
            string originalContent;
            Encoding encoding;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                originalContent = sr.ReadToEnd();
                encoding = sr.CurrentEncoding;
            }

            // 检测文件的原始换行符风格
            string lineEnding = originalContent.Contains("\r\n") ? "\r\n" : "\n";

            // 将文件内容和 oldString/newString 归一化为 \n 进行匹配
            string normalizedContent = originalContent.Replace("\r\n", "\n").Replace("\r", "\n");
            string normalizedOld = oldString.Replace("\r\n", "\n").Replace("\r", "\n");
            string normalizedNew = newString.Replace("\r\n", "\n").Replace("\r", "\n");

            // 计算匹配次数
            int matchCount = 0;
            int searchStart = 0;
            var matchPositions = new List<int>();
            while (true)
            {
                int pos = normalizedContent.IndexOf(normalizedOld, searchStart, StringComparison.Ordinal);
                if (pos < 0) break;
                matchPositions.Add(pos);
                matchCount++;
                searchStart = pos + normalizedOld.Length;
            }

            if (matchCount == 0)
                return $"❌ Error: oldString not found in file.\nThe provided text does not match any content in {Path.GetFileName(full)}.\n💡 TIP: Use ReadFile to re-read the file and copy the exact text (including whitespace and indentation).";

            if (matchCount > 1 && !replaceAll)
                return $"❌ Error: Found {matchCount} matches for oldString. Either:\n  1. Set replaceAll=true to replace all occurrences, or\n  2. Provide a longer/more specific oldString that matches uniquely.";

            // 执行替换
            string newContent;
            int replacedCount;
            if (replaceAll)
            {
                newContent = normalizedContent.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal);
                replacedCount = matchCount;
            }
            else
            {
                int firstPos = matchPositions[0];
                newContent = string.Concat(
                    normalizedContent.AsSpan(0, firstPos),
                    normalizedNew,
                    normalizedContent.AsSpan(firstPos + normalizedOld.Length));
                replacedCount = 1;
            }

            // 还原到文件的原始换行符风格
            if (lineEnding == "\r\n")
                newContent = newContent.Replace("\n", "\r\n");

            // 原子写入
            var tmp = full + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, newContent, encoding);
            File.Move(tmp, full, overwrite: true);

            // 生成 diff 预览
            var sb = new StringBuilder();
            sb.AppendLine($"✅ --- Edit Successful ---");
            sb.AppendLine($"File: {full}");
            sb.AppendLine($"Matches replaced: {replacedCount}");

            // 为每个替换位置生成 diff hunk（最多展示 3 个）
            var originalLines = normalizedContent.Split('\n');
            var newLines = newContent.Replace("\r\n", "\n").Split('\n');
            var oldStringLines = normalizedOld.Split('\n');
            var newStringLines = normalizedNew.Split('\n');

            sb.AppendLine("\n[Diff Preview]:");
            sb.AppendLine("```diff");
            sb.AppendLine($"--- a/{Path.GetFileName(full)}");
            sb.AppendLine($"+++ b/{Path.GetFileName(full)}");

            int hunksToShow = Math.Min(replacedCount, 3);
            for (int h = 0; h < hunksToShow; h++)
            {
                if (h > 0) sb.AppendLine("...");

                // 计算匹配起始行号
                int charsBefore = matchPositions[h];
                int matchStartLine = normalizedContent.AsSpan(0, charsBefore).Count('\n');

                int contextSize = 3;
                int ctxStart = Math.Max(0, matchStartLine - contextSize);
                int ctxEndOld = Math.Min(originalLines.Length - 1, matchStartLine + oldStringLines.Length - 1 + contextSize);

                // 上下文行（修改前）
                for (int i = ctxStart; i < matchStartLine; i++)
                    sb.AppendLine($"   {i + 1,4} | {originalLines[i]}");

                // 被删除的行
                for (int i = 0; i < oldStringLines.Length; i++)
                    sb.AppendLine($"-  {matchStartLine + i + 1,4} | {oldStringLines[i]}");

                // 新增的行
                for (int i = 0; i < newStringLines.Length; i++)
                    sb.AppendLine($"+  {matchStartLine + i + 1,4} | {newStringLines[i]}");

                // 下方上下文行
                int afterStart = matchStartLine + oldStringLines.Length;
                int afterEnd = Math.Min(originalLines.Length, afterStart + contextSize);
                for (int i = afterStart; i < afterEnd; i++)
                    sb.AppendLine($"   {i + 1,4} | {originalLines[i]}");
            }

            if (replacedCount > 3)
                sb.AppendLine($"... ({replacedCount - 3} more replacements not shown)");

            sb.AppendLine("```");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"❌ Error ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [Description("高级内容搜索工具（类似 ripgrep）。支持正则表达式、上下文行显示、多种输出模式和文件类型过滤。" +
        "输出模式：'content' 显示匹配行内容（支持上下文行），'files_with_matches' 仅显示文件路径（默认），'count' 显示匹配计数。")]
    public string Grep(
        [Description("搜索的正则表达式模式")] string pattern,
        [Description("搜索路径（文件或目录，默认为工作目录）")] string path = "",
        [Description("Glob 过滤器，限定搜索的文件（例如 '*.cs', '*.{ts,tsx}'）")] string glob = "",
        [Description("按文件类型过滤（例如 'cs', 'js', 'py', 'java', 'go', 'rust', 'ts', 'xml', 'json', 'md', 'html', 'css', 'sql'）")] string type = "",
        [Description("输出模式：'content'（显示匹配行）, 'files_with_matches'（仅文件路径，默认）, 'count'（匹配计数）")] string outputMode = "files_with_matches",
        [Description("匹配行之后显示的上下文行数")] int afterContext = 0,
        [Description("匹配行之前显示的上下文行数")] int beforeContext = 0,
        [Description("匹配行前后显示的上下文行数（同时设置 before 和 after）")] int context = 0,
        [Description("显示行号（仅 content 模式有效，默认 true）")] bool showLineNumbers = true,
        [Description("大小写不敏感搜索")] bool ignoreCase = false,
        [Description("限制输出的最大条目数（0 表示不限制）")] int headLimit = 0,
        [Description("跳过前 N 个条目（用于分页）")] int offset = 0,
        [Description("多行模式（. 匹配换行符，模式可跨行匹配）")] bool multiline = false,
        [Description("工作目录（可选）")] string workingDirectory = "")
    {
        return RunNative(
            displayCommand: $"grep '{pattern}'",
            runner: async (ctx, ct) =>
            {
                try
                {
                    await Task.Yield();

                    var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                        ? GetDefaultWorkspace()
                        : workingDirectory!;

                    var searchPath = string.IsNullOrWhiteSpace(path)
                        ? baseDir
                        : Path.GetFullPath(path, baseDir);

                    // 解析上下文参数
                    int effectiveBefore = context > 0 ? context : beforeContext;
                    int effectiveAfter = context > 0 ? context : afterContext;

                    // 编译正则
                    var regexOptions = RegexOptions.Compiled;
                    if (ignoreCase) regexOptions |= RegexOptions.IgnoreCase;
                    if (multiline) regexOptions |= RegexOptions.Singleline; // . matches \n

                    Regex regex;
                    try { regex = new Regex(pattern, regexOptions); }
                    catch (RegexParseException ex)
                    {
                        ctx.WriteStderrLine($"❌ Error: Invalid regex pattern: {ex.Message}");
                        return 1;
                    }

                    // 类型到 glob 映射
                    var typeExtMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["cs"] = ["*.cs"],
                        ["js"] = ["*.js", "*.mjs", "*.cjs"],
                        ["ts"] = ["*.ts", "*.tsx"],
                        ["py"] = ["*.py"],
                        ["java"] = ["*.java"],
                        ["go"] = ["*.go"],
                        ["rust"] = ["*.rs"],
                        ["xml"] = ["*.xml"],
                        ["json"] = ["*.json"],
                        ["md"] = ["*.md"],
                        ["html"] = ["*.html", "*.htm"],
                        ["css"] = ["*.css", "*.scss", "*.less"],
                        ["sql"] = ["*.sql"],
                        ["yaml"] = ["*.yaml", "*.yml"],
                        ["toml"] = ["*.toml"],
                        ["sh"] = ["*.sh", "*.bash"],
                    };

                    // 确定搜索文件列表
                    var filesToSearch = new List<string>();

                    if (File.Exists(searchPath))
                    {
                        // 单文件搜索
                        filesToSearch.Add(searchPath);
                    }
                    else if (Directory.Exists(searchPath))
                    {
                        // 确定文件过滤模式
                        var patterns = new List<string>();
                        if (!string.IsNullOrWhiteSpace(type) && typeExtMap.TryGetValue(type.Trim(), out var exts))
                            patterns.AddRange(exts);
                        else if (!string.IsNullOrWhiteSpace(glob))
                            patterns.Add(glob);
                        else
                            patterns.Add("*");

                        string[] ignoredDirs = [
                            $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                            $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}"
                        ];

                        var enumOpts = new EnumerationOptions
                        {
                            IgnoreInaccessible = true,
                            RecurseSubdirectories = true,
                            MatchCasing = MatchCasing.PlatformDefault
                        };

                        foreach (var p in patterns)
                        {
                            try
                            {
                                foreach (var f in Directory.EnumerateFiles(searchPath, p, enumOpts))
                                {
                                    ct.ThrowIfCancellationRequested();
                                    if (ignoredDirs.Any(d => f.Contains(d, StringComparison.OrdinalIgnoreCase)))
                                        continue;
                                    filesToSearch.Add(f);
                                }
                            }
                            catch (Exception ex)
                            {
                                ctx.WriteStderrLine($"Warning: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        ctx.WriteStderrLine($"❌ Error: Path not found: {searchPath}");
                        return 2;
                    }

                    var mode = (outputMode ?? "files_with_matches").Trim().ToLowerInvariant();
                    var sb = new StringBuilder();
                    int entryCount = 0;
                    int skipped = 0;

                    if (mode == "files_with_matches")
                    {
                        foreach (var file in filesToSearch)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                bool hasMatch;
                                if (multiline)
                                {
                                    var content = await File.ReadAllTextAsync(file, ct);
                                    hasMatch = regex.IsMatch(content);
                                }
                                else
                                {
                                    hasMatch = false;
                                    foreach (var line in File.ReadLines(file))
                                    {
                                        if (regex.IsMatch(line)) { hasMatch = true; break; }
                                    }
                                }

                                if (hasMatch)
                                {
                                    if (skipped < offset) { skipped++; continue; }
                                    sb.AppendLine(file);
                                    entryCount++;
                                    if (headLimit > 0 && entryCount >= headLimit) break;
                                }
                            }
                            catch { /* skip unreadable files */ }
                        }
                    }
                    else if (mode == "count")
                    {
                        foreach (var file in filesToSearch)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                int count = 0;
                                if (multiline)
                                {
                                    var content = await File.ReadAllTextAsync(file, ct);
                                    count = regex.Matches(content).Count;
                                }
                                else
                                {
                                    foreach (var line in File.ReadLines(file))
                                        if (regex.IsMatch(line)) count++;
                                }

                                if (count > 0)
                                {
                                    if (skipped < offset) { skipped++; continue; }
                                    sb.AppendLine($"{file}:{count}");
                                    entryCount++;
                                    if (headLimit > 0 && entryCount >= headLimit) break;
                                }
                            }
                            catch { /* skip */ }
                        }
                    }
                    else // content mode
                    {
                        foreach (var file in filesToSearch)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                var allLines = File.ReadAllLines(file);
                                var matchLineNums = new List<int>(); // 0-based

                                for (int i = 0; i < allLines.Length; i++)
                                    if (regex.IsMatch(allLines[i]))
                                        matchLineNums.Add(i);

                                if (matchLineNums.Count == 0) continue;

                                if (skipped < offset) { skipped++; continue; }

                                // 合并上下文区间
                                var ranges = new List<(int Start, int End)>();
                                foreach (var m in matchLineNums)
                                {
                                    int s = Math.Max(0, m - effectiveBefore);
                                    int e = Math.Min(allLines.Length - 1, m + effectiveAfter);
                                    if (ranges.Count > 0 && s <= ranges[^1].End + 1)
                                        ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, e));
                                    else
                                        ranges.Add((s, e));
                                }

                                var matchSet = new HashSet<int>(matchLineNums);

                                sb.AppendLine($"\n📄 {file}");
                                for (int r = 0; r < ranges.Count; r++)
                                {
                                    if (r > 0) sb.AppendLine("--");
                                    var (start, end) = ranges[r];
                                    for (int i = start; i <= end; i++)
                                    {
                                        string prefix = matchSet.Contains(i) ? ">" : " ";
                                        if (showLineNumbers)
                                            sb.AppendLine($"{prefix} {i + 1,4} | {allLines[i]}");
                                        else
                                            sb.AppendLine($"{prefix} {allLines[i]}");
                                    }
                                }

                                entryCount++;
                                if (headLimit > 0 && entryCount >= headLimit) break;
                            }
                            catch { /* skip */ }
                        }
                    }

                    ctx.WriteStdoutLine(sb.ToString());
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
            runInBackground: false,
            timeoutMs: 60000
        );
    }

    [Description("快速文件模式匹配工具。支持 glob 模式（如 '**/*.cs'、'src/**/*.ts'）。" +
        "结果按修改时间排序（最近修改的排在前面），适合快速定位最近编辑过的文件。")]
    public string GlobFiles(
        [Description("Glob 模式（例如 '**/*.cs', 'src/**/*.ts', '*.json'）")] string pattern,
        [Description("搜索目录（默认为工作目录）")] string path = "",
        [Description("工作目录（可选）")] string workingDirectory = "")
    {
        return RunNative(
            displayCommand: $"glob '{pattern}'",
            runner: async (ctx, ct) =>
            {
                try
                {
                    await Task.Yield();

                    var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                        ? GetDefaultWorkspace()
                        : workingDirectory!;

                    var searchDir = string.IsNullOrWhiteSpace(path)
                        ? baseDir
                        : Path.GetFullPath(path, baseDir);

                    // 解析 glob 模式：处理 ** 和目录前缀
                    // 例如 "src/**/*.ts" → searchDir=src, filePattern=*.ts, recursive=true
                    // 例如 "**/*.cs" → searchDir=., filePattern=*.cs, recursive=true
                    // 例如 "*.json" → searchDir=., filePattern=*.json, recursive=false
                    string filePattern;
                    bool recursive;

                    if (pattern.Contains("**"))
                    {
                        recursive = true;
                        // 分割 ** 前后的部分
                        int starStarIdx = pattern.IndexOf("**", StringComparison.Ordinal);
                        string prefix = pattern[..starStarIdx].TrimEnd('/', '\\');
                        string suffix = pattern[(starStarIdx + 2)..].TrimStart('/', '\\');

                        if (!string.IsNullOrEmpty(prefix))
                            searchDir = Path.GetFullPath(prefix, searchDir);

                        filePattern = string.IsNullOrEmpty(suffix) ? "*" : suffix;
                    }
                    else if (pattern.Contains('/') || pattern.Contains('\\'))
                    {
                        // 包含目录分隔符：提取目录和文件名部分
                        var dir = Path.GetDirectoryName(pattern);
                        filePattern = Path.GetFileName(pattern);
                        if (!string.IsNullOrEmpty(dir))
                            searchDir = Path.GetFullPath(dir, searchDir);
                        recursive = false;
                    }
                    else
                    {
                        filePattern = pattern;
                        recursive = false;
                    }

                    if (!Directory.Exists(searchDir))
                    {
                        ctx.WriteStderrLine($"❌ Error: Directory not found: {searchDir}");
                        return 2;
                    }

                    string[] ignoredDirs = [
                        $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}"
                    ];

                    var enumOpts = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = recursive,
                        MatchCasing = MatchCasing.PlatformDefault
                    };

                    var results = new List<(string Path, DateTimeOffset ModTime)>();
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(searchDir, filePattern, enumOpts))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (ignoredDirs.Any(d => file.Contains(d, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            var fi = new FileInfo(file);
                            results.Add((file, SafeLastWriteTimeUtc(fi)));
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.WriteStderrLine($"Warning: {ex.Message}");
                    }

                    // 按修改时间降序排列
                    results.Sort((a, b) => b.ModTime.CompareTo(a.ModTime));

                    var sb = new StringBuilder();
                    foreach (var (filePath, modTime) in results)
                    {
                        sb.AppendLine(filePath);
                    }

                    ctx.WriteStdoutLine(sb.ToString());
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
            runInBackground: false,
            timeoutMs: 30000
        );
    }

    [Description("写入文件内容（覆盖整个文件）。适用于创建新文件或需要完全重写文件内容的场景。" +
        "如果文件不存在则创建（包括必要的父目录）。如果文件已存在则覆盖。")]
    public string WriteFile(
        [Description("文件路径")] string filePath,
        [Description("要写入的完整文件内容")] string content,
        [Description("工作目录（可选）")] string workingDirectory = "")
    {
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? GetDefaultWorkspace()
                : workingDirectory!;

            var full = Path.GetFullPath(filePath, baseDir);

            // 自动创建父目录
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            bool existed = File.Exists(full);
            content ??= string.Empty;

            // 原子写入
            var tmp = full + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, full, overwrite: true);

            int lineCount = content.Split('\n').Length;

            return $"✅ File {(existed ? "overwritten" : "created")}: {full}\nCharacters: {content.Length}\nLines: {lineCount}";
        }
        catch (Exception ex)
        {
            return $"❌ Error ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [Description("读取文件内容。默认从头读取最多 2000 行。可指定 offset（起始行号）和 limit（读取行数）来读取大文件的特定片段。" +
        "输出格式为带行号的 cat -n 风格。")]
    public string ReadFile(
        [Description("文件路径")] string filePath,
        [Description("从第几行开始读（1-based，默认从第 1 行开始）")] int offset = 0,
        [Description("读取多少行（默认不指定则最多读取 2000 行）")] int limit = 0,
        [Description("工作目录（可选）")] string workingDirectory = "")
    {
        return RunNative(
            displayCommand: $"read {filePath}",
            runner: async (ctx, ct) =>
            {
                try
                {
                    var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                        ? GetDefaultWorkspace()
                        : workingDirectory!;

                    var full = Path.GetFullPath(filePath, baseDir);

                    if (!File.Exists(full))
                    {
                        ctx.WriteStderrLine($"❌ Error: File not found: {full}");
                        return 2;
                    }

                    int startLine = offset > 0 ? offset : 1;
                    int maxLines = limit > 0 ? limit : CatReadPageLines;

                    await using var fs = new FileStream(
                        full,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan
                    );

                    using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                    // 跳到起始行
                    int currentLineNo = 0;
                    while (currentLineNo < startLine - 1)
                    {
                        ct.ThrowIfCancellationRequested();
                        var skip = await sr.ReadLineAsync().ConfigureAwait(false);
                        if (skip == null)
                        {
                            ctx.WriteStderrLine($"Warning: offset {startLine} exceeds file length ({currentLineNo} lines).");
                            return 0;
                        }
                        currentLineNo++;
                    }

                    // 读取指定行数
                    int emitted = 0;
                    while (emitted < maxLines)
                    {
                        ct.ThrowIfCancellationRequested();
                        var line = await sr.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;

                        currentLineNo++;
                        ctx.WriteStdoutLine($"{currentLineNo,4} | {line}");
                        emitted++;
                    }

                    // 检查是否还有更多内容
                    ct.ThrowIfCancellationRequested();
                    var lookahead = await sr.ReadLineAsync().ConfigureAwait(false);

                    if (lookahead != null)
                    {
                        ctx.WriteStdoutLine($"\n--more-- (File continues below line {currentLineNo})");
                        ctx.WriteStdoutLine($"Use ReadFile with offset={currentLineNo + 1} to continue reading.");
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
            runInBackground: false,
            timeoutMs: 0
        );
    }
}
