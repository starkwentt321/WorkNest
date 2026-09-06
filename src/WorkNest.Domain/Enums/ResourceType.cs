namespace WorkNest.Domain;

/// <summary>资源类型：决定启动策略、图标与唯一键构成。</summary>
public enum ResourceType
{
    /// <summary>文件夹目录。</summary>
    Directory = 0,

    /// <summary>普通文档/数据文件，按系统关联打开。</summary>
    File = 1,

    /// <summary>可执行程序，可带参数与工作目录。</summary>
    Program = 2,

    /// <summary>网站地址（http/https）。</summary>
    Website = 3,
}
