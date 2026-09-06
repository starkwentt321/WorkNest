using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Validation;
using WorkNest.Domain;

namespace WorkNest.Application.Services;

/// <summary>
/// 资源用例实现：目标规范化、唯一键复用、共享资源跨工作区同步。
/// 名称/目标/参数属于 ResourceItem（共享），置顶与排序属于关联（按工作区隔离）。
/// </summary>
public sealed class ResourceService : IResourceService
{
    private readonly IResourceRepository _resourceRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IClock _clock;
    private readonly DebouncedBackupScheduler? _backupScheduler;

    public ResourceService(
        IResourceRepository resourceRepository,
        IWorkspaceRepository workspaceRepository,
        IClock clock,
        DebouncedBackupScheduler? backupScheduler = null)
    {
        _resourceRepository = resourceRepository;
        _workspaceRepository = workspaceRepository;
        _clock = clock;
        _backupScheduler = backupScheduler;
    }

    /// <summary>核心内容成功提交后通知延迟合并备份；校验失败/事务回滚不会走到这里（决策 65/R03）。</summary>
    private void NotifyContentChanged() => _backupScheduler?.Schedule();

    public async Task<IReadOnlyList<ResourceDto>> GetForWorkspaceAsync(int workspaceId)
    {
        var rows = await _resourceRepository.GetForWorkspaceAsync(workspaceId);
        return ResourceOrdering.ApplyDefault(rows.Select(MapDto)).ToList();
    }

    public async Task<IReadOnlyList<ResourceDto>> GetAllWorkspacesAsync()
    {
        var rows = await _resourceRepository.GetAllLinksAsync();
        // 同一资源关联多个工作区时按 Item.Id 去重，仅保留首个关联行，共享资源只出现一次；
        // “全部工作区”视图无单工作区置顶语义，统一按非置顶展示
        var deduped = rows
            .GroupBy(r => r.Item.Id)
            .Select(g => g.First())
            .Select(r => MapDto(r, isPinned: false, linkSortOrder: 0));

        return ResourceOrdering.ApplyDefault(deduped).ToList();
    }

    public async Task<IReadOnlyList<ResourceDto>> GetRecentlyUsedAsync(int workspaceId, int maxCount)
    {
        // 仓储已按最近成功启动时间倒序并截取条数（F09/决策 59/70），这里保持顺序返回，不做置顶重排
        var rows = await _resourceRepository.GetRecentlyUsedForWorkspaceAsync(workspaceId, maxCount);
        return rows.Select(MapDto).ToList();
    }

    public async Task<ResourceDto> AddAsync(ResourceEditInput input)
    {
        if (!TargetNormalizer.TryNormalize(input.Type, input.Target, out var normalized, out var error))
        {
            throw new ValidationException(error ?? "目标无效");
        }

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            // 决策 61：名称留空时按目标自动提取
            name = TargetNormalizer.SuggestName(input.Type, normalized);
        }

        var key = BuildKey(input.Type, normalized, input.Arguments, input.WorkingDirectory);
        var existing = await _resourceRepository.FindByKeyAsync(key);

        int resourceId;
        if (existing is not null)
        {
            var linkedWorkspaces = await _resourceRepository.GetLinkedWorkspaceIdsAsync(existing.Id);
            if (linkedWorkspaces.Contains(input.WorkspaceId))
            {
                throw new ValidationException("该资源已存在于当前工作区");
            }

            // 已存在但不属于当前工作区：复用 ResourceItem，仅新增关联（文档 7.2）
            await _resourceRepository.AddLinkAsync(input.WorkspaceId, existing.Id, isPinned: false, sortOrder: 0);
            resourceId = existing.Id;
        }
        else
        {
            var now = _clock.UtcNow;
            var item = new ResourceItem
            {
                Type = input.Type,
                Name = name,
                Target = normalized,
                Arguments = input.Arguments,
                WorkingDirectory = input.WorkingDirectory,
                CreatedAt = now,
                UpdatedAt = now,
            };
            resourceId = await _resourceRepository.AddAsync(item, input.Tags);
            await _resourceRepository.AddLinkAsync(input.WorkspaceId, resourceId, isPinned: false, sortOrder: 0);
        }

        NotifyContentChanged();

