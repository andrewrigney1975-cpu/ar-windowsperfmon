using System.Runtime.InteropServices;

namespace Detective.Core.Native;

/// <summary>Per-process Win32 calls: identity checks, termination, image path and version description.</summary>
internal static unsafe class ProcessApi
{
    internal const uint PROCESS_TERMINATE = 0x0001;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_INVALID_PARAMETER = 87;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsProcessCritical(IntPtr process, out bool critical);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* name, ref uint size);

    [DllImport("kernel32.dll")]
    internal static extern uint GetActiveProcessorCount(ushort groupNumber);

    internal const ushort ALL_PROCESSOR_GROUPS = 0xFFFF;

    [DllImport("version.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetFileVersionInfoSizeW(string file, IntPtr handle);

    [DllImport("version.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetFileVersionInfoW(string file, uint handle, uint length, byte* data);

    [DllImport("version.dll", CharSet = CharSet.Unicode)]
    private static extern bool VerQueryValueW(byte* block, string subBlock, out IntPtr buffer, out uint length);

    // LUID is two 32-bit fields, so the struct is 4-byte packed.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint length, IntPtr previous, IntPtr returnLength);

    /// <summary>
    /// When elevated, turns on SeDebugPrivilege (as Task Manager does) so processes of other users and services
    /// can be inspected and ended. Does nothing for a standard user, who doesn't hold the privilege.
    /// </summary>
    internal static void EnableDebugPrivilege()
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out long luid)) return;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            AdjustTokenPrivileges(token, false, ref tp, (uint)sizeof(TOKEN_PRIVILEGES), IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>Full image path, or null when the process can't be opened (protected or already gone).</summary>
    internal static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>The file's version-resource FileDescription ("Google Chrome"), or null.</summary>
    internal static string? FileDescription(string path)
    {
        uint size = GetFileVersionInfoSizeW(path, IntPtr.Zero);
        if (size == 0) return null;
        var data = new byte[size];
        fixed (byte* block = data)
        {
            if (!GetFileVersionInfoW(path, 0, size, block)) return null;

            // Prefer the first listed translation, then the common English/Unicode fallbacks.
            var candidates = new List<string>(3);
            if (VerQueryValueW(block, @"\VarFileInfo\Translation", out var t, out uint tLen) && tLen >= 4)
            {
                ushort lang = *(ushort*)t, codePage = *((ushort*)t + 1);
                candidates.Add($"{lang:X4}{codePage:X4}");
            }
            candidates.Add("040904B0");
            candidates.Add("040904E4");

            foreach (var c in candidates)
            {
                if (VerQueryValueW(block, $@"\StringFileInfo\{c}\FileDescription", out var value, out uint len) && len > 1)
                {
                    var text = new string((char*)value).Trim();
                    if (text.Length > 0) return text;
                }
            }
        }
        return null;
    }
}

/// <summary>Finds processes that own a window the user would call an app (what Task Manager lists under Apps).</summary>
internal static unsafe class AppWindows
{
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, char* name, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);

    /// <summary>Visible, titled, unowned, uncloaked top-level windows that aren't tool windows or the desktop/taskbar.</summary>
    public static HashSet<int> ProcessIds()
    {
        var set = new HashSet<int>();
        var handle = GCHandle.Alloc(set);
        try { EnumWindows(&Visit, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return set;
    }

    [UnmanagedCallersOnly]
    private static int Visit(IntPtr hwnd, IntPtr state)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero || GetWindowTextLengthW(hwnd) == 0) return 1;
        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return 1;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return 1;

        char* cls = stackalloc char[64];
        var name = new string(cls, 0, GetClassNameW(hwnd, cls, 64));
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return 1;

        GetWindowThreadProcessId(hwnd, out int pid);
        if (pid != 0) ((HashSet<int>)GCHandle.FromIntPtr(state).Target!).Add(pid);
        return 1;
    }
}
