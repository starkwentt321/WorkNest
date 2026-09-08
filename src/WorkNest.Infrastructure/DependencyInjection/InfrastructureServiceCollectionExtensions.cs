using Microsoft.Extensions.DependencyInjection;
using WorkNest.Application.Abstractions;
using WorkNest.Infrastructure.Backup;
using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.Database.Migrations;
using WorkNest.Infrastructure.Logging;
using WorkNest.Infrastructure.Repositories;

namespace WorkNest.Infrastructure.DependencyInjection;

/// <summary>Infrastructure 层的一站式注册入口。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// 注册数据库、迁移器、三个仓储与备份服务，并初始化日志。
    /// dataRootDir 下强制分离 Data / Logs / Backups 三个子目录，
    /// 符合"数据库、日志、备份分开存放"的设计约定。
    /// </summary>
    public static IServiceCollection AddWorkNestInfrastructure(this IServiceCollection services, string dataRootDir)
    {
        var dataDir = Path.Combine(dataRootDir, "Data");
        var logsDir = Path.Combine(dataRootDir, "Logs");
        var backupsDir = Path.Combine(dataRootDir, "Backups");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(backupsDir);

        // 日志尽早初始化，后续注册与启动过程即可产生日志
        WorkNestLog.Initialize(logsDir);

        var dbPath = Path.Combine(dataDir, "worknest.db");

        // 历史版本的迁移前备份写在 Data/Backups，与设置页扫描的根 Backups 不一致；
        // 启动时一次性搬移到统一目录（同卷 File.Move，目标重名时保留现文件），让全部备份可在设置页看到
        MoveLegacyMigrateBackups(Path.Combine(dataDir, "Backups"), backupsDir);

        services.AddSingleton(_ => new WorkNestDb(dbPath));
        // 迁移前备份必须与设置页使用同一备份目录，否则迁移备份在设置页不可见
        services.AddSingleton(sp => new DbMigrator(
            sp.GetRequiredService<WorkNestDb>(),
            new IMigration[] { new Migration0001InitialSchema(), new Migration0002DropViewPreferenceAndAddResourceIndex() },
            backupsDir));
        services.AddSingleton<IWorkspaceRepository, SqliteWorkspaceRepository>();
        services.AddSingleton<IResourceRepository, SqliteResourceRepository>();
        services.AddSingleton<ISettingsRepository, SqliteSettingsRepository>();
        // 备份服务复用已注册的 WorkNestDb 单例（连接工厂无状态），不再重复装配 dbPath
        services.AddSingleton<IBackupService>(sp => new SqliteBackupService(
            sp.GetRequiredService<WorkNestDb>(), backupsDir));

        return services;
    }

    /// <summary>把旧版 Data/Backups 下的迁移备份搬移到统一备份目录；失败只记日志，不阻断启动。</summary>
    private static void MoveLegacyMigrateBackups(string legacyDir, string unifiedDir)
    {
        if (!Directory.Exists(legacyDir))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(legacyDir, "*.db"))
        {
            try
            {
                var targetPath = Path.Combine(unifiedDir, Path.GetFileName(filePath));
                if (!File.Exists(targetPath))
                {
                    File.Move(filePath, targetPath);
                    WorkNestLog.Info("Startup", $"历史迁移备份已搬移到统一备份目录：{Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                WorkNestLog.Warning("Startup", $"搬移历史迁移备份失败：{filePath}", ex);
            }
        }

        try
        {
            // 搬移后目录应为空；非空（还有未识别文件）时保留目录不动
            if (!Directory.EnumerateFileSystemEntries(legacyDir).Any())
            {
                Directory.Delete(legacyDir);
            }
        }
        catch (Exception ex)
        {
            WorkNestLog.Warning("Startup", $"清理旧迁移备份目录失败：{legacyDir}", ex);
        }
    }
}
