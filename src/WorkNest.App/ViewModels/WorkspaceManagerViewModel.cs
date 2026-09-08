using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Validation;
using WorkNest.App.Media;
using WorkNest.App.Services;

namespace WorkNest.App.ViewModels;

/// <summary>
/// 工作区管理对话框视图模型：新建（内联输入）、重命名、删除、上移/下移与恢复最近使用排序（F01/决策 45/49）。
/// 结构性变化即时持久化；上移/下移通过 SetManualOrderAsync 保存全部工作区当前顺序。
/// </summary>
public partial class WorkspaceManagerViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspaceService;
    private readonly IAppDialogs _dialogs;

    public WorkspaceManagerViewModel(IWorkspaceService workspaceService, IAppDialogs dialogs)
    {
        _workspaceService = workspaceService;
        _dialogs = dialogs;
    }

    public ObservableCollection<WorkspaceOptionViewModel> Workspaces { get; } = [];

    [ObservableProperty]
    private WorkspaceOptionViewModel? _selectedWorkspace;

    partial void OnSelectedWorkspaceChanged(WorkspaceOptionViewModel? value) => InvalidateCommands();

    [ObservableProperty]
    private string _newName = string.Empty;

    partial void OnNewNameChanged(string value) => InvalidateCommands();

    /// <summary>窗口内错误提示（决策 89：普通错误不弹窗）。</summary>
    [ObservableProperty]
    private string? _errorText;

    public async Task LoadAsync()
    {
        var keepId = SelectedWorkspace?.Workspace?.Id;
        IReadOnlyList<WorkNest.Application.Dtos.WorkspaceDto> dtos = [];
        try
        {
            dtos = await _workspaceService.GetOrderedAsync();
            ErrorText = null;
        }
        catch (Exception ex)
        {
            ErrorText = $"加载工作区失败：{ex.Message}";
        }
        Workspaces.Clear();
        foreach (var dto in dtos)
        {
            Workspaces.Add(new WorkspaceOptionViewModel(dto));
        }
        if (keepId is { } id)
        {
            SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Workspace?.Id == id);
        }
        InvalidateCommands();
    }

    private bool CanCreate() => !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var name = NewName.Trim();
        try
        {
            await _workspaceService.CreateAsync(name);
            NewName = string.Empty;
            ErrorText = null;
            await LoadAsync();
            // 选到新建的工作区（GetOrderedAsync 后按名称匹配最新项）
            SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Name == name) ?? Workspaces.FirstOrDefault(w => !w.IsManageItem);
        }
        catch (ValidationException vex)
        {
            ErrorText = vex.Message;
        }
        catch (Exception ex)
        {
            ErrorText = $"创建失败：{ex.Message}";
        }
    }

    private bool CanRename() => SelectedWorkspace?.Workspace is not null && !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        var ws = SelectedWorkspace?.Workspace;
        if (ws is null)
        {
            return;
        }
        try
        {
            await _workspaceService.RenameAsync(ws.Id, NewName.Trim());
            NewName = string.Empty;
            ErrorText = null;
            await LoadAsync();
        }
        catch (ValidationException vex)
        {
            ErrorText = vex.Message;
        }
        catch (Exception ex)
        {
            ErrorText = $"重命名失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        var ws = SelectedWorkspace?.Workspace;
        if (ws is null)
        {
            return;
        }
        // 删除确认并列出资源数（7.2：共享资源不受影响）；模态归属由 IAppDialogs 实现内解析
        var message = $"确定删除工作区“{ws.Name}”吗？\n其中 {ws.ResourceCount} 个资源关联将一并移除；被多个工作区共享的资源在其他工作区仍保留。";
        if (!_dialogs.Confirm("删除工作区", message, MessageBoxImage.Warning))
        {
            return;
        }
        try
        {
            await _workspaceService.DeleteAsync(ws.Id);
            SelectedWorkspace = null;
            ErrorText = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = $"删除失败：{ex.Message}";
        }
    }

    private bool CanDelete() => SelectedWorkspace?.Workspace is not null;

    private bool CanMoveUp() =>
        SelectedWorkspace?.Workspace is not null && Workspaces.IndexOf(SelectedWorkspace) > 0;

    private bool CanMoveDown() =>
        SelectedWorkspace?.Workspace is not null && Workspaces.IndexOf(SelectedWorkspace) < Workspaces.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private async Task MoveUpAsync() => await MoveAsync(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private async Task MoveDownAsync() => await MoveAsync(+1);

    private async Task MoveAsync(int delta)
    {
        var selected = SelectedWorkspace;
        if (selected?.Workspace is null)
        {
            return;
        }
        var index = Workspaces.IndexOf(selected);
        var target = index + delta;
        if (target < 0 || target >= Workspaces.Count)
        {
            return;
        }
        Workspaces.Move(index, target);
        SelectedWorkspace = Workspaces[target];
        await SaveManualOrderAsync();
    }

    /// <summary>把当前列表顺序保存为手动排序（决策 45/49：拖动/移动后切换手动模式）。</summary>
    private async Task SaveManualOrderAsync()
    {
        var orderedIds = Workspaces.Where(w => w.Workspace is not null).Select(w => w.Workspace!.Id).ToList();
        try
        {
            await _workspaceService.SetManualOrderAsync(orderedIds);
            ErrorText = null;
        }
        catch (Exception ex)
        {
            ErrorText = $"保存顺序失败：{ex.Message}";
        }
    }

    private bool CanRestoreLastUsed() => true;

    [RelayCommand(CanExecute = nameof(CanRestoreLastUsed))]
    private async Task RestoreLastUsedAsync()
    {
        try
        {
            await _workspaceService.UseLastUsedOrderAsync();
            ErrorText = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = $"恢复排序失败：{ex.Message}";
        }
    }

    /// <summary>命令可用性刷新：Toolkit 生成的 RelayCommand 不挂接 CommandManager.RequerySuggested，必须显式通知才会重新求值 CanExecute。</summary>
    private void InvalidateCommands()
    {
        CreateCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
}
