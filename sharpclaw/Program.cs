using sharpclaw.Channels.Tui;
using sharpclaw.Core;
using sharpclaw.UI;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";

switch (command)
{
    case "serve":
        KeyStore.PasswordPrompt = ConsolePasswordPrompt;
        await sharpclaw.Channels.Web.WebServer.RunAsync(args);
        return;

    case "qqbot":
        KeyStore.PasswordPrompt = ConsolePasswordPrompt;
        await sharpclaw.Channels.QQBot.QQBotServer.RunAsync(args);
        return;

    case "tui":
        RunTui(args);
        return;

    case "config":
        RunTui(args);
        return;

    case "help" or "--help" or "-h":
        PrintHelp();
        return;

    default:
        PrintHelp();
        return;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Sharpclaw - AI 智能助手

        用法: sharpclaw <命令> [选项]

        命令:
          tui                 启动 TUI 终端界面
          serve [--port N]    启动 Web 服务（默认端口 5000）
          qqbot               启动 QQ Bot 服务
          config              打开配置界面
          help                显示帮助信息
        """);
}

static void RunTui(string[] args)
{
    using var app = Application.Create().Init();

    // TUI 模式下通过对话框提示输入密码
    KeyStore.PasswordPrompt = prompt =>
    {
        string? result = null;
        var dlg = new Dialog { Title = "Keychain 解锁", Width = 50, Height = 15 };
        var label = new Label { Text = prompt, X = 1, Y = 1, Width = Dim.Fill(1) };
        var field = new TextField { X = 1, Y = 3, Width = Dim.Fill(1), Secret = true };
        dlg.Add(label, field);

        var ok = new Button { Text = "确定", IsDefault = true };
        ok.Accepting += (_, e) => { result = field.Text; dlg.RequestStop(); e.Handled = true; };
        var cancel = new Button { Text = "跳过" };
        cancel.Accepting += (_, e) => { dlg.RequestStop(); e.Handled = true; };
        dlg.AddButton(ok);
        dlg.AddButton(cancel);

        app.Run(dlg);
        dlg.Dispose();
        return result;
    };

    // ── 配置检测 ──
    if (args.Contains("config") || !SharpclawConfig.Exists())
    {
        var configDialog = new ConfigDialog();
        if (SharpclawConfig.Exists())
            configDialog.LoadFrom(SharpclawConfig.Load());
        app.Run(configDialog);
        configDialog.Dispose();

        if (!configDialog.Saved || args.Contains("config"))
            return;
    }

    // ── 初始化 ──
    var bootstrap = AgentBootstrap.Initialize();

    if (bootstrap.MemoryStore is null)
        AppLogger.Log("[Config] 向量记忆已禁用，记忆压缩将使用总结模式");

    // ── 创建 ChatWindow 并启动主智能体 ──
    var chatWindow = new ChatWindow(bootstrap.Config.Channels.Tui);
    var agent = new sharpclaw.Agents.MainAgent(
        bootstrap.Config, bootstrap.MemoryStore, bootstrap.CommandSkills, chatIO: chatWindow);

    // 在后台线程启动智能体循环
    _ = Task.Run(() => agent.RunAsync());

    // 运行 Terminal.Gui 主循环（阻塞直到退出）
    app.Run(chatWindow);
    chatWindow.Dispose();
}

static string? ConsolePasswordPrompt(string prompt)
{
    Console.Write($"[KeyStore] {prompt} (直接回车跳过): ");
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Remove(sb.Length - 1, 1);
        else if (key.KeyChar >= ' ') sb.Append(key.KeyChar);
    }
    var result = sb.ToString();
    return string.IsNullOrEmpty(result) ? null : result;
}
