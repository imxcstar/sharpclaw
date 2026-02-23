using System.Text.Json;
using System.Text.Json.Nodes;

namespace sharpclaw.Core;

public class SharpclawConfig
{
    /// <summary>
    /// 当前配置版本。每次结构变更时递增，用于自动迁移。
    /// </summary>
    public const int CurrentVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public int Version { get; set; } = CurrentVersion;
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
        var oldVersion = ConfigMigrator.ReadVersion(json);
        var migrated = ConfigMigrator.MigrateIfNeeded(json, oldVersion);
        var config = JsonSerializer.Deserialize<SharpclawConfig>(migrated, JsonOptions)!;
        config.DecryptKeys();

        if (oldVersion != CurrentVersion)
            config.Save();

        return config;
    }

    public void Save()
    {
        Version = CurrentVersion;
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);

        EncryptKeys();
        var json = JsonSerializer.Serialize(this, JsonOptions);
        DecryptKeys();

        File.WriteAllText(ConfigPath, json);
    }

    private void EncryptKeys()
    {
        ApiKey = DataProtector.Encrypt(ApiKey);
        Memory.EmbeddingApiKey = DataProtector.Encrypt(Memory.EmbeddingApiKey);
        Memory.RerankApiKey = DataProtector.Encrypt(Memory.RerankApiKey);
    }

    private void DecryptKeys()
    {
        ApiKey = DataProtector.Decrypt(ApiKey);
        Memory.EmbeddingApiKey = DataProtector.Decrypt(Memory.EmbeddingApiKey);
        Memory.RerankApiKey = DataProtector.Decrypt(Memory.RerankApiKey);
    }
}

public class MemoryConfig
{
    public bool Enabled { get; set; } = true;
    public string EmbeddingEndpoint { get; set; } = "";
    public string EmbeddingApiKey { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public bool RerankEnabled { get; set; }
    public string RerankEndpoint { get; set; } = "";
    public string RerankApiKey { get; set; } = "";
    public string RerankModel { get; set; } = "";
}

/// <summary>
/// 配置版本迁移器：按版本号顺序执行迁移函数，将旧配置升级到最新版本。
/// </summary>
public static class ConfigMigrator
{
    private static readonly Dictionary<int, Action<JsonObject>> Migrations = new()
    {
        // v1 → v2: 新增 memory.enabled 字段（默认 true，兼容旧配置）
        [2] = json =>
        {
            var memory = json["memory"]?.AsObject();
            if (memory is not null && !memory.ContainsKey("enabled"))
            {
                memory["enabled"] = true;
            }
        },

        // v2 → v3: 加密所有明文 API Key
        [3] = json =>
        {
            EncryptField(json, "apiKey");
            var memory = json["memory"]?.AsObject();
            if (memory is not null)
            {
                EncryptField(memory, "embeddingApiKey");
                EncryptField(memory, "rerankApiKey");
            }
        },
    };

    private static void EncryptField(JsonObject obj, string key)
    {
        var value = obj[key]?.GetValue<string>();
        if (!string.IsNullOrEmpty(value) && !DataProtector.IsEncrypted(value))
        {
            obj[key] = DataProtector.Encrypt(value);
        }
    }

    /// <summary>
    /// 从 JSON 中读取版本号，缺失时视为 v1。
    /// </summary>
    public static int ReadVersion(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject();
        return root?["version"]?.GetValue<int>() ?? 1;
    }

    /// <summary>
    /// 检测 JSON 中的版本号，按顺序执行所有需要的迁移，返回迁移后的 JSON 字符串。
    /// </summary>
    public static string MigrateIfNeeded(string json, int version)
    {
        if (version >= SharpclawConfig.CurrentVersion)
            return json;

        var root = JsonNode.Parse(json)?.AsObject();
        if (root is null) return json;

        Console.WriteLine($"[Config] 检测到旧版本配置 (v{version})，正在迁移到 v{SharpclawConfig.CurrentVersion}...");

        for (var v = version + 1; v <= SharpclawConfig.CurrentVersion; v++)
        {
            if (Migrations.TryGetValue(v, out var migrate))
            {
                migrate(root);
                Console.WriteLine($"[Config] 已完成 v{v - 1} → v{v} 迁移");
            }
        }

        root["version"] = SharpclawConfig.CurrentVersion;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
