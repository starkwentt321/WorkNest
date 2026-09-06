namespace WorkNest.Domain;

/// <summary>工作区与资源的多对多关联：置顶、排序与使用统计按工作区独立保存。</summary>
public sealed class WorkspaceResource
{
    public int WorkspaceId { get; set; }

    public int ResourceId { get; set; }

    /// <summary>置顶资源始终位于列表顶部。</summary>
    public bool IsPinned { get; set; }

    /// <summary>置顶组内的手动顺序，越小越靠前。</summary>
    public int SortOrder { get; set; }

    /// <summary>在该工作区内成功启动的次数；失败不计入。</summary>
    public int RunCount { get; set; }

    /// <summary>在该工作区内最近一次成功启动时间（UTC）。</summary>
    public DateTime? LastUsedAt { get; set; }
}
