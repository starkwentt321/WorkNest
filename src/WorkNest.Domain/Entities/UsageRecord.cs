namespace WorkNest.Domain;

/// <summary>
/// 一次启动请求的记录。仅成功启动时写入（失败路径未实现）；
/// 当前无应用内读取方，数据仅供直接查库诊断。
/// </summary>
public sealed class UsageRecord
{
    public int Id { get; set; }

    public int ResourceId { get; set; }

    /// <summary>从哪个工作区发起启动；工作区删除后置空。</summary>
    public int? WorkspaceId { get; set; }

    /// <summary>启动时间（UTC）。</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>结果：1 = 成功（当前唯一写入值）；2 = 失败（预留，失败路径未实现）。</summary>
    public int Result { get; set; }

    /// <summary>失败时的错误码（如 Win32 错误号）；当前恒为 NULL。</summary>
    public string? ErrorCode { get; set; }

    /// <summary>从发起启动到系统接受请求的耗时（毫秒）。</summary>
    public int? DurationMs { get; set; }
}
