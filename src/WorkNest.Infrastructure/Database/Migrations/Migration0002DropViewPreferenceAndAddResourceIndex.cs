using Microsoft.Data.Sqlite;

namespace WorkNest.Infrastructure.Database.Migrations;

/// <summary>v2：删除预留未用的 ViewPreference 表，并为 WorkspaceResource(ResourceId) 补充确定索引。</summary>
public sealed class Migration0002DropViewPreferenceAndAddResourceIndex : IMigration
{
    // DROP：v1 预留的视图偏好表从未接入任何读写，实际存储走 AppSetting 的 state.viewPreference 键，该键已随死代码清理移除，表无保留价值
    // CREATE INDEX：消除关联子查询对 SQLite automatic_index 启发式的依赖，让 GetAllLinksAsync / GetLinkedWorkspaceIdsAsync / CountLinksAsync 的 ResourceId 前导过滤走确定索引
    private const string Sql = """
        DROP TABLE IF EXISTS ViewPreference;
        CREATE INDEX IF NOT EXISTS IX_WorkspaceResource_Resource ON WorkspaceResource(ResourceId);
        """;

    public int Version => 2;

    public string Description => "删除未用的 ViewPreference 表，新增 WorkspaceResource(ResourceId) 索引";

    public void Apply(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }
}
