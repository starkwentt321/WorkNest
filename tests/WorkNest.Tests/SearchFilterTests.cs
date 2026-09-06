using WorkNest.Application.Dtos;
using WorkNest.Application.Validation;
using WorkNest.Domain;
using Xunit;

namespace WorkNest.Tests;

/// <summary>搜索过滤：纯函数测试，不依赖任何仓储。</summary>
public sealed class SearchFilterTests
{
    private static ResourceDto MakeDto(string name, string target, params string[] tags) => new()
    {
        Id = 1,
        Type = ResourceType.File,
        Name = name,
        Target = target,
        Tags = tags,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQuery_PassesEverything(string? query)
    {
        var item = MakeDto("报表", @"C:\data\report.xlsx", "工作");

        Assert.True(SearchFilter.Matches(item, query));
    }

    [Fact]
    public void MatchesName()
    {
        var item = MakeDto("项目周报", @"C:\docs\weekly.xlsx");

        Assert.True(SearchFilter.Matches(item, "周报"));
    }

    [Fact]
    public void MatchesTag()
    {
        var item = MakeDto("未命名", @"C:\x\a.txt", "工作", "重要");

        Assert.True(SearchFilter.Matches(item, "重要"));
    }

    [Fact]
    public void MatchesTargetPath()
    {
        var item = MakeDto("未命名", @"C:\tools\everything.exe");

        Assert.True(SearchFilter.Matches(item, "tools"));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var item = MakeDto("Notes", @"C:\Docs\note.txt");

        Assert.True(SearchFilter.Matches(item, "NOTES"));
        Assert.True(SearchFilter.Matches(item, "docs"));
    }

    [Fact]
    public void MultipleTokensRequireAllHits()
    {
        // AND 语义：名称命中 "note" 且标签命中 "work" 才通过
        var item = MakeDto("note-计划", @"C:\x.txt", "work");

        Assert.True(SearchFilter.Matches(item, "note work"));

        // 任一关键字未命中即排除
        Assert.False(SearchFilter.Matches(item, "note home"));
    }

    [Fact]
    public void NoHit_IsExcluded()
    {
        var item = MakeDto("报表", @"C:\data\report.xlsx");

        Assert.False(SearchFilter.Matches(item, "不存在词"));
    }
}
