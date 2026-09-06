using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Services;
using WorkNest.Application.Validation;
using WorkNest.Domain;
using WorkNest.Tests.Fakes;
using Xunit;

namespace WorkNest.Tests;

/// <summary>资源用例：目标唯一键复用、共享同步、置顶顺序；全部经内存假仓储。</summary>
public sealed class ResourceServiceTests
{
    private sealed record Env(
        ResourceService Service,
        InMemoryResourceRepository Resources,
        InMemoryWorkspaceRepository Workspaces,
        FakeClock Clock);

    private static Env CreateEnv()
    {
        var workspaces = new InMemoryWorkspaceRepository();
        var resources = new InMemoryResourceRepository(workspaces);
        var clock = new FakeClock();
        return new Env(new ResourceService(resources, workspaces, clock), resources, workspaces, clock);
    }

    /// <summary>种子工作区，返回其 Id。</summary>
    private static async Task<int> SeedWorkspaceAsync(Env env, string name)
    {
        var id = await env.Workspaces.AddAsync(new Workspace { Name = name, CreatedAt = env.Clock.UtcNow, UpdatedAt = env.Clock.UtcNow });
        return id;
    }

    private static ResourceEditInput Input(int workspaceId, ResourceType type, string target, string? name = null, string? arguments = null) => new()
    {
        WorkspaceId = workspaceId,
        Type = type,
        Name = name ?? string.Empty,
        Target = target,
        Arguments = arguments,
    };

    [Fact]
    public async Task Add_SameTargetAcrossWorkspaces_ReusesSingleItem()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");
        var ws2 = await SeedWorkspaceAsync(env, "二");

        var first = await env.Service.AddAsync(Input(ws1, ResourceType.Directory, @"C:\shared\docs"));
        var second = await env.Service.AddAsync(Input(ws2, ResourceType.Directory, @"C:\shared\docs"));

