using sharpclaw.Channels.Tui;
using sharpclaw.Core;
using sharpclaw.UI;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";

#if DEBUG 
// 调试模式下如果没有提供命令参数，默认进入 cli 模式，方便调试和开发。
// 生产环境下则要求明确指定命令，避免误操作，兼容以往无参退出体验。
// 如果作为Fallback使用，建议在生产环境中也生效，默认命令为 cli，以保持一致的用户体验。
if (command.Length == 0)
{
    Console.WriteLine("[Debug] 未提供命令参数，默认进入cli。");
    command = "cli";
}
else 
    Console.WriteLine($"[Debug] 启动参数: {string.Join(' ', args)} ");
#endif

    switch (command)
{
    case "web":
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

    case "cli":
        await RunCliAsync(args);
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
          cli                              启动纯 CLI 对话模式（Fallback）
          tui                              启动 TUI 终端界面
          web [--address ADDR] [--port N]  启动 Web 服务
          qqbot                            启动 QQ Bot 服务
          config                           打开配置界面
          help                             显示帮助信息
        """);
}

static async Task RunCliAsync(string[] args)
{
    KeyStore.PasswordPrompt = ConsolePasswordPrompt;

    // ── 配置检测 ──
    if (!SharpclawConfig.Exists())
    {
        Console.WriteLine("[Config] 尚未找到配置文件，请先运行 'sharpclaw config' 完成配置。");
        return;
    }

    // ── 初始化 ──
    var bootstrap = AgentBootstrap.Initialize();

    if (bootstrap.MemoryStore is null)
        Console.WriteLine("[Config] 向量记忆已禁用，记忆压缩将使用总结模式");

    Console.WriteLine("Sharpclaw CLI 模式（输入 /help 查看指令，/exit 退出）");
    Console.WriteLine(new string('-', 48));

    var chatIO = new sharpclaw.Channels.Cli.CliChatIO();
    AppLogger.SetInstance(new sharpclaw.Channels.Cli.CliAppLogger(chatIO));
    var agent = new sharpclaw.Agents.MainAgent(
        bootstrap.Config, bootstrap.MemoryStore, bootstrap.CommandSkills,
        chatIO: chatIO, bootstrap.AgentContext);

    await agent.RunAsync();

    Console.WriteLine("\n[Cli] 已退出。");
    bootstrap.TaskManager.Dispose();
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
        bootstrap.Config, bootstrap.MemoryStore, bootstrap.CommandSkills, chatIO: chatWindow, bootstrap.AgentContext);

    // 在后台线程启动智能体循环
    _ = Task.Run(() => agent.RunAsync());

    // 运行 Terminal.Gui 主循环（阻塞直到退出）
    app.Run(chatWindow);
    chatWindow.Dispose();

    // 清理所有后台任务
    bootstrap.TaskManager.Dispose();
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
