using System.Diagnostics;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Domain;

namespace WorkNest.Application.Services;

/// <summary>
/// 启动用例实现：统一从界面层接收启动请求，成功才记录使用统计（决策 31）。
/// 任何失败都以结果 DTO 返回，绝不抛出导致主程序退出（文档 4.1）。
/// </summary>
public sealed class LauncherService : ILauncherService
{
    private readonly IResourceRepository _resourceRepository;
    private readonly IProcessLauncher _launcher;
    private readonly IClock _clock;

    public LauncherService(IResourceRepository resourceRepository, IProcessLauncher launcher, IClock clock)
    {
        _resourceRepository = resourceRepository;
        _launcher = launcher;
        _clock = clock;
    }

    public async Task<LaunchResultDto> LaunchAsync(int workspaceId, int resourceId)
    {
        var item = await _resourceRepository.GetAsync(resourceId);
        if (item is null)
        {
            // 资源可能刚被删除或来自过期的列表数据；按不可用目标提示，不抛异常
            return LaunchResultDto.Fail(LaunchFailureKind.NotSupported, "资源不存在或已被删除");
        }

        // 耗时从发起启动计到系统接受请求，供使用记录诊断使用
        var stopwatch = Stopwatch.StartNew();
        var outcome = _launcher.Launch(item);
        stopwatch.Stop();

        if (!outcome.Success)
        {
            // 决策 31：只统计成功启动，失败不写使用记录，仅返回失败信息供窗口内提示
            var message = string.IsNullOrWhiteSpace(outcome.ErrorMessage)
                ? DefaultMessage(outcome.FailureKind)
                : outcome.ErrorMessage!;
            return LaunchResultDto.Fail(outcome.FailureKind, message);
        }

        try
        {
            // 文档 4.1.6：数据库写入与启动解耦，写入失败不得导致重复启动或崩溃，这里仅吞掉异常
            await _resourceRepository.RecordSuccessAsync(workspaceId, resourceId, _clock.UtcNow, (int)stopwatch.ElapsedMilliseconds);
        }
        catch (Exception)
        {
            // 有意吞掉：启动已完成，持久化失败只影响统计完整性
        }

        return LaunchResultDto.Ok();
    }

    public async Task<LaunchResultDto> RecordInlineOpenAsync(int workspaceId, int resourceId)
    {
        var item = await _resourceRepository.GetAsync(resourceId);
        if (item is null)
        {
            // 与 LaunchAsync 同契约：资源可能刚被删除或来自过期列表数据，按结果返回不抛异常
            return LaunchResultDto.Fail(LaunchFailureKind.NotSupported, "资源不存在或已被删除");
        }

        // 应用内浏览只针对可用目录；文件/程序/网站与失效路径一律回退系统启动语义
        if (item.Type != ResourceType.Directory || !Directory.Exists(item.Target))
        {
            return LaunchResultDto.Fail(LaunchFailureKind.TargetMissing, "目录不存在或无法访问");
        }

        try
        {
            // 浏览视为一次成功使用：与 Shell 启动共用记账（文档 4.1.6 同样解耦写库失败）
            await _resourceRepository.RecordSuccessAsync(workspaceId, resourceId, _clock.UtcNow, 0);
        }
        catch (Exception)
        {
            // 有意吞掉：浏览已可继续，持久化失败只影响统计完整性
        }

        return LaunchResultDto.Ok();
    }

    /// <summary>平台未给出错误信息时，按失败类别给出中文默认文案。</summary>
    private static string DefaultMessage(LaunchFailureKind kind) => kind switch
    {
        LaunchFailureKind.TargetMissing => "目标不存在或无法访问",
        LaunchFailureKind.NotSupported => "不支持该类型或协议",
        LaunchFailureKind.ShellError => "系统拒绝了启动请求",
        _ => "启动失败",
    };
}
