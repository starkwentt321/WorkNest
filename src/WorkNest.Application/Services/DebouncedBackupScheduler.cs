using WorkNest.Application.Abstractions;

namespace WorkNest.Application.Services;

/// <summary>
/// 延迟合并自动备份调度器（决策 65/66）：每次配置修改成功后 Schedule()，
/// 连续修改只保留最后一次计时，静默 delay 后创建一份 "settings" 快照。
/// 备份失败绝不向上抛（设置操作不因备份受阻），改经 SnapshotFailed 事件由宿主记日志。
/// </summary>
public sealed class DebouncedBackupScheduler : IDisposable
{
    private readonly IBackupService _backupService;
    private readonly TimeSpan _delay;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    // 排他阶段（如恢复备份）挂起计数：>0 时到期的快照直接跳过，避免与数据库文件替换并发
    private int _suspendCount;

    /// <summary>快照失败时触发（含异常对象）；订阅方负责记日志，事件回调内不得再抛。</summary>
    public event EventHandler<Exception>? SnapshotFailed;

    public DebouncedBackupScheduler(IBackupService backupService, TimeSpan? delay = null)
    {
        _backupService = backupService;
        _delay = delay ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>重置合并计时；到期后触发一次快照，重复调用自动合并。</summary>
    public void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            // 丢弃未到期的旧计时再建新计时，实现“连续修改合并为一次”
            _timer?.Dispose();
            _timer = new Timer(static async state => await ((DebouncedBackupScheduler)state!).FireAsync(),
                this, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>挂起自动快照（恢复备份等排他阶段），返回的 IDisposable 释放时解除；支持嵌套。</summary>
    public IDisposable Suspend()
    {
        Interlocked.Increment(ref _suspendCount);
        return new SuspendScope(this);
    }

    private async Task FireAsync()
    {
        // 挂起期间到期的快照直接跳过：该阶段数据库文件正在被替换，快照既不安全也无意义；
        // 解除后不补拍，恢复流程自身已有 pre-restore 快照兜底。
        // 残余窗口：计时在 Suspend 生效前一刻到期并已开始快照的执行不可被挂起中断
        // （毫秒级 TOCTOU，主库有 pre-restore 快照兜底）
        if (Volatile.Read(ref _suspendCount) > 0)
        {
            return;
        }

        try
        {
            await _backupService.CreateSnapshotAsync("settings");
        }
        catch (Exception ex)
        {
            SnapshotFailed?.Invoke(this, ex);
        }
    }

    /// <summary>挂起作用域：释放时递减挂起计数，异常路径下也保证解除。</summary>
    private sealed class SuspendScope : IDisposable
    {
        private readonly DebouncedBackupScheduler _owner;
        private bool _disposed;

        public SuspendScope(DebouncedBackupScheduler owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Decrement(ref _owner._suspendCount);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
