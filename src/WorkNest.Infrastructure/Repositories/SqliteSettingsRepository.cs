using System.Globalization;
using WorkNest.Application.Abstractions;
using WorkNest.Infrastructure.Database;

namespace WorkNest.Infrastructure.Repositories;

/// <summary>基于 SQLite 的键值设置仓储（AppSetting 表）。</summary>
public sealed class SqliteSettingsRepository : ISettingsRepository
{
    private readonly WorkNestDb _db;

    public SqliteSettingsRepository(WorkNestDb db) => _db = db;

    public async Task<string?> GetAsync(string key)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppSetting WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    public async Task SetAsync(string key, string value)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        // UPSERT：同键覆盖并刷新更新时间，调用方无需关心键是否已存在
        command.CommandText = """
            INSERT INTO AppSetting (Key, Value, UpdatedAt)
            VALUES ($key, $value, $updatedAt)
            ON CONFLICT(Key) DO UPDATE SET Value = $value, UpdatedAt = $updatedAt;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedAt", SqliteTime.ToUtc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync();
    }
}
