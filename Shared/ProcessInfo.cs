using System.Runtime.InteropServices;

namespace ClaudeStatus;

/// <summary>Finds the Claude Code process behind a hook, and checks later whether it is still running.</summary>
public static class ProcessInfo
{
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;
    private static readonly IntPtr InvalidHandle = new(-1);

    // Programs that sit between Claude Code and the hook. The first parent that isn't one of these is Claude Code.
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash.exe", "sh.exe", "dash.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "env.exe", "wsl.exe",
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>When the process started (an exact Windows time stamp), or 0 if it isn't running.</summary>
    public static long StartTime(int pid)
    {
        if (pid <= 0) return 0;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            if (!GetExitCodeProcess(handle, out uint code) || code != STILL_ACTIVE) return 0;
            return GetProcessTimes(handle, out long created, out _, out _, out _) ? created : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// True if that exact process is still running. The start time guards against Windows
    /// handing the same process number to a new, unrelated program.
    /// </summary>
    public static bool IsRunning(int pid, long startTime) => startTime != 0 && StartTime(pid) == startTime;

    /// <summary>
    /// Walks up from this process to the first parent that isn't a shell: the Claude Code process.
    /// Also returns when Claude Code launched this hook (the start time of the program it started directly),
    /// which is the best clock for "which event happened first".
    /// </summary>
    public static (int Pid, long StartTime, long LaunchedAt) FindClaudeProcess()
    {
        var table = new Dictionary<int, (int Parent, string Exe)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle || snapshot == IntPtr.Zero) return (0, 0, 0);
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                table[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
        }
        finally
        {
            CloseHandle(snapshot);
        }

        int pid = Environment.ProcessId;
        long childStart = StartTime(pid);
        for (int depth = 0; depth < 16; depth++)
        {
            if (!table.TryGetValue(pid, out var me) || !table.TryGetValue(me.Parent, out var parent)) break;
            long start = StartTime(me.Parent);
            // A parent that started after its child is an unrelated program that reused the number.
            if (start == 0 || start > childStart) break;
            if (!Shells.Contains(parent.Exe)) return (me.Parent, start, childStart);
            pid = me.Parent;
            childStart = start;
        }
        return (0, 0, 0);
    }
}
