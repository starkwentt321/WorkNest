using WorkNest.Application.Abstractions;
using WorkNest.Domain;

namespace WorkNest.Tests.Fakes;

/// <summary>
/// 内存资源仓储假实现，镜像真实数据库语义：
/// 唯一键匹配（Target 忽略大小写、null 参数归一空串）、
/// 移除最后一个关联时连带清理资源/标签/使用记录、
/// RecordSuccess 在一次“事务”内更新链接统计与工作区 LastUsedAt。
/// </summary>
public sealed class InMemoryResourceRepository : IResourceRepository
{
    private readonly List<ResourceItem> _items = [];
    private readonly Dictionary<int, List<string>> _tags = [];
    private readonly List<WorkspaceResource> _links = [];
    private readonly List<UsageRecord> _usageRecords = [];

    /// <summary>可选联动：真实库中 RecordSuccess 会同事务更新 Workspace.LastUsedAt，这里镜像该行为。</summary>
    private readonly InMemoryWorkspaceRepository? _workspaceRepository;

    /// <summary>测试开关：置为 true 时 RecordSuccessAsync 抛异常，用于验证启动用例吞掉持久化失败。</summary>
    public bool ThrowOnRecordSuccess { get; set; }

    /// <summary>测试开关：置为 true 时 ImportLinksAsync 抛异常，用于验证导入用例的失败语义。</summary>
    public bool ThrowOnImportLinks { get; set; }

    /// <summary>测试断言用：记录每次批量导入的（工作区, 是否覆盖）调用。</summary>
    public List<(int WorkspaceId, bool ReplaceExisting)> ImportCalls { get; } = [];

    public InMemoryResourceRepository(InMemoryWorkspaceRepository? workspaceRepository = null)
    {
        _workspaceRepository = workspaceRepository;
    }

    public Task<IReadOnlyList<ResourceLinkRow>> GetForWorkspaceAsync(int workspaceId)
    {
        var rows = _links
            .Where(l => l.WorkspaceId == workspaceId)
            .Select(l => BuildRow(l))
            .ToList();
        return Task.FromResult<IReadOnlyList<ResourceLinkRow>>(rows);
    }

    public Task<IReadOnlyList<ResourceLinkRow>> GetAllLinksAsync()
    {
        var rows = _links.Select(BuildRow).ToList();
        return Task.FromResult<IReadOnlyList<ResourceLinkRow>>(rows);
    }

    public Task<IReadOnlyList<ResourceLinkRow>> GetRecentlyUsedForWorkspaceAsync(int workspaceId, int maxCount)
    {
        // 镜像真实仓储语义：LastUsedAt 非空按其倒序截取前 N 条（F09）
        var rows = _links
            .Where(l => l.WorkspaceId == workspaceId && l.LastUsedAt.HasValue)
            .OrderByDescending(l => l.LastUsedAt)
            .Take(maxCount)
            .Select(l => BuildRow(l))
            .ToList();
        return Task.FromResult<IReadOnlyList<ResourceLinkRow>>(rows);
    }

