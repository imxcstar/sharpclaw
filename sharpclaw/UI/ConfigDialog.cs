using System.Drawing;
using sharpclaw.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace sharpclaw.UI;

/// <summary>
/// 配置引导对话框：使用 TabView 按智能体分页配置。
/// </summary>
public sealed class ConfigDialog : Dialog
{
    private record ProviderDefaults(string Endpoint, string Model);

    private static readonly Dictionary<string, ProviderDefaults> Providers = new()
    {
        ["anthropic"] = new("https://api.anthropic.com", "claude-opus-4-6"),
        ["openai"] = new("https://api.openai.com/v1", "gpt-5.3"),
        ["gemini"] = new("https://generativelanguage.googleapis.com", "gemini-3.1-pro-preview"),
    };

    private static readonly string[] ProviderNames = ["anthropic", "openai", "gemini"];
    private static readonly string[] ProviderLabels = ["Anthropic", "OpenAI", "Gemini"];

    // 默认配置
    private readonly OptionSelector _defaultProvider;
    private readonly TextField _defaultEndpoint;
    private readonly TextField _defaultApiKey;
    private readonly TextField _defaultModel;

    // 各智能体配置面板
    private readonly AgentPanel _mainPanel;
    private readonly AgentPanel _recallerPanel;
    private readonly AgentPanel _saverPanel;
    private readonly AgentPanel _summarizerPanel;

    // 记忆配置
    private readonly CheckBox _memoryEnabledCheck;
    private readonly TextField _embeddingEndpointField;
    private readonly TextField _embeddingApiKeyField;
    private readonly TextField _embeddingModelField;
    private readonly CheckBox _rerankEnabledCheck;
    private readonly TextField _rerankEndpointField;
    private readonly TextField _rerankApiKeyField;
    private readonly TextField _rerankModelField;
    private readonly List<View> _embeddingViews = [];
    private readonly List<View> _rerankViews = [];

    // TUI 配置
    private readonly CheckBox _tuiLogCollapsedCheck;
    private readonly TextField _tuiQuitKeyField;
    private readonly TextField _tuiToggleLogKeyField;
    private readonly TextField _tuiCancelKeyField;

    // QQ Bot 配置
    private readonly CheckBox _qqBotEnabledCheck;
    private readonly TextField _qqBotAppIdField;
    private readonly TextField _qqBotClientSecretField;
    private readonly CheckBox _qqBotSandboxCheck;
    private readonly List<View> _qqBotViews = [];

    // Web 服务配置
    private readonly CheckBox _webEnabledCheck;
    private readonly TextField _webListenAddressField;
    private readonly TextField _webPortField;
    private readonly List<View> _webViews = [];

    public bool Saved { get; private set; }

    public ConfigDialog()
    {
        Title = "Sharpclaw 配置引导";
        Width = Dim.Percent(80);
        Height = Dim.Percent(90);

        var tabView = new TabView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            CanFocus = true,
        };

        // ── 默认配置 Tab ──
        var defaultView = new View { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true, BorderStyle = Terminal.Gui.Drawing.LineStyle.Single };
        var y = 1;

        var providerLabel = new Label { Text = "AI 供应商:", X = 1, Y = y };
        _defaultProvider = new OptionSelector
        {
            X = 14, Y = y,
            Labels = ProviderLabels,
            Orientation = Orientation.Horizontal,
        };
        _defaultProvider.ValueChanged += OnDefaultProviderChanged;
        y += 2;

