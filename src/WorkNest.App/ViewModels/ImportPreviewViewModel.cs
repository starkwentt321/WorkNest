using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;

namespace WorkNest.App.ViewModels;

/// <summary>
/// 导入预览对话框 VM（F11/决策 74/75/84/85）：解析导出文件显示新增/复用/冲突统计，
/// 用户为同名冲突工作区选择合并/创建副本/跳过/覆盖策略后执行。
/// 覆盖导入需二次确认；安全快照由 ImportService 内部保证。
/// </summary>
public partial class ImportPreviewViewModel : ObservableObject
{
    private readonly IImportService _importService;
    private readonly string _filePath;

    public ImportPreviewViewModel(IImportService importService, string filePath)
    {
        _importService = importService;
        _filePath = filePath;
    }

    /// <summary>弹窗归属（ImportPreviewWindow 构造时注入）。</summary>
    internal System.Windows.Window? WindowOwner { get; set; }

    public ObservableCollection<ImportWorkspaceItem> Workspaces { get; } = [];

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _summaryText;

    [ObservableProperty]
    private string? _resultText;

    /// <summary>解析中/失败时禁用执行按钮。</summary>
    [ObservableProperty]
    private bool _isReady;

    /// <summary>任一工作区选择覆盖导入：执行前需二次确认（决策 85）。</summary>
    [ObservableProperty]
    private bool _hasOverwrite;

    /// <summary>导入成功后通知窗口关闭（View 层订阅）。</summary>
    public event EventHandler? ImportCompleted;

    /// <summary>解析得到的文件格式版本（执行计划原样带回）。</summary>
    private int _schemaVersion;

    public async Task LoadAsync()
    {
        try
        {
            var preview = await _importService.AnalyzeAsync(_filePath);
            _schemaVersion = preview.SchemaVersion;
            foreach (var workspace in preview.Workspaces)
            {
                var item = new ImportWorkspaceItem(workspace);
                // 策略变化影响“覆盖导入”提示与执行计划的实时性
                item.PropertyChanged += (_, _) => RefreshOverwriteFlag();
                Workspaces.Add(item);
            }

            SummaryText = $"{Workspaces.Count} 个工作区：新增 {Workspaces.Sum(w => w.Source.NewResourceCount)}、" +
                          $"复用 {Workspaces.Sum(w => w.Source.ReusableCount)} 条资源" +
                          (Workspaces.Any(w => w.Source.InvalidCount > 0)
                              ? $"，{Workspaces.Sum(w => w.Source.InvalidCount)} 条无效条目将被跳过"
                              : string.Empty);
            IsReady = true;
            RefreshOverwriteFlag();
        }
        catch (Exception ex)
        {
            // 决策 63：版本不支持等原因在此以明确文案呈现，执行按钮保持禁用
            ErrorText = ex.Message;
            IsReady = false;
        }
    }

    private bool CanExecuteImport() => IsReady;

    [RelayCommand(CanExecute = nameof(CanExecuteImport))]
    private async Task ExecuteImportAsync()
    {
        // 以界面当前勾选的策略重建执行计划
        var preview = new ImportPreview
        {
            FilePath = _filePath,
            SchemaVersion = _schemaVersion,
            Workspaces = Workspaces.Select(w => w.Source).ToList(),
        };

        if (preview.HasOverwrite)
        {
            // 决策 85：覆盖导入显示影响范围并二次确认
            var overwriteNames = preview.Workspaces
                .Where(w => w.Strategy == ImportStrategy.Overwrite)
                .Select(w => w.Name);
            var confirm = System.Windows.MessageBox.Show(WindowOwner,
                "覆盖导入将清空以下工作区的现有资源关联，并按导入文件重建：\n" +
                string.Join("、", overwriteNames) +
                "\n\n执行前会自动创建当前状态的安全快照。确定继续？",
                "覆盖导入确认", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            var count = await _importService.ExecuteAsync(preview);
            ResultText = $"导入完成：已写入 {count} 条资源关联。";
            ErrorText = null;
            ImportCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorText = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>任一行策略变化后刷新覆盖提示的显隐。</summary>
    private void RefreshOverwriteFlag() =>
        HasOverwrite = Workspaces.Any(w => w.Source.Strategy == ImportStrategy.Overwrite);
}

/// <summary>导入预览中的一行：原始预览数据 + 界面绑定的策略选择。</summary>
public partial class ImportWorkspaceItem : ObservableObject
{
    /// <summary>底层预览数据（Strategy 可写，执行时直接以 Source 重建计划）。</summary>
    public ImportWorkspacePreview Source { get; }

    public ImportWorkspaceItem(ImportWorkspacePreview source)
    {
        Source = source;
        // 决策 84：仅同名冲突行开放全部策略；新工作区固定“新建”
        IReadOnlyList<KeyValuePair<ImportStrategy, string>> strategies;
        if (source.ConflictsWithExisting)
        {
            strategies =
            [
                new KeyValuePair<ImportStrategy, string>(ImportStrategy.Merge, "合并（缺的资源并入）"),
                new KeyValuePair<ImportStrategy, string>(ImportStrategy.Copy, "创建副本"),
                new KeyValuePair<ImportStrategy, string>(ImportStrategy.Skip, "跳过"),
                new KeyValuePair<ImportStrategy, string>(ImportStrategy.Overwrite, "覆盖（清空后重建）"),
            ];
        }
        else
        {
            strategies =
            [
                new KeyValuePair<ImportStrategy, string>(ImportStrategy.Merge, "新建工作区"),
            ];
        }

        Strategies = strategies;
        _selectedStrategy = strategies[0];
    }

    public string Name => Source.Name;

    /// <summary>冲突状态文案（决策 75）。</summary>
    public string StatusText => Source.ConflictsWithExisting ? "同名冲突" : "新工作区";

    public int NewCount => Source.NewResourceCount;

    public int ReuseCount => Source.ReusableCount;

    public int InvalidCount => Source.InvalidCount;

    /// <summary>非冲突行策略锁定为“新建工作区”（决策 84 只约束同名冲突）。</summary>
    public bool IsConflict => Source.ConflictsWithExisting;

    public IReadOnlyList<KeyValuePair<ImportStrategy, string>> Strategies { get; }

    [ObservableProperty]
    private KeyValuePair<ImportStrategy, string> _selectedStrategy;

    partial void OnSelectedStrategyChanged(KeyValuePair<ImportStrategy, string> value)
    {
        Source.Strategy = value.Key;
    }
}
