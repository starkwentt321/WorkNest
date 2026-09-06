using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WorkNest.App.Services;
using WorkNest.App.ViewModels;
using WorkNest.App.Views;
using WorkNest.Domain;

namespace WorkNest.App;

/// <summary>
/// 主窗口：XAML 事件转发与视图交互（文件对话框、置顶行拖动）在此处理，业务逻辑全部在 MainViewModel。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>主视图模型（组合根经此属性访问 InitialWorkspaceId / InitializeAsync）。</summary>
    public MainViewModel ViewModel { get; }

    // 置顶行拖动排序的状态
    private ResourceItemViewModel? _dragItem;
    private Point _dragStart;

    private const string PinnedDragFormat = "WorkNest.PinnedItemId";

    public BackgroundImageSettings BackgroundSettings { get; }

    public MainWindow(MainViewModel viewModel, BackgroundImageSettings backgroundSettings)
    {
        BackgroundSettings = backgroundSettings;
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel; // 组合根约定：public 属性名 ViewModel

        // 窗口按当前主题渲染（InitializeAsync 后按设置覆盖，默认浅色）
        Themes.ThemeManager.Apply(this, Themes.ThemeManager.CurrentTheme);

        ViewModel.RequestManageWorkspaces += OnRequestManageWorkspaces;
        StateChanged += MainWindow_StateChanged; // 最大化贴右（F17/决策 14）
        Closing += (_, e) =>
        {
            // 决策 72：编辑面板存在未保存修改时确认
            if (!ViewModel.ConfirmDiscardChanges())
            {
                e.Cancel = true;
            }
        };
    }

    // ===== 最大化贴右（F17/决策 14：侧边工具栏语义） =====
    //
    // 窗口受 MaxWidth=800 约束，"最大化"实际是"全高窄窗"；Win32 把被截断的
    // 最大化窗口默认摆到工作区左上角，违背停靠右侧的产品语义，这里重新定位。

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // 延迟到系统完成最大化摆位之后再平移，避免定位被后续摆位覆盖
            Dispatcher.BeginInvoke(new Action(MaximizeToRightEdge), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>把最大化窗框平移到窗口所在显示器工作区右缘（多显示器取就近显示器）。</summary>
    private void MaximizeToRightEdge()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || WindowState != WindowState.Maximized)
            {
                return;
            }

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out RECT frame))
            {
                return;
            }

            // 最大化窗框会向工作区四周外扩（典型 9px），按当前矩形推算外扩量；
            // 直接物理像素平移，不写 WPF Left/Top（避免污染还原边界 rcNormalPosition）
            var overhang = info.rcWork.Left - frame.Left;
            var frameWidth = frame.Right - frame.Left;
            var targetLeft = info.rcWork.Right + overhang - frameWidth;
            SetWindowPos(hwnd, IntPtr.Zero, targetLeft, frame.Top, 0, 0,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
        }
        catch
        {
            // 定位失败保持系统默认位置，不影响最大化本身
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;

    // ===== 顶部栏 =====

    /// <summary>新增按钮：弹出三入口菜单（决策 115）。</summary>
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(ViewModel.CreateSettingsViewModel(), BackgroundSettings) { Owner = this };
        window.ShowDialog();
        // 导入配置可能改变工作区与资源；恢复备份走整进程重启，无需在此处理。
        // 刷新下拉与重载资源必须顺序执行，并发触发会互相竞争当前选择（R07）
        _ = RefreshWorkspaceAndResourcesAsync();
        // 内嵌浏览开关无其他生效链路，设置窗口关闭后立即重读
        _ = ViewModel.RefreshInlineBrowseSettingAsync();
    }

    /// <summary>选中“管理工作区…”时打开管理对话框，关闭后刷新下拉（决策 26/44）。</summary>
    private void OnRequestManageWorkspaces(object? sender, EventArgs e)
    {
        var window = new WorkspaceManagerWindow(ViewModel.CreateWorkspaceManagerViewModel()) { Owner = this };
        window.ShowDialog();
        // R07：管理后“刷新下拉 → 回退有效选择 → 重载资源”作为一个顺序操作执行
        _ = RefreshWorkspaceAndResourcesAsync();
    }

    /// <summary>先刷新工作区下拉（内部完成选择回退），再按最终选择重载资源列表。</summary>
    private async Task RefreshWorkspaceAndResourcesAsync()
    {
        await ViewModel.ReloadWorkspacesAsync();
        await ViewModel.LoadResourcesAsync();
    }

    // ===== 列表 =====

    /// <summary>列头点击排序转发（决策 51）。</summary>
    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is GridViewColumnHeader header && header.Tag is string key)
        {
            ViewModel.ApplyColumnSortCommand.Execute(key);
        }
    }

    /// <summary>双击行打开（决策 11）：目录资源在开关开启时进应用内浏览，其余走系统启动（OpenCommand 内部分支）；
    /// 路径列双击由其 MouseBinding 处理“打开所在位置”（决策 52），此处跳过。</summary>
    private void ResourceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { Tag: "OpenLocationCell" })
        {
            return;
        }
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ResourceList, source) is ListViewItem { Content: ResourceItemViewModel item })
        {
            ViewModel.OpenCommand.Execute(item);
        }
    }

    // ===== 文件夹应用内浏览面板 =====

    /// <summary>双击子项：子目录深入、子文件交主 VM 用系统默认程序打开（OpenEntryCommand 内部分支）。</summary>
    private void FolderEntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.FolderBrowser is not { } browser)
        {
            return;
        }
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(FolderEntryList, source) is ListViewItem { Content: FolderEntryViewModel entry })
        {
            browser.OpenEntryCommand.Execute(entry);
        }
    }

    /// <summary>鼠标侧键导航：XButton1 后退、XButton2 前进（与浏览器/资源管理器习惯一致）。</summary>
    private void FolderBrowserPanel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.FolderBrowser is not { } browser)
        {
            return;
        }
        switch (e.ChangedButton)
        {
            case MouseButton.XButton1 when browser.CanGoBack:
                browser.GoBackCommand.Execute(null);
                e.Handled = true;
                break;
            case MouseButton.XButton2 when browser.CanGoForward:
                browser.GoForwardCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 右键子项弹出操作菜单：右键未选中项时改选为该项（多选集保留）。
    /// 刻意不用进程内 IContextMenu 系统菜单——实测本机该链路会被第三方 Shell 扩展
    /// 弄崩宿主进程，改用等价的稳定 API 子集（打开/定位/复制/重命名/回收站删除/属性）。
    /// </summary>
    private void FolderEntryList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.FolderBrowser is not { } browser || browser.IsBusy)
        {
            return;
        }
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(FolderEntryList, source) is not ListViewItem { Content: FolderEntryViewModel entry })
        {
            return; // 空白区右键不弹菜单
        }
        if (!FolderEntryList.SelectedItems.Contains(entry))
        {
            FolderEntryList.SelectedItem = entry;
        }
        var selected = FolderEntryList.SelectedItems
            .Cast<FolderEntryViewModel>()
            .ToList();

        var menu = BuildFolderEntryMenu(browser, selected);
        menu.PlacementTarget = FolderEntryList;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) => menu.PlacementTarget = null;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>按选中集构建浏览面板右键菜单。</summary>
    private ContextMenu BuildFolderEntryMenu(FolderBrowserViewModel browser, List<FolderEntryViewModel> selected)
    {
        var menu = new ContextMenu();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        var open = new MenuItem { Header = "打开" };
        open.Click += (_, _) => browser.OpenEntryCommand.Execute(selected[0]);
        menu.Items.Add(open);

        var reveal = new MenuItem { Header = "在资源管理器中显示" };
        reveal.Click += (_, _) =>
        {
            try
            {
                FolderItemOps.ShowInExplorer(selected[0].FullPath);
            }
            catch (Exception ex)
            {
                ViewModel.ShowError($"定位失败：{ex.Message}");
            }
        };
        menu.Items.Add(reveal);
        menu.Items.Add(new Separator());

        var copy = new MenuItem { Header = "复制", ToolTip = "复制选中的文件/文件夹" };
        copy.Click += (_, _) =>
        {
            var list = new System.Collections.Specialized.StringCollection();
            foreach (var item in selected)
            {
                list.Add(item.FullPath);
            }
            Clipboard.SetFileDropList(list);
        };
        menu.Items.Add(copy);

        var copyPath = new MenuItem
        {
            Header = selected.Count > 1 ? "复制路径" : "复制文件路径",
            ToolTip = "把完整路径复制到剪贴板",
        };
        copyPath.Click += (_, _) => Clipboard.SetText(string.Join("\n", selected.Select(x => x.FullPath)));
        menu.Items.Add(copyPath);
        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) =>
        {
            var dialog = new RenameWindow(selected[0].FullPath) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                browser.ScheduleRefreshAfterShellCommand(isRename: false);
            }
        };
        menu.Items.Add(rename);

        var delete = new MenuItem { Header = selected.Count > 1 ? $"删除 {selected.Count} 项" : "删除" };
        delete.Click += (_, _) =>
        {
            try
            {
                FolderItemOps.DeleteToRecycleBin(hwnd, selected.Select(x => x.FullPath).ToList());
            }
            catch (Exception ex)
            {
                ViewModel.ShowError($"删除失败：{ex.Message}");
            }
            browser.ScheduleRefreshAfterShellCommand(isRename: false);
        };
        menu.Items.Add(delete);

        if (selected.Count == 1)
        {
            var properties = new MenuItem { Header = "属性" };
            properties.Click += (_, _) =>
            {
                try
                {
                    FolderItemOps.ShowProperties(hwnd, selected[0].FullPath);
                }
                catch (Exception ex)
                {
                    ViewModel.ShowError($"打开属性失败：{ex.Message}");
                }
            };
            menu.Items.Add(properties);
        }

        return menu;
    }

    // ===== 编辑面板浏览（决策 115） =====

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        var panel = ViewModel.EditPanel;
        if (panel is null || panel.Type == ResourceType.Website)
        {
            return;
        }
        if (panel.Type == ResourceType.Directory)
        {
            var dialog = new OpenFolderDialog { Title = "选择文件夹" };
            if (dialog.ShowDialog(this) == true)
            {
                panel.Target = dialog.FolderName;
            }
        }
        else
        {
            var dialog = new OpenFileDialog { Title = "选择文件", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true)
            {
                panel.Target = dialog.FileName;
            }
        }
    }

    private void BrowseWorkingDir_Click(object sender, RoutedEventArgs e)
    {
        var panel = ViewModel.EditPanel;
        if (panel is null)
        {
            return;
        }
        var dialog = new OpenFolderDialog { Title = "选择工作目录" };
        if (dialog.ShowDialog(this) == true)
        {
            panel.WorkingDirectory = dialog.FolderName;
        }
    }

    // ===== 置顶行拖动排序（决策 32；仅当前工作区范围可用） =====

    private void ResourceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        if (ViewModel.SearchAll)
        {
            return; // 全部范围不承载单工作区置顶顺序
        }
        // 按命中容器判断（不依赖选中状态），且仅置顶行可拖
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ResourceList, source) is ListViewItem { Content: ResourceItemViewModel item } &&
            item.IsPinned)
        {
            _dragItem = item;
            _dragStart = e.GetPosition(ResourceList);
        }
    }

    private void ResourceList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var pos = e.GetPosition(ResourceList);
        // 超过系统拖动阈值才触发，避免误拖
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        var item = _dragItem;
        _dragItem = null;
        try
        {
            var data = new DataObject(PinnedDragFormat, item!.Id);
            DragDrop.DoDragDrop(ResourceList, data, DragDropEffects.Move);
        }
        catch
        {
            // 拖动被中断不影响列表状态
        }
    }

    /// <summary>
    /// 拖放悬停效果：外部文件/文件夹仅在普通“当前工作区”视图显示可放（Copy），
    /// 置顶行内部拖动显示 Move，其余来源一律禁止，Drop 由此先行把关。
    /// </summary>
    private void ResourceList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = ViewModel.CanAcceptExternalDrop ? DragDropEffects.Copy : DragDropEffects.None;
        }
        else if (e.Data.GetDataPresent(PinnedDragFormat))
        {
            e.Effects = ViewModel.SearchAll ? DragDropEffects.None : DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void ResourceList_Drop(object sender, DragEventArgs e)
    {
        // 外部文件/文件夹拖入：批量加入当前工作区（可接受性与效果已在 DragOver 把关）
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } droppedPaths)
        {
            _ = ViewModel.AddDroppedPathsAsync(droppedPaths);
            return;
        }
        if (ViewModel.SearchAll)
        {
            return;
        }
        if (e.Data.GetData(PinnedDragFormat) is not int sourceId)
        {
            return;
        }
        // 落点必须是置顶行；按落点上下半区决定插到目标前后
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ResourceList, source) is ListViewItem { Content: ResourceItemViewModel target } &&
            target.IsPinned)
        {
            var container = ItemsControl.ContainerFromElement(ResourceList, source) as ListViewItem;
            var insertAfter = container is not null && e.GetPosition(container).Y > container.ActualHeight / 2;
            _ = ViewModel.ReorderPinnedAsync(sourceId, target.Id, insertAfter);
        }
    }
}
