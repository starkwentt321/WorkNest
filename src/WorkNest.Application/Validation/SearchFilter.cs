using WorkNest.Application.Dtos;

namespace WorkNest.Application.Validation;

/// <summary>
/// 搜索过滤：普通包含匹配（决策 48），命中范围覆盖名称、标签与路径；
/// 多个关键字以空格分隔时要求全部命中（AND 语义）。
/// </summary>
public static class SearchFilter
{
    public static bool Matches(ResourceDto item, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (!MatchesToken(item, token))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesToken(ResourceDto item, string token)
    {
        if (item.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.Target.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var tag in item.Tags)
        {
            if (tag.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
