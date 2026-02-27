using System.Net.WebSockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using sharpclaw.Core;
using sharpclaw.UI;

namespace sharpclaw.Channels.Web;

/// <summary>
/// ASP.NET Core WebSocket 服务主机。
/// </summary>
public static class WebServer
{
    public static async Task RunAsync(string[] args)
    {
        if (!SharpclawConfig.Exists())
        {
            Console.WriteLine("[Error] 配置文件不存在，请先运行 TUI 模式完成配置：dotnet run --project sharpclaw config");
            return;
        }

        var bootstrap = AgentBootstrap.Initialize();

        // 端口优先级：命令行参数 > 配置文件 > 默认值
        var address = bootstrap.Config.Channels.Web.ListenAddress;
        var port = bootstrap.Config.Channels.Web.Port;
        var addrIdx = Array.IndexOf(args, "--address");
        if (addrIdx >= 0 && addrIdx + 1 < args.Length)
            address = args[addrIdx + 1];
        var portIdx = Array.IndexOf(args, "--port");
        if (portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out var p))
            port = p;

        if (bootstrap.MemoryStore is null)
            Console.WriteLine("[Config] 向量记忆已禁用，记忆压缩将使用总结模式");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{address}:{port}");

        var app = builder.Build();
        app.UseWebSockets();

        // 从嵌入资源提供 index.html
        var indexHtml = LoadEmbeddedResource("index.html");
        app.MapGet("/", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.WriteAsync(indexHtml);
        });

        // 用信号量限制单客户端
        var semaphore = new SemaphoreSlim(1, 1);

        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            if (!await semaphore.WaitAsync(0))
            {
                context.Response.StatusCode = 409; // Conflict
                await context.Response.WriteAsync("已有客户端连接");
                return;
            }

            WebSocket ws;
            try
            {
                ws = await context.WebSockets.AcceptWebSocketAsync();
            }
            catch
            {
                semaphore.Release();
                return;
            }

            Console.WriteLine($"[WebSocket] 客户端已连接: {context.Connection.RemoteIpAddress}");

            await using var sender = new WebSocketSender(ws);
            await using var chatIO = new WebSocketChatIO(sender);
            var logger = new WebSocketLogger(sender);
            AppLogger.SetInstance(logger);

            chatIO.StartReceiving();

            var agent = new sharpclaw.Agents.MainAgent(
                bootstrap.Config,
                bootstrap.MemoryStore,
                bootstrap.CommandSkills,
                chatIO: chatIO);

            try
            {
                await agent.RunAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Agent 异常: {ex.Message}");
            }

            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }

            Console.WriteLine("[WebSocket] 客户端已断开");
            semaphore.Release();
        });

        Console.WriteLine($"Sharpclaw WebSocket 服务已启动: http://{address}:{port}");
        Console.WriteLine("按 Ctrl+C 停止");

        await app.RunAsync();

        // 清理所有后台任务
        bootstrap.TaskManager.Dispose();
    }

    private static string LoadEmbeddedResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"嵌入资源 '{name}' 未找到");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
