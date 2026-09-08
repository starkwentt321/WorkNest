using WorkNest.Domain;
using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.Database.Migrations;
using WorkNest.Infrastructure.Repositories;
using Xunit;

namespace WorkNest.IntegrationTests;

/// <summary>工作区仓储：增删改查、级联删除。</summary>
public sealed class WorkspaceRepositoryTests : IDisposable
{
    private readonly TempWorkNestRoot _root = new();
    private readonly SqliteWorkspaceRepository _repository;

    public WorkspaceRepositoryTests()
    {
        // 每个测试先建库到 v1，保证表结构可用
        new DbMigrator(
            _root.CreateDb(),
            new IMigration[] { new Migration0001InitialSchema() },
            _root.BackupsDir).Migrate();
        _repository = new SqliteWorkspaceRepository(_root.CreateDb());
    }

    private static Workspace NewWorkspace(string name = "工作台A", int sortOrder = 0) => new()
    {
        Name = name,
        Color = "#4CC2FF",
        SortOrder = sortOrder,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task AddGetUpdateDelete_Roundtrip()
    {
        var workspace = NewWorkspace();
        workspace.Id = await _repository.AddAsync(workspace);

        var loaded = await _repository.GetAsync(workspace.Id);
        Assert.NotNull(loaded);
        Assert.Equal("工作台A", loaded!.Name);
        Assert.Equal("#4CC2FF", loaded.Color);
        Assert.Equal(0, loaded.SortOrder);
        Assert.Equal(workspace.CreatedAt, loaded.CreatedAt);

        loaded.Name = "工作台B";
        loaded.Color = "#FF8800";
        loaded.SortOrder = 5;
        loaded.UpdatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        await _repository.UpdateAsync(loaded);

        var updated = await _repository.GetAsync(workspace.Id);
        Assert.NotNull(updated);
        Assert.Equal("工作台B", updated!.Name);
        Assert.Equal("#FF8800", updated.Color);
        Assert.Equal(5, updated.SortOrder);

        await _repository.DeleteAsync(workspace.Id);
        Assert.Null(await _repository.GetAsync(workspace.Id));
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllOrderedBySortOrder()
    {
        var second = NewWorkspace("B", sortOrder: 2);
        second.Id = await _repository.AddAsync(second);
        var first = NewWorkspace("A", sortOrder: 1);
        first.Id = await _repository.AddAsync(first);

        var all = await _repository.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal(first.Id, all[0].Id);
        Assert.Equal(second.Id, all[1].Id);
    }

    [Fact]
    public async Task Delete_CascadesWorkspaceResource()
    {
        var workspace = NewWorkspace();
        workspace.Id = await _repository.AddAsync(workspace);

        var resource = new ResourceItem
        {
            Type = ResourceType.Directory,
            Name = "演示目录",
            Target = @"C:\demo",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var resourceRepository = new SqliteResourceRepository(_root.CreateDb());
        var resourceId = await resourceRepository.AddAsync(resource, []);
        await resourceRepository.AddLinkAsync(workspace.Id, resourceId, isPinned: false, sortOrder: 0);

        Assert.Equal(1, await _repository.GetResourceCountAsync(workspace.Id));

        await _repository.DeleteAsync(workspace.Id);

        // 关联行随工作区级联消失
        Assert.Equal(0, await _repository.GetResourceCountAsync(workspace.Id));
        Assert.Empty(await resourceRepository.GetLinkedWorkspaceIdsAsync(resourceId));
    }

    public void Dispose() => _root.Dispose();
}
