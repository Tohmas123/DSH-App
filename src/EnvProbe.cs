using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Runtime dependency checks. Everything here reports a structured result rather
/// than throwing, because the UI must be able to explain the situation to a
/// non-technical user in plain language.
/// </summary>
public static class EnvProbe
{
    public sealed class CommandResult
    {
        public int ExitCode = -1;
        public string StdOut = "";
        public string StdErr = "";
        public bool TimedOut;
        public bool Started;

        public bool Ok { get { return Started && !TimedOut && ExitCode == 0; } }
    }

    /// <summary>
    /// Run a console program without a flashing window and capture its output.
    /// Never uses a shell, so arguments cannot be reinterpreted.
    /// </summary>
    public static CommandResult Run(string exe, string args, int timeoutMs)
    {
        return Run(exe, args, timeoutMs, null);
    }

    /// <param name="workDir">Directory to run in, or null for the current one.</param>
    public static CommandResult Run(string exe, string args, int timeoutMs, string workDir)
    {
        return Run(exe, args, timeoutMs, workDir, null);
    }

    /// <param name="extraEnv">
    /// Extra environment variables for the child only. Needed because some child
    /// processes look things up on PATH that the launcher had to provide itself -- the
    /// plugin installer, for instance, is a pnpm forwarder, and pnpm is not part of a
    /// Node install. Setting this on the child keeps that provision local to the one
    /// command instead of editing the user's environment.
    /// </param>
    public static CommandResult Run(string exe, string args, int timeoutMs, string workDir,
                                    IDictionary<string, string> extraEnv)
    {
        var r = new CommandResult();
        try
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
            if (extraEnv != null)
            {
                foreach (var kv in extraEnv)
                {
                    if (kv.Key == null) continue;
                    psi.EnvironmentVariables[kv.Key] = kv.Value ?? "";
                }
            }

            using (var p = Process.Start(psi))
            {
                if (p == null) return r;
                r.Started = true;

                // Read both pipes concurrently: filling one buffer while the other
                // is drained deadlocks on large output.
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                if (!p.WaitForExit(timeoutMs))
                {
                    r.TimedOut = true;
                    try { p.Kill(); } catch { }
                }
                r.StdOut = outTask.Result;
                r.StdErr = errTask.Result;
                r.ExitCode = p.HasExited ? p.ExitCode : -1;
            }
        }
        catch (Exception ex)
        {
            r.StdErr = ex.GetType().Name + ": " + ex.Message;
        }
        return r;
    }

    public sealed class NodeInfo
    {
        public bool Found;
        public string Version = "";
        public string Path = "";
        public string Detail = "";

        /// <summary>
        /// Minimum Node the harness actually needs.
        ///
        /// The harness declares no "engines" field of its own, so this is derived
        /// from what its dependency tree requires: body-parser and bundle-name both
        /// declare node >= 18, and the code uses APIs introduced around the same
        /// time (structuredClone = 17, AbortSignal.timeout = 17.3, global fetch =
        /// 18). 20 is used as the floor for headroom: it is a real LTS release, so
        /// asking for it can never demand a version that does not exist.
        /// </summary>
        public const int MinimumMajor = 20;

        /// <summary>Parsed major version, or -1 when the version string is unreadable.</summary>
        public int Major = -1;
        public int Minor = -1;
        public int Patch = -1;

        /// <summary>True when a version was read and it is below the minimum.</summary>
        public bool TooOld
        {
            get { return Found && Major >= 0 && Major < MinimumMajor; }
        }

        /// <summary>"v24.21.0" -> (24, 21, 0). Returns false when unparseable.</summary>
        public static bool TryParse(string raw, out int major, out int minor, out int patch)
        {
            major = minor = patch = -1;
            if (string.IsNullOrEmpty(raw)) return false;

            string s = raw.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);

            // Cut anything after the numbers, e.g. "-rc.1" or "+build".
            int end = 0;
            while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
            s = s.Substring(0, end);
            if (s.Length == 0) return false;

            string[] parts = s.Split('.');
            if (!int.TryParse(parts[0], out major)) { major = -1; return false; }
            if (parts.Length > 1 && !int.TryParse(parts[1], out minor)) minor = -1;
            if (parts.Length > 2 && !int.TryParse(parts[2], out patch)) patch = -1;
            return true;
        }
    }

    /// <summary>
    /// Locate Node.js. Deliberately does not trust PATH alone: a machine can have
    /// node installed but not on the PATH this process inherited.
    /// </summary>
    public static NodeInfo ProbeNode()
    {
        var info = new NodeInfo();

        var r = Run("node", "--version", 20000);
        if (r.Ok && r.StdOut.Trim().Length > 0)
        {
            info.Found = true;
            info.Version = r.StdOut.Trim();
            info.Detail = "PATH";
            info.Path = FindOnPath("node.exe");
            NodeInfo.TryParse(info.Version, out info.Major, out info.Minor, out info.Patch);
            AppLogger.Write("node found: " + info.Version + " (major " + info.Major
                + ", required >=" + NodeInfo.MinimumMajor + ") at " + info.Path);
            return info;
        }

        // Fall back to the conventional install locations before giving up.
        string[] guesses = new string[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe")
        };
        foreach (string g in guesses)
        {
            if (!File.Exists(g)) continue;
            var r2 = Run(g, "--version", 20000);
            if (r2.Ok)
            {
                info.Found = true;
                info.Version = r2.StdOut.Trim();
                info.Path = g;
                info.Detail = "outside PATH";
                NodeInfo.TryParse(info.Version, out info.Major, out info.Minor, out info.Patch);
                AppLogger.Write("node found outside PATH: " + info.Version + " (major " + info.Major
                    + ") at " + g);
                return info;
            }
        }

        info.Detail = r.StdErr;
        AppLogger.Write("node NOT found. probe stderr: " + r.StdErr);
        return info;
    }

    public static string FindOnPath(string fileName)
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try
                {
                    string full = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
        }
        catch { }
        return "";
    }

    public sealed class DshInfo
    {
        /// <summary>How we will launch the harness: "npx-cache", "npx-download", "path".</summary>
        public string How = "";
        public string LauncherExe = "";
        public string LauncherArgs = "";
        public string Version = "";
        public bool Ready;
        public string Detail = "";

        /// <summary>
        /// The harness's real version, e.g. "0.1.5-rc.2", read off the installed tree.
        ///
        /// "Version" above only ever described HOW the harness was found ("cached",
        /// "installed globally") -- which says nothing about which build the user is
        /// actually running. There was no way to answer "which harness is that machine
        /// on?" without going and reading files by hand, and that blind spot is exactly
        /// what made the 2026-09-20 startup failure take so long to place.
        ///
        /// Empty when it cannot be determined.
        /// </summary>
        public string VersionDetail = "";

        /// <summary>
        /// For the npx-cache case, the directory npm installed into -- what a repair
        /// has to touch. Empty when the harness came from PATH instead.
        /// </summary>
        public string TreeRoot = "";
    }

    /// <summary>
    /// Decide how to start the harness, mirroring the launcher's three-tier
    /// fallback: a global dsh on PATH, an already-downloaded npx cache entry,
    /// or letting npx fetch it.
    /// </summary>
    public static DshInfo ProbeDsh(NodeInfo node)
    {
        var info = new DshInfo();

        string shim = FindOnPath("dsh.cmd");
        if (shim.Length == 0) shim = FindOnPath("dsh.exe");
        if (shim.Length > 0)
        {
            info.How = "path";
            info.LauncherExe = shim;
            info.LauncherArgs = "web --no-open --port {port}";
            info.Ready = true;
            info.Version = "installed globally";
            AppLogger.Write("dsh resolved on PATH: " + shim);
            return info;
        }

        string cache = FindNpxCacheEntry();
        if (cache.Length > 0)
        {
            info.How = "npx-cache";
            info.LauncherExe = node.Found && node.Path.Length > 0 ? node.Path : "node";
            info.LauncherArgs = "\"" + cache + "\" web --no-open --port {port}";
            info.Ready = true;
            info.Version = "cached";
            info.TreeRoot = TreeRootOf(cache);
            info.VersionDetail = ReadHarnessVersion(info.TreeRoot);
            AppLogger.Write("dsh resolved from npx cache: " + cache
                + (info.VersionDetail.Length > 0 ? " (harness " + info.VersionDetail + ")" : ""));
            return info;
        }

        string npx = FindOnPath("npx.cmd");
        if (npx.Length == 0) npx = FindOnPath("npx");
        if (npx.Length > 0)
        {
            info.How = "npx-download";
            info.LauncherExe = npx;
            info.LauncherArgs = "-y @deepseek-ai/dsh web --no-open --port {port}";
            info.Ready = true;
            info.Version = "will download on first run";
            AppLogger.Write("dsh will be fetched by npx: " + npx);
            return info;
        }

        info.Detail = "no dsh and no npx";
        AppLogger.Write("dsh NOT resolvable (no PATH shim, no npx cache, no npx)");
        return info;
    }

    /// <summary>
    /// Newest @deepseek-ai/dsh entry inside the npx cache, if one was ever fetched.
    /// </summary>
    public static string FindNpxCacheEntry()
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "npm-cache", "_npx");
            if (!Directory.Exists(root)) return "";

            string best = "";
            DateTime bestTime = DateTime.MinValue;

            foreach (string dir in Directory.GetDirectories(root))
            {
                string candidate = Path.Combine(dir,
                    Path.Combine("node_modules", Path.Combine("@deepseek-ai", Path.Combine("dsh", Path.Combine("lib", "bin.js")))));
                if (!File.Exists(candidate)) continue;
                DateTime t = File.GetLastWriteTimeUtc(candidate);
                if (t > bestTime) { bestTime = t; best = candidate; }
            }
            return best;
        }
        catch (Exception ex)
        {
            AppLogger.Write("npx cache scan failed: " + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// Everything before the first "\node_modules\" of a resolved entry point: the
    /// directory npm/npx installed the tree into. That directory is the unit a repair
    /// works on -- install one package into it, or delete it so the next start
    /// downloads a clean one.
    /// </summary>
    public static string TreeRootOf(string entryPoint)
    {
        if (string.IsNullOrEmpty(entryPoint)) return "";
        int i = entryPoint.IndexOf("\\node_modules\\", StringComparison.OrdinalIgnoreCase);
        if (i > 0) return entryPoint.Substring(0, i);
        return "";
    }

    /// <summary>
    /// The harness version as installed in a tree root, straight out of the package's
    /// own manifest. Read-only and failure-tolerant: an unreadable manifest yields an
    /// empty string, never an exception that could break startup.
    /// </summary>
    public static string ReadHarnessVersion(string treeRoot)
    {
        if (string.IsNullOrEmpty(treeRoot)) return "";
        try
        {
            string manifest = Path.Combine(
                Path.Combine(Path.Combine(treeRoot, "node_modules"), "@deepseek-ai"),
                Path.Combine("dsh", "package.json"));
            if (!File.Exists(manifest)) return "";

            string text;
            // FileShare.ReadWrite for the same reason as everywhere else in this file:
            // never assume we are the only reader of a file we do not own.
            using (var fs = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                text = sr.ReadToEnd();
            }

            // Deliberately not a JSON parser: one field, fixed shape, and a dependency
            // on a serialiser would be more risk than the field is worth.
            var m = Regex.Match(text, "\"version\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch (Exception ex)
        {
            AppLogger.Write("harness version read failed: " + ex.Message);
            return "";
        }
    }

    /// <summary>Is sharp's Windows binary actually inside this tree?</summary>
    public static bool SharpBinaryPresent(string treeRoot)
    {
        try
        {
            string img = Path.Combine(Path.Combine(treeRoot, "node_modules"), "@img");
            if (!Directory.Exists(img)) return false;
            foreach (string d in Directory.GetDirectories(img))
            {
                if (Path.GetFileName(d).StartsWith("sharp-win32", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Repair a harness tree that is missing an optional, platform-specific package.
    ///
    /// sharp declares its Windows binary as an optional dependency, so a failed
    /// download is not an error for npm: the tree looks installed and dies at boot.
    /// Two attempts, cheapest first -- install the package into the existing tree,
    /// and if npm cannot do that (offline, permissions, a mirror without it), delete
    /// the tree so the next start downloads a clean one.
    ///
    /// Measured caveat: deleting the tree only helps if the next start can actually
    /// download. When the failure that triggered the repair was itself a network
    /// failure, that is known to be false, so the caller is told the difference
    /// instead of being handed a plain "fixed".
    /// </summary>
    /// <param name="triggeringKind">The FailureKind that led here, so a network
    /// failure is not followed by a claim that everything is installed.</param>
    public static bool RepairOptionalDeps(DshInfo dsh, string triggeringKind, out string message)
    {
        message = "";
        if (dsh == null || string.IsNullOrEmpty(dsh.TreeRoot) || !Directory.Exists(dsh.TreeRoot))
        {
            message = "这次启动不是从 npx 缓存跑的，没法自动修（可手动执行：npm i -g @deepseek-ai/dsh）";
            return false;
        }

        AppLogger.Write("repair: npm install --include=optional sharp in " + dsh.TreeRoot);
        CommandResult r = Run("cmd.exe", "/c npm install --include=optional sharp", 600000, dsh.TreeRoot);
        AppLogger.Write("repair: npm exit " + r.ExitCode + ", timed out=" + r.TimedOut);
        if (!r.Ok)
        {
            string detail = (r.StdErr + "\n" + r.StdOut).Trim();
            if (detail.Length > 800) detail = detail.Substring(detail.Length - 800);
            AppLogger.Write("repair output: " + detail);
        }

        if (r.Ok && SharpBinaryPresent(dsh.TreeRoot))
        {
            message = "缺失的组件已重新装好";
            return true;
        }

        // In-place repair did not produce the binary. Removing the tree makes the next
        // start fetch a whole clean one, which is the only other lever there is.
        try
        {
            AppLogger.Write("repair: removing " + dsh.TreeRoot + " so the next start refetches");
            Directory.Delete(dsh.TreeRoot, true);
            if (triggeringKind == "network")
            {
                // Nothing was installed; the tree was only cleared. Saying "fixed"
                // here would be a lie the next start immediately exposes.
                message = "已清掉损坏的下载缓存，但没能联网补装组件。请确认网络可用后再点「重试」重新下载";
            }
            else
            {
                message = "已清掉损坏的下载缓存，重新启动会重新下载一份";
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Write("repair: could not remove the tree: " + ex.Message);
            message = "修复失败：" + ex.Message;
            return false;
        }
    }

    public sealed class WebViewInfo
    {
        public bool Found;
        public string Version = "";
        public string Detail = "";
    }

    public static WebViewInfo ProbeWebView()
    {
        var info = new WebViewInfo();
        try
        {
            string v = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (!string.IsNullOrEmpty(v))
            {
                info.Found = true;
                info.Version = v;
            }
            else
            {
                info.Detail = "runtime reports no version";
            }
        }
        catch (Exception ex)
        {
            info.Detail = ex.Message;
        }
        AppLogger.Write("webview2 runtime: " + (info.Found ? info.Version : "NOT AVAILABLE - " + info.Detail));
        return info;
    }

    public sealed class OsInfo
    {
        public string Caption = "";
        public string Architecture = "";
        public bool Is64Bit;
        public Version Version;
    }

    public static OsInfo ProbeOs()
    {
        var info = new OsInfo();
        try
        {
            info.Version = Environment.OSVersion.Version;
            info.Is64Bit = Environment.Is64BitOperatingSystem;
            info.Architecture = info.Is64Bit ? "x64" : "x86";
            info.Caption = info.Is64Bit ? "Windows (64-bit)" : "Windows (32-bit)";
        }
        catch { }
        return info;
    }

    public sealed class PortInfo
    {
        public int Port;
        public bool Open;
    }

    /// <summary>
    /// Fast TCP probe. A socket connect to a closed loopback port fails in
    /// milliseconds, whereas enumerating TCP connections can take a second or
    /// more -- too slow for a startup hot path.
    /// </summary>
    public static bool TestPortOpen(int port, int timeoutMs)
    {
        if (port <= 0) return false;
        var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        try
        {
            var iar = socket.BeginConnect("127.0.0.1", port, null, null);
            if (!iar.AsyncWaitHandle.WaitOne(timeoutMs, false)) return false;
            return socket.Connected;
        }
        catch { return false; }
        finally { try { socket.Close(); } catch { } }
    }

    public static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static int GetListeningPid(int port)
    {
        try
        {
            var r = Run("netstat", "-ano -p tcp", 15000);
            if (!r.Ok && r.StdOut.Length == 0) return 0;
            var m = Regex.Match(r.StdOut,
                @":(?<p>" + port + @")\s+\S+\s+LISTENING\s+(?<pid>\d+)");
            if (m.Success)
            {
                int pid;
                if (int.TryParse(m.Groups["pid"].Value, out pid)) return pid;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Ports reserved by Windows itself, from `netsh int ipv4 show excludedportrange`.
    ///
    /// These are the ports where "something answers on that port" is true while "some
    /// program is listening" is false: the kernel completes the TCP handshake for a
    /// reserved port even though nothing is bound, and a bind there fails with "access
    /// permissions". Reporting "another program is using this port" for one of them sends
    /// the user hunting for a process that does not exist.
    ///
    /// Purely a read-only query: this never attempts a bind, so it cannot briefly occupy a
    /// port that the startup path is about to use.
    /// </summary>
    private static List<int[]> GetExcludedPortRanges()
    {
        var ranges = new List<int[]>();
        try
        {
            var r = Run("netsh", "int ipv4 show excludedportrange protocol=tcp", 8000);
            if (!r.Started || r.StdOut.Length == 0) return ranges;

            // The table is plain and unlocalised (the labels stay English on a Chinese
            // Windows), and the range rows are just "  <start>   <end>". Matching
            // "two integers on their own line" avoids depending on the header wording.
            foreach (Match m in Regex.Matches(r.StdOut, @"(?m)^\s*(\d{1,5})\s+(\d{1,5})\s*$"))
            {
                int a, b;
                if (int.TryParse(m.Groups[1].Value, out a) &&
                    int.TryParse(m.Groups[2].Value, out b) && a > 0 && b >= a)
                {
                    ranges.Add(new int[] { a, b });
                }
            }
        }
        catch { }
        return ranges;
    }

    /// <summary>
    /// Why a port answers on connect. This is not the same question as "is something
    /// listening" -- see GetExcludedPortRanges.
    /// </summary>
    public enum PortState
    {
        /// <summary>Nothing answers; the port is usable.</summary>
        Free = 0,
        /// <summary>A real process owns it (our old harness, or somebody else's program).</summary>
        Listening = 1,
        /// <summary>Reserved by Windows. Nothing is listening and it cannot be bound.</summary>
        Reserved = 2
    }

    /// <summary>
    /// Classify a port by WHY it is unavailable, so the UI can say something true.
    ///
    /// Measured on this machine, with 8884 inside the reserved range (the System process
    /// owns the netstat entry there):
    ///
    ///     port   answers on connect   netstat pid   real bind
    ///     8884   yes                 4 (System)    fails: access permissions
    ///     3099   yes                 the real pid  (in use)
    ///     31500  no                  0             succeeds
    ///
    /// So a listening pid of 0 -- or of 4, the System process -- together with a reserved
    /// range means "Windows reserved this", not "somebody is using this".
    /// </summary>
    public static PortState ClassifyPort(int port, out int ownerPid)
    {
        ownerPid = 0;
        if (port <= 0 || port > 65535) return PortState.Free;
        if (!TestPortOpen(port, 250)) return PortState.Free;

        ownerPid = GetListeningPid(port);
        bool ownerIsNothing = ownerPid <= 0 || ownerPid == 4;
        if (ownerIsNothing)
        {
            foreach (int[] range in GetExcludedPortRanges())
            {
                if (port >= range[0] && port <= range[1]) return PortState.Reserved;
            }
        }
        return PortState.Listening;
    }
}
