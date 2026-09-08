using System.Windows;
using WorkNest.App.Services;
using WorkNest.App.Themes;
using WorkNest.App.ViewModels;

namespace WorkNest.App.Views;

/// <summary>设置窗口（F22/决策 87）：常规、外观、快捷键、数据与备份、关于。</summary>
public partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }
    public WorkNest.App.Services.BackgroundImageSettings BackgroundSettings { get; }

    private readonly Win32FilePicker _filePicker = new();

    public SettingsWindow(SettingsViewModel viewModel, WorkNest.App.Services.BackgroundImageSettings backgroundSettings)
    {
        BackgroundSettings = backgroundSettings;
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ViewModel.RequestExport += OnRequestExport;
        ViewModel.RequestImport += OnRequestImport;
        ThemeManager.Apply(this, ThemeManager.CurrentTheme);
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var path = _filePicker.PickFile("选择背景图片",
            "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif", this);
        if (path is not null)
        {
            BackgroundSettings.ImagePath = path;
        }
    }

    /// <summary>导出配置（F11）：打开导出对话框；完成后无需刷新主窗口。</summary>
    private void OnRequestExport(object? sender, EventArgs e)
    {
        var viewModel = ViewModel.CreateExportViewModel();
        var window = new ExportWindow(viewModel) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>导入配置（F11）：选文件 → 预览对话框；导入成功后刷新备份列表回显。</summary>
    private void OnRequestImport(object? sender, EventArgs e)
    {
        var path = _filePicker.PickFile("选择要导入的 WorkNest 配置文件", "JSON 配置 (*.json)|*.json", this);
        if (path is null)
        {
            return;
        }

        var viewModel = ViewModel.CreateImportPreviewViewModel(path);
        var window = new ImportPreviewWindow(viewModel) { Owner = this };
        window.ShowDialog();
    }
}
