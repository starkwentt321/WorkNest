using System.Windows.Media;
using WorkNest.Application.Dtos;
using WorkNest.App.Media;

namespace WorkNest.App.ViewModels;

/// <summary>
/// 工作区下拉项。末尾附加两个特殊项（决策 26/44/34）：
/// “最近使用”独立视图（决策 44：入口在工作区下拉框中）与“管理工作区…”，
/// 由 MainViewModel 在选中它们时分别进入最近使用视图 / 打开管理窗口。
/// </summary>
public sealed class WorkspaceOptionViewModel
{
    /// <summary>管理项单例工厂。</summary>
    public static WorkspaceOptionViewModel ManageItem { get; } = new(isRecentItem: false);

    /// <summary>最近使用项单例工厂（决策 34：独立视图，复用资源列表结构）。</summary>
    public static WorkspaceOptionViewModel RecentItem { get; } = new(isRecentItem: true);

    public WorkspaceDto? Workspace { get; }

    public bool IsManageItem { get; }

    /// <summary>是否“最近使用”特殊项（决策 44）。</summary>
    public bool IsRecentItem { get; }

    /// <summary>任一特殊项（下拉分隔线与图标的统一判断依据）。</summary>
    public bool IsSpecialItem => IsManageItem || IsRecentItem;

    private WorkspaceOptionViewModel(bool isRecentItem)
    {
        IsManageItem = !isRecentItem;
        IsRecentItem = isRecentItem;
    }

    public WorkspaceOptionViewModel(WorkspaceDto workspace)
    {
        Workspace = workspace;
    }

    /// <summary>显示名；特殊项为固定文案（决策 69：最近使用标题配合状态栏呈现完整上下文）。</summary>
    public string Name => IsRecentItem ? "最近使用" : IsManageItem ? "管理工作区…" : Workspace!.Name;

    /// <summary>工作区颜色串（用于下拉项色块）。</summary>
    public string? Color => Workspace?.Color;

    /// <summary>文件夹风格图标（管理工作区窗口与下拉普通项使用，决策 39）。</summary>
    public ImageSource? Icon => Workspace is null ? null : WorkspaceIconFactory.Create(Workspace.Color);

    public int ResourceCount => Workspace?.ResourceCount ?? 0;

    public int WorkspaceId => Workspace?.Id ?? 0;
}
