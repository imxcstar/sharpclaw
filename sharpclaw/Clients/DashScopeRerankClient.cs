using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sharpclaw.Clients;

/// <summary>重排序结果：原始索引 + 相关度分数</summary>
public record RerankResult(int Index, double RelevanceScore);

/// <summary>
/// 阿里云 DashScope 重排序客户端：对候选文档按查询相关度重新排序。
/// </summary>
public class DashScopeRerankClient
{
    private static readonly Uri DefaultEndpoint = new("https://dashscope.aliyuncs.com/compatible-api/v1/reranks");

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _model;

    public DashScopeRerankClient(HttpClient httpClient, string apiKey, string model = "gte-rerank-v2", Uri? endpoint = null)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _model = model;
        _endpoint = endpoint ?? DefaultEndpoint;
    }

    public async Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        int topN,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            model = _model,
            query,
            documents,
            top_n = topN
        };

        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<RerankResponse>(responseJson);

        return result?.Results?
            .Select(r => new RerankResult(r.Index, r.RelevanceScore))
            .ToList()
            .AsReadOnly() ?? [];
    }

    private class RerankResponse
    {
        [JsonPropertyName("results")]
        public List<RerankResultDto>? Results { get; set; }
    }

    private class RerankResultDto
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("relevance_score")]
        public double RelevanceScore { get; set; }
    }
}
