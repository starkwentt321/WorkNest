using System.Globalization;

namespace WorkNest.Infrastructure.Logging;

/// <summary>
/// 静态日志门面：不依赖 Microsoft.Extensions.Logging，供本层与迁移器直接使用。
/// 日志按本地日期写入 app-yyyyMMdd.log，线程安全；任何日志动作绝不抛出异常影响业务。
/// Initialize 之前调用一律静默丢弃，避免测试环境与启动早期的副作用。
/// </summary>
public static class WorkNestLog
{
    private const int RetentionDays = 30;
    private const long MaxTotalBytes = 100 * 1024 * 1024; // 目录日志总量上限 100MB

    private static readonly object Gate = new();

    private static string? _logDir;

    /// <summary>初始化日志目录并执行清理：先删过期文件，总量超限再从最旧删起。</summary>
    public static void Initialize(string logDir)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                DeleteExpiredLogs(logDir);
                EnforceTotalSizeLimit(logDir);
            }
            catch
            {
                // 目录不可用时不阻断启动；写入时会再次吞掉异常
            }

            _logDir = logDir;
        }
    }

    public static void Info(string category, string message, Exception? ex = null)
        => Write("INFO", category, message, ex);

    public static void Warning(string category, string message, Exception? ex = null)
        => Write("WARN", category, message, ex);

    public static void Error(string category, string message, Exception? ex = null)
        => Write("ERROR", category, message, ex);

    private static void Write(string level, string category, string message, Exception? ex)
    {
        // 未初始化（或初始化失败）时静默丢弃，保证零副作用
        var logDir = _logDir;
        if (logDir is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var fileName = Path.Combine(logDir, $"app-{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{category}] {message}";
                if (ex is not null)
                {
                    // 异常详情另起一行：异常类型、消息与堆栈
                    line += Environment.NewLine
                            + $"    {ex.GetType().FullName}: {ex.Message}"
                            + Environment.NewLine
                            + ex.StackTrace;
                }

                File.AppendAllText(fileName, line + Environment.NewLine);
            }
        }
        catch
        {
            // 磁盘故障等场景下吞掉异常，日志失败不能拖垮业务
        }
    }

    /// <summary>删除超过保留期的日志文件；单文件删除失败不中断整体清理。</summary>
    private static void DeleteExpiredLogs(string logDir)
    {
        var threshold = DateTime.Now.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(logDir, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < threshold)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 文件被占用等场景跳过该文件
            }
        }
    }

    /// <summary>目录总量超过上限时，按最后写入时间从最旧开始删除直到达标。</summary>
    private static void EnforceTotalSizeLimit(string logDir)
    {
        var files = Directory.EnumerateFiles(logDir, "*.log")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.LastWriteTime)
            .ToList();

        var totalBytes = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (totalBytes <= MaxTotalBytes)
            {
                break;
            }

            try
            {
                totalBytes -= file.Length;
                file.Delete();
            }
            catch
            {
                // 删除失败时把大小加回去，避免统计失真
                totalBytes += file.Length;
            }
        }
    }
}
