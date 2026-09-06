using Microsoft.Extensions.DependencyInjection;
using WorkNest.Application.Abstractions;
using WorkNest.Platform.Windows.Autostart;
using WorkNest.Platform.Windows.Hotkeys;
using WorkNest.Platform.Windows.Icons;
using WorkNest.Platform.Windows.Launching;
using WorkNest.Platform.Windows.SingleInstance;
using WorkNest.Platform.Windows.Tray;

namespace WorkNest.Platform.Windows;

/// <summary>Windows 平台适配层的 DI 注册入口（组合根 WorkNest.App 调用）。</summary>
public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// 注册六个 Windows 平台服务。全部单例：服务有明确的状态
    /// （Mutex/事件句柄、GDI 托盘图标、热键窗口、图标缓存、注册表键），重复实例化会泄漏系统资源。
    /// </summary>
    public static IServiceCollection AddWorkNestWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ISingleInstanceLock, SingleInstanceLock>();
        services.AddSingleton<IProcessLauncher, WindowsProcessLauncher>();
        services.AddSingleton<IResourceIconProvider, ResourceIconProvider>();
        services.AddSingleton<ITrayService, TrayService>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<IAutostartService, AutostartService>();
        return services;
    }
}
