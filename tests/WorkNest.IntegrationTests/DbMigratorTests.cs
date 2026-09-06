using Microsoft.Data.Sqlite;
using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.Database.Migrations;
using Xunit;

namespace WorkNest.IntegrationTests;

/// <summary>迁移器行为：版本记录、幂等性、迁移前备份。</summary>
public sealed class DbMigratorTests : IDisposable
{
    private readonly TempWorkNestRoot _root = new();

    [Fact]
    public void FreshDatabase_Migrate_WritesVersion1Record()
    {
        var migrator = new DbMigrator(_root.CreateDb(), new IMigration[] { new Migration0001InitialSchema() }, _root.BackupsDir);

        migrator.Migrate();

        // SchemaVersion 应恰好有一条 v1 记录
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM SchemaVersion WHERE Version = 1"));
        // 核心表应存在
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Workspace'"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ResourceItem'"));
    }

    [Fact]
    public void Migrate_Twice_IsIdempotent()
    {
        var migrator = new DbMigrator(_root.CreateDb(), new IMigration[] { new Migration0001InitialSchema() }, _root.BackupsDir);

        migrator.Migrate();
        migrator.Migrate(); // 第二次应为无操作，不报错

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM SchemaVersion"));
    }

    [Fact]
    public void Migrate_CreatesPremigrateBackup()
    {
        var migrator = new DbMigrator(_root.CreateDb(), new IMigration[] { new Migration0001InitialSchema() }, _root.BackupsDir);

        migrator.Migrate();

        // 全新库从 v0 升到 v1，应产生 premigrate-v0-to-v1-* 备份文件
        var backups = Directory.GetFiles(_root.BackupsDir, "premigrate-v0-to-v1-*.db");
        Assert.Single(backups);
        Assert.True(new FileInfo(backups[0]).Length > 0, "premigrate 备份不应是空文件。");
    }

    [Fact]
    public void Migrate_Failure_RollsBackAndThrows()
    {
        // 一个必失败的迁移：引用不存在的表
        var brokenMigration = new BrokenMigration();
        var migrator = new DbMigrator(_root.CreateDb(), new IMigration[] { new Migration0001InitialSchema(), brokenMigration }, _root.BackupsDir);

        var exception = Assert.Throws<InvalidOperationException>(() => migrator.Migrate());

        // v1 应已提交成功，失败的 v2 不应留下版本记录
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM SchemaVersion WHERE Version = 1"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM SchemaVersion WHERE Version = 2"));
        Assert.Contains("v2", exception.Message);
    }

    [Fact]
    public void Migrate_DatabaseNewerThanSupported_ThrowsForwardProtection()
    {
        // 先迁到 v1，再把版本号改到支持范围之上，模拟“更高版本 WorkNest 创建的库”
        var migrator = new DbMigrator(_root.CreateDb(), new IMigration[] { new Migration0001InitialSchema() }, _root.BackupsDir);
        migrator.Migrate();
        Execute("UPDATE SchemaVersion SET Version = 99;");

        var exception = Assert.Throws<InvalidOperationException>(() => migrator.Migrate());

        Assert.Contains("高于当前程序支持", exception.Message);
        // 版本记录未被篡改回可写状态，库内容保持原样
        Assert.Equal(99L, Scalar("SELECT MAX(Version) FROM SchemaVersion"));
    }

    private void Execute(string sql)
    {
        var db = _root.CreateDb();
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        // WorkNestDb 是连接工厂本身，不持有连接，无需 dispose；真正的连接在方法内关闭
        var db = _root.CreateDb();
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>故意引用不存在表的迁移，用于验证失败回滚。</summary>
    private sealed class BrokenMigration : IMigration
    {
        public int Version => 2;

        public string Description => "故意失败的迁移";

        public void Apply(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO NoSuchTable (Id) VALUES (1);";
            command.ExecuteNonQuery();
        }
    }

    public void Dispose() => _root.Dispose();
}
