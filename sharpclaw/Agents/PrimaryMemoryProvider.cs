using Microsoft.Agents.AI;
using sharpclaw.UI;

namespace sharpclaw.Agents;

/// <summary>
/// 简单的主要记忆注入器：仅在 MemoryRecaller 未启用时使用，
/// 将 primary_memory.md 的内容注入到 AI 上下文中。
/// </summary>
public class PrimaryMemoryProvider : AIContextProvider
{
    private readonly string _primaryMemoryPath;

    public PrimaryMemoryProvider(string primaryMemoryPath)
        : base("PrimaryMemoryProvider")
    {
        _primaryMemoryPath = primaryMemoryPath;
    }

    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_primaryMemoryPath))
            return new AIContext();

        try
        {
            var content = await File.ReadAllTextAsync(_primaryMemoryPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
                return new AIContext();

            AppLogger.Log($"[PrimaryMemory] 注入主要记忆（{content.Length}字）");
            return new AIContext
            {
                Instructions = $"[主要记忆] 以下是持久化的长期重要信息：\n\n{content}"
            };
        }
        catch
        {
            return new AIContext();
        }
    }
}
