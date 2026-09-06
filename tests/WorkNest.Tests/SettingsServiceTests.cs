using WorkNest.Application.Abstractions;
using WorkNest.Application.Services;
using WorkNest.Tests.Fakes;
using Xunit;

namespace WorkNest.Tests;

/// <summary>设置用例：类型化读写经内存键值仓储，校验 JSON 序列化与回退语义。</summary>
public sealed class SettingsServiceTests
{
    private static (SettingsService Service, InMemorySettingsRepository Repo) CreateEnv()
    {
        var repo = new InMemorySettingsRepository();
        return (new SettingsService(repo), repo);
    }

    [Fact]
    public async Task Bool_RoundTrips()
    {
        var (service, repo) = CreateEnv();

        await service.SetAsync(SettingKeys.CloseToTray, true);
        var value = await service.GetAsync(SettingKeys.CloseToTray, false);

        Assert.True(value);
        Assert.Equal("true", repo.RawValue(SettingKeys.CloseToTray));
    }

    [Fact]
    public async Task Int_RoundTrips()
    {
        var (service, repo) = CreateEnv();

        await service.SetAsync(SettingKeys.LastWorkspaceId, 42);
        var value = await service.GetAsync(SettingKeys.LastWorkspaceId, 0);

        Assert.Equal(42, value);
        Assert.Equal("42", repo.RawValue(SettingKeys.LastWorkspaceId));
    }

    [Fact]
    public async Task String_StoresRawText_WithoutJsonQuotes()
    {
        var (service, repo) = CreateEnv();

        await service.SetAsync(SettingKeys.Hotkey, "Ctrl+Alt+W");
        var value = await service.GetAsync(SettingKeys.Hotkey, "默认键");

        Assert.Equal("Ctrl+Alt+W", value);
        // string 直存原文：不能被 JSON 引号包裹，否则其他读取方拿到带引号的值
        Assert.Equal("Ctrl+Alt+W", repo.RawValue(SettingKeys.Hotkey));
    }

    [Fact]
    public async Task MissingKey_ReturnsFallback()
    {
        var (service, _) = CreateEnv();

        Assert.False(await service.GetAsync("missing.flag", false));
        Assert.Equal(-1, await service.GetAsync("missing.number", -1));
        Assert.Equal("默认主题", await service.GetAsync("missing.theme", "默认主题"));
    }

    [Fact]
    public async Task CorruptedJson_ReturnsFallback()
    {
        var (service, repo) = CreateEnv();
        // 模拟历史版本/手改留下的损坏值
        await repo.SetAsync("broken.flag", "{oops");
        await repo.SetAsync("broken.number", "12x");

        // 损坏设置按默认处理，不崩溃
        Assert.True(await service.GetAsync("broken.flag", true));
        Assert.Equal(7, await service.GetAsync("broken.number", 7));
    }
}
