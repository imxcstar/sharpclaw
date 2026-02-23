using sharpclaw.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace sharpclaw.UI;

/// <summary>
/// 配置引导对话框：替代 Console 交互式配置。
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

    // 基础配置
    private readonly OptionSelector _providerRadio;
    private readonly TextField _endpointField;
    private readonly TextField _apiKeyField;
    private readonly TextField _modelField;

    // 记忆配置
    private readonly CheckBox _memoryEnabledCheck;
    private readonly TextField _embeddingEndpointField;
    private readonly TextField _embeddingApiKeyField;
    private readonly TextField _embeddingModelField;
    private readonly CheckBox _rerankEnabledCheck;
    private readonly TextField _rerankEndpointField;
    private readonly TextField _rerankApiKeyField;
    private readonly TextField _rerankModelField;

    // 记忆相关控件列表（用于显示/隐藏）
    private readonly List<View> _embeddingViews = [];
    private readonly List<View> _rerankViews = [];

    public bool Saved { get; private set; }

    public ConfigDialog()
    {
        Title = "Sharpclaw 配置引导";
        Width = Dim.Percent(80);
        Height = Dim.Percent(90);

        var y = 0;

        // ── 供应商选择 ──
        var providerLabel = new Label { Text = "AI 供应商:", X = 1, Y = y };
        _providerRadio = new OptionSelector
        {
            X = 14,
            Y = y,
            Labels = ProviderLabels,
            Orientation = Orientation.Horizontal,
        };
        _providerRadio.ValueChanged += OnProviderChanged;
        y += 2;

        // ── 基础配置 ──
        var endpointLabel = new Label { Text = "Endpoint:", X = 1, Y = y };
        _endpointField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = Providers["anthropic"].Endpoint };
        y += 2;

        var apiKeyLabel = new Label { Text = "API Key:", X = 1, Y = y };
        _apiKeyField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Secret = true };
        y += 2;

        var modelLabel = new Label { Text = "模型名称:", X = 1, Y = y };
        _modelField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = Providers["anthropic"].Model };
        y += 2;

        // ── 记忆配置 ──
        var memSeparator = new Label { Text = "── 记忆功能 ──", X = 1, Y = y };
        y += 1;

        _memoryEnabledCheck = new CheckBox { Text = "启用向量记忆（禁用后降级为总结压缩）", X = 1, Y = y, Value = CheckState.Checked };
        _memoryEnabledCheck.ValueChanged += OnMemoryEnabledChanged;
        y += 2;

        var embEndpointLabel = new Label { Text = "Embedding:", X = 1, Y = y };
        _embeddingEndpointField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = "https://dashscope.aliyuncs.com/compatible-mode/v1" };
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

        var rerankEndpointLabel = new Label { Text = "Rerank:", X = 1, Y = y };
        _rerankEndpointField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = "https://dashscope.aliyuncs.com/compatible-api/v1/reranks" };
        _rerankViews.AddRange([rerankEndpointLabel, _rerankEndpointField]);
        y += 2;

        var rerankApiKeyLabel = new Label { Text = "Rrk Key:", X = 1, Y = y };
        _rerankApiKeyField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Secret = true };
        _rerankViews.AddRange([rerankApiKeyLabel, _rerankApiKeyField]);
        y += 2;

        var rerankModelLabel = new Label { Text = "Rrk 模型:", X = 1, Y = y };
        _rerankModelField = new TextField { X = 14, Y = y, Width = Dim.Fill(2), Text = "qwen3-vl-rerank" };
        _rerankViews.AddRange([rerankModelLabel, _rerankModelField]);

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

        // 添加所有控件
        Add(
            providerLabel, _providerRadio,
            endpointLabel, _endpointField,
            apiKeyLabel, _apiKeyField,
            modelLabel, _modelField,
            memSeparator, _memoryEnabledCheck
        );

        foreach (var v in _embeddingViews) Add(v);
        foreach (var v in _rerankViews) Add(v);
    }

    private void OnProviderChanged(object? sender, ValueChangedEventArgs<int?> e)
    {
        var idx = e.NewValue ?? 0;
        var name = ProviderNames[idx];
        var defaults = Providers[name];
        _endpointField.Text = defaults.Endpoint;
        _modelField.Text = defaults.Model;
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

    private void OnSave(object? sender, CommandEventArgs e)
    {
        e.Handled = true;

        if (string.IsNullOrWhiteSpace(_apiKeyField.Text))
        {
            MessageBox.ErrorQuery(App!, "错误", "API Key 不能为空", "确定");
            return;
        }

        var providerName = ProviderNames[_providerRadio.Value ?? 0];
        var memoryEnabled = _memoryEnabledCheck.Value == CheckState.Checked;
        var rerankEnabled = _rerankEnabledCheck.Value == CheckState.Checked;

        var config = new SharpclawConfig
        {
            Provider = providerName,
            Endpoint = _endpointField.Text ?? "",
            ApiKey = _apiKeyField.Text ?? "",
            Model = _modelField.Text ?? "",
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
            }
        };

        config.Save();
        Saved = true;
        App!.RequestStop();
    }
}
