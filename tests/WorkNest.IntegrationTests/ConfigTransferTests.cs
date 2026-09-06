using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Services;
using WorkNest.Domain;
using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.Database.Migrations;
using WorkNest.Infrastructure.Repositories;
using Xunit;

namespace WorkNest.IntegrationTests;

/// <summary>配置导入导出回环与冲突策略（F11/决策 63-85/116）+ 最近使用仓储查询（F09）。</summary>
public sealed class ConfigTransferTests : IDisposable
{
    private static readonly DateTime SeedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TempWorkNestRoot _root = new();
    private readonly SqliteWorkspaceRepository _workspaces;
    private readonly SqliteResourceRepository _resources;
    private readonly RecordingBackupService _backup = new();

    public ConfigTransferTests()
    {
        new DbMigrator(_root.CreateDb(), new IMigration[] { new Migration0001InitialSchema() }, _root.BackupsDir).Migrate();
        _workspaces = new SqliteWorkspaceRepository(_root.CreateDb());
        _resources = new SqliteResourceRepository(_root.CreateDb());
    }

    /// <summary>备份服务假实现：记录快照调用原因，供覆盖导入的安全快照断言（决策 85）。</summary>
    private sealed class RecordingBackupService : IBackupService
    {
        public List<string> Reasons { get; } = [];

        public Task<string> CreateSnapshotAsync(string reason)
        {
            Reasons.Add(reason);
            return Task.FromResult(Path.Combine(Path.GetTempPath(), $"fake-snapshot-{Guid.NewGuid():N}.db"));
        }

        public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync() =>
            Task.FromResult<IReadOnlyList<BackupInfo>>([]);

        public Task RestoreAsync(string backupFilePath) => Task.CompletedTask;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class MemorySettings : ISettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private ImportService CreateImportService(TempWorkNestRoot? root = null, SqliteWorkspaceRepository? workspaces = null, SqliteResourceRepository? resources = null) => new(
        workspaces ?? _workspaces,
        resources ?? _resources,
        _backup,
        new WorkspaceService(workspaces ?? _workspaces, new MemorySettings(), new FixedClock()),
        new FixedClock());

    private async Task<int> CreateWorkspaceAsync(string name, string color = "")
    {
        var workspace = new Workspace
        {
            Name = name,
            Color = color,
            CreatedAt = SeedTime,
            UpdatedAt = SeedTime,
        };
        return await _workspaces.AddAsync(workspace);
    }

    private async Task<int> AddResourceAsync(int workspaceId, ResourceType type, string target,
        string? arguments = null, IReadOnlyList<string>? tags = null, bool isPinned = false, int sortOrder = 0)
    {
        var item = new ResourceItem
        {
            Type = type,
            Name = "种子资源",
            Target = target,
            Arguments = arguments,
            CreatedAt = SeedTime,
            UpdatedAt = SeedTime,
        };
        var id = await _resources.AddAsync(item, tags ?? []);
        await _resources.AddLinkAsync(workspaceId, id, isPinned, sortOrder);
        return id;
    }

    /// <summary>标准种子：A（网站-置顶带标签 + 目录）与 B（程序带参数 + 共享目录），返回相关 Id。</summary>
    private async Task<(int WorkspaceA, int WorkspaceB, int WebsiteId, int DirectoryId)> SeedAsync()
    {
        var workspaceA = await CreateWorkspaceAsync("工作区A", "#FF0000");
        var workspaceB = await CreateWorkspaceAsync("工作区B");
        var websiteId = await AddResourceAsync(workspaceA, ResourceType.Website, "https://example.com/",
            tags: ["常用"], isPinned: true, sortOrder: 3);
        var directoryId = await AddResourceAsync(workspaceA, ResourceType.Directory, @"E:\Seed\Docs");
        await _resources.AddLinkAsync(workspaceB, directoryId, isPinned: false, 0); // 目录跨工作区共享
        await AddResourceAsync(workspaceB, ResourceType.Program, @"C:\Tools\Demo.exe", arguments: "-v");
        return (workspaceA, workspaceB, websiteId, directoryId);
    }

    private async Task<string> ExportStandardAsync()
    {
        var ids = (await _workspaces.GetAllAsync()).Select(w => w.Id).ToList();
        var filePath = Path.Combine(_root.RootPath, "export.json");
        var exportService = new ExportService(_workspaces, _resources);
        var count = await exportService.ExportAsync(ids, filePath);

        Assert.Equal(4, count); // A 2 条 + B 2 条
        var json = await File.ReadAllTextAsync(filePath);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("https://example.com/", json);
        Assert.Contains("常用", json); // 中文与路径不做 \u 转义（决策 96）
        return filePath;
    }

