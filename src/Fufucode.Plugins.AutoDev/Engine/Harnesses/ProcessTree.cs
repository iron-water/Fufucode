using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Fufucode.Core.Automation.Harnesses;

/// <summary>
/// Live child pids of one process. A managed writer is only stopped when neither its own
/// pid nor any of its children can still write into the worktree.
/// Windows keeps the creator pid in the process entry after the creator exits, so orphaned
/// writers stay discoverable. Linux reparents orphans: there the working directories of all
/// readable processes are scanned instead, because a pid's parent is then no longer evidence.
/// </summary>
public static class ProcessTree
{
    public static IReadOnlyList<int> Children(int parentId) =>
        OperatingSystem.IsWindows() ? WindowsChildren(parentId) : UnixChildren(parentId);

    /// <summary>Windows retains a numeric creator PID, even across reuse. A process born before
    /// the recorded writer cannot be its child. Unknown birth times remain blocking evidence.</summary>
    public static IReadOnlyList<int> ChildrenOfWriter(int parentId, long? writerStartUtc, IProcessProbe probe)
    {
        var children = Children(parentId);
        if (writerStartUtc is not { } started) return children;
        var births = children.ToDictionary(pid => pid, pid => probe.Probe(pid).StartUtc?.ToUnixTimeMilliseconds());
        if (OperatingSystem.IsWindows() && probe is SystemProcessProbe && births.Values.Any(time => time is null))
            foreach (var (pid, birth) in ReadWindowsChildBirths(parentId))
                if (births.ContainsKey(pid) && births[pid] is null) births[pid] = birth;
        return children.Where(pid => births[pid] is not { } birth || birth >= started).ToArray();
    }

    // Protected system processes deny Process.StartTime but expose creation metadata through CIM.
    // This rare fallback observes only children of one journaled PID, without elevation or shell input.
    private static Dictionary<int, long> ReadWindowsChildBirths(int parentId)
    {
        var births = new Dictionary<int, long>();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) return births;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "@((Get-CimInstance Win32_Process -Filter 'ParentProcessId=" + parentId.ToString(CultureInfo.InvariantCulture) +
            "') | ForEach-Object { @{Pid=$_.ProcessId; Born=$_.CreationDate.ToUniversalTime().ToString('O')} }) | ConvertTo-Json -Compress" })
            start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); return births; }
            if (process.ExitCode != 0) return births;
            using var json = JsonDocument.Parse(output.GetAwaiter().GetResult());
            var rows = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : [json.RootElement];
            foreach (var row in rows)
                if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("Pid", out var pid) && row.TryGetProperty("Born", out var born) &&
                    DateTimeOffset.TryParse(born.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc))
                    births[pid.GetInt32()] = utc.ToUnixTimeMilliseconds();
            _ = error.GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException or FormatException)
        { /* Missing or unverifiable metadata never clears a writer. */ }
        return births;
    }

    /// <summary>Processes whose current directory is inside the given worktree (Unix orphan scan).</summary>
    public static IReadOnlyList<int> WritersAt(string worktree)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(worktree) || !Directory.Exists(worktree))
            return [];
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        var result = new List<int>();
        foreach (var entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(entry.AsSpan(entry.LastIndexOf('/') + 1), out var pid)) continue;
            try
            {
                var cwd = new DirectoryInfo(File.ReadAllText(Path.Combine(entry, "cwd")));
                var full = Path.TrimEndingDirectorySeparator(cwd.FullName);
                if (string.Equals(full, target, StringComparison.Ordinal) ||
                    full.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    result.Add(pid);
            }
            catch (IOException) { /* process vanished */ }
            catch (UnauthorizedAccessException) { /* another user's process: not readable evidence */ }
            catch (ArgumentException) { /* unreadable link target */ }
        }
        result.Sort();
        return result;
    }

    private static IReadOnlyList<int> UnixChildren(int parentId)
    {
        var result = new List<int>();
        foreach (var entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(entry.AsSpan(entry.LastIndexOf('/') + 1), out var pid)) continue;
            try
            {
                // field 1 pid, field 2 comm (may contain spaces), field 4 ppid.
                var stat = File.ReadAllText(Path.Combine(entry, "stat"));
                var tail = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (tail.Length >= 3 && int.TryParse(tail[2], out var ppid) && ppid == parentId)
                    result.Add(pid);
            }
            catch (IOException) { /* process vanished between enumeration and read */ }
            catch (UnauthorizedAccessException) { result.Add(pid); } // keep, never assume
        }
        result.Sort();
        return result;
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;

    // The structure must match PROCESSENTRY32W exactly, including the Unicode name buffer;
    // a wrong dwSize makes CreateToolhelp32Snapshot's enumeration fail silently.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static IReadOnlyList<int> WindowsChildren(int parentId)
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return [];
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        if (!Process32First(snapshot, ref entry))
            throw new InvalidOperationException(
                $"Cannot enumerate processes: {Marshal.GetLastWin32Error()}.");
            var result = new List<int>();
            do if (entry.th32ParentProcessID == parentId) result.Add((int)entry.th32ProcessID);
        while (Process32Next(snapshot, ref entry));
            result.Sort();
            return result;
        }
        finally { CloseHandle(snapshot); }
    }
}
