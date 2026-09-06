using System.Windows;
using System.Reflection;
using System.Runtime.CompilerServices;
using WorkNest.App.Services;
using WorkNest.App.Tests.Fakes;
using WorkNest.App.ViewModels;
using WorkNest.App.Views;
using WorkNest.App.Themes;
using System.Windows.Controls;
using Xunit;

namespace WorkNest.App.Tests;

public class WindowResourceTests
{
    [Fact]
    public async Task MainWindow_FolderBrowser_HidesResourceListAndRestoresItOnReturn()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var settings = new FakeSettingsService();
                var browserService = new FakeFolderBrowserService();
                var vm = new MainViewModel(new FakeWorkspaceService(), new FakeResourceService(),
                    new FakeLauncherService(), settings, new FakeAutostartService(), new FakeBackupService(),
                    new FakeIconProvider(), new FakeHotkeyService(), new FakeExportService(),
                    new FakeImportService(), new FakeAppRestart(), new FakeDialogs(), new FakeFilePicker(), browserService);
                var window = new MainWindow(vm, new BackgroundImageSettings(settings));
                try
                {
                    var list = Assert.IsType<ListView>(window.FindName("ResourceList"));
                    // 窗口未 Show 时显式处理绑定队列，模拟真实窗口的 Dispatcher 数据绑定周期。
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.Equal(Visibility.Visible, list.Visibility);
                    vm.FolderBrowser = new FolderBrowserViewModel(browserService, @"C:\isolated", "测试目录", 1);
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.Equal(Visibility.Collapsed, list.Visibility);
                    vm.CloseFolderBrowserCommand.Execute(null);
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.Equal(Visibility.Visible, list.Visibility);
                }
                finally { window.Close(); }
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [ModuleInitializer]
    internal static void InitializeWpfResources()
    {
        // 测试宿主的入口程序集不含应用主题；在任何 WPF 测试前指定真实资源程序集。
        var entry = Assembly.GetEntryAssembly();
        try
        {
            Assembly.SetEntryAssembly(null);
            System.Windows.Application.ResourceAssembly = typeof(SettingsWindow).Assembly;
        }
        finally { Assembly.SetEntryAssembly(entry); }
    }

    [Fact]
    public async Task DialogWindows_CanLoadCompiledXamlAndIcons()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // 使用真实编译 XAML 发现仅构建/VM 测试无法覆盖的资源解析错误；不启动应用。
                var settings = new FakeSettingsService();
                var vm = new SettingsViewModel(settings, new FakeAutostartService(), new FakeBackupService(),
                    new FakeHotkeyService(), new FakeExportService(), new FakeImportService(),
                    new FakeWorkspaceService(), new FakeAppRestart());
                var window = new SettingsWindow(vm, new BackgroundImageSettings(settings));
                Assert.NotNull(window.Icon);
                Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                window.Close();
                Window[] related =
                [
                    new ExportWindow(new ExportViewModel(new FakeWorkspaceService(), new FakeExportService())),
                    new ImportPreviewWindow(new ImportPreviewViewModel(new FakeImportService(), "unused.json")),
                    new WorkspaceManagerWindow(new WorkspaceManagerViewModel(new FakeWorkspaceService())),
                ];
                foreach (var dialog in related)
                {
                    Assert.NotNull(dialog.Icon);
                    dialog.Close();
                }
                var originalTheme = ThemeManager.CurrentTheme;
                try
                {
                    foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
                    {
                        ThemeManager.ApplyToAll(theme);
                        var rename = new RenameWindow("Directory.Build.targets");
                        try
                        {
                            Assert.NotNull(rename.Icon);
                            Assert.Same(rename.FindResource("Bg"), rename.Background);
                            Assert.Same(rename.FindResource("Fg"), rename.Foreground);
                            var input = Assert.IsType<TextBox>(rename.FindName("NameBox"));
                            Assert.NotNull(input.Style);
                            Assert.Equal("Directory.Build.targets", input.Text);
                            var prompt = Assert.IsType<TextBlock>(rename.FindName("PromptText"));
                            Assert.Same(rename.FindResource("FgSecondary"), prompt.Foreground);
                        }
                        finally { rename.Close(); }
                    }
                }
                finally { ThemeManager.ApplyToAll(originalTheme); }
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
