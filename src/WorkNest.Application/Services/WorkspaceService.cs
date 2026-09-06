using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Validation;
using WorkNest.Domain;

namespace WorkNest.Application.Services;

/// <summary>
/// 工作区用例实现：命名唯一性校验、颜色自动分配、双排序模式切换。
/// 排序模式持久化在 SettingKeys.WorkspaceSortMode，值为 "LastUsed" / "Manual"。
/// </summary>
public sealed class WorkspaceService : IWorkspaceService
{
    /// <summary>固定调色板（#RRGGBB）；按创建时已有工作区数量取模循环分配，保证前 10 个工作区颜色互不相同。</summary>
    private static readonly string[] Palette =
    [
        "#0078D4", "#00B294", "#E3008C", "#FF8C00", "#744DA9",
        "#498205", "#CA5010", "#038387", "#C239B3", "#6B69D6",
    ];

    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IClock _clock;
    private readonly DebouncedBackupScheduler? _backupScheduler;

    public WorkspaceService(
        IWorkspaceRepository workspaceRepository,
        ISettingsRepository settingsRepository,
        IClock clock,
        DebouncedBackupScheduler? backupScheduler = null)
    {
        _workspaceRepository = workspaceRepository;
        _settingsRepository = settingsRepository;
        _clock = clock;
        _backupScheduler = backupScheduler;
    }

    /// <summary>核心内容成功提交后通知延迟合并备份；校验失败/删除不存在的工作区不会走到这里（决策 65/R03）。</summary>
    private void NotifyContentChanged() => _backupScheduler?.Schedule();

    /// <summary>
    /// 当前排序模式的缓存值。构造时默认 LastUsed；
    /// 该属性在首次调用 GetOrderedAsync 后才与设置一致（GetOrderedAsync 每次都会重新读取设置刷新缓存），
    /// 仅调用 SetManualOrderAsync/UseLastUsedOrderAsync 后不保证立即刷新。
    /// </summary>
    public WorkspaceSortMode CurrentSortMode { get; private set; } = WorkspaceSortMode.LastUsed;

    public async Task<IReadOnlyList<WorkspaceDto>> GetOrderedAsync()
    {
        // 每次查询都从设置刷新缓存，避免多视图/外部修改导致模式过期
        var raw = await _settingsRepository.GetAsync(SettingKeys.WorkspaceSortMode);
        CurrentSortMode = ParseMode(raw);

        var all = await _workspaceRepository.GetAllAsync();
        IEnumerable<Workspace> ordered = CurrentSortMode == WorkspaceSortMode.Manual
            ? all.OrderBy(w => w.SortOrder)
            // 最近使用模式：LastUsedAt 倒序（null 视为最小），同值按创建时间倒序兜底
            : all.OrderByDescending(w => w.LastUsedAt ?? DateTime.MinValue)
                 .ThenByDescending(w => w.CreatedAt);

        var result = new List<WorkspaceDto>(all.Count);
        foreach (var workspace in ordered)
        {
            var count = await _workspaceRepository.GetResourceCountAsync(workspace.Id);
            result.Add(new WorkspaceDto(workspace.Id, workspace.Name, workspace.Color, workspace.LastUsedAt, count));
        }

        return result;
    }

    public async Task<WorkspaceDto> CreateAsync(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ValidationException("工作区名称不能为空");
        }

        var existing = await _workspaceRepository.GetAllAsync();
        if (existing.Any(w => string.Equals(w.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException("已存在同名工作区");
        }

        var now = _clock.UtcNow;
        var workspace = new Workspace
        {
            Name = trimmed,
            // 按创建时已有数量取模，从调色板顺序取色
            Color = Palette[existing.Count % Palette.Length],
            // 手动排序追加到末尾：现有最大值 +1，无工作区时从 1 开始
            SortOrder = existing.Count == 0 ? 1 : existing.Max(w => w.SortOrder) + 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // 仓储契约“插入并回填 Id”：必须把自增 Id 写回实体，
        // 否则返回的 DTO Id=0，调用方（如导入服务）无法定位新建的工作区
        workspace.Id = await _workspaceRepository.AddAsync(workspace);
        NotifyContentChanged();

        var count = await _workspaceRepository.GetResourceCountAsync(workspace.Id);
        return new WorkspaceDto(workspace.Id, workspace.Name, workspace.Color, workspace.LastUsedAt, count);
    }

    public async Task RenameAsync(int workspaceId, string newName)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ValidationException("工作区名称不能为空");
        }

        var workspace = await _workspaceRepository.GetAsync(workspaceId)
            ?? throw new ValidationException("工作区不存在");

        // 重名校验需排除自身，允许仅改变大小写
        var all = await _workspaceRepository.GetAllAsync();
        if (all.Any(w => w.Id != workspaceId && string.Equals(w.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException("已存在同名工作区");
        }

        workspace.Name = trimmed;
        workspace.UpdatedAt = _clock.UtcNow;
        await _workspaceRepository.UpdateAsync(workspace);
        NotifyContentChanged();
    }

    public async Task DeleteAsync(int workspaceId)
    {
        await _workspaceRepository.DeleteAsync(workspaceId);
        NotifyContentChanged();
    }

    public async Task SetManualOrderAsync(IReadOnlyList<int> orderedIds)
    {
        var now = _clock.UtcNow;
        // 按调用方给出的顺序写入 SortOrder=下标；未列出的工作区保持原顺序值
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var workspace = await _workspaceRepository.GetAsync(orderedIds[i]);
            if (workspace is null)
            {
                continue;
            }

            workspace.SortOrder = i;
            workspace.UpdatedAt = now;
            await _workspaceRepository.UpdateAsync(workspace);
        }

        // 手动排序属于持久化偏好，与顺序数据一起落库
        await _settingsRepository.SetAsync(SettingKeys.WorkspaceSortMode, WorkspaceSortMode.Manual.ToString());
        NotifyContentChanged();
    }

    public async Task UseLastUsedOrderAsync()
    {
        await _settingsRepository.SetAsync(SettingKeys.WorkspaceSortMode, WorkspaceSortMode.LastUsed.ToString());
        NotifyContentChanged();
    }

    /// <summary>设置值为自由文本，解析失败时回退默认的最近使用模式，避免脏数据导致崩溃。</summary>
    private static WorkspaceSortMode ParseMode(string? raw) => raw switch
    {
        nameof(WorkspaceSortMode.Manual) => WorkspaceSortMode.Manual,
        nameof(WorkspaceSortMode.LastUsed) => WorkspaceSortMode.LastUsed,
        _ => WorkspaceSortMode.LastUsed,
    };
}
