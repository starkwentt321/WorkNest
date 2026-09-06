using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;

namespace WorkNest.Application.Services;

/// <summary>
/// 配置导出用例（F11/决策 63/64/96）：把选定工作区导出为明文 JSON 文件。
/// JSON 保持可编辑（中文与路径不做 \u 转义），文件包含真实路径与网址等敏感信息，界面须提示。
/// </summary>
public sealed class ExportService : IExportService
{
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IResourceRepository _resourceRepository;

    public ExportService(IWorkspaceRepository workspaceRepository, IResourceRepository resourceRepository)
    {
        _workspaceRepository = workspaceRepository;
        _resourceRepository = resourceRepository;
    }

    /// <summary>
    /// 导出序列化选项：缩进排版 + camelCase + 中文/路径原样可读（决策 96“明文可编辑”）。
    /// 同一组选项用于导入反序列化：大小写不敏感，兼容用户手改后的文件。
    /// </summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<int> ExportAsync(IReadOnlyList<int> workspaceIds, string filePath)
    {
        var idSet = workspaceIds.Distinct().ToHashSet();
        var all = await _workspaceRepository.GetAllAsync();

        var workspaces = new List<ExportedWorkspace>();
        var linkCount = 0;
        foreach (var workspace in all.Where(w => idSet.Contains(w.Id)))
        {
            var rows = await _resourceRepository.GetForWorkspaceAsync(workspace.Id);
            workspaces.Add(new ExportedWorkspace
            {
                Name = workspace.Name,
                Color = workspace.Color,
                Resources = rows.Select(ToExportedResource).ToList(),
            });
            linkCount += rows.Count;
        }

        var root = new ExportedConfigFile
        {
            ExportedAt = DateTime.UtcNow,
            Workspaces = workspaces,
        };

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // UTF-8 不带 BOM，保证常见文本编辑器与导入端读取一致
        await File.WriteAllTextAsync(
            filePath,
            JsonSerializer.Serialize(root, SerializerOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return linkCount;
    }

    /// <summary>关联行 → 导出条目：可选字段 null 归一为空串，保证 JSON 语义简单。</summary>
    private static ExportedResource ToExportedResource(ResourceLinkRow row) => new()
    {
        Type = row.Item.Type.ToString(),
        Name = row.Item.Name,
        Target = row.Item.Target,
        Arguments = row.Item.Arguments ?? string.Empty,
        WorkingDirectory = row.Item.WorkingDirectory ?? string.Empty,
        Tags = row.Tags,
        IsPinned = row.IsPinned,
        SortOrder = row.LinkSortOrder,
    };
}
