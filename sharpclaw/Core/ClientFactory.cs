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
    public static IChatClient CreateChatClient(SharpclawConfig config)
    {
        return config.Provider.ToLowerInvariant() switch
        {
            "anthropic" => new AnthropicClient
            {
                AuthToken = config.ApiKey,
                BaseUrl = config.Endpoint,
            }.AsIChatClient(config.Model),

            "openai" => new OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(config.ApiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint) })
                .GetChatClient(config.Model)
                .AsIChatClient(),

            "gemini" => new GeminiChatClient(new GeminiClientOptions
            {
                Endpoint = new Uri(config.Endpoint),
                ApiKey = config.ApiKey,
                ModelId = config.Model,
            }),

            _ => throw new NotSupportedException($"不支持的供应商: {config.Provider}")
        };
    }

    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(SharpclawConfig config)
    {
        var mem = config.Memory;
        return new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(mem.EmbeddingApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(mem.EmbeddingEndpoint) })
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
            new HttpClient(), mem.RerankApiKey, mem.RerankModel, endpoint);
    }

    public static VectorMemoryStore CreateMemoryStore(SharpclawConfig config)
    {
        return new VectorMemoryStore(
            CreateEmbeddingGenerator(config),
            filePath: "memories.json",
            rerankClient: CreateRerankClient(config));
    }
}
