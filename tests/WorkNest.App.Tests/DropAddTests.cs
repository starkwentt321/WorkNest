using System.IO;
using WorkNest.App.Tests.Fakes;
using WorkNest.App.ViewModels;
using WorkNest.Application.Dtos;
using WorkNest.Application.Validation;
using WorkNest.Domain;
using Xunit;

namespace WorkNest.App.Tests;

/// <summary>
/// 外部文件/文件夹拖入工作区（批次 C）：类型判定与“新增文件/文件夹”入口一致、
/// 名称留空交给服务按目标提取、逐项容错并汇总失败、仅普通当前工作区视图接受拖放。
/// 判定基于真实文件系统（Directory.Exists/File.Exists），用临时目录构造样例。
/// </summary>
public class DropAddTests : IDisposable
{
    private const int W1Id = 1;

    private readonly string _root = Directory.CreateTempSubdirectory("worknest-drop-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private static async Task<(MainViewModel Vm, FakeResourceService Resource)> CreateOnWorkspace1Async()
    {
        var workspace = new FakeWorkspaceService();
        workspace.EnqueueOrder(new WorkspaceDto(W1Id, "工作区1", "#3399FF", null, 0));
        var resource = new FakeResourceService();
        var vm = new MainViewModel(
            workspace,
            resource,
            new FakeLauncherService(),
            new FakeSettingsService(),
            new FakeAutostartService(),
            new FakeBackupService(),
            new FakeIconProvider(),
            new FakeHotkeyService(),
            new FakeExportService(),
            new FakeImportService(),
            new FakeAppRestart(),
            new FakeDialogs(),
            new FakeFilePicker(),
            new FakeFolderBrowserService());
        await vm.ReloadWorkspacesAsync(); // 无 InitialWorkspace：回退到第一个有效工作区 W1
        Assert.Equal(W1Id, vm.CurrentWorkspace!.Id);
        return (vm, resource);
    }

    private string TouchFile(string fileName)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public async Task Drop_MixedItems_DecidesTypes_AndReloadsList()
    {
        var (vm, resource) = await CreateOnWorkspace1Async();
        var reloadsAfterInit = resource.ForWorkspaceCalls.Count;
        var dir = Path.Combine(_root, "资料夹");
        Directory.CreateDirectory(dir);
        var exe = TouchFile("工具.exe");
        var txt = TouchFile("说明.txt");

        await vm.AddDroppedPathsAsync([dir, exe, txt]);

        Assert.Equal(3, resource.AddCalls.Count);
        Assert.Equal(ResourceType.Directory, resource.AddCalls[0].Type);
        Assert.Equal(ResourceType.Program, resource.AddCalls[1].Type);
        Assert.Equal(ResourceType.File, resource.AddCalls[2].Type);
        Assert.All(resource.AddCalls, c => Assert.Equal(W1Id, c.WorkspaceId));
        // 名称留空由服务按目标自动提取（决策 61）
        Assert.All(resource.AddCalls, c => Assert.Equal(string.Empty, c.Name));
        Assert.Null(vm.ErrorMessage);
        // 新增后列表已按普通视图重载
        Assert.Equal(reloadsAfterInit + 1, resource.ForWorkspaceCalls.Count);
    }

    [Fact]
    public async Task Drop_WithDuplicateTarget_ContinuesOthers_AndReportsSummary()
    {
        var (vm, resource) = await CreateOnWorkspace1Async();
        var exe = TouchFile("工具.exe");
        var txt = TouchFile("说明.txt");
        resource.AddFailures[exe] = new ValidationException("该资源已存在于当前工作区");

        await vm.AddDroppedPathsAsync([exe, txt, exe]);

        // exe 两次都失败被跳过，txt 正常加入；失败汇总一次性上横幅
        var added = Assert.Single(resource.AddCalls);
        Assert.Equal(ResourceType.File, added.Type);
        Assert.Equal(txt, added.Target);
        Assert.NotNull(vm.ErrorMessage);
        Assert.StartsWith("已加入 1 项，2 项失败", vm.ErrorMessage);
        Assert.Contains("工具.exe：该资源已存在于当前工作区", vm.ErrorMessage);
    }

    [Fact]
    public async Task Drop_AllPathsInvalid_ReportsNothingAdded()
    {
        var (vm, resource) = await CreateOnWorkspace1Async();
        var ghost = Path.Combine(_root, "不存在.txt");

        await vm.AddDroppedPathsAsync([ghost]);

        Assert.Empty(resource.AddCalls);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("未添加任何资源", vm.ErrorMessage);
    }

    [Fact]
    public async Task Drop_NonPlainViews_NotAccepted()
    {
        var (vm, resource) = await CreateOnWorkspace1Async();
        var txt = TouchFile("说明.txt");

        // 普通当前工作区视图：接受
        Assert.True(vm.CanAcceptExternalDrop);

        // “全部工作区”范围：归属不明确，拒绝
        vm.SearchAll = true;
        Assert.False(vm.CanAcceptExternalDrop);
        await vm.AddDroppedPathsAsync([txt]);
        Assert.Empty(resource.AddCalls);

        // 最近使用视图：新增项不会出现在最近列表，为避免“拖了没反应”的误解直接拒绝
        vm.SearchAll = false;
        vm.CurrentWorkspaceOption = vm.WorkspaceOptions.First(o => o.IsRecentItem);
        Assert.True(vm.IsRecentView);
        Assert.False(vm.CanAcceptExternalDrop);
        await vm.AddDroppedPathsAsync([txt]);
        Assert.Empty(resource.AddCalls);
    }
}
