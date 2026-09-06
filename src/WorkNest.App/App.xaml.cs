using System.Reflection;
using System.IO; // WindowsDesktop 隐式 using 不含 System.IO（被 WinForms/Drawing 顶替）
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WorkNest.App.Startup;
using WorkNest.App.Themes;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Services;
using WorkNest.Infrastructure.Database;
using WorkNest.Infrastructure.DependencyInjection;
using WorkNest.Infrastructure.Logging;
using WorkNest.Platform.Windows;

namespace WorkNest.App;

/// <summary>
/// 组合根：按文档 6.3 启动流程组装——
/// 单实例检查 → 数据目录与日志 → SQLite 迁移 → 平台服务/托盘/热键 → 主窗口与状态恢复。
/// </summary>
public partial class App : System.Windows.Application, IAppRestart
{
    private ServiceProvider? _host;
    private MainWindow? _mainWindow;
    private ITrayService? _tray;
    private IGlobalHotkeyService? _hotkey;

    // 托盘“退出”/重启提出的退出请求：仅表示意图，主窗口未保存确认放行后才提交为退出；
    // 确认被取消时还原，保证后续 X 按钮仍按关闭设置行为执行（R08）
    private bool _exitRequested;

    // F24：单实例确认后才算“本次会话开始”，OnExit 据此决定是否写收尾标志，
    // 避免二次启动实例的快速退出把正在运行实例的布防标志误写为 true
    private bool _sessionArmed;

    // 恢复备份后的整进程重启：先完全退出释放数据库句柄，OnExit 里再拉起新进程
    private bool _restartOnExit;

