using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

/// <summary>
/// Entry point. Compiled with /target:winexe on purpose: a console-subsystem
/// binary can be terminated by console control events, which kills it before
/// any logging happens and leaves nothing to diagnose.
/// </summary>
internal static class Program
{
    /// <summary>
    /// The launcher's own version. Bump this for every build handed to a user: it is
    /// the only way to answer "which version is that machine running?" when a problem
    /// is reported, and it is shown in the tray menu and on the control panel so the
    /// user can read it back.
    /// </summary>
    public const string AppVersion = "1.0.0";

    /// <summary>
    /// Held for the life of the process. A second copy of the app would just fight the
    /// first over the service port and leave the user staring at a "port in use" screen
    /// when the program they wanted is already running.
    ///
    /// "Local\" scopes it to the logon session, so two different Windows users can each
    /// run their own copy -- which matters because the port range and the data folders
    /// are per user, not per machine.
    /// </summary>
    private static Mutex singleInstance;

    [STAThread]
    private static void Main(string[] args)
    {
        // Absolute first action: establish a log file, so any later failure is
        // recorded rather than silent.
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH-App");
        string logDir = Path.Combine(dataDir, "logs");
        try
        {
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(logDir);
        }
        catch { }

        AppLogger.Init(logDir);
        AppLogger.Write("================ DSH-App starting ================");
        AppLogger.Write("version: " + AppVersion);
        AppLogger.Write("exe: " + Application.ExecutablePath);
        AppLogger.Write("data: " + dataDir);
        AppLogger.Write("os: " + Environment.OSVersion.VersionString + " 64bit=" + Environment.Is64BitOperatingSystem);
        if (args != null && args.Length > 0) AppLogger.Write("args: " + string.Join(" ", args));

        // Command line first: --help must work without a window, without a singleton
        // check and without touching any settings.
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            AppLogger.Write("bad command line: " + ex.Message);
            MessageBox.Show(ex.Message + "\n\n" + Options.UsageText, "DSH-App",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (options.Help)
        {
            AppLogger.Write("command line help requested; exiting");
            MessageBox.Show(Options.UsageText, "DSH-App 命令行", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // One instance per logon session. Before any window is created, so a second
        // copy never gets as far as opening the service port.
        bool isFirstInstance;
        try
        {
            singleInstance = new Mutex(true, @"Local\DSH-App-SingleInstance", out isFirstInstance);
        }
        catch (Exception ex)
        {
            // Never let this check stop the app from running at all.
            AppLogger.Write("single-instance check unavailable: " + ex.Message);
            isFirstInstance = true;
        }

        if (!isFirstInstance)
        {
            AppLogger.Write("another instance is already running; exiting");
            MessageBox.Show(
                "DSH-App 已经在运行了。\n\n"
                + "它可能只是缩小到了任务栏右下角的托盘里 —— 双击那个图标就能打开。",
                "DSH-App", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Must happen before the first window exists. A DPI-unaware process is told
        // the display is 96 DPI and has its whole window bitmap-stretched by Windows
        // whenever display scaling is not 100%, so the embedded browser is rendered
        // small and then blown up: visibly blurry however well the page is built.
        EnableDpiAwareness();

        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
        {
            AppLogger.Write("UNHANDLED: " + e.ExceptionObject);
        };
        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
        {
            AppLogger.Write("THREAD EXCEPTION: " + e.Exception);
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            AppConfig.Load(dataDir);
            KeyStore.Init(dataDir);

            // Settings given on the command line are written to the same config.ini the
            // interface edits, so there is one source of truth and no hidden state that
            // only exists for this run.
            ApplyOptions(options);

            // The UI is served over loopback HTTP (see UiServer for why file://
            // cannot work), so resolve its directory rather than a single file.
            string uiDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");
            if (!File.Exists(Path.Combine(uiDir, "app.html")))
            {
                string flat = AppDomain.CurrentDomain.BaseDirectory;
                if (File.Exists(Path.Combine(flat, "app.html"))) uiDir = flat;
            }
            bool uiOk = File.Exists(Path.Combine(uiDir, "app.html"));
            AppLogger.Write("ui dir: " + uiDir + " app.html exists=" + uiOk);

            if (!uiOk)
            {
                MessageBox.Show("找不到界面文件：\n" + Path.Combine(uiDir, "app.html")
                    + "\n\n请确认 ui\\app.html 与程序放在一起。",
                    "DSH-App", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(dataDir, logDir, uiDir));
        }
        catch (Exception ex)
        {
            AppLogger.Write("FATAL: " + ex);
            MessageBox.Show(ex.Message, "DSH-App 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            AppLogger.Write("================ DSH-App stopped ================");
        }
    }

    /// <summary>
    /// Writes command line settings into config.ini. Reported into the log so a support
    /// question ("why is it on that port?") has an answer that does not depend on
    /// remembering what was typed weeks ago.
    /// </summary>
    private static void ApplyOptions(Options o)
    {
        if (o.Port > 0)
        {
            AppConfig.Set(AppConfig.KeyPort, o.Port.ToString());
            AppLogger.Write("command line: port set to " + o.Port);
        }
        if (!string.IsNullOrEmpty(o.Workspace))
        {
            AppConfig.Set(AppConfig.KeyWorkspace, o.Workspace);
            AppLogger.Write("command line: workspace set to " + o.Workspace);
        }
        if (o.ResetWindow)
        {
            // The window remembers its size and the page remembers its zoom; a value set
            // on a big monitor can leave the app unusable on a small one. Clearing the
            // zoom is what the tray's own reset does, and it is the one piece of state
            // that can make the interface look broken rather than merely misplaced.
            AppConfig.Remove(AppConfig.KeyZoom);
            AppLogger.Write("command line: window/zoom state reset");
        }
        AppConfig.Save();
    }

    /// <summary>
    /// The command line, deliberately small.
    ///
    /// Everything here has to work for someone who double-clicks the exe and never sees
    /// a prompt, so an unusable value is refused loudly rather than silently ignored --
    /// and one bad flag refuses the whole line, because quietly applying half of what was
    /// asked for is worse than applying none of it.
    /// </summary>
    private sealed class Options
    {
        public bool Help;
        public int Port;
        public string Workspace = "";
        public bool ResetWindow;

        public const string UsageText =
            "DSH-App 命令行参数（不带参数时就是普通的双击启动）：\n\n"
            + "  --port <1024-65535>   设置服务端口，并写入设置文件\n"
            + "  --workspace <目录>    设置助手可读写的工作目录\n"
            + "  --reset-window        重置界面缩放（界面看起来不正常时用）\n"
            + "  --help                显示这段说明\n\n"
            + "例：DSH-App.exe --port 3200\n\n"
            + "说明：不带参数直接双击即可。这些参数写进 %LOCALAPPDATA%\\DSH-App\\config.ini，\n"
            + "和界面里改端口是同一个地方，不会产生两套设置。";

        public static Options Parse(string[] args)
        {
            var o = new Options();
            if (args == null) return o;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim();
                if (a.Length == 0) continue;

                switch (a.ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        o.Help = true;
                        break;

                    case "--reset-window":
                        o.ResetWindow = true;
                        break;

                    case "--port":
                        o.Port = ParsePort(Next(args, ref i, a));
                        break;

                    case "--workspace":
                        string dir = Next(args, ref i, a);
                        if (dir.Trim().Length == 0) throw new ArgumentException("--workspace 需要一个目录。");
                        o.Workspace = dir;
                        break;

                    default:
                        throw new ArgumentException("不认识的参数：" + a);
                }
            }
            return o;
        }

        private static string Next(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(flag + " 后面需要一个值。");
            i++;
            return args[i];
        }

        private static int ParsePort(string text)
        {
            int port;
            // Same range the interface enforces, so a port accepted here is one the rest
            // of the app will agree to use.
            if (!int.TryParse(text.Trim(), out port) || port < 1024 || port > 65535)
            {
                throw new ArgumentException("端口要填 1024 到 65535 之间的数字，收到的是：" + text);
            }
            return port;
        }
    }

    // DPI_AWARENESS_CONTEXT values; -4 is PER_MONITOR_AWARE_V2, -2 is SYSTEM_AWARE.
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    /// <summary>
    /// Stop Windows from virtualising this process.
    ///
    /// Every call is guarded: the context API only exists on Windows 10 1703+ and
    /// shcore only on 8.1+, so a missing export must degrade to the next option
    /// instead of killing startup. Which one won is logged, never assumed.
    /// </summary>
    private static void EnableDpiAwareness()
    {
        int[] contexts = new int[] { -4, -3, -2 };
        foreach (int context in contexts)
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(context)))
                {
                    AppLogger.Write("dpi awareness: SetProcessDpiAwarenessContext(" + context + ")");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("dpi context " + context + " unavailable: " + ex.GetType().Name);
            }
        }

        try
        {
            if (SetProcessDpiAwareness(2) == 0)
            {
                AppLogger.Write("dpi awareness: SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE)");
                return;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("dpi shcore unavailable: " + ex.GetType().Name);
        }

        try
        {
            if (SetProcessDPIAware())
            {
                AppLogger.Write("dpi awareness: SetProcessDPIAware()");
                return;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("dpi user32 unavailable: " + ex.GetType().Name);
        }

        AppLogger.Write("dpi awareness: no call succeeded (already set by Windows?)");
    }
}
