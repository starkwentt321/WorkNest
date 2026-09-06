namespace WorkNest.Domain;

/// <summary>资源标签：支持按标签搜索；资源删除时一并清理。</summary>
public sealed class ResourceTag
{
    public int ResourceId { get; set; }

    public string Tag { get; set; } = string.Empty;
}
