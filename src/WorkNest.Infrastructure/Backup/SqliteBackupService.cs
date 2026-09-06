using System.Globalization;
using WorkNest.Application.Abstractions;
using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.Logging;

namespace WorkNest.Infrastructure.Backup;

/// <summary>
/// 基于数据库文件复制的快照备份服务。
/// 复制前先做 WAL checkpoint，确保 .db 主文件包含全部已提交数据。
/// </summary>
public sealed class SqliteBackupService : IBackupService
{
    private readonly WorkNestDb _db;
    private readonly string _backupsDir;

    public SqliteBackupService(WorkNestDb db, string backupsDir)
    {
        _db = db;
        _backupsDir = backupsDir;
    }

    public async Task<string> CreateSnapshotAsync(string reason)
    {
        // checkpoint 后主文件即为完整一致状态，直接复制即可
        using (var connection = _db.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync();
        }

        Directory.CreateDirectory(_backupsDir);
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var fileName = $"auto-{SanitizeReason(reason)}-{timestamp}.db";
        var targetPath = Path.Combine(_backupsDir, fileName);

        // 同一秒内同 reason 可能重名：加序号后缀避免覆盖先前快照
        var attempt = 1;
        while (File.Exists(targetPath))
        {
            fileName = $"auto-{SanitizeReason(reason)}-{timestamp}-{attempt}.db";
            targetPath = Path.Combine(_backupsDir, fileName);
            attempt++;
        }

        File.Copy(_db.DbFilePath, targetPath, overwrite: false);
        TrimAutoBackups();
        WorkNestLog.Info("BackupService", $"已创建数据库快照：{targetPath}");
        return targetPath;
    }

    public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync()
    {
        var list = new List<BackupInfo>();
        if (!Directory.Exists(_backupsDir))
        {
            return Task.FromResult<IReadOnlyList<BackupInfo>>(list);
        }

        foreach (var filePath in Directory.EnumerateFiles(_backupsDir, "*.db"))
        {
            var info = new FileInfo(filePath);
            var (reason, createdAt) = ParseFileName(info.Name);
            // 文件名解析不出时间时回退到文件最后写入时间，保证排序仍有意义
            list.Add(new BackupInfo(filePath, createdAt ?? info.LastWriteTime, info.Length, reason));
        }

        list.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return Task.FromResult<IReadOnlyList<BackupInfo>>(list);
    }

    public async Task RestoreAsync(string backupFilePath)
    {
        if (!File.Exists(backupFilePath))
        {
            throw new FileNotFoundException($"要恢复的备份文件不存在：{backupFilePath}", backupFilePath);
        }

        // 恢复源先复制到不参与保留策略清理的临时位置并校验：
        // 若源恰好是最旧的 auto 备份，下一步安全快照触发的清理会把它删掉，
        // 造成“恢复动作自己删掉了恢复源”。临时副本还兼作覆盖主库的数据来源。
        var tempRestorePath = Path.Combine(Path.GetTempPath(), $"worknest-restore-{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(backupFilePath, tempRestorePath, overwrite: false);
            EnsureSqliteHeader(tempRestorePath);

            // 恢复前先对当前状态做安全快照；快照失败则整体中止，绝不动数据库文件
            var preRestorePath = await CreateSnapshotAsync("pre-restore");

            try
            {
                File.Copy(tempRestorePath, _db.DbFilePath, overwrite: true);
            }
            catch (Exception copyEx)
            {
                // 覆盖中断可能留下半写的主库：尽力用恢复前安全快照还原，保证当前数据仍可用
                RestoreMainDbQuietly(preRestorePath);
                throw new InvalidOperationException(
                    $"恢复备份时写入数据库失败，已尝试用恢复前快照还原当前数据：{copyEx.Message}", copyEx);
            }

            DeleteSideFiles();

            WorkNestLog.Info("BackupService", $"已从备份恢复数据库：{backupFilePath}");
        }
        finally
        {
            DeleteFileQuietly(tempRestorePath);
        }
    }

    /// <summary>校验文件具备 SQLite 数据库头，拒绝把文本/损坏文件当备份恢复。</summary>
    private static void EnsureSqliteHeader(string filePath)
    {
        var header = new byte[16];
        using (var stream = File.OpenRead(filePath))
        {
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
                || !header.AsSpan().SequenceEqual("SQLite format 3\0"u8))
            {
                throw new InvalidDataException($"文件不是有效的 SQLite 数据库，已中止恢复：{filePath}");
            }
        }
    }

    /// <summary>用安全快照还原主库（尽力而为）：失败只记日志，保留原始异常向上传播。</summary>
    private void RestoreMainDbQuietly(string snapshotPath)
    {
        try
        {
            File.Copy(snapshotPath, _db.DbFilePath, overwrite: true);
            DeleteSideFiles();
            WorkNestLog.Info("BackupService", "已用恢复前安全快照还原主数据库文件。");
        }
        catch (Exception ex)
        {
            WorkNestLog.Error("BackupService", "用安全快照还原主库失败，当前数据可能不可用，请从备份目录手动恢复。", ex);
        }
    }

    /// <summary>删除主库残留的 wal/shm 辅助文件，避免恢复后被误判为脏页。</summary>
    private void DeleteSideFiles()
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sideFile = _db.DbFilePath + suffix;
            if (File.Exists(sideFile))
            {
                File.Delete(sideFile);
            }
        }
    }

    /// <summary>清理临时文件；删除失败不影响恢复结果，仅记日志。</summary>
    private static void DeleteFileQuietly(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            WorkNestLog.Warning("BackupService", $"清理临时文件失败：{filePath}", ex);
        }
    }

    /// <summary>只保留最新的 10 份 auto- 前缀快照；premigrate 等其他前缀不受影响。</summary>
    private void TrimAutoBackups()
    {
        var autoFiles = Directory.EnumerateFiles(_backupsDir, "auto-*.db")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();

        for (var index = 10; index < autoFiles.Count; index++)
        {
            try
            {
                autoFiles[index].Delete();
                WorkNestLog.Info("BackupService", $"快照超出保留数量，已删除最旧备份：{autoFiles[index].Name}");
            }
            catch (Exception ex)
            {
                WorkNestLog.Warning("BackupService", $"删除旧快照失败：{autoFiles[index].Name}", ex);
            }
        }
    }

    /// <summary>文件名中的非法字符替换为 '-'，避免 reason 破坏路径。</summary>
    private static string SanitizeReason(string reason)
        => string.Join("_", reason.Split(Path.GetInvalidFileNameChars()));

    /// <summary>
    /// 从文件名解析 Reason 与创建时间：auto-{reason}-{yyyyMMddHHmmss}.db。
    /// 非 auto- 前缀或格式不符时按 manual 处理，时间为 null（由调用方回退文件时间）。
    /// </summary>
    private static (string Reason, DateTime? CreatedAt) ParseFileName(string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        if (!nameWithoutExt.StartsWith("auto-", StringComparison.Ordinal))
        {
            return ("manual", null);
        }

        var rest = nameWithoutExt["auto-".Length..];
        // 末尾 14 位是 yyyyMMddHHmmss，其前应有一个 '-' 作为与 reason 的分隔
        if (rest.Length < 16 || rest[^15] != '-'
            || !DateTime.TryParseExact(rest[^14..], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var createdAt))
        {
            return ("manual", null);
        }

        return (rest[..^15], createdAt);
    }
}
