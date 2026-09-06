using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Services;
using WorkNest.Application.Validation;
using WorkNest.Tests.Fakes;
using Xunit;

namespace WorkNest.Tests;

/// <summary>
/// 延迟合并备份调度器（决策 65/66）：连续 Schedule 合并为一次快照、失败改走事件不上抛、
/// 设置写入成功后自动触发调度。
/// </summary>
public sealed class DebouncedBackupSchedulerTests
{
    /// <summary>备份服务假实现：计数快照次数，可注入异常验证事件路径。</summary>
    private sealed class FakeBackupService : IBackupService
    {
        public int SnapshotCount;
        public string? LastReason;
        public Exception? ThrowOnSnapshot;

        public Task<string> CreateSnapshotAsync(string reason)
        {
            if (ThrowOnSnapshot is not null)
            {
                throw ThrowOnSnapshot;
            }

            SnapshotCount++;
            LastReason = reason;
            return Task.FromResult(@"C:\fake\worknest-backup.db");
        }

        public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync() =>
            Task.FromResult<IReadOnlyList<BackupInfo>>([]);

        public Task RestoreAsync(string backupFilePath) => Task.CompletedTask;
    }

    [Fact]
    public async Task Schedule_CoalescesRapidCalls_IntoSingleSnapshot()
    {
        var fake = new FakeBackupService();
        using var scheduler = new DebouncedBackupScheduler(fake, TimeSpan.FromMilliseconds(60));

        scheduler.Schedule();
        scheduler.Schedule();
        scheduler.Schedule();
        await Task.Delay(200);

        Assert.Equal(1, fake.SnapshotCount);
        Assert.Equal("settings", fake.LastReason);
    }

    [Fact]
    public async Task SnapshotFailure_RaisesEvent_DoesNotThrow()
    {
        var fake = new FakeBackupService { ThrowOnSnapshot = new InvalidOperationException("磁盘空间不足") };
        using var scheduler = new DebouncedBackupScheduler(fake, TimeSpan.FromMilliseconds(40));

        Exception? observed = null;
        scheduler.SnapshotFailed += (_, ex) => observed = ex;
        scheduler.Schedule();
        await Task.Delay(150);

        Assert.Same(fake.ThrowOnSnapshot, observed);
    }

    [Fact]
    public async Task SetAsync_TriggersScheduledBackup()
    {
        var settingsRepository = new InMemorySettingsRepository();
        var fake = new FakeBackupService();
        using var scheduler = new DebouncedBackupScheduler(fake, TimeSpan.FromMilliseconds(40));
        var service = new SettingsService(settingsRepository, scheduler);

        await service.SetAsync(SettingKeys.CloseToTray, false);
        await Task.Delay(150);

        Assert.Equal(1, fake.SnapshotCount);
    }

    [Fact]
    public async Task SetAsync_SessionStateKey_DoesNotScheduleBackup()
    {
        // 窗口布局/会话标志属于运行状态（R03）：写入不触发备份，避免仅浏览也重置防抖计时
        var settingsRepository = new InMemorySettingsRepository();
        var fake = new FakeBackupService();
        using var scheduler = new DebouncedBackupScheduler(fake, TimeSpan.FromMilliseconds(40));
        var service = new SettingsService(settingsRepository, scheduler);

        await service.SetAsync(SettingKeys.WindowBounds, """{"X":10}""");
        await service.SetAsync(SettingKeys.SessionCleanExit, true);
        await Task.Delay(150);

        Assert.Equal(0, fake.SnapshotCount);
    }

    [Fact]
    public async Task Suspend_SkipsSnapshotsUntilDisposed()
    {
        var fake = new FakeBackupService();
        using var scheduler = new DebouncedBackupScheduler(fake, TimeSpan.FromMilliseconds(40));

        using (scheduler.Suspend())
        {
            scheduler.Schedule();
            await Task.Delay(150);
            // 挂起期间到期的快照被跳过（恢复备份等排他阶段）
            Assert.Equal(0, fake.SnapshotCount);
        }

        // 解除后恢复正常调度
        scheduler.Schedule();
        await Task.Delay(150);
        Assert.Equal(1, fake.SnapshotCount);
    }

    [Fact]
    public async Task ResourceService_ContentChange_SchedulesBackup()
    {
        // R03：新增资源成功提交后应触发延迟合并备份
        var workspaces = new InMemoryWorkspaceRepository();
        var resources = new InMemoryResourceRepository(workspaces);
        var fake = new FakeBackupService();
        using var scheduler = new DebouncedBackupScheduler(fake, TimeSpan.FromMilliseconds(40));
        var service = new ResourceService(resources, workspaces, new FakeClock(), scheduler);

        var workspace = await workspaces.AddAsync(new Domain.Workspace { Name = "工作台" });
        await service.AddAsync(new ResourceEditInput
        {
            WorkspaceId = workspace,
            Type = Domain.ResourceType.Directory,
            Name = "文档",
            Target = @"C:\docs",
        });
        await Task.Delay(150);

        Assert.Equal(1, fake.SnapshotCount);
    }

    [Fact]
    public async Task ResourceService_ValidationFailure_DoesNotScheduleBackup()
    {
        // 失败的事务/校验不触发成功备份通知（R03 验收）
        var workspaces = new InMemoryWorkspaceRepository();
        var resources = new InMemoryResourceRepository(workspaces);
        var fake = new FakeBackupService();
        using var scheduler = new DebouncedBackupScheduler(fake, TimeSpan.FromMilliseconds(40));
        var service = new ResourceService(resources, workspaces, new FakeClock(), scheduler);

        var workspace = await workspaces.AddAsync(new Domain.Workspace { Name = "工作台" });
        await Assert.ThrowsAsync<ValidationException>(() => service.AddAsync(new ResourceEditInput
        {
            WorkspaceId = workspace,
            Type = Domain.ResourceType.Directory,
            Name = "坏目标",
            Target = "   ",
        }));
        await Task.Delay(150);

        Assert.Equal(0, fake.SnapshotCount);
    }

    [Fact]
    public async Task WorkspaceService_Delete_SchedulesBackup()
    {
        var workspaces = new InMemoryWorkspaceRepository();
        var fake = new FakeBackupService();
        using var scheduler = new DebouncedBackupScheduler(fake, TimeSpan.FromMilliseconds(40));
        var service = new WorkspaceService(workspaces, new InMemorySettingsRepository(), new FakeClock(), scheduler);

        var workspace = await service.CreateAsync("将被删除");
        await service.DeleteAsync(workspace.Id);
        await Task.Delay(150);

        Assert.Equal(1, fake.SnapshotCount);
    }

    [Fact]
    public async Task SetAsync_WithoutScheduler_DoesNotThrow()
    {
        // 兼容旧构造（单测/无 DI 场景）：未注入调度器时写入照常成功
        var service = new SettingsService(new InMemorySettingsRepository());
        await service.SetAsync(SettingKeys.Theme, "dark");
        Assert.Equal("dark", await service.GetAsync(SettingKeys.Theme, "light"));
    }
}
