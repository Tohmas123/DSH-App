using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

/// <summary>
/// Owns the `dsh web` child process: start it, discover the tokenised URL it
/// prints, report readiness, and shut it down without leaving orphans.
///
/// Two things here are the result of hard-won debugging:
///
///  * The URL banner only appears when the child's stdout is a FILE, not a pipe.
///    Reading it through redirected pipes (Process.BeginOutputReadLine) reliably
///    produced no banner at all. So the child's output is bound to log files and
///    the banner is read back from those files.
///
///  * The token matters. A bare http://127.0.0.1:PORT answers 401 without the
///    cookie, so the full URL from the banner is the only usable entry point.
/// </summary>
public sealed class DshServer
{
    private Process process;
    private FileStream outStream;
    private FileStream errStream;
    private string outPath;
    private string errPath;
    private long lastScanLength = -1;

    public int Port { get; private set; }
    public string Url { get; private set; }
    public bool Ready { get; private set; }
    public string LastError { get; private set; }

    /// <summary>
    /// Machine-readable reason for an immediate exit, so the UI can explain it in
    /// plain language instead of showing the user an exit code. Empty until a
    /// failure has been classified.
    /// </summary>
    public string FailureKind { get; private set; }

    /// <summary>
    /// The first line of the harness's own output that actually says something --
    /// the file separator ReadTail prepends is skipped. Shown to the user when the
    /// failure cannot be classified, because a raw exit code tells them nothing.
    /// </summary>
    public string FailureDetail { get; private set; }

    /// <summary>
    /// Set when the preferred port is already held by something else. Adopting a
    /// server this app did not start would be wrong: we hold no token for it, so
    /// the user would land on a 401 page with no explanation.
    /// </summary>
    public bool PortConflict { get; private set; }

    /// <summary>
    /// True when the conflict is a Windows port reservation rather than a real listener.
    /// The UI must word these differently: there is no process to find and no program to
    /// close, only a different port to pick. See EnvProbe.ClassifyPort.
    /// </summary>
    public bool PortReserved { get; private set; }

    public int ConflictPid { get; private set; }

    private int wrapperPid;
    private int serverPid;

    public int ServerPid { get { return serverPid; } }

