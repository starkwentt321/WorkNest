using WorkNest.Application.Abstractions;

namespace WorkNest.Tests.Fakes;

/// <summary>可编程时钟假实现：测试手动推进时间，保证排序/统计断言确定性。</summary>
public sealed class FakeClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;

    public FakeClock()
        : this(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    {
    }

    /// <summary>向前推进指定时长（向后传负值）。</summary>
    public void Advance(TimeSpan delta) => UtcNow += delta;
}