    public Task<ResourceItem?> FindByKeyAsync(ResourceKey key)
    {
        // 唯一键归一：Target 忽略大小写（Windows 路径不区分），Arguments 按原文精确比较，null 统一归一空串
        var args = key.Arguments ?? string.Empty;
        var workDir = key.WorkingDirectory ?? string.Empty;
        var hit = _items.FirstOrDefault(i =>
            i.Type == key.Type
            && string.Equals(i.Target, key.Target, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.Arguments ?? string.Empty, args, StringComparison.Ordinal)
            && string.Equals(i.WorkingDirectory ?? string.Empty, workDir, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(hit is null ? null : Clone(hit));
    }

    /// <summary>镜像真实仓储语义：一次取回全部唯一键，Arguments/WorkingDirectory 归一空串（与 FindByKeyAsync 口径一致）。</summary>
    public Task<IReadOnlyList<ResourceKey>> GetAllKeysAsync()
    {
        IReadOnlyList<ResourceKey> keys = _items
            .Select(i => new ResourceKey(i.Type, i.Target, i.Arguments ?? string.Empty, i.WorkingDirectory ?? string.Empty))
            .ToList();
        return Task.FromResult(keys);
    }

    public Task<ResourceItem?> GetAsync(int id) =>
        Task.FromResult(_items.Where(i => i.Id == id).Select(Clone).FirstOrDefault());

    public Task<int> AddAsync(ResourceItem item, IReadOnlyList<string> tags)
    {
        item.Id = _items.Count == 0 ? 1 : _items.Max(i => i.Id) + 1;
        _items.Add(Clone(item));
        _tags[item.Id] = [.. tags];
        return Task.FromResult(item.Id);
    }

    public Task UpdateAsync(ResourceItem item, IReadOnlyList<string> tags)
    {
        var index = _items.FindIndex(i => i.Id == item.Id);
        if (index >= 0)
        {
            _items[index] = Clone(item);
        }

        // 镜像“重建标签集合”语义
        _tags[item.Id] = [.. tags];
        return Task.CompletedTask;
    }

    public Task AddLinkAsync(int workspaceId, int resourceId, bool isPinned, int sortOrder)
    {
        _links.Add(new WorkspaceResource
        {
            WorkspaceId = workspaceId,
            ResourceId = resourceId,
            IsPinned = isPinned,
            SortOrder = sortOrder,
        });
        return Task.CompletedTask;
    }

    /// <summary>镜像真实仓储语义：覆盖先清关联并清理孤儿，再按唯一键复用/建档；失败前不产生半截数据。</summary>
    public Task<int> ImportLinksAsync(int workspaceId, IReadOnlyList<PendingResourceLink> links, bool replaceExisting)
    {
        ImportCalls.Add((workspaceId, replaceExisting));
        if (ThrowOnImportLinks)
        {
            throw new InvalidOperationException("simulated import failure");
        }

        if (replaceExisting)
        {
            var removedResourceIds = _links
                .Where(l => l.WorkspaceId == workspaceId)
                .Select(l => l.ResourceId)
                .Distinct()
                .ToList();
            _links.RemoveAll(l => l.WorkspaceId == workspaceId);
            foreach (var resourceId in removedResourceIds)
            {
                if (_links.All(l => l.ResourceId != resourceId))
                {
                    _items.RemoveAll(i => i.Id == resourceId);
                    _tags.Remove(resourceId);
                    _usageRecords.RemoveAll(u => u.ResourceId == resourceId);
                }
            }
        }

        var processed = _links
            .Where(l => l.WorkspaceId == workspaceId)
            .Select(l => l.ResourceId)
            .ToHashSet();
        var added = 0;
        foreach (var link in links)
        {
            var template = link.Item;
            var existing = _items.FirstOrDefault(i =>
                i.Type == template.Type
                && string.Equals(i.Target, template.Target, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.Arguments ?? string.Empty, template.Arguments ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(i.WorkingDirectory ?? string.Empty, template.WorkingDirectory ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            int itemId;
            if (existing is null)
            {
                itemId = _items.Count == 0 ? 1 : _items.Max(i => i.Id) + 1;
                var created = Clone(template);
                created.Id = itemId;
                _items.Add(created);
                _tags[itemId] = [.. link.Tags];
            }
            else
            {
                itemId = existing.Id;
            }

            if (processed.Add(itemId))
            {
                _links.Add(new WorkspaceResource
                {
                    WorkspaceId = workspaceId,
                    ResourceId = itemId,
                    IsPinned = link.IsPinned,
                    SortOrder = link.SortOrder,
                });
                added++;
            }
        }

        return Task.FromResult(added);
    }

    public Task UpdateLinkAsync(int workspaceId, int resourceId, bool isPinned, int? sortOrder)
    {
        var link = _links.FirstOrDefault(l => l.WorkspaceId == workspaceId && l.ResourceId == resourceId);
        if (link is not null)
        {
            link.IsPinned = isPinned;
            // sortOrder 传 null 表示不变，与契约一致
            if (sortOrder.HasValue)
            {
                link.SortOrder = sortOrder.Value;
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveLinkAsync(int workspaceId, int resourceId)
    {
        _links.RemoveAll(l => l.WorkspaceId == workspaceId && l.ResourceId == resourceId);

        // 镜像真实语义：最后一个关联被移除后，资源及其标签、使用记录一并清理
        if (_links.All(l => l.ResourceId != resourceId))
        {
            _items.RemoveAll(i => i.Id == resourceId);
            _tags.Remove(resourceId);
            _usageRecords.RemoveAll(u => u.ResourceId == resourceId);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<int>> GetLinkedWorkspaceIdsAsync(int resourceId) =>
        Task.FromResult<IReadOnlyList<int>>(_links
            .Where(l => l.ResourceId == resourceId)
            .Select(l => l.WorkspaceId)
            .OrderBy(id => id)
            .ToList());

    public Task RecordSuccessAsync(int workspaceId, int resourceId, DateTime utc, int? durationMs)
    {
        if (ThrowOnRecordSuccess)
        {
            // 模拟数据库写入失败，验证调用方按 4.1.6 吞掉异常
            throw new InvalidOperationException("simulated database failure");
        }

        var link = _links.FirstOrDefault(l => l.WorkspaceId == workspaceId && l.ResourceId == resourceId);
        if (link is not null)
        {
            link.RunCount++;
            link.LastUsedAt = utc;
        }

        _usageRecords.Add(new UsageRecord
        {
            Id = _usageRecords.Count == 0 ? 1 : _usageRecords.Max(u => u.Id) + 1,
            ResourceId = resourceId,
            WorkspaceId = workspaceId,
            StartedAt = utc,
            Result = 1,
            DurationMs = durationMs,
        });

        // 同“事务”更新工作区最近使用时间（仅成功启动触发）
        if (_workspaceRepository is not null)
        {
            _workspaceRepository.SeedLastUsed(workspaceId, utc);
        }

        return Task.CompletedTask;
    }

    // ---- 以下成员仅供测试断言与种子，不属于 IResourceRepository 契约 ----

    /// <summary>测试种子：注入资源及其关联的工作区（真实库中由 AddAsync + AddLinkAsync 组合完成）。</summary>
    public void SeedResource(ResourceItem item, params int[] workspaceIds)
    {
        var index = _items.Count == 0 ? 1 : _items.Max(i => i.Id) + 1;
        item.Id = index;
        _items.Add(Clone(item));
        foreach (var workspaceId in workspaceIds)
        {
            _links.Add(new WorkspaceResource { WorkspaceId = workspaceId, ResourceId = index });
        }
    }

    /// <summary>测试种子：为资源追加标签。</summary>
    public void SeedTags(int resourceId, params string[] tags)
    {
        if (!_tags.TryGetValue(resourceId, out var list))
        {
            list = [];
            _tags[resourceId] = list;
        }

        list.AddRange(tags);
    }

    /// <summary>测试断言用：当前存活的资源数量（验证“末关联清理”是否生效）。</summary>
    public int ItemCount => _items.Count;

    /// <summary>测试断言用：按 Id 查找存活资源。</summary>
    public ResourceItem? FindItem(int resourceId) => _items.FirstOrDefault(i => i.Id == resourceId);

    /// <summary>测试断言用：按 Id 查找关联行。</summary>
    public WorkspaceResource? FindLink(int workspaceId, int resourceId) =>
        _links.FirstOrDefault(l => l.WorkspaceId == workspaceId && l.ResourceId == resourceId);

    /// <summary>测试断言用：使用记录只统计成功启动（决策 31）。</summary>
    public IReadOnlyList<UsageRecord> UsageRecords => _usageRecords;

    private ResourceLinkRow BuildRow(WorkspaceResource link)
    {
        var item = _items.First(i => i.Id == link.ResourceId);
        var sharedIds = _links
            .Where(l => l.ResourceId == item.Id)
            .Select(l => l.WorkspaceId)
            .OrderBy(id => id)
            .ToList();
        var tags = _tags.TryGetValue(item.Id, out var list) ? list.ToList() : [];
        return new ResourceLinkRow(Clone(item), link.IsPinned, link.SortOrder, link.RunCount, link.LastUsedAt, tags, sharedIds);
    }

    private static ResourceItem Clone(ResourceItem i) => new()
    {
        Id = i.Id,
        Type = i.Type,
        Name = i.Name,
        Target = i.Target,
        Arguments = i.Arguments,
        WorkingDirectory = i.WorkingDirectory,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt,
    };
}
