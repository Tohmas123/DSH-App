using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

/// <summary>
/// DSH-App installer.
///
/// A single window that copies the product to a chosen folder and creates a
/// desktop shortcut. Packaged with IExpress so the user receives one file.
///
/// Design decisions worth keeping:
///
///  * The install folder defaults to %LOCALAPPDATA%\Programs, which needs no
///    administrator rights. That is deliberate: a UAC prompt is the single most
///    effective way to lose a non-technical user. Elevation is avoided rather
///    than requested up front.
///
///  * The payload arrives as a cabinet (DSH-App.dat) because IExpress does not
///    compress by itself. Extraction prefers the system `expand.exe`, and falls
///    back to a built-in reader of uncompressed cabinets if that is unavailable.
///
///  * Every step writes to a log file next to the installer. A silent failure in
///    an installer is unfixable by a non-technical user, so nothing is swallowed.
/// </summary>
internal static class Installer
{
    private const string AppName = "DSH-App";
    private const string ShortcutName = "DSH-App.lnk";
    private const string ExecutableName = "DSH-App.exe";

    private static string logPath;
    private static string payloadPath;

    [STAThread]
    private static void Main(string[] rawArgs)
    {
        // When IExpress extracts and runs us, the working directory is its
        // temporary folder; use the executable's own folder to find the payload.
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        payloadPath = Path.Combine(baseDir, "DSH-App.dat");
        // The log goes under %LOCALAPPDATA%, not next to the executable: an
        // installer run from the distribution folder must not leave anything
        // behind there, and when the bundle runs it the executable's own folder is
        // a temporary directory that gets wiped -- taking the log with it exactly
        // when it would be worth reading.
        logPath = DefaultLogPath();

        // Before any window exists, for the same reason as the main app: a
        // DPI-unaware process has its whole window bitmap-stretched by Windows.
        EnableDpiAwareness();

        Log("=== DSH-App installer ===");
        Log("base dir  : " + baseDir);
        Log("payload   : " + payloadPath + " exists=" + File.Exists(payloadPath));

        // A payload left on the Desktop or in Downloads is the single most likely
        // mistake when someone copies the installer around. Look there too.
        if (!File.Exists(payloadPath))
        {
            string[] guesses = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DSH-App.dat"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DSH-App.dat"),
                Path.Combine(baseDir, "DSH-App-setup.dat")
            };
            foreach (string g in guesses)
            {
                if (File.Exists(g)) { payloadPath = g; Log("payload found at " + g); break; }
            }
        }

        // Standalone build: the cabinet is embedded in this executable, so the
        // user only ever handles one file. Materialise it next to the log.
        if (!File.Exists(payloadPath))
        {
            string embedded = TryExtractEmbeddedPayload(baseDir);
            if (embedded != null) payloadPath = embedded;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!File.Exists(payloadPath))
        {
            MessageBox.Show(
                "找不到安装数据文件 DSH-App.dat。\n\n"
                + "请把 DSH-App-setup.exe 和 DSH-App.dat 放在同一个文件夹里再运行。",
                AppName + " 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Unattended mode, for scripted deployment and for automated verification:
        //   /S [/D=<dir>] [/nodesktop] [/noreg] [/launch]
        bool silent = false;
        string silentDir = null;
        bool wantDesktop = true, wantLaunch = false, wantRegister = true;
        foreach (string a in rawArgs)
        {
            if (a.Equals("/S", StringComparison.OrdinalIgnoreCase)
                || a.Equals("/silent", StringComparison.OrdinalIgnoreCase)) silent = true;
            else if (a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)) silentDir = a.Substring(3).Trim('"');
            else if (a.Equals("/nodesktop", StringComparison.OrdinalIgnoreCase)) wantDesktop = false;
            // Test hook, same spirit as the uninstaller's: without it an automated install
            // would rewrite the machine's real "Apps & features" entry for DSH-App and
            // leave it pointing at a throwaway test folder.
            else if (a.Equals("/noreg", StringComparison.OrdinalIgnoreCase)) wantRegister = false;
            else if (a.Equals("/launch", StringComparison.OrdinalIgnoreCase)) wantLaunch = true;
        }

        if (silent)
        {
            Log("unattended install requested, dir=" + (silentDir ?? "(default)"));
            int code = SilentInstall.Run(payloadPath, silentDir, wantDesktop, wantLaunch, wantRegister);
            Log("unattended install finished with code " + code);
            Environment.Exit(code);
        }

        Application.Run(new InstallForm(payloadPath, logPath));
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(logPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// Where the install log lives. Falls back to the executable's own folder only
    /// if the profile location cannot be written.
    /// </summary>
    private static string DefaultLogPath()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH-App");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "install.log");
        }
        catch
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "install.log");
        }
    }

    /// <summary>
    /// Write out the cabinet carried inside this executable, for the standalone
    /// build. Returns the path, or null when this build has no embedded payload.
    /// </summary>
    private static string TryExtractEmbeddedPayload(string baseDir)
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string[] names = asm.GetManifestResourceNames();
            string name = null;
            foreach (string n in names)
            {
                if (n.EndsWith("payload.cab", StringComparison.OrdinalIgnoreCase)) { name = n; break; }
            }
            if (name == null)
            {
                Log("no embedded payload; resource names = " + string.Join(", ", names));
                return null;
            }

            string outPath;
            try { outPath = Path.Combine(Path.GetTempPath(), "DSH-App-payload-" + Guid.NewGuid().ToString("N") + ".cab"); }
            catch { outPath = Path.Combine(baseDir, "DSH-App-embedded.cab"); }

            using (var src = asm.GetManifestResourceStream(name))
            using (var dst = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            {
                if (src == null) return null;
                src.CopyTo(dst);
            }
            Log("embedded payload written to " + outPath + " (" + new FileInfo(outPath).Length + " bytes)");
            return outPath;
        }
        catch (Exception ex)
        {
            Log("embedded payload extraction failed: " + ex.Message);
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    /// <summary>
    /// Stop Windows from virtualising the installer window. Guarded calls only: the
    /// context API is Windows 10 1703+ and shcore 8.1+, so a missing export must
    /// degrade to the next option instead of aborting the install.
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
                    Log("dpi awareness: SetProcessDpiAwarenessContext(" + context + ")");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log("dpi context " + context + " unavailable: " + ex.GetType().Name);
            }
        }

        try
        {
            if (SetProcessDpiAwareness(2) == 0)
            {
                Log("dpi awareness: SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE)");
                return;
            }
        }
        catch (Exception ex)
        {
            Log("dpi shcore unavailable: " + ex.GetType().Name);
        }

        try
        {
            if (SetProcessDPIAware())
            {
                Log("dpi awareness: SetProcessDPIAware()");
                return;
            }
        }
        catch (Exception ex)
        {
            Log("dpi user32 unavailable: " + ex.GetType().Name);
        }

        Log("dpi awareness: no call succeeded (already set by Windows?)");
    }
}

