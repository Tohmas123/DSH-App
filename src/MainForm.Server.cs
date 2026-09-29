using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

/// <summary>
/// MainForm, part: server lifecycle and shutdown
///
/// Split out of MainForm.cs along its existing section comments to keep each file
/// navigable. Same class, same behaviour -- a partial declaration, nothing moved
/// but the text.
/// </summary>
public sealed partial class MainForm : Form
{

// ---------------------------------------------------------------- server lifecycle and shutdown
//
    // ───────────────────────── server lifecycle ─────────────────────────

    private void StartServerAsync()
    {
        StartServerAsync(false);
    }

    /// <param name="forceFreePort">
    /// When true, ignore the configured port entirely and take a free one. Used
    /// after the user chooses that in the port-conflict step.
    /// </param>
    private void StartServerAsync(bool forceFreePort)
    {
        AppLogger.Write("start requested (forceFreePort=" + forceFreePort + ", serverStarted=" + serverStarted + ")");
        if (!forceFreePort && serverStarted) return;
        serverStarted = true;
        starting = true;

        // Measured first-run download: ~214 MB of node_modules (~360 MB including the
        // npm cache), about 100 seconds on a normal connection. Saying so up front is
        // the cheapest fix for the worst part of the first run -- the user knows the
        // wait is expected and roughly how long, instead of watching an unexplained
        // progress bar.
        //
        // Whether this really is a first run is only known after probing (see the npx
        // "download" case below), so this opening line stays generic and the real size
        // warning is sent once the probe has confirmed there is downloading to do.
        Send("{\"type\":\"starting\",\"text\":\"正在启动服务…\"}");

        var t = new Thread(delegate()
        {
            try
            {
                server.SetPhase("正在检查运行环境");
                var node = EnvProbe.ProbeNode();
                server.SetPhase("正在定位 DeepSeek Harness");
                var dsh = EnvProbe.ProbeDsh(node);
                string key = KeyStore.Load();
                string workspace = AppConfig.Get(AppConfig.KeyWorkspace, AppConfig.DefaultWorkspace());
                int port = forceFreePort
                    ? EnvProbe.FindFreePort()
                    : AppConfig.GetInt(AppConfig.KeyPort, 3080);
                if (forceFreePort) AppLogger.Write("using free port " + port + " at the user's request");

                // Recorded before the blocking call below: when a restart is triggered by
                // the watchdog, this line is what separates "we never got here" from "we
                // got here and Start() never returned".
                AppLogger.Write("starting the service on port " + port
                    + " (api key " + (string.IsNullOrEmpty(key) ? "absent" : "present") + ")");
                server.SetPhase("正在启动服务");

                // Only now is it known that there is something to download, so only now is
                // the size warning honest. Telling every user about a 200 MB first run when
                // their files are already there is worry for nothing.
                if (dsh.How == "npx-download")
                {
                    Send("{\"type\":\"progress\",\"text\":" + Json.Quote(
                        "首次运行需要联网下载运行文件（约 200 MB，视网速可能要几分钟），请耐心等待…") + "}");
                }

                string url = server.Start(dsh, node, port, workspace, key, logDir,
                    delegate(int rounds)
                    {
                        // Time-based fallback. Wording matches "nothing measured yet" so the
                        // panel never claims progress it cannot see.
                        int secs = rounds * 300 / 1000;
                        Send("{\"type\":\"progress\",\"text\":" + Json.Quote(
                            "正在启动服务…（已等待 " + secs + " 秒）首次运行需要下载与准备文件，请耐心等待") + "}");
                    },
                    delegate(DshServer.Progress p)
                    {
                        // Measured progress supersedes the elapsed-time sentence.
                        Send("{\"type\":\"progress\",\"text\":" + Json.Quote(server.Describe(p)) + "}");
                    });

                if (url == null && server.PortConflict)
                {
                    // The port belongs to a harness we did not start, so we have no
                    // token for it. Ask the user instead of showing a bare 401 page.
                    //
                    // The common cause right after an upgrade: the installer stops the
                    // old launcher, but its harness child survives and keeps holding the
                    // port. Recognize that case and offer a safe one-click fix instead of
                    // leaving the user to guess.
                    //
                    // The other case is not a process at all: Windows reserved the port, so
                    // the connect probe answers while nothing is listening. There is no pid
                    // to offer a button for, and the UI words it differently.
                    bool orphan = IsLikelyOurHarness(server.ConflictPid);
                    AppLogger.Write("resolving port conflict with the user (orphan harness=" + orphan
                        + ", reserved=" + server.PortReserved + ")");
                    Send("{\"type\":\"portConflict\",\"port\":" + port
                         + ",\"pid\":" + server.ConflictPid
                         + ",\"orphan\":" + (orphan ? "true" : "false")
                         + ",\"reserved\":" + (server.PortReserved ? "true" : "false") + "}");
                    serverStarted = false;
                    return;
                }

                if (url == null)
                {
                    string why = server.LastError ?? "未知原因";
                    AppLogger.Write("server start failed: " + why);

                    if (server.FailureKind == "sharp")
                    {
                        // The one failure the app can fix by itself: say what is wrong in
                        // plain language and offer the repair instead of an exit code.
                        AppLogger.Write("harness died without the sharp module; offering the repair");
                        Send("{\"type\":\"failed\",\"kind\":\"sharp\",\"text\":" + Json.Quote(
                            "启动失败：程序缺少一个图片处理组件（sharp），通常是上次下载没装完整。\n\n"
                            + "点下面的「自动修复并重试」，程序会自己重新安装它（需要联网）。") + "}");
                        serverStarted = false;
                        return;
                    }

                    if (server.FailureKind == "network")
                    {
                        // A download that never completed. Retrying is exactly the right
                        // action here, so the plain 重试 button is the answer -- and it
                        // must retry the SAME port, not silently pick a new one.
                        AppLogger.Write("harness failed on a network/download error; suggesting a retry");
                        Send("{\"type\":\"failed\",\"kind\":\"network\",\"text\":" + Json.Quote(
                            "启动失败：没能下载到运行所需的文件。\n\n"
                            + "通常是网络不通，或公司网络/安全软件拦住了 npm 下载。\n"
                            + "请确认网络可用后点「重试」。") + "}");
                        serverStarted = false;
                        return;
                    }

                    // Not one we can name. Show the harness's own first error line when
                    // there is one: "exit code 1" tells a non-technical user nothing,
                    // and the kind is what decides whether a retry is worth trying.
                    string detail = server.FailureDetail ?? "";
                    string body;
                    if (detail.Length > 0)
                    {
                        body = "启动失败，程序自己没能解决，原因来自它内部的服务：\n\n"
                               + detail
                               + "\n\n完整记录在这里（可发给懂技术的人看）：\n"
                               + logDir;
                    }
                    else
                    {
                        body = "启动失败，而且它内部的服务没有留下任何说明。\n\n"
                               + "完整记录在这里（可发给懂技术的人看）：\n"
                               + logDir;
                    }

                    if (server.FailureKind == "plugin")
                    {
                        body = "启动失败：程序的一个功能组件没能加载。\n\n" + body;
                    }

                    Send("{\"type\":\"failed\",\"kind\":" + Json.Quote(server.FailureKind)
                         + ",\"text\":" + Json.Quote(body) + "}");
                    serverStarted = false;
                    return;
                }

                AppLogger.Write("navigating to " + url);
                string origin = null;
                try
                {
                    var u = new Uri(url);
                    origin = u.Scheme + "://" + u.Authority;
                }
                catch { }

                // Hand the transition to the UI thread in ONE hop, and do the
                // window navigation there rather than trusting a page-side
                // location.href assignment: the host owns this window, so the
                // transition must not depend on the page's script running.
                AppLogger.Write("scheduling navigation (origin=" + (origin ?? "?") + ")");
                string capturedUrl = url;
                string capturedOrigin = origin;
                BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        harnessOrigin = capturedOrigin;
                        recoveryTried = false;
                        crashStreak = 0;
                        // The service just became usable: refresh the tray status line
                        // here, not only when the menu is next opened, or it keeps
                        // claiming "not running" while it is plainly running.
                        UpdateStatusLine();
                        StartWatchdog();

                        // Do NOT navigate into the harness by itself. The window stays
                        // on the control panel so the user chooses: the embedded
                        // window, or the browser. Opening the harness is now driven by
                        // an explicit message from that panel (or from the tray).
                        bool embedded = webView.Source != null
                            && webView.Source.ToString().StartsWith(uiUrl, StringComparison.OrdinalIgnoreCase);
                        if (embedded)
                        {
                            ShowRunningPanel();
                        }
                        else
                        {
                            // The window is already somewhere else (a previous harness
                            // page); bring it back so the choice is visible.
                            ShowControlPanel();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Write("ui transition failed: " + ex);
                    }
                });
                AppLogger.Write("transition scheduled");
            }
            catch (Exception ex)
            {
                AppLogger.Write("start thread threw: " + ex);
                Send("{\"type\":\"failed\",\"text\":" + Json.Quote("启动出错：" + ex.Message) + "}");
                serverStarted = false;
            }
            finally
            {
                starting = false;
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>
    /// Hand a link to the user's default browser.
    ///
    /// Process.Start on a URL goes through ShellExecute, which is what resolves the
    /// default browser. It is tried first; the cmd fallback exists only because a
    /// silent failure here would leave a non-technical user staring at a dead link.
    ///
    /// Both paths are guarded by IsSafeExternalUrl first. The fallback builds a command
    /// line -- "cmd /c start "" &lt;url&gt;" -- and that string must never be assembled from an
    /// unchecked address: a url containing a quote would end the argument early and the rest
    /// of it would run as a command. The url can come from the page (the panel's "open in
    /// browser" button, and the openUrl message), so it is treated as untrusted input.
    /// </summary>
    private void OpenInDefaultBrowser(string url)
    {
        if (!IsSafeExternalUrl(url))
        {
            AppLogger.Write("refusing to open this as a web address: " + url);
            Send("{\"type\":\"openFailed\",\"url\":" + Json.Quote(url ?? "") + "}");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(url);
            AppLogger.Write("opened in the default browser: " + url);
            return;
        }
        catch (Exception ex)
        {
            AppLogger.Write("Process.Start could not open " + url + ": " + ex.Message);
        }

        try
        {
            System.Diagnostics.Process.Start("cmd.exe", "/c start \"\" \"" + url + "\"");
            AppLogger.Write("opened via cmd start: " + url);
        }
        catch (Exception ex)
        {
            AppLogger.Write("could not open " + url + " at all: " + ex.Message);
            Send("{\"type\":\"openFailed\",\"url\":" + Json.Quote(url) + "}");
        }
    }

    /// <summary>
    /// A plain http(s) address, with nothing in it that a command line would act on.
    ///
    /// The rejected characters are the ones that survive the surrounding quotes:
    /// <c>"</c> closes the argument, and cmd.exe expands <c>%NAME%</c> even inside quotes
    /// (so a url could smuggle a variable into what the browser opens). A CR or LF would end
    /// the command line outright. &amp;, |, &lt; and &gt; are deliberately NOT rejected --
    /// inside quotes cmd.exe treats them as ordinary characters, and query strings contain
    /// them.
    /// </summary>
    private static bool IsSafeExternalUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 4096) return false;

        Uri parsed;
        if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return false;
        if (!parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;

        foreach (char c in url)
        {
            if (c == '"' || c == '%' || c < 0x20 || c == 0x7f) return false;
        }
        return true;
    }

    /// <summary>
    /// Reinstall the optional dependency the harness died without, then start again.
    ///
    /// Off the UI thread on purpose: npm can take minutes and the window has to stay
    /// alive while it works.
    ///
    /// The last failure kind is handed over so the repair can tell "installed the
    /// missing package" apart from "gave up and deleted the tree" -- the latter only
    /// helps if the next start can actually download, so it must not be reported as
    /// a plain success.
    /// </summary>
    private void RepairHarnessAsync()
    {
        string triggeringKind = server.FailureKind;
        Send("{\"type\":\"repairing\",\"text\":\"正在重新安装缺失的组件…需要联网，可能要几分钟\"}");

        var t = new Thread(delegate()
        {
            try
            {
                var node = EnvProbe.ProbeNode();
                var dsh = EnvProbe.ProbeDsh(node);
                string message;
                bool ok = EnvProbe.RepairOptionalDeps(dsh, triggeringKind, out message);
                AppLogger.Write("repair finished: ok=" + ok + " - " + message);

                if (ok)
                {
                    Send("{\"type\":\"repairing\",\"text\":" + Json.Quote("修复完成（" + message + "），正在重新启动服务…") + "}");
                    serverStarted = false;
                    StartServerAsync();
                }
                else
                {
                    Send("{\"type\":\"failed\",\"text\":" + Json.Quote(
                        "自动修复没能完成：" + message
                        + "\n\n可以手动打开 PowerShell 执行：\nnpm install --include=optional sharp") + "}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("repair threw: " + ex);
                Send("{\"type\":\"failed\",\"text\":" + Json.Quote("修复过程出错：" + ex.Message) + "}");
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // ───────────────────────── shutdown ─────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (tray == null) SetUpTray();
    }

    private void SetUpTray()
    {
        try
        {
            tray = new NotifyIcon();
            tray.Icon = Icon ?? SystemIcons.Application;
            tray.Text = "DSH-App";
            tray.Visible = true;

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, delegate { RestoreFromTray(); });
            menu.Items.Add(new ToolStripSeparator());

            // Read-only status line: which port the service is on and which process
            // is serving it. Someone who also drives the harness from a terminal
            // needs exactly this to avoid fighting over the port, and it is the
            // quickest way to see whether the service is up at all.
            trayStatusItem = new ToolStripMenuItem();
            trayStatusItem.Enabled = false;
            menu.Items.Add(trayStatusItem);
            menu.Items.Add(new ToolStripSeparator());

            // Same tokenised URL the embedded window uses. It stays valid for the
            // whole session (measured: a second visitor with an empty cookie jar
            // gets its own fresh session cookie), so the browser becomes a second,
            // independent view of the same harness.
            menu.Items.Add("在浏览器中打开服务页面", null, delegate { OpenHarnessInBrowser(); });
            // The terminal user's counterpart to a hard kill: this leaves no orphan
            // harness behind, which killing the window from Task Manager does.
            menu.Items.Add("停止服务", null, delegate { StopServer(); });
            menu.Items.Add("重启服务", null, delegate { RestartServer(); });
            menu.Items.Add("修改服务端口…", null, delegate { ChangePort(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开工作目录", null, delegate
            {
                try
                {
                    string ws = AppConfig.Get(AppConfig.KeyWorkspace, AppConfig.DefaultWorkspace());
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + ws + "\"");
                }
                catch { }
            });
            menu.Items.Add("打开日志目录", null, delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + logDir + "\""); }
                catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于 DSH-App v" + Program.AppVersion, null, delegate
            {
                MessageBox.Show(
                    "DSH-App 启动器版本：" + Program.AppVersion + "\n"
                    + "DeepSeek Harness 版本：" + (HarnessVersion().Length > 0 ? HarnessVersion() : "未知") + "\n\n"
                    + "程序目录：\n" + AppDomain.CurrentDomain.BaseDirectory + "\n\n"
                    + "数据目录（对话、设置、密钥）：\n" + dataDir + "\n\n"
                    + "升级只替换程序文件，不会改动上面的数据目录。",
                    "关于 DSH-App", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            menu.Items.Add("退出", null, delegate { QuitApp(); });
            tray.ContextMenuStrip = menu;

            // Keep the status line honest: refresh when the menu opens and right
            // after any action that changes the service.
            menu.Opening += delegate { UpdateStatusLine(); };
            UpdateStatusLine();

            tray.DoubleClick += delegate { RestoreFromTray(); };
        }
        catch (Exception ex)
        {
            AppLogger.Write("tray setup failed: " + ex.Message);
        }
    }

    /// <summary>
    /// The tray's "show window" now means "bring me back to the control panel":
    /// from the panel the user can pick either way in, which is the whole point of
    /// keeping the window there.
    /// </summary>
    private void RestoreFromTray()
    {
        try
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            ShowControlPanel();
        }
        catch { }
    }

}
