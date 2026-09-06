using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Validation;
using WorkNest.Domain;

namespace WorkNest.App.Tests.Fakes;

/// <summary>
/// 工作区服务假实现：GetOrderedAsync 按脚本依次返回不同列表（脚本耗尽后保持最后一次结果），
/// 用于模拟工作区被删除前后下拉数据变化（R07）；其余成员按最小契约实现。
/// </summary>
public sealed class FakeWorkspaceService : IWorkspaceService
{
    private readonly Queue<IReadOnlyList<WorkspaceDto>> _script = new();

    /// <summary>GetOrderedAsync 最近一次返回的列表（脚本出队后保存，重复调用保持稳定）。</summary>
    public IReadOnlyList<WorkspaceDto> LastOrder { get; private set; } = [];

    public int GetOrderedCallCount { get; private set; }

    /// <summary>追加一段返回脚本；按调用顺序依次生效。</summary>
    public void EnqueueOrder(params WorkspaceDto[] dtos) => _script.Enqueue(dtos);

    public WorkspaceSortMode CurrentSortMode => WorkspaceSortMode.LastUsed;

    public Task<IReadOnlyList<WorkspaceDto>> GetOrderedAsync()
    {
        GetOrderedCallCount++;
        if (_script.Count > 0)
        {
            LastOrder = _script.Dequeue();
        }
        return Task.FromResult(LastOrder);
    }

    public Task<WorkspaceDto> CreateAsync(string name) => throw new NotSupportedException("测试未使用该成员");

    public Task RenameAsync(int workspaceId, string newName) => Task.CompletedTask;

    public Task DeleteAsync(int workspaceId) => Task.CompletedTask;

    public Task SetManualOrderAsync(IReadOnlyList<int> orderedIds) => Task.CompletedTask;

    public Task UseLastUsedOrderAsync() => Task.CompletedTask;
}

/// <summary>
/// 资源服务假实现：GetForWorkspaceAsync/GetRecentlyUsedAsync 返回预设列表并记录调用参数，
/// 用于断言最近使用视图的数据源契约（R06）；GetSharedWorkspaceNamesAsync 恒返回空；其余空实现。
/// 所有异步成员返回已完成 Task，保证 VM 的 fire-and-forget 加载路径在测试线程内同步完成。
/// </summary>
public sealed class FakeResourceService : IResourceService
{
    /// <summary>普通视图预设数据（服务端契约顺序）。</summary>
    public IReadOnlyList<ResourceDto> ForWorkspace { get; set; } = [];

    /// <summary>最近使用视图预设数据（LastUsedAt 倒序的服务端契约顺序）。</summary>
    public IReadOnlyList<ResourceDto> RecentlyUsed { get; set; } = [];

    public List<int> ForWorkspaceCalls { get; } = [];

    public List<(int WorkspaceId, int MaxCount)> RecentlyUsedCalls { get; } = [];

    /// <summary>AddAsync 收到的输入（按调用顺序），供拖放新增断言。</summary>
    public List<ResourceEditInput> AddCalls { get; } = [];

    /// <summary>按目标注入的失败（模拟唯一键冲突等），未命中目标的调用正常记录并返回。</summary>
    public Dictionary<string, Exception> AddFailures { get; } = [];

    public Task<IReadOnlyList<ResourceDto>> GetForWorkspaceAsync(int workspaceId)
    {
        ForWorkspaceCalls.Add(workspaceId);
        return Task.FromResult(ForWorkspace);
    }

    public Task<IReadOnlyList<ResourceDto>> GetAllWorkspacesAsync() =>
        Task.FromResult<IReadOnlyList<ResourceDto>>([]);

    public Task<ResourceDto> AddAsync(ResourceEditInput input)
    {
        if (AddFailures.TryGetValue(input.Target, out var failure))
        {
            throw failure;
        }
        AddCalls.Add(input);
        return Task.FromResult(new ResourceDto
        {
            Id = AddCalls.Count,
            Type = input.Type,
            Name = input.Name,
            Target = input.Target,
            WorkspaceIds = [input.WorkspaceId],
            PathExists = true,
        });
    }

    public Task<ResourceDto> UpdateAsync(ResourceEditInput input) => throw new NotSupportedException("测试未使用该成员");

    public Task RemoveFromWorkspaceAsync(int workspaceId, int resourceId) => Task.CompletedTask;

    public Task SetPinnedAsync(int workspaceId, int resourceId, bool pinned) => Task.CompletedTask;

