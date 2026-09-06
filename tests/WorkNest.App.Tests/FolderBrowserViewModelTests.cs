using System.IO; // WindowsDesktop SDK 的隐式 using 不含 System.IO，必须显式引入
using WorkNest.App.Tests.Fakes;
using WorkNest.App.ViewModels;
using WorkNest.Application.Dtos;
using Xunit;

namespace WorkNest.App.Tests;

/// <summary>
/// 文件夹浏览面板视图模型：首屏加载与面包屑、逐级深入/上级、前进/后退历史导航、
/// 前进分支截断、子文件打开事件与枚举失败时的状态保持。fake 全同步，命令返回即完成。
/// </summary>
public class FolderBrowserViewModelTests
{
    private const string Root = @"C:\work\proj";
    private const string Sub = @"C:\work\proj\sub";
    private const string Deep = @"C:\work\proj\sub\deep";

    private static FolderListingDto Listing(string path, params string[] names) => new(
        path,
        names.Select(n => new FolderEntryDto(n, Path.Combine(path, n), IsDirectory: true, 0, DateTime.Now)).ToList(),
        Truncated: false);

    private static FolderEntryViewModel EntryOf(FolderBrowserViewModel vm, string name) =>
        vm.Entries.Single(e => e.Name == name);

    private static FolderBrowserViewModel Create(FakeFolderBrowserService? service = null)
    {
        service ??= new FakeFolderBrowserService();
        var vm = new FolderBrowserViewModel(service, Root, "proj", resourceId: 7);
        return vm;
    }

    [Fact]
    public async Task Initialize_LoadsRoot_AndBuildsBreadcrumbs()
    {
        var fake = new FakeFolderBrowserService();
        fake.Listings[Root] = Listing(Root, "sub", "README.md");

        var vm = Create(fake);
        await vm.InitializeAsync();

        Assert.Equal(Root, vm.CurrentPath);
        Assert.Equal(2, vm.Entries.Count);
        Assert.False(vm.CanGoUp);
        Assert.False(vm.CanGoBack);
        Assert.False(vm.CanGoForward);
        Assert.Equal(["proj"], vm.Breadcrumbs.Select(b => b.Name)); // 根目录面包屑只有资源名一段
        Assert.Equal("2 项", vm.StatusText);
    }

    [Fact]
    public async Task OpenEntry_Subdirectory_Navigates_HistoryBackAndForward()
    {
        var fake = new FakeFolderBrowserService();
        fake.Listings[Root] = Listing(Root, "sub");
        fake.Listings[Sub] = Listing(Sub, "deep");
        fake.Listings[Deep] = Listing(Deep);
        var vm = Create(fake);
        await vm.InitializeAsync();

        await vm.OpenEntryCommand.ExecuteAsync(EntryOf(vm, "sub"));
        Assert.Equal(Sub, vm.CurrentPath);
        Assert.True(vm.CanGoBack);
        Assert.True(vm.CanGoUp);
        Assert.Equal(["proj", "sub"], vm.Breadcrumbs.Select(b => b.Name));

        // 逐级深入到第二层
        await vm.OpenEntryCommand.ExecuteAsync(EntryOf(vm, "deep"));
        Assert.Equal(Deep, vm.CurrentPath);
        Assert.Equal(["proj", "sub", "deep"], vm.Breadcrumbs.Select(b => b.Name));

        // 后退两级 → 前进一级
        await vm.GoBackCommand.ExecuteAsync(null);
        await vm.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(Root, vm.CurrentPath);
        Assert.False(vm.CanGoBack);
        Assert.True(vm.CanGoForward);
        await vm.GoForwardCommand.ExecuteAsync(null);
        Assert.Equal(Sub, vm.CurrentPath);
    }

    [Fact]
    public async Task GoUp_WalksToRoot_AndStops()
    {
        var fake = new FakeFolderBrowserService();
        fake.Listings[Root] = Listing(Root, "sub");
        fake.Listings[Sub] = Listing(Sub);
        var vm = Create(fake);
        await vm.InitializeAsync();
        await vm.OpenEntryCommand.ExecuteAsync(EntryOf(vm, "sub"));

        await vm.GoUpCommand.ExecuteAsync(null);
        Assert.Equal(Root, vm.CurrentPath);
        Assert.False(vm.CanGoUp); // 上级到浏览根为止，不越出资源范围

        await vm.GoUpCommand.ExecuteAsync(null);
        Assert.Equal(Root, vm.CurrentPath); // 根上再点上级无效
    }

    [Fact]
    public async Task Open_FromHistory_TruncatesForwardBranch()
    {
        var fake = new FakeFolderBrowserService();
        fake.Listings[Root] = Listing(Root, "sub", "other");
        fake.Listings[Sub] = Listing(Sub);
        fake.Listings[Deep] = Listing(Deep);
        var vm = Create(fake);
        await vm.InitializeAsync();

        await vm.OpenEntryCommand.ExecuteAsync(EntryOf(vm, "sub"));
        await vm.GoBackCommand.ExecuteAsync(null);
        // 回到根后改走另一分支：原有前进历史应被截断
        await vm.OpenEntryCommand.ExecuteAsync(EntryOf(vm, "other"));
        Assert.Equal(Path.Combine(Root, "other"), vm.CurrentPath);
        Assert.False(vm.CanGoForward);
    }

    [Fact]
    public async Task OpenEntry_File_RaisesFileOpenRequested()
    {
        var fake = new FakeFolderBrowserService();
        fake.Listings[Root] = new FolderListingDto(Root,
            [new FolderEntryDto("说明.txt", Path.Combine(Root, "说明.txt"), false, 12, DateTime.Now)], false);
        var vm = Create(fake);
        await vm.InitializeAsync();
        var opened = new List<string>();
        vm.FileOpenRequested += path => opened.Add(path);

        await vm.OpenEntryCommand.ExecuteAsync(EntryOf(vm, "说明.txt"));

        Assert.Equal([Path.Combine(Root, "说明.txt")], opened); // 文件不导航，交给主窗口系统打开
        Assert.Equal(Root, vm.CurrentPath);
    }

    [Fact]
    public async Task ListingFailure_KeepsState_AndRaisesError()
    {
        var fake = new FakeFolderBrowserService(); // 未预设 Sub 的内容也会成功返回空；用异常路径单独验证：
        fake.ThrowOnPath[Sub] = "无法读取目录内容";
        var vm = Create(fake);
        await vm.InitializeAsync();

        var errors = new List<string>();
        vm.ErrorOccurred += message => errors.Add(message);
        await vm.OpenBreadcrumbCommand.ExecuteAsync(new BreadcrumbSegment("sub", Sub));

        Assert.Single(errors);                       // 错误上报主窗口横幅
        Assert.Equal(Root, vm.CurrentPath);          // 失败不提交导航：目录与历史保持原状
        Assert.False(vm.CanGoBack);
        Assert.Empty(vm.Breadcrumbs.Skip(1));        // 面包屑不出现失败目标
    }
}
