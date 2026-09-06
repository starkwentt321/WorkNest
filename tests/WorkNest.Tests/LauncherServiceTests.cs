using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Services;
using WorkNest.Domain;
using WorkNest.Tests.Fakes;
using Xunit;

namespace WorkNest.Tests;

/// <summary>启动用例：成功才写使用记录；一切失败以 DTO 返回，不抛异常。</summary>
public sealed class LauncherServiceTests
{
    private sealed record Env(
        LauncherService Service,
        InMemoryResourceRepository Resources,
        InMemoryWorkspaceRepository Workspaces,
        FakeLauncher Launcher,
        FakeClock Clock,
        int WorkspaceId,
        int ResourceId);

    private static async Task<Env> CreateEnvAsync()
    {
        var workspaces = new InMemoryWorkspaceRepository();
        var resources = new InMemoryResourceRepository(workspaces);
        var launcher = new FakeLauncher();
        var clock = new FakeClock();
        var service = new LauncherService(resources, launcher, clock);

        var workspaceId = await workspaces.AddAsync(new Workspace { Name = "工作区" });
        resources.SeedResource(new ResourceItem
        {
            Type = ResourceType.File,
            Name = "示例文档",
            Target = @"C:\docs\demo.txt",
        }, workspaceId);

        return new Env(service, resources, workspaces, launcher, clock, workspaceId, 1);
    }

    private static WorkspaceResource Link(Env env) => env.Resources.FindLink(env.WorkspaceId, env.ResourceId)!;

    [Fact]
    public async Task Success_LaunchesOnce_AndRecordsUsage()
    {
        var env = await CreateEnvAsync();

        var result = await env.Service.LaunchAsync(env.WorkspaceId, env.ResourceId);

        Assert.Equal(LaunchResultDto.Ok(), result);
        Assert.Equal(1, env.Launcher.LaunchCount);
        Assert.Equal([env.ResourceId], env.Launcher.LaunchedResourceIds.ToArray());

        // 文档 7.2：成功启动在同一事务更新链接统计与工作区 LastUsedAt
        var link = Link(env);
        Assert.Equal(1, link.RunCount);
        Assert.Equal(env.Clock.UtcNow, link.LastUsedAt);
        Assert.Equal(env.Clock.UtcNow, (await env.Workspaces.GetAsync(env.WorkspaceId))!.LastUsedAt);
        var record = Assert.Single(env.Resources.UsageRecords);
        Assert.Equal(env.WorkspaceId, record.WorkspaceId);
        Assert.Equal(1, record.Result);
        Assert.True(record.DurationMs >= 0);
    }

    [Fact]
    public async Task Failure_RecordsNothing_AndCarriesMessage()
    {
        var env = await CreateEnvAsync();
        env.Launcher.NextOutcome = LaunchOutcome.Fail(LaunchFailureKind.ShellError, "shell 拒绝");

        var result = await env.Service.LaunchAsync(env.WorkspaceId, env.ResourceId);

        Assert.False(result.Success);
        Assert.Equal(LaunchFailureKind.ShellError, result.FailureKind);
        Assert.Equal("shell 拒绝", result.ErrorMessage);
        Assert.Equal(1, env.Launcher.LaunchCount);

        // 决策 31：只统计成功启动，失败不写使用记录
        Assert.Empty(env.Resources.UsageRecords);
        Assert.Equal(0, Link(env).RunCount);
        Assert.Null(Link(env).LastUsedAt);
    }

    [Fact]
    public async Task Failure_WithoutMessage_UsesKindDefaultText()
    {
        var env = await CreateEnvAsync();
        // ErrorMessage 为空白时服务端按类别补默认文案（契约上 message 非空，故传空串）
        env.Launcher.NextOutcome = LaunchOutcome.Fail(LaunchFailureKind.TargetMissing, string.Empty);

        var result = await env.Service.LaunchAsync(env.WorkspaceId, env.ResourceId);

        Assert.False(result.Success);
        Assert.Equal(LaunchFailureKind.TargetMissing, result.FailureKind);
        // 平台未给错误信息时按类别给中文默认文案
        Assert.Equal("目标不存在或无法访问", result.ErrorMessage);
    }

