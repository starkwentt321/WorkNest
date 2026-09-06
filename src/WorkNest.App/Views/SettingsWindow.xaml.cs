using System.Windows;
using Microsoft.Win32;
using WorkNest.App.Themes;
using WorkNest.App.ViewModels;

namespace WorkNest.App.Views;

/// <summary>设置窗口（F22/决策 87）：常规、外观、快捷键、数据与备份、关于。</summary>
public partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }
    public WorkNest.App.Services.BackgroundImageSettings BackgroundSettings { get; }

    public SettingsWindow(SettingsViewModel viewModel, WorkNest.App.Services.BackgroundImageSettings backgroundSettings)
    {
        BackgroundSettings = backgroundSettings;
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ViewModel.WindowOwner = this; // MessageBox/文件对话框保持模态归属
        ViewModel.RequestExport += OnRequestExport;
        ViewModel.RequestImport += OnRequestImport;
        ThemeManager.Apply(this, ThemeManager.CurrentTheme);
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
            BackgroundSettings.ImagePath = dialog.FileName;
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
        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的 WorkNest 配置文件",
            Filter = "JSON 配置 (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var viewModel = ViewModel.CreateImportPreviewViewModel(dialog.FileName);
        var window = new ImportPreviewWindow(viewModel) { Owner = this };
        window.ShowDialog();
    }
}
