using WorkNest.Application.Dtos;
using WorkNest.Domain;

namespace WorkNest.Application.Abstractions;

/// <summary>工作区用例服务。</summary>
public interface IWorkspaceService
{
    /// <summary>当前排序模式（最近使用 / 手动）。</summary>
    WorkspaceSortMode CurrentSortMode { get; }

    /// <summary>按当前模式排序的全部工作区。</summary>
    Task<IReadOnlyList<WorkspaceDto>> GetOrderedAsync();

    /// <summary>创建工作区；自动分配颜色。</summary>
    Task<WorkspaceDto> CreateAsync(string name);

    Task RenameAsync(int workspaceId, string newName);

    /// <summary>删除工作区及其资源关联；共享资源不受影响。</summary>
    Task DeleteAsync(int workspaceId);

    /// <summary>保存手动顺序并切换为手动排序模式。</summary>
    Task SetManualOrderAsync(IReadOnlyList<int> orderedIds);

    /// <summary>恢复默认“最近使用优先”排序模式。</summary>
    Task UseLastUsedOrderAsync();
}

/// <summary>资源用例服务。</summary>
public interface IResourceService
{
    /// <summary>某工作区的资源，已按默认规则排序：置顶优先，其余按使用频率。</summary>
    Task<IReadOnlyList<ResourceDto>> GetForWorkspaceAsync(int workspaceId);

    /// <summary>全部工作区的资源；共享资源只出现一次，WorkspaceIds 列出全部归属。</summary>
    Task<IReadOnlyList<ResourceDto>> GetAllWorkspacesAsync();

    /// <summary>新增资源；规范化目标、按唯一键复用已有 ResourceItem。</summary>
    Task<ResourceDto> AddAsync(ResourceEditInput input);

    /// <summary>编辑资源；修改同步影响所有共享该资源的工作区。</summary>
    Task<ResourceDto> UpdateAsync(ResourceEditInput input);

    /// <summary>仅从指定工作区移除关联；最后一个关联被移除后清理资源记录。</summary>
    Task RemoveFromWorkspaceAsync(int workspaceId, int resourceId);

    Task SetPinnedAsync(int workspaceId, int resourceId, bool pinned);

    /// <summary>保存置顶组的手动拖动顺序。</summary>
    Task SetPinnedOrderAsync(int workspaceId, IReadOnlyList<int> pinnedResourceIds);

    /// <summary>共享提示用：除指定工作区外还关联了哪些工作区名。</summary>
    Task<IReadOnlyList<string>> GetSharedWorkspaceNamesAsync(int resourceId, int excludeWorkspaceId);

    /// <summary>最近使用视图数据源（F09/决策 59/70）：当前工作区内最近成功启动过的不同资源，
    /// 按成功时间倒序，最多 maxCount 条；不做置顶优先重排。</summary>
    Task<IReadOnlyList<ResourceDto>> GetRecentlyUsedAsync(int workspaceId, int maxCount);
}

/// <summary>配置导出用例（F11/决策 63/64/96）。</summary>
public interface IExportService
{
    /// <summary>把指定工作区导出为明文 JSON 文件；返回导出的资源关联条数。
    /// 文件包含真实路径与网址，界面须提示敏感信息（决策 96）。</summary>
    Task<int> ExportAsync(IReadOnlyList<int> workspaceIds, string filePath);
}

/// <summary>配置导入用例（F11/决策 74/75/84/85/116）。</summary>
public interface IImportService
{
    /// <summary>解析导出文件并生成预览（含新增/复用统计与同名冲突标记）。
    /// schemaVersion 高于支持版本时抛异常说明原因，拒绝导入（决策 63）。</summary>
    Task<ImportPreview> AnalyzeAsync(string filePath);

    /// <summary>按预览中每工作区的策略执行导入，返回实际写入的资源关联条数。
    /// 含覆盖策略时先自动创建当前状态的安全快照（决策 85）；快照失败则整体中止。</summary>
    Task<int> ExecuteAsync(ImportPreview preview);
}

/// <summary>资源启动用例：统一入口，界面层不得绕过直接执行命令。</summary>
public interface ILauncherService
{
    /// <summary>启动成功后写入使用记录；失败不抛出、不导致主程序退出。</summary>
    Task<LaunchResultDto> LaunchAsync(int workspaceId, int resourceId);

    /// <summary>应用内浏览打开（决策见 desktop-interaction）：不启动外部程序，
    /// 仅校验目录目标可用并计入使用记录；失败以 DTO 返回，契约与 LaunchAsync 一致。</summary>
    Task<LaunchResultDto> RecordInlineOpenAsync(int workspaceId, int resourceId);
}

/// <summary>文件夹浏览用例：枚举目录子项供应用内浏览面板展示。</summary>
public interface IFolderBrowserService
{
    /// <summary>枚举指定目录的直接子项（含隐藏/系统项），目录在前、名称升序；
    /// 超出单次上限时截断并标记。目录不存在或不可读时抛 ValidationException。</summary>
    Task<FolderListingDto> ListAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>设置服务：类型化读写，底层 JSON 序列化保存。</summary>
public interface ISettingsService
{
    /// <summary>读取设置；缺失或解析失败时返回 fallback。</summary>
    Task<T> GetAsync<T>(string key, T fallback);

    Task SetAsync<T>(string key, T value);
}