        // 决策：复用同一 ResourceItem，仅新增关联
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, env.Resources.ItemCount);

        // 共享范围从重查结果断言（先返回的 DTO 是当时的快照，不含后来的关联）
        var inWs1 = await env.Service.GetForWorkspaceAsync(ws1);
        var inWs2 = await env.Service.GetForWorkspaceAsync(ws2);
        Assert.Equal([ws1, ws2], inWs1.Single(d => d.Id == first.Id).WorkspaceIds.ToArray());
        Assert.Equal([ws1, ws2], inWs2.Single(d => d.Id == first.Id).WorkspaceIds.ToArray());
        Assert.All(inWs1, dto => Assert.False(dto.PathExists));
        Assert.Contains(inWs2, dto => dto.Id == first.Id);
    }

    [Fact]
    public async Task Add_DuplicateInSameWorkspace_Throws()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");
        await env.Service.AddAsync(Input(ws1, ResourceType.Directory, @"C:\shared\docs"));

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => env.Service.AddAsync(Input(ws1, ResourceType.Directory, @"C:\shared\docs")));

        Assert.Equal("该资源已存在于当前工作区", ex.Message);
        Assert.Equal(1, env.Resources.ItemCount);
    }

    [Fact]
    public async Task Add_ProgramSamePathDifferentArguments_AreDistinctResources()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");

        var release = await env.Service.AddAsync(Input(ws1, ResourceType.Program, @"C:\app\app.exe", arguments: "--profile=release"));
        var debug = await env.Service.AddAsync(Input(ws1, ResourceType.Program, @"C:\app\app.exe", arguments: "--profile=debug"));

        // 文档 7.2：程序按“路径+参数+工作目录”查重，参数不同即不同资源
        Assert.NotEqual(release.Id, debug.Id);
        Assert.Equal(2, env.Resources.ItemCount);
    }

    [Fact]
    public async Task Add_EmptyName_SuggestsFromTarget()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");

        var dto = await env.Service.AddAsync(Input(ws1, ResourceType.Program, @"C:\tools\tool.exe"));

        // 决策 61：名称留空自动提取文件名（去扩展名）
        Assert.Equal("tool", dto.Name);
    }

    [Fact]
    public async Task Add_InvalidTarget_ThrowsValidation()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");

        await Assert.ThrowsAsync<ValidationException>(
            () => env.Service.AddAsync(Input(ws1, ResourceType.Website, "ftp://example.com")));
    }

    [Fact]
    public async Task Add_Website_MarksPathExistsTrue()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");

        var dto = await env.Service.AddAsync(Input(ws1, ResourceType.Website, "example.com"));

        Assert.True(dto.PathExists);
        Assert.Equal("https://example.com/", dto.Target);
    }

    [Fact]
    public async Task Update_SyncsNameAcrossSharingWorkspaces()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");
        var ws2 = await SeedWorkspaceAsync(env, "二");
        var added = await env.Service.AddAsync(Input(ws1, ResourceType.Directory, @"C:\shared\docs"));
        await env.Service.AddAsync(Input(ws2, ResourceType.Directory, @"C:\shared\docs"));

        var updated = await env.Service.UpdateAsync(new ResourceEditInput
        {
            Id = added.Id,
            WorkspaceId = ws1,
            Type = ResourceType.Directory,
            Name = "共享文档改名",
            Target = @"C:\shared\docs",
        });

        Assert.Equal("共享文档改名", updated.Name);

        // 资源属性属于 ResourceItem：两个工作区看到的名称同步变化
        var inWs1 = await env.Service.GetForWorkspaceAsync(ws1);
        var inWs2 = await env.Service.GetForWorkspaceAsync(ws2);
        Assert.Equal("共享文档改名", inWs1.Single(d => d.Id == added.Id).Name);
        Assert.Equal("共享文档改名", inWs2.Single(d => d.Id == added.Id).Name);
    }

    [Fact]
    public async Task Update_TargetConflictsWithOtherResource_Throws()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");
        var a = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\a.txt", name: "A"));
        var b = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\b.txt", name: "B"));

        // 目标改为与资源 A 相同（大小写不同仍视为同一唯一键）→ 拒绝
        var ex = await Assert.ThrowsAsync<ValidationException>(() => env.Service.UpdateAsync(new ResourceEditInput
        {
            Id = b.Id,
            WorkspaceId = ws1,
            Type = ResourceType.File,
            Name = "B",
            Target = @"C:\A.TXT",
        }));

        Assert.Equal("已存在相同目标的资源", ex.Message);
        Assert.Equal("A", (await env.Service.GetForWorkspaceAsync(ws1)).Single(d => d.Id == a.Id).Name);
    }

    [Fact]
    public async Task Update_MissingResource_Throws()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");

        var ex = await Assert.ThrowsAsync<ValidationException>(() => env.Service.UpdateAsync(new ResourceEditInput
        {
            Id = 999,
            WorkspaceId = ws1,
            Type = ResourceType.File,
            Name = "x",
            Target = @"C:\x.txt",
        }));

        Assert.Equal("资源不存在", ex.Message);
    }

    [Fact]
    public async Task RemoveFromWorkspace_LastLink_CleansResource()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");
        var added = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\only\here.txt"));

        await env.Service.RemoveFromWorkspaceAsync(ws1, added.Id);

        // 最后一个关联被移除后，资源、标签与使用记录一并清理
        Assert.Null(env.Resources.FindItem(added.Id));
        Assert.Equal(0, env.Resources.ItemCount);
        Assert.Empty(await env.Service.GetForWorkspaceAsync(ws1));
    }

    [Fact]
    public async Task RemoveFromWorkspace_StillShared_KeepsResource()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");
        var ws2 = await SeedWorkspaceAsync(env, "二");
        var added = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\shared\a.txt"));
        await env.Service.AddAsync(Input(ws2, ResourceType.File, @"C:\shared\a.txt"));

        await env.Service.RemoveFromWorkspaceAsync(ws1, added.Id);

        Assert.NotNull(env.Resources.FindItem(added.Id));
        var inWs2 = await env.Service.GetForWorkspaceAsync(ws2);
        Assert.Contains(inWs2, d => d.Id == added.Id);
        Assert.Equal([ws2], inWs2.Single(d => d.Id == added.Id).WorkspaceIds.ToArray());
    }

    [Fact]
    public async Task SetPinnedAsync_AssignsIncreasingSortOrder()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");
        var a = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\a.txt"));
        var b = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\b.txt"));
        var c = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\c.txt"));

        await env.Service.SetPinnedAsync(ws1, b.Id, pinned: true);
        await env.Service.SetPinnedAsync(ws1, a.Id, pinned: true);
        await env.Service.SetPinnedAsync(ws1, c.Id, pinned: true);

        // 置顶顺序号按置顶动作依次递增
        Assert.Equal(1, env.Resources.FindLink(ws1, b.Id)!.SortOrder);
        Assert.Equal(2, env.Resources.FindLink(ws1, a.Id)!.SortOrder);
        Assert.Equal(3, env.Resources.FindLink(ws1, c.Id)!.SortOrder);
    }

    [Fact]
    public async Task SetPinnedAsync_Unpin_KeepsOrderValue()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");
        var a = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\a.txt"));
        var b = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\b.txt"));
        await env.Service.SetPinnedAsync(ws1, a.Id, pinned: true);
        await env.Service.SetPinnedAsync(ws1, b.Id, pinned: true);

        await env.Service.SetPinnedAsync(ws1, a.Id, pinned: false);

        // 取消置顶不改变组内顺序值，重新置顶可回到原相对位置附近
        var link = env.Resources.FindLink(ws1, a.Id)!;
        Assert.False(link.IsPinned);
        Assert.Equal(1, link.SortOrder);
    }

    [Fact]
    public async Task SetPinnedOrderAsync_PersistsGivenSequence()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");
        var a = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\a.txt"));
        var b = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\b.txt"));
        var c = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\c.txt"));
        foreach (var id in new[] { a.Id, b.Id, c.Id })
        {
            await env.Service.SetPinnedAsync(ws1, id, pinned: true);
        }

        await env.Service.SetPinnedOrderAsync(ws1, [c.Id, a.Id, b.Id]);

        // 按给定顺序落库为 LinkSortOrder=下标
        Assert.Equal(0, env.Resources.FindLink(ws1, c.Id)!.SortOrder);
        Assert.Equal(1, env.Resources.FindLink(ws1, a.Id)!.SortOrder);
        Assert.Equal(2, env.Resources.FindLink(ws1, b.Id)!.SortOrder);

        // 默认排序：置顶组在前且按手动顺序
        var ordered = await env.Service.GetForWorkspaceAsync(ws1);
        Assert.Equal([c.Id, a.Id, b.Id], ordered.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task GetSharedWorkspaceNames_ExcludesGivenWorkspace()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "工作区一");
        var ws2 = await SeedWorkspaceAsync(env, "工作区二");
        var ws3 = await SeedWorkspaceAsync(env, "工作区三");
        var added = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\shared\a.txt"));
        await env.Service.AddAsync(Input(ws2, ResourceType.File, @"C:\shared\a.txt"));
        await env.Service.AddAsync(Input(ws3, ResourceType.File, @"C:\shared\a.txt"));

        var names = await env.Service.GetSharedWorkspaceNamesAsync(added.Id, excludeWorkspaceId: ws1);

        Assert.Equal(["工作区二", "工作区三"], names.ToArray());
    }

    [Fact]
    public async Task GetAllWorkspaces_DedupesSharedResourceAndClearsPinned()
    {
        var env = CreateEnv();
        var ws1 = await SeedWorkspaceAsync(env, "一");
        var ws2 = await SeedWorkspaceAsync(env, "二");
        var added = await env.Service.AddAsync(Input(ws1, ResourceType.File, @"C:\shared\a.txt"));
        await env.Service.AddAsync(Input(ws2, ResourceType.File, @"C:\shared\a.txt"));
        await env.Service.SetPinnedAsync(ws1, added.Id, pinned: true);

        var all = await env.Service.GetAllWorkspacesAsync();

        // 共享资源只出现一次；“全部工作区”视图不带单工作区置顶语义
        var dto = Assert.Single(all);
        Assert.Equal(added.Id, dto.Id);
        Assert.False(dto.IsPinned);
        Assert.Equal(0, dto.LinkSortOrder);
        Assert.Equal([ws1, ws2], dto.WorkspaceIds.ToArray());
    }
}
