using System.Text.Json;
using System.Windows;
using WorkNest.Application.Abstractions;
using WorkNest.Infrastructure.Logging;

namespace WorkNest.App.Startup;

/// <summary>主窗口位置持久化模型（SettingKeys.WindowBounds 的 JSON 载荷）。</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, WindowState State);

/// <summary>
/// 主窗口布局控制（F17 / 决策 22-25、103-105）：
/// 尺寸钳制 320–640 × ≥480、恢复时校验可视区域、默认停靠主屏右侧、靠近左右边缘自动吸附。
/// </summary>
public static class WindowController
{
    private const double DefaultWidth = 420;
    private const double DefaultHeight = 720;
    private const double MinWidth = 320;
    private const double MaxWidth = 640;
    private const double MinHeight = 480;
    private const double SnapTolerance = 16;

    private static bool _snapping;

    /// <summary>按持久化布局定位窗口；无有效布局时默认停靠主屏工作区右侧并垂直居中。</summary>
    public static async Task ApplyAsync(Window window, ISettingsService settings)
    {
        var work = SystemParameters.WorkArea;
        var placement = await settings.GetAsync<WindowPlacement?>(SettingKeys.WindowBounds, null);

        // 决策 22/104：宽度 320–640，高度不低于 480 且不超过工作区
        var width = placement is null ? DefaultWidth : Math.Clamp((double)placement.Width, MinWidth, MaxWidth);
        var height = placement is null ? DefaultHeight : Math.Min(Math.Max(placement.Height, MinHeight), work.Height);

        double x, y;
        if (placement is not null && IsVisibleOnAnyMonitor(placement.X, placement.Y, width, height))
        {
            x = placement.X;
            y = placement.Y;
        }
        else
        {
            // 决策 105：位置失效（如显示器断开）时回退，而不是把窗口丢出屏幕
            x = work.Right - width;
            y = work.Top + Math.Max(0, (work.Height - height) / 2);
        }

        window.Left = x;
        window.Top = y;
        window.Width = width;
        window.Height = height;
    }

    /// <summary>至少保留 100×60 可视区域，避免窗口整体落在已断开的显示器上。</summary>
    private static bool IsVisibleOnAnyMonitor(double x, double y, double width, double height)
    {
        var vs = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var rect = new Rect(x, y, width, height);
        return vs.IntersectsWith(rect)
               && Math.Min(vs.Right, rect.Right) - Math.Max(vs.Left, rect.Left) >= 100
               && Math.Min(vs.Bottom, rect.Bottom) - Math.Max(vs.Top, rect.Top) >= 60;
    }

    /// <summary>保存布局（异步落盘，不阻塞 UI）。最小化/最大化时不覆盖，保留最近一次正常边界。</summary>
    public static void Save(Window window, ISettingsService settings)
    {
        if (window.WindowState != WindowState.Normal)
        {
            return;
        }

        var placement = new WindowPlacement(
            (int)window.Left,
            (int)window.Top,
            (int)Math.Round(window.ActualWidth),
            (int)Math.Round(window.ActualHeight),
            WindowState.Normal);

        // 设置服务内部走异步 SQLite；放线程池执行，规避 UI 线程同步等待造成死锁
        _ = Task.Run(async () =>
        {
            try
            {
                await settings.SetAsync(SettingKeys.WindowBounds, placement);
            }
            catch (Exception ex)
            {
                WorkNestLog.Warning("Window", "保存窗口位置失败", ex);
            }
        });
    }

    /// <summary>退出路径用的同步保存：线程池执行并限时等待，确保进程结束前落盘完成。</summary>
    public static void SaveAndWait(Window window, ISettingsService settings)
    {
        try
        {
            if (window.WindowState != WindowState.Normal)
            {
                return;
            }

            var placement = new WindowPlacement(
                (int)window.Left,
                (int)window.Top,
                (int)Math.Round(window.ActualWidth),
                (int)Math.Round(window.ActualHeight),
                WindowState.Normal);

            Task.Run(() => settings.SetAsync(SettingKeys.WindowBounds, placement)).Wait(1500);
        }
        catch (Exception ex)
        {
            WorkNestLog.Warning("Window", "退出保存窗口位置失败", ex);
        }
    }

    /// <summary>靠近虚拟屏幕左右边缘时自动吸附（决策 23），仅在普通状态下生效。</summary>
    public static void AttachEdgeSnap(Window window)
    {
        window.LocationChanged += (_, _) => Snap(window);
    }

    private static void Snap(Window window)
    {
        if (_snapping || window.WindowState != WindowState.Normal)
        {
            return;
        }

        var vsLeft = SystemParameters.VirtualScreenLeft;
        var vsRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;

        if (window.Left > vsLeft && window.Left - vsLeft <= SnapTolerance)
        {
            _snapping = true;
            window.Left = vsLeft;
            _snapping = false;
            return;
        }

        var right = window.Left + window.ActualWidth;
        if (right < vsRight && vsRight - right <= SnapTolerance)
        {
            _snapping = true;
            window.Left = vsRight - window.ActualWidth;
            _snapping = false;
        }
    }
}
