using System.IO;
using System.Runtime.InteropServices;

namespace WorkNest.App.Services;

/// <summary>
/// 浏览面板右键菜单的文件系统操作（回收站删除、重命名、属性、资源管理器定位）。
/// 刻意不走 IContextMenu 进程内集成：实测本机该链路会被第三方 Shell 扩展
/// 弄崩宿主进程（GetUIObjectOf 内部 AV），这里的 API 均为稳定系统调用。
/// </summary>
public static class FolderItemOps
{
    /// <summary>把一组文件/文件夹删除到回收站（FOF_ALLOWUNDO），系统自带确认框。返回是否成功且未中断。</summary>
    public static bool DeleteToRecycleBin(IntPtr ownerHwnd, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        // pFrom 为双零分隔、双零结尾的多路径串；string 封送会在首个 \0 截断，手工写内存
        var buffer = Marshal.AllocHGlobal((paths.Sum(p => (p.Length + 1) * 2) + 1) * 2);
        try
        {
            var write = buffer;
            foreach (var path in paths)
            {
                foreach (var ch in path)
                {
                    Marshal.WriteInt16(write, ch);
                    write += 2;
                }
                Marshal.WriteInt16(write, 0);
                write += 2;
            }
            Marshal.WriteInt16(write, 0); // 结尾双零

            var op = new SHFILEOPSTRUCTW
            {
                hwnd = ownerHwnd,
                wFunc = FO_DELETE,
                pFrom = buffer,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR,
            };
            var result = SHFileOperationW(ref op);
            return result == 0 && op.fAnyOperationsAborted == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>同一目录内重命名（文件或文件夹）。名称非法或目标已存在时返回错误信息，成功返回 null。</summary>
    public static string? Rename(string path, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return "名称不能为空。";
        }
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "名称包含系统不允许的字符。";
        }
        var newPath = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (string.Equals(newPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return null; // 名称未变化
        }
        if (File.Exists(newPath) || Directory.Exists(newPath))
        {
            return "同名文件或文件夹已存在。";
        }
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, newPath);
            }
            else if (Directory.Exists(path))
            {
                Directory.Move(path, newPath);
            }
            else
            {
                return "原文件或文件夹不存在。";
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>调出系统属性对话框（SEE_MASK_INVOKEIDLIST，仅单选）。</summary>
    public static void ShowProperties(IntPtr ownerHwnd, string path)
    {
        var info = new SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_NOCLOSEPROCESS,
            hwnd = ownerHwnd,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
        };
        ShellExecuteExW(ref info);
    }

    /// <summary>在资源管理器中直接打开目录。</summary>
    public static void OpenInExplorer(string path)
    {
        // ArgumentList 逐个传参：路径含空格/特殊字符时不再依赖字符串拼接与手工引号，消除参数注入面
        var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        psi.ArgumentList.Add(path); // explorer 直接以路径参数打开该目录
        System.Diagnostics.Process.Start(psi);
    }

    /// <summary>在资源管理器中打开所在文件夹并选中该条目。</summary>
    public static void RevealInExplorer(string path)
    {
        // explorer 的 /select 开关必须与路径以逗号合并为同一参数（/select,"path"）；
        // 拆成两个参数时 explorer 解析失败，实测会退回打开“文档”目录
        if (path.Contains('"'))
        {
            throw new ArgumentException("路径包含非法字符。", nameof(path));
        }
        // 必须走原始 Arguments 拼接（ArgumentList 会整体加引号破坏开关解析）；
        // Windows 文件名不允许含引号，上面的守卫保证拼接不引入额外参数
        var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        psi.Arguments = $"/select,\"{path}\"";
        System.Diagnostics.Process.Start(psi);
    }

    private const uint FO_DELETE = 3;
    private const short FOF_ALLOWUNDO = 0x40;
    private const short FOF_NOCONFIRMMKDIR = 0x20;
    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public short fFlags;
        public int fAnyOperationsAborted; // Win32 BOOL，4 字节
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFOW
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW info);
}
