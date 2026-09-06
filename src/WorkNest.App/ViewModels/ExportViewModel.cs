using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;

namespace WorkNest.App.ViewModels;

/// <summary>
/// 配置导出对话框 VM（F11/决策 64/96）：勾选全部或部分工作区、选择导出位置。
/// 导出文件为明文 JSON 且包含真实路径，界面明确提示敏感信息。
/// </summary>
public partial class ExportViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspaceService;
    private readonly IExportService _exportService;

    public ExportViewModel(IWorkspaceService workspaceService, IExportService exportService)
    {
        _workspaceService = workspaceService;
        _exportService = exportService;
    }

    /// <summary>弹窗归属（ExportWindow 构造时注入）。</summary>
    internal System.Windows.Window? WindowOwner { get; set; }

    public ObservableCollection<ExportWorkspaceItem> Workspaces { get; } = [];

    [ObservableProperty]
    private string _exportPath = DefaultExportPath();

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isExporting;

    public async Task LoadAsync()
    {
        try
        {
            var dtos = await _workspaceService.GetOrderedAsync();
            foreach (var dto in dtos)
            {
                Workspaces.Add(new ExportWorkspaceItem(dto, isChecked: true)); // 默认全选（决策 64）
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"加载工作区失败：{ex.Message}";
        }
    }

    /// <summary>默认导出到桌面，带时间戳避免覆盖历史导出。</summary>
    private static string DefaultExportPath()
    {
        string directory;
        try
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Environment.CurrentDirectory;
            }
        }
        catch
        {
            directory = Environment.CurrentDirectory;
        }
        return Path.Combine(directory, $"WorkNest配置_{DateTime.Now:yyyyMMddHHmm}.json");
    }

    [RelayCommand]
    private void SelectAll() => SetAllChecked(true);

    [RelayCommand]
    private void SelectNone() => SetAllChecked(false);

    private void SetAllChecked(bool isChecked)
    {
        foreach (var item in Workspaces)
        {
            item.IsChecked = isChecked;
        }
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "选择导出位置",
            Filter = "JSON 配置 (*.json)|*.json",
            FileName = Path.GetFileName(ExportPath),
        };
        var directory = Path.GetDirectoryName(ExportPath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }
        if (dialog.ShowDialog(WindowOwner) == true)
        {
            ExportPath = dialog.FileName;
        }
    }

    private bool CanExport() => !IsExporting;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var selectedIds = Workspaces.Where(w => w.IsChecked).Select(w => w.Workspace.Id).ToList();
        if (selectedIds.Count == 0)
        {
            ErrorText = "请至少勾选一个工作区。";
            return;
        }
        var path = ExportPath.Trim();
        if (path.Length == 0)
        {
            ErrorText = "请选择导出文件位置。";
            return;
        }

        IsExporting = true;
        try
        {
            var count = await _exportService.ExportAsync(selectedIds, path);
            ResultText = $"已导出 {selectedIds.Count} 个工作区 / {count} 条资源关联：\n{path}";
            ErrorText = null;
        }
        catch (Exception ex)
        {
            ErrorText = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }
}

/// <summary>导出清单中的一行：工作区 + 勾选状态。</summary>
public partial class ExportWorkspaceItem : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public WorkspaceDto Workspace { get; }

    public ExportWorkspaceItem(WorkspaceDto workspace, bool isChecked)
    {
        Workspace = workspace;
        _isChecked = isChecked;
    }

    public string Name => Workspace.Name;

    public int ResourceCount => Workspace.ResourceCount;
}