    public Task SetPinnedOrderAsync(int workspaceId, IReadOnlyList<int> pinnedResourceIds) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> GetSharedWorkspaceNamesAsync(int resourceId, int excludeWorkspaceId) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<ResourceDto>> GetRecentlyUsedAsync(int workspaceId, int maxCount)
    {
        RecentlyUsedCalls.Add((workspaceId, maxCount));
        return Task.FromResult(RecentlyUsed);
    }
}

/// <summary>启动服务桩：测试不实际启动任何目标；内嵌浏览记账可注入结果并记录调用。</summary>
public sealed class FakeLauncherService : ILauncherService
{
    public int LaunchCallCount { get; private set; }

    public List<int> LaunchedResourceIds { get; } = [];

    /// <summary>RecordInlineOpenAsync 收到的资源 Id（按调用顺序）。</summary>
    public List<int> InlineOpenCalls { get; } = [];

    /// <summary>内嵌记账的预设结果；null = 成功。</summary>
    public LaunchResultDto? NextInlineOpenResult { get; set; }

    public Task<LaunchResultDto> LaunchAsync(int workspaceId, int resourceId)
    {
        LaunchCallCount++;
        LaunchedResourceIds.Add(resourceId);
        return Task.FromResult(LaunchResultDto.Ok());
    }

    public Task<LaunchResultDto> RecordInlineOpenAsync(int workspaceId, int resourceId)
    {
        InlineOpenCalls.Add(resourceId);
        return Task.FromResult(NextInlineOpenResult ?? LaunchResultDto.Ok());
    }
}

/// <summary>设置服务桩：键值存内存字典，未写入的键读取返回 fallback。</summary>
public sealed class FakeSettingsService : ISettingsService
{
    public Dictionary<string, object?> Values { get; } = [];

    public Task<T> GetAsync<T>(string key, T fallback) =>
        Task.FromResult(Values.TryGetValue(key, out var stored) && stored is T typed ? typed : fallback);

    public Task SetAsync<T>(string key, T value)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }
}

/// <summary>文件夹浏览服务桩：返回预设列表并记录枚举路径，不触碰真实文件系统。</summary>
public sealed class FakeFolderBrowserService : IFolderBrowserService
{
    /// <summary>按路径预设的枚举结果；未命中路径返回空目录内容。</summary>
    public Dictionary<string, FolderListingDto> Listings { get; } = [];

    /// <summary>按路径注入枚举失败（模拟目录不可读），值为异常消息。</summary>
    public Dictionary<string, string> ThrowOnPath { get; } = [];

    public List<string> ListCalls { get; } = [];

    public Task<FolderListingDto> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        ListCalls.Add(path);
        if (ThrowOnPath.TryGetValue(path, out var message))
        {
            throw new ValidationException(message);
        }
        return Task.FromResult(Listings.TryGetValue(path, out var listing)
            ? listing
            : new FolderListingDto(path, [], false));
    }
}

/// <summary>开机启动服务桩。</summary>
public sealed class FakeAutostartService : IAutostartService
{
    public bool IsEnabled() => false;

    public void SetEnabled(bool enabled)
    {
    }
}

/// <summary>备份服务桩。</summary>
public sealed class FakeBackupService : IBackupService
{
    public Task<string> CreateSnapshotAsync(string reason) => Task.FromResult("fake-snapshot.db");

    public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync() => Task.FromResult<IReadOnlyList<BackupInfo>>([]);

    public Task RestoreAsync(string backupFilePath) => Task.CompletedTask;
}

/// <summary>全局热键服务桩。</summary>
public sealed class FakeHotkeyService : IGlobalHotkeyService
{
    public bool TryRegister(string gesture) => true;

    public string? RegisteredGesture => null;

    public void Unregister()
    {
    }

    // 桩实现不触发热键事件，用空访问器避免 CS0067
    public event EventHandler? HotkeyPressed
    {
        add { }
        remove { }
    }
}

/// <summary>导出服务桩。</summary>
public sealed class FakeExportService : IExportService
{
    public Task<int> ExportAsync(IReadOnlyList<int> workspaceIds, string filePath) => Task.FromResult(0);
}

/// <summary>导入服务桩。</summary>
public sealed class FakeImportService : IImportService
{
    public Task<ImportPreview> AnalyzeAsync(string filePath) => throw new NotSupportedException("测试未使用该成员");

    public Task<int> ExecuteAsync(ImportPreview preview) => Task.FromResult(0);
}

/// <summary>应用重启桩。</summary>
public sealed class FakeAppRestart : IAppRestart
{
    public void Restart()
    {
    }
}
