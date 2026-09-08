using Microsoft.Data.Sqlite;
using WorkNest.Application.Abstractions;
using WorkNest.Domain;
using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.Database.Migrations;
using WorkNest.Infrastructure.Repositories;
using Xunit;

namespace WorkNest.IntegrationTests;

/// <summary>资源仓储：唯一键查重、标签、关联生命周期、启动统计事务、共享工作区聚合。</summary>
public sealed class ResourceRepositoryTests : IDisposable
{
    private readonly TempWorkNestRoot _root = new();
    private readonly SqliteResourceRepository _repository;
    private readonly SqliteWorkspaceRepository _workspaceRepository;

    public ResourceRepositoryTests()
    {
        new DbMigrator(_root.CreateDb(), new IMigration[] { new Migration0001InitialSchema() }, _root.BackupsDir).Migrate();
        _repository = new SqliteResourceRepository(_root.CreateDb());
        _workspaceRepository = new SqliteWorkspaceRepository(_root.CreateDb());
    }

    private async Task<int> CreateWorkspaceAsync(string name)
    {
        var workspace = new Workspace
        {
            Name = name,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        return await _workspaceRepository.AddAsync(workspace);
    }

    private async Task<int> AddProgramAsync(string target, string? arguments,
        IReadOnlyList<string>? tags = null, string? workingDirectory = null)
    {
        var item = new ResourceItem
        {
            Type = ResourceType.Program,
            Name = "演示程序",
            Target = target,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        return await _repository.AddAsync(item, tags ?? []);
    }

    [Fact]
    public async Task GetAllKeysAsync_ReturnsEveryKeyWithNullPartsNormalizedToEmptyStrings()
    {
        // 参数/工作目录按 null 与空串混合写入，覆盖归一的两条路径
        await AddProgramAsync(@"C:\Tools\Alpha.exe", "-v");                       // 参数有值，工作目录 null
        await AddProgramAsync(@"C:\Tools\Beta.exe", null, workingDirectory: @"C:\temp"); // 参数 null，工作目录有值
        await AddProgramAsync(@"c:\tools\gamma.exe", "", workingDirectory: "");   // 均为空串，Target 小写写入

        var keys = await _repository.GetAllKeysAsync();

        Assert.Equal(3, keys.Count);
        // Target 原样返回（NOCASE 是比较侧语义，键本身不折叠大小写）；
        // Arguments/WorkingDirectory 按 SQL 口径 IFNULL 归一为空串（非 null）
        Assert.Contains(new ResourceKey(ResourceType.Program, @"C:\Tools\Alpha.exe", "-v", ""), keys);
        Assert.Contains(new ResourceKey(ResourceType.Program, @"C:\Tools\Beta.exe", "", @"C:\temp"), keys);
        Assert.Contains(new ResourceKey(ResourceType.Program, @"c:\tools\gamma.exe", "", ""), keys);
    }

    [Fact]
    public async Task FindByKey_MatchesCaseInsensitiveTarget()
    {
        var id = await AddProgramAsync(@"C:\Tools\Demo.exe", "-v");

        // 大小写不同的同一路径应命中同一资源
        var found = await _repository.FindByKeyAsync(
            new ResourceKey(ResourceType.Program, @"c:\tools\DEMO.EXE", "-v", null));

        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
    }

    [Fact]
    public async Task FindByKey_DifferentArguments_IsDifferentResource()
    {
        var firstId = await AddProgramAsync(@"C:\Tools\Demo.exe", "-v");

        // 同路径不同参数：查不到已有资源，视为不同资源
        var withOtherArgs = await _repository.FindByKeyAsync(
            new ResourceKey(ResourceType.Program, @"C:\Tools\Demo.exe", "-x", null));
        Assert.Null(withOtherArgs);

        var secondId = await AddProgramAsync(@"C:\Tools\Demo.exe", "-x");
        Assert.NotEqual(firstId, secondId);

        var found = await _repository.FindByKeyAsync(
            new ResourceKey(ResourceType.Program, @"C:\Tools\Demo.exe", "-x", null));
        Assert.NotNull(found);
        Assert.Equal(secondId, found!.Id);
    }

    [Fact]
    public async Task AddAsync_PersistsTags()
    {
        var workspaceId = await CreateWorkspaceAsync("工作台");
        var resourceId = await AddProgramAsync(@"C:\Tools\Demo.exe", null, ["工作", "工具"]);
        await _repository.AddLinkAsync(workspaceId, resourceId, isPinned: false, sortOrder: 0);

        var rows = await _repository.GetForWorkspaceAsync(workspaceId);

        var row = Assert.Single(rows);
        Assert.Equal(["工作", "工具"], row.Tags);
    }

    [Fact]
    public async Task UpdateAsync_RebuildsTagSet()
    {
        var resourceId = await AddProgramAsync(@"C:\Tools\Demo.exe", null, ["旧标签"]);

        var item = (await _repository.GetAsync(resourceId))!;
        item.Name = "改名后";
        item.UpdatedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        await _repository.UpdateAsync(item, ["新标签A", "新标签B"]);

        // 旧标签应被清空，新标签应完整写入
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ResourceTag WHERE Tag = '旧标签'"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM ResourceTag WHERE Tag LIKE '新标签%'"));
        Assert.Equal("改名后", (await _repository.GetAsync(resourceId))!.Name);
    }

