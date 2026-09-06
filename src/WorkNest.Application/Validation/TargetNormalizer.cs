using WorkNest.Domain;

namespace WorkNest.Application.Validation;

/// <summary>
/// 目标规范化：本地路径取完整路径，URL 补全协议并转绝对 URI。
/// 规范化结果参与唯一键查重与启动，是数据一致性的基础。
/// </summary>
public static class TargetNormalizer
{
    /// <summary>允许登记的 URL 协议白名单；未知协议先拒绝，避免误注册任意协议处理程序。</summary>
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https" };

    public static bool TryNormalize(ResourceType type, string rawTarget, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        var trimmed = (rawTarget ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            error = "目标不能为空";
            return false;
        }

        if (type == ResourceType.Website)
        {
            // 用户省略协议时默认按 https 处理，降低输入成本
            if (!trimmed.Contains("://", StringComparison.Ordinal))
            {
                trimmed = "https://" + trimmed;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || !AllowedSchemes.Contains(uri.Scheme))
            {
                error = "网站地址无效，当前仅支持 http/https 协议";
                return false;
            }

            normalized = uri.ToString();
            return true;
        }

        try
        {
            normalized = Path.GetFullPath(trimmed);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "路径无效";
            return false;
        }
    }

    /// <summary>按目标自动提取显示名称（决策 61：本地资源自动提取文件名）。</summary>
    public static string SuggestName(ResourceType type, string normalizedTarget)
    {
        try
        {
            return type switch
            {
                ResourceType.Directory => Path.TrimEndingDirectorySeparator(normalizedTarget) is var p ? Path.GetFileName(p) : normalizedTarget,
                ResourceType.Program => Path.GetFileNameWithoutExtension(normalizedTarget),
                ResourceType.File => StripShortcutSuffix(Path.GetFileName(normalizedTarget)),
                ResourceType.Website when Uri.TryCreate(normalizedTarget, UriKind.Absolute, out var uri) => uri.Host,
                _ => normalizedTarget,
            };
        }
        catch (Exception)
        {
            // 提取失败时退回原始目标，不阻止保存
            return normalizedTarget;
        }
    }

    /// <summary>快捷方式文件本身作为目标时，自动名称不带 .lnk 后缀（“xx.lnk”→“xx”）；普通文件扩展名保持原样。</summary>
    private static string StripShortcutSuffix(string fileName) =>
        fileName.Length > 4 && fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
}
