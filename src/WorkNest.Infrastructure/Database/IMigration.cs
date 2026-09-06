using Microsoft.Data.Sqlite;

namespace WorkNest.Infrastructure.Database;

/// <summary>
/// 单个数据库迁移步骤。实现只在 Apply 内执行 DDL/DML，
/// 事务的开启、提交与回滚由 DbMigrator 统一负责。
/// </summary>
public interface IMigration
{
    /// <summary>目标 schema 版本号，必须严格递增且唯一。</summary>
    int Version { get; }

    /// <summary>人类可读描述，用于日志与 premigrate 备份命名之外的诊断。</summary>
    string Description { get; }

    void Apply(SqliteConnection connection);
}
