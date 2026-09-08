using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WorkNest.App.Services;
using WorkNest.App.Tests.Fakes;
using WorkNest.App.ViewModels;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Services;
using WorkNest.Domain;
using Xunit;

namespace WorkNest.App.Tests;

public class ListExperienceTests
{
    private sealed class SettingsRepository : ISettingsRepository
    {
        public string? Raw { get; set; }
        public bool FailWrites { get; set; }
        public Task<string?> GetAsync(string key) => Task.FromResult(Raw);
        public Task SetAsync(string key, string value)
        {
            if (FailWrites) throw new InvalidOperationException("模拟写入失败");
            Raw = value;
            return Task.CompletedTask;
        }
    }

    private static MainViewModel CreateVm(FakeResourceService resource, ISettingsService? settings = null,
        int debounce = 0)
    {
        var workspace = new FakeWorkspaceService();
        workspace.EnqueueOrder(new WorkspaceDto(1, "开发", "#3399FF", null, 0));
        return new MainViewModel(workspace, resource, new FakeLauncherService(), settings ?? new FakeSettingsService(),
            new FakeAutostartService(), new FakeBackupService(), new FakeIconProvider(), new FakeDialogs(),
            new FakeFilePicker(), new FakeFolderBrowserService(), debounce);
    }

    private static ResourceDto Item(int id, string name) => new()
    {
        Id = id, Name = name, Target = @"C:\isolated\" + name, Type = ResourceType.File,
        PathExists = true, WorkspaceIds = [1],
    };

    [Fact]
    public Task Search_KeyboardFlushUsesLatestTextAndClearPreservesScope() => RunSta(async () =>
    {
        var vm = CreateVm(new FakeResourceService { ForWorkspace = [Item(1, "旧结果"), Item(2, "新结果")] }, debounce: 60000);
        await vm.ReloadWorkspacesAsync();
        await vm.LoadResourcesAsync();
        vm.SearchText = "新结果";
        Assert.Equal(2, vm.Resources.Count); // 防抖仍未触发
        Assert.True(vm.ApplyPendingSearch());
        Assert.Equal(2, Assert.Single(vm.Resources).Id);
        vm.SearchText = "不存在";
        Assert.False(vm.ShowSearchEmpty); // 未完成过滤时不提前显示空状态
        vm.ApplyPendingSearch();
        Assert.True(vm.ShowSearchEmpty);
        vm.ClearSearchCommand.Execute(null);
        Assert.False(vm.SearchAll);
        Assert.Equal(2, vm.Resources.Count);
        Assert.False(vm.ShowSearchEmpty);
    });

    [Fact]
    public Task Search_ExpandRetainsQueryAndEmptyStateRespectsPanelsAndRecentScope() => RunSta(async () =>
    {
        var vm = CreateVm(new FakeResourceService { AllWorkspaces = [Item(2, "说明书")] });
        await vm.ReloadWorkspacesAsync();
        await vm.LoadResourcesAsync();
        vm.SearchText = "说明书";
        Assert.True(vm.ShowSearchEmpty);
        Assert.True(vm.CanExpandSearch);
        vm.ExpandSearchCommand.Execute(null);
        await WaitUntil(() => !vm.IsResourceLoading);
        Assert.Equal("说明书", vm.SearchText);
        Assert.Equal(2, Assert.Single(vm.Resources).Id);
        Assert.False(vm.CanExpandSearch);
        vm.SearchText = "其他";
        Assert.Contains("全部工作区", vm.SearchEmptyMessage);
        vm.EditPanel = ResourceEditViewModel.New(ResourceType.File, @"C:\isolated\file", 1);
        Assert.False(vm.ShowSearchEmpty);
        vm.EditPanel = null;
        Assert.True(vm.ShowSearchEmpty);
        vm.FolderBrowser = new FolderBrowserViewModel(new FakeFolderBrowserService(), @"C:\isolated", "目录", 1);
        Assert.False(vm.ShowSearchEmpty);
        vm.FolderBrowser = null;
        vm.CurrentWorkspaceOption = WorkspaceOptionViewModel.RecentItem;
        await WaitUntil(() => !vm.IsResourceLoading);
        vm.SearchText = "其他";
        Assert.True(vm.ShowSearchEmpty);
        Assert.Contains("最近使用", vm.SearchEmptyMessage);
        Assert.False(vm.CanExpandSearch);
    });

