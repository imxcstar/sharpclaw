using System.Text.Json;
using System.Text.Json.Nodes;
using sharpclaw.UI;

namespace sharpclaw.Core;

public class SharpclawConfig
{
    /// <summary>
    /// 当前配置版本。每次结构变更时递增，用于自动迁移。
    /// </summary>
    public const int CurrentVersion = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public int Version { get; set; } = CurrentVersion;
    public DefaultAgentConfig Default { get; set; } = new();
    public AgentsConfig Agents { get; set; } = new();
    public MemoryConfig Memory { get; set; } = new();
    public QQBotConfig QQBot { get; set; } = new();

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".sharpclaw", "config.json");

    public static bool Exists() => File.Exists(ConfigPath);

    /// <summary>
    /// 解析智能体的有效配置：智能体自身配置覆盖默认配置。
    /// </summary>
    public DefaultAgentConfig ResolveAgent(AgentConfig agent)
    {
        return new DefaultAgentConfig
        {
            Provider = agent.Provider ?? Default.Provider,
            Endpoint = agent.Endpoint ?? Default.Endpoint,
            ApiKey = agent.ApiKey ?? Default.ApiKey,
            Model = agent.Model ?? Default.Model,
        };
    }

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
        Default.ApiKey = DataProtector.Encrypt(Default.ApiKey);
        EncryptAgentKey(Agents.Main);
        EncryptAgentKey(Agents.Recaller);
        EncryptAgentKey(Agents.Saver);
        EncryptAgentKey(Agents.Summarizer);
        Memory.EmbeddingApiKey = DataProtector.Encrypt(Memory.EmbeddingApiKey);
        Memory.RerankApiKey = DataProtector.Encrypt(Memory.RerankApiKey);
        QQBot.ClientSecret = DataProtector.Encrypt(QQBot.ClientSecret);
    }

    private void DecryptKeys()
    {
        Default.ApiKey = DataProtector.Decrypt(Default.ApiKey);
        DecryptAgentKey(Agents.Main);
        DecryptAgentKey(Agents.Recaller);
        DecryptAgentKey(Agents.Saver);
        DecryptAgentKey(Agents.Summarizer);
        Memory.EmbeddingApiKey = DataProtector.Decrypt(Memory.EmbeddingApiKey);
        Memory.RerankApiKey = DataProtector.Decrypt(Memory.RerankApiKey);
        QQBot.ClientSecret = DataProtector.Decrypt(QQBot.ClientSecret);
    }

    private static void EncryptAgentKey(AgentConfig agent)
    {
        if (agent.ApiKey is not null)
            agent.ApiKey = DataProtector.Encrypt(agent.ApiKey);
    }

    private static void DecryptAgentKey(AgentConfig agent)
    {
        if (agent.ApiKey is not null)
            agent.ApiKey = DataProtector.Decrypt(agent.ApiKey);
    }
}

/// <summary>
/// 默认智能体配置，所有字段必填。
/// </summary>
public class DefaultAgentConfig
{
    public string Provider { get; set; } = "anthropic";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
}

/// <summary>
/// 单个智能体配置，字段为 null 时继承默认配置。
/// </summary>
public class AgentConfig
{
    public bool Enabled { get; set; } = true;
    public string? Provider { get; set; }
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
}

/// <summary>
/// 各智能体配置集合。
/// </summary>
public class AgentsConfig
{
    public AgentConfig Main { get; set; } = new();
    public AgentConfig Recaller { get; set; } = new();
    public AgentConfig Saver { get; set; } = new();
    public AgentConfig Summarizer { get; set; } = new();
}

public class QQBotConfig
{
    public bool Enabled { get; set; } = false;
    public string AppId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool Sandbox { get; set; } = false;
}

public class MemoryConfig
{
    public bool Enabled { get; set; } = false;
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

        // v3 → v4: 按智能体划分配置，顶层字段移入 default 对象
        [4] = json =>
        {
            // 将顶层 provider/endpoint/apiKey/model 移入 default
            var defaultObj = new JsonObject();
            foreach (var key in new[] { "provider", "endpoint", "apiKey", "model" })
            {
                if (json[key] is { } val)
                {
                    defaultObj[key] = val.DeepClone();
                    json.Remove(key);
                }
            }
            json["default"] = defaultObj;

            // 创建空的 agents 配置
            json["agents"] = new JsonObject
            {
                ["main"] = new JsonObject(),
                ["recaller"] = new JsonObject(),
                ["saver"] = new JsonObject(),
                ["summarizer"] = new JsonObject(),
            };
        },

        // v4 → v5: 新增 QQ Bot 配置
        [5] = json =>
        {
            if (!json.ContainsKey("qqBot"))
            {
                json["qqBot"] = new JsonObject
                {
                    ["enabled"] = false,
                    ["appId"] = "",
                    ["clientSecret"] = "",
                    ["sandbox"] = false,
                };
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

        AppLogger.Log($"[Config] 检测到旧版本配置 (v{version})，正在迁移到 v{SharpclawConfig.CurrentVersion}...");

        for (var v = version + 1; v <= SharpclawConfig.CurrentVersion; v++)
        {
            if (Migrations.TryGetValue(v, out var migrate))
            {
                migrate(root);
                AppLogger.Log($"[Config] 已完成 v{v - 1} → v{v} 迁移");
            }
        }

        root["version"] = SharpclawConfig.CurrentVersion;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
