namespace WorkNest.Application.Dtos;

/// <summary>启动失败的类别；用于界面给出可执行的提示。</summary>
public enum LaunchFailureKind
{
    None = 0,

    /// <summary>本地路径不存在或无法访问。</summary>
    TargetMissing = 1,

    /// <summary>类型不支持或协议不在白名单。</summary>
    NotSupported = 2,

    /// <summary>系统 Shell 拒绝了启动请求。</summary>
    ShellError = 3,
}

/// <summary>单项启动结果。</summary>
public sealed record LaunchResultDto(bool Success, LaunchFailureKind FailureKind, string? ErrorMessage)
{
    public static LaunchResultDto Ok() => new(true, LaunchFailureKind.None, null);

    public static LaunchResultDto Fail(LaunchFailureKind kind, string message) => new(false, kind, message);
}
