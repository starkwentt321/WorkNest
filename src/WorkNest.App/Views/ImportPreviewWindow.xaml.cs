using System.Windows;
using WorkNest.App.Themes;
using WorkNest.App.ViewModels;

namespace WorkNest.App.Views;

/// <summary>导入预览对话框（F11/决策 74/75/84/85）：统计预览 + 策略选择 + 执行。</summary>
public partial class ImportPreviewWindow : Window
{
    public ImportPreviewViewModel ViewModel { get; }

    public ImportPreviewWindow(ImportPreviewViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ViewModel.ImportCompleted += (_, _) => DialogResult = true; // 成功后关闭，由设置窗口统一收口
        ThemeManager.Apply(this, ThemeManager.CurrentTheme);
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
}
