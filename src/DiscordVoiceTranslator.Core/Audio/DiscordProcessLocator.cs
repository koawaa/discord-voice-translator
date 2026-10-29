using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiscordVoiceTranslator.Core.Audio;

public sealed record DiscordProcess(int ProcessId, string ExeName);

public static class DiscordProcessLocator
{
    // Ordre de préférence si plusieurs variantes tournent en même temps.
    private static readonly string[] ExeNames = ["Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe"];

    /// <summary>
    /// Trouve le processus Discord racine : celui dont le parent n'est pas lui-même un
    /// processus Discord du même nom. Les sous-processus (rendu, GPU, utilitaires, clips)
    /// en descendent, et la capture en mode INCLUDE_TARGET_PROCESS_TREE les couvre tous.
    /// </summary>
    public static DiscordProcess? FindRoot()
    {
        var all = Snapshot();
        foreach (var exe in ExeNames)
        {
            var sameName = all.Where(p => p.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList();
            var pids = sameName.Select(p => p.Pid).ToHashSet();
            foreach (var p in sameName)
            {
                if (!pids.Contains(p.ParentPid))
                    return new DiscordProcess((int)p.Pid, exe);
            }
        }
        return null;
    }

    private readonly record struct Entry(uint Pid, uint ParentPid, string ExeName);

    // Toolhelp donne PID, PID parent et nom de tous les processus en un seul appel,
    // sans passer par WMI (lent) ni ouvrir chaque processus.
    private static List<Entry> Snapshot()
    {
        var list = new List<Entry>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                list.Add(new Entry(entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return list;
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private static readonly IntPtr InvalidHandleValue = new(-1);

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
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