        var endpointLabel = new Label { Text = "Endpoint:", X = 1, Y = y };
        _defaultEndpoint = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = Providers["anthropic"].Endpoint };
        y += 2;

        var apiKeyLabel = new Label { Text = "API Key:", X = 1, Y = y };
        _defaultApiKey = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Secret = true };
        y += 2;

        var modelLabel = new Label { Text = "模型名称:", X = 1, Y = y };
        _defaultModel = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = Providers["anthropic"].Model };

        defaultView.Add(providerLabel, _defaultProvider, endpointLabel, _defaultEndpoint,
            apiKeyLabel, _defaultApiKey, modelLabel, _defaultModel);

        tabView.AddTab(CreateTab(tabView, "默认", defaultView), true);

        // ── 智能体 Tabs ──
        _mainPanel = CreateAgentPanel("主智能体", showEnabled: false);
        _recallerPanel = CreateAgentPanel("记忆回忆");
        _saverPanel = CreateAgentPanel("记忆保存");
        _summarizerPanel = CreateAgentPanel("对话总结");

        tabView.AddTab(CreateTab(tabView, "主智能体", _mainPanel.Container), false);
        tabView.AddTab(CreateTab(tabView, "记忆回忆", _recallerPanel.Container), false);
        tabView.AddTab(CreateTab(tabView, "记忆保存", _saverPanel.Container), false);
        tabView.AddTab(CreateTab(tabView, "对话总结", _summarizerPanel.Container), false);

        // ── 向量记忆 Tab ──
        var memoryView = new View { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true, BorderStyle = Terminal.Gui.Drawing.LineStyle.Single };
        y = 1;

        _memoryEnabledCheck = new CheckBox { Text = "启用向量记忆（禁用后降级为总结压缩）", X = 1, Y = y, Value = CheckState.UnChecked };
        _memoryEnabledCheck.ValueChanged += OnMemoryEnabledChanged;
        y += 2;

        var embEndpointLabel = new Label { Text = "Embedding Endpoint:", X = 1, Y = y };
        _embeddingEndpointField = new TextField { X = 22, Y = y, Width = Dim.Fill(2), Text = "https://dashscope.aliyuncs.com/compatible-mode/v1" };
        _embeddingViews.AddRange([embEndpointLabel, _embeddingEndpointField]);
        y += 2;

        var embApiKeyLabel = new Label { Text = "Emb Key:", X = 1, Y = y };
        _embeddingApiKeyField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Secret = true };
        _embeddingViews.AddRange([embApiKeyLabel, _embeddingApiKeyField]);
        y += 2;

        var embModelLabel = new Label { Text = "Emb 模型:", X = 1, Y = y };
        _embeddingModelField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = "text-embedding-v4" };
        _embeddingViews.AddRange([embModelLabel, _embeddingModelField]);
        y += 2;

        _rerankEnabledCheck = new CheckBox { Text = "启用重排序", X = 1, Y = y, Value = CheckState.Checked };
        _rerankEnabledCheck.ValueChanged += OnRerankEnabledChanged;
        _embeddingViews.Add(_rerankEnabledCheck);
        y += 2;

        var rerankEndpointLabel = new Label { Text = "Rerank Endpoint:", X = 1, Y = y };
        _rerankEndpointField = new TextField { X = 22, Y = y, Width = Dim.Fill(2), Text = "https://dashscope.aliyuncs.com/compatible-api/v1/reranks" };
        _rerankViews.AddRange([rerankEndpointLabel, _rerankEndpointField]);
        y += 2;

        var rerankApiKeyLabel = new Label { Text = "Rrk Key:", X = 1, Y = y };
        _rerankApiKeyField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Secret = true };
        _rerankViews.AddRange([rerankApiKeyLabel, _rerankApiKeyField]);
        y += 2;

        var rerankModelLabel = new Label { Text = "Rrk 模型:", X = 1, Y = y };
        _rerankModelField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = "qwen3-vl-rerank" };
        _rerankViews.AddRange([rerankModelLabel, _rerankModelField]);
        y += 2;

        memoryView.Add(_memoryEnabledCheck);
        foreach (var v in _embeddingViews) memoryView.Add(v);
        foreach (var v in _rerankViews) memoryView.Add(v);

        EnableVerticalScroll(memoryView, y);

        tabView.AddTab(CreateTab(tabView, "向量记忆", memoryView), false);

        // ── 渠道配置 Tab ──
        var channelsView = new View
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            BorderStyle = Terminal.Gui.Drawing.LineStyle.Single,
        };
        y = 1;

        // TUI
        var tuiTitle = new Label { Text = "── TUI 终端 ──", X = 1, Y = y };
        channelsView.Add(tuiTitle);
        y += 1;

        _tuiLogCollapsedCheck = new CheckBox { Text = "默认收起日志区", X = 1, Y = y, Value = CheckState.UnChecked };
        channelsView.Add(_tuiLogCollapsedCheck);
        y += 2;

        var tuiQuitKeyLabel = new Label { Text = "退出键:", X = 1, Y = y };
        _tuiQuitKeyField = new TextField { X = 14, Y = y, Width = 20, Text = "Ctrl+Q" };
        channelsView.Add(tuiQuitKeyLabel, _tuiQuitKeyField);
        y += 2;

        var tuiToggleLogLabel = new Label { Text = "日志切换:", X = 1, Y = y };
        _tuiToggleLogKeyField = new TextField { X = 14, Y = y, Width = 20, Text = "Ctrl+L" };
        channelsView.Add(tuiToggleLogLabel, _tuiToggleLogKeyField);
        y += 2;

        var tuiCancelLabel = new Label { Text = "取消键:", X = 1, Y = y };
        _tuiCancelKeyField = new TextField { X = 14, Y = y, Width = 20, Text = "Esc" };
        channelsView.Add(tuiCancelLabel, _tuiCancelKeyField);
        y += 2;

        // Web 服务
        var webTitle = new Label { Text = "── Web 服务 ──", X = 1, Y = y };
        channelsView.Add(webTitle);
        y += 1;

        _webEnabledCheck = new CheckBox { Text = "启用 Web 服务", X = 1, Y = y, Value = CheckState.Checked };
        _webEnabledCheck.ValueChanged += OnWebEnabledChanged;
        channelsView.Add(_webEnabledCheck);
        y += 2;

        var webAddressLabel = new Label { Text = "监听地址:", X = 1, Y = y };
        _webListenAddressField = new TextField { X = 14, Y = y, Width = 20, Text = "localhost" };
        _webViews.AddRange([webAddressLabel, _webListenAddressField]);
        y += 2;

        var webPortLabel = new Label { Text = "端口:", X = 1, Y = y };
        _webPortField = new TextField { X = 14, Y = y, Width = 10, Text = "5000" };
        _webViews.AddRange([webPortLabel, _webPortField]);
        foreach (var v in _webViews) channelsView.Add(v);
        y += 2;

        // QQ Bot
        var qqTitle = new Label { Text = "── QQ Bot ──", X = 1, Y = y };
        channelsView.Add(qqTitle);
        y += 1;

        _qqBotEnabledCheck = new CheckBox { Text = "启用 QQ Bot", X = 1, Y = y, Value = CheckState.UnChecked };
        _qqBotEnabledCheck.ValueChanged += OnQQBotEnabledChanged;
        channelsView.Add(_qqBotEnabledCheck);
        y += 2;

        var qqAppIdLabel = new Label { Text = "AppId:", X = 1, Y = y };
        _qqBotAppIdField = new TextField { X = 14, Y = y, Width = Dim.Fill(2) };
        _qqBotViews.AddRange([qqAppIdLabel, _qqBotAppIdField]);
        y += 2;

        var qqSecretLabel = new Label { Text = "Secret:", X = 1, Y = y };
        _qqBotClientSecretField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Secret = true };
        _qqBotViews.AddRange([qqSecretLabel, _qqBotClientSecretField]);
        y += 2;

        _qqBotSandboxCheck = new CheckBox { Text = "沙箱模式", X = 1, Y = y, Value = CheckState.UnChecked };
        _qqBotViews.Add(_qqBotSandboxCheck);
        y += 1;

        foreach (var v in _qqBotViews) channelsView.Add(v);

        EnableVerticalScroll(channelsView, y);

        tabView.AddTab(CreateTab(tabView, "渠道配置", channelsView), false);

        // ── 按钮 ──
        var saveButton = new Button { Text = "保存", IsDefault = true };
        saveButton.Accepting += OnSave;

        var cancelButton = new Button { Text = "取消" };
        cancelButton.Accepting += (_, e) =>
        {
            App!.RequestStop();
            e.Handled = true;
        };

        AddButton(saveButton);
        AddButton(cancelButton);

        Add(tabView);

        // 初始化记忆字段可见性
        OnMemoryEnabledChanged(null, null!);
        OnWebEnabledChanged(null, null!);
        OnQQBotEnabledChanged(null, null!);
    }

    /// <summary>
    /// 从已有配置加载到 UI。
    /// </summary>
    public void LoadFrom(SharpclawConfig config)
    {
        // 默认配置
        var providerIdx = Array.IndexOf(ProviderNames, config.Default.Provider.ToLowerInvariant());
        if (providerIdx >= 0) _defaultProvider.Value = providerIdx;
        _defaultEndpoint.Text = config.Default.Endpoint;
        _defaultApiKey.Text = config.Default.ApiKey;
        _defaultModel.Text = config.Default.Model;

        // 各智能体
        LoadAgentPanel(_mainPanel, config.Agents.Main);
        LoadAgentPanel(_recallerPanel, config.Agents.Recaller);
        LoadAgentPanel(_saverPanel, config.Agents.Saver);
        LoadAgentPanel(_summarizerPanel, config.Agents.Summarizer);

        // 记忆
        _memoryEnabledCheck.Value = config.Memory.Enabled ? CheckState.Checked : CheckState.UnChecked;
        _embeddingEndpointField.Text = config.Memory.EmbeddingEndpoint;
        _embeddingApiKeyField.Text = config.Memory.EmbeddingApiKey;
        _embeddingModelField.Text = config.Memory.EmbeddingModel;
        _rerankEnabledCheck.Value = config.Memory.RerankEnabled ? CheckState.Checked : CheckState.UnChecked;
        _rerankEndpointField.Text = config.Memory.RerankEndpoint;
        _rerankApiKeyField.Text = config.Memory.RerankApiKey;
        _rerankModelField.Text = config.Memory.RerankModel;

        // QQ Bot
        _qqBotEnabledCheck.Value = config.Channels.QQBot.Enabled ? CheckState.Checked : CheckState.UnChecked;
        _qqBotAppIdField.Text = config.Channels.QQBot.AppId;
        _qqBotClientSecretField.Text = config.Channels.QQBot.ClientSecret;
        _qqBotSandboxCheck.Value = config.Channels.QQBot.Sandbox ? CheckState.Checked : CheckState.UnChecked;

        // Web 服务
        _webEnabledCheck.Value = config.Channels.Web.Enabled ? CheckState.Checked : CheckState.UnChecked;
        _webListenAddressField.Text = config.Channels.Web.ListenAddress;
        _webPortField.Text = config.Channels.Web.Port.ToString();

        // TUI
        _tuiLogCollapsedCheck.Value = config.Channels.Tui.LogCollapsed ? CheckState.Checked : CheckState.UnChecked;
        _tuiQuitKeyField.Text = config.Channels.Tui.QuitKey;
        _tuiToggleLogKeyField.Text = config.Channels.Tui.ToggleLogKey;
        _tuiCancelKeyField.Text = config.Channels.Tui.CancelKey;

        OnMemoryEnabledChanged(null, null!);
        OnWebEnabledChanged(null, null!);
        OnQQBotEnabledChanged(null, null!);
    }

    private void OnDefaultProviderChanged(object? sender, ValueChangedEventArgs<int?> e)
    {
        var idx = e.NewValue ?? 0;
        var name = ProviderNames[idx];
        var defaults = Providers[name];
        _defaultEndpoint.Text = defaults.Endpoint;
        _defaultModel.Text = defaults.Model;
    }

    private void OnMemoryEnabledChanged(object? sender, ValueChangedEventArgs<CheckState> e)
    {
        var enabled = _memoryEnabledCheck.Value == CheckState.Checked;
        foreach (var v in _embeddingViews) v.Visible = enabled;
        foreach (var v in _rerankViews) v.Visible = enabled && _rerankEnabledCheck.Value == CheckState.Checked;
    }

    private void OnRerankEnabledChanged(object? sender, ValueChangedEventArgs<CheckState> e)
    {
        var enabled = _rerankEnabledCheck.Value == CheckState.Checked;
        foreach (var v in _rerankViews) v.Visible = enabled;
    }

    private void OnQQBotEnabledChanged(object? sender, ValueChangedEventArgs<CheckState> e)
    {
        var enabled = _qqBotEnabledCheck.Value == CheckState.Checked;
        foreach (var v in _qqBotViews) v.Visible = enabled;
    }

    private void OnWebEnabledChanged(object? sender, ValueChangedEventArgs<CheckState> e)
    {
        var enabled = _webEnabledCheck.Value == CheckState.Checked;
        foreach (var v in _webViews) v.Visible = enabled;
    }

    private void OnSave(object? sender, CommandEventArgs e)
    {
        e.Handled = true;

        if (string.IsNullOrWhiteSpace(_defaultApiKey.Text))
        {
            MessageBox.ErrorQuery(App!, "错误", "默认 API Key 不能为空", "确定");
            return;
        }

        var memoryEnabled = _memoryEnabledCheck.Value == CheckState.Checked;
        var rerankEnabled = _rerankEnabledCheck.Value == CheckState.Checked;

        var config = new SharpclawConfig
        {
            Default = new DefaultAgentConfig
            {
                Provider = ProviderNames[_defaultProvider.Value ?? 0],
                Endpoint = _defaultEndpoint.Text ?? "",
                ApiKey = _defaultApiKey.Text ?? "",
                Model = _defaultModel.Text ?? "",
            },
            Agents = new AgentsConfig
            {
                Main = BuildAgentConfig(_mainPanel),
                Recaller = BuildAgentConfig(_recallerPanel),
                Saver = BuildAgentConfig(_saverPanel),
                Summarizer = BuildAgentConfig(_summarizerPanel),
            },
            Memory = new MemoryConfig
            {
                Enabled = memoryEnabled,
                EmbeddingEndpoint = memoryEnabled ? _embeddingEndpointField.Text ?? "" : "",
                EmbeddingApiKey = memoryEnabled ? _embeddingApiKeyField.Text ?? "" : "",
                EmbeddingModel = memoryEnabled ? _embeddingModelField.Text ?? "" : "",
                RerankEnabled = memoryEnabled && rerankEnabled,
                RerankEndpoint = memoryEnabled && rerankEnabled ? _rerankEndpointField.Text ?? "" : "",
                RerankApiKey = memoryEnabled && rerankEnabled ? _rerankApiKeyField.Text ?? "" : "",
                RerankModel = memoryEnabled && rerankEnabled ? _rerankModelField.Text ?? "" : "",
            },
            Channels = new ChannelsConfig
            {
                Tui = new TuiChannelConfig
                {
                    LogCollapsed = _tuiLogCollapsedCheck.Value == CheckState.Checked,
                    QuitKey = _tuiQuitKeyField.Text ?? "Ctrl+Q",
                    ToggleLogKey = _tuiToggleLogKeyField.Text ?? "Ctrl+L",
                    CancelKey = _tuiCancelKeyField.Text ?? "Esc",
                },
                Web = new WebChannelConfig
                {
                    Enabled = _webEnabledCheck.Value == CheckState.Checked,
                    ListenAddress = _webListenAddressField.Text ?? "localhost",
                    Port = int.TryParse(_webPortField.Text, out var webPort) ? webPort : 5000,
                },
                QQBot = new QQBotChannelConfig
                {
                    Enabled = _qqBotEnabledCheck.Value == CheckState.Checked,
                    AppId = _qqBotAppIdField.Text ?? "",
                    ClientSecret = _qqBotClientSecretField.Text ?? "",
                    Sandbox = _qqBotSandboxCheck.Value == CheckState.Checked,
                }
            }
        };

        config.Save();
        Saved = true;
        App!.RequestStop();
    }

    #region AgentPanel helpers

    private record AgentPanel(
        View Container,
        CheckBox? EnabledCheck,
        CheckBox UseCustomCheck,
        OptionSelector Provider,
        TextField Endpoint,
        TextField ApiKey,
        TextField Model,
        List<View> CustomViews);

    private AgentPanel CreateAgentPanel(string name, bool showEnabled = true)
    {
        var container = new View { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true, BorderStyle = Terminal.Gui.Drawing.LineStyle.Single };
        var y = 1;

        CheckBox? enabledCheck = null;
        if (showEnabled)
        {
            enabledCheck = new CheckBox { Text = $"启用{name}", X = 1, Y = y, Value = CheckState.Checked };
            container.Add(enabledCheck);
            y += 2;
        }

        var useCustomCheck = new CheckBox { Text = "使用独立配置（不勾选则继承默认配置）", X = 1, Y = y };
        container.Add(useCustomCheck);
        y += 2;

        var customViews = new List<View>();

        var providerLabel = new Label { Text = "AI 供应商:", X = 1, Y = y };
        var provider = new OptionSelector
        {
            X = 14, Y = y,
            Labels = ProviderLabels,
            Orientation = Orientation.Horizontal,
        };
        customViews.AddRange([providerLabel, provider]);
        y += 2;

        var endpointLabel = new Label { Text = "Endpoint:", X = 1, Y = y };
        var endpoint = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = Providers["anthropic"].Endpoint };
        customViews.AddRange([endpointLabel, endpoint]);
        y += 2;

        var apiKeyLabel = new Label { Text = "API Key:", X = 1, Y = y };
        var apiKey = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Secret = true };
        customViews.AddRange([apiKeyLabel, apiKey]);
        y += 2;

        var modelLabel = new Label { Text = "模型名称:", X = 1, Y = y };
        var model = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = Providers["anthropic"].Model };
        customViews.AddRange([modelLabel, model]);

        // 默认隐藏独立配置字段
        foreach (var v in customViews)
        {
            v.Visible = false;
            container.Add(v);
        }

        // 供应商切换时更新默认值
        provider.ValueChanged += (_, e) =>
        {
            var idx = e.NewValue ?? 0;
            var defaults = Providers[ProviderNames[idx]];
            endpoint.Text = defaults.Endpoint;
            model.Text = defaults.Model;
        };

        // 勾选"使用独立配置"时显示/隐藏字段
        useCustomCheck.ValueChanged += (_, _) =>
        {
            var custom = useCustomCheck.Value == CheckState.Checked;
            foreach (var v in customViews) v.Visible = custom;
        };

        return new AgentPanel(container, enabledCheck, useCustomCheck, provider, endpoint, apiKey, model, customViews);
    }

    private static AgentConfig BuildAgentConfig(AgentPanel panel)
    {
        var enabled = panel.EnabledCheck?.Value != CheckState.UnChecked;
        var useCustom = panel.UseCustomCheck.Value == CheckState.Checked;

        if (!useCustom)
            return new AgentConfig { Enabled = enabled };

        return new AgentConfig
        {
            Enabled = enabled,
            Provider = ProviderNames[panel.Provider.Value ?? 0],
            Endpoint = panel.Endpoint.Text ?? "",
            ApiKey = panel.ApiKey.Text ?? "",
            Model = panel.Model.Text ?? "",
        };
    }

    private static void LoadAgentPanel(AgentPanel panel, AgentConfig agent)
    {
        if (panel.EnabledCheck is not null)
            panel.EnabledCheck.Value = agent.Enabled ? CheckState.Checked : CheckState.UnChecked;

        var hasCustom = agent.Provider is not null || agent.Endpoint is not null
            || agent.ApiKey is not null || agent.Model is not null;

        panel.UseCustomCheck.Value = hasCustom ? CheckState.Checked : CheckState.UnChecked;

        if (hasCustom)
        {
            if (agent.Provider is not null)
            {
                var idx = Array.IndexOf(ProviderNames, agent.Provider.ToLowerInvariant());
                if (idx >= 0) panel.Provider.Value = idx;
            }
            if (agent.Endpoint is not null) panel.Endpoint.Text = agent.Endpoint;
            if (agent.ApiKey is not null) panel.ApiKey.Text = agent.ApiKey;
            if (agent.Model is not null) panel.Model.Text = agent.Model;

            foreach (var v in panel.CustomViews) v.Visible = true;
        }
    }

    #endregion

    private static Tab CreateTab(TabView tabView, string displayText, View view)
    {
        var tab = new Tab { DisplayText = displayText, View = view };
        tab.MouseEvent += (_, e) =>
        {
            if (e.Flags.HasFlag(MouseFlags.LeftButtonClicked) && tab.CanFocus)
            {
                tabView.SelectedTab = tab;
                tab.SetFocus();
                e.Handled = true;
            }
        };
        return tab;
    }

    /// <summary>
    /// 为 View 启用垂直滚动：设置内容高度、显示滚动条、焦点自动跟随。
    /// </summary>
    private static void EnableVerticalScroll(View view, int contentHeight)
    {
        view.Initialized += (_, _) =>
        {
            view.SetContentSize(new Size(view.GetContentSize().Width, contentHeight));
            view.VerticalScrollBar.Visible = true;

            foreach (var sub in view.SubViews)
            {
                sub.HasFocusChanged += (s, args) =>
                {
                    if (!args.NewValue || s is not View focused)
                        return;

                    var vy = view.Viewport.Y;
                    var vh = view.Viewport.Height;
                    var fy = focused.Frame.Y;
                    var fh = focused.Frame.Height;

                    if (fy < vy)
                        view.Viewport = view.Viewport with { Y = fy };
                    else if (fy + fh > vy + vh)
                        view.Viewport = view.Viewport with { Y = fy + fh - vh };
                };
            }
        };
    }
}
