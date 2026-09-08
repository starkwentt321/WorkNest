using WorkNest.App.Tests.Fakes;
using WorkNest.App.ViewModels;
using WorkNest.Application.Dtos;
using WorkNest.Domain;
using Xunit;

namespace WorkNest.App.Tests;

/// <summary>
/// MainViewModel 行为测试（批次 B：R04 未保存确认保面板 / R06 最近使用顺序 / R07 工作区回退）。
/// 通过手写 fake 注入全部依赖；fake 均返回已完成 Task，VM 的 fire-and-forget 加载
/// 在测试线程内同步完成，断言时机确定。不调用 InitializeAsync（内部涉及 ThemeManager 静态主题）。
/// </summary>
public class MainViewModelTests
{
    private const int W1Id = 1;
    private const int AId = 101; // 置顶 + 高频资源：普通默认排序应排最前
    private const int BId = 102; // 普通资源但 LastUsedAt 更新：最近使用视图应排最前

    private sealed record VmHarness(
        MainViewModel Vm,
        FakeWorkspaceService Workspace,
        FakeResourceService Resource,
        FakeDialogs Dialogs,
        FakeFilePicker Picker,
        FakeLauncherService Launcher);

    private static VmHarness CreateVm() => CreateVmCore([]);

    private static VmHarness CreateVmCore(params WorkspaceDto[] initialOrder) =>
        BuildVm(250, initialOrder); // 250 = MainViewModel 生产默认防抖

    /// <summary>防抖 0 重载：SearchText 变化同步重建视图（测试线程无消息泵路径）。</summary>
    private static VmHarness CreateVmCore(int searchDebounceMilliseconds, params WorkspaceDto[] initialOrder) =>
        BuildVm(searchDebounceMilliseconds, initialOrder);

    private static VmHarness BuildVm(int searchDebounceMilliseconds, WorkspaceDto[] initialOrder)
    {
        var workspace = new FakeWorkspaceService();
        if (initialOrder.Length > 0)
        {
            workspace.EnqueueOrder(initialOrder);
        }
        var resource = new FakeResourceService();
        var launcher = new FakeLauncherService();
        var dialogs = new FakeDialogs();
        var picker = new FakeFilePicker();
        var vm = new MainViewModel(
            workspace,
            resource,
            launcher,
            new FakeSettingsService(),
            new FakeAutostartService(),
            new FakeBackupService(),
            new FakeIconProvider(),
            dialogs,
            picker,
            new FakeFolderBrowserService(),
            searchDebounceMilliseconds: searchDebounceMilliseconds);
        return new VmHarness(vm, workspace, resource, dialogs, picker, launcher);
    }

    private static WorkspaceDto Workspace(int id, string name) =>
        new(id, name, "#3399FF", null, 0);

    private static ResourceDto Resource(int id, string name, ResourceType type, string target,
        bool pinned = false, int runCount = 0, DateTime? lastUsedAt = null) => new()
    {
        Id = id,
        Type = type,
        Name = name,
        Target = target,
        IsPinned = pinned,
        RunCount = runCount,
        LastUsedAt = lastUsedAt,
        PathExists = true,
        WorkspaceIds = [W1Id],
    };