    // 关闭按钮行为的进程内缓存；窗口每次激活时刷新，Closing 时只读缓存（不可异步）
    private bool _closeToTray = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _ = StartupAsync();
    }

    private async Task StartupAsync()
    {
        try
        {
            await RunStartupCoreAsync();
        }
        catch (Exception ex)
        {
            // 启动链路任何未预期异常：记录 + 严重错误弹窗（F23），不留下无提示的静默进程
            WorkNestLog.Error("App", "启动失败", ex);
            MessageBox.Show(
                "WorkNest 启动失败：" + ex.Message + "\n\n日志目录：%LocalAppData%\\WorkNest\\Logs",
                "WorkNest",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task RunStartupCoreAsync()
    {
        // 1~2. 数据目录 + 日志 + 全部服务注册（目录与日志在注册方法内完成）
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorkNest");
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        services.AddWorkNestInfrastructure(dataRoot);
        services.AddWorkNestWindowsPlatform();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<IResourceService, ResourceService>();
        services.AddSingleton<ILauncherService, LauncherService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<WorkNest.App.Services.BackgroundImageSettings>();
        services.AddSingleton<IFolderBrowserService, FolderBrowserService>();
        services.AddSingleton<DebouncedBackupScheduler>();
        services.AddSingleton<IExportService, ExportService>();
        services.AddSingleton<IImportService, ImportService>();
        // 确认框与文件选择经接口注入视图模型，VM 状态测试可替换（批次 B 完成标准）
        services.AddSingleton<WorkNest.App.Services.IAppDialogs, WorkNest.App.Services.AppDialogs>();
        services.AddSingleton<WorkNest.App.Services.IFilePicker, WorkNest.App.Services.Win32FilePicker>();
        // 恢复备份需要整进程重启（释放数据库句柄），实现就挂在本组合根上
        services.AddSingleton<IAppRestart>(_ => (IAppRestart)System.Windows.Application.Current);
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<MainWindow>();
        _host = services.BuildServiceProvider();

        // 延迟合并自动备份失败只记日志，不打断任何设置操作（决策 65）
        _host.GetRequiredService<DebouncedBackupScheduler>().SnapshotFailed += (_, ex) =>
            WorkNestLog.Error("App", "自动备份失败", ex);

        // 3. 单实例：已有实例时激活它并退出当前进程（决策 3.4）
        var instanceLock = _host.GetRequiredService<ISingleInstanceLock>();
        if (!instanceLock.TryAcquireFirstInstance())
        {
            instanceLock.SignalRunningInstance();
            WorkNestLog.Info("App", "已有实例在运行，通知激活后退出");
            Shutdown();
            return;
        }
        // 尽早监听激活请求，避免迁移期间二次启动的信号丢失
        instanceLock.ListenActivationRequest(() => Dispatcher.BeginInvoke(ShowMainWindow));

        RegisterGlobalExceptionHandlers();
        WorkNestLog.Info("App", $"WorkNest v{Assembly.GetExecutingAssembly().GetName().Version} 启动");

        // 4. 数据库迁移（7.3：事务迁移，失败保护原文件并明确报错）
        try
        {
            _host.GetRequiredService<DbMigrator>().Migrate();
        }
        catch (Exception ex)
        {
            WorkNestLog.Error("App", "数据库迁移失败", ex);
            var result = MessageBox.Show(
                "数据库初始化失败，已保留原文件未做修改。\n" +
                ex.Message +
                "\n\n是否打开备份目录？", "WorkNest",
                MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", Path.Combine(dataRoot, "Backups"));
                }
                catch
                {
                    // 打开目录失败不影响退出流程
                }
            }
            Shutdown();
            return;
        }

        var settings = _host.GetRequiredService<ISettingsService>();
        _closeToTray = await settings.GetAsync(SettingKeys.CloseToTray, true);

        // F24（决策 99/102）：读到 false = 上次异常退出；随后立即布防为 false，
        // 正常退出时在 OnExit 写回 true，强杀/断电则到不了 OnExit
        var cleanExit = await settings.GetAsync(SettingKeys.SessionCleanExit, true);
        if (!cleanExit)
        {
            WorkNestLog.Warning("App", "检测到上次异常退出，将在主窗口显示提示");
        }
        _ = settings.SetAsync(SettingKeys.SessionCleanExit, false);
        _sessionArmed = true;

        // 5. 主题（决策 30：首次浅色，之后按设置恢复）
        var theme = await settings.GetAsync(SettingKeys.Theme, ThemeManager.Light);
        ThemeManager.ApplyToAll(theme);
        await _host.GetRequiredService<WorkNest.App.Services.BackgroundImageSettings>().LoadAsync();

        // 6. 托盘（F06）：菜单与双击都回到 UI 线程处理
        _tray = _host.GetRequiredService<ITrayService>();
        _tray.OpenRequested += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
        _tray.ExitRequested += (_, _) => Dispatcher.BeginInvoke(RequestExit);
        _tray.Initialize();

        // 7. 全局呼出快捷键（F10）：注册冲突时托盘入口始终可用，并在主窗口横幅可见（R05）
        _hotkey = _host.GetRequiredService<IGlobalHotkeyService>();
        var gesture = await settings.GetAsync(SettingKeys.Hotkey, "Ctrl+Alt+W");
        string? hotkeyWarning = null;
        if (!_hotkey.TryRegister(gesture))
        {
            WorkNestLog.Warning("App", $"全局快捷键 {gesture} 注册失败（可能被占用），托盘入口仍可用");
            hotkeyWarning = $"全局快捷键 {gesture} 注册失败（可能被占用），当前呼出热键未生效；托盘入口仍可用，可在设置中更换组合。";
        }
        _hotkey.HotkeyPressed += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);

        // 8. 主窗口：恢复上次工作区与窗口布局（决策 93/103/105）
        _mainWindow = _host.GetRequiredService<MainWindow>();
        if (hotkeyWarning is not null)
        {
            _mainWindow.ViewModel.ShowError(hotkeyWarning);
        }
        _mainWindow.ViewModel.InitialWorkspaceId =
            await settings.GetAsync<int?>(SettingKeys.LastWorkspaceId, null);
        _mainWindow.ViewModel.ShowAbnormalExitNotice = !cleanExit; // F24：横幅随窗口首帧出现
        await _mainWindow.ViewModel.InitializeAsync();
        await WindowController.ApplyAsync(_mainWindow, settings);
        WindowController.AttachEdgeSnap(_mainWindow);

        // 关闭行为：先让窗口自身的“未保存修改”确认执行，未被取消才进入托盘/退出分支
        _mainWindow.Closing += OnMainWindowClosing;
        _mainWindow.Activated += OnMainWindowActivated;
        SessionEnding += (_, _) => WindowController.SaveAndWait(_mainWindow, settings);

        _mainWindow.Show();
        WorkNestLog.Info("App", "主窗口已显示");
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WorkNestLog.Error("App", "UI 线程未处理异常", args.Exception);
            MessageBox.Show(
                "发生未处理的错误：" + args.Exception.Message + "\n\n应用将继续运行，详情见日志。",
                "WorkNest", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WorkNestLog.Error("App", "后台任务未观察异常", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>呼出主窗口：隐藏则显示、最小化则还原，已显示则仅置前（决策 25）。</summary>
    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        // Activate 可能被前台锁策略挡住，短暂置顶确保可见后立即交还
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
    }

    private void OnMainWindowActivated(object? sender, EventArgs e)
    {
        // 异步刷新关闭行为缓存（设置窗口可能已修改）
        if (_host is not null)
        {
            var settings = _host.GetRequiredService<ISettingsService>();
            _ = Task.Run(async () =>
            {
                try
                {
                    var value = await settings.GetAsync(SettingKeys.CloseToTray, true);
                    await Dispatcher.InvokeAsync(() => _closeToTray = value);
                }
                catch
                {
                    // 缓存刷新失败沿用旧值
                }
            });
        }
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_mainWindow is null || _host is null)
        {
            return;
        }

        // 窗口构造时注册的“未保存修改”确认已先行执行；被取消则不再处理，
        // 并还原退出请求，避免取消一次托盘退出后 X 按钮被当成强制退出（R08）
        if (e.Cancel)
        {
            _exitRequested = false;
            return;
        }

        WindowController.SaveAndWait(_mainWindow, _host.GetRequiredService<ISettingsService>());

        if (!_exitRequested && _closeToTray)
        {
            // 决策 3：首次默认隐藏到托盘，进程常驻
            e.Cancel = true;
            _mainWindow.Hide();
            WorkNestLog.Info("App", "主窗口隐藏到托盘");
            return;
        }

        WorkNestLog.Info("App", _exitRequested ? "用户请求退出" : "按关闭设置直接退出");
        TeardownAndShutdown();
    }

    private void RequestExit()
    {
        _exitRequested = true;
        _mainWindow?.Close();
    }

    private void TeardownAndShutdown()
    {
        // 托盘/热键/容器统一在 OnExit 释放，这里仅触发关闭流程
        Shutdown();
    }

    /// <summary>IAppRestart：恢复备份后整进程重启。先置重启标记再优雅退出，
    /// OnExit 在释放单实例锁与数据库句柄之后拉起新进程，避免新实例撞上互斥体。</summary>
    public void Restart()
    {
        WorkNestLog.Info("App", "请求重启应用（恢复备份后）");
        _restartOnExit = true;
        _exitRequested = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // F24：正常退出路径写收尾标志。阻塞等待确保落库后才退出进程；
        // 强杀/断电到不了这里，下次启动读到 false 即提示异常退出
        if (_sessionArmed && _host is not null)
        {
            try
            {
                var settings = _host.GetRequiredService<ISettingsService>();
                settings.SetAsync(SettingKeys.SessionCleanExit, true).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                WorkNestLog.Warning("App", "写入会话收尾标志失败", ex);
            }
        }

        _hotkey?.Unregister();
        _tray?.Dispose();
        _tray = null;
        _host?.Dispose();

        // 重启必须在单实例锁释放之后执行，否则新进程会被当作二次启动而退出
        if (_restartOnExit)
        {
            _restartOnExit = false;
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    System.Diagnostics.Process.Start(exe);
                }
            }
            catch (Exception ex)
            {
                WorkNestLog.Error("App", "重启应用失败", ex);
            }
        }

        base.OnExit(e);
    }
}
