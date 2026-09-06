using WorkNest.Application.Abstractions;
using WorkNest.Domain;

namespace WorkNest.Tests.Fakes;

/// <summary>
/// 内存工作区仓储假实现，镜像真实数据库语义：
/// 读写快照（避免引用穿透）、删除时级联清理资源关联、Id 自增回填。
/// </summary>
public sealed class InMemoryWorkspaceRepository : IWorkspaceRepository
{
    private readonly List<Workspace> _workspaces = [];

    /// <summary>资源关联数据：与真实库同库，供 GetResourceCountAsync 与级联删除使用；测试可直接 Seed。</summary>
    private readonly List<WorkspaceResource> _links = [];

    public Task<IReadOnlyList<Workspace>> GetAllAsync() =>
        Task.FromResult<IReadOnlyList<Workspace>>(_workspaces.Select(Clone).ToList());

    public Task<Workspace?> GetAsync(int id) =>
        Task.FromResult(_workspaces.Where(w => w.Id == id).Select(Clone).FirstOrDefault());

    public Task<int> AddAsync(Workspace workspace)
    {
        workspace.Id = _workspaces.Count == 0 ? 1 : _workspaces.Max(w => w.Id) + 1;
        _workspaces.Add(Clone(workspace));
        return Task.FromResult(workspace.Id);
    }

    public Task UpdateAsync(Workspace workspace)
    {
        var index = _workspaces.FindIndex(w => w.Id == workspace.Id);
        if (index >= 0)
        {
            _workspaces[index] = Clone(workspace);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(int id)
    {
        _workspaces.RemoveAll(w => w.Id == id);
        // 镜像真实语义：级联删除该工作区的资源关联，不触碰共享资源本身
        _links.RemoveAll(l => l.WorkspaceId == id);
        return Task.CompletedTask;
    }

    public Task TouchLastUsedAsync(int id, DateTime utc)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == id);
        if (workspace is not null)
        {
            workspace.LastUsedAt = utc;
        }

        return Task.CompletedTask;
    }

    public Task<int> GetResourceCountAsync(int workspaceId) =>
        Task.FromResult(_links.Count(l => l.WorkspaceId == workspaceId));

    /// <summary>测试种子：直接注入关联行（真实库中由 AddLinkAsync 写入）。</summary>
    public void SeedLink(int workspaceId, int resourceId) =>
        _links.Add(new WorkspaceResource { WorkspaceId = workspaceId, ResourceId = resourceId });

    private static Workspace Clone(Workspace w) => new()
    {
        Id = w.Id,
        Name = w.Name,
        Color = w.Color,
        SortOrder = w.SortOrder,
        LastUsedAt = w.LastUsedAt,
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt,
    };
}
