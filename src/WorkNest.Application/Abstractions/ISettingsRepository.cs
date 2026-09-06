namespace WorkNest.Application.Abstractions;

/// <summary>键值设置仓储（AppSetting 表）。</summary>
public interface ISettingsRepository
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string value);
}
