using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.Database.Migrations;
using WorkNest.Infrastructure.Repositories;
using Xunit;

namespace WorkNest.IntegrationTests;

/// <summary>键值设置仓储：写入、读取与覆盖。</summary>
public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly TempWorkNestRoot _root = new();

    private SqliteSettingsRepository CreateRepository()
    {
        new DbMigrator(_root.CreateDb(), new IMigration[] { new Migration0001InitialSchema() }, _root.BackupsDir).Migrate();
        return new SqliteSettingsRepository(_root.CreateDb());
    }

    [Fact]
    public async Task Get_MissingKey_ReturnsNull()
    {
        var repository = CreateRepository();

        Assert.Null(await repository.GetAsync("no.such.key"));
    }

    [Fact]
    public async Task SetThenGet_Roundtrip()
    {
        var repository = CreateRepository();

        await repository.SetAsync("theme", "dark");

        Assert.Equal("dark", await repository.GetAsync("theme"));
    }

    [Fact]
    public async Task Set_SameKey_OverridesValue()
    {
        var repository = CreateRepository();

        await repository.SetAsync("theme", "dark");
        await repository.SetAsync("theme", "light");

        Assert.Equal("light", await repository.GetAsync("theme"));
        // UPSERT 不应产生第二行
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM AppSetting WHERE Key = 'theme'"));
    }

    private object? Scalar(string sql)
    {
        // WorkNestDb 是连接工厂本身，不持有连接，无需 dispose
        var db = _root.CreateDb();
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose() => _root.Dispose();
}
