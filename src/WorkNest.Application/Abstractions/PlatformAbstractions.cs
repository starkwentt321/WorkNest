using WorkNest.Application.Dtos;
using WorkNest.Domain;

namespace WorkNest.Application.Abstractions;

/// <summary>可测试的时间源。</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>系统时钟。</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>平台启动器的一次执行结果。</summary>
public sealed record LaunchOutcome(bool Success, LaunchFailureKind FailureKind = LaunchFailureKind.None, string? ErrorMessage = null, string? ErrorCode = null)
{
    public static LaunchOutcome Ok() => new(true);

    public static LaunchOutcome Fail(LaunchFailureKind kind, string message, string? errorCode = null) => new(false, kind, message, errorCode);
}

/// <summary>
/// 平台启动服务：按资源类型调用对应启动策略。
/// 实现必须捕获全部异常并转为失败结果，绝不允许导致主程序退出。
/// </summary>
public interface IProcessLauncher
{
    LaunchOutcome Launch(ResourceItem item);
}

/// <summary>单实例锁：第二次启动时激活已有实例并退出新进程。</summary>
public interface ISingleInstanceLock
{
    /// <summary>尝试成为首个实例；失败表示已有实例在运行。</summary>
    bool TryAcquireFirstInstance();

    /// <summary>通知已运行的实例激活主窗口（在新进程中调用）。</summary>
    void SignalRunningInstance();

    /// <summary>首个实例监听激活请求；回调可能来自后台线程，界面侧需自行调度。</summary>
    void ListenActivationRequest(Action onActivate);
}

/// <summary>托盘图标服务。</summary>
public interface ITrayService
{
    void Initialize();

    /// <summary>托盘菜单“打开 WorkNest”或双击图标。</summary>
    event EventHandler? OpenRequested;

    /// <summary>托盘菜单“退出”。</summary>
    event EventHandler? ExitRequested;

    void Dispose();
}

/// <summary>全局呼出快捷键服务；注册冲突时返回 false 并保留托盘入口。</summary>
public interface IGlobalHotkeyService
{
    /// <summary>gesture 形如 "Ctrl+Alt+W"；成功注册返回 true。</summary>
    bool TryRegister(string gesture);

    /// <summary>当前实际生效的手势；未注册任何热键时为 null（提示必须反映该真实状态）。</summary>
    string? RegisteredGesture { get; }

    void Unregister();

    event EventHandler? HotkeyPressed;
}

/// <summary>开机启动（当前 Windows 用户，注册表 HKCU Run）。</summary>
public interface IAutostartService
{
    bool IsEnabled();

    void SetEnabled(bool enabled);
}

/// <summary>备份信息。</summary>
public sealed record BackupInfo(string FilePath, DateTime CreatedAt, long SizeBytes, string Reason);

/// <summary>数据库快照备份；恢复前必须先创建安全快照。</summary>
public interface IBackupService
{
    /// <summary>创建数据库快照，返回备份文件路径。</summary>
    Task<string> CreateSnapshotAsync(string reason);

    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync();

    /// <summary>恢复指定备份；实现内部先对当前状态做安全快照，失败则中止恢复。</summary>
    Task RestoreAsync(string backupFilePath);
}

/// <summary>应用进程生命周期（恢复备份后需整进程重启，以释放数据库文件句柄并重载全部缓存）。</summary>
public interface IAppRestart
{
    /// <summary>以当前进程同一可执行文件重新启动应用，并退出当前实例。</summary>
    void Restart();
}
