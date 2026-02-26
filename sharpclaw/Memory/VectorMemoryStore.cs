using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Sharc;
using Sharc.Vector;
using System.Runtime.InteropServices;
using System.Text.Json;

using sharpclaw.Clients;

namespace sharpclaw.Memory;

/// <summary>
/// 基于 Sharc + SQLite 的向量记忆存储：
/// - Microsoft.Data.Sqlite 负责写操作（INSERT/UPDATE/DELETE）
/// - Sharc.Vector 负责向量相似度搜索（SIMD 加速）
/// - 嵌入向量以 BLOB 形式存储在 SQLite 中
/// </summary>
public class VectorMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS memories (
            id TEXT PRIMARY KEY,
            category TEXT NOT NULL,
            importance INTEGER NOT NULL,
            content TEXT NOT NULL,
            keywords TEXT NOT NULL,
            created_at TEXT NOT NULL,
            embedding BLOB NOT NULL
        )
        """;

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly DashScopeRerankClient? _rerankClient;
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _dirty = true;

    public int MaxEntries { get; set; } = 200;
    public int RerankCandidateMultiplier { get; set; } = 3;

    /// <summary>
    /// 语义去重阈值：余弦距离小于此值时视为重复，合并而非新增。默认 0.15（对应相似度 0.85）。
    /// </summary>
    public float DeduplicationDistance { get; set; } = 0.15f;

    public VectorMemoryStore(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string filePath,
        DashScopeRerankClient? rerankClient = null)
    {
        _embeddingGenerator = embeddingGenerator;
        _dbPath = filePath;
        _connectionString = $"Data Source={filePath}";
        _rerankClient = rerankClient;

        InitializeDatabase();
        MigrateFromJson();
    }

    public async Task AddAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var embedding = await _embeddingGenerator.GenerateAsync(
                entry.Content, cancellationToken: cancellationToken);
            var vector = embedding.Vector.ToArray();

            // 语义去重：用 Sharc 搜索最相似的已有记忆
            var mostSimilar = FindMostSimilarWithinThreshold(vector);
            if (mostSimilar is not null)
            {
                // 合并：保留更高重要度的内容，合并关键词，刷新时间戳
                var existing = mostSimilar.Value.Entry;
                if (entry.Importance >= existing.Importance)
                {
                    existing.Content = entry.Content;
                    existing.Importance = entry.Importance;
                    existing.Category = entry.Category;
                }
                existing.Keywords = existing.Keywords
                    .Union(entry.Keywords, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                existing.CreatedAt = DateTimeOffset.UtcNow;

                // 更新数据库（用新向量如果内容被替换）
                var finalVector = entry.Importance >= mostSimilar.Value.Entry.Importance
                    ? vector : null;
                UpdateRow(existing, finalVector);
                return;
            }

            InsertRow(entry, vector);

            // 容量限制：淘汰重要度最低且最旧的记忆
            EvictIfNeeded();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var existingContent = GetContentById(entry.Id);
            if (existingContent is null)
                return;

            float[]? newVector = null;
            if (existingContent != entry.Content)
            {
                var embedding = await _embeddingGenerator.GenerateAsync(
                    entry.Content, cancellationToken: cancellationToken);
                newVector = embedding.Vector.ToArray();
            }

            UpdateRow(entry, newVector);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetRecentAsync(
        int count, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, category, importance, content, keywords, created_at FROM memories ORDER BY created_at DESC LIMIT $count";
            cmd.Parameters.AddWithValue("$count", count);

            var results = new List<MemoryEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadMemoryEntry(reader));

            return results.AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        string query, int count, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var totalCount = GetCount();
            if (totalCount == 0)
                return [];

            // Phase 1: 用 Sharc 向量搜索
            var queryEmbedding = await _embeddingGenerator.GenerateAsync(
                query, cancellationToken: cancellationToken);
            var queryVector = queryEmbedding.Vector.ToArray();

            var candidateCount = count * RerankCandidateMultiplier;
            var candidates = VectorSearch(queryVector, candidateCount);

            if (candidates.Count == 0)
                return [];

            // 用 rowId 查回完整的 MemoryEntry
            var entries = LoadEntriesByRowIds(candidates.Select(c => c.RowId).ToList());

            // Phase 2: Rerank (optional)
            if (_rerankClient is not null && entries.Count > 0)
            {
                try
                {
                    var documents = entries.Select(e => e.Content).ToList();
                    var rerankResults = await _rerankClient.RerankAsync(
                        query, documents, count, cancellationToken);

                    return rerankResults
                        .Select(r => entries[r.Index])
                        .ToList()
                        .AsReadOnly();
                }
                catch
                {
                    // Rerank failed, fall back to vector results
                }
            }

            return entries
                .Take(count)
                .ToList()
                .AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM memories WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            _dirty = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return GetCount();
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Private helpers ──

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = CreateTableSql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 从旧的 memories.json 迁移数据到 SQLite。
    /// </summary>
    private void MigrateFromJson()
    {
        var jsonPath = Path.ChangeExtension(_dbPath, ".json");
        if (!File.Exists(jsonPath))
            return;

        try
        {
            var json = File.ReadAllText(jsonPath);
            var oldEntries = JsonSerializer.Deserialize<List<StoredMemoryEntryLegacy>>(json);
            if (oldEntries is null or [])
            {
                File.Move(jsonPath, jsonPath + ".bak", overwrite: true);
                return;
            }

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            foreach (var old in oldEntries)
            {
                if (old.Vector is null || old.Entry is null)
                    continue;

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO memories (id, category, importance, content, keywords, created_at, embedding)
                    VALUES ($id, $category, $importance, $content, $keywords, $created_at, $embedding)
                    """;
                cmd.Parameters.AddWithValue("$id", old.Entry.Id);
                cmd.Parameters.AddWithValue("$category", old.Entry.Category);
                cmd.Parameters.AddWithValue("$importance", old.Entry.Importance);
                cmd.Parameters.AddWithValue("$content", old.Entry.Content);
                cmd.Parameters.AddWithValue("$keywords", JsonSerializer.Serialize(old.Entry.Keywords, JsonOptions));
                cmd.Parameters.AddWithValue("$created_at", old.Entry.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$embedding", MemoryMarshal.AsBytes(old.Vector.AsSpan()).ToArray());
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            _dirty = true;

            File.Move(jsonPath, jsonPath + ".bak", overwrite: true);
        }
        catch
        {
            // 迁移失败不影响正常使用
        }
    }

    private void InsertRow(MemoryEntry entry, float[] vector)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memories (id, category, importance, content, keywords, created_at, embedding)
            VALUES ($id, $category, $importance, $content, $keywords, $created_at, $embedding)
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$category", entry.Category);
        cmd.Parameters.AddWithValue("$importance", entry.Importance);
        cmd.Parameters.AddWithValue("$content", entry.Content);
        cmd.Parameters.AddWithValue("$keywords", JsonSerializer.Serialize(entry.Keywords, JsonOptions));
        cmd.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$embedding", MemoryMarshal.AsBytes(vector.AsSpan()).ToArray());
        cmd.ExecuteNonQuery();
        _dirty = true;
    }

    private void UpdateRow(MemoryEntry entry, float[]? newVector)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        if (newVector is not null)
        {
            cmd.CommandText = """
                UPDATE memories SET category = $category, importance = $importance, content = $content,
                    keywords = $keywords, created_at = $created_at, embedding = $embedding
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$embedding", MemoryMarshal.AsBytes(newVector.AsSpan()).ToArray());
        }
        else
        {
            cmd.CommandText = """
                UPDATE memories SET category = $category, importance = $importance, content = $content,
                    keywords = $keywords, created_at = $created_at
                WHERE id = $id
                """;
        }

        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$category", entry.Category);
        cmd.Parameters.AddWithValue("$importance", entry.Importance);
        cmd.Parameters.AddWithValue("$content", entry.Content);
        cmd.Parameters.AddWithValue("$keywords", JsonSerializer.Serialize(entry.Keywords, JsonOptions));
        cmd.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        _dirty = true;
    }

    private void EvictIfNeeded()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM memories";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());

        if (count <= MaxEntries)
            return;

        using var deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = """
            DELETE FROM memories WHERE id IN (
                SELECT id FROM memories ORDER BY importance ASC, created_at ASC LIMIT $excess
            )
            """;
        deleteCmd.Parameters.AddWithValue("$excess", count - MaxEntries);
        deleteCmd.ExecuteNonQuery();
        _dirty = true;
    }

    private int GetCount()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memories";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private string? GetContentById(string id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM memories WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// 用 Sharc 执行向量相似度搜索。
    /// </summary>
    private IReadOnlyList<VectorMatch> VectorSearch(float[] queryVector, int k)
    {
        // 确保 Sqlite WAL checkpoint 完成，Sharc 能读到最新数据
        if (_dirty)
        {
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
            _dirty = false;
        }

        if (!File.Exists(_dbPath) || new FileInfo(_dbPath).Length == 0)
            return [];

        try
        {
            var dbBytes = File.ReadAllBytes(_dbPath);
            using var db = SharcDatabase.OpenMemory(dbBytes);
            using var vq = db.Vector("memories", "embedding", DistanceMetric.Cosine);
            var result = vq.NearestTo(queryVector, k: k);
            return result.Matches;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 查找距离阈值内最相似的记忆（用于语义去重）。
    /// </summary>
    private (MemoryEntry Entry, float Distance)? FindMostSimilarWithinThreshold(float[] vector)
    {
        var matches = VectorSearch(vector, 1);
        if (matches.Count == 0)
            return null;

        var match = matches[0];
        // Cosine distance: 0 = identical, 2 = opposite. 阈值 0.15 ≈ 相似度 0.85
        if (match.Distance > DeduplicationDistance)
            return null;

        var entry = LoadEntryByRowId(match.RowId);
        if (entry is null)
            return null;

        return (entry, match.Distance);
    }

    private MemoryEntry? LoadEntryByRowId(long rowId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, category, importance, content, keywords, created_at FROM memories WHERE rowid = $rowid";
        cmd.Parameters.AddWithValue("$rowid", rowId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMemoryEntry(reader) : null;
    }

    private List<MemoryEntry> LoadEntriesByRowIds(List<long> rowIds)
    {
        if (rowIds.Count == 0)
            return [];

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // 保持 rowIds 的顺序（即向量搜索的相关度顺序）
        var placeholders = string.Join(",", rowIds.Select((_, i) => $"$r{i}"));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, category, importance, content, keywords, created_at, rowid FROM memories WHERE rowid IN ({placeholders})";
        for (var i = 0; i < rowIds.Count; i++)
            cmd.Parameters.AddWithValue($"$r{i}", rowIds[i]);

        var map = new Dictionary<long, MemoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = ReadMemoryEntry(reader);
            var rid = reader.GetInt64(6);
            map[rid] = entry;
        }

        // 按原始 rowIds 顺序返回
        var results = new List<MemoryEntry>();
        foreach (var rid in rowIds)
        {
            if (map.TryGetValue(rid, out var entry))
                results.Add(entry);
        }
        return results;
    }

    private static MemoryEntry ReadMemoryEntry(SqliteDataReader reader)
    {
        return new MemoryEntry
        {
            Id = reader.GetString(0),
            Category = reader.GetString(1),
            Importance = reader.GetInt32(2),
            Content = reader.GetString(3),
            Keywords = JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [],
            CreatedAt = DateTimeOffset.Parse(reader.GetString(5))
        };
    }

    /// <summary>
    /// 旧版 JSON 格式的记忆条目，仅用于迁移。
    /// </summary>
    private class StoredMemoryEntryLegacy
    {
        public MemoryEntry? Entry { get; set; }
        public float[]? Vector { get; set; }
    }
}
