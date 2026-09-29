using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;

/// <summary>
/// Process helpers for clean shutdown.
///
/// Starting the harness can go through cmd.exe / npx, which nests several
/// processes deep. Killing only the process holding the TCP port leaves cmd
/// wrappers and node children behind, so shutdown walks the whole tree.
/// </summary>
public static class ProcessUtil
{
    public static List<int> GetDescendants(int rootPid)
    {
        var found = new List<int>();
        try
        {
            var children = new Dictionary<int, List<int>>();
            var query = "SELECT ProcessId, ParentProcessId FROM Win32_Process";
            using (var searcher = new ManagementObjectSearcher(query))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject mo in results)
                {
                    try
                    {
                        int pid = Convert.ToInt32(mo["ProcessId"]);
                        int ppid = Convert.ToInt32(mo["ParentProcessId"]);
                        if (!children.ContainsKey(ppid)) children[ppid] = new List<int>();
                        children[ppid].Add(pid);
                    }
                    catch { }
                    finally { mo.Dispose(); }
                }
            }

            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            int self = Process.GetCurrentProcess().Id;
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                List<int> kids;
                if (!children.TryGetValue(current, out kids)) continue;
                foreach (int k in kids)
                {
                    if (k == self) continue;
                    if (found.Contains(k)) continue;
                    found.Add(k);
                    queue.Enqueue(k);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("GetDescendants failed: " + ex.Message);
        }
        return found;
    }

    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using (var p = Process.GetProcessById(pid)) return !p.HasExited;
        }
        catch { return false; }
    }

    /// <summary>
    /// Kill a process and every descendant. Children are killed before their
    /// parents so nothing gets re-parented and orphaned mid-walk.
    /// </summary>
    public static void KillTree(int rootPid)
    {
        if (rootPid <= 0) return;
        var kids = GetDescendants(rootPid);
        for (int i = kids.Count - 1; i >= 0; i--)
        {
            try
            {
                using (var p = Process.GetProcessById(kids[i]))
                {
                    AppLogger.Write("  killing child pid " + kids[i] + " (" + p.ProcessName + ")");
                    p.Kill();
                }
            }
            catch { }
        }
        try
        {
            using (var p = Process.GetProcessById(rootPid))
            {
                AppLogger.Write("  killing pid " + rootPid + " (" + p.ProcessName + ")");
                p.Kill();
            }
        }
        catch { }
    }
}
