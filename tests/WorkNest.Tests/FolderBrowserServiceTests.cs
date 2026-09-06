using System.IO;
using WorkNest.Application.Services;
using WorkNest.Application.Validation;
using Xunit;

namespace WorkNest.Tests;

/// <summary>
/// 文件夹浏览用例：在真实临时目录上验证枚举顺序（目录在前、名称升序）、
/// 隐藏项包含、目录缺失校验与超大目录截断。
/// </summary>
public sealed class FolderBrowserServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("worknest-browse-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task ListAsync_DirectoriesFirst_ThenFilesByName()
    {
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "z.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        var service = new FolderBrowserService();

        var listing = await service.ListAsync(_root);

        Assert.False(listing.Truncated);
        Assert.Equal(["alpha", "beta", "a.txt", "z.txt"], listing.Entries.Select(e => e.Name).ToArray());
        Assert.True(listing.Entries[0].IsDirectory);
        Assert.True(listing.Entries[1].IsDirectory);
        Assert.False(listing.Entries[2].IsDirectory);
        var file = listing.Entries[2];
        Assert.Equal(Path.Combine(_root, "a.txt"), file.FullPath);
        Assert.Equal(1, file.Length);
    }

    [Fact]
    public async Task ListAsync_IncludesHiddenEntries()
    {
        var hidden = Path.Combine(_root, "hidden.txt");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var service = new FolderBrowserService();

        var listing = await service.ListAsync(_root);

        // 面向开发场景：隐藏/系统项不筛除（用户确认的行为）
        Assert.Contains(listing.Entries, e => e.Name == "hidden.txt");
    }

    [Fact]
    public async Task ListAsync_MissingDirectory_ThrowsValidation()
    {
        var service = new FolderBrowserService();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.ListAsync(Path.Combine(_root, "不存在")));

        Assert.Contains("目录不存在", ex.Message);
    }

    [Fact]
    public async Task ListAsync_TruncatesAtConfiguredLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), "x");
        }
        var service = new FolderBrowserService(maxEntries: 3);

        var listing = await service.ListAsync(_root);

        Assert.True(listing.Truncated);
        Assert.Equal(3, listing.Entries.Count);
    }
}
