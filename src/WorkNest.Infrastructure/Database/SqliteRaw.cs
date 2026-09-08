using Microsoft.Data.Sqlite;

namespace WorkNest.Infrastructure.Database;

/// <summary>
/// 仓储与迁移器共用的 SQLite 原生语句执行。
/// 仅用于 BEGIN/COMMIT/ROLLBACK 等固定控制语句，不承载参数化业务 SQL。
/// </summary>
internal static class SqliteRaw
{
    /// <summary>执行一条无参数的原生 SQL 语句。</summary>
    internal static void Execute(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }
}
