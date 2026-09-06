namespace WorkNest.Domain;

/// <summary>一次启动请求的记录；成功与失败均记录，便于诊断，但使用次数只统计成功。</summary>
public sealed class UsageRecord
{
    public int Id { get; set; }

    public int ResourceId { get; set; }

    /// <summary>从哪个工作区发起启动；工作区删除后置空。</summary>
    public int? WorkspaceId { get; set; }

    /// <summary>启动时间（UTC）。</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>1 = 成功，2 = 失败。</summary>
    public int Result { get; set; }

    /// <summary>失败时的错误码（如 Win32 错误号）。</summary>
    public string? ErrorCode { get; set; }

    /// <summary>从发起启动到系统接受请求的耗时（毫秒）。</summary>
    public int? DurationMs { get; set; }
}
