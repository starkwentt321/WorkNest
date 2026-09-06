namespace WorkNest.Domain;

/// <summary>工作区：工作资源的一级分区。</summary>
public sealed class Workspace
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>十六进制颜色（#RRGGBB），系统自动分配，用于生成文件夹风格图标。</summary>
    public string Color { get; set; } = "#4CC2FF";

    /// <summary>手动排序时的顺序值，越小越靠前；最近使用模式下不参与排序。</summary>
    public int SortOrder { get; set; }

    /// <summary>最近一次从该工作区成功启动资源的时间（UTC）；仅浏览不更新。</summary>
    public DateTime? LastUsedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
