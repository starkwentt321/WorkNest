using System.Diagnostics;
using System.IO;

namespace WorkNest.App.Services;

/// <summary>定位当前文件所属的 Git 仓库，并启动 Git Extensions 的稳定命令行入口。</summary>
public static class GitIntegration
{
    // 右键菜单会频繁构建；Git Extensions 的安装路径只需在进程内探测一次，避免扫描 PATH 拖慢菜单响应。
    private static readonly Lazy<string?> GitExtensionsExecutable = new(LocateGitExtensions);

    /// <summary>
    /// 从文件或目录向上查找仓库根目录。兼容普通仓库的 .git 目录和 worktree 的 .git 文件。
    /// </summary>
    public static string? FindRepositoryRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = new DirectoryInfo(fullPath);
            if (!directory.Exists)
            {
                directory = directory.Parent!;
            }

            while (directory is not null)
            {
                var gitMarker = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
                {
                    return directory.FullName;
                }

                directory = directory.Parent!;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException)
        {
            // 网络路径或已被删除的条目不应阻塞右键菜单；找不到仓库时隐藏 Git 入口即可。
        }

        return null;
    }

    /// <summary>检查 Git Extensions 是否已安装，不启动外部程序。</summary>
    public static bool TryFindGitExtensions(out string executable)
    {
        executable = GitExtensionsExecutable.Value ?? string.Empty;
        return executable.Length > 0;
    }

    private static string? LocateGitExtensions()
    {
        var candidates = new List<string>();
        AddProgramFilesCandidate(candidates, Environment.GetEnvironmentVariable("ProgramW6432"));
        AddProgramFilesCandidate(candidates, Environment.GetEnvironmentVariable("ProgramFiles"));
        AddProgramFilesCandidate(candidates, Environment.GetEnvironmentVariable("ProgramFiles(x86)"));

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(directory, "GitExtensions.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static void OpenRepository(string repositoryRoot) => Start(repositoryRoot, "openrepo", repositoryRoot);

    public static void OpenDifftool(string repositoryRoot, string path) => Start(repositoryRoot, "difftool", path);

    public static void OpenFileHistory(string repositoryRoot, string path) => Start(repositoryRoot, "filehistory", path);

    public static void OpenReset(string repositoryRoot) => Start(repositoryRoot, "reset");

    public static void OpenAddFiles(string repositoryRoot, string path) => Start(repositoryRoot, "addfiles", path);

    public static void OpenApplyPatch(string repositoryRoot, string path) => Start(repositoryRoot, "applypatch", path);

    public static void OpenSettings(string repositoryRoot) => Start(repositoryRoot, "settings");

    private static void Start(string repositoryRoot, params string[] arguments)
    {
        if (!TryFindGitExtensions(out var executable))
        {
            throw new FileNotFoundException("未检测到 Git Extensions，请先安装 Git Extensions。", "GitExtensions.exe");
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = repositoryRoot,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (Process.Start(start) is null)
        {
            throw new InvalidOperationException("无法启动 Git Extensions。");
        }
    }

    private static void AddProgramFilesCandidate(List<string> candidates, string? programFiles)
    {
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "GitExtensions", "GitExtensions.exe"));
        }
    }
}
