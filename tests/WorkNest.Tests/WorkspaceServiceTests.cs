using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Services;
using WorkNest.Application.Validation;
using WorkNest.Domain;
using WorkNest.Tests.Fakes;
using Xunit;


namespace WorkNest.Tests;

/// <summary>工作区用例：经内存假仓储与假设置仓储驱动，不触碰数据库。</summary>
public sealed class WorkspaceServiceTests
{
    private sealed record Env(
        WorkspaceService Service,
        InMemoryWorkspaceRepository Workspaces,
        InMemorySettingsRepository Settings,
        FakeClock Clock);

    private static Env CreateEnv()
    {
        var workspaces = new InMemoryWorkspaceRepository();
        var settings = new InMemorySettingsRepository();
        return new Env(
            new WorkspaceService(workspaces, settings, new FakeClock()),
            workspaces,
            settings,
            new FakeClock());
    }

    [Fact]
    public async Task CreateAsync_AssignsPaletteColorAndSortOrder()
    {
        var env = CreateEnv();

        var first = await env.Service.CreateAsync("开发");
        var second = await env.Service.CreateAsync("文档");

        // 调色板顺序取色；手动排序值依次递增（现有最大 +1，首个为 1）
        Assert.Equal("#0078D4", first.Color);
        Assert.Equal("#00B294", second.Color);
        Assert.Equal(0, first.ResourceCount);
        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        // SortOrder 不在 DTO 上，经仓储断言
        Assert.Equal(1, (await env.Workspaces.GetAsync(first.Id))!.SortOrder);
        Assert.Equal(2, (await env.Workspaces.GetAsync(second.Id))!.SortOrder);
    }

    [Fact]
    public async Task CreateAsync_WrapsPaletteAfterTenWorkspaces()
    {
        var env = CreateEnv();

        WorkspaceDto last = null!;
        for (var i = 0; i < 11; i++)
        {
            last = await env.Service.CreateAsync($"工作区{i}");
        }

        // 第 11 个工作区取模回到调色板第 1 色
        Assert.Equal("#0078D4", last.Color);
    }

    [Fact]
    public async Task CreateAsync_DefaultSortModeIsLastUsed()
    {
        var env = CreateEnv();

        // 构造后未调用 GetOrderedAsync 前，缓存值默认 LastUsed
        Assert.Equal(WorkspaceSortMode.LastUsed, env.Service.CurrentSortMode);
        Assert.Null(env.Settings.RawValue(SettingKeys.WorkspaceSortMode));

        await env.Service.CreateAsync("开发");
        Assert.Single(await env.Service.GetOrderedAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_EmptyName_Throws(string name)
    {
        var env = CreateEnv();

        var ex = await Assert.ThrowsAsync<ValidationException>(() => env.Service.CreateAsync(name));

        Assert.Equal("工作区名称不能为空", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_DuplicateNameIgnoringCase_Throws()
    {
        var env = CreateEnv();
        await env.Service.CreateAsync("Work");

        var ex = await Assert.ThrowsAsync<ValidationException>(() => env.Service.CreateAsync("  work "));

        // 名称唯一性按 OrdinalIgnoreCase 且 trim 后比较
        Assert.Equal("已存在同名工作区", ex.Message);
    }

    [Fact]
    public async Task RenameAsync_ValidatesDuplicateButAllowsSelf()
    {
        var env = CreateEnv();
        var a = await env.Service.CreateAsync("Alpha");
        await env.Service.CreateAsync("Beta");

        // 与他人重名 → 拒绝
        var duplicate = await Assert.ThrowsAsync<ValidationException>(
            () => env.Service.RenameAsync(a.Id, "beta"));
        Assert.Equal("已存在同名工作区", duplicate.Message);

        // 仅改变自身大小写 → 允许（校验排除自身）
        await env.Service.RenameAsync(a.Id, "ALPHA");
        var all = await env.Service.GetOrderedAsync();
        Assert.Equal("ALPHA", all.Single(w => w.Id == a.Id).Name);
    }

    [Fact]
    public async Task DeleteAsync_RemovesWorkspace()
    {
        var env = CreateEnv();
        var created = await env.Service.CreateAsync("临时");

        await env.Service.DeleteAsync(created.Id);

        Assert.Empty(await env.Service.GetOrderedAsync());
        Assert.Null(await env.Workspaces.GetAsync(created.Id));
    }

    [Fact]
    public async Task SetManualOrderAsync_PersistsOrderAndSwitchesMode()
    {
        var env = CreateEnv();
        await env.Service.CreateAsync("A");
        await env.Service.CreateAsync("B");
        await env.Service.CreateAsync("C");

        await env.Service.SetManualOrderAsync([3, 1, 2]);

        Assert.Equal("Manual", env.Settings.RawValue(SettingKeys.WorkspaceSortMode));

        var ordered = await env.Service.GetOrderedAsync();
        Assert.Equal([3, 1, 2], ordered.Select(w => w.Id).ToArray());
        Assert.Equal(WorkspaceSortMode.Manual, env.Service.CurrentSortMode);

        // 顺序值按列表下标持久化
        Assert.Equal(0, (await env.Workspaces.GetAsync(3))!.SortOrder);
        Assert.Equal(1, (await env.Workspaces.GetAsync(1))!.SortOrder);
        Assert.Equal(2, (await env.Workspaces.GetAsync(2))!.SortOrder);
    }

    [Fact]
    public async Task UseLastUsedOrderAsync_SwitchesBackToLastUsed()
    {
        var env = CreateEnv();
        await env.Service.SetManualOrderAsync([]);

        await env.Service.UseLastUsedOrderAsync();

        Assert.Equal("LastUsed", env.Settings.RawValue(SettingKeys.WorkspaceSortMode));
        await env.Service.GetOrderedAsync();
        Assert.Equal(WorkspaceSortMode.LastUsed, env.Service.CurrentSortMode);
    }

    [Fact]
    public async Task LastUsedMode_OrdersByLastUsedDescending_NullLast()
    {
        var env = CreateEnv();
        var a = await env.Service.CreateAsync("A");
        var b = await env.Service.CreateAsync("B");
        var c = await env.Service.CreateAsync("C");

        // 成功启动才更新 LastUsedAt：A 早于 B，C 从未启动（null）；
        // A 先被触碰，让期望顺序 [B,A,C] 有别于创建顺序 [A,B,C]，才有区分度
        env.Clock.Advance(TimeSpan.FromMinutes(10));
        await env.Workspaces.TouchLastUsedAsync(a.Id, env.Clock.UtcNow);
        env.Clock.Advance(TimeSpan.FromMinutes(10));
        await env.Workspaces.TouchLastUsedAsync(b.Id, env.Clock.UtcNow);

        var ordered = await env.Service.GetOrderedAsync();

        // 最近使用倒序；null（C）视为最小排最后，即便其创建时间不受影响
        Assert.Equal([b.Id, a.Id, c.Id], ordered.Select(w => w.Id).ToArray());
    }
}