    [Fact]
    public async Task Analyze_SameDatabase_MarksConflictsAndReuse()
    {
        await SeedAsync();
        var filePath = await ExportStandardAsync();

        var preview = await CreateImportService().AnalyzeAsync(filePath);

        Assert.Equal(2, preview.Workspaces.Count);
        Assert.All(preview.Workspaces, w => Assert.True(w.ConflictsWithExisting));
        Assert.All(preview.Workspaces, w => Assert.Equal(0, w.NewResourceCount));
        Assert.All(preview.Workspaces, w => Assert.Equal(2, w.ReusableCount));
        Assert.All(preview.Workspaces, w => Assert.Equal(0, w.InvalidCount));
    }

    [Fact]
    public async Task Execute_SkipStrategy_LeavesDataUnchanged()
    {
        var (workspaceA, _, _, _) = await SeedAsync();
        var filePath = await ExportStandardAsync();
        var service = CreateImportService();
        var preview = await service.AnalyzeAsync(filePath);
        foreach (var workspace in preview.Workspaces)
        {
            workspace.Strategy = ImportStrategy.Skip;
        }

        var added = await service.ExecuteAsync(preview);

        Assert.Equal(0, added);
        Assert.Equal(2, (await _resources.GetForWorkspaceAsync(workspaceA)).Count);
        Assert.Empty(_backup.Reasons); // 无覆盖不触发快照
    }

    [Fact]
    public async Task Execute_MergeIntoFreshDatabase_DeduplicatesByKeyAndRestoresLinkState()
    {
        var (workspaceA, workspaceB, _, directoryId) = await SeedAsync();
        var filePath = await ExportStandardAsync();

        // 干净库：单独迁移与仓储
        using var freshRoot = new TempWorkNestRoot();
        new DbMigrator(freshRoot.CreateDb(), new IMigration[] { new Migration0001InitialSchema() }, freshRoot.BackupsDir).Migrate();
        var freshWorkspaces = new SqliteWorkspaceRepository(freshRoot.CreateDb());
        var freshResources = new SqliteResourceRepository(freshRoot.CreateDb());
        var service = CreateImportService(freshRoot, freshWorkspaces, freshResources);
        var preview = await service.AnalyzeAsync(filePath);
        Assert.All(preview.Workspaces, w => Assert.False(w.ConflictsWithExisting));

        var added = await service.ExecuteAsync(preview);

        Assert.Equal(4, added); // A 2 条 + B 2 条（共享目录在两个工作区各一条关联）
        var importedA = (await freshWorkspaces.GetAllAsync()).First(w => w.Name == "工作区A");
        Assert.Equal("#FF0000", importedA.Color);

        // 共享目录只建一个 ResourceItem，被两个工作区复用（决策 7/116）：
        // 4 条关联只对应 3 个唯一资源（网站、共享目录、程序）
        var directoryItem = await freshResources.FindByKeyAsync(
            new ResourceKey(ResourceType.Directory, @"E:\Seed\Docs", null, null));
        Assert.NotNull(directoryItem);
        Assert.Equal(2, (await freshResources.GetLinkedWorkspaceIdsAsync(directoryItem!.Id)).Count);
        var importedB = (await freshWorkspaces.GetAllAsync()).First(w => w.Name == "工作区B");
        var allImportedLinks = (await freshResources.GetForWorkspaceAsync(importedA.Id))
            .Concat(await freshResources.GetForWorkspaceAsync(importedB.Id))
            .ToList();
        Assert.Equal(4, allImportedLinks.Count);
        Assert.Equal(3, allImportedLinks.Select(r => r.Item.Id).Distinct().Count());

        // 置顶、组内顺序与标签随导出还原
        var link = (await freshResources.GetForWorkspaceAsync(importedA.Id)).First(r => r.Item.Type == ResourceType.Website);
        Assert.True(link.IsPinned);
        Assert.Equal(3, link.LinkSortOrder);
        Assert.Contains("常用", link.Tags);
    }

