using WorkNest.Application.Abstractions;

namespace WorkNest.Tests.Fakes;

/// <summary>内存键值设置假实现：行为即真实 AppSetting 表的子集（按 key 覆盖写）。</summary>
public sealed class InMemorySettingsRepository : ISettingsRepository
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    /// <summary>测试断言用：直接查看某 key 的原始存储文本（校验 string 不被引号包裹等）。</summary>
    public string? RawValue(string key) => _values.TryGetValue(key, out var value) ? value : null;
}
