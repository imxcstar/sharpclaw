using System.Text.Json;

namespace sharpclaw.Core;

public class SharpclawConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Provider { get; set; } = "anthropic";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public MemoryConfig Memory { get; set; } = new();

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".sharpclaw", "config.json");

    public static bool Exists() => File.Exists(ConfigPath);

    public static SharpclawConfig Load()
    {
        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<SharpclawConfig>(json, JsonOptions)!;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}

public class MemoryConfig
{
    public string EmbeddingEndpoint { get; set; } = "";
    public string EmbeddingApiKey { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public bool RerankEnabled { get; set; }
    public string RerankEndpoint { get; set; } = "";
    public string RerankApiKey { get; set; } = "";
    public string RerankModel { get; set; } = "";
}
