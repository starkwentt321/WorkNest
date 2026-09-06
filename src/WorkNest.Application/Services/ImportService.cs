using System.Text.Json;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Validation;
using WorkNest.Domain;

namespace WorkNest.Application.Services;

/// <summary>
/// 配置导入用例（F11/决策 74/75/84/85/116）：
/// AnalyzeAsync 解析导出文件生成预览（新增/复用统计、同名冲突标记），
/// ExecuteAsync 按每工作区策略执行。含覆盖策略时先自动创建当前状态安全快照，
/// 快照失败则整体中止（决策 85）。资源按唯一键复用 ResourceItem，绝不复制真实文件（决策 116）。
/// </summary>
public sealed class ImportService : IImportService
{
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IResourceRepository _resourceRepository;
    private readonly IBackupService _backupService;
    private readonly IWorkspaceService _workspaceService;
    private readonly IClock _clock;

    public ImportService(
        IWorkspaceRepository workspaceRepository,
        IResourceRepository resourceRepository,
        IBackupService backupService,
        IWorkspaceService workspaceService,
        IClock clock)
    {
        _workspaceRepository = workspaceRepository;
        _resourceRepository = resourceRepository;
        _backupService = backupService;
        _workspaceService = workspaceService;
        _clock = clock;
    }

    public async Task<ImportPreview> AnalyzeAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        ExportedConfigFile? root;
        try
        {
            root = JsonSerializer.Deserialize<ExportedConfigFile>(json, ExportService.SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"导出文件不是有效的 JSON：{ex.Message}", ex);
        }

        if (root is null)
        {
            throw new InvalidDataException("导出文件内容为空或结构不正确");
        }

