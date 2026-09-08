using WorkNest.Domain;

namespace WorkNest.Application.Abstractions;

/// <summary>工作区仓储。</summary>
public interface IWorkspaceRepository
{
    Task<IReadOnlyList<Workspace>> GetAllAsync();

    Task<Workspace?> GetAsync(int id);

    /// <summary>插入并回填 Id。</summary>
    Task<int> AddAsync(Workspace workspace);

    Task UpdateAsync(Workspace workspace);

    /// <summary>级联删除关联（WorkspaceResource）；不触碰真实文件。</summary>
    Task DeleteAsync(int id);

    Task<int> GetResourceCountAsync(int workspaceId);
}