        // 重新查询而非手工拼 DTO，保证与列表视图字段（置顶/统计/共享范围）完全一致
        return await GetSingleDtoAsync(input.WorkspaceId, resourceId);
    }

    public async Task<ResourceDto> UpdateAsync(ResourceEditInput input)
    {
        var existing = input.Id is int id
            ? await _resourceRepository.GetAsync(id)
            : null;
        if (existing is null)
        {
            throw new ValidationException("资源不存在");
        }

        if (!TargetNormalizer.TryNormalize(input.Type, input.Target, out var normalized, out var error))
        {
            throw new ValidationException(error ?? "目标无效");
        }

        // 唯一键查重需排除自身，允许“未改动目标”的编辑通过
        var hit = await _resourceRepository.FindByKeyAsync(BuildKey(input.Type, normalized, input.Arguments, input.WorkingDirectory));
        if (hit is not null && hit.Id != existing.Id)
        {
            throw new ValidationException("已存在相同目标的资源");
        }

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = TargetNormalizer.SuggestName(input.Type, normalized);
        }

        // 资源属性修改同步影响所有共享工作区（文档 7.2）
        existing.Type = input.Type;
        existing.Name = name;
        existing.Target = normalized;
        existing.Arguments = input.Arguments;
        existing.WorkingDirectory = input.WorkingDirectory;
        existing.UpdatedAt = _clock.UtcNow;
        await _resourceRepository.UpdateAsync(existing, input.Tags);
        NotifyContentChanged();

        return await GetSingleDtoAsync(input.WorkspaceId, existing.Id);
    }

    public async Task RemoveFromWorkspaceAsync(int workspaceId, int resourceId)
    {
        // 移除最后一个关联时，仓储会连带清理资源、标签与使用记录（文档 7.2）
        await _resourceRepository.RemoveLinkAsync(workspaceId, resourceId);
        NotifyContentChanged();
    }

    public async Task SetPinnedAsync(int workspaceId, int resourceId, bool pinned)
    {
        if (pinned)
        {
            // 置顶追加到置顶组末尾：当前工作区已置顶的最大顺序 +1
            var rows = await _resourceRepository.GetForWorkspaceAsync(workspaceId);
            var next = rows.Where(r => r.IsPinned).Select(r => r.LinkSortOrder).DefaultIfEmpty(0).Max() + 1;
            await _resourceRepository.UpdateLinkAsync(workspaceId, resourceId, isPinned: true, next);
        }
        else
        {
            // 取消置顶不改顺序值，重置顶时保留原组内位置
            await _resourceRepository.UpdateLinkAsync(workspaceId, resourceId, isPinned: false, sortOrder: null);
        }

        NotifyContentChanged();
    }

    public async Task SetPinnedOrderAsync(int workspaceId, IReadOnlyList<int> pinnedResourceIds)
    {
        // 拖动排序：按给定顺序逐个写入 LinkSortOrder=下标，全部保持置顶
        for (var i = 0; i < pinnedResourceIds.Count; i++)
        {
            await _resourceRepository.UpdateLinkAsync(workspaceId, pinnedResourceIds[i], isPinned: true, i);
        }

        NotifyContentChanged();
    }

    public async Task<IReadOnlyList<string>> GetSharedWorkspaceNamesAsync(int resourceId, int excludeWorkspaceId)
    {
        var linkedIds = await _resourceRepository.GetLinkedWorkspaceIdsAsync(resourceId);
        var all = await _workspaceRepository.GetAllAsync();
        return all
            .Where(w => w.Id != excludeWorkspaceId && linkedIds.Contains(w.Id))
            .Select(w => w.Name)
            .ToList();
    }

    /// <summary>关联视图行转 DTO；PathExists 按类型判断本地目标可用性，网站恒为可用。</summary>
    private ResourceDto MapDto(ResourceLinkRow row) => MapDto(row, row.IsPinned, row.LinkSortOrder);

    /// <summary>映射核心：置顶状态与组内顺序由调用方决定（工作区视图取行值，全局视图固定非置顶）。</summary>
    private ResourceDto MapDto(ResourceLinkRow row, bool isPinned, int linkSortOrder) => new()
    {
        Id = row.Item.Id,
        Type = row.Item.Type,
        Name = row.Item.Name,
        Target = row.Item.Target,
        Arguments = row.Item.Arguments,
        WorkingDirectory = row.Item.WorkingDirectory,
        Tags = row.Tags,
        IsPinned = isPinned,
        LinkSortOrder = linkSortOrder,
        RunCount = row.RunCount,
        LastUsedAt = row.LinkLastUsedAt,
        WorkspaceIds = row.SharedWorkspaceIds,
        PathExists = row.Item.Type switch
        {
            ResourceType.Directory => Directory.Exists(row.Item.Target),
            ResourceType.File or ResourceType.Program => File.Exists(row.Item.Target),
            // 网站可用性不在本地判断，恒标记为存在
            _ => true,
        },
    };

    /// <summary>重查指定工作区中对应资源的行并映射；用于新增/编辑后的返回值。</summary>
    private async Task<ResourceDto> GetSingleDtoAsync(int workspaceId, int resourceId)
    {
        var rows = await _resourceRepository.GetForWorkspaceAsync(workspaceId);
        var row = rows.First(r => r.Item.Id == resourceId);
        return MapDto(row);
    }

    /// <summary>唯一键构成：程序为“路径+参数+工作目录”，目录/文件/网站只看目标。</summary>
    private static ResourceKey BuildKey(ResourceType type, string normalizedTarget, string? arguments, string? workingDirectory) =>
        new(type, normalizedTarget, arguments, workingDirectory);
}
