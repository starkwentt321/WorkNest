using Microsoft.Data.Sqlite;

namespace WorkNest.Infrastructure.Database.Migrations;

/// <summary>v1 初始 schema：全部核心表与索引。</summary>
public sealed class Migration0001InitialSchema : IMigration
{
    // 首版建表脚本，按设计文档原样执行，不做任何改写
    private const string Sql = """
        CREATE TABLE Workspace (
          Id INTEGER PRIMARY KEY AUTOINCREMENT,
          Name TEXT NOT NULL,
          Color TEXT NOT NULL,
          SortOrder INTEGER NOT NULL DEFAULT 0,
          LastUsedAt TEXT,
          CreatedAt TEXT NOT NULL,
          UpdatedAt TEXT NOT NULL
        );
        CREATE TABLE ResourceItem (
          Id INTEGER PRIMARY KEY AUTOINCREMENT,
          Type INTEGER NOT NULL,
          Name TEXT NOT NULL,
          Target TEXT NOT NULL COLLATE NOCASE,
          Arguments TEXT,
          WorkingDirectory TEXT,
          CreatedAt TEXT NOT NULL,
          UpdatedAt TEXT NOT NULL
        );
        CREATE UNIQUE INDEX UX_ResourceItem_Key ON ResourceItem(Type, Target, IFNULL(Arguments,''), IFNULL(WorkingDirectory,''));
        CREATE TABLE WorkspaceResource (
          WorkspaceId INTEGER NOT NULL REFERENCES Workspace(Id) ON DELETE CASCADE,
          ResourceId INTEGER NOT NULL REFERENCES ResourceItem(Id) ON DELETE CASCADE,
          IsPinned INTEGER NOT NULL DEFAULT 0,
          SortOrder INTEGER NOT NULL DEFAULT 0,
          RunCount INTEGER NOT NULL DEFAULT 0,
          LastUsedAt TEXT,
          PRIMARY KEY (WorkspaceId, ResourceId)
        );
        CREATE TABLE ResourceTag (
          ResourceId INTEGER NOT NULL REFERENCES ResourceItem(Id) ON DELETE CASCADE,
          Tag TEXT NOT NULL,
          PRIMARY KEY (ResourceId, Tag)
        );
        CREATE TABLE UsageRecord (
          Id INTEGER PRIMARY KEY AUTOINCREMENT,
          ResourceId INTEGER NOT NULL REFERENCES ResourceItem(Id) ON DELETE CASCADE,
          WorkspaceId INTEGER REFERENCES Workspace(Id) ON DELETE SET NULL,
          StartedAt TEXT NOT NULL,
          Result INTEGER NOT NULL,
          ErrorCode TEXT,
          DurationMs INTEGER
        );
        CREATE TABLE AppSetting (
          Key TEXT PRIMARY KEY,
          Value TEXT NOT NULL,
          UpdatedAt TEXT NOT NULL
        );
        CREATE TABLE ViewPreference (
          Id INTEGER PRIMARY KEY CHECK (Id = 1),
          ColumnOrder TEXT, ColumnWidths TEXT, VisibleColumns TEXT, SortColumn TEXT, SortDirection TEXT
        );
        """;

    public int Version => 1;

    public string Description => "初始 schema：核心表与唯一索引";

    public void Apply(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }
}
