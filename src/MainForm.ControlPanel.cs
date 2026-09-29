using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

/// <summary>
/// MainForm, part: the control panel and service watchdog
///
/// Split out of MainForm.cs along its existing section comments to keep each file
/// navigable. Same class, same behaviour -- a partial declaration, nothing moved
/// but the text.
/// </summary>
public sealed partial class MainForm : Form
{

// ---------------------------------------------------------------- control panel
//
    // ─────────────────── control panel (the window's home screen) ───────────────────

    /// <summary>
    /// Put the window back on the app's own panel. Everything else the panel needs
    /// is pushed with it, so the page is a pure renderer.
    ///
    /// Marshals to the UI thread first: WebView2 may only be touched from there
    /// (rule 6), and this is reachable from a tray click, from the window itself and
    /// from the startup thread. Getting it wrong fails quietly -- it only logs
    /// "CoreWebView2 can only be accessed from the UI thread" inside a catch -- and
    /// the panel simply never comes back.
    /// </summary>
    private void ShowControlPanel()
    {
        try
        {
            if (IsHandleCreated && InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { ShowControlPanel(); });
                return;
            }
            if (webView.CoreWebView2 == null) return;
            bool here = webView.Source != null
                && webView.Source.ToString().StartsWith(uiUrl, StringComparison.OrdinalIgnoreCase);
            if (!here)
            {
                AppLogger.Write("returning the window to the control panel");
                webView.CoreWebView2.Navigate(uiUrl);
            }
            ShowRunningPanel();
        }
        catch (Exception ex)
        {
            AppLogger.Write("could not show the control panel: " + ex.Message);
        }
    }

    /// <summary>Tell the panel what is running, and whether it can be opened yet.</summary>
    private void ShowRunningPanel()
    {
        bool ready = !string.IsNullOrEmpty(server.Url);
        string json = "{\"type\":\"panel\""
                      + ",\"version\":" + Json.Quote(Program.AppVersion)
                      + ",\"harness\":" + Json.Quote(HarnessVersion())
                      + ",\"upgradedFrom\":" + Json.Quote(previousVersion)
                      + ",\"port\":" + ReadCurrentPort()
                      + ",\"pid\":" + server.ServerPid
                      + ",\"ready\":" + (ready ? "true" : "false")
                      + ",\"text\":" + Json.Quote("正在启动服务（首次运行需要下载，约 200 MB）…") + "}";
        Send(json);
    }

    /// <summary>
    /// Put a line of progress on the control panel. Used for actions that have no
    /// output of their own (installing a plugin), so the user is not left staring at
    /// an unchanged window wondering whether the click registered.
    /// </summary>
    private void SetPanelProgress(string text)
    {
        Send("{\"type\":\"panel\",\"version\":" + Json.Quote(Program.AppVersion)
             + ",\"port\":" + ReadCurrentPort()
             + ",\"pid\":" + server.ServerPid
             + ",\"ready\":false"
             + ",\"text\":" + Json.Quote(text) + "}");
    }

    /// <summary>Same, but safe to call from a background thread.</summary>
    private void PostPanelProgress(string text)
    {
        try
        {
            BeginInvoke((MethodInvoker)delegate { SetPanelProgress(text); });
        }
        catch (Exception ex)
        {
            AppLogger.Write("could not post panel progress: " + ex.Message);
        }
    }

    /// <summary>
    /// Open the harness in the window this app owns. This is the old default
    /// behaviour, now driven by the user pressing the panel's button.
    /// </summary>
    private void OpenEmbeddedWindow()
    {
        string url = server.Url;
        if (string.IsNullOrEmpty(url))
        {
            AppLogger.Write("embedded window requested but the service is not ready yet");
            ShowBalloon("服务还没准备好，请稍候再试。", ToolTipIcon.Info);
            return;
        }
        AppLogger.Write("user chose the embedded window");
        // Clear any cookie from a previous run, then navigate in its continuation:
        // the continuation is required because sync-waiting here deadlocks the UI.
        ClearHarnessCookiesThen(url, delegate
        {
            try
            {
                AppLogger.Write("host navigating window to harness (user asked)");
                webView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex) { AppLogger.Write("embedded navigation failed: " + ex.Message); }
        });
    }

    // ───────────────────────── environment check ─────────────────────────
    /// <summary>
    /// Block until a start attempt that is already running has finished.
    ///
    /// A start blocks for seconds (first run: minutes, while it downloads) before it
    /// reports back. Any stop/restart/port change arriving inside that window would
    /// otherwise operate on the same DshServer while it is mid-start: it would close
    /// the log streams and kill the process under the polling loop, and the harness
    /// that had just come up would be abandoned on the old port. Serialising the
    /// operations is cheap and removes the whole class of problem.
    /// </summary>
    private void WaitForInFlightStart(string why)
    {
        if (!starting) return;
        AppLogger.Write(why + ": waiting for the in-flight start to finish");
        for (int i = 0; i < 120 && starting; i++) Thread.Sleep(500);
        AppLogger.Write(why + ": in-flight start finished (starting=" + starting + ")");
    }

    private void RestartServer()
    {
        var t = new Thread(delegate()
        {
            try
            {
                // Same reason as StopServer: never pull the service out from under a
                // start that is still coming up.
                WaitForInFlightStart("restart");
                server.Stop();
                serverStarted = false;
                BeginInvoke((MethodInvoker)delegate { UpdateStatusLine(); });
                StartServerAsync();
            }
            catch (Exception ex) { AppLogger.Write("restart failed: " + ex.Message); }
        });
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>
    /// Make the freshly installed plugin show up without asking the user to do
    /// anything. The harness assembles its client plugins per request, so reloading
    /// is enough to pick up the new bundle -- and if the running service cached its
    /// own boot-time bundle list, the reload still lands the user on a page where the
    /// tray's「重启服务」fixes it, instead of leaving them staring at the control panel
    /// wondering whether the install worked.
    /// </summary>
    private void ReloadHarnessUi()
    {
        try
        {
            if (webView.CoreWebView2 == null) return;
            string url = server.Url;
            if (string.IsNullOrEmpty(url))
            {
                // No service to show: stay on the panel, which is the right place.
                return;
            }
            AppLogger.Write("reloading the harness so the new plugin is picked up");
            ClearHarnessCookiesThen(url, delegate
            {
                try { webView.CoreWebView2.Navigate(url); }
                catch (Exception ex) { AppLogger.Write("reload navigate failed: " + ex.Message); }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Write("could not reload after install: " + ex.Message);
        }
    }

    /// <summary>
    /// Is the process holding our port one of our own leftover harnesses?
    ///
    /// Deliberately strict: only a node process whose command line actually runs the
    /// harness launcher counts. Anything else is somebody else's program and must
    /// never be killed -- a bare "free the port" button would be dangerous.
    /// </summary>
    private static bool IsLikelyOurHarness(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using (var p = System.Diagnostics.Process.GetProcessById(pid))
            {
                if (!string.Equals(p.ProcessName, "node", StringComparison.OrdinalIgnoreCase)) return false;
            }
            using (var q = new System.Management.ManagementObjectSearcher(
                       "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
            {
                foreach (System.Management.ManagementBaseObject mo in q.Get())
                {
                    object raw = mo["CommandLine"];
                    string cmd = raw == null ? "" : raw.ToString();
                    return cmd.IndexOf("dsh", StringComparison.OrdinalIgnoreCase) >= 0
                           && cmd.IndexOf("bin.js", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("orphan check failed for pid " + pid + ": " + ex.Message);
        }
        return false;
    }

    /// <summary>
    /// End a leftover harness that is holding the configured port, then start again.
    ///
    /// This is the tidy way out of the upgrade case: the installer stops the old
    /// launcher, but its harness child survives and keeps the port. Killing just the
    /// listener is enough -- its parent is already gone, so nothing is orphaned in turn.
    /// </summary>
    private void EndOrphanHarness(int pid)
    {
        if (!IsLikelyOurHarness(pid))
        {
            // Re-checked here on purpose: this is the last gate before killing anything.
            AppLogger.Write("refusing to end pid " + pid + ": not recognized as our harness");
            Send("{\"type\":\"portError\",\"text\":" + Json.Quote(
                "那个进程看起来不是本程序启动的服务，所以没有替你结束它。可以用「换个端口重新启动」。") + "}");
            return;
        }

        var t = new Thread(delegate()
        {
            try
            {
                AppLogger.Write("ending the leftover harness on pid " + pid);
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                {
                    p.Kill();
                    p.WaitForExit(10000);
                }
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline
                       && EnvProbe.TestPortOpen(AppConfig.GetInt(AppConfig.KeyPort, 3080), 250))
                {
                    Thread.Sleep(250);
                }
                AppLogger.Write("leftover harness ended; starting the service again");
                BeginInvoke((MethodInvoker)delegate
                {
                    serverStarted = false;
                    StartServerAsync();
                });
            }
            catch (Exception ex)
            {
                AppLogger.Write("could not end the leftover harness: " + ex.Message);
                Send("{\"type\":\"portError\",\"text\":" + Json.Quote(
                    "没能结束那个进程：" + ex.Message + "\n可以用「换个端口重新启动」。") + "}");
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>
    /// Stop the service without restarting it and without leaving anything behind.
    ///
    /// Killing the window or the process from Task Manager orphans the harness:
    /// it keeps holding the port and a terminal user then cannot start their own.
    /// This path uses the same tree-killing shutdown the app itself uses on exit.
    /// </summary>
    private void StopServer()
    {
        var t = new Thread(delegate()
        {
            try
            {
                // Never close the log streams and kill the process while a start is
                // still polling them; wait for it to settle first.
                WaitForInFlightStart("stop");
                server.Stop();
                serverStarted = false;
                BeginInvoke((MethodInvoker)delegate
                {
                    UpdateStatusLine();
                    ShowBalloon("服务已停止。端口已释放，你也可以在终端里自己启动。", ToolTipIcon.Info);
                });
            }
            catch (Exception ex)
            {
                AppLogger.Write("stop service failed: " + ex.Message);
                BeginInvoke((MethodInvoker)delegate
                {
                    ShowBalloon("停止服务失败：" + ex.Message, ToolTipIcon.Warning);
                });
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>
    /// Open the running service in the user's default browser.
    ///
    /// This uses the tokenised URL directly. Measured: the URL is not consumed by
    /// the embedded window -- a second visitor with an empty cookie jar is handed
    /// its own fresh session cookie -- so the browser and the embedded window can
    /// both use the same harness at the same time.
    /// </summary>
    private void OpenHarnessInBrowser()
    {
        string url = server.Url;
        if (string.IsNullOrEmpty(url))
        {
            AppLogger.Write("browser page requested but no harness URL is available yet");
            ShowBalloon(serverStarted
                ? "服务还在启动中，请稍候再试。"
                : "服务当前没有运行。可以先用「重启服务」启动它。", ToolTipIcon.Info);
            return;
        }
        AppLogger.Write("opening the harness page in the default browser: " + url);
        OpenInDefaultBrowser(url);
        ShowBalloon("已在浏览器中打开。内置窗口仍然可以继续使用。", ToolTipIcon.Info);
    }

    /// <summary>
    /// Show what is running where. Read-only by design: nothing here is a control.
    /// </summary>
    private void UpdateStatusLine()
    {
        if (trayStatusItem == null) return;
        int port = ReadCurrentPort();
        bool up = serverStarted || server.Ready || server.IsRunning
                  || (port > 0 && EnvProbe.TestPortOpen(port, 120));
        if (up && port > 0)
        {
            trayStatusItem.Text = "服务运行中：" + port + " 端口　进程 " + server.ServerPid;
        }
        else
        {
            trayStatusItem.Text = "服务未运行（已配置端口 " + port + "）";
        }
    }

    /// <summary>
    /// Record the running version in config.ini and report whether this start is the
    /// first one after an upgrade.
    ///
    /// The exact version is written (not just "an upgrade happened") so the panel can
    /// say "from x to y" and so the next upgrade has a baseline. An empty previous
    /// value means either a brand-new install or a version so old that it knew nothing
    /// about versioning -- both get the same wording, because in both cases the
    /// launcher has just changed under the user.
    /// </summary>
    private void NoticeUpgrade()
    {
        try
        {
            string previous = AppConfig.Get(AppConfig.KeyLastRunVersion, "");
            previousVersion = previous;

            if (previous.Length == 0)
            {
                AppLogger.Write("first run of a version-aware build (version " + Program.AppVersion + ")");
                upgradeNotice = "欢迎使用 DSH-App（启动器版本 v" + Program.AppVersion + "）。";
            }
            else if (!previous.Equals(Program.AppVersion, StringComparison.OrdinalIgnoreCase))
            {
                // Direction-neutral on purpose: the release number can go backwards when a
                // version was built but never handed out, and telling a user they were
                // "upgraded from v1.0.1 to v1.0.0" is nonsense they cannot act on. The point
                // of the message is "the program under you changed, your data did not".
                AppLogger.Write("launcher version changed: " + previous + " -> " + Program.AppVersion);
                upgradeNotice = "启动器已更新：v" + previous + " → v" + Program.AppVersion
                                + "。你的对话、设置和插件都没有被改动。";
            }
            else
            {
                AppLogger.Write("launcher version unchanged: " + Program.AppVersion);
            }

            AppConfig.Set(AppConfig.KeyLastRunVersion, Program.AppVersion);
            AppConfig.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Write("upgrade notice failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Which harness build is actually installed, resolved once and remembered.
    ///
    /// The launcher's own version says nothing about this: the harness is fetched by
    /// npx and can be any build. Without this there is no way to answer "which harness
    /// is that machine running?" -- the blind spot that made the 2026-09-20 startup
    /// failure slow to place. Cached because it only changes when the harness is
    /// reinstalled, which cannot happen while this window is open.
    /// </summary>
    private string HarnessVersion()
    {
        if (harnessVersion == null)
        {
            try
            {
                var dsh = EnvProbe.ProbeDsh(EnvProbe.ProbeNode());
                harnessVersion = dsh.VersionDetail.Length > 0 ? dsh.VersionDetail : "";
            }
            catch (Exception ex)
            {
                AppLogger.Write("harness version unavailable: " + ex.Message);
                harnessVersion = "";
            }
        }
        return harnessVersion;
    }

    /// <summary>Tray balloon, used where a modal dialog would block the UI thread.</summary>
    private void ShowBalloon(string text, ToolTipIcon icon)
    {
        try
        {
            if (tray != null && tray.Visible) tray.ShowBalloonTip(4000, "DSH-App", text, icon);
        }
        catch { }
    }

    /// <summary>
    /// The port from config.ini, read straight off disk.
    ///
    /// Deliberately not "the port the running service happens to be on": the panel shows
    /// the CONFIGURED port so that someone who also drives the harness from a terminal
    /// can see what this app will use next, whether or not a service is up right now.
    /// </summary>
    private static int ReadCurrentPort()
    {
        try
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH-App");
            string path = Path.Combine(dataDir, "config.ini");
            if (File.Exists(path))
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string s = line.Trim();
                    if (!s.StartsWith("port=", StringComparison.OrdinalIgnoreCase)) continue;
                    int p;
                    // Same accepted range as the interface and the command line, so all
                    // three agree on what a usable port is.
                    if (int.TryParse(s.Substring(5).Trim(), out p) && p >= 1024 && p <= 65535) return p;
                }
            }
        }
        catch { }
        return AppConfig.GetInt(AppConfig.KeyPort, 3080);
    }

    /// <summary>
    /// Change the port the service listens on, so a terminal user can have a port
    /// of their own choosing and the two never fight over the same one.
    ///
    /// Written to config.ini; it takes effect on the next start. It never touches a
    /// service that is already running on the old port.
    /// </summary>
    private void ChangePort()
    {
        int current = ReadCurrentPort();
        string entered = PromptForPort(current);
        if (entered == null) return;

        int next;
        if (!int.TryParse(entered.Trim(), out next) || next < 1024 || next > 65535)
        {
            MessageBox.Show("端口要填 1024 到 65535 之间的数字。", "DSH-App",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (next == current)
        {
            AppLogger.Write("port unchanged (" + current + ")");
            return;
        }

        AppConfig.Set(AppConfig.KeyPort, next.ToString());
        AppConfig.Save();
        AppLogger.Write("port changed: " + current + " -> " + next + " (takes effect on restart)");
        UpdateStatusLine();

        string question = "端口已改为 " + next + "。\n\n现在就重启服务吗？\n"
                          + "（不重启的话，下次启动程序时才会用新端口）";
        if (MessageBox.Show(question, "DSH-App", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            == DialogResult.Yes)
        {
            serverStarted = false;
            RestartServer();
        }
    }

    /// <summary>Minimal numeric prompt; returns null when cancelled.</summary>
    private static string PromptForPort(int current)
    {
        using (var dlg = new Form())
        {
            dlg.Text = "修改服务端口";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterScreen;
            dlg.MinimizeBox = false;
            dlg.MaximizeBox = false;
            dlg.ClientSize = new Size(330, 132);
            dlg.Font = new Font("Microsoft YaHei UI", 9f);

            var label = new Label();
            label.Text = "服务监听的端口（1024-65535）：";
            label.AutoSize = true;
            label.Location = new Point(16, 16);
            dlg.Controls.Add(label);

            var box = new TextBox();
            box.Text = current.ToString();
            box.Location = new Point(16, 44);
            box.Width = 296;
            dlg.Controls.Add(box);

            var ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(136, 86);
            ok.Width = 84;
            dlg.Controls.Add(ok);

            var cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(228, 86);
            cancel.Width = 84;
            dlg.Controls.Add(cancel);

            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            return dlg.ShowDialog() == DialogResult.OK ? box.Text : null;
        }
    }


// ---------------------------------------------------------------- the service watchdog
//
    // ───────────────────────────── 服务看门狗 ─────────────────────────────

    /// <summary>
    /// Watches the harness after it is up and puts it back if it dies.
    ///
    /// Without this, a harness crash mid-conversation leaves the user looking at a
    /// dead page with no explanation -- the app itself is perfectly healthy, so
    /// nothing else notices. This is the "crash auto-recovery" requirement.
    ///
    /// Health is checked with a single TCP connect to loopback, which costs
    /// milliseconds. It runs on a timer rather than a thread so a slow check can
    /// never pile up.
    /// </summary>
    private void StartWatchdog()
    {
        if (watchdog != null) return;
        watchdog = new System.Windows.Forms.Timer();
        watchdog.Interval = 10000;
        watchdog.Tick += delegate { WatchdogTick(); };
        watchdog.Start();
        AppLogger.Write("service watchdog started (10s interval)");
    }

    private void StopWatchdog()
    {
        if (watchdog == null) return;
        try { watchdog.Stop(); watchdog.Dispose(); } catch { }
        watchdog = null;
    }

    private void WatchdogTick()
    {
        if (quitting || !serverStarted || server.Ready == false) return;

        int port = server.Port;
        if (port <= 0) return;

        // The port is the authority: the process may have been replaced without the
        // exit being observed, and a listener that answers is what the user needs.
        if (EnvProbe.TestPortOpen(port, 250))
        {
            crashStreak = 0;
            return;
        }

        crashStreak++;
        AppLogger.Write("watchdog: port " + port + " is not answering (miss " + crashStreak + "/3)");
        if (crashStreak < 3) return;

        crashStreak = 0;
        AppLogger.Write("watchdog: harness is gone; restarting it");
        Notify("服务意外停止，正在自动重启…", ToolTipIcon.Warning);
        RestartServer();
    }

    private void Notify(string text, ToolTipIcon icon)
    {
        try
        {
            if (tray != null && tray.Visible) tray.ShowBalloonTip(4000, "DSH-App", text, icon);
        }
        catch { }
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (quitting)
        {
            Shutdown();
            return;
        }

        // A tray icon keeps the app reachable: closing the window parks it there
        // rather than tearing down the window, matching how real desktop apps behave.
        if (tray != null && tray.Visible)
        {
            e.Cancel = true;
            Hide();
            tray.ShowBalloonTip(2500, "DSH-App", "已最小化到托盘，服务继续在后台运行。右键托盘图标可退出。",
                ToolTipIcon.Info);
            AppLogger.Write("window hidden to tray");
        }
        else
        {
            Shutdown();
        }
    }

    private void QuitApp()
    {
        quitting = true;
        Close();
    }

    private void Shutdown()
    {
        AppLogger.Write("=== shutdown");
        StopWatchdog();
        try
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        }
        catch { }

        try
        {
            if (server.IsRunning || server.Ready) server.Stop();
        }
        catch (Exception ex) { AppLogger.Write("server stop failed: " + ex.Message); }

        try
        {
            if (webView.CoreWebView2 != null) webView.Dispose();
        }
        catch (Exception ex) { AppLogger.Write("webview dispose failed: " + ex.Message); }

        try { uiServer.Stop(); }
        catch (Exception ex) { AppLogger.Write("ui server stop failed: " + ex.Message); }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        AppLogger.Write("=== exit complete");
    }
}

/// <summary>
/// Three-field JSON helpers. Enough for the fixed messages this bridge carries,
/// and no dependency on a serialiser.
/// </summary>
public static class Json
{
    public static string GetString(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return "";
        string needle = "\"" + field + "\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return "";
        i = json.IndexOf(':', i + needle.Length);
        if (i < 0) return "";
        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length) return "";

        if (json[i] == '"')
        {
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else if (n == '"') sb.Append('"');
                    else if (n == '\\') sb.Append('\\');
                    else if (n == 'u' && i + 5 < json.Length)
                    {
                        int code;
                        if (int.TryParse(json.Substring(i + 2, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 6;
                            continue;
                        }
                        sb.Append(n);
                    }
                    else sb.Append(n);
                    i += 2;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        int start = i;
        while (i < json.Length && json[i] != ',' && json[i] != '}') i++;
        return json.Substring(start, i - start).Trim();
    }

    /// <summary>Numeric field, used by the port messages (which carry a bare number).</summary>
    public static int GetInt(string json, string field)
    {
        string raw = GetString(json, field).Trim('"', ' ');
        int v;
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
        return -1;
    }

    public static string Quote(string s)
    {
        if (s == null) return "\"\"";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