internal sealed class InstallForm : Form
{
    private const string ExecutableName = "DSH-App.exe";
    private const string ShortcutName = "DSH-App.lnk";

    private readonly string payloadPath;
    private readonly string logPath;
    private TextBox pathBox;
    private Button browseButton;
    private Button installButton;
    private Button cancelButton;
    private CheckBox launchBox;
    private CheckBox desktopBox;
    private ProgressBar bar;
    private Label statusLabel;
    private TextBox detailBox;

    private bool finished;
    private bool upgradeMode;

    public InstallForm(string payloadPath, string logPath)
    {
        this.payloadPath = payloadPath;
        this.logPath = logPath;

        Text = "安装 DSH-App";
        ClientSize = new Size(560, 396);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9f);
        // Hand-placed layout: WinForms must not auto-scale it; the scaling happens
        // once from the real DPI in the Load handler below.
        AutoScaleMode = AutoScaleMode.None;

        BuildUi();

        // IExpress launches the payload with a hidden-window flag, so the form gets
        // created but never becomes visible -- the installer appears to do nothing
        // at all. Force it on screen. Harmless on a normal launch, where the form
        // is already visible.
        Load += delegate
        {
            try
            {
                // Controls are positioned in 96 DPI pixels while the font is
                // point-sized and therefore grows with the DPI. Scale once so the
                // two stay in proportion instead of the text outgrowing its boxes.
                int dpi = RealDpi();
                float scale = dpi / 96f;
                if (scale > 1.005f)
                {
                    Scale(new SizeF(scale, scale));
                    Installer.Log("window scaled by " + scale.ToString("0.##") + " for dpi " + dpi);
                }

                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
                Installer.Log("window shown (forced visible for the IExpress launch)");
            }
            catch (Exception ex)
            {
                Installer.Log("could not force the window visible: " + ex.Message);
            }
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// The DPI this window is really on. Control.DeviceDpi reports the virtualised
    /// 96 under .NET Framework 4.8, which would leave the hand-placed layout
    /// unscaled while the point-sized font still grew.
    /// </summary>
    private int RealDpi()
    {
        try
        {
            uint dpi = GetDpiForWindow(Handle);
            if (dpi >= 48) return (int)dpi;
        }
        catch (Exception ex)
        {
            Installer.Log("GetDpiForWindow unavailable: " + ex.GetType().Name);
        }
        return DeviceDpi;
    }

    private void BuildUi()
    {
        // The same package serves two cases. A first-time install must look exactly
        // as it always has; an upgrade is announced, because for an upgrade the one
        // thing the user cares about is whether their chats and settings survive.
        upgradeMode = InstallSteps.LooksLikeUpgrade();

        var title = new Label();
        title.Text = upgradeMode ? "升级 DSH-App" : "安装 DSH-App";
        title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(20, 24, 33);
        title.Location = new Point(24, 20);
        title.AutoSize = true;
        Controls.Add(title);

        var sub = new Label();
        sub.Text = upgradeMode
            ? "检测到已安装过　·　升级只替换程序文件，对话和设置都会保留"
            : "DeepSeek Harness 桌面客户端　·　装好后双击桌面图标即可使用";
        sub.ForeColor = Color.FromArgb(110, 118, 132);
        sub.Location = new Point(26, 54);
        sub.AutoSize = true;
        Controls.Add(sub);

        var line = new Label();
        line.BorderStyle = BorderStyle.Fixed3D;
        line.Location = new Point(24, 84);
        line.Size = new Size(512, 2);
        Controls.Add(line);

        var pathLabel = new Label();
        pathLabel.Text = "安装位置";
        pathLabel.ForeColor = Color.FromArgb(60, 66, 78);
        pathLabel.Location = new Point(26, 100);
        pathLabel.AutoSize = true;
        Controls.Add(pathLabel);

        pathBox = new TextBox();
        pathBox.Location = new Point(26, 122);
        pathBox.Size = new Size(410, 26);
        pathBox.Text = DefaultInstallDir();
        Controls.Add(pathBox);

        browseButton = new Button();
        browseButton.Text = "浏览…";
        browseButton.Location = new Point(444, 121);
        browseButton.Size = new Size(92, 27);
        browseButton.Click += OnBrowse;
        Controls.Add(browseButton);

        desktopBox = new CheckBox();
        desktopBox.Text = "在桌面创建快捷方式";
        desktopBox.Checked = true;
        desktopBox.Location = new Point(26, 158);
        desktopBox.AutoSize = true;
        Controls.Add(desktopBox);

        launchBox = new CheckBox();
        launchBox.Text = "安装完成后立即启动";
        launchBox.Checked = true;
        launchBox.Location = new Point(26, 182);
        launchBox.AutoSize = true;
        Controls.Add(launchBox);

        statusLabel = new Label();
        statusLabel.Text = "准备就绪，点击「开始安装」。";
        statusLabel.ForeColor = Color.FromArgb(60, 66, 78);
        statusLabel.Location = new Point(26, 214);
        statusLabel.AutoSize = true;
        Controls.Add(statusLabel);

        bar = new ProgressBar();
        bar.Location = new Point(26, 236);
        bar.Size = new Size(510, 8);
        bar.Style = ProgressBarStyle.Continuous;
        bar.Minimum = 0;
        bar.Maximum = 100;
        Controls.Add(bar);

        detailBox = new TextBox();
        detailBox.Location = new Point(26, 254);
        detailBox.Size = new Size(510, 78);
        detailBox.Multiline = true;
        detailBox.ReadOnly = true;
        detailBox.ScrollBars = ScrollBars.Vertical;
        detailBox.BackColor = Color.FromArgb(244, 245, 248);
        detailBox.BorderStyle = BorderStyle.FixedSingle;
        detailBox.Font = new Font("Consolas", 8.5f);
        Controls.Add(detailBox);

        installButton = new Button();
        installButton.Text = upgradeMode ? "开始升级" : "开始安装";
        installButton.Location = new Point(336, 344);
        installButton.Size = new Size(100, 32);
        installButton.Click += OnInstall;
        Controls.Add(installButton);

        cancelButton = new Button();
        cancelButton.Text = "退出";
        cancelButton.Location = new Point(442, 344);
        cancelButton.Size = new Size(94, 32);
        cancelButton.Click += delegate { Close(); };
        Controls.Add(cancelButton);

        AcceptButton = installButton;
        CancelButton = cancelButton;
    }

    private static string DefaultInstallDir()
    {
        return InstallSteps.DefaultInstallDir();
    }

    /// <summary>Is a launcher already up? "Closed" does not mean "not running".</summary>
    private static bool IsLauncherRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("DSH-App").Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// Room the first run will need: the harness download measured ~214 MB of
    /// node_modules plus ~147 MB of npm cache, rounded up for headroom.
    /// </summary>
    private const long RequiredFreeBytes = 500L * 1024 * 1024;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directory,
        out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    /// <summary>Free bytes on the volume holding this path, or -1 when unknown.</summary>
    private static long FreeSpaceOn(string path)
    {
        try
        {
            // Walk up to a directory that exists: the target may not be created yet.
            string probe = path;
            while (!string.IsNullOrEmpty(probe) && !System.IO.Directory.Exists(probe))
            {
                string parent = System.IO.Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || parent == probe) break;
                probe = parent;
            }
            ulong available, total, free;
            if (GetDiskFreeSpaceEx(probe, out available, out total, out free))
            {
                return (long)available;
            }
        }
        catch (Exception ex) { Installer.Log("free space check failed: " + ex.Message); }
        return -1;
    }

