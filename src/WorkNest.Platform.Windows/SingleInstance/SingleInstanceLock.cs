using WorkNest.Application.Abstractions;

namespace WorkNest.Platform.Windows.SingleInstance;

/// <summary>
/// 单实例锁（命名 Mutex）+ 激活信号（命名 EventWaitHandle）。
/// 内核对象名使用 <c>Local\</c> 前缀：按登录会话命名空间隔离，
/// 同一 Windows 用户在多个会话（如远程桌面并行登录）各自持有独立实例，互不干扰。
/// 实现 <see cref="IDisposable"/>：组合根以单例注册，容器 Dispose 时释放互斥体，
/// 供恢复备份后的整进程重启在拉起新进程前主动让出单实例锁。
/// </summary>
public sealed class SingleInstanceLock : ISingleInstanceLock, IDisposable
{
    private const string MutexName = @"Local\WorkNest.SingleInstance";
    private const string ActivateEventName = @"Local\WorkNest.Activate";

    private Mutex? _mutex;

    /// <summary>尝试成为首个实例；失败表示本会话已有 WorkNest 在运行，新进程应信号旧实例后退出。</summary>
    public bool TryAcquireFirstInstance()
    {
        try
        {
            // initiallyOwned=true：创建成功即持有，进程存活期间保持占用；
            // 命名 Mutex 下 initiallyOwned 仅在 createdNew=true（新建对象）时生效，打开已有对象不会等待
            _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
            if (createdNew)
            {
                return true;
            }

            // 已有实例在运行：立即释放句柄，不长期占用命名对象
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (AbandonedMutexException)
        {
            // 上个实例异常退出留下的 abandoned 锁：已无存活持有者，视为可接管（本次启动即首实例）
            return true;
        }
    }

    /// <summary>通知本会话已运行的实例激活主窗口（在第二个进程中调用）。</summary>
    public void SignalRunningInstance()
    {
        try
        {
            using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            activateEvent.Set();
        }
        catch
        {
            // 打开/置信号失败（首实例已退出、尚未创建事件等）：新进程本就即将退出，无需处理
        }
    }

    /// <summary>首实例监听激活请求；在后台线程阻塞等待，回调由界面侧自行调度到 UI 线程。</summary>
    public void ListenActivationRequest(Action onActivate)
    {
        // create-or-open：由首实例创建（避免“二次启动先 Set、首实例后创建”的竞态丢失信号）；
        // AutoReset：每次 Set 只唤醒一次 WaitOne，对应一次激活请求
        var activateEvent = new EventWaitHandle(initialState: false, mode: EventResetMode.AutoReset, name: ActivateEventName);

        // 常驻后台线程阻塞等待（应用生命周期内仅此一个线程，可接受）；
        // 句柄释放（进程退出）后 WaitOne 抛 ObjectDisposedException，任务随之结束
        _ = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    if (!activateEvent.WaitOne())
                    {
                        return; // 循环持续到句柄失效
                    }
                }
                catch (ObjectDisposedException)
                {
                    return; // 本实例正在退出：正常结束监听
                }

                try
                {
                    onActivate();
                }
                catch
                {
                    // 回调异常不允许终止监听循环，否则后续所有激活请求都会静默失效
                }
            }
        });
    }

    /// <summary>释放单实例锁：让出互斥体所有权并关闭句柄；幂等，未获得锁时调用亦安全。</summary>
    public void Dispose()
    {
        // 先原子置空再做释放，保证重复 Dispose（如容器释放与显式释放叠加）不会二次处理
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            // 必须显式 ReleaseMutex：仅 Dispose 句柄不会释放命名互斥体的所有权，
            // 新进程会因拿不到锁而被误判为二次启动
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 当前线程未持有所有权（异常路径下可能发生）：直接关闭句柄即可
        }
        mutex.Dispose();
    }
}
