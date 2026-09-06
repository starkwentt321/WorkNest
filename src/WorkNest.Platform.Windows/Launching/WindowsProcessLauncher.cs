using System.ComponentModel;
using System.Diagnostics;
using System.IO; // 注意：WindowsDesktop SDK 的隐式 using 不含 System.IO，必须显式引入
using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Domain;

namespace WorkNest.Platform.Windows.Launching;

/// <summary>
/// Windows Shell 启动器（设计 8.1）：目录/文件/网站统一走系统 Shell 打开；
/// 程序的可执行路径、参数、工作目录通过 ProcessStartInfo 独立属性传递，禁止字符串拼接。
/// 任何异常都转为失败结果返回，绝不允许向上抛出导致主程序退出（契约约束）。
/// </summary>
public sealed class WindowsProcessLauncher : IProcessLauncher
{
    public LaunchOutcome Launch(ResourceItem item)
    {
        try
        {
            return item.Type switch
            {
                ResourceType.Directory => LaunchDirectory(item.Target),
                ResourceType.File => LaunchFile(item.Target),
                ResourceType.Program => LaunchProgram(item),
                ResourceType.Website => LaunchWebsite(item.Target),
                _ => LaunchOutcome.Fail(LaunchFailureKind.NotSupported, $"暂不支持的资源类型：{item.Type}"),
            };
        }
        catch (Exception ex)
        {
            // 兜底：策略方法内部已捕获 Shell 异常，这里防御未来改动漏网的意外异常
            return LaunchOutcome.Fail(LaunchFailureKind.ShellError, $"启动失败：{ex.Message}");
        }
    }

    private static LaunchOutcome LaunchDirectory(string target)
    {
        if (!Directory.Exists(target))
        {
            return LaunchOutcome.Fail(LaunchFailureKind.TargetMissing, "目录不存在或无法访问");
        }

        // 交由系统 Shell（资源管理器）打开
        return StartShell(new ProcessStartInfo { FileName = target, UseShellExecute = true });
    }

    private static LaunchOutcome LaunchFile(string target)
    {
        if (!File.Exists(target))
        {
            return LaunchOutcome.Fail(LaunchFailureKind.TargetMissing, "文件不存在或无法访问");
        }

        return StartShell(new ProcessStartInfo { FileName = target, UseShellExecute = true });
    }

    private static LaunchOutcome LaunchProgram(ResourceItem item)
    {
        if (!File.Exists(item.Target))
        {
            return LaunchOutcome.Fail(LaunchFailureKind.TargetMissing, "程序不存在或无法访问");
        }

        // 工作目录失效不阻止启动：允许保存时目录暂缺（界面已按设计标记状态），启动时回退系统默认目录
        var workingDirectory = item.WorkingDirectory is { Length: > 0 } workingDir && Directory.Exists(workingDir)
            ? workingDir
            : string.Empty;

        // 可执行路径与参数是独立属性，由 Shell 负责转义，避免拼接带来的注入风险（设计 8.1）
        return StartShell(new ProcessStartInfo
        {
            FileName = item.Target,
            Arguments = item.Arguments ?? string.Empty,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        });
    }

    private static LaunchOutcome LaunchWebsite(string target)
    {
        // URL 协议白名单：只放行 http/https；未知协议必须先提示用户，不得静默交给 Shell（设计 8.1）
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return LaunchOutcome.Fail(LaunchFailureKind.NotSupported, "暂不支持的网址协议");
        }

        return StartShell(new ProcessStartInfo { FileName = target, UseShellExecute = true });
    }

    private static LaunchOutcome StartShell(ProcessStartInfo startInfo)
    {
        try
        {
            // UseShellExecute=true 时，ShellExecuteEx 失败会抛 Win32Exception（见下方 catch）；
            // 返回 null 不是失败：Shell 已受理请求并把目标交给既有进程打开（如文件夹交给
            // 已运行的资源管理器），此时拿不到新进程句柄，按成功处理。
            using var process = Process.Start(startInfo);
            return LaunchOutcome.Ok();
        }
        catch (Win32Exception ex)
        {
            // Win32 错误码：2=系统找不到文件 3=系统找不到指定路径 267=目录名称无效 206=文件名或扩展名太长
            var kind = ex.NativeErrorCode is 2 or 3 or 267 or 206
                ? LaunchFailureKind.TargetMissing
                : LaunchFailureKind.ShellError;
            return LaunchOutcome.Fail(
                kind,
                $"系统 Shell 启动失败（Win32 错误码 {ex.NativeErrorCode}）：{ex.Message}",
                ex.NativeErrorCode.ToString());
        }
        catch (Exception ex)
        {
            return LaunchOutcome.Fail(LaunchFailureKind.ShellError, $"系统 Shell 启动失败：{ex.Message}");
        }
    }
}