    private void OnBrowse(object sender, EventArgs e)
    {
        using (var dlg = new FolderBrowserDialog())
        {
            dlg.Description = "选择安装位置";
            dlg.ShowNewFolderButton = true;
            try { dlg.SelectedPath = pathBox.Text; } catch { }
            if (dlg.ShowDialog(this) == DialogResult.OK) pathBox.Text = dlg.SelectedPath;
        }
    }

    private void Say(string text)
    {
        statusLabel.Text = text;
        Detail(text);
        Application.DoEvents();
    }

    private void Detail(string text)
    {
        detailBox.AppendText(text + Environment.NewLine);
        Installer.Log(text);
    }

    private void OnInstall(object sender, EventArgs e)
    {
        string target = pathBox.Text.Trim();
        if (target.Length == 0)
        {
            MessageBox.Show("请先选择安装位置。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // A drive-relative or malformed path is a common paste accident.
        try { target = Path.GetFullPath(target); }
        catch
        {
            MessageBox.Show("这个安装位置不是有效的路径。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Ask before doing anything. Measured: the program files need ~1 MB, but the
        // harness download that follows on first run needs ~360 MB, and a mysterious
        // failure halfway through extraction is much worse than being told up front.
        long free = FreeSpaceOn(target);
        if (free >= 0 && free < RequiredFreeBytes)
        {
            MessageBox.Show(
                "这个位置所在的磁盘空间不够。\n\n"
                + "可用：" + (free / 1024 / 1024) + " MB\n"
                + "建议至少预留：" + (RequiredFreeBytes / 1024 / 1024) + " MB\n\n"
                + "请先清理一些空间，或者换一个磁盘再安装。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Windows cannot replace a running executable. The app parks in the tray when
        // its window is closed, so ask before doing this rather than failing later
        // with a file-in-use error nobody can act on.
        if (IsLauncherRunning())
        {
            var answer = MessageBox.Show(
                "DSH-App 正在运行（可能只是缩小到了托盘）。\n\n"
                + "升级需要先结束它，装完可以再启动。\n"
                + "你的对话、设置、API 密钥和插件都不会丢失。\n\n"
                + "现在结束它并继续吗？",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
        }

        SetBusy(true);
        try
        {
            InstallSteps.Run(payloadPath, target, desktopBox.Checked, Detail);
            bar.Value = 90;
        }
        catch (Exception ex)
        {
            Installer.Log("FATAL: " + ex);
            Detail("");
            Detail("安装失败：" + ex.Message);
            Say("安装失败。");
            MessageBox.Show(
                "安装过程中出错：\n\n" + ex.Message
                + (upgradeMode
                    ? "\n\n如果提示文件被占用，请先从托盘图标退出 DSH-App，再运行本安装包。"
                    : "\n\n如果提示拒绝访问，请换一个位置，或右键以管理员身份运行。")
                + "\n\n修好上面的问题后，可以直接再点一次按钮重试。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetBusy(false);
            // Say plainly that the button is now a retry: the install is a safe
            // overlay, so pressing it again is the right next move and re-running the
            // whole package is not necessary.
            try
            {
                installButton.Text = "重试";
                statusLabel.Text = "安装失败，可以点「重试」再来一次。";
            }
            catch { }
            return;
        }

        finished = true;
        Say(upgradeMode ? "升级完成。" : "安装完成。");
        bar.Value = 100;
        Detail("");
        Detail("已安装到：" + target);

        bool launch = launchBox.Checked;
        MessageBox.Show(
            (upgradeMode
                ? "DSH-App 已升级完成。\n\n你的对话、设置和插件都保留着，没有被改动。\n\n"
                : "DSH-App 安装完成。\n\n"
                  + (desktopBox.Checked ? "桌面已创建快捷方式。\n\n" : "")
                  + "首次启动会自动检查运行环境。"),
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

        if (launch)
        {
            try { InstallSteps.Launch(target, Detail); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        Close();
    }

    private void SetBusy(bool busy)
    {
        installButton.Enabled = !busy;
        browseButton.Enabled = !busy;
        pathBox.Enabled = !busy;
        desktopBox.Enabled = !busy;
        launchBox.Enabled = !busy;
        cancelButton.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing mid-install would leave a half-copied program behind.
        if (!finished && !installButton.Enabled)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }
}
