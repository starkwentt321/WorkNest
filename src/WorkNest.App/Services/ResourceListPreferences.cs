namespace WorkNest.App.Services;

/// <summary>资源列表全局偏好；只保存显示状态，不改变资源或工作区数据。</summary>
public sealed record ResourceListPreferences
{
    public int Version { get; init; } = 1;
    public double NameWidth { get; init; } = 190;
    public double TypeWidth { get; init; } = 62;
    public double TargetWidth { get; init; } = 180;
    public bool ShowType { get; init; } = true;
    public bool ShowTarget { get; init; } = true;
    public string? SortKey { get; init; }
    public bool SortDescending { get; init; }

    public ResourceListPreferences Normalize()
    {
        if (Version != 1) return new();
        var key = SortKey is "Name" or "Type" or "Target" ? SortKey : null;
        if (key == "Type" && !ShowType || key == "Target" && !ShowTarget) key = null;
        return this with
        {
            NameWidth = Width(NameWidth, 190), TypeWidth = Width(TypeWidth, 62),
            TargetWidth = Width(TargetWidth, 180), SortKey = key,
            SortDescending = key is not null && SortDescending,
        };
    }

    private static double Width(double value, double fallback) =>
        double.IsFinite(value) && value >= 40 && value <= 2000 ? value : fallback;
}
