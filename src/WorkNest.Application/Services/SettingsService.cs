using System.Text.Json;
using WorkNest.Application.Abstractions;

namespace WorkNest.Application.Services;

/// <summary>
/// 设置用例实现：底层以 JSON 文本持久化；string 类型直存原文避免被引号包裹。
/// 读取遇缺失或损坏一律回退默认值，设置问题不允许影响主流程。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly ISettingsRepository _repository;
    private readonly DebouncedBackupScheduler? _backupScheduler;

    public SettingsService(ISettingsRepository repository, DebouncedBackupScheduler? backupScheduler = null)
    {
        _repository = repository;
        _backupScheduler = backupScheduler;
    }

    public async Task<T> GetAsync<T>(string key, T fallback)
    {
        try
        {
            var raw = await _repository.GetAsync(key);
            if (raw is null)
            {
                return fallback;
            }

            // string 直读原文：历史值或用户手改值未必是合法 JSON 字符串，反序列化反而破坏语义
            if (typeof(T) == typeof(string))
            {
                return (T)(object)raw;
            }

            return JsonSerializer.Deserialize<T>(raw) ?? fallback;
        }
        catch (Exception)
        {
            // 损坏的设置按默认处理，不让单个键的异常值导致崩溃
            return fallback;
        }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        // string 原样存储（如 "Ctrl+Alt+W"、"dark"），其余类型 JSON 序列化（bool → "true"）
        var raw = typeof(T) == typeof(string)
            ? (string)(object)value!
            : JsonSerializer.Serialize(value);
        await _repository.SetAsync(key, raw);

        // 决策 65：配置修改成功后触发延迟合并备份；连续修改由调度器合并为一次。
        // 窗口布局、会话标志等 state. 运行状态不触发，避免仅浏览或移动窗口也重置防抖计时
        if (!SettingKeys.IsSessionStateKey(key))
        {
            _backupScheduler?.Schedule();
        }
    }
}