    [Fact]
    public Task Search_LoadingFailureAndOutdatedCompletionNeverShowFalseEmptyState() => RunSta(async () =>
    {
        var resource = new FakeResourceService { ForWorkspace = [Item(1, "旧资源")] };
        var vm = CreateVm(resource);
        await vm.ReloadWorkspacesAsync();
        await vm.LoadResourcesAsync();
        vm.SearchText = "不存在";
        Assert.True(vm.ShowSearchEmpty);
        var old = new TaskCompletionSource<IReadOnlyList<ResourceDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resource.LoadWorkspace = _ => { entered.TrySetResult(); return old.Task; };
        var oldLoad = vm.LoadResourcesAsync();
        await entered.Task;
        Assert.True(vm.ShowResourceLoading);
        Assert.False(vm.ShowSearchEmpty);
        Assert.False(vm.ApplyPendingSearch());
        resource.LoadWorkspace = _ => throw new InvalidOperationException("模拟读取失败");
        await vm.LoadResourcesAsync();
        Assert.False(vm.IsResourceLoading);
        Assert.False(vm.ShowSearchEmpty);
        Assert.Contains("模拟读取失败", vm.ErrorMessage);
        old.SetResult([Item(9, "不存在")]);
        await oldLoad;
        Assert.Empty(vm.Resources);
        Assert.False(vm.ShowSearchEmpty);
        resource.LoadWorkspace = null;
        await vm.LoadResourcesAsync();
        Assert.True(vm.ShowSearchEmpty); // 最新加载成功后恢复正常无结果提示
    });

    [Fact]
    public async Task Preferences_RoundTripHiddenSortResetAndWriteFailure()
    {
        var repository = new SettingsRepository();
        var settings = new SettingsService(repository);
        var vm = CreateVm(new FakeResourceService(), settings);
        await vm.UpdateListPreferencesAsync(new ResourceListPreferences
        {
            NameWidth = 288, TypeWidth = 84, TargetWidth = 420, ShowTarget = false,
            SortKey = "Type", SortDescending = true,
        });
        var restored = CreateVm(new FakeResourceService(), settings);
        await restored.LoadListPreferencesAsync();
        Assert.Equal(vm.ListPreferences, restored.ListPreferences);
        Assert.Equal("Type", restored.SortKey);
        Assert.True(restored.SortDescending);
        await restored.UpdateListPreferencesAsync(restored.ListPreferences with { ShowType = false });
        Assert.Null(restored.SortKey);
        Assert.False(restored.SortDescending);
        repository.FailWrites = true;
        await restored.UpdateListPreferencesAsync(restored.ListPreferences with { NameWidth = 320 });
        Assert.Equal(320, restored.ListPreferences.NameWidth);
        Assert.Contains("列表偏好保存失败", restored.ErrorMessage);
        repository.FailWrites = false;
        await restored.ResetListLayoutCommand.ExecuteAsync(null);
        Assert.Equal(new ResourceListPreferences(), restored.ListPreferences);
    }

    [Theory]
    [InlineData("损坏 JSON")]
    [InlineData("null")]
    [InlineData("{\"Version\":99,\"NameWidth\":333}")]
    public async Task Preferences_InvalidSettingsUseDefaults(string raw)
    {
        var vm = CreateVm(new FakeResourceService(), new SettingsService(new SettingsRepository { Raw = raw }));
        await vm.LoadListPreferencesAsync();
        Assert.Equal(new ResourceListPreferences(), vm.ListPreferences);
    }

    [Fact]
    public async Task Preferences_InvalidFieldsAreCorrectedIndividually()
    {
        var raw = "{\"NameWidth\":-1,\"TypeWidth\":88,\"TargetWidth\":99999,\"SortKey\":\"Unknown\",\"SortDescending\":true,\"ShowTarget\":false}";
        var vm = CreateVm(new FakeResourceService(), new SettingsService(new SettingsRepository { Raw = raw }));
        await vm.LoadListPreferencesAsync();
        Assert.Equal(190, vm.ListPreferences.NameWidth);
        Assert.Equal(88, vm.ListPreferences.TypeWidth);
        Assert.Equal(180, vm.ListPreferences.TargetWidth);
        Assert.False(vm.ListPreferences.ShowTarget);
        Assert.Null(vm.SortKey);
        Assert.False(vm.SortDescending);
    }

