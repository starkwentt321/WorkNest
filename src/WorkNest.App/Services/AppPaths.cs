using System.IO;

namespace WorkNest.App.Services;

/// <summary>
/// App 层数据路径约定（决策 100）：统一收敛 %LocalAppData%\WorkNest 下的目录拼接，
/// 避免各处独立拼串造成口径漂移。Infrastructure 侧的目录名约定保持其内部现状，不在此重构。
/// </summary>
internal static class AppPaths
{
    /// <summary>用户数据根目录：%LocalAppData%\WorkNest（决策 100）。</summary>
    internal static string DataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkNest");

    internal static string LogsDir => Path.Combine(DataRoot, "Logs");

    internal static string DataDir => Path.Combine(DataRoot, "Data");

    internal static string BackupsDir => Path.Combine(DataRoot, "Backups");

    /// <summary>
    /// 确保目录存在后在资源管理器中打开（CreateDirectory 幂等，已存在时不抛）。
    /// 返回是否成功；调用方按场景决定失败时是静默还是提示。
    /// </summary>
    internal static bool OpenOrCreateInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            FolderItemOps.OpenInExplorer(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