    /// <summary>
    /// Launch the harness and block until its URL appears, or the deadline passes.
    /// Returns the tokenised URL, or null on failure (LastError explains why).
    /// </summary>
    public string Start(EnvProbe.DshInfo dsh, EnvProbe.NodeInfo node, int preferredPort,
                        string workspace, string apiKey, string logDir, Action<int> onProgress,
                        Action<Progress> onTree)
    {
        LastError = null;
        FailureKind = "";
        FailureDetail = "";
        Ready = false;
        Url = null;
        PortConflict = false;
        PortReserved = false;
        ConflictPid = 0;

        int port = preferredPort;
        if (EnvProbe.TestPortOpen(port, 250))
        {
            PortConflict = true;
            int owner;
            var state = EnvProbe.ClassifyPort(port, out owner);
            if (state == EnvProbe.PortState.Reserved)
            {
                // Nothing is listening: Windows reserved this port, so the connect probe
                // succeeds and the bind is impossible. Saying "already in use" here would
                // send the user looking for a process that does not exist.
                PortReserved = true;
                ConflictPid = 0;
                LastError = "port " + port + " is reserved by Windows";
                AppLogger.Write("port conflict: " + LastError + " (nothing is listening)");
            }
            else
            {
                ConflictPid = owner;
                LastError = "port " + port + " is already in use"
                    + (owner > 0 ? " (pid " + owner + ")" : "");
                AppLogger.Write("port conflict: " + LastError);
            }
            return null;
        }
        Port = port;

        if (!Directory.Exists(workspace))
        {
            string fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AppLogger.Write("workspace missing (" + workspace + "), using " + fallback);
            workspace = fallback;
        }

        try
        {
            Directory.CreateDirectory(logDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            outPath = Path.Combine(logDir, "server-" + stamp + ".out.log");
            errPath = Path.Combine(logDir, "server-" + stamp + ".err.log");

            // Share read/write so the banner can be read while the child keeps writing.
            outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            errStream = new FileStream(errPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

            string exe = dsh.LauncherExe;
            string args = dsh.LauncherArgs.Replace("{port}", port.ToString());

            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = workspace;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            if (!string.IsNullOrEmpty(apiKey))
            {
                psi.EnvironmentVariables["DEEPSEEK_API_KEY"] = apiKey;
            }

            AppLogger.Write("starting harness: " + exe + " " + args);
            AppLogger.Write("  workspace: " + workspace);
            AppLogger.Write("  stdout -> " + outPath);

            process = Process.Start(psi);
            if (process == null)
            {
                LastError = "the harness process did not start";
                AppLogger.Write("FAILED: Process.Start returned null");
                return null;
            }
            wrapperPid = process.Id;
            AppLogger.Write("  wrapper pid " + wrapperPid);

            // Pump the child's pipes into the log files. This is what makes the
            // banner appear: the child sees a file, not a console-less pipe pair.
            PumpAsync(process.StandardOutput.BaseStream, outStream);
            PumpAsync(process.StandardError.BaseStream, errStream);

            // The overall budget for the child to become usable, plus when we started,
            // because "how long did we actually wait" belongs in the failure text.
            var started = DateTime.UtcNow;
            var deadline = started.AddSeconds(300);
            int rounds = 0;
            DateTime? listeningSince = null;

            InitProgressRoot(dsh);
            int lastReportedPackets = -1;
            long lastReportedBytes = -1;

            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(300);
                rounds++;
                if (onProgress != null && rounds % 10 == 0) onProgress(rounds);

                // Real progress, measured off the disk rather than guessed. Reported only
                // when a number the user can see actually moves, so the panel does not
                // redraw the same sentence every 1.5 seconds.
                if (onTree != null && rounds % 4 == 0)
                {
                    var snap = SampleProgress(started);
                    if (snap != null && (snap.Packets != lastReportedPackets || snap.Bytes != lastReportedBytes))
                    {
                        lastReportedPackets = snap.Packets;
                        lastReportedBytes = snap.Bytes;
                        try { onTree(snap); } catch { }
                    }
                }

                string found = ScanForUrl();
                if (found != null)
                {
                    Url = found;
                    AppLogger.Write("url captured: " + found);
                    break;
                }

                if (process.HasExited)
                {
                    LastError = "the harness exited immediately (code " + process.ExitCode
                                + "). See the server logs in the log folder.";
                    AppLogger.Write("FAILED: harness exited, code " + process.ExitCode);
                    string tail = ReadTailSettled();
                    ClassifyFailure(tail);
                    // The raw tail is kept in app.log only when it could not be
                    // classified. For a classified failure the kind says more than
                    // the noise, and the full text is still in the server-*.log
                    // files this app writes next to app.log.
                    if (FailureKind == "crash" || FailureKind.StartsWith("crash "))
                    {
                        DumpTail(tail);
                    }
                    return null;
                }

                // Watching the port is only for diagnosis. It must NOT be a reason to
                // give up: measured 2026-09-20, the harness can bind the port well
                // before it prints its banner (a third-party plugin doing LAN setup
                // and pairing stretched that gap past 10 seconds). The previous code
                // treated "listening for more than 10s without a URL" as fatal, so a
                // merely slow start was reported as a failure -- while the banner was
                // sitting in the log the whole time. Only the deadline ends this loop.
                if (rounds % 4 == 0 && EnvProbe.TestPortOpen(port, 250))
                {
                    if (listeningSince == null)
                    {
                        listeningSince = DateTime.UtcNow;
                        AppLogger.Write("port " + port + " is listening; waiting for the access URL");
                    }
                }
            }

            if (Url == null)
            {
                int waited = (int)(DateTime.UtcNow - started).TotalSeconds;
                if (listeningSince != null)
                {
                    // The port opened but the banner never arrived. Say how long we
                    // waited and how long it had been listening, so the next person can
                    // tell "slow start" apart from "never started".
                    LastError = "the service is listening on port " + port
                                + " but never printed its access URL within " + waited
                                + " seconds";
                }
                else
                {
                    LastError = "the harness did not become ready within " + waited
                                + " seconds (it never even opened port " + port + ")";
                }
                AppLogger.Write("FAILED: " + LastError);
                DumpTail(ReadTail());
                return null;
            }

            serverPid = EnvProbe.GetListeningPid(port);
            if (serverPid <= 0) serverPid = wrapperPid;
            Ready = true;
            AppLogger.Write("harness ready: port " + port + ", server pid " + serverPid);
            return Url;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLogger.Write("start exception: " + ex);
            return null;
        }
    }

    /// <summary>
    /// What to tell the user while the first run downloads its files.
    ///
    /// WHY THIS MEASURES THE DISK INSTEAD OF PARSING THE CHILD'S OUTPUT
    ///
    /// The obvious approach -- read npm's progress lines -- does not work here. npm only
    /// paints its progress bar on a TTY, and this child's output goes to log files, so
    /// there is no "added 37/208" line to parse. (That redirect is deliberate and load
    /// bearing: see the class comment about the banner.)
    ///
    /// What IS reliably observable is the directory filling up. On the first run npx
    /// creates node_modules and installs into it; measured on a machine that had already
    /// done this once, the finished tree is ~190 top-level packages / ~25,400 files /
    /// ~224 MB. Counting the top-level entries is cheap (one enumeration per sample, no
    /// recursion) and it is exactly the number npm itself would report as "added N".
    ///
    /// The total is not known in advance -- it depends on the harness version -- so no
    /// percentage is invented. The user gets a number that only ever goes up, which is
    /// what "is it still working?" actually needs.
    /// </summary>
    public sealed class Progress
    {
        public int Packets;
        public long Bytes;
        public int ElapsedSec;
        public long TreesSeen;
        public string Phase = "";
        public string Note = "";
    }

    private string progressRoot;
    private DateTime progressNextEntries;
    private DateTime progressNextBytes;
    private int progressLastPackets = -1;
    private long progressLastBytes = -1;
    private string progressPhase = "";
    private const int ProgressEntryIntervalMs = 1500;
    private const int ProgressBytesIntervalMs = 4000;
    private const int ProgressChangeEverySec = 5;

    /// <summary>Name the current stage, so the panel can say something true even before
    /// any files appear (finding the harness, talking to npm, starting the service).</summary>
    public void SetPhase(string text)
    {
        progressPhase = text ?? "";
    }

    /// <summary>
    /// Which directory grows during the first run. Derived from the launcher path rather
    /// than from DshInfo.TreeRoot, because on a first run that tree does not exist yet --
    /// where it WILL be created is what we need, not where it currently is.
    ///
    /// Walks up looking for the "node_modules" component rather than counting a fixed
    /// number of parent hops. Counting hops happened to work for the real layout
    /// (&lt;entry&gt;\node_modules\@deepseek-ai\dsh\lib\bin.js) but silently picked the wrong
    /// directory for any other shape -- in testing it walked straight past the tree and
    /// ended up sampling the whole temp folder (3 entries, 2.3 GB reported). Naming the
    /// thing being looked for is clearer and self-correcting.
    /// </summary>
    private void InitProgressRoot(EnvProbe.DshInfo dsh)
    {
        try
        {
            if (dsh == null || string.IsNullOrEmpty(dsh.LauncherArgs)) return;
            var m = Regex.Match(dsh.LauncherArgs, "\"(?<p>[^\"]+)\"");
            if (!m.Success) return;
            string dir = Path.GetDirectoryName(m.Groups["p"].Value);
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (string.Equals(Path.GetFileName(dir), "node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    progressRoot = dir;
                    // Logged because a wrong root fails quietly: progress simply never
                    // appears, and the next person would have nothing to go on.
                    AppLogger.Write("progress: watching " + progressRoot);
                    return;
                }
                dir = Path.GetDirectoryName(dir);
            }
            AppLogger.Write("progress: no node_modules in the launcher path ("
                + m.Groups["p"].Value + "); progress unavailable");
        }
        catch { }
    }

    /// <summary>
    /// A snapshot of what is on disk now, or null when nothing has changed enough to be
    /// worth reporting. Never throws: a failed sample must not disturb an otherwise
    /// healthy start.
    ///
    /// Cost is bounded on purpose. Counting the top-level entries is one enumeration. The
    /// byte total walks the whole tree, so it runs on a much longer interval than the
    /// count -- both because it is expensive and because a byte figure that jitters is
    /// worse than one that updates less often.
    /// </summary>
    private Progress SampleProgress(DateTime started)
    {
        try
        {
            if (string.IsNullOrEmpty(progressRoot) || !Directory.Exists(progressRoot)) return null;

            var now = DateTime.UtcNow;
            if (now < progressNextEntries) return null;
            progressNextEntries = now.AddMilliseconds(ProgressEntryIntervalMs);

            var entries = new DirectoryInfo(progressRoot).GetFileSystemInfos();
            int packets = entries.Length;

            bool wantBytes = now >= progressNextBytes;
            long bytes = wantBytes ? SumBytes(entries) : progressLastBytes;
            if (wantBytes) progressNextBytes = now.AddMilliseconds(ProgressBytesIntervalMs);

            progressLastPackets = packets;
            progressLastBytes = bytes;

            var p = new Progress();
            p.Packets = packets;
            p.Bytes = bytes < 0 ? 0 : bytes;
            p.ElapsedSec = (int)(now - started).TotalSeconds;
            p.Phase = progressPhase;
            return p;
        }
        catch { return null; }
    }

    private static long SumBytes(FileSystemInfo[] entries)
    {
        long bytes = 0;
        foreach (var e in entries)
        {
            var fi = e as FileInfo;
            if (fi != null) { bytes += fi.Length; continue; }
            var di = e as DirectoryInfo;
            if (di == null) continue;
            try
            {
                foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories)) bytes += f.Length;
            }
            catch { }
        }
        return bytes;
    }

    private static string HumanMB(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 100 ? ((int)Math.Round(mb)) + " MB" : mb.ToString("0.#") + " MB";
    }

    /// <summary>
    /// One line for the panel. Deliberately reports counts and never a percentage: the
    /// final total is unknown until it is done.
    /// </summary>
    public string Describe(Progress p)
    {
        string head = string.IsNullOrEmpty(p.Phase) ? "正在准备运行文件" : p.Phase;
        if (p.Packets <= 0)
        {
            return head + "（已等待 " + p.ElapsedSec + " 秒）";
        }

        string detail = "已下载 " + p.Packets + " 个依赖包";
        if (p.Bytes > 0) detail += "（约 " + HumanMB(p.Bytes) + "）";

        if (p.ElapsedSec >= 10 && p.Bytes > 0)
        {
            // Rough but honest: extrapolate from what has actually landed so far, and only
            // once there is enough of it for a rate to mean anything.
            double mbPerSec = (p.Bytes / (1024.0 * 1024.0)) / p.ElapsedSec;
            if (mbPerSec > 0.05)
            {
                // ~224 MB is a typical finished tree; used as a hint, not as a promise.
                int remaining = (int)Math.Max(0, (224.0 - p.Bytes / (1024.0 * 1024.0)) / mbPerSec);
                if (remaining > 5)
                {
                    detail += "，按当前速度大约还需 " + (remaining >= 90
                        ? ((int)Math.Round(remaining / 60.0)) + " 分钟"
                        : remaining + " 秒");
                }
            }
        }
        return head + "：" + detail;
    }

    private static void PumpAsync(Stream from, Stream to)
    {        try
        {
            var buffer = new byte[8192];
            AsyncCallback read = null;
            read = delegate(IAsyncResult ar)
            {
                try
                {
                    int n = from.EndRead(ar);
                    if (n <= 0) { try { to.Flush(); } catch { } return; }
                    to.Write(buffer, 0, n);
                    to.Flush();
                    from.BeginRead(buffer, 0, buffer.Length, read, null);
                }
                catch { }
            };
            from.BeginRead(buffer, 0, buffer.Length, read, null);
        }
        catch (Exception ex)
        {
            AppLogger.Write("pump failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Read the child's output looking for the access URL. Both streams are
    /// checked: which one carries the banner is an implementation detail we
    /// should not depend on. Only re-reads when a file has grown.
    /// </summary>
    private string ScanForUrl()
    {
        string fromOut = ScanFile(outPath, ref lastScanLength);
        if (fromOut != null) return fromOut;
        long errLen = -1;
        return ScanFile(errPath, ref errLen);
    }

    private static string ScanFile(string path, ref long lastLength)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length == lastLength) return null;
            lastLength = info.Length;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string all = sr.ReadToEnd();
                var m = Regex.Match(all, @"https?://[^\s""'<>]+");
                if (!m.Success) return null;
                string url = m.Value.TrimEnd('.', ',', ')', ']');
                if (url.IndexOf("token=", StringComparison.OrdinalIgnoreCase) < 0) return null;
                return url;
            }
        }
        catch { return null; }
    }

    /// <summary>Tail of both child logs: used to classify a failure and to keep the
    /// evidence in the app log.</summary>
    private string ReadTail()
    {
        string all = "";
        foreach (string p in new string[] { errPath, outPath })
        {
            try
            {
                if (!File.Exists(p)) continue;
                string text;
                // FileShare.ReadWrite is essential: our own pump still holds the file
                // open for writing, and File.ReadAllText asks for FileShare.Read,
                // which Windows refuses while a writer exists -- the read would throw
                // and the tail would silently come back empty. Read it the same way
                // the URL scan does.
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    text = sr.ReadToEnd();
                }
                if (text.Trim().Length == 0) continue;
                if (text.Length > 4000) text = text.Substring(text.Length - 4000);
                if (all.Length > 0) all += Environment.NewLine;
                all += "--- tail of " + Path.GetFileName(p) + " ---" + Environment.NewLine + text;
            }
            catch { }
        }
        return all;
    }

    /// <summary>
    /// The child's output is pumped asynchronously, so the instant the process exits
    /// the log can still be empty -- and an empty tail is a failure that cannot be
    /// classified. Give the pump a bounded moment to catch up.
    /// </summary>
    private string ReadTailSettled()
    {
        string tail = ReadTail();
        for (int i = 0; i < 20 && tail.Trim().Length == 0; i++)
        {
            Thread.Sleep(100);
            tail = ReadTail();
        }
        if (tail.Trim().Length == 0) AppLogger.Write("harness printed nothing before exiting");
        return tail;
    }

    private void DumpTail(string text)
    {
        if (text == null || text.Trim().Length == 0) return;
        if (text.Length > 3000) text = text.Substring(text.Length - 3000);
        AppLogger.Write(text);
    }

    /// <summary>
    /// Work out why the harness died, as far as its own output allows, and keep the
    /// first line of that output so an unclassified failure can still be explained.
    ///
    /// The one that matters most is sharp: it ships its platform binary as an
    /// OPTIONAL package, so a download that fails is not an error for npm. The tree
    /// installs "successfully" (measured: exit code 0, not even a warning), the
    /// harness throws at boot, and the user is left with a bare exit code.
    ///
    /// Order matters: the real sharp log carries BOTH "plugin tree failed to load"
    /// and the sharp signature on the same line, so sharp is tested first. Each
    /// signature here was reproduced experimentally, not guessed -- see the
    /// 09-18 section of 进度.md for the cases and their measured output.
    /// </summary>
    private void ClassifyFailure(string tail)
    {
        FailureDetail = FirstErrorLine(tail);

        if (string.IsNullOrEmpty(tail))
        {
            FailureKind = "crash (no output to classify)";
            AppLogger.Write("harness failure kind: " + FailureKind);
            return;
        }

        if (Has(tail, "Could not load the \"sharp\" module"))
        {
            FailureKind = "sharp";
        }
        else if (Has(tail, "plugin tree failed to load"))
        {
            FailureKind = "plugin";
        }
        else if (Has(tail, "ENOTCACHED") || Has(tail, "ECONNREFUSED") || Has(tail, "ETIMEDOUT")
                 || Has(tail, "EAI_AGAIN") || Has(tail, "ENOTFOUND") || Has(tail, "network request"))
        {
            // Download-time failures. Measured: npm reports these with a stable
            // error code (ENOTCACHED, ETIMEDOUT, ...) that survives a Chinese
            // locale, because the code itself is never translated.
            FailureKind = "network";
        }
        else
        {
            FailureKind = "crash";
        }

        AppLogger.Write("harness failure kind: " + FailureKind
            + (FailureDetail.Length > 0 ? " | first line: " + FailureDetail : ""));
    }

    private static bool Has(string haystack, string needle)
    {
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// The first line worth showing a human, skipping the "--- tail of x.log ---"
    /// separator ReadTail adds, and capped so a stack-trace line cannot fill the
    /// panel.
    /// </summary>
    private static string FirstErrorLine(string tail)
    {
        if (string.IsNullOrEmpty(tail)) return "";
        foreach (string raw in tail.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("--- tail of", StringComparison.Ordinal)) continue;
            if (line.Length > 300) line = line.Substring(0, 300) + "...";
            return line;
        }
        return "";
    }

    public bool IsRunning
    {
        get
        {
            if (process == null) return false;
            try { return !process.HasExited; }
            catch { return false; }
        }
    }

    /// <summary>Stop the server and wait for the port to actually be released.</summary>
    public void Stop()
    {
        AppLogger.Write("stopping harness on port " + Port);

        int listener = EnvProbe.GetListeningPid(Port);
        if (listener > 0) ProcessUtil.KillTree(listener);
        if (wrapperPid > 0 && wrapperPid != listener) ProcessUtil.KillTree(wrapperPid);
        if (process != null)
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && EnvProbe.TestPortOpen(Port, 250))
        {
            Thread.Sleep(250);
        }
        AppLogger.Write("port " + Port + " released = " + !EnvProbe.TestPortOpen(Port, 250));

        try { if (outStream != null) outStream.Dispose(); } catch { }
        try { if (errStream != null) errStream.Dispose(); } catch { }
        outStream = null;
        errStream = null;
        Ready = false;
    }
}
