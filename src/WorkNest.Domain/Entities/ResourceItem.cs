namespace WorkNest.Domain;

/// <summary>可启动资源：目录、文件、程序、网站的统一抽象，可被多个工作区共享。</summary>
public sealed class ResourceItem
{
    public int Id { get; set; }

    public ResourceType Type { get; set; }

    /// <summary>用户看到的显示名称，同一工作区内允许重名（界面提示）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>规范化后的本地完整路径或绝对 URI；参与唯一键查重。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>启动参数；原样保存，不参与字符串拼接执行。</summary>
    public string? Arguments { get; set; }

    /// <summary>程序启动时的工作目录；不存在时允许保存但会标记状态。</summary>
    public string? WorkingDirectory { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
