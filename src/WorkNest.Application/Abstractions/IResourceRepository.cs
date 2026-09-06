using WorkNest.Domain;

namespace WorkNest.Application.Abstractions;

/// <summary>资源唯一键：文件/目录按规范化完整路径，网站按完整 URL，程序按“路径+参数+工作目录”。</summary>
public sealed record ResourceKey(ResourceType Type, string Target, string? Arguments, string? WorkingDirectory);

/// <summary>资源与其在某工作区/全局范围内的关联视图行。</summary>
public sealed record ResourceLinkRow(
    ResourceItem Item,
    bool IsPinned,
    int LinkSortOrder,
    int RunCount,
    DateTime? LinkLastUsedAt,
    IReadOnlyList<string> Tags,
    IReadOnlyList<int> SharedWorkspaceIds);

/// <summary>导入用例的单条待落库关联：资源模板（按唯一键查重复用）+ 标签 + 关联属性。</summary>
public sealed record PendingResourceLink(
    ResourceItem Item,
    IReadOnlyList<string> Tags,
    bool IsPinned,
    int SortOrder);

/// <summary>资源仓储；涉及统计与关联的写入必须在事务中完成。</summary>
public interface IResourceRepository
{
    /// <summary>某工作区内的全部资源及关联状态。</summary>
    Task<IReadOnlyList<ResourceLinkRow>> GetForWorkspaceAsync(int workspaceId);

    /// <summary>全部工作区的资源关联视图（供“全部工作区”搜索）。</summary>
    Task<IReadOnlyList<ResourceLinkRow>> GetAllLinksAsync();

    /// <summary>最近成功启动过的关联行（F09）：LinkLastUsedAt 非空按其倒序，最多返回 maxCount 条。</summary>
    Task<IReadOnlyList<ResourceLinkRow>> GetRecentlyUsedForWorkspaceAsync(int workspaceId, int maxCount);

    /// <summary>按唯一键查找已存在资源（存在即复用，不重复建档）。</summary>
    Task<ResourceItem?> FindByKeyAsync(ResourceKey key);

    Task<ResourceItem?> GetAsync(int id);

    /// <summary>插入资源与标签并回填 Id。</summary>
    Task<int> AddAsync(ResourceItem item, IReadOnlyList<string> tags);

    /// <summary>更新资源与标签（重建标签集合）。</summary>
    Task UpdateAsync(ResourceItem item, IReadOnlyList<string> tags);

    /// <summary>删除资源及其标签、使用记录与全部关联。</summary>
    Task DeleteAsync(int resourceId);

    Task AddLinkAsync(int workspaceId, int resourceId, bool isPinned, int sortOrder);

    /// <summary>
    /// 导入用例的一次性事务：replaceExisting=true 时先清空该工作区全部关联并清理已无归属的资源，
    /// 再按唯一键查重复用/缺失建档、缺失加关联。单个工作区的整批导入在同一事务内完成，
    /// 中途失败整体回滚，工作区保持导入前状态。返回实际新增的关联条数。
    /// </summary>
    Task<int> ImportLinksAsync(int workspaceId, IReadOnlyList<PendingResourceLink> links, bool replaceExisting);

    /// <summary>更新置顶状态；sortOrder 传 null 表示不变。</summary>
    Task UpdateLinkAsync(int workspaceId, int resourceId, bool isPinned, int? sortOrder);

    /// <summary>移除单个关联；若是最后一个关联则同时清理资源、标签与使用记录。</summary>
    Task RemoveLinkAsync(int workspaceId, int resourceId);

    Task<IReadOnlyList<int>> GetLinkedWorkspaceIdsAsync(int resourceId);

    /// <summary>成功启动后的一次性事务：RunCount+1、LinkLastUsedAt、Workspace.LastUsedAt、UsageRecord。</summary>
    Task RecordSuccessAsync(int workspaceId, int resourceId, DateTime utc, int? durationMs);
}
