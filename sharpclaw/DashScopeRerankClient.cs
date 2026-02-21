using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sharpclaw;

public record RerankResult(int Index, double RelevanceScore);

public class DashScopeRerankClient
{
    private static readonly Uri Endpoint = new("https://dashscope.aliyuncs.com/compatible-api/v1/reranks");

    private readonly HttpClient _httpClient;
    private readonly string _model;

    public DashScopeRerankClient(HttpClient httpClient, string apiKey, string model = "gte-rerank-v2")
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _model = model;
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
        using var response = await _httpClient.PostAsync(Endpoint, content, cancellationToken);
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
