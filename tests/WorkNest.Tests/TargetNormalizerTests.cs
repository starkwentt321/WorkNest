using WorkNest.Application.Validation;
using WorkNest.Domain;
using Xunit;

namespace WorkNest.Tests;

/// <summary>目标规范化：纯字符串/路径运算，不触碰文件系统。</summary>
public sealed class TargetNormalizerTests
{
    [Fact]
    public void RelativePath_BecomesFullPath()
    {
        var ok = TargetNormalizer.TryNormalize(ResourceType.File, @"logs\app.log", out var normalized, out var error);

        Assert.True(ok);
        Assert.Null(error);
        // 规范化后必须为绝对路径，供唯一键查重与启动使用
        Assert.True(Path.IsPathRooted(normalized));
        Assert.EndsWith(@"logs\app.log", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void SurroundingQuotes_AreStripped()
    {
        // 拖放/复制粘贴常带首尾引号，需剥离后再取完整路径
        var ok = TargetNormalizer.TryNormalize(ResourceType.File, @"""C:\tmp\a.txt""", out var normalized, out _);

        Assert.True(ok);
        Assert.Equal(@"C:\tmp\a.txt", normalized);
    }

    [Fact]
    public void SchemelessWebsite_DefaultsToHttps()
    {
        var ok = TargetNormalizer.TryNormalize(ResourceType.Website, "example.com", out var normalized, out _);

        Assert.True(ok);
        Assert.Equal("https://example.com/", normalized);
    }

    [Theory]
    [InlineData("http://example.com/x")]
    [InlineData("https://example.com/x")]
    public void HttpAndHttps_ArePreserved(string url)
    {
        var ok = TargetNormalizer.TryNormalize(ResourceType.Website, url, out var normalized, out _);

        Assert.True(ok);
        Assert.Equal(url, normalized);
    }

    [Fact]
    public void FtpScheme_IsRejected()
    {
        // 协议白名单：仅 http/https，避免误注册任意协议处理程序
        var ok = TargetNormalizer.TryNormalize(ResourceType.Website, "ftp://example.com", out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTarget_IsRejected(string raw)
    {
        var ok = TargetNormalizer.TryNormalize(ResourceType.File, raw, out _, out var error);

        Assert.False(ok);
        Assert.Equal("目标不能为空", error);
    }

    [Fact]
    public void SuggestName_Program_StripsExtension()
    {
        Assert.Equal("tool", TargetNormalizer.SuggestName(ResourceType.Program, @"C:\tools\tool.exe"));
    }

    [Fact]
    public void SuggestName_Directory_UsesDirectoryName()
    {
        Assert.Equal("projects", TargetNormalizer.SuggestName(ResourceType.Directory, @"C:\data\projects"));
    }

    [Fact]
    public void SuggestName_File_KeepsExtension()
    {
        Assert.Equal("a.pdf", TargetNormalizer.SuggestName(ResourceType.File, @"C:\docs\a.pdf"));
    }

    [Fact]
    public void SuggestName_Website_UsesHost()
    {
        Assert.Equal("www.example.com", TargetNormalizer.SuggestName(ResourceType.Website, "https://www.example.com/x"));
    }

    [Fact]
    public void SuggestName_LnkFile_StripsShortcutSuffix()
    {
        // 快捷方式本身作为目标：显示名不带 .lnk 后缀（拖入/新增/导入共用该推导）
        Assert.Equal("马来西亚天鹏", TargetNormalizer.SuggestName(ResourceType.File, @"E:\Desktop\马来西亚天鹏.lnk"));
        Assert.Equal("TOOL", TargetNormalizer.SuggestName(ResourceType.File, @"C:\tools\TOOL.LNK"));
        // 仅 .lnk：普通文件扩展名行为不变（a.pdf.lnk 剥离后为 a.pdf）
        Assert.Equal("a.pdf", TargetNormalizer.SuggestName(ResourceType.File, @"C:\docs\a.pdf.lnk"));
    }
}
