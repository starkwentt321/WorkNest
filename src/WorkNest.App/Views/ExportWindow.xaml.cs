using System.Windows;
using WorkNest.App.Themes;
using WorkNest.App.ViewModels;

namespace WorkNest.App.Views;

/// <summary>导出配置对话框（F11/决策 64/96）：工作区勾选 + 导出位置。</summary>
public partial class ExportWindow : Window
{
    public ExportViewModel ViewModel { get; }

    public ExportWindow(ExportViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ThemeManager.Apply(this, ThemeManager.CurrentTheme);
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
}
