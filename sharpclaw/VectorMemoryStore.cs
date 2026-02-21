using Microsoft.Extensions.AI;
using System.Numerics.Tensors;
using System.Text.Json;

namespace sharpclaw;

internal class StoredMemoryEntry
{
    public MemoryEntry Entry { get; set; } = new();
    public float[]? Vector { get; set; }
}

public class VectorMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly DashScopeRerankClient? _rerankClient;
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<StoredMemoryEntry> _entries = [];

    public int MaxEntries { get; set; } = 200;
    public int RerankCandidateMultiplier { get; set; } = 3;

    /// <summary>
    /// 语义去重阈值：余弦相似度超过此值时视为重复，合并而非新增。默认 0.85。
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.85f;

    public VectorMemoryStore(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string filePath,
        DashScopeRerankClient? rerankClient = null)
    {
        _embeddingGenerator = embeddingGenerator;
        _filePath = filePath;
        _rerankClient = rerankClient;
        LoadFromFile();
    }

    public async Task AddAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var embedding = await _embeddingGenerator.GenerateAsync(
                entry.Content, cancellationToken: cancellationToken);
            var vector = embedding.Vector.ToArray();

            // 语义去重：找到最相似的已有记忆
            var mostSimilar = _entries
                .Where(e => e.Vector is not null)
                .Select(e => new
                {
                    Stored = e,
                    Similarity = TensorPrimitives.CosineSimilarity(
                        new ReadOnlySpan<float>(vector),
                        new ReadOnlySpan<float>(e.Vector!))
                })
                .Where(x => x.Similarity >= SimilarityThreshold)
                .MaxBy(x => x.Similarity);

            if (mostSimilar is not null)
            {
                // 合并：保留更高重要度的内容，合并关键词，刷新时间戳
                var existing = mostSimilar.Stored.Entry;
                if (entry.Importance >= existing.Importance)
                {
                    existing.Content = entry.Content;
                    existing.Importance = entry.Importance;
                    existing.Category = entry.Category;
                    mostSimilar.Stored.Vector = vector;
                }
                existing.Keywords = existing.Keywords
                    .Union(entry.Keywords, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                existing.CreatedAt = DateTimeOffset.UtcNow;
                SaveToFile();
                return;
            }

            _entries.Add(new StoredMemoryEntry { Entry = entry, Vector = vector });

            if (_entries.Count > MaxEntries)
            {
                var toRemove = _entries
                    .OrderBy(e => e.Entry.Importance)
                    .ThenBy(e => e.Entry.CreatedAt)
                    .First();
                _entries.Remove(toRemove);
            }

            SaveToFile();
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
            var stored = _entries.FirstOrDefault(e => e.Entry.Id == entry.Id);
            if (stored is null)
                return;

            // 内容变了需要重新生成向量
            if (stored.Entry.Content != entry.Content)
            {
                var embedding = await _embeddingGenerator.GenerateAsync(
                    entry.Content, cancellationToken: cancellationToken);
                stored.Vector = embedding.Vector.ToArray();
            }

            stored.Entry.Content = entry.Content;
            stored.Entry.Category = entry.Category;
            stored.Entry.Importance = entry.Importance;
            stored.Entry.Keywords = entry.Keywords;
            stored.Entry.CreatedAt = DateTimeOffset.UtcNow;
            SaveToFile();
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
            return _entries
                .OrderByDescending(e => e.Entry.CreatedAt)
                .Take(count)
                .Select(e => e.Entry)
                .ToList()
                .AsReadOnly();
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
            if (_entries.Count == 0)
                return [];

            // Phase 1: Vector recall
            var queryEmbedding = await _embeddingGenerator.GenerateAsync(
                query, cancellationToken: cancellationToken);
            var queryVector = queryEmbedding.Vector.ToArray();

            var candidateCount = count * RerankCandidateMultiplier;
            var candidates = _entries
                .Where(e => e.Vector is not null)
                .Select(e => new
                {
                    Stored = e,
                    Score = TensorPrimitives.CosineSimilarity(
                        new ReadOnlySpan<float>(queryVector),
                        new ReadOnlySpan<float>(e.Vector!))
                })
                .OrderByDescending(x => x.Score)
                .Take(candidateCount)
                .ToList();

            if (candidates.Count == 0)
                return [];

            // Phase 2: Rerank (optional)
            if (_rerankClient is not null)
            {
                try
                {
                    var documents = candidates.Select(c => c.Stored.Entry.Content).ToList();
                    var rerankResults = await _rerankClient.RerankAsync(
                        query, documents, count, cancellationToken);

                    return rerankResults
                        .Select(r => candidates[r.Index].Stored.Entry)
                        .ToList()
                        .AsReadOnly();
                }
                catch
                {
                    // Rerank failed, fall back to vector results
                }
            }

            return candidates
                .Take(count)
                .Select(x => x.Stored.Entry)
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
            _entries.RemoveAll(e => e.Entry.Id == id);
            SaveToFile();
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
            return _entries.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            var json = File.ReadAllText(_filePath);
            _entries = JsonSerializer.Deserialize<List<StoredMemoryEntry>>(json, JsonOptions) ?? [];
        }
        catch
        {
            _entries = [];
        }
    }

    private void SaveToFile()
    {
        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
