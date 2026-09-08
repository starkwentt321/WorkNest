using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkNest.Application.Abstractions;
using WorkNest.Domain;
using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.Logging;

namespace WorkNest.Infrastructure.Repositories;

/// <summary>基于 SQLite 的资源仓储；多步写入一律走原生事务保证原子性。</summary>
public sealed class SqliteResourceRepository : IResourceRepository
{
    private readonly WorkNestDb _db;

    public SqliteResourceRepository(WorkNestDb db) => _db = db;

    // 关联视图列集：资源字段 + 关联状态 + 聚合标签 + 聚合共享工作区
    private const string LinkSelectSql = """
        SELECT r.Id, r.Type, r.Name, r.Target, r.Arguments, r.WorkingDirectory,
               r.CreatedAt, r.UpdatedAt,
               wr.IsPinned, wr.SortOrder, wr.RunCount, wr.LastUsedAt,
               (SELECT GROUP_CONCAT(t.Tag, '|') FROM ResourceTag t WHERE t.ResourceId = r.Id) AS Tags,
               (SELECT GROUP_CONCAT(DISTINCT wr2.WorkspaceId) FROM WorkspaceResource wr2 WHERE wr2.ResourceId = r.Id) AS SharedIds
        FROM WorkspaceResource wr
        JOIN ResourceItem r ON r.Id = wr.ResourceId
        """;

    public async Task<IReadOnlyList<ResourceLinkRow>> GetForWorkspaceAsync(int workspaceId)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = LinkSelectSql + """

            WHERE wr.WorkspaceId = $workspaceId
            ORDER BY wr.IsPinned DESC, wr.SortOrder, r.Name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        return await ReadLinkRowsAsync(command);
    }

    public async Task<IReadOnlyList<ResourceLinkRow>> GetAllLinksAsync()
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = LinkSelectSql + """

            ORDER BY wr.IsPinned DESC, wr.SortOrder, r.Name COLLATE NOCASE;
            """;
        return await ReadLinkRowsAsync(command);
    }

    public async Task<IReadOnlyList<ResourceLinkRow>> GetRecentlyUsedForWorkspaceAsync(int workspaceId, int maxCount)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        // LastUsedAt 仅在成功启动时写入（决策 31/50），非空即代表“成功使用过”；
        // 按其倒序截取前 N 条，即最近使用视图的数据源（F09/决策 70）
        command.CommandText = LinkSelectSql + """

