using WorkNest.Application.Abstractions;
using WorkNest.Application.Dtos;
using WorkNest.Application.Validation;

namespace WorkNest.Application.Services;

/// <summary>
/// 文件夹浏览用例实现：后台线程枚举直接子项，目录在前、名称升序，包含隐藏/系统项。
/// 单次枚举有数量上限，超大目录截断展示避免界面长时间无响应。
/// </summary>
public sealed class FolderBrowserService : IFolderBrowserService
{
    /// <summary>默认单次展示上限；超出后停止枚举并标记 Truncated。</summary>
    public const int DefaultMaxEntries = 5000;

    private readonly int _maxEntries;

    public FolderBrowserService(int maxEntries = DefaultMaxEntries)
    {
        _maxEntries = maxEntries;
    }

    public async Task<FolderListingDto> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new ValidationException("目录不存在或无法访问");
        }

        return await Task.Run(() => ListCore(path, cancellationToken), cancellationToken);
    }

    private FolderListingDto ListCore(string path, CancellationToken cancellationToken)
    {
        List<FolderEntryDto> entries;
        try
        {
            entries = [];
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= _maxEntries)
                {
                    return new FolderListingDto(path, Sort(entries), Truncated: true);
                }

                if (info is DirectoryInfo directory)
                {
                    entries.Add(new FolderEntryDto(directory.Name, directory.FullName, true, 0, directory.LastWriteTime));
                }
                else
                {
                    var file = (FileInfo)info;
                    entries.Add(new FolderEntryDto(file.Name, file.FullName, false, file.Length, file.LastWriteTime));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 目录整体不可读（权限不足/介质错误）：以校验异常呈现，界面回显错误横幅
            throw new ValidationException($"无法读取目录内容：{ex.Message}");
        }

        return new FolderListingDto(path, Sort(entries), Truncated: false);
    }

    /// <summary>展示顺序：子目录在前，同类按名称不区分大小写升序。</summary>
    private static List<FolderEntryDto> Sort(List<FolderEntryDto> entries)
    {
        entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return entries;
    }
}
