using WorkNest.Domain;

namespace WorkNest.Application.Dtos;

/// <summary>资源列表项：领域数据 + 关联状态 + 目标可用性。</summary>
public sealed class ResourceDto
{
    public required int Id { get; init; }

    public required ResourceType Type { get; init; }

    public required string Name { get; init; }

    public required string Target { get; init; }

    public string? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>是否已在该工作区置顶。</summary>
    public bool IsPinned { get; init; }

    /// <summary>置顶组内手动顺序；非置顶为 0。</summary>
    public int LinkSortOrder { get; init; }

    /// <summary>在该工作区内成功启动次数。</summary>
    public int RunCount { get; init; }

    /// <summary>在该工作区内最近一次成功启动时间（UTC）。</summary>
    public DateTime? LastUsedAt { get; init; }

    /// <summary>关联的全部工作区 Id，用于“全部工作区”范围展示与去重。</summary>
    public IReadOnlyList<int> WorkspaceIds { get; init; } = [];

    /// <summary>本地目标当前是否存在（网站类型恒为 true）；用于失效标记。</summary>
    public bool PathExists { get; init; }
}
