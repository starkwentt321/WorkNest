namespace WorkNest.Application.Dtos;

/// <summary>工作区列表项。</summary>
public sealed record WorkspaceDto(
    int Id,
    string Name,
    string Color,
    DateTime? LastUsedAt,
    int ResourceCount);