    [Fact]
    public async Task RemoveLink_LastLink_RemovesResourceTagsAndUsage()
    {
        var workspaceId = await CreateWorkspaceAsync("工作台");
        var resourceId = await AddProgramAsync(@"C:\Tools\Demo.exe", null, ["标签"]);
        await _repository.AddLinkAsync(workspaceId, resourceId, isPinned: false, sortOrder: 0);
        await _repository.RecordSuccessAsync(workspaceId, resourceId,
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), durationMs: null);

        await _repository.RemoveLinkAsync(workspaceId, resourceId);

        // 资源、标签、使用记录全部随最后一个关联一并清理
        Assert.Null(await _repository.FindByKeyAsync(
            new ResourceKey(ResourceType.Program, @"C:\Tools\Demo.exe", null, null)));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ResourceItem"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ResourceTag"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM UsageRecord"));
    }

    [Fact]
    public async Task RemoveLink_WhenStillShared_KeepsResource()
    {
        var workspaceA = await CreateWorkspaceAsync("A");
        var workspaceB = await CreateWorkspaceAsync("B");
        var resourceId = await AddProgramAsync(@"C:\Tools\Shared.exe", null);
        await _repository.AddLinkAsync(workspaceA, resourceId, isPinned: false, sortOrder: 0);
        await _repository.AddLinkAsync(workspaceB, resourceId, isPinned: false, sortOrder: 1);

        await _repository.RemoveLinkAsync(workspaceA, resourceId);

        // 资源仍被 B 使用，只移除了 A 的关联
        Assert.Equal([workspaceB], await _repository.GetLinkedWorkspaceIdsAsync(resourceId));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ResourceItem"));
    }

    [Fact]
    public async Task RecordSuccess_UpdatesLinkWorkspaceAndUsageInOneTransaction()
    {
        var workspaceId = await CreateWorkspaceAsync("工作台");
        var resourceId = await AddProgramAsync(@"C:\Tools\Demo.exe", null);
        await _repository.AddLinkAsync(workspaceId, resourceId, isPinned: false, sortOrder: 0);
        var utc = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc);

        await _repository.RecordSuccessAsync(workspaceId, resourceId, utc, durationMs: 123);

        var rows = await _repository.GetForWorkspaceAsync(workspaceId);
        var row = Assert.Single(rows);
        Assert.Equal(1, row.RunCount);
        Assert.Equal(utc, row.LinkLastUsedAt);

        var workspace = (await _workspaceRepository.GetAsync(workspaceId))!;
        Assert.Equal(utc, workspace.LastUsedAt);

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM UsageRecord"));
        Assert.Equal(1L, Scalar("SELECT Result FROM UsageRecord"));
        Assert.Equal(123L, Scalar("SELECT DurationMs FROM UsageRecord"));
        Assert.Equal(utc, Scalar("SELECT StartedAt FROM UsageRecord") is string s
            ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind)
            : throw new InvalidOperationException("UsageRecord.StartedAt 不应为空"));
    }

    [Fact]
    public async Task GetAllLinks_ReturnsSharedWorkspaceIds()
    {
        var workspaceA = await CreateWorkspaceAsync("A");
        var workspaceB = await CreateWorkspaceAsync("B");
        var resourceId = await AddProgramAsync(@"C:\Tools\Shared.exe", null);
        await _repository.AddLinkAsync(workspaceA, resourceId, isPinned: false, sortOrder: 0);
        await _repository.AddLinkAsync(workspaceB, resourceId, isPinned: false, sortOrder: 1);

        var links = await _repository.GetAllLinksAsync();

        // 每行都应聚合出全部共享工作区 Id
        Assert.Equal(2, links.Count);
        Assert.All(links, row => Assert.Equal(
            new[] { workspaceA, workspaceB }.OrderBy(x => x),
            row.SharedWorkspaceIds.OrderBy(x => x)));
    }

    [Fact]
    public async Task ImportLinks_Merge_ReusesExistingAndAddsMissing()
    {
        var workspaceId = await CreateWorkspaceAsync("工作台");
        var existingId = await AddProgramAsync(@"C:\Tools\Existing.exe", null);
        await _repository.AddLinkAsync(workspaceId, existingId, isPinned: false, sortOrder: 0);

        var added = await _repository.ImportLinksAsync(workspaceId, new[]
        {
            Link(@"C:\Tools\Existing.exe", null),   // 已有关联：跳过
            Link(@"c:\tools\EXISTING.exe", null),   // 大小写不同同目标：仍跳过
            Link(@"C:\Tools\New.exe", null, ["新标签"]), // 缺失：建档+关联
        }, replaceExisting: false);

        Assert.Equal(1, added);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM ResourceItem"));
        Assert.Equal(2, (await _repository.GetForWorkspaceAsync(workspaceId)).Count);
    }

    [Fact]
    public async Task ImportLinks_Replace_ClearsOldLinksAndOrphanResource()
    {
        var workspaceId = await CreateWorkspaceAsync("工作台");
        var orphanId = await AddProgramAsync(@"C:\Tools\Old.exe", null, ["旧标签"]);
        await _repository.AddLinkAsync(workspaceId, orphanId, isPinned: false, sortOrder: 0);
        // 共享资源仍被其他工作区使用，覆盖后必须保留
        var otherWorkspaceId = await CreateWorkspaceAsync("其他");
        var sharedId = await AddProgramAsync(@"C:\Tools\Shared.exe", null);
        await _repository.AddLinkAsync(workspaceId, sharedId, isPinned: false, sortOrder: 1);
        await _repository.AddLinkAsync(otherWorkspaceId, sharedId, isPinned: false, sortOrder: 0);

        var added = await _repository.ImportLinksAsync(workspaceId, new[]
        {
            Link(@"C:\Tools\Incoming.exe", null),
        }, replaceExisting: true);

        Assert.Equal(1, added);
        // 独占旧资源与其标签随覆盖清理；共享资源保留
        Assert.Equal(0L, Scalar($"SELECT COUNT(*) FROM ResourceItem WHERE Id = {orphanId}"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ResourceTag"));
        Assert.NotNull(await _repository.GetAsync(sharedId));
        var rows = await _repository.GetForWorkspaceAsync(workspaceId);
        Assert.Equal(@"C:\Tools\Incoming.exe", Assert.Single(rows).Item.Target);
    }

    [Fact]
    public async Task ImportLinks_FailureAtNthRow_RollsBackWholeWorkspace()
    {
        var workspaceId = await CreateWorkspaceAsync("工作台");
        var beforeId = await AddProgramAsync(@"C:\Tools\Before.exe", null);
        await _repository.AddLinkAsync(workspaceId, beforeId, isPinned: false, sortOrder: 0);

        // Target 违反 NOT NULL：制造第 N 条写入失败，验证整批回滚（R02 验收）
        var links = new List<PendingResourceLink>
        {
            Link(@"C:\Tools\Valid.exe", null),
            new(new ResourceItem
            {
                Type = ResourceType.Program,
                Name = "坏数据",
                Target = null!,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            }, [], IsPinned: false, SortOrder: 0),
        };

        // Microsoft.Data.Sqlite 对 null 参数在绑定阶段抛 InvalidOperationException，整批回滚
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ImportLinksAsync(workspaceId, links, replaceExisting: true));

        // 工作区保持导入前状态：旧关联还在，新资源没有半截写入
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ResourceItem"));
        var rows = await _repository.GetForWorkspaceAsync(workspaceId);
        Assert.Equal(beforeId, Assert.Single(rows).Item.Id);
    }

    /// <summary>构造导入用待落库关联（程序类型，目标即唯一键）。</summary>
    private static PendingResourceLink Link(string target, string? arguments, IReadOnlyList<string>? tags = null)
        => new(new ResourceItem
        {
            Type = ResourceType.Program,
            Name = "导入资源",
            Target = target,
            Arguments = arguments,
            WorkingDirectory = null,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        }, tags ?? [], IsPinned: false, SortOrder: 0);

    private object? Scalar(string sql)
    {
        // WorkNestDb 是连接工厂本身，不持有连接，无需 dispose
        var db = _root.CreateDb();
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose() => _root.Dispose();
}
