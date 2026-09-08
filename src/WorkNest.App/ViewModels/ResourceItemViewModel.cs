using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WorkNest.Application.Dtos;
using WorkNest.Domain;
using WorkNest.Platform.Windows.Icons;

namespace WorkNest.App.ViewModels;

/// <summary>
/// 资源列表行视图模型：包装 ResourceDto，提供类型中文名、图标（提取或类型默认字形，决策 106）
/// 与失效状态展示（决策 73/F08）。列表重载时整体重建实例，仅 IsInvalid 支持运行期更新。
/// </summary>
public partial class ResourceItemViewModel : ObservableObject
{
    /// <summary>行数据；启动成功后的局部记账会整体替换实例（DTO 属性 init-only），展示属性不受影响。</summary>
    public ResourceDto Dto { get; private set; }

    public int Id => Dto.Id;

    public ResourceType Type => Dto.Type;

    /// <summary>类型中文名（目录/文件/程序/网站）。</summary>
    public string TypeName { get; }

    public string Name => Dto.Name;

    /// <summary>展示名：失效资源在名称后追加标记（决策 73）。</summary>
    public string DisplayName => IsInvalid ? $"{Name}（失效）" : Name;

    public string Target => Dto.Target;

    public bool IsPinned => Dto.IsPinned;

    /// <summary>提取到的目标图标；null 时界面回退到 TypeGlyph 字形。</summary>
    public ImageSource? Icon { get; }

    /// <summary>类型默认图标字形（Segoe Fluent Icons）。</summary>
    public string TypeGlyph { get; }

    /// <summary>标签展示串（逗号分隔）。</summary>
    public string TagsDisplay => string.Join(", ", Dto.Tags);

    [ObservableProperty]
    private bool _isInvalid;

    public ResourceItemViewModel(ResourceDto dto, IResourceIconProvider? iconProvider)
    {
        Dto = dto;
        TypeName = TypeNameOf(dto.Type);
        TypeGlyph = GlyphOfType(dto.Type);
        Icon = iconProvider?.GetIcon(dto.Type, dto.Target);
        // DTO 已按当前目标可用性判定；网站恒为可用（决策 73/F08）
        _isInvalid = !dto.PathExists;
    }

    /// <summary>
    /// 启动/内联打开成功后的局部记账：RunCount+1、最近使用时间刷新、目标判定为存在。
    /// DTO 属性 init-only，重建实例替换；统计字段无 UI 绑定（仅参与排序，由 RebuildView 重算），无需逐属性通知。
    /// </summary>
    public void ApplySuccessfulLaunch(DateTime utcNow)
    {
        var d = Dto;
        Dto = new ResourceDto
        {
            Id = d.Id,
            Type = d.Type,
            Name = d.Name,
            Target = d.Target,
            Arguments = d.Arguments,
            WorkingDirectory = d.WorkingDirectory,
            Tags = d.Tags,
            IsPinned = d.IsPinned,
            LinkSortOrder = d.LinkSortOrder,
            RunCount = d.RunCount + 1,
            LastUsedAt = utcNow,
            WorkspaceIds = d.WorkspaceIds,
            PathExists = true, // 成功使用即目标可达，与全量重载后的失效判定口径一致
        };
        if (IsInvalid)
        {
            IsInvalid = false; // 联动清除失效标记（内部带 DisplayName 通知）
        }
    }

    partial void OnIsInvalidChanged(bool value) => OnPropertyChanged(nameof(DisplayName));

    /// <summary>类型中文名映射（决策 4.2）。</summary>
    public static string TypeNameOf(ResourceType type) => type switch
    {
        ResourceType.Directory => "目录",
        ResourceType.File => "文件",
        ResourceType.Program => "程序",
        ResourceType.Website => "网站",
        _ => type.ToString(),
    };

    /// <summary>类型默认图标字形；提取失败时的回退展示（决策 106）。</summary>
    public static string GlyphOfType(ResourceType type) => type switch
    {
        ResourceType.Directory => "\uE8B7", // Folder
        ResourceType.File => "\uE7C3",      // Page
        ResourceType.Program => "\uE7C4",   // TwoPage（程序无专用字形，取近似的页面组字形）
        ResourceType.Website => "\uE774",   // Globe
        _ => "\uE7C3",
    };
}
