using WorkNest.Application.Dtos;

namespace WorkNest.Application.Validation;

/// <summary>
/// 资源默认排序（决策 29/33/51）：
/// 置顶组在前按手动顺序（LinkSortOrder）；普通组按成功次数倒序、最近成功时间倒序，
/// 同值时按名称稳定收尾。列头排序只改变各组内部顺序，不打散置顶分组。
/// </summary>
public static class ResourceOrdering
{
    public static IOrderedEnumerable<ResourceDto> ApplyDefault(IEnumerable<ResourceDto> items) =>
        items.OrderBy(x => x.IsPinned ? 0 : 1)
             .ThenBy(x => x.IsPinned ? x.LinkSortOrder : 0)
             .ThenByDescending(x => x.RunCount)
             .ThenByDescending(x => x.LastUsedAt ?? DateTime.MinValue)
             .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>列头排序：置顶组保持手动顺序，普通组在组内按指定列与方向排序。</summary>
    public static IEnumerable<ResourceDto> ApplyColumnSort<TKey>(
        IEnumerable<ResourceDto> items, Func<ResourceDto, TKey> keySelector, bool descending)
    {
        var pinned = items.Where(x => x.IsPinned).OrderBy(x => x.LinkSortOrder);
        var normal = descending
            ? items.Where(x => !x.IsPinned).OrderByDescending(keySelector)
            : items.Where(x => !x.IsPinned).OrderBy(keySelector);
        return pinned.Concat(normal);
    }
}
