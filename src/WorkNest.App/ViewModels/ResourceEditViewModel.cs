using CommunityToolkit.Mvvm.ComponentModel;
using WorkNest.Application.Dtos;
using WorkNest.Domain;

namespace WorkNest.App.ViewModels;

/// <summary>
/// 主窗口内编辑面板视图模型（决策 62/71/72/115）。
/// 类型由新增入口或被编辑资源决定，面板内只读展示；字段变更即标记 IsDirty，供取消/关闭时确认。
/// </summary>
public partial class ResourceEditViewModel : ObservableObject
{
    // 加载既有资源时抑制 IsDirty 标记
    private bool _loading;

    public int? Id { get; }

    public ResourceType Type { get; }

    /// <summary>类型只读展示（4.2：类型为必填字段；面板中不提供切换）。</summary>
    public string TypeDisplay { get; }

    /// <summary>是否新增模式（决定面板标题与保存调用）。</summary>
    public bool IsNew { get; }

    /// <summary>本次新增/编辑归属的工作区（进入面板时锁定，避免中途切换上下文）。</summary>
    public int EditingWorkspaceId { get; }

    /// <summary>仅程序类型显示启动参数与工作目录（4.2）。</summary>
    public bool IsProgram => Type == ResourceType.Program;

    /// <summary>目录/文件/程序目标可浏览，网站为手工输入。</summary>
    public bool ShowBrowse => Type != ResourceType.Website;

    /// <summary>面板标题。</summary>
    public string PanelTitle => IsNew ? "新增资源" : "编辑资源";

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private string _tagsText = string.Empty;

    /// <summary>面板内是否存在未保存修改（决策 72）。</summary>
    public bool IsDirty { get; private set; }

    private ResourceEditViewModel(int? id, ResourceType type, bool isNew, int editingWorkspaceId)
    {
        Id = id;
        Type = type;
        IsNew = isNew;
        EditingWorkspaceId = editingWorkspaceId;
        TypeDisplay = ResourceItemViewModel.TypeNameOf(type);
    }

    /// <summary>新增面板：类型由入口决定，目标预填（决策 115）。</summary>
    public static ResourceEditViewModel New(ResourceType type, string target, int workspaceId)
    {
        var vm = new ResourceEditViewModel(null, type, isNew: true, workspaceId)
        {
            Target = target,
        };
        return vm;
    }

    /// <summary>编辑面板：预填现有字段，名称/图标保持原样（决策 61）。</summary>
    public static ResourceEditViewModel FromItem(ResourceItemViewModel item, int editingWorkspaceId)
    {
        var vm = new ResourceEditViewModel(item.Id, item.Type, isNew: false, editingWorkspaceId)
        {
            _loading = true,
        };
        vm.Name = item.Name;
        vm.Target = item.Target;
        vm.Arguments = item.Dto.Arguments ?? string.Empty;
        vm.WorkingDirectory = item.Dto.WorkingDirectory ?? string.Empty;
        vm.TagsText = item.TagsDisplay;
        vm._loading = false;
        return vm;
    }

    partial void OnNameChanged(string value) => MarkDirty();
    partial void OnTargetChanged(string value) => MarkDirty();
    partial void OnArgumentsChanged(string value) => MarkDirty();
    partial void OnWorkingDirectoryChanged(string value) => MarkDirty();
    partial void OnTagsTextChanged(string value) => MarkDirty();

    private void MarkDirty()
    {
        if (!_loading)
        {
            IsDirty = true;
        }
    }

    /// <summary>标签输入按中英文逗号/顿号/空格拆分，去重去空（4.2）。</summary>
    private static IReadOnlyList<string> SplitTags(string text) => text
        .Split([',', '，', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>构造应用层编辑输入；名称留空时由服务按目标自动提取（决策 61）。</summary>
    public ResourceEditInput BuildInput() => new()
    {
        Id = Id,
        WorkspaceId = EditingWorkspaceId,
        Type = Type,
        Name = Name,
        Target = Target,
        Arguments = string.IsNullOrWhiteSpace(Arguments) ? null : Arguments.Trim(),
        WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim(),
        Tags = SplitTags(TagsText),
    };
}
