using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using sharpclaw.Core.TaskManagement;

namespace sharpclaw.Commands;

/// <summary>
/// HTTP request commands.
/// </summary>
public class HttpCommands : CommandBase
{
    public HttpCommands(TaskManager taskManager)
        : base(taskManager)
    {
    }

    [Description("Make HTTP requests with support for various methods, headers, query parameters, and request bodies")]
    public string CommandHttp(
        [Description("Request URL (http/https)")] string url,
        [Description("HTTP method: GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS")] string method = "GET",
        [Description("Request headers (can be repeated): \"Key: Value\"")] string[] headers = null,
        [Description("Query parameters (can be repeated): k=v")] string[] query = null,
        [Description("Request body (string with escape sequences)")] string dataEscaped = "",
        [Description("Read request body from file")] string dataFile = "",
        [Description("JSON request body (auto-sets Content-Type)")] string jsonEscaped = "",
        [Description("Form fields (can be repeated): k=v")] string[] form = null,
        [Description("Explicit Content-Type header")] string contentType = "",
        [Description("Request timeout in milliseconds")] int timeoutMs = 30000,
        [Description("Save response body to file")] string outputFile = "",
        [Description("Maximum body characters to display")] int maxBodyChars = 8192,
        [Description("Skip TLS certificate validation")] bool insecure = false,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        var display = $"http {url} -X {(string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant())}";

        return RunNative(
            displayCommand: display,
            runner: async (ctx, ct) =>
            {
                var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.CurrentDirectory
                    : workingDirectory!;

                try
                {
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        ctx.WriteStderrLine("url is empty.");
                        return 2;
                    }

                    var finalUrl = HttpAppendQuery(url, query);
                    var httpMethod = HttpParseMethod(method);

                    ctx.WriteStdoutLine($"Request: {httpMethod.Method} {finalUrl}");

                    using var handler = new HttpClientHandler
                    {
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
                    };

                    if (insecure)
                        handler.ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

                    using var client = new HttpClient(handler);
                    client.Timeout = timeoutMs <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(timeoutMs);

                    using var req = new HttpRequestMessage(httpMethod, finalUrl);

                    string? headerContentType = null;

                    if (headers != null)
                    {
                        foreach (var h in headers.Where(x => !string.IsNullOrWhiteSpace(x)))
                        {
                            if (!HttpTrySplitHeader(h, out var k, out var v))
                            {
                                ctx.WriteStderrLine($"Invalid header format: {h} (use \"Key: Value\")");
                                return 2;
                            }

                            if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                            {
                                headerContentType = v;
                                continue;
                            }

                            req.Headers.TryAddWithoutValidation(k, v);
                        }
                    }

                    HttpContent? content = null;

                    if (form != null && form.Length > 0)
                    {
                        var pairs = new List<KeyValuePair<string, string>>();
                        foreach (var kv in form)
                        {
                            if (!HttpTrySplitKeyValue(kv, out var k, out var v))
                            {
                                ctx.WriteStderrLine($"Invalid form item: {kv} (use k=v)");
                                return 2;
                            }
                            pairs.Add(new KeyValuePair<string, string>(k, v));
                        }
                        content = new FormUrlEncodedContent(pairs);
                    }
                    else if (!string.IsNullOrWhiteSpace(jsonEscaped))
                    {
                        var json = UnescapePayload(jsonEscaped);
                        content = new StringContent(json, Encoding.UTF8, "application/json");
                    }
                    else if (!string.IsNullOrWhiteSpace(dataFile))
                    {
                        var fullDataFile = Path.GetFullPath(dataFile!, baseDir);
                        if (!File.Exists(fullDataFile))
                        {
                            ctx.WriteStderrLine($"data-file not found: {fullDataFile}");
                            return 2;
                        }

                        var bytes = await File.ReadAllBytesAsync(fullDataFile, ct).ConfigureAwait(false);
                        content = new ByteArrayContent(bytes);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    }
                    else if (!string.IsNullOrWhiteSpace(dataEscaped))
                    {
                        var data = UnescapePayload(dataEscaped);
                        content = new StringContent(data, Encoding.UTF8, "text/plain");
                    }

                    if (httpMethod == HttpMethod.Head)
                        content = null;

                    if (content != null)
                    {
                        var ctValue = contentType ?? headerContentType;
                        if (!string.IsNullOrWhiteSpace(ctValue))
                        {
                            content.Headers.Remove("Content-Type");
                            content.Headers.TryAddWithoutValidation("Content-Type", ctValue);
                        }
                        req.Content = content;
                    }

                    var sw = Stopwatch.StartNew();
                    using var resp = await client.SendAsync(
                        req,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct
                    ).ConfigureAwait(false);
                    sw.Stop();

                    ctx.WriteStdoutLine($"Response: {(int)resp.StatusCode} {resp.ReasonPhrase} ({sw.ElapsedMilliseconds} ms)");

                    foreach (var kv in resp.Headers)
                        ctx.WriteStdoutLine($"H: {kv.Key}: {string.Join(", ", kv.Value)}");
                    if (resp.Content != null)
                    {
                        foreach (var kv in resp.Content.Headers)
                            ctx.WriteStdoutLine($"H: {kv.Key}: {string.Join(", ", kv.Value)}");
                    }

                    if (resp.Content != null && httpMethod != HttpMethod.Head)
                    {
                        string? savedTo = null;
                        long savedBytes = 0;

                        FileStream? ofs = null;
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(outputFile))
                            {
                                var outFull = Path.GetFullPath(outputFile!, baseDir);
                                var outDir = Path.GetDirectoryName(outFull);
                                if (!string.IsNullOrWhiteSpace(outDir))
                                    Directory.CreateDirectory(outDir);

                                ofs = new FileStream(outFull, FileMode.Create, FileAccess.Write, FileShare.Read);
                                savedTo = outFull;
                                ctx.WriteStdoutLine($"Saving body to: {savedTo}");
                            }

                            await using var rs = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                            var captureBytesLimit = Math.Max(0, maxBodyChars) * 4;
                            using var ms = captureBytesLimit > 0 ? new MemoryStream(Math.Min(captureBytesLimit, 256 * 1024)) : new MemoryStream(0);

                            var buf = new byte[8192];
                            int n;
                            while ((n = await rs.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                            {
                                savedBytes += n;

                                if (ofs != null)
                                    await ofs.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);

                                if (captureBytesLimit > 0 && ms.Length < captureBytesLimit)
                                {
                                    var toWrite = (int)Math.Min(n, captureBytesLimit - ms.Length);
                                    if (toWrite > 0) ms.Write(buf, 0, toWrite);
                                }
                            }

                            if (ofs != null)
                                await ofs.FlushAsync(ct).ConfigureAwait(false);

                            var captured = ms.ToArray();
                            if (captured.Length > 0)
                            {
                                var enc = HttpGetResponseEncoding(resp) ?? Encoding.UTF8;
                                var text = enc.GetString(captured);

                                bool truncated = false;
                                if (maxBodyChars >= 0 && text.Length > maxBodyChars)
                                {
                                    text = text.Substring(0, maxBodyChars);
                                    truncated = true;
                                }

                                ctx.WriteStdoutLine("");
                                ctx.WriteStdoutLine("---- body (preview) ----");
                                foreach (var line in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
                                    ctx.WriteStdoutLine(line);

                                if (truncated)
                                    ctx.WriteStdoutLine("---- body preview truncated ----");
                            }

                            if (savedTo != null)
                                ctx.WriteStdoutLine($"Saved bytes: {savedBytes}");
                        }
                        finally
                        {
                            if (ofs != null) await ofs.DisposeAsync().ConfigureAwait(false);
                        }
                    }

                    return 0;
                }
                catch (TaskCanceledException ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        ctx.WriteStderrLine($"Request timed out: {ex.Message}");
                        return 124;
                    }

                    ctx.WriteStderrLine("Operation canceled.");
                    return 137;
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
            timeoutMs: timeoutMs <= 0 ? 0 : timeoutMs
        );
    }

