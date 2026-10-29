using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiscordVoiceTranslator.Core.Audio;

public sealed record AppProcess(int ProcessId, string ExeName);

/// <summary>Une appli ouverte avec une fenêtre, proposée comme source audio.</summary>
public sealed record RunningApp(string ExeName, string Title);

public static class ProcessLocator
{
    // Ordre de préférence si plusieurs variantes tournent en même temps.
    public static readonly string[] DiscordExeNames = ["Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe"];

    // Fenêtres qui n'ont pas de son à elles : hôtes d'applis, shell, réglages.
    private static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ApplicationFrameHost.exe", "TextInputHost.exe", "SystemSettings.exe", "ShellExperienceHost.exe",
    };

    /// <summary>
    /// Trouve le processus racine de l'appli : celui dont le parent n'est pas lui-même un
    /// processus du même nom. Les sous-processus (rendu, GPU, utilitaires, clips) en
    /// descendent, et la capture en mode INCLUDE_TARGET_PROCESS_TREE les couvre tous.
    /// </summary>
    public static AppProcess? FindRoot(IEnumerable<string> exeNames)
    {
        var all = Snapshot();
        foreach (var exe in exeNames)
        {
            var sameName = all.Where(p => p.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList();
            var pids = sameName.Select(p => p.Pid).ToHashSet();
            foreach (var p in sameName)
            {
                if (!pids.Contains(p.ParentPid))
                    return new AppProcess((int)p.Pid, p.ExeName);
            }
        }
        return null;
    }

    /// <summary>
    /// Applis ouvertes avec une fenêtre visible (jeux compris), une par exécutable, sans
    /// Discord (déjà proposé à part) ni cette appli. Prend quelques centaines de ms.
    /// </summary>
    public static List<RunningApp> ListWindowedApps()
    {
        int self = Environment.ProcessId;
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == self || process.MainWindowHandle == IntPtr.Zero || process.MainWindowTitle.Length == 0)
                        continue;
                    string exe = process.ProcessName + ".exe";
                    if (NotApps.Contains(exe) || DiscordExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase))
                        continue;
                    apps.TryAdd(exe, new RunningApp(exe, process.MainWindowTitle));
                }
                catch (InvalidOperationException)
                {
                    // Fermée pendant l'énumération.
                }
            }
        }
        return apps.Values.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
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
