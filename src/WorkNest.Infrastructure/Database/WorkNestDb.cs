using Microsoft.Data.Sqlite;

namespace WorkNest.Infrastructure.Database;

/// <summary>
/// SQLite 连接工厂。持有数据库文件路径，负责统一连接字符串与 PRAGMA 初始化；
/// 不长期持有连接，各仓储按操作打开/关闭，减少文件锁占用。
/// </summary>
public sealed class WorkNestDb
{
    private readonly string _connectionString;

    public WorkNestDb(string dbFilePath)
    {
        DbFilePath = dbFilePath;
        // ReadWriteCreate：文件不存在时自动建库；不做加密与只读限制
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>数据库文件绝对路径，备份/恢复需要直接操作该文件。</summary>
    public string DbFilePath { get; }

    /// <summary>
    /// 打开连接并启用外键级联与忙等待。
    /// foreign_keys 必须每个连接单独开启（SQLite 按连接生效）；
    /// busy_timeout 缓解并发写时的 SQLITE_BUSY。
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ExecutePragma(connection, "PRAGMA foreign_keys=ON;");
        ExecutePragma(connection, "PRAGMA busy_timeout=3000;");
        return connection;
    }

    private static void ExecutePragma(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        command.ExecuteNonQuery();
    }
}
