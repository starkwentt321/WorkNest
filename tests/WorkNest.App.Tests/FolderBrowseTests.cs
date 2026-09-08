using WorkNest.App.Tests.Fakes;
using WorkNest.App.ViewModels;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Domain;
using Xunit;

namespace WorkNest.App.Tests;

/// <summary>
/// 双击打开分支与浏览面板状态（MainViewModel.OpenCommand）：开关开启且为普通视图下的
/// 目录资源 → 计入使用并进入应用内浏览；文件类型、开关关闭、“全部工作区”范围回退系统启动；
/// 内嵌记账失败标灰并提示；切换工作区/移除资源联动关闭浏览面板。
/// fake 全部同步完成，OpenCommand.Execute 返回后即可断言。
/// </summary>
public class FolderBrowseTests
{
    private const int W1Id = 1;
    private const int DirId = 201;
    private const int FileId = 202;

    private sealed record Harness(
        MainViewModel Vm,
        FakeLauncherService Launcher,
        FakeSettingsService Settings,
        FakeFolderBrowserService Browser,
        FakeResourceService Resource);

    private static ResourceDto DirResource(string target = @"C:\work\资料夹") => new()
    {
        Id = DirId,
        Type = ResourceType.Directory,
        Name = "资料夹",
        Target = target,
        PathExists = true,
        WorkspaceIds = [W1Id],
    };

    private static ResourceDto FileResource() => new()
    {
        Id = FileId,
        Type = ResourceType.File,
        Name = "说明.txt",
        Target = @"C:\work\说明.txt",
        PathExists = true,
        WorkspaceIds = [W1Id],
    };

    private static async Task<Harness> CreateAsync(bool enabled, WorkspaceDto[]? workspaces = null, params ResourceDto[] resources)
    {
        var workspace = new FakeWorkspaceService();
        workspace.EnqueueOrder(workspaces ?? [new WorkspaceDto(W1Id, "工作区1", "#3399FF", null, 0)]);
        var resource = new FakeResourceService { ForWorkspace = resources };
        var launcher = new FakeLauncherService();
        var settings = new FakeSettingsService();
        var browser = new FakeFolderBrowserService();
        var vm = new MainViewModel(
            workspace,
            resource,
            launcher,
            settings,
            new FakeAutostartService(),
            new FakeBackupService(),
            new FakeIconProvider(),
            new FakeDialogs(),
            new FakeFilePicker(),
            browser);
        await vm.ReloadWorkspacesAsync(); // 回退到第一个有效工作区 W1，普通视图
        await vm.LoadResourcesAsync();    // 下拉重载不串联资源加载，需显式调起（由调用方串联）
        if (enabled)
        {
            settings.Values[SettingKeys.FolderInlineBrowse] = true;
            await vm.RefreshInlineBrowseSettingAsync();
        }
        return new Harness(vm, launcher, settings, browser, resource);
    }

    [Fact]
    public async Task Open_Directory_WhenEnabled_EntersBrowse_RecordsUsage_NoShellLaunch()
    {
        var harness = await CreateAsync(enabled: true, resources: [DirResource()]);
        var item = harness.Vm.Resources.Single(r => r.Id == DirId);

        harness.Vm.OpenCommand.Execute(item);

        Assert.NotNull(harness.Vm.FolderBrowser);
        Assert.True(harness.Vm.IsBrowsingFolder);
        Assert.Equal(DirId, harness.Vm.FolderBrowser!.ResourceId);
        Assert.Equal(DirId, Assert.Single(harness.Launcher.InlineOpenCalls)); // 浏览计入使用
        Assert.Equal(0, harness.Launcher.LaunchCallCount);                   // 不走系统启动
        Assert.Equal(@"C:\work\资料夹", Assert.Single(harness.Browser.ListCalls)); // 首屏枚举根目录
    }

    [Fact]
    public async Task Open_Directory_WhenDisabled_UsesShellLaunch()
    {
        var harness = await CreateAsync(enabled: false, resources: [DirResource()]);
        var item = harness.Vm.Resources.Single(r => r.Id == DirId);

        harness.Vm.OpenCommand.Execute(item);

        Assert.Null(harness.Vm.FolderBrowser);
        Assert.Empty(harness.Launcher.InlineOpenCalls);
        Assert.Equal(DirId, Assert.Single(harness.Launcher.LaunchedResourceIds));
    }

    [Fact]
    public async Task Open_File_WhenEnabled_UsesShellLaunch()
    {
        var harness = await CreateAsync(enabled: true, resources: [DirResource(), FileResource()]);
        var item = harness.Vm.Resources.Single(r => r.Id == FileId);

        harness.Vm.OpenCommand.Execute(item);

        Assert.Null(harness.Vm.FolderBrowser); // 仅目录类型进应用内浏览
        Assert.Equal(FileId, Assert.Single(harness.Launcher.LaunchedResourceIds));
    }

    [Fact]
    public async Task Open_Directory_InSearchAllScope_UsesShellLaunch()
    {
        var harness = await CreateAsync(enabled: true, resources: [DirResource()]);
        var item = harness.Vm.Resources.Single(r => r.Id == DirId);
        harness.Vm.SearchAll = true;

        harness.Vm.OpenCommand.Execute(item);

        // “全部工作区”归属不明确：保持系统启动旧行为
        Assert.Null(harness.Vm.FolderBrowser);
        Assert.Equal(DirId, Assert.Single(harness.Launcher.LaunchedResourceIds));
    }

    [Fact]
    public async Task Open_Directory_InlineFailure_MarksInvalid_AndShowsError()
    {
        var harness = await CreateAsync(enabled: true, resources: [DirResource()]);
        harness.Launcher.NextInlineOpenResult =
            LaunchResultDto.Fail(LaunchFailureKind.TargetMissing, "目录不存在或无法访问");
        var item = harness.Vm.Resources.Single(r => r.Id == DirId);

        harness.Vm.OpenCommand.Execute(item);

        Assert.True(item.IsInvalid); // 与系统启动失败同款标灰（F08）
        Assert.True(harness.Vm.HasError);
        Assert.Null(harness.Vm.FolderBrowser); // 失败不进浏览面板，也不再回退系统启动
        Assert.Equal(0, harness.Launcher.LaunchCallCount);
    }

    [Fact]
    public async Task SwitchWorkspace_ExitsBrowse()
    {
        var workspaces = new[]
        {
            new WorkspaceDto(W1Id, "工作区1", "#3399FF", null, 0),
            new WorkspaceDto(2, "工作区2", "#3399FF", null, 0),
        };
        var harness = await CreateAsync(enabled: true, workspaces, DirResource());
        harness.Vm.OpenCommand.Execute(harness.Vm.Resources.Single(r => r.Id == DirId));
        Assert.True(harness.Vm.IsBrowsingFolder);

        harness.Vm.CurrentWorkspaceOption = harness.Vm.WorkspaceOptions.First(o => o.Workspace?.Id == 2);

        Assert.Null(harness.Vm.FolderBrowser); // 切换工作区联动退出浏览态
    }

    [Fact]
    public async Task RemoveBrowsedFolder_ClosesBrowse()
    {
        var harness = await CreateAsync(enabled: true, resources: [DirResource()]);
        var item = harness.Vm.Resources.Single(r => r.Id == DirId);
        harness.Vm.OpenCommand.Execute(item);
        Assert.True(harness.Vm.IsBrowsingFolder);

        harness.Vm.RemoveFromWorkspaceCommand.Execute(item);

        Assert.Null(harness.Vm.FolderBrowser); // 浏览目标被移除时关闭面板
    }
}