    /// <summary>服务端契约顺序为 [B, A]：B 最近用过但普通/低频，A 置顶且高频。</summary>
    private static ResourceDto B() => Resource(BId, "B文档", ResourceType.File, @"C:\docs\b.txt",
        pinned: false, runCount: 1, lastUsedAt: new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc));

    private static ResourceDto A() => Resource(AId, "A程序", ResourceType.Program, @"C:\tools\a.exe",
        pinned: true, runCount: 10, lastUsedAt: new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));

    /// <summary>防御性等待：fake 全部同步完成时首次检查即通过，不会真正等待。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("等待 VM 异步加载完成超时");
            }
            await Task.Delay(10);
        }
    }

    /// <summary>构造选中 W1 的 VM（CurrentWorkspace 非 null，新增入口可用）。</summary>
    private static async Task<VmHarness> CreateVmOnWorkspace1Async()
    {
        var h = CreateVmCore(Workspace(W1Id, "工作区1"));
        await h.Vm.ReloadWorkspacesAsync(); // 无 InitialWorkspace：回退到第一个有效工作区 W1
        Assert.Equal(W1Id, h.Vm.CurrentWorkspace!.Id);
        return h;
    }

    /// <summary>构造“存在未保存修改”的编辑面板并挂到 VM 上。</summary>
    private static ResourceEditViewModel AttachDirtyPanel(MainViewModel vm)
    {
        var panel = ResourceEditViewModel.New(ResourceType.File, @"C:\old\note.txt", W1Id);
        vm.EditPanel = panel;
        panel.Name = "用户改过的名字"; // 字段 setter 置 IsDirty（决策 72）
        Assert.True(panel.IsDirty);
        return panel;
    }

    // ============ R04：新增入口遇未保存修改，取消确认必须保留旧面板 ============

    [Fact]
    public async Task AddWebsite_DirtyPanel_CancelConfirm_KeepsPanel_Confirm_ReplacesWithNewWebsitePanel()
    {
        var h = await CreateVmOnWorkspace1Async();
        var oldPanel = AttachDirtyPanel(h.Vm);

        // 用户取消放弃：面板引用不变，不进入新增流程
        h.Dialogs.ConfirmResult = false;
        h.Vm.AddWebsiteCommand.Execute(null);
        Assert.Same(oldPanel, h.Vm.EditPanel);
        var confirm = Assert.Single(h.Dialogs.ConfirmCalls);
        Assert.Equal("未保存修改", confirm.Title);

        // 用户确认放弃：面板被替换为新增网站面板
        h.Dialogs.ConfirmResult = true;
        h.Vm.AddWebsiteCommand.Execute(null);
        var newPanel = h.Vm.EditPanel;
        Assert.NotNull(newPanel);
        Assert.NotSame(oldPanel, newPanel);
        Assert.True(newPanel!.IsNew);
        Assert.Equal(ResourceType.Website, newPanel.Type);
        Assert.Equal(W1Id, newPanel.EditingWorkspaceId);
    }

    [Fact]
    public async Task AddFile_DirtyPanel_CancelConfirm_KeepsPanel_PickerCancel_KeepsPanel_PickerPaths_DecideType()
    {
        var h = await CreateVmOnWorkspace1Async();
        var oldPanel = AttachDirtyPanel(h.Vm);

        // 未保存确认被取消：直接返回，不弹出文件选择器，面板不变
        h.Dialogs.ConfirmResult = false;
        h.Vm.AddFileCommand.Execute(null);
        Assert.Same(oldPanel, h.Vm.EditPanel);
        Assert.Single(h.Dialogs.ConfirmCalls);
        Assert.Empty(h.Picker.PickFileCalls);

        // 确认放弃但用户在文件对话框取消（返回 null）：面板保持不变
        h.Dialogs.ConfirmResult = true;
        h.Picker.FileResult = null;
        h.Vm.AddFileCommand.Execute(null);
        Assert.Same(oldPanel, h.Vm.EditPanel);
        var pick = Assert.Single(h.Picker.PickFileCalls);
        Assert.Equal("选择文件", pick);

        // 选中 .exe：新增面板类型为程序，目标为所选路径
        h.Picker.FileResult = @"C:\tools\app.exe";
        h.Vm.AddFileCommand.Execute(null);
        var exePanel = h.Vm.EditPanel;
        Assert.NotNull(exePanel);
        Assert.NotSame(oldPanel, exePanel);
        Assert.True(exePanel!.IsNew);
        Assert.Equal(ResourceType.Program, exePanel.Type);
        Assert.Equal(@"C:\tools\app.exe", exePanel.Target);

        // 选中 .txt：新增面板类型为普通文件
        h.Picker.FileResult = @"C:\docs\readme.txt";
        h.Vm.AddFileCommand.Execute(null);
        var txtPanel = h.Vm.EditPanel;
        Assert.NotNull(txtPanel);
        Assert.Equal(ResourceType.File, txtPanel!.Type);
        Assert.Equal(@"C:\docs\readme.txt", txtPanel.Target);
    }

    // ============ R06：最近使用视图未点列头时保持服务返回顺序 ============

    [Fact]
    public async Task RecentView_KeepsServiceOrder_WhileNormalViewAppliesDefaultSort()
    {
        var h = CreateVm();
        // 同一份数据两种视图共用：服务端顺序 [B, A]
        h.Resource.ForWorkspace = [B(), A()];
        h.Resource.RecentlyUsed = [B(), A()];

        // 普通工作区视图：默认排序按置顶/频次重排 → A（置顶）在前（对照组）
        h.Vm.CurrentWorkspaceOption = new WorkspaceOptionViewModel(Workspace(W1Id, "工作区1"));
        // 批次 B 性能改造后加载在后台线程执行：初始 Resources 为空，非空即本次加载已落地
        await WaitUntilAsync(() => h.Vm.Resources.Count > 0);
        Assert.Equal([AId, BId], h.Vm.Resources.Select(r => r.Id));
        Assert.Equal([W1Id], h.Resource.ForWorkspaceCalls);

        // 进入最近使用视图：必须保持 GetRecentlyUsedAsync 的契约顺序 [B, A]，不被默认排序重排
        h.Vm.CurrentWorkspaceOption = WorkspaceOptionViewModel.RecentItem;
        Assert.True(h.Vm.IsRecentView);
        // 等待最近视图加载落地（后台执行；服务端契约顺序 B 最前，首项变为 B 即完成）
        await WaitUntilAsync(() => h.Vm.Resources.FirstOrDefault()?.Id == BId);
        Assert.Equal([(W1Id, 20)], h.Resource.RecentlyUsedCalls);
        Assert.Equal([BId, AId], h.Vm.Resources.Select(r => r.Id));
    }

    // ============ R07：ReloadWorkspacesAsync 的选中回退 ============

    [Fact]
    public async Task ReloadWorkspaces_CurrentWorkspaceDeleted_FallsBackToFirstRemaining()
    {
        var h = CreateVmCore(Workspace(1, "工作区1"), Workspace(2, "工作区2"));
        await h.Vm.ReloadWorkspacesAsync();
        Assert.Equal(1, h.Vm.CurrentWorkspace!.Id);

        // 选中 W2
        h.Vm.CurrentWorkspaceOption = h.Vm.WorkspaceOptions.First(o => o.Workspace?.Id == 2);
        Assert.Equal(2, h.Vm.CurrentWorkspace!.Id);

        // W2 被删除后刷新：回退到剩余的第一个有效工作区 W1
        h.Workspace.EnqueueOrder(Workspace(1, "工作区1"));
        await h.Vm.ReloadWorkspacesAsync();
        Assert.Equal(1, h.Vm.CurrentWorkspace!.Id);
        Assert.Same(
            h.Vm.WorkspaceOptions.First(o => o.Workspace?.Id == 1),
            h.Vm.CurrentWorkspaceOption);
    }

    [Fact]
    public async Task ReloadWorkspaces_AllWorkspacesDeleted_ClearsSelectionAndDisablesAddCommands()
    {
        var h = CreateVmCore(Workspace(1, "工作区1"), Workspace(2, "工作区2"));
        await h.Vm.ReloadWorkspacesAsync();
        h.Vm.CurrentWorkspaceOption = h.Vm.WorkspaceOptions.First(o => o.Workspace?.Id == 2);
        Assert.Equal(2, h.Vm.CurrentWorkspace!.Id);

        // 全部工作区被删除后刷新：无工作区可选
        h.Workspace.EnqueueOrder([]);
        await h.Vm.ReloadWorkspacesAsync();
        Assert.Null(h.Vm.CurrentWorkspace);
        Assert.DoesNotContain(h.Vm.WorkspaceOptions, o => !o.IsSpecialItem);
        Assert.False(h.Vm.AddWebsiteCommand.CanExecute(null));
        Assert.False(h.Vm.AddFileCommand.CanExecute(null));
        Assert.False(h.Vm.AddFolderCommand.CanExecute(null));
    }

    // ============ 搜索防抖 0：SearchText 变化同步重建视图（测试环境无消息泵路径） ============

    [Fact]
    public async Task SearchText_ZeroDebounce_FiltersResourcesSynchronously()
    {
        var h = CreateVmCore(searchDebounceMilliseconds: 0);
        h.Resource.ForWorkspace = [B(), A()];
        h.Vm.CurrentWorkspaceOption = new WorkspaceOptionViewModel(Workspace(W1Id, "工作区1"));
        await WaitUntilAsync(() => h.Vm.Resources.Count > 0);
        Assert.Equal([AId, BId], h.Vm.Resources.Select(r => r.Id));

        // 不匹配关键词：0 防抖下输入即过滤，无需等待计时器
        h.Vm.SearchText = "zzz不匹配zzz";
        Assert.Empty(h.Vm.Resources);

        // 匹配关键词：按名称命中保留，过滤结果立即可见
        h.Vm.SearchText = "文档";
        var visible = Assert.Single(h.Vm.Resources);
        Assert.Equal(BId, visible.Id);
    }

    // ============ 启动成功：普通视图局部重排，与全量重载同序且不触发重载 ============

    [Fact]
    public async Task LaunchSuccess_NormalView_ReranksByDefaultSort_WithoutFullReload()
    {
        var h = CreateVm();
        // 普通组内 RunCount 倒序：高频(6)在前，低频(5)在后
        var lowRun = Resource(301, "低频", ResourceType.File, @"C:\docs\low.txt",
            runCount: 5, lastUsedAt: new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));
        var highRun = Resource(302, "高频", ResourceType.File, @"C:\docs\high.txt",
            runCount: 6, lastUsedAt: new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc));
        h.Resource.ForWorkspace = [highRun, lowRun];
        h.Vm.CurrentWorkspaceOption = new WorkspaceOptionViewModel(Workspace(W1Id, "工作区1"));
        await WaitUntilAsync(() => h.Vm.Resources.Count > 0);
        Assert.Equal([302, 301], h.Vm.Resources.Select(r => r.Id));

        // 启动低频项：RunCount 追平 6，LastUsedAt 刷新为最新 → 默认排序应上移到普通组首位
        var reloadCallsBefore = h.Resource.ForWorkspaceCalls.Count;
        var launched = h.Vm.Resources.First(r => r.Id == 301);
        h.Vm.LaunchCommand.Execute(launched);
        await WaitUntilAsync(() => h.Vm.Resources.FirstOrDefault()?.Id == 301);

        Assert.Equal([301, 302], h.Vm.Resources.Select(r => r.Id));
        Assert.Equal(6, launched.Dto.RunCount);
        // 局部更新语义锁定：不触发 GetForWorkspaceAsync 全量重载（重排结果与全量重载一致）
        Assert.Equal(reloadCallsBefore, h.Resource.ForWorkspaceCalls.Count);
        Assert.Equal([301], h.Launcher.LaunchedResourceIds);
    }
}