    [Fact]
    public async Task Execute_CopyStrategy_CreatesDeduplicatedDuplicate()
    {
        var (workspaceA, _, _, directoryId) = await SeedAsync();
        var filePath = await ExportStandardAsync();
        var service = CreateImportService();
        var preview = await service.AnalyzeAsync(filePath);
        preview.Workspaces.First(w => w.Name == "工作区A").Strategy = ImportStrategy.Copy;

        var added = await service.ExecuteAsync(preview);

        Assert.Equal(2, added);
        var all = await _workspaces.GetAllAsync();
        Assert.Contains(all, w => w.Name == "工作区A (副本)");
        var copy = all.First(w => w.Name == "工作区A (副本)");
        var copyLink = (await _resources.GetForWorkspaceAsync(copy.Id)).First(r => r.Item.Type == ResourceType.Directory);
        Assert.Equal(directoryId, copyLink.Item.Id); // 副本仍引用原 ResourceItem（决策 116）
    }

    [Fact]
    public async Task Execute_OverwriteStrategy_SnapshotsThenRebuildsAndCleansOrphans()
    {
        var (workspaceA, _, websiteId, directoryId) = await SeedAsync();
        var filePath = await ExportStandardAsync();

        // 先从 A 移除网站（唯一关联 → 资源连带删除），制造“覆盖后需重建 + 孤儿清理”的场景
        await _resources.RemoveLinkAsync(workspaceA, websiteId);
        Assert.Null(await _resources.GetAsync(websiteId));

        var service = CreateImportService();
        var preview = await service.AnalyzeAsync(filePath);
        preview.Workspaces.First(w => w.Name == "工作区A").Strategy = ImportStrategy.Overwrite;

        var added = await service.ExecuteAsync(preview);

        Assert.Equal(2, added);
        Assert.Equal(["pre-import"], _backup.Reasons); // 决策 85：覆盖前自动快照
        var links = await _resources.GetForWorkspaceAsync(workspaceA);
        Assert.Equal(2, links.Count);
        // 网站以全新 ResourceItem 重建；目录沿用原档案，B 的共享关联不受影响
        var newWebsite = links.First(r => r.Item.Type == ResourceType.Website);
        Assert.NotEqual(websiteId, newWebsite.Item.Id);
        Assert.NotNull(await _resources.GetAsync(directoryId));
        Assert.Contains(workspaceA, await _resources.GetLinkedWorkspaceIdsAsync(directoryId));
    }

    [Fact]
    public async Task Analyze_UnsupportedVersion_ThrowsWithReason()
    {
        var filePath = Path.Combine(_root.RootPath, "future.json");
        await File.WriteAllTextAsync(filePath, """{"schemaVersion": 99, "workspaces": []}""");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => CreateImportService().AnalyzeAsync(filePath));
        Assert.Contains("99", ex.Message);
        Assert.Contains("不受支持", ex.Message);
    }

    [Fact]
    public async Task Analyze_CorruptJson_ThrowsInvalidData()
    {
        var filePath = Path.Combine(_root.RootPath, "broken.json");
        await File.WriteAllTextAsync(filePath, "{ 这不是 JSON ");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateImportService().AnalyzeAsync(filePath));
    }

    [Fact]
    public async Task GetRecentlyUsedForWorkspaceAsync_ReturnsNewestFirstWithLimit()
    {
        var workspaceId = await CreateWorkspaceAsync("时间轴");
        var first = await AddResourceAsync(workspaceId, ResourceType.Directory, @"E:\T\One");
        var second = await AddResourceAsync(workspaceId, ResourceType.Directory, @"E:\T\Two");
        var third = await AddResourceAsync(workspaceId, ResourceType.Directory, @"E:\T\Three");
        var neverUsed = await AddResourceAsync(workspaceId, ResourceType.Directory, @"E:\T\Never");

        // 依次成功启动：first 最旧，third 最新；neverUsed 从未启动
        await _resources.RecordSuccessAsync(workspaceId, first, SeedTime.AddMinutes(1), null);
        await _resources.RecordSuccessAsync(workspaceId, second, SeedTime.AddMinutes(2), null);
        await _resources.RecordSuccessAsync(workspaceId, third, SeedTime.AddMinutes(3), null);

        var recent = await _resources.GetRecentlyUsedForWorkspaceAsync(workspaceId, 10);

        Assert.Equal([third, second, first], recent.Select(r => r.Item.Id).ToList()); // 倒序
        Assert.DoesNotContain(recent, r => r.Item.Id == neverUsed); // 从未启动不出现（决策 70）

        var limited = await _resources.GetRecentlyUsedForWorkspaceAsync(workspaceId, 2);
        Assert.Equal([third, second], limited.Select(r => r.Item.Id).ToList()); // LIMIT 生效
    }

    public void Dispose() => _root.Dispose();
}
