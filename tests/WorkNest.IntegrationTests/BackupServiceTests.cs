using WorkNest.Domain;
using WorkNest.Infrastructure.Backup;
using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.Database.Migrations;
using WorkNest.Infrastructure.Repositories;
using Xunit;

namespace WorkNest.IntegrationTests;

/// <summary>备份服务：快照保留策略、恢复源保护（R01）、非数据库文件拒恢复。</summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly TempWorkNestRoot _root = new();
    private readonly SqliteBackupService _backupService;
    private readonly SqliteWorkspaceRepository _workspaceRepository;

    public BackupServiceTests()
    {
        new DbMigrator(_root.CreateDb(), new IMigration[] { new Migration0001InitialSchema() }, _root.BackupsDir).Migrate();
        _backupService = new SqliteBackupService(_root.CreateDb(), _root.BackupsDir);
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

    [Fact]
    public async Task Restore_OldestBackupThatCleanupWouldDelete_Succeeds()
    {
        // 构造 R01 场景：先备份（作为恢复源），再凑满 10 份触发保留上限；
        // 恢复动作自身的 pre-restore 快照会把最旧备份（=恢复源）清理掉
        await CreateWorkspaceAsync("保留工作区");
        var oldestPath = await _backupService.CreateSnapshotAsync("milestone");
        for (var i = 0; i < 9; i++)
        {
            await _backupService.CreateSnapshotAsync("filler");
        }

        // 明确把恢复源标记为最旧，使其必然成为下次清理目标；随后新增内容供恢复后验证消失
        File.SetLastWriteTime(oldestPath, DateTime.Now.AddDays(-1));
        await CreateWorkspaceAsync("恢复后应消失");
        Assert.Equal(10, Directory.GetFiles(_root.BackupsDir, "auto-*.db").Length);

        await _backupService.RestoreAsync(oldestPath);

        // 数据回到备份时点：只剩“保留工作区”
        var workspaces = await _workspaceRepository.GetAllAsync();
        var workspace = Assert.Single(workspaces);
        Assert.Equal("保留工作区", workspace.Name);
        // 恢复源即便已被保留策略删除，恢复依然成功（源走的是临时副本）
        Assert.False(File.Exists(oldestPath));
    }

    [Fact]
    public async Task Restore_NonSqliteFile_ThrowsAndKeepsCurrentData()
    {
        await CreateWorkspaceAsync("现有数据");
        var fakeBackup = Path.Combine(_root.BackupsDir, "auto-broken-20200101000000.db");
        await File.WriteAllTextAsync(fakeBackup, "这不是数据库文件");

        await Assert.ThrowsAsync<InvalidDataException>(() => _backupService.RestoreAsync(fakeBackup));

        // 头校验失败发生在动主库之前，当前数据保持完好
        var workspaces = await _workspaceRepository.GetAllAsync();
        Assert.Equal("现有数据", Assert.Single(workspaces).Name);
    }

    public void Dispose() => _root.Dispose();
}
