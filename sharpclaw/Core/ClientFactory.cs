using Anthropic;
using GeminiDotnet;
using GeminiDotnet.Extensions.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using sharpclaw.Clients;
using sharpclaw.Memory;

namespace sharpclaw.Core;

/// <summary>
/// 根据配置创建各类 AI 客户端。
/// </summary>
public static class ClientFactory
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 根据已解析的智能体配置创建 IChatClient。
    /// </summary>
    public static IChatClient CreateChatClient(DefaultAgentConfig resolved)
    {
        return resolved.Provider.ToLowerInvariant() switch
        {
            "anthropic" => new AnthropicClient
            {
                AuthToken = resolved.ApiKey,
                BaseUrl = resolved.Endpoint,
                HttpClient = new HttpClient { Timeout = AgentTimeout },
            }.AsIChatClient(resolved.Model),

            "openai" => new OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(resolved.ApiKey),
                    new OpenAIClientOptions
                    {
                        Endpoint = new Uri(resolved.Endpoint),
                        NetworkTimeout = AgentTimeout
                    })
                .GetChatClient(resolved.Model)
                .AsIChatClient(),

            "gemini" => new GeminiChatClient(new GeminiClientOptions
            {
                Endpoint = new Uri(resolved.Endpoint),
                ApiKey = resolved.ApiKey,
                ModelId = resolved.Model,
            }),

            _ => throw new NotSupportedException($"不支持的供应商: {resolved.Provider}")
        };
    }

    /// <summary>
    /// 为指定智能体创建 IChatClient（自动合并默认配置）。
    /// </summary>
    public static IChatClient CreateAgentClient(SharpclawConfig config, AgentConfig agent)
    {
        return CreateChatClient(config.ResolveAgent(agent));
    }

    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(SharpclawConfig config)
    {
        var mem = config.Memory;
        return new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(mem.EmbeddingApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(mem.EmbeddingEndpoint),
                    NetworkTimeout = AgentTimeout
                })
            .GetEmbeddingClient(mem.EmbeddingModel)
            .AsIEmbeddingGenerator();
    }

    public static DashScopeRerankClient? CreateRerankClient(SharpclawConfig config)
    {
        var mem = config.Memory;
        if (!mem.RerankEnabled) return null;

        Uri? endpoint = !string.IsNullOrEmpty(mem.RerankEndpoint)
            ? new Uri(mem.RerankEndpoint)
            : null;

        return new DashScopeRerankClient(
            new HttpClient { Timeout = AgentTimeout }, mem.RerankApiKey, mem.RerankModel, endpoint);
    }

    public static VectorMemoryStore? CreateMemoryStore(SharpclawConfig config)
    {
        if (!config.Memory.Enabled)
            return null;

        return new VectorMemoryStore(
            CreateEmbeddingGenerator(config),
            filePath: Path.Combine(Path.GetDirectoryName(SharpclawConfig.ConfigPath)!, "memories.db"),
            rerankClient: CreateRerankClient(config));
    }
}
