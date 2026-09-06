using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkNest.Application.Abstractions;
using WorkNest.App.Themes;

namespace WorkNest.App.ViewModels;

/// <summary>
/// 设置窗口视图模型（F22/决策 87）：常规（关闭行为/开机启动）、外观（主题）、
/// 快捷键（可配置，F10/决策 12/8.2）、数据与备份（备份管理 F21/决策 76/86 + 导入导出 F11）、关于。
/// 设置项变更即持久化；普通错误在窗口内提示（决策 89）。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IAutostartService _autostartService;
    private readonly IBackupService _backupService;
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly IExportService _exportService;
    private readonly IImportService _importService;
    private readonly IWorkspaceService _workspaceService;
    private readonly IAppRestart _appRestart;

    public SettingsViewModel(
        ISettingsService settingsService,
        IAutostartService autostartService,
        IBackupService backupService,
        IGlobalHotkeyService hotkeyService,
        IExportService exportService,
        IImportService importService,
        IWorkspaceService workspaceService,
        IAppRestart appRestart)
    {
        _settingsService = settingsService;
        _autostartService = autostartService;
        _backupService = backupService;
        _hotkeyService = hotkeyService;
        _exportService = exportService;
        _importService = importService;
        _workspaceService = workspaceService;
        _appRestart = appRestart;
    }

    /// <summary>弹窗归属：SettingsWindow 构造时注入，保证 MessageBox/文件对话框保持模态。</summary>
    internal Window? WindowOwner { get; set; }

    // ============ 常规 ============

    /// <summary>关闭按钮行为：true = 隐藏到托盘（首次默认，决策 3）。</summary>
    [ObservableProperty]
    private bool _closeToTray = true;

    partial void OnCloseToTrayChanged(bool value) => _ = SaveAsync(SettingKeys.CloseToTray, value);

    /// <summary>开机启动（当前 Windows 用户，决策 82）。</summary>
    [ObservableProperty]
    private bool _autostart;

    partial void OnAutostartChanged(bool value)
    {
        try
        {
            _autostartService.SetEnabled(value); // 以注册表为准写入
        }
        catch (Exception ex)
        {
            ErrorText = $"设置开机启动失败：{ex.Message}";
            return;
        }
        _ = SaveAsync(SettingKeys.Autostart, value);
    }

    // ============ 外观 ============

    /// <summary>双击文件夹资源在应用内浏览（否则系统资源管理器打开，默认关闭保持旧行为）。</summary>
    [ObservableProperty]
    private bool _folderInlineBrowse;

    partial void OnFolderInlineBrowseChanged(bool value) => _ = SaveAsync(SettingKeys.FolderInlineBrowse, value);

    /// <summary>当前主题（"light"/"dark"，决策 13/30）。</summary>
    [ObservableProperty]
    private string _theme = ThemeManager.Light;

    partial void OnThemeChanged(string value)
    {
        // 深浅主题同键位，切换 = 换各窗口合并字典（决策 18）
        ThemeManager.ApplyToAll(value);
        _ = SaveAsync(SettingKeys.Theme, value);
    }

    /// <summary>主题选项（浅色/深色均可用：深色为同键位完整实现）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ThemeOptions { get; } =
    [
        new(ThemeManager.Light, "浅色"),
        new(ThemeManager.Dark, "深色"),
    ];

    // ============ 快捷键（F10/决策 12/8.2） ============

    /// <summary>修饰键与键位选择状态；应用时才注册并持久化，失败保留原快捷键。</summary>
    [ObservableProperty] private bool _hotkeyCtrl = true;
    [ObservableProperty] private bool _hotkeyAlt = true;
    [ObservableProperty] private bool _hotkeyShift;
    [ObservableProperty] private bool _hotkeyWin;
    [ObservableProperty] private string _hotkeyKeyDisplay = "W";
    [ObservableProperty] private string? _hotkeyStatusText;

    partial void OnHotkeyCtrlChanged(bool value) => OnPropertyChanged(nameof(HotkeyPreview));
    partial void OnHotkeyAltChanged(bool value) => OnPropertyChanged(nameof(HotkeyPreview));
    partial void OnHotkeyShiftChanged(bool value) => OnPropertyChanged(nameof(HotkeyPreview));
    partial void OnHotkeyWinChanged(bool value) => OnPropertyChanged(nameof(HotkeyPreview));
    partial void OnHotkeyKeyDisplayChanged(string value) => OnPropertyChanged(nameof(HotkeyPreview));

    /// <summary>当前组合的预览文本。</summary>
    public string HotkeyPreview
    {
        get
        {
            var parts = new List<string>();
            if (HotkeyCtrl) parts.Add("Ctrl");
            if (HotkeyAlt) parts.Add("Alt");
            if (HotkeyShift) parts.Add("Shift");
            if (HotkeyWin) parts.Add("Win");
            parts.Add(HotkeyKeyDisplay);
            return string.Join("+", parts);
        }
    }

    /// <summary>可选键位：字母 / 数字 / F1-F12。</summary>
    public IReadOnlyList<string> HotkeyKeyOptions { get; } = BuildKeyOptions();

    private static IReadOnlyList<string> BuildKeyOptions()
    {
        var keys = new List<string>();
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            keys.Add(letter.ToString());
        }
        for (var digit = 0; digit <= 9; digit++)
        {
            keys.Add(((char)('0' + digit)).ToString());
        }
        for (var fn = 1; fn <= 12; fn++)
        {
            keys.Add($"F{fn}");
        }
        return keys;
    }

    /// <summary>数字键显示名 → 手势键位名：平台解析用 WinForms.Keys 枚举名，数字须存 "D0".."D9"。</summary>
    private static string ToGestureKey(string display) =>
        display.Length == 1 && char.IsAsciiDigit(display[0]) ? "D" + display : display;

    /// <summary>手势串 → 复选框状态（LoadAsync 回填用）；无法解析时返回 null。</summary>
    private static (bool Ctrl, bool Alt, bool Shift, bool Win, string KeyDisplay)? ParseGesture(string gesture)
    {
        bool ctrl = false, alt = false, shift = false, win = false;
        string? key = null;
        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (key is not null)
                    {
                        return null; // 多于一个键位，不符合约定
                    }
                    key = part.Length == 2 && part[0] is 'd' or 'D' && char.IsAsciiDigit(part[1])
                        ? part[1].ToString() // "D5" → 显示 "5"
                        : part;
                    break;
            }
        }

        return key is null || (!ctrl && !alt && !shift && !win) ? null : (ctrl, alt, shift, win, key);
    }

    [RelayCommand]
    private async Task ApplyHotkeyAsync()
    {
        var gesture = HotkeyPreview;
        if (HotkeyCtrl is false && HotkeyAlt is false && HotkeyShift is false && HotkeyWin is false)
        {
            ErrorText = "全局快捷键至少需要包含一个修饰键（Ctrl/Alt/Shift/Win）。";
            return;
        }

        // 手势串用 ToGestureKey 归一数字键位
        var normalized = string.Join("+", gesture.Split('+')
            .Select(p => p == HotkeyKeyDisplay ? ToGestureKey(p) : p));
        if (!_hotkeyService.TryRegister(normalized))
        {
            // 提示以服务实际生效状态为准，不得谎称“已保留原快捷键”
            var actual = _hotkeyService.RegisteredGesture;
            ErrorText = actual is null
                ? "快捷键注册失败（可能已被其他程序占用），当前没有生效的全局快捷键；托盘入口不受影响，可更换组合重试。"
                : $"快捷键 {normalized} 注册失败（可能已被其他程序占用），已保持原快捷键 {actual} 生效；托盘入口不受影响，可更换组合重试。";
            return;
        }

        HotkeyStatusText = $"已应用：{normalized}";
        ErrorText = null;
        await SaveAsync(SettingKeys.Hotkey, normalized);
    }

    [RelayCommand]
    private async Task ResetHotkeyAsync()
    {
        HotkeyCtrl = true;
        HotkeyAlt = true;
        HotkeyShift = false;
        HotkeyWin = false;
        HotkeyKeyDisplay = "W";
        OnPropertyChanged(nameof(HotkeyPreview));
        await ApplyHotkeyAsync();
    }

    // ============ 数据与备份（F21/决策 76/86 + F11） ============

    /// <summary>窗口内错误提示（决策 89）。</summary>
    [ObservableProperty]
    private string? _errorText;

    /// <summary>最近一次备份结果（路径回显）。</summary>
    [ObservableProperty]
    private string? _backupResultText;

    /// <summary>备份文件列表（F21 查看）。</summary>
    public ObservableCollection<BackupInfo> Backups { get; } = [];

    [ObservableProperty]
    private BackupInfo? _selectedBackup;

    partial void OnSelectedBackupChanged(BackupInfo? value) => RestoreBackupCommand.NotifyCanExecuteChanged();

    /// <summary>导入/导出对话框请求（View 层订阅并创建窗口）。</summary>
    public event EventHandler? RequestExport;
    public event EventHandler? RequestImport;

    public async Task LoadAsync()
    {
        try
        {
            CloseToTray = await _settingsService.GetAsync(SettingKeys.CloseToTray, true);
            Autostart = _autostartService.IsEnabled(); // 以注册表实际状态为准
            Theme = await _settingsService.GetAsync(SettingKeys.Theme, ThemeManager.Light);
            FolderInlineBrowse = await _settingsService.GetAsync(SettingKeys.FolderInlineBrowse, false);

            var gesture = await _settingsService.GetAsync(SettingKeys.Hotkey, "Ctrl+Alt+W");
            if (ParseGesture(gesture) is { } parsed)
            {
                HotkeyCtrl = parsed.Ctrl;
                HotkeyAlt = parsed.Alt;
                HotkeyShift = parsed.Shift;
                HotkeyWin = parsed.Win;
                HotkeyKeyDisplay = parsed.KeyDisplay;
                OnPropertyChanged(nameof(HotkeyPreview));
            }

            await RefreshBackupsAsync();
            ErrorText = null;
        }
        catch (Exception ex)
        {
            ErrorText = $"读取设置失败：{ex.Message}";
        }
    }

    private async Task SaveAsync<T>(string key, T value)
    {
        try
        {
            await _settingsService.SetAsync(key, value);
        }
        catch (Exception ex)
        {
            ErrorText = $"保存设置失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        try
        {
            var path = await _backupService.CreateSnapshotAsync("manual");
            BackupResultText = $"备份完成：{path}";
            ErrorText = null;
            await RefreshBackupsAsync();
        }
        catch (Exception ex)
        {
            ErrorText = $"备份失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshBackupsAsync()
    {
        try
        {
            var list = await _backupService.ListBackupsAsync();
            Backups.Clear();
            foreach (var info in list)
            {
                Backups.Add(info);
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"读取备份列表失败：{ex.Message}";
        }
    }

    private bool CanRestoreBackup() => SelectedBackup is not null;

    [RelayCommand(CanExecute = nameof(CanRestoreBackup))]
    private async Task RestoreBackupAsync()
    {
        var backup = SelectedBackup;
        if (backup is null)
        {
            return;
        }

        var confirm = MessageBox.Show(WindowOwner,
            "将用所选备份覆盖当前数据。\n恢复前会自动创建当前状态的安全快照，恢复完成后应用将自动重启。\n\n确定恢复？",
            "恢复备份", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            // 决策 86：实现内部先对当前状态做安全快照，快照失败则中止恢复
            await _backupService.RestoreAsync(backup.FilePath);
            _appRestart.Restart(); // 整进程重启以释放数据库句柄并重载缓存
        }
        catch (Exception ex)
        {
            ErrorText = $"恢复备份失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void Export() => RequestExport?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Import() => RequestImport?.Invoke(this, EventArgs.Empty);

    /// <summary>导出对话框 VM 工厂（SettingsWindow.xaml.cs 调用）。</summary>
    public ExportViewModel CreateExportViewModel() => new(_workspaceService, _exportService);

    /// <summary>导入预览对话框 VM 工厂（SettingsWindow.xaml.cs 调用）。</summary>
    public ImportPreviewViewModel CreateImportPreviewViewModel(string filePath) => new(_importService, filePath);

    [RelayCommand]
    private void OpenLogs() => OpenDir(Path.Combine(GetDataRoot(), "Logs"));

    [RelayCommand]
    private void OpenData() => OpenDir(Path.Combine(GetDataRoot(), "Data"));

    /// <summary>用户数据根目录：%LocalAppData%\WorkNest（决策 100）。</summary>
    private static string GetDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkNest");

    private static void OpenDir(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打开目录失败不打断设置操作
        }
    }
}
