namespace sharpclaw.Core;

/// <summary>
/// 交互式配置引导：逐步收集 AI 供应商、API Key、记忆功能等配置。
/// </summary>
public static class ConfigWizard
{
    private record ProviderDefaults(string Endpoint, string Model);

    private static readonly Dictionary<string, ProviderDefaults> Providers = new()
    {
        ["anthropic"] = new("https://api.anthropic.com", "claude-opus-4-6"),
        ["openai"] = new("https://api.openai.com/v1", "gpt-5.3"),
        ["gemini"] = new("https://generativelanguage.googleapis.com", "gemini-3.1-pro-preview"),
    };

    public static Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== Sharpclaw 配置引导 ===");
        Console.WriteLine();

        // 1. 选择供应商
        Console.WriteLine("选择 AI 供应商:");
        Console.WriteLine("  1. Anthropic");
        Console.WriteLine("  2. OpenAI");
        Console.WriteLine("  3. Gemini");
        var providerIndex = ReadChoice("请选择", 1, 3, 1);
        var providerName = providerIndex switch
        {
            1 => "anthropic",
            2 => "openai",
            3 => "gemini",
            _ => "anthropic"
        };
        var defaults = Providers[providerName];

        // 2-4. Endpoint / API Key / Model
        var endpoint = ReadLine("API Endpoint", defaults.Endpoint);
        var apiKey = ReadLine("API Key", "");
        var model = ReadLine("模型名称", defaults.Model);

        // 5. 是否启用向量记忆
        Console.WriteLine();
        Console.WriteLine("=== 记忆功能配置 ===");
        Console.WriteLine("向量记忆使用 Embedding 模型实现语义搜索，需要额外配置。");
        Console.WriteLine("禁用后记忆压缩将降级为总结压缩（仅保留对话摘要）。");
        var memoryEnabled = ReadYesNo("启用向量记忆?", true);

        string embeddingEndpoint = "", embeddingApiKey = "", embeddingModel = "";
        var rerankEnabled = false;
        string rerankEndpoint = "", rerankApiKey = "", rerankModel = "";

        if (memoryEnabled)
        {
            // 6-8. Embedding 配置
            embeddingEndpoint = ReadLine("Embedding Endpoint",
                "https://dashscope.aliyuncs.com/compatible-mode/v1");
            embeddingApiKey = ReadLine("Embedding API Key", "");
            embeddingModel = ReadLine("Embedding 模型", "text-embedding-v4");

            // 9. 是否启用重排序
            rerankEnabled = ReadYesNo("启用重排序?", true);

            if (rerankEnabled)
            {
                // 10. 重排序配置
                rerankEndpoint = ReadLine("Rerank Endpoint",
                    "https://dashscope.aliyuncs.com/compatible-api/v1/reranks");
                rerankApiKey = ReadLine("Rerank API Key", embeddingApiKey, "同 Embedding API Key");
                rerankModel = ReadLine("Rerank 模型", "qwen3-vl-rerank");
            }
        }

        // 11. 保存
        var config = new SharpclawConfig
        {
            Provider = providerName,
            Endpoint = endpoint,
            ApiKey = apiKey,
            Model = model,
            Memory = new MemoryConfig
            {
                Enabled = memoryEnabled,
                EmbeddingEndpoint = embeddingEndpoint,
                EmbeddingApiKey = embeddingApiKey,
                EmbeddingModel = embeddingModel,
                RerankEnabled = rerankEnabled,
                RerankEndpoint = rerankEndpoint,
                RerankApiKey = rerankApiKey,
                RerankModel = rerankModel,
            }
        };

        config.Save();
        Console.WriteLine();
        Console.WriteLine($"配置已保存到 {SharpclawConfig.ConfigPath}");

        return Task.CompletedTask;
    }

    private static string ReadLine(string prompt, string defaultValue, string? defaultHint = null)
    {
        var hint = defaultHint ?? defaultValue;
        if (!string.IsNullOrEmpty(hint))
            Console.Write($"{prompt} [{hint}]: ");
        else
            Console.Write($"{prompt}: ");

        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) ? defaultValue : input;
    }

    private static int ReadChoice(string prompt, int min, int max, int defaultValue)
    {
        Console.Write($"{prompt} [{defaultValue}]: ");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input))
            return defaultValue;
        return int.TryParse(input, out var value) && value >= min && value <= max
            ? value
            : defaultValue;
    }

    private static bool ReadYesNo(string prompt, bool defaultValue)
    {
        var hint = defaultValue ? "Y" : "N";
        Console.Write($"{prompt} (Y/N) [{hint}]: ");
        var input = Console.ReadLine()?.Trim().ToUpperInvariant();
        return input switch
        {
            "Y" => true,
            "N" => false,
            _ => defaultValue,
        };
    }
}
