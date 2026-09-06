using System.Globalization;

namespace WorkNest.Infrastructure.Repositories;

/// <summary>
/// SQLite 时间存取约定：所有时间以 UTC 的 ISO "O" 格式字符串存储，
/// 读回时用 RoundtripKind 解析还原 UTC 时刻，避免本地时区歧义。
/// 调用方必须传入 Kind=Utc 的时间。
/// </summary>
internal static class SqliteTime
{
    public static string ToUtc(DateTime utc)
        => utc.Kind == DateTimeKind.Utc
            ? utc.ToString("O", CultureInfo.InvariantCulture)
            // 防御：非 UTC Kind 一律先转换为 UTC，保证库内格式一致
            : utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTime? FromUtcOrNull(string? value)
        => value is null
            ? null
            : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
