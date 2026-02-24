using sharpclaw.Core;
using sharpclaw.UI;
using Terminal.Gui.App;

// ── 模式选择 ──
if (args.Contains("serve"))
{
    await sharpclaw.Web.WebServer.RunAsync(args);
    return;
}

if (args.Contains("qqbot"))
{
    await sharpclaw.QQBot.QQBotServer.RunAsync(args);
    return;
}

// ── Terminal.Gui 初始化 ──
using var app = Application.Create().Init();

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
var chatWindow = new ChatWindow();
var agent = new sharpclaw.Agents.MainAgent(
    bootstrap.Config, bootstrap.MemoryStore, bootstrap.CommandSkills, chatIO: chatWindow);

// 在后台线程启动智能体循环
_ = Task.Run(() => agent.RunAsync());

// 运行 Terminal.Gui 主循环（阻塞直到退出）
app.Run(chatWindow);
chatWindow.Dispose();