        // 决策 63：未知（高/低）版本一律拒绝，给出明确原因
        if (root.SchemaVersion != ExportedConfigFile.CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"导入文件的格式版本 {root.SchemaVersion} 不受支持（当前支持版本 {ExportedConfigFile.CurrentSchemaVersion}）。" +
                "请使用匹配版本的 WorkNest 重新导出。");
        }

        var existingNames = (await _workspaceRepository.GetAllAsync())
            .Select(w => w.Name)
            .ToHashSet(StringComparer.Ordinal);

        var previews = new List<ImportWorkspacePreview>();
        foreach (var workspace in root.Workspaces)
        {
            var newCount = 0;
            var reuseCount = 0;
            var invalidCount = 0;
            foreach (var resource in workspace.Resources)
            {
                if (!TryResolveKey(resource, out var key))
                {
                    invalidCount++;
                    continue;
                }

                // 统计口径与执行一致：唯一键命中即复用，未命中即新建（只统计不落库）
                if (await _resourceRepository.FindByKeyAsync(key) is null)
                {
                    newCount++;
                }
                else
                {
                    reuseCount++;
                }
            }

            previews.Add(new ImportWorkspacePreview
            {
                Data = workspace,
                Name = workspace.Name,
                ConflictsWithExisting = existingNames.Contains(workspace.Name),
                NewResourceCount = newCount,
                ReusableCount = reuseCount,
                InvalidCount = invalidCount,
            });
        }

        return new ImportPreview
        {
            FilePath = filePath,
            SchemaVersion = root.SchemaVersion,
            ExportedAt = root.ExportedAt,
            Workspaces = previews,
        };
    }

    public async Task<int> ExecuteAsync(ImportPreview preview)
    {
        if (preview.HasOverwrite)
        {
            // 决策 85：覆盖导入前先对当前状态做安全快照；快照失败异常上抛，整体中止
            await _backupService.CreateSnapshotAsync("pre-import");
        }

        var addedLinks = 0;
        // 失败语义：单个工作区的替换/合并在仓储层一个事务内原子完成，失败时该工作区保持导入前状态；
        // 已提交的先前工作区不回滚（有 pre-import 快照兜底），异常消息中报告受影响范围
        var completedWorkspaces = 0;
        try
        {
            foreach (var item in preview.Workspaces)
            {
                switch (item.Strategy)
                {
                    case ImportStrategy.Skip:
                        continue;

                    case ImportStrategy.Copy:
                        // 决策 116：副本只复制工作区结构与关联，资源仍按唯一键复用
                        var copy = await CreateWorkspaceAsync(await DeduplicateNameAsync($"{item.Name} (副本)"), item.Data.Color);
                        addedLinks += await ImportLinksAsync(copy.Id, item.Data, replaceExisting: false);
                        break;

                    case ImportStrategy.Overwrite:
                        var target = await FindByNameAsync(item.Name)
                            ?? throw new InvalidOperationException($"覆盖导入中止：工作区“{item.Name}”已不存在，请重新预览。");
                        // 先原子替换关联，成功后再改颜色：链接失败时工作区内容与外观都保持导入前状态
                        addedLinks += await ImportLinksAsync(target.Id, item.Data, replaceExisting: true);
                        if (!string.IsNullOrEmpty(item.Data.Color))
                        {
                            await ApplyColorAsync(target.Id, item.Data.Color);
                        }
                        break;

                    case ImportStrategy.Merge:
                    default:
                        // 无冲突 = 新建工作区；冲突 = 并入现有工作区（已有关联一律不动）
                        var workspace = item.ConflictsWithExisting
                            ? await FindByNameAsync(item.Name)
                              ?? throw new InvalidOperationException($"合并导入中止：工作区“{item.Name}”已不存在，请重新预览。")
                            : await CreateWorkspaceAsync(item.Name, item.Data.Color);
                        addedLinks += await ImportLinksAsync(workspace.Id, item.Data, replaceExisting: false);
                        break;
                }

                completedWorkspaces++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (completedWorkspaces > 0)
            {
                throw new InvalidOperationException(
                    $"导入在第 {completedWorkspaces + 1} 个工作区失败，前 {completedWorkspaces} 个工作区已导入并保持有效；" +
                    "可重新预览后再导入，或从恢复前快照还原。" + ex.Message, ex);
            }

            throw;
        }

        return addedLinks;
    }

    /// <summary>解析单条导入资源为唯一键；类型未知或目标无法规范化时返回 false（计入无效数）。</summary>
    private static bool TryResolveKey(ExportedResource resource, out ResourceKey key)
    {
        key = new ResourceKey(default, string.Empty, null, null);
        if (!Enum.TryParse<ResourceType>(resource.Type, ignoreCase: true, out var type))
        {
            return false;
        }

        if (!TargetNormalizer.TryNormalize(type, resource.Target, out var normalized, out _))
        {
            return false;
        }

        // 键语义与 ResourceService.BuildKey 一致：程序含参数与工作目录，其余只看目标
        key = new ResourceKey(type, normalized, resource.Arguments, resource.WorkingDirectory);
        return true;
    }

    /// <summary>
    /// 把整个工作区的导入资源转为待落库清单，交由仓储在单个事务内完成：
    /// 唯一键命中即复用、缺失才建档、缺失才加关联（合并语义，决策 84）。
    /// 无效条目在预览中已计数，执行阶段静默跳过。返回实际新增的关联条数。
    /// </summary>
    private async Task<int> ImportLinksAsync(int workspaceId, ExportedWorkspace data, bool replaceExisting)
    {
        var links = new List<PendingResourceLink>();
        foreach (var resource in data.Resources)
        {
            if (!TryResolveKey(resource, out var key))
            {
                continue;
            }

            var name = resource.Name.Trim();
            if (name.Length == 0)
            {
                // 与 ResourceService.AddAsync 相同：名称留空时按目标自动提取（决策 61）
                name = TargetNormalizer.SuggestName(key.Type, key.Target);
            }

            links.Add(new PendingResourceLink(
                new ResourceItem
                {
                    Type = key.Type,
                    Name = name,
                    Target = key.Target,
                    Arguments = resource.Arguments,
                    WorkingDirectory = resource.WorkingDirectory,
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow,
                },
                resource.Tags,
                resource.IsPinned,
                resource.SortOrder));
        }

        return await _resourceRepository.ImportLinksAsync(workspaceId, links, replaceExisting);
    }

    /// <summary>经 IWorkspaceService 建工作区（唯一性校验 + 自动配色），导入颜色非空时覆盖。</summary>
    private async Task<Workspace> CreateWorkspaceAsync(string name, string color)
    {
        var dto = await _workspaceService.CreateAsync(name);
        if (!string.IsNullOrEmpty(color))
        {
            await ApplyColorAsync(dto.Id, color);
        }

        return (await _workspaceRepository.GetAsync(dto.Id))!;
    }

    /// <summary>Copy 策略的去重命名：“名称 (副本)”，被占用则递增后缀，与建库校验同为忽略大小写。</summary>
    private async Task<string> DeduplicateNameAsync(string baseName)
    {
        var candidate = baseName;
        for (var attempt = 2; ; attempt++)
        {
            var taken = (await _workspaceRepository.GetAllAsync())
                .Any(w => string.Equals(w.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (!taken)
            {
                return candidate;
            }

            candidate = $"{baseName}{attempt}";
        }
    }

    private async Task<Workspace?> FindByNameAsync(string name)
    {
        var all = await _workspaceRepository.GetAllAsync();
        return all.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.Ordinal));
    }

    /// <summary>覆盖工作区颜色：取实体改 Color 后整体更新。</summary>
    private async Task ApplyColorAsync(int workspaceId, string color)
    {
        var entity = await _workspaceRepository.GetAsync(workspaceId);
        if (entity is null)
        {
            return;
        }

        entity.Color = color;
        await _workspaceRepository.UpdateAsync(entity);
    }
}
