using WorkNest.Application.Dtos;
using WorkNest.Application.Validation;
using WorkNest.Domain;
using Xunit;

namespace WorkNest.Tests;

/// <summary>资源默认排序与列头排序：纯函数测试。</summary>
public sealed class ResourceOrderingTests
{
    private static ResourceDto MakeDto(
        string name,
        bool isPinned = false,
        int linkSortOrder = 0,
        int runCount = 0,
        DateTime? lastUsedAt = null) => new()
    {
        Id = 0,
        Type = ResourceType.File,
        Name = name,
        Target = @"C:\" + name,
        IsPinned = isPinned,
        LinkSortOrder = linkSortOrder,
        RunCount = runCount,
        LastUsedAt = lastUsedAt,
    };

    [Fact]
    public void PinnedGroup_ComesFirst_InLinkSortOrder()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = new[]
        {
            MakeDto("普通高 usage", runCount: 99, lastUsedAt: t1),   // 普通组即使使用最多也排置顶之后
            MakeDto("置顶乙", isPinned: true, linkSortOrder: 2),
            MakeDto("置顶甲", isPinned: true, linkSortOrder: 1),
        };

        var ordered = ResourceOrdering.ApplyDefault(items).ToList();

        Assert.Equal(["置顶甲", "置顶乙", "普通高 usage"], ordered.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void NormalGroup_OrdersByRunCountDescending()
    {
        var items = new[]
        {
            MakeDto("低频", runCount: 1),
            MakeDto("高频", runCount: 5),
            MakeDto("中频", runCount: 3),
        };

        var ordered = ResourceOrdering.ApplyDefault(items).ToList();

        Assert.Equal(["高频", "中频", "低频"], ordered.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void SameRunCount_OrdersByLastUsedDescending()
    {
        var early = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var late = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var items = new[]
        {
            MakeDto("早用", runCount: 2, lastUsedAt: early),
            MakeDto("晚用", runCount: 2, lastUsedAt: late),
        };

        var ordered = ResourceOrdering.ApplyDefault(items).ToList();

        Assert.Equal(["晚用", "早用"], ordered.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void StillTied_OrdersByNameOrdinalIgnoreCase()
    {
        var items = new[]
        {
            MakeDto("banana"),
            MakeDto("Apple"),
        };

        var ordered = ResourceOrdering.ApplyDefault(items).ToList();

        Assert.Equal(["Apple", "banana"], ordered.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void ColumnSort_KeepsPinnedGroupAndSortsNormalWithin()
    {
        var items = new[]
        {
            MakeDto("置顶乙", isPinned: true, linkSortOrder: 2),
            MakeDto("普通甲", runCount: 1),
            MakeDto("置顶甲", isPinned: true, linkSortOrder: 1),
            MakeDto("普通乙", runCount: 9),
        };

        var byRunCountDesc = ResourceOrdering.ApplyColumnSort(items, x => x.RunCount, descending: true).ToList();

        // 置顶组保持手动顺序在前，普通组按列倒序，分组不被打散
        Assert.Equal(["置顶甲", "置顶乙", "普通乙", "普通甲"], byRunCountDesc.Select(x => x.Name).ToArray());

        var byRunCountAsc = ResourceOrdering.ApplyColumnSort(items, x => x.RunCount, descending: false).ToList();

        Assert.Equal(["置顶甲", "置顶乙", "普通甲", "普通乙"], byRunCountAsc.Select(x => x.Name).ToArray());
    }
}
