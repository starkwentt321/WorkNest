using WorkNest.Domain;

namespace WorkNest.Application.Dtos;

/// <summary>新增或编辑资源的输入；Id 为空表示新增。</summary>
public sealed class ResourceEditInput
{
    public int? Id { get; set; }

    /// <summary>本次添加/编辑所属的工作区。</summary>
    public int WorkspaceId { get; set; }

    public ResourceType Type { get; set; }

    /// <summary>显示名称；留空时按目标自动提取。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>用户输入的原始目标；保存前会规范化。</summary>
    public string Target { get; set; } = string.Empty;

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>标签，以逗号/空格分隔输入后由界面拆分。</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];
}