            WHERE wr.WorkspaceId = $workspaceId AND wr.LastUsedAt IS NOT NULL
            ORDER BY wr.LastUsedAt DESC
            LIMIT $maxCount;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$maxCount", maxCount);
        return await ReadLinkRowsAsync(command);
    }

    public async Task<ResourceItem?> FindByKeyAsync(ResourceKey key)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Type, Name, Target, Arguments, WorkingDirectory, CreatedAt, UpdatedAt
            FROM ResourceItem
            """;
        AppendUniqueKeyFilter(command, key.Type, key.Target, key.Arguments, key.WorkingDirectory);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return MapItem(reader);
    }

    /// <summary>
    /// 批量取回唯一键；单条 SELECT 一次读全表键列，
    /// 与 FindByKeyAsync / 唯一索引 UX_ResourceItem_Key 同口径（IFNULL 归一空串）。
    /// </summary>
    public async Task<IReadOnlyList<ResourceKey>> GetAllKeysAsync()
    {
        var keys = new List<ResourceKey>();
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Type, Target, IFNULL(Arguments, ''), IFNULL(WorkingDirectory, '')
            FROM ResourceItem;
            """;
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(new ResourceKey(
                (ResourceType)reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return keys;
    }

    public async Task<ResourceItem?> GetAsync(int id)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Type, Name, Target, Arguments, WorkingDirectory, CreatedAt, UpdatedAt
            FROM ResourceItem
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return MapItem(reader);
    }

    public async Task<int> AddAsync(ResourceItem item, IReadOnlyList<string> tags)
    {
        using var connection = _db.OpenConnection();
        SqliteRaw.Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            // 与导入建档共用同一插入例程（INSERT 全字段 + Id 回填 + 标签写入），避免重复实现
            await InsertItemAsync(connection, item, tags);
            SqliteRaw.Execute(connection, "COMMIT;");
            WorkNestLog.Info("ResourceRepository", $"新增资源 Id={item.Id} Type={item.Type} Target={item.Target}（{tags.Count} 个标签）。");
            return item.Id;
        }
        catch
        {
            RollbackQuietly(connection);
            throw;
        }
    }

    public async Task UpdateAsync(ResourceItem item, IReadOnlyList<string> tags)
    {
        using var connection = _db.OpenConnection();
        SqliteRaw.Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE ResourceItem
                    SET Type = $type, Name = $name, Target = $target,
                        Arguments = $arguments, WorkingDirectory = $workingDirectory, UpdatedAt = $updatedAt
                    WHERE Id = $id;
                    """;
                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$type", (int)item.Type);
                command.Parameters.AddWithValue("$name", item.Name);
                command.Parameters.AddWithValue("$target", item.Target);
                command.Parameters.AddWithValue("$arguments", (object?)item.Arguments ?? DBNull.Value);
                command.Parameters.AddWithValue("$workingDirectory", (object?)item.WorkingDirectory ?? DBNull.Value);
                command.Parameters.AddWithValue("$updatedAt", SqliteTime.ToUtc(item.UpdatedAt));
                var affected = await command.ExecuteNonQueryAsync();
                if (affected == 0)
                {
                    throw new InvalidOperationException($"更新资源失败：Id={item.Id} 不存在。");
                }
            }

            // 标签集合整体重建：先删后插，避免逐个比对
            using (var deleteTags = connection.CreateCommand())
            {
                deleteTags.CommandText = "DELETE FROM ResourceTag WHERE ResourceId = $id;";
                deleteTags.Parameters.AddWithValue("$id", item.Id);
                await deleteTags.ExecuteNonQueryAsync();
            }

            InsertTags(connection, item.Id, tags);
            SqliteRaw.Execute(connection, "COMMIT;");
        }
        catch
        {
            RollbackQuietly(connection);
            throw;
        }
    }

    public async Task AddLinkAsync(int workspaceId, int resourceId, bool isPinned, int sortOrder)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WorkspaceResource (WorkspaceId, ResourceId, IsPinned, SortOrder, RunCount, LastUsedAt)
            VALUES ($workspaceId, $resourceId, $isPinned, $sortOrder, 0, NULL);
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$resourceId", resourceId);
        command.Parameters.AddWithValue("$isPinned", isPinned ? 1 : 0);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        await command.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public async Task<int> ImportLinksAsync(int workspaceId, IReadOnlyList<PendingResourceLink> links, bool replaceExisting)
    {
        using var connection = _db.OpenConnection();
        SqliteRaw.Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            var processed = new HashSet<int>();
            if (replaceExisting)
            {
                // 覆盖语义：先清空全部关联，再清理已无任何归属的资源，
                // 与逐条移除最后关联的行为保持一致（标签/使用记录由外键级联清理）
                using (var clearLinks = connection.CreateCommand())
                {
                    clearLinks.CommandText = "DELETE FROM WorkspaceResource WHERE WorkspaceId = $workspaceId;";
                    clearLinks.Parameters.AddWithValue("$workspaceId", workspaceId);
                    await clearLinks.ExecuteNonQueryAsync();
                }

                using (var deleteOrphans = connection.CreateCommand())
                {
                    deleteOrphans.CommandText = """
                        DELETE FROM ResourceItem
                        WHERE Id NOT IN (SELECT DISTINCT ResourceId FROM WorkspaceResource);
                        """;
                    await deleteOrphans.ExecuteNonQueryAsync();
                }
            }
            else
            {
                // 合并语义：已在该工作区的资源不再重复加关联
                using var existingLinks = connection.CreateCommand();
                existingLinks.CommandText = "SELECT ResourceId FROM WorkspaceResource WHERE WorkspaceId = $workspaceId;";
                existingLinks.Parameters.AddWithValue("$workspaceId", workspaceId);
                using var reader = await existingLinks.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    processed.Add(reader.GetInt32(0));
                }
            }

            var added = 0;
            foreach (var link in links)
            {
                var item = link.Item;
                // 唯一键查重在事务连接内进行，与建档同生共死，避免并发建档撞唯一索引
                var existingId = await FindIdByKeyAsync(connection, item.Type, item.Target, item.Arguments, item.WorkingDirectory);
                if (existingId is null)
                {
                    existingId = await InsertItemAsync(connection, item, link.Tags);
                }

                // processed 兼作“本次已处理”集合：同名重复条目只加一次关联
                if (processed.Add(existingId.Value))
                {
                    added += await InsertLinkIgnoreConflictAsync(connection, workspaceId, existingId.Value, link.IsPinned, link.SortOrder);
                }
            }

            SqliteRaw.Execute(connection, "COMMIT;");
            WorkNestLog.Info("ResourceRepository",
                $"工作区 Id={workspaceId} 导入完成：新增关联 {added} 条（replaceExisting={replaceExisting}）。");
            return added;
        }
        catch
        {
            RollbackQuietly(connection);
            throw;
        }
    }

    /// <summary>事务连接内的唯一键查重；WHERE/参数口径经 AppendUniqueKeyFilter 与 FindByKeyAsync 共享。</summary>
    private static async Task<int?> FindIdByKeyAsync(
        SqliteConnection connection, ResourceType type, string target, string? arguments, string? workingDirectory)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id FROM ResourceItem
            """;
        AppendUniqueKeyFilter(command, type, target, arguments, workingDirectory);
        var result = await command.ExecuteScalarAsync();
        return result is null || result is DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 拼接唯一键查重 WHERE 子句并绑定参数：与唯一索引 UX_ResourceItem_Key 的 IFNULL(...) 表达式严格对齐，
    /// 参数侧把 null 归一为 ''；Target 比较显式声明 NOCASE，实现路径大小写不敏感查重。
    /// FindByKeyAsync 与 FindIdByKeyAsync 的 SELECT 列不同，WHERE/参数归一口径必须保持一致。
    /// </summary>
    private static void AppendUniqueKeyFilter(
        SqliteCommand command, ResourceType type, string target, string? arguments, string? workingDirectory)
    {
        // 片段以空行开头：与基础 SELECT 拼接时补出 WHERE 前的换行（与 LinkSelectSql 拼接风格一致）
        command.CommandText += """

            WHERE Type = $type
              AND Target = $target COLLATE NOCASE
              AND IFNULL(Arguments, '') = $arguments
              AND IFNULL(WorkingDirectory, '') = $workingDirectory
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$type", (int)type);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$arguments", arguments ?? string.Empty);
        command.Parameters.AddWithValue("$workingDirectory", workingDirectory ?? string.Empty);
    }

    /// <summary>事务连接内插入资源与标签并返回自增 Id。</summary>
    private static async Task<int> InsertItemAsync(SqliteConnection connection, ResourceItem item, IReadOnlyList<string> tags)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO ResourceItem (Type, Name, Target, Arguments, WorkingDirectory, CreatedAt, UpdatedAt)
                VALUES ($type, $name, $target, $arguments, $workingDirectory, $createdAt, $updatedAt);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$type", (int)item.Type);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$target", item.Target);
            command.Parameters.AddWithValue("$arguments", (object?)item.Arguments ?? DBNull.Value);
            command.Parameters.AddWithValue("$workingDirectory", (object?)item.WorkingDirectory ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", SqliteTime.ToUtc(item.CreatedAt));
            command.Parameters.AddWithValue("$updatedAt", SqliteTime.ToUtc(item.UpdatedAt));
            item.Id = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        InsertTags(connection, item.Id, tags);
        return item.Id;
    }

    /// <summary>插入关联并忽略已存在的主键冲突，返回实际新增行数。</summary>
    private static async Task<int> InsertLinkIgnoreConflictAsync(
        SqliteConnection connection, int workspaceId, int resourceId, bool isPinned, int sortOrder)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO WorkspaceResource (WorkspaceId, ResourceId, IsPinned, SortOrder, RunCount, LastUsedAt)
            VALUES ($workspaceId, $resourceId, $isPinned, $sortOrder, 0, NULL);
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$resourceId", resourceId);
        command.Parameters.AddWithValue("$isPinned", isPinned ? 1 : 0);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateLinkAsync(int workspaceId, int resourceId, bool isPinned, int? sortOrder)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        // sortOrder 为 null 表示只改置顶状态，不动手动顺序
        command.CommandText = sortOrder.HasValue
            ? """
              UPDATE WorkspaceResource
              SET IsPinned = $isPinned, SortOrder = $sortOrder
              WHERE WorkspaceId = $workspaceId AND ResourceId = $resourceId;
              """
            : """
              UPDATE WorkspaceResource
              SET IsPinned = $isPinned
              WHERE WorkspaceId = $workspaceId AND ResourceId = $resourceId;
              """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$resourceId", resourceId);
        command.Parameters.AddWithValue("$isPinned", isPinned ? 1 : 0);
        if (sortOrder.HasValue)
        {
            command.Parameters.AddWithValue("$sortOrder", sortOrder.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 移除单个关联；若这是该资源最后一个关联，资源已无归属，
    /// 在同一事务内删除资源本体，由外键级联清掉标签/使用记录。
    /// </summary>
    public async Task RemoveLinkAsync(int workspaceId, int resourceId)
    {
        using var connection = _db.OpenConnection();
        SqliteRaw.Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            using (var deleteLink = connection.CreateCommand())
            {
                deleteLink.CommandText = """
                    DELETE FROM WorkspaceResource
                    WHERE WorkspaceId = $workspaceId AND ResourceId = $resourceId;
                    """;
                deleteLink.Parameters.AddWithValue("$workspaceId", workspaceId);
                deleteLink.Parameters.AddWithValue("$resourceId", resourceId);
                await deleteLink.ExecuteNonQueryAsync();
            }

            var remaining = await CountLinksAsync(connection, resourceId);
            if (remaining == 0)
            {
                using var deleteItem = connection.CreateCommand();
                deleteItem.CommandText = "DELETE FROM ResourceItem WHERE Id = $id;";
                deleteItem.Parameters.AddWithValue("$id", resourceId);
                await deleteItem.ExecuteNonQueryAsync();
                WorkNestLog.Info("ResourceRepository", $"资源 Id={resourceId} 已无任何关联，随最后关联一并删除。");
            }

            SqliteRaw.Execute(connection, "COMMIT;");
        }
        catch
        {
            RollbackQuietly(connection);
            throw;
        }
    }

    public async Task<IReadOnlyList<int>> GetLinkedWorkspaceIdsAsync(int resourceId)
    {
        var ids = new List<int>();
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT WorkspaceId FROM WorkspaceResource WHERE ResourceId = $id ORDER BY WorkspaceId;";
        command.Parameters.AddWithValue("$id", resourceId);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    /// <summary>
    /// 成功启动的一次性事务：关联行的 RunCount/LastUsedAt、工作区 LastUsedAt、使用流水三者同生共死。
    /// RunCount 更新带关联存在条件：关联不存在时不产生半截数据。
    /// </summary>
    public async Task RecordSuccessAsync(int workspaceId, int resourceId, DateTime utc, int? durationMs)
    {
        using var connection = _db.OpenConnection();
        SqliteRaw.Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            using (var touchLink = connection.CreateCommand())
            {
                touchLink.CommandText = """
                    UPDATE WorkspaceResource
                    SET RunCount = RunCount + 1, LastUsedAt = $now
                    WHERE WorkspaceId = $workspaceId AND ResourceId = $resourceId;
                    """;
                touchLink.Parameters.AddWithValue("$now", SqliteTime.ToUtc(utc));
                touchLink.Parameters.AddWithValue("$workspaceId", workspaceId);
                touchLink.Parameters.AddWithValue("$resourceId", resourceId);
                await touchLink.ExecuteNonQueryAsync();
            }

            using (var touchWorkspace = connection.CreateCommand())
            {
                touchWorkspace.CommandText = """
                    UPDATE Workspace
                    SET LastUsedAt = $now, UpdatedAt = $now
                    WHERE Id = $workspaceId;
                    """;
                touchWorkspace.Parameters.AddWithValue("$now", SqliteTime.ToUtc(utc));
                touchWorkspace.Parameters.AddWithValue("$workspaceId", workspaceId);
                await touchWorkspace.ExecuteNonQueryAsync();
            }

            using (var insertRecord = connection.CreateCommand())
            {
                insertRecord.CommandText = """
                    INSERT INTO UsageRecord (ResourceId, WorkspaceId, StartedAt, Result, ErrorCode, DurationMs)
                    VALUES ($resourceId, $workspaceId, $startedAt, 1, NULL, $durationMs);
                    """;
                insertRecord.Parameters.AddWithValue("$resourceId", resourceId);
                insertRecord.Parameters.AddWithValue("$workspaceId", workspaceId);
                insertRecord.Parameters.AddWithValue("$startedAt", SqliteTime.ToUtc(utc));
                AddNullableInt(insertRecord, "$durationMs", durationMs);
                await insertRecord.ExecuteNonQueryAsync();
            }

            SqliteRaw.Execute(connection, "COMMIT;");
        }
        catch
        {
            RollbackQuietly(connection);
            throw;
        }
    }

    private static async Task<int> CountLinksAsync(SqliteConnection connection, int resourceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM WorkspaceResource WHERE ResourceId = $id;";
        command.Parameters.AddWithValue("$id", resourceId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static void InsertTags(SqliteConnection connection, int resourceId, IReadOnlyList<string> tags)
    {
        // Distinct 防止重复标签触发主键冲突
        foreach (var tag in tags.Distinct())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ResourceTag (ResourceId, Tag) VALUES ($resourceId, $tag);";
            command.Parameters.AddWithValue("$resourceId", resourceId);
            command.Parameters.AddWithValue("$tag", tag);
            command.ExecuteNonQuery();
        }
    }

    private static async Task<List<ResourceLinkRow>> ReadLinkRowsAsync(SqliteCommand command)
    {
        var rows = new List<ResourceLinkRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var item = MapItem(reader);
            // GROUP_CONCAT 结果为 NULL 表示无标签/无共享工作区
            var tags = reader.IsDBNull(12)
                ? []
                : reader.GetString(12).Split('|');
            var sharedIds = ParseSharedWorkspaceIds(reader.IsDBNull(13) ? null : reader.GetString(13));
            rows.Add(new ResourceLinkRow(
                item,
                reader.GetInt64(8) != 0,
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.IsDBNull(11) ? null : SqliteTime.FromUtcOrNull(reader.GetString(11)),
                tags,
                sharedIds));
        }

        return rows;
    }

    /// <summary>GROUP_CONCAT(DISTINCT WorkspaceId) 默认以逗号分隔；解析失败的段直接丢弃。</summary>
    private static List<int> ParseSharedWorkspaceIds(string? aggregated)
    {
        if (aggregated is null)
        {
            return [];
        }

        var ids = new List<int>();
        foreach (var segment in aggregated.Split(','))
        {
            if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static ResourceItem MapItem(SqliteDataReader reader)
    {
        return new ResourceItem
        {
            Id = reader.GetInt32(0),
            Type = (ResourceType)reader.GetInt32(1),
            Name = reader.GetString(2),
            Target = reader.GetString(3),
            Arguments = reader.IsDBNull(4) ? null : reader.GetString(4),
            WorkingDirectory = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = SqliteTime.FromUtcOrNull(reader.GetString(6))!.Value,
            UpdatedAt = SqliteTime.FromUtcOrNull(reader.GetString(7))!.Value,
        };
    }

    private static void AddNullableInt(SqliteCommand command, string parameterName, int? value)
    {
        command.Parameters.AddWithValue(parameterName, value.HasValue ? value.Value : DBNull.Value);
    }

    private static void RollbackQuietly(SqliteConnection connection)
    {
        try
        {
            SqliteRaw.Execute(connection, "ROLLBACK;");
        }
        catch
        {
            // 连接已损坏等场景下回滚失败无需再抛，保留原始异常即可
        }
    }
}