    [Fact]
    public async Task MissingResource_FailsWithoutThrowing()
    {
        var env = await CreateEnvAsync();

        var result = await env.Service.LaunchAsync(env.WorkspaceId, resourceId: 999);

        Assert.False(result.Success);
        Assert.Equal(LaunchFailureKind.NotSupported, result.FailureKind);
        Assert.Equal("资源不存在或已被删除", result.ErrorMessage);
        Assert.Equal(0, env.Launcher.LaunchCount);
        Assert.Empty(env.Resources.UsageRecords);
    }

    [Fact]
    public async Task UsageWriteFailure_IsSwallowed_AndLaunchStillSucceeds()
    {
        var env = await CreateEnvAsync();
        env.Resources.ThrowOnRecordSuccess = true;

        // 文档 4.1.6：数据库写入失败不得导致重复启动或崩溃
        var result = await env.Service.LaunchAsync(env.WorkspaceId, env.ResourceId);

        Assert.Equal(LaunchResultDto.Ok(), result);
        Assert.Equal(1, env.Launcher.LaunchCount);
        Assert.Empty(env.Resources.UsageRecords);
    }

    // ============ 应用内浏览记账（RecordInlineOpenAsync） ============

    /// <summary>在环境里追加一个目录类型资源（SeedResource 自动分配 Id=2）。</summary>
    private static Env SeedDirectory(Env env)
    {
        env.Resources.SeedResource(new ResourceItem
        {
            Type = ResourceType.Directory,
            Name = "资料夹",
            Target = Path.GetTempPath(), // 真实存在的目录，覆盖可用性校验的通过路径
        }, env.WorkspaceId);
        return env;
    }

    [Fact]
    public async Task RecordInlineOpen_Directory_RecordsUsage_WithoutShellLaunch()
    {
        var env = SeedDirectory(await CreateEnvAsync());

        var result = await env.Service.RecordInlineOpenAsync(env.WorkspaceId, resourceId: 2);

        Assert.Equal(LaunchResultDto.Ok(), result);
        Assert.Equal(0, env.Launcher.LaunchCount); // 浏览不触发外部启动
        var record = Assert.Single(env.Resources.UsageRecords);
        Assert.Equal(env.WorkspaceId, record.WorkspaceId);
        Assert.Equal(env.Clock.UtcNow, env.Resources.FindLink(env.WorkspaceId, 2)!.LastUsedAt);
    }

    [Fact]
    public async Task RecordInlineOpen_MissingDirectory_FailsWithoutRecord()
    {
        var env = SeedDirectory(await CreateEnvAsync());
        env.Resources.SeedResource(new ResourceItem
        {
            Type = ResourceType.Directory,
            Name = "失效目录",
            Target = @"C:\surely\missing\dir",
        }, env.WorkspaceId);

        var result = await env.Service.RecordInlineOpenAsync(env.WorkspaceId, resourceId: 3);

        Assert.False(result.Success);
        Assert.Equal(LaunchFailureKind.TargetMissing, result.FailureKind);
        Assert.Empty(env.Resources.UsageRecords); // 失效目标不写使用记录
    }

    [Fact]
    public async Task RecordInlineOpen_NonDirectoryResource_Fails()
    {
        var env = await CreateEnvAsync(); // 种子资源为 File 类型（Id=1）

        var result = await env.Service.RecordInlineOpenAsync(env.WorkspaceId, env.ResourceId);

        Assert.False(result.Success);
        Assert.Equal(LaunchFailureKind.TargetMissing, result.FailureKind);
        Assert.Equal(0, env.Launcher.LaunchCount);
        Assert.Empty(env.Resources.UsageRecords);
    }

    [Fact]
    public async Task RecordInlineOpen_RecordWriteFailure_IsSwallowed()
    {
        var env = SeedDirectory(await CreateEnvAsync());
        env.Resources.ThrowOnRecordSuccess = true;

        // 浏览已可继续：记账失败只影响统计，不影响结果（与 LaunchAsync 契约一致）
        var result = await env.Service.RecordInlineOpenAsync(env.WorkspaceId, resourceId: 2);

        Assert.Equal(LaunchResultDto.Ok(), result);
        Assert.Empty(env.Resources.UsageRecords);
    }
}
