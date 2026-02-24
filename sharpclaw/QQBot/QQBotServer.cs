using Luolan.QQBot;
using sharpclaw.Core;
using sharpclaw.UI;

namespace sharpclaw.QQBot;

/// <summary>
/// QQ Bot 服务主机。接收 QQ 消息并转发给 MainAgent 处理。
/// </summary>
public static class QQBotServer
{
    public static async Task RunAsync(string[] args)
    {
        if (!SharpclawConfig.Exists())
        {
            Console.WriteLine("[Error] 配置文件不存在，请先运行 TUI 模式完成配置：dotnet run --project sharpclaw config");
            return;
        }

        var bootstrap = AgentBootstrap.Initialize();

        if (!bootstrap.Config.QQBot.Enabled)
        {
            Console.WriteLine("[Error] QQ Bot 未启用，请在配置中启用 QQ Bot。");
            return;
        }

        var qqConfig = bootstrap.Config.QQBot;
        if (string.IsNullOrWhiteSpace(qqConfig.AppId) || string.IsNullOrWhiteSpace(qqConfig.ClientSecret))
        {
            Console.WriteLine("[Error] QQ Bot AppId 或 ClientSecret 未配置。");
            return;
        }

        if (bootstrap.MemoryStore is null)
            Console.WriteLine("[Config] 向量记忆已禁用，记忆压缩将使用总结模式");

        // 构建 QQ Bot 客户端
        var bot = new QQBotClientBuilder()
            .WithAppId(qqConfig.AppId)
            .WithClientSecret(qqConfig.ClientSecret)
            .WithIntents(Intents.Default | Intents.GroupAtMessages | Intents.C2CMessages)
            .UseSandbox(qqConfig.Sandbox)
            .Build();

        var chatIO = new QQBotChatIO(bot);

        // 设置日志
        AppLogger.SetInstance(new ConsoleLogger());

        // 注册消息事件
        bot.OnAtMessageCreate += async e =>
        {
            Console.WriteLine($"[QQBot] 频道消息: {e.Message.Content}");
            await chatIO.EnqueueMessageAsync(e.Message, MessageSource.Channel);
        };

        bot.OnGroupAtMessageCreate += async e =>
        {
            Console.WriteLine($"[QQBot] 群消息: {e.Message.Content}");
            await chatIO.EnqueueMessageAsync(e.Message, MessageSource.Group);
        };

        bot.OnC2CMessageCreate += async e =>
        {
            Console.WriteLine($"[QQBot] 私聊消息: {e.Message.Content}");
            await chatIO.EnqueueMessageAsync(e.Message, MessageSource.C2C);
        };

        bot.OnReady += e =>
        {
            Console.WriteLine($"[QQBot] 机器人已就绪: {bot.CurrentUser?.Username ?? "unknown"}");
            return Task.CompletedTask;
        };

        // 创建 MainAgent
        var agent = new Agents.MainAgent(
            bootstrap.Config,
            bootstrap.MemoryStore,
            bootstrap.CommandSkills,
            chatIO: chatIO);

        // 启动 Bot 连接
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            chatIO.RequestStop();
        };

        await bot.StartAsync(cts.Token);
        Console.WriteLine("[QQBot] QQ Bot 已启动，按 Ctrl+C 停止");

        // 在后台运行 Agent 对话循环
        var agentTask = Task.Run(() => agent.RunAsync(cts.Token));

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }

        Console.WriteLine("[QQBot] 正在停止...");
        await bot.StopAsync();
        await agentTask;
    }

    /// <summary>
    /// 简单的控制台日志实现。
    /// </summary>
    private sealed class ConsoleLogger : Abstractions.IAppLogger
    {
        public void Log(string message) => Console.WriteLine(message);
        public void SetStatus(string status) => Console.WriteLine($"[Status] {status}");
    }
}