    private static HttpMethod HttpParseMethod(string? s)
    {
        var m = (s ?? "GET").Trim().ToUpperInvariant();
        return m switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            "DELETE" => HttpMethod.Delete,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            _ => new HttpMethod(m)
        };
    }

    private static bool HttpTrySplitHeader(string raw, out string key, out string value)
    {
        key = "";
        value = "";

        if (string.IsNullOrWhiteSpace(raw)) return false;
        var idx = raw.IndexOf(':');
        if (idx <= 0) return false;

        key = raw.Substring(0, idx).Trim();
        value = raw.Substring(idx + 1).Trim();
        return key.Length > 0;
    }

    private static bool HttpTrySplitKeyValue(string raw, out string key, out string value)
    {
        key = "";
        value = "";

        if (string.IsNullOrWhiteSpace(raw)) return false;
        var idx = raw.IndexOf('=');
        if (idx <= 0) return false;

        key = raw.Substring(0, idx).Trim();
        value = raw.Substring(idx + 1).Trim();
        return key.Length > 0;
    }

    private static string HttpAppendQuery(string url, string[]? queryItems)
    {
        if (queryItems == null || queryItems.Length == 0) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var pairs = new List<(string k, string v)>();
        foreach (var q in queryItems.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (HttpTrySplitKeyValue(q, out var k, out var v))
                pairs.Add((k, v));

        if (pairs.Count == 0) return url;

        var ub = new UriBuilder(uri);
        var existing = (ub.Query ?? "").TrimStart('?');

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(existing))
        {
            sb.Append(existing);
            if (sb[^1] != '&') sb.Append('&');
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(pairs[i].k));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(pairs[i].v));
        }

        ub.Query = sb.ToString();
        return ub.Uri.ToString();
    }

    private static Encoding? HttpGetResponseEncoding(HttpResponseMessage resp)
    {
        try
        {
            var cs = resp.Content?.Headers?.ContentType?.CharSet;
            if (string.IsNullOrWhiteSpace(cs)) return null;
            return Encoding.GetEncoding(cs.Trim());
        }
        catch
        {
            return null;
        }
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
