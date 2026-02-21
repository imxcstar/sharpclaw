namespace sharpclaw.Memory;

/// <summary>
/// 记忆存储接口：定义记忆的增删改查操作。
/// </summary>
public interface IMemoryStore
{
    /// <summary>添加一条新记忆（实现可能进行语义去重）</summary>
    Task AddAsync(MemoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>按 ID 更新已有记忆</summary>
    Task UpdateAsync(MemoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>获取最近 N 条记忆，按时间倒序</summary>
    Task<IReadOnlyList<MemoryEntry>> GetRecentAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>语义搜索，返回最相关的 N 条记忆</summary>
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int count, CancellationToken cancellationToken = default);

    /// <summary>按 ID 删除记忆</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>返回记忆总数</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
