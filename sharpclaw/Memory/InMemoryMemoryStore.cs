namespace sharpclaw.Memory;

/// <summary>
/// 基于内存的记忆存储：使用关键词匹配搜索，适合轻量场景。
/// 评分综合考虑：内容匹配、关键词匹配、重要度加权、时间衰减。
/// </summary>
public class InMemoryMemoryStore : IMemoryStore
{
    private readonly List<MemoryEntry> _entries = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>记忆库最大容量，超出时淘汰重要度最低且最旧的记忆</summary>
    public int MaxEntries { get; set; } = 200;

    public async Task AddAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_entries.Any(e => e.Content == entry.Content))
                return;

            _entries.Add(entry);

            if (_entries.Count > MaxEntries)
            {
                var toRemove = _entries
                    .OrderBy(e => e.Importance)
                    .ThenBy(e => e.CreatedAt)
                    .First();
                _entries.Remove(toRemove);
            }
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
            var existing = _entries.FirstOrDefault(e => e.Id == entry.Id);
            if (existing is null)
                return;

            existing.Content = entry.Content;
            existing.Category = entry.Category;
            existing.Importance = entry.Importance;
            existing.Keywords = entry.Keywords;
            existing.CreatedAt = DateTimeOffset.UtcNow;
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
                .OrderByDescending(e => e.CreatedAt)
                .Take(count)
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
            var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return _entries
                .Select(e => new
                {
                    Entry = e,
                    Score = ComputeRelevanceScore(e, queryTerms)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Entry.Importance)
                .Take(count)
                .Select(x => x.Entry)
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
            _entries.RemoveAll(e => e.Id == id);
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

    private static double ComputeRelevanceScore(MemoryEntry entry, string[] queryTerms)
    {
        if (queryTerms.Length == 0)
            return 0;

        double score = 0;
        var contentLower = entry.Content.ToLowerInvariant();
        var keywordsLower = entry.Keywords.Select(k => k.ToLowerInvariant()).ToList();

        foreach (var term in queryTerms)
        {
            var termLower = term.ToLowerInvariant();

            if (contentLower.Contains(termLower))
                score += 1.0;

            if (keywordsLower.Any(k => k == termLower))
                score += 2.0;
            else if (keywordsLower.Any(k => k.Contains(termLower)))
                score += 1.0;
        }

        if (score <= 0)
            return 0;

        score *= 0.5 + entry.Importance / 10.0;

        var ageHours = (DateTimeOffset.UtcNow - entry.CreatedAt).TotalHours;
        var timeFactor = 1.0 / (1.0 + Math.Log(1.0 + ageHours / 24.0));
        score *= timeFactor;

        return score;
    }
}