    [Fact]
    public Task Preferences_ColumnSortCyclesPersistAndRecentDefaultKeepsTimeOrder() => RunSta(async () =>
    {
        var repository = new SettingsRepository();
        var settings = new SettingsService(repository);
        var resource = new FakeResourceService { RecentlyUsed = [Item(2, "B"), Item(1, "A")] };
        var vm = CreateVm(resource, settings);
        await vm.ReloadWorkspacesAsync();
        vm.CurrentWorkspaceOption = WorkspaceOptionViewModel.RecentItem;
        await WaitUntil(() => !vm.IsResourceLoading);
        Assert.Equal(new[] { 2, 1 }, vm.Resources.Select(r => r.Id));
        vm.ApplyColumnSortCommand.Execute("Name");
        Assert.Equal(new[] { 1, 2 }, vm.Resources.Select(r => r.Id));
        vm.ApplyColumnSortCommand.Execute("Name");
        Assert.Equal(new[] { 2, 1 }, vm.Resources.Select(r => r.Id));
        Assert.True(await vm.FlushListPreferencesAsync());
        var restored = CreateVm(resource, settings);
        await restored.LoadListPreferencesAsync();
        Assert.Equal("Name", restored.SortKey);
        Assert.True(restored.SortDescending);
        vm.ApplyColumnSortCommand.Execute("Name");
        Assert.True(await vm.FlushListPreferencesAsync());
        Assert.Null(vm.SortKey);
        Assert.Equal(new[] { 2, 1 }, vm.Resources.Select(r => r.Id));
        await restored.LoadListPreferencesAsync();
        Assert.Null(restored.SortKey);
    });

