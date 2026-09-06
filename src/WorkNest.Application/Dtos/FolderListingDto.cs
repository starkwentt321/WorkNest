namespace WorkNest.Application.Dtos;

/// <summary>文件夹浏览面板中的单个子项（子目录或文件）。</summary>
public sealed record FolderEntryDto(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTime);

/// <summary>一次目录枚举结果；Truncated 表示超出单次上限只展示部分条目。</summary>
public sealed record FolderListingDto(string Path, IReadOnlyList<FolderEntryDto> Entries, bool Truncated);
