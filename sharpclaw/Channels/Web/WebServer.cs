using System.Net.WebSockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using sharpclaw.Core;
using sharpclaw.UI;

namespace sharpclaw.Channels.Web;

/// <summary>
/// ASP.NET Core WebSocket 服务主机。
/// 作为整个 Sharpclaw 的基础宿主，承载 Web UI、WebSocket 端点和可选的 QQBot 服务。
/// </summary>
public static class WebServer
{
    /// <summary>
    /// 启动 Web 宿主。bootstrap 由外部提供，支持多客户端 WebSocket 连接。
    /// </summary>
    public static async Task RunAsync(string[] args, AgentBootstrap.BootstrapResult bootstrap, bool silent = false)
    {
        // 端口优先级：命令行参数 > 配置文件 > 默认值
        var address = bootstrap.Config.Channels.Web.ListenAddress;
        var port = bootstrap.Config.Channels.Web.Port;
        var addrIdx = Array.IndexOf(args, "--address");
        if (addrIdx >= 0 && addrIdx + 1 < args.Length)
            address = args[addrIdx + 1];
        var portIdx = Array.IndexOf(args, "--port");
        if (portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out var p))
            port = p;

        if (bootstrap.MemoryStore is null && !silent)
            Console.WriteLine("[Config] 向量记忆已禁用，记忆压缩将使用总结模式");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{address}:{port}");

        // silent 模式下完全禁止控制台日志输出
        if (silent)
        {
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.None);
        }

        // 如果 QQBot 配置启用，注册为托管服务
        if (bootstrap.Config.Channels.QQBot.Enabled)
        {
            var qqConfig = bootstrap.Config.Channels.QQBot;
            if (!string.IsNullOrWhiteSpace(qqConfig.AppId) && !string.IsNullOrWhiteSpace(qqConfig.ClientSecret))
            {
                builder.Services.AddSingleton(new QQBot.QQBotHostedService(bootstrap));
                builder.Services.AddHostedService(sp => sp.GetRequiredService<QQBot.QQBotHostedService>());
                if (!silent) Console.WriteLine("[QQBot] QQ Bot 已注册为托管服务，将随 Web 宿主一同启动");
            }
            else
            {
                if (!silent) Console.WriteLine("[QQBot] QQ Bot 已启用但 AppId 或 ClientSecret 未配置，跳过注册。");
            }
        }

        var app = builder.Build();
        app.UseWebSockets();

        // 从嵌入资源提供 index.html
        var indexHtml = LoadEmbeddedResource("index.html");
        app.MapGet("/", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.WriteAsync(indexHtml);
        });

        // WebSocket 端点 — 支持多客户端
        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            WebSocket ws;
            try
            {
                ws = await context.WebSockets.AcceptWebSocketAsync();
            }
            catch
            {
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
                chatIO: chatIO,
                bootstrap.AgentContext);

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
        });

        Console.WriteLine($"Sharpclaw Web 服务已启动: http://{address}:{port}");
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