    [Fact]
    public Task Window_SearchTextFiltersVisibleListAndFindWorksInBothContexts() => RunSta(async () =>
    {
        var settings = new FakeSettingsService();
        var vm = CreateVm(new FakeResourceService { ForWorkspace = [Item(1, "开发说明.txt"), Item(2, "照片.png")] },
            settings, debounce: 30);
        var window = new MainWindow(vm, new BackgroundImageSettings(settings), () =>
            new SettingsViewModel(settings, new FakeAutostartService(), new FakeBackupService(),
                new FakeHotkeyService(), new FakeExportService(), new FakeImportService(), new FakeWorkspaceService(),
                new FakeAppRestart(), new FakeDialogs(), new FakeFilePicker()));
        try
        {
            await vm.ReloadWorkspacesAsync();
            await vm.LoadResourcesAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var search = (TextBox)window.FindName("SearchBox");
            search.SetCurrentValue(TextBox.TextProperty, "说明");
            await WaitUntil(() => vm.Resources.Count == 1);
            Assert.Equal("开发说明.txt", Assert.Single(vm.Resources).Name);
            Assert.Equal("说明", vm.SearchText);
            var shortcut = Assert.Single(window.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.F && b.Modifiers == ModifierKeys.Control);
            Assert.Same(ApplicationCommands.Find, shortcut.Command);
            Assert.True(ApplicationCommands.Find.CanExecute(null, window));
            ApplicationCommands.Find.Execute(null, window);
            Assert.Equal(search.Text.Length, search.SelectionLength);

            var service = new FakeFolderBrowserService();
            const string root = @"C:\isolated";
            service.Listings[root] = new FolderListingDto(root,
                [new("会议说明.TXT", root + @"\会议说明.TXT", false, 10, DateTime.Now),
                 new("照片.png", root + @"\照片.png", false, 10, DateTime.Now)], false);
            var browser = new FolderBrowserViewModel(service, root, "目录", 1);
            await browser.InitializeAsync();
            vm.FolderBrowser = browser;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(string.Empty, search.Text);
            search.SetCurrentValue(TextBox.TextProperty, "说明 txt");
            await WaitUntil(() => browser.Entries.Count == 1);
            Assert.Equal("会议说明.TXT", Assert.Single(browser.Entries).Name);
            Assert.Equal("说明", vm.SearchText); // 文件目录查询独立保存，不覆盖资源列表查询。
            Assert.True(ApplicationCommands.Find.CanExecute(null, window));
            ApplicationCommands.Find.Execute(null, window);
            Assert.Equal(search.Text.Length, search.SelectionLength);
            await browser.RefreshCommand.ExecuteAsync(null);
            Assert.Single(browser.Entries);
            search.SetCurrentValue(TextBox.TextProperty, "无匹配");
            await WaitUntil(() => browser.Entries.Count == 0);
            Assert.Contains("找到 0 项", browser.StatusText);
            search.SetCurrentValue(TextBox.TextProperty, "");
            await WaitUntil(() => browser.Entries.Count == 2);
            // 通过真实导航命令进入两层子目录，再验证屏幕上的列表绑定，而非仅检查 VM。
            foreach (var child in new[] { root + @"\第二层", root + @"\第二层\第三层" })
            {
                service.Listings[child] = new FolderListingDto(child,
                    [new("子目录报告.txt", child + @"\子目录报告.txt", false, 10, DateTime.Now),
                     new("其他.png", child + @"\其他.png", false, 10, DateTime.Now)], false);
                await browser.OpenEntryCommand.ExecuteAsync(new FolderEntryViewModel(
                    new FolderEntryDto("子目录", child, true, 0, DateTime.Now)));
                Assert.Equal(child, browser.CurrentPath);
                search.SelectAll();
                search.SelectedText = "报告";
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var visibleList = (ListView)window.FindName("FolderEntryList");
                Assert.Equal("子目录报告.txt", Assert.Single(visibleList.Items.Cast<FolderEntryViewModel>()).Name);
                Assert.Equal("报告", browser.SearchText);
                search.SelectAll();
                search.SelectedText = string.Empty;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(2, visibleList.Items.Count);
            }
            vm.FolderBrowser = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("说明", search.Text);
            Assert.Single(vm.Resources);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Window_ColumnMenuAndPreferencesUpdateRealXaml() => RunSta(async () =>
    {
        var settings = new FakeSettingsService();
        var vm = CreateVm(new FakeResourceService(), settings);
        var window = new MainWindow(vm, new BackgroundImageSettings(settings), () =>
            new SettingsViewModel(settings, new FakeAutostartService(), new FakeBackupService(),
                new FakeHotkeyService(), new FakeExportService(), new FakeImportService(), new FakeWorkspaceService(),
                new FakeAppRestart(), new FakeDialogs(), new FakeFilePicker()));
        try
        {
            var list = (ListView)window.FindName("ResourceList");
            var grid = (GridView)list.View;
            Assert.Equal(3, grid.Columns.Count);
            await vm.UpdateListPreferencesAsync(new ResourceListPreferences { NameWidth = 280, ShowType = false });
            Assert.Equal(2, grid.Columns.Count);
            Assert.Equal(280, grid.Columns[0].Width);
            var header = Assert.IsType<GridViewColumnHeader>(grid.Columns[0].Header);
            header.ContextMenu!.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.Equal(6, header.ContextMenu.Items.Count);
            Assert.False(((MenuItem)header.ContextMenu.Items[0]).IsEnabled);
            Assert.False(((MenuItem)header.ContextMenu.Items[1]).IsChecked);
            await vm.ResetListLayoutCommand.ExecuteAsync(null);
            Assert.Equal(3, grid.Columns.Count);
            Assert.Equal(190, grid.Columns[0].Width);
            grid.Columns[0].Width = 310;
            list.RaiseEvent(new DragCompletedEventArgs(120, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Assert.True(await vm.FlushListPreferencesAsync());
            Assert.Equal(310, vm.ListPreferences.NameWidth);
            await vm.ReloadWorkspacesAsync();
            await vm.LoadResourcesAsync();
            vm.SearchText = "未找到";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var empty = (Border)window.FindName("SearchEmptyPanel");
            Assert.Equal(Visibility.Visible, empty.Visibility);
            vm.EditPanel = ResourceEditViewModel.New(ResourceType.File, @"C:\isolated\test", 1);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Collapsed, empty.Visibility);
            vm.EditPanel = null;
        }
        finally { window.Close(); }
    });

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private static async Task RunSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
