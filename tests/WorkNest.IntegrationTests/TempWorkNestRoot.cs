using WorkNest.Infrastructure.Database;

namespace WorkNest.IntegrationTests;

/// <summary>
/// 每个测试实例独占一个 %TEMP% 下的 Guid 目录，Dispose 时整体删除，测试间互不污染。
/// 目录结构：root/Data（数据库）、root/Backups（备份）。
/// </summary>
internal sealed class TempWorkNestRoot : IDisposable
{
    public string RootPath { get; }
    public string DataDir { get; }
    public string BackupsDir { get; }
    public string DbPath { get; }

    public TempWorkNestRoot()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "worknest-it-" + Guid.NewGuid().ToString("N"));
        DataDir = Path.Combine(RootPath, "Data");
        BackupsDir = Path.Combine(RootPath, "Backups");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BackupsDir);
        DbPath = Path.Combine(DataDir, "worknest.db");
    }

    public WorkNestDb CreateDb() => new(DbPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch
        {
            // 文件被占用等场景下允许 Windows 稍后清理，不影响测试结论
        }
    }
}
