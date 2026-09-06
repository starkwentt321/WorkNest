using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkNest.Application.Abstractions;
using WorkNest.Domain;
using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.Logging;

namespace WorkNest.Infrastructure.Repositories;

/// <summary>基于 SQLite 的工作区仓储。</summary>
public sealed class SqliteWorkspaceRepository : IWorkspaceRepository
{
    private readonly WorkNestDb _db;

    public SqliteWorkspaceRepository(WorkNestDb db) => _db = db;

    public async Task<IReadOnlyList<Workspace>> GetAllAsync()
    {
        var list = new List<Workspace>();
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Color, SortOrder, LastUsedAt, CreatedAt, UpdatedAt
            FROM Workspace
            ORDER BY SortOrder, Name COLLATE NOCASE;
            """;
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(MapFromReader(reader));
        }

        return list;
    }

    public async Task<Workspace?> GetAsync(int id)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Color, SortOrder, LastUsedAt, CreatedAt, UpdatedAt
            FROM Workspace
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return MapFromReader(reader);
    }

    public async Task<int> AddAsync(Workspace workspace)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Workspace (Name, Color, SortOrder, LastUsedAt, CreatedAt, UpdatedAt)
            VALUES ($name, $color, $sortOrder, $lastUsedAt, $createdAt, $updatedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", workspace.Name);
        command.Parameters.AddWithValue("$color", workspace.Color);
        command.Parameters.AddWithValue("$sortOrder", workspace.SortOrder);
        AddNullableTime(command, "$lastUsedAt", workspace.LastUsedAt);
        command.Parameters.AddWithValue("$createdAt", SqliteTime.ToUtc(workspace.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", SqliteTime.ToUtc(workspace.UpdatedAt));
        var id = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        WorkNestLog.Info("WorkspaceRepository", $"新增工作区 Id={id} Name={workspace.Name}");
        return id;
    }

    public async Task UpdateAsync(Workspace workspace)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Workspace
            SET Name = $name, Color = $color, SortOrder = $sortOrder,
                LastUsedAt = $lastUsedAt, UpdatedAt = $updatedAt
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", workspace.Id);
        command.Parameters.AddWithValue("$name", workspace.Name);
        command.Parameters.AddWithValue("$color", workspace.Color);
        command.Parameters.AddWithValue("$sortOrder", workspace.SortOrder);
        AddNullableTime(command, "$lastUsedAt", workspace.LastUsedAt);
        command.Parameters.AddWithValue("$updatedAt", SqliteTime.ToUtc(workspace.UpdatedAt));
        var affected = await command.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            throw new InvalidOperationException($"更新工作区失败：Id={workspace.Id} 不存在。");
        }
    }

    /// <summary>关联的 WorkspaceResource 依赖外键 ON DELETE CASCADE 一并删除。</summary>
    public async Task DeleteAsync(int id)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Workspace WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var affected = await command.ExecuteNonQueryAsync();
        if (affected > 0)
        {
            WorkNestLog.Info("WorkspaceRepository", $"已删除工作区 Id={id}（关联行由外键级联清理）。");
        }
    }

    public async Task TouchLastUsedAsync(int id, DateTime utc)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Workspace
            SET LastUsedAt = $utc, UpdatedAt = $utc
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$utc", SqliteTime.ToUtc(utc));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> GetResourceCountAsync(int workspaceId)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM WorkspaceResource WHERE WorkspaceId = $id;";
        command.Parameters.AddWithValue("$id", workspaceId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static Workspace MapFromReader(SqliteDataReader reader)
    {
        return new Workspace
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            Color = reader.GetString(2),
            SortOrder = reader.GetInt32(3),
            LastUsedAt = reader.IsDBNull(4) ? null : SqliteTime.FromUtcOrNull(reader.GetString(4)),
            CreatedAt = SqliteTime.FromUtcOrNull(reader.GetString(5))!.Value,
            UpdatedAt = SqliteTime.FromUtcOrNull(reader.GetString(6))!.Value,
        };
    }

    private static void AddNullableTime(SqliteCommand command, string parameterName, DateTime? value)
    {
        command.Parameters.AddWithValue(parameterName, value.HasValue ? SqliteTime.ToUtc(value.Value) : DBNull.Value);
    }
}
