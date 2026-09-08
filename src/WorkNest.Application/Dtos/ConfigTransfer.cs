namespace WorkNest.Application.Dtos;

/// <summary>同名工作区冲突时的导入处理策略（决策 74/84/85/116）。</summary>
public enum ImportStrategy
{
    /// <summary>保留现有工作区，仅把缺失的资源并入；已有关联的置顶/顺序不动。</summary>
    Merge,

    /// <summary>创建“名称 (副本)”工作区并照搬结构，资源仍按唯一键复用，不复制真实文件（决策 116）。</summary>
    Copy,

    /// <summary>跳过该工作区，不做任何修改。</summary>
    Skip,

    /// <summary>清空现有工作区的资源关联后按导出内容重建；执行前自动创建当前状态安全快照（决策 85）。</summary>
    Overwrite,
}

/// <summary>导出 JSON 中的单个资源条目（决策 96：明文可编辑，可能包含真实路径与网址）。</summary>
public sealed class ExportedResource
{
    /// <summary>ResourceType 枚举名（Directory/File/Program/Website）。</summary>
    public required string Type { get; init; }

    public required string Name { get; init; }

    public required string Target { get; init; }

    public string Arguments { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public bool IsPinned { get; init; }

    /// <summary>置顶组内顺序；非置顶为 0。</summary>
    public int SortOrder { get; init; }
}

/// <summary>导出 JSON 中的单个工作区。</summary>
public sealed class ExportedWorkspace
{
    public required string Name { get; init; }

    /// <summary>工作区颜色（#RRGGBB）；允许为空，导入时回退默认配色。</summary>
    public string Color { get; init; } = string.Empty;

    public IReadOnlyList<ExportedResource> Resources { get; init; } = [];
}

/// <summary>导出 JSON 文件根结构（决策 63：含 schemaVersion，导入前校验）。</summary>
public sealed class ExportedConfigFile
{
    /// <summary>当前支持的 schema 版本；更高版本导入时拒绝并说明原因。</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTime ExportedAt { get; init; }

    public IReadOnlyList<ExportedWorkspace> Workspaces { get; init; } = [];
}

/// <summary>单个导入工作区的预览行 + 用户选择的处理策略（决策 75/84）。</summary>
public sealed class ImportWorkspacePreview
{
    /// <summary>导入文件中的原始工作区数据。</summary>
    public required ExportedWorkspace Data { get; init; }

    public required string Name { get; init; }

    /// <summary>与现有工作区同名冲突。</summary>
    public required bool ConflictsWithExisting { get; init; }

    /// <summary>按唯一键判断需要全新建档的资源数（无效目标不计入）。</summary>
    public required int NewResourceCount { get; init; }

    /// <summary>按唯一键可复用已有 ResourceItem 的资源数（无效目标不计入）。</summary>
    public required int ReusableCount { get; init; }

    /// <summary>用户选择的处理策略；无冲突时固定 Merge 且界面不可改。</summary>
    public ImportStrategy Strategy { get; set; } = ImportStrategy.Merge;

    /// <summary>规范化失败、无法导入的资源条数（仅提示，不阻断其他条目）。</summary>
    public int InvalidCount { get; init; }
}

/// <summary>导入预览（决策 75）；ExecuteAsync 以它为执行计划。</summary>
public sealed class ImportPreview
{
    public required string FilePath { get; init; }

    public required int SchemaVersion { get; init; }

    public required List<ImportWorkspacePreview> Workspaces { get; init; }

    /// <summary>任一工作区选择了覆盖导入：需要二次确认并自动创建安全快照（决策 85）。</summary>
    public bool HasOverwrite => Workspaces.Any(w => w.Strategy == ImportStrategy.Overwrite);
}
