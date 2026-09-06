using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkNest.Infrastructure.Logging;

namespace WorkNest.Infrastructure.Database;

/// <summary>
/// 版本化迁移执行器：按 SchemaVersion 表记录的版本号，顺序应用尚未执行的迁移。
/// 每个迁移在独立事务中执行；应用第一个迁移前先做 premigrate 备份。
/// </summary>
public sealed class DbMigrator
{
    private readonly WorkNestDb _db;
    private readonly IReadOnlyList<IMigration> _migrations;
    private readonly string? _backupsDir;

    /// <summary>生产构造：使用内置迁移清单，premigrate 备份写入数据库同级的 Backups 目录。</summary>
    public DbMigrator(WorkNestDb db)
        : this(db, new IMigration[] { new Migrations.Migration0001InitialSchema() }, backupsDir: null)
    {
    }

    /// <summary>测试/扩展构造：允许显式提供迁移清单与备份目录。</summary>
    public DbMigrator(WorkNestDb db, IEnumerable<IMigration> migrations, string? backupsDir = null)
    {
        _db = db;
        _migrations = migrations.OrderBy(m => m.Version).ToList();
        _backupsDir = backupsDir;
    }

    /// <summary>premigrate 备份目录：显式指定优先，否则取数据库文件所在目录下的 Backups。</summary>
    private string ResolveBackupsDir()
        => _backupsDir ?? Path.Combine(Path.GetDirectoryName(_db.DbFilePath) ?? ".", "Backups");

    /// <summary>把数据库迁移到最新版本；已是最新时直接返回（幂等）。</summary>
    public void Migrate()
    {
        using var connection = _db.OpenConnection();

        // 版本表不存在则先建；建表动作本身幂等
        using (var createVersionTable = connection.CreateCommand())
        {
            createVersionTable.CommandText =
                "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL);";
            createVersionTable.ExecuteNonQuery();
        }

        var currentVersion = ReadCurrentVersion(connection);

        // 前向版本保护：库版本高于本程序支持的版本，说明它由更高版本的 WorkNest 创建，
        // 旧程序继续读写可能破坏未知的表结构；明确报错退出，交由用户升级或还原备份
        var maxSupportedVersion = _migrations.Count == 0 ? 0 : _migrations.Max(m => m.Version);
        if (currentVersion > maxSupportedVersion)
        {
            throw new InvalidOperationException(
                $"数据库版本 v{currentVersion} 高于当前程序支持的 v{maxSupportedVersion}，" +
                "可能由更新版本的 WorkNest 创建。请升级 WorkNest 后再使用，或从备份还原数据；" +
                "继续使用旧版本可能损坏数据，已停止启动。");
        }

        var pending = _migrations.Where(m => m.Version > currentVersion).ToList();
        if (pending.Count == 0)
        {
            return; // 已是最新版本，无需任何动作
        }

        var targetVersion = pending[^1].Version;
        WorkNestLog.Info("DbMigrator", $"发现待应用迁移 v{currentVersion} -> v{targetVersion}，共 {pending.Count} 个。");

        CreatePremigrateBackupOrThrow(currentVersion, targetVersion);

        foreach (var migration in pending)
        {
            ApplyInOwnTransaction(connection, migration);
            WorkNestLog.Info("DbMigrator", $"迁移 v{migration.Version} 应用成功：{migration.Description}");
        }
    }

    private static int ReadCurrentVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
        var result = command.ExecuteScalar();
        return result is null || result is DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 迁移前备份：先 checkpoint 把 WAL 合并回主文件，再整文件复制，
    /// 保证快照包含全部已提交数据。备份失败视为环境问题，中止迁移。
    /// </summary>
    private void CreatePremigrateBackupOrThrow(int currentVersion, int targetVersion)
    {
        try
        {
            using (var checkpoint = _db.OpenConnection())
            using (var command = checkpoint.CreateCommand())
            {
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                command.ExecuteNonQuery();
            }

            var backupsDir = ResolveBackupsDir();
            Directory.CreateDirectory(backupsDir);
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(backupsDir, $"premigrate-v{currentVersion}-to-v{targetVersion}-{timestamp}.db");
            File.Copy(_db.DbFilePath, backupPath, overwrite: false);
            WorkNestLog.Info("DbMigrator", $"已创建迁移前备份：{backupPath}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"迁移前备份失败，已中止迁移（v{currentVersion} -> v{targetVersion}）：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 单个迁移在独立事务中执行：迁移脚本 + 版本记录同生共死。
    /// 用原生 BEGIN/COMMIT 而非 SqliteTransaction，确保脚本内所有命令都被事务覆盖。
    /// </summary>
    private static void ApplyInOwnTransaction(SqliteConnection connection, IMigration migration)
    {
        ExecuteRaw(connection, "BEGIN IMMEDIATE;");
        try
        {
            migration.Apply(connection);

            using var recordVersion = connection.CreateCommand();
            recordVersion.CommandText = "INSERT INTO SchemaVersion (Version, AppliedAt) VALUES ($version, $appliedAt);";
            recordVersion.Parameters.AddWithValue("$version", migration.Version);
            recordVersion.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            recordVersion.ExecuteNonQuery();

            ExecuteRaw(connection, "COMMIT;");
        }
        catch (Exception ex)
        {
            try
            {
                ExecuteRaw(connection, "ROLLBACK;");
            }
            catch (Exception rollbackEx)
            {
                WorkNestLog.Error("DbMigrator", $"迁移 v{migration.Version} 回滚时出现次生错误。", rollbackEx);
            }

            WorkNestLog.Error("DbMigrator", $"迁移 v{migration.Version} 执行失败，已回滚：{migration.Description}", ex);
            throw new InvalidOperationException($"数据库迁移 v{migration.Version} 执行失败（{migration.Description}），已回滚本次迁移，数据库保持原版本。{ex.Message}", ex);
        }
    }

    private static void ExecuteRaw(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }
}
