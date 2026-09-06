using WorkNest.Application.Abstractions;
using WorkNest.Domain;

namespace WorkNest.Tests.Fakes;

/// <summary>
/// 平台启动器假实现：不启动真实进程，按预设结果返回并记录调用，供启动用例断言。
/// </summary>
public sealed class FakeLauncher : IProcessLauncher
{
    /// <summary>下一次 Launch 返回的结果；默认成功。</summary>
    public LaunchOutcome NextOutcome { get; set; } = LaunchOutcome.Ok();

    public int LaunchCount { get; private set; }

    /// <summary>按调用顺序记录被启动的资源快照，供断言启动目标正确。</summary>
    public List<int> LaunchedResourceIds { get; } = [];

    public LaunchOutcome Launch(ResourceItem item)
    {
        LaunchCount++;
        LaunchedResourceIds.Add(item.Id);
        return NextOutcome;
    }
}
