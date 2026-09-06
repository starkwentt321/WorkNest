namespace WorkNest.Domain;

/// <summary>工作区排序模式：默认最近使用优先；用户手动排序后切换为手动模式。</summary>
public enum WorkspaceSortMode
{
    /// <summary>按最近成功启动时间倒序。</summary>
    LastUsed = 0,

    /// <summary>按用户手动指定的顺序。</summary>
    Manual = 1,
}
