using System;
using System.IO;
using System.Text;

/// <summary>
/// Minimal file logger. Deliberately simple and dependency-free.
///
/// Two hard-won rules are baked in here:
///   1. Every write is flushed immediately. A GUI app that crashes must still
///      leave a usable trail -- buffered logs vanish exactly when they matter.
///   2. The log rotates instead of being truncated. Overwriting the log on each
///      start (as a naive redirect does) destroys the evidence of why the
///      previous run failed.
/// </summary>
public static class AppLogger
{
    private static readonly object Gate = new object();
    private static string logDir;
    private static string logFile;
    private const long MaxBytes = 1024 * 1024;

    public static void Init(string directory)
    {
        logDir = directory;
        Directory.CreateDirectory(logDir);
        logFile = Path.Combine(logDir, "app.log");
        Rotate();
    }

    public static string LogDirectory
    {
        get { return logDir; }
    }

    private static void Rotate()
    {
        try
        {
            if (!File.Exists(logFile)) return;
            if (new FileInfo(logFile).Length < MaxBytes) return;
            string old = Path.Combine(logDir, "app.prev.log");
            if (File.Exists(old)) File.Delete(old);
            File.Move(logFile, old);
        }
        catch { }
    }

    public static void Write(string message)
    {
        if (logFile == null) return;
        lock (Gate)
        {
            try
            {
                Rotate();
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                              + "  " + message + Environment.NewLine;
                using (var fs = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.Write(line);
                    sw.Flush();
                    fs.Flush();
                }
            }
            catch { }
        }
    }

    public static void Write(string format, params object[] args)
    {
        try { Write(string.Format(format, args)); }
        catch { Write(format); }
    }
}
