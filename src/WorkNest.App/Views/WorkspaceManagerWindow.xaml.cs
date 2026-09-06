using System.Windows;
using WorkNest.App.Themes;
using WorkNest.App.ViewModels;

namespace WorkNest.App.Views;

/// <summary>工作区管理对话框：业务在 WorkspaceManagerViewModel，关闭后由主窗口刷新下拉。</summary>
public partial class WorkspaceManagerWindow : Window
{
    public WorkspaceManagerViewModel ViewModel { get; }

    public WorkspaceManagerWindow(WorkspaceManagerViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ThemeManager.Apply(this, ThemeManager.CurrentTheme);
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
}
