using System.Net.Sockets;
using System.Text.Json.Nodes;
using sharpclaw.Channels.Tui;
using sharpclaw.Core;
using sharpclaw.UI;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";

#if DEBUG
if (command.Length == 0)
{
    Console.WriteLine("[Debug] 未提供命令参数，默认启动 Web 宿主。");
    command = "web";
}
else
    Console.WriteLine($"[Debug] 启动参数: {string.Join(' ', args)} ");
#endif

switch (command)
{
    case "web":
        KeyStore.PasswordPrompt = ConsolePasswordPrompt;
        await RunWebAsync(args);
        return;

    case "" or "cli":
        await RunCliAsync(args);
        return;

    case "tui":
        await RunTuiAsync(args);
        return;

    case "config":
        RunConfigDialog();
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
          web [--address ADDR] [--port N]   启动 Web 宿主（含 WebSocket + 可选 QQBot）
          cli [--address ADDR] [--port N]   CLI 模式（自动检测 Web 服务，无则自动启动）
          tui [--address ADDR] [--port N]   TUI 模式（自动检测 Web 服务，无则自动启动）
          config                            打开配置界面
          help                              显示帮助信息

        CLI/TUI 模式会自动检测 Web 服务是否已启动：
          - 已启动 → 直接连接
          - 未启动 → 自动在后台启动 Web 服务，然后连接
        """);
}

// ─────────────────────────────────────────────────────
// Web 宿主模式
// ─────────────────────────────────────────────────────
static async Task RunWebAsync(string[] args)
{
    if (!SharpclawConfig.Exists())
    {
        Console.WriteLine("[Error] 配置文件不存在，请先运行 'sharpclaw config' 完成配置。");
        return;
    }

    var bootstrap = AgentBootstrap.Initialize();
    await sharpclaw.Channels.Web.WebServer.RunAsync(args, bootstrap);
}

// ─────────────────────────────────────────────────────
// CLI 模式：检测 Web → 连接 or 启动+连接
// ─────────────────────────────────────────────────────
static async Task RunCliAsync(string[] args)
{
    var (address, port) = ResolveWebAddress(args);
    var clientAddr = NormalizeClientAddress(address);
    var serverUrl = $"ws://{clientAddr}:{port}/ws";

    if (await IsWebServiceRunningAsync(clientAddr, port))
    {
        Console.WriteLine($"[Cli] 检测到 Web 服务已在 {clientAddr}:{port} 运行，直接连接...");
    }
    else
    {
        Console.WriteLine($"[Cli] Web 服务未运行，正在自动启动...");
        KeyStore.PasswordPrompt = ConsolePasswordPrompt;

        if (!SharpclawConfig.Exists())
        {
            Console.WriteLine("[Error] 配置文件不存在，请先运行 'sharpclaw config' 完成配置。");
            return;
        }

        var bootstrap = AgentBootstrap.Initialize();
        _ = Task.Run(() => sharpclaw.Channels.Web.WebServer.RunAsync(args, bootstrap, silent: true));

        if (!await WaitForWebServiceAsync(clientAddr, port))
        {
            Console.WriteLine("[Error] Web 服务启动超时。");
            return;
        }

        Console.WriteLine($"[Cli] Web 服务已启动: http://{clientAddr}:{port}");
    }

    using var client = new sharpclaw.Channels.Cli.CliClient(serverUrl);
    await client.RunAsync();
}

// ─────────────────────────────────────────────────────
// TUI 模式：检测 Web → 连接 or 启动+连接
// ─────────────────────────────────────────────────────
static async Task RunTuiAsync(string[] args)
{
    using var app = Application.Create().Init();

    var (address, port) = ResolveWebAddress(args);
    var clientAddr = NormalizeClientAddress(address);

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

    TuiChannelConfig tuiConfig = new();
    AgentBootstrap.BootstrapResult? bootstrap = null;

    bool webRunning = await IsWebServiceRunningAsync(clientAddr, port);

    if (!webRunning)
    {
        // Web 未运行 → 需要本地初始化并启动 Web
        if (!SharpclawConfig.Exists())
        {
            // 首次运行：弹出配置对话框
            var configDialog = new ConfigDialog();
            app.Run(configDialog);
            configDialog.Dispose();
            if (!configDialog.Saved)
                return;
        }

        bootstrap = AgentBootstrap.Initialize();
        tuiConfig = bootstrap.Config.Channels.Tui;

        if (bootstrap.MemoryStore is null)
            AppLogger.Log("[Config] 向量记忆已禁用，记忆压缩将使用总结模式");

        // 后台启动 Web 服务
        _ = Task.Run(() => sharpclaw.Channels.Web.WebServer.RunAsync(args, bootstrap, silent: true));

        if (!await WaitForWebServiceAsync(clientAddr, port))
        {
            Console.WriteLine("[Error] Web 服务启动超时。");
            return;
        }
    }
    else
    {
        // Web 已运行 → 尝试从配置读取 TUI 偏好
        if (SharpclawConfig.Exists())
        {
            try
            {
                var config = SharpclawConfig.Load();
                tuiConfig = config.Channels.Tui;
            }
            catch { }
        }
    }

    // TUI 作为 WebSocket 客户端连接到 Web 服务
    var serverUrl = $"ws://{clientAddr}:{port}/ws";
    var tuiClient = new TuiClient(serverUrl, tuiConfig);

    app.Run(tuiClient);
    tuiClient.Dispose();

    bootstrap?.TaskManager.Dispose();
}

// ─────────────────────────────────────────────────────
// 配置对话框（独立模式）
// ─────────────────────────────────────────────────────
static void RunConfigDialog()
{
    using var app = Application.Create().Init();

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

    var configDialog = new ConfigDialog();
    if (SharpclawConfig.Exists())
        configDialog.LoadFrom(SharpclawConfig.Load());
    app.Run(configDialog);
    configDialog.Dispose();
}

// ─────────────────────────────────────────────────────
// 辅助方法
// ─────────────────────────────────────────────────────

/// <summary>
/// 从配置文件和命令行参数解析 Web 服务地址。不需要解密密钥，直接读 JSON。
/// </summary>
static (string address, int port) ResolveWebAddress(string[] args)
{
    var address = "localhost";
    var port = 5000;

    var configPath = SharpclawConfig.ConfigPath;
    if (File.Exists(configPath))
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(configPath));
            var web = json?["channels"]?["web"];
            if (web != null)
            {
                address = web["listenAddress"]?.GetValue<string>() ?? address;
                port = web["port"]?.GetValue<int>() ?? port;
            }
        }
        catch { }
    }

    var addrIdx = Array.IndexOf(args, "--address");
    if (addrIdx >= 0 && addrIdx + 1 < args.Length)
        address = args[addrIdx + 1];
    var portIdx = Array.IndexOf(args, "--port");
    if (portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out var p))
        port = p;

    return (address, port);
}

/// <summary>
/// 服务器监听在 0.0.0.0 等通配地址时，客户端应连接 localhost。
/// </summary>
static string NormalizeClientAddress(string serverAddress) =>
    serverAddress is "0.0.0.0" or "*" or "+" ? "localhost" : serverAddress;

/// <summary>
/// 检测指定地址的 Web 服务是否已在运行（TCP 连接探测）。
/// </summary>
static async Task<bool> IsWebServiceRunningAsync(string address, int port)
{
    try
    {
        using var tcp = new TcpClient();
        var connectTask = tcp.ConnectAsync(address, port);
        if (await Task.WhenAny(connectTask, Task.Delay(1000)) == connectTask)
        {
            await connectTask;
            return true;
        }
        return false;
    }
    catch
    {
        return false;
    }
}

/// <summary>
/// 等待 Web 服务启动就绪，最多等待 15 秒。
/// </summary>
static async Task<bool> WaitForWebServiceAsync(string address, int port)
{
    for (int i = 0; i < 75; i++) // 75 × 200ms = 15s
    {
        if (await IsWebServiceRunningAsync(address, port))
            return true;
        await Task.Delay(200);
    }
    return false;
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
