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
/// The application window: hosts the WebView2 control, runs the environment
/// check, drives the first-run steps, owns the harness process, and closes
/// cleanly.
///
/// A deliberate design rule throughout: the WebView2 control is the ONLY child
/// of the form, so attaching a tray icon or changing the window layout can never
/// confuse its lifetime.
///
/// This class is split across several partial files purely to keep each file
/// navigable -- see MainForm.Messages.cs, MainForm.HarnessCheck.cs,
/// MainForm.Server.cs and MainForm.ControlPanel.cs. Nothing is defined twice:
/// this file holds the fields, the constructor and startup.
/// </summary>
public sealed partial class MainForm : Form
{
    private readonly WebView2 webView = new WebView2();
    private readonly DshServer server = new DshServer();
    private readonly UiServer uiServer = new UiServer();
    private NotifyIcon tray;
    private ToolStripMenuItem trayStatusItem;
    private System.Windows.Forms.Timer watchdog;
    // These three are written on one thread and read on another (the UI thread versus
    // the start/watchdog threads). On x64 the reads and writes are atomic anyway, so
    // this is about removing a theoretical hazard rather than fixing an observed bug --
    // but it is free, and it makes the intent explicit for whoever reads this next.
    private volatile int crashStreak;
    private volatile bool quitting;
    private volatile bool serverStarted;
    // True while a start attempt is in flight. Needed because a start blocks for
    // seconds waiting for the harness URL, and a port change arriving in that
    // window must not start a second harness on top of the first one.
    private volatile bool starting;
    private string upgradeNotice = "";
    private string previousVersion = "";
    // Cached harness build string; null means "not looked up yet".
    private string harnessVersion;

    private string dataDir;
    private string logDir;
    private string uiDir;
    private string uiUrl;

    // Optional interface zoom from config.ini. Negative means "not configured",
    // which is different from 1.0: an explicit 1.0 would overwrite whatever zoom the
    // user set with Ctrl + / Ctrl + wheel, which the browser stores per origin.
    private float configuredZoom = -1f;
    private const float MinZoom = 0.5f;
    private const float MaxZoom = 3f;

    // Design geometry, expressed in 96 DPI pixels; scaled up from the real DPI
    // in OnLoaded so the window matches the display it is actually running on.
    private const int BaseWidth = 1180;
    private const int BaseHeight = 800;
    private const int BaseMinWidth = 760;
    private const int BaseMinHeight = 520;

    public MainForm(string dataDir, string logDir, string uiDir)
    {
        this.dataDir = dataDir;
        this.logDir = logDir;
        this.uiDir = uiDir;

        Text = "DSH-App";
        Width = BaseWidth;
        Height = BaseHeight;
        MinimumSize = new Size(BaseMinWidth, BaseMinHeight);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 17, 21);
        // Built in code, so there are no designer dimensions for WinForms to
        // auto-scale; sizing is done explicitly from the real DPI instead.
        AutoScaleMode = AutoScaleMode.None;
        try { Icon = LoadAppIcon(); } catch { }

        string zoomRaw = AppConfig.Get(AppConfig.KeyZoom, "");
        if (zoomRaw.Length > 0)
        {
            configuredZoom = AppConfig.GetFloat(AppConfig.KeyZoom, 1f);
            if (configuredZoom < MinZoom)
            {
                configuredZoom = MinZoom;
                AppLogger.Write("zoom " + zoomRaw + " is below " + MinZoom + ", clamped");
            }
            if (configuredZoom > MaxZoom)
            {
                configuredZoom = MaxZoom;
                AppLogger.Write("zoom " + zoomRaw + " is above " + MaxZoom + ", clamped");
            }
            AppLogger.Write("ui zoom configured: " + configuredZoom.ToString("0.##"));
        }

        webView.Dock = DockStyle.Fill;
        Controls.Add(webView);

        Load += OnLoaded;
        FormClosing += OnFormClosing;
    }

    private static Icon LoadAppIcon()
    {
        // Reuse an icon shipped next to the executable when present.
        string candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dsh.ico");
        if (File.Exists(candidate)) return new Icon(candidate);
        return SystemIcons.Application;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// The DPI this window is really on, asked of the window itself.
    ///
    /// Control.DeviceDpi cannot be trusted here: without the app.config
    /// DpiAwareness switch, .NET Framework 4.8 keeps answering 96 even after the
    /// process has been made per-monitor aware, so the window would never scale and
    /// the layout would stay 96-DPI sized inside a 144-DPI world.
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
            AppLogger.Write("GetDpiForWindow unavailable: " + ex.GetType().Name);
        }
        return DeviceDpi;
    }

    /// <summary>
    /// Record what the embedded browser is actually rendering at.
    ///
    /// A DPI-unaware host has its window bitmap-scaled by Windows, which no in-page
    /// measurement can see. Logging the window DPI next to the browser device scale
    /// is what turns "it looks blurry" into something checkable.
    /// </summary>
    private void ProbeRenderingScale()
    {
        try
        {
            string script = "window.devicePixelRatio + '|' + window.innerWidth + 'x' + window.innerHeight";
            webView.CoreWebView2.ExecuteScriptAsync(script).ContinueWith(
                delegate(System.Threading.Tasks.Task<string> t)
                {
                    try
                    {
                        if (t.IsFaulted)
                        {
                            AppLogger.Write("render probe failed");
                            return;
                        }
                        AppLogger.Write("browser renders at " + t.Result + "  (devicePixelRatio|css px)");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Write("render probe threw: " + ex.Message);
                    }
                },
                System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex)
        {
            AppLogger.Write("render probe could not start: " + ex.Message);
        }
    }

    /// <summary>
    /// Apply the optional `zoom` setting. Does nothing when config.ini omits it.
    /// </summary>
    private void ApplyConfiguredZoom()
    {
        if (configuredZoom < 0f) return;
        try
        {
            webView.ZoomFactor = configuredZoom;
            AppLogger.Write("ui zoom applied: " + configuredZoom.ToString("0.##")
                + " (browser reports " + webView.ZoomFactor.ToString("0.##") + ")");
        }
        catch (Exception ex)
        {
            AppLogger.Write("ui zoom could not be applied: " + ex.Message);
        }
    }
    // ───────────────────────────── startup ─────────────────────────────

    private void OnLoaded(object sender, EventArgs e)
    {
        AppLogger.Write("window loaded, preparing webview");

        // Version bookkeeping comes first: it decides whether the user is told that
        // the launcher just changed under them, and it is what makes "which version is
        // that machine running?" answerable from the config file.
        NoticeUpgrade();
        if (upgradeNotice.Length > 0)
        {
            ShowBalloon(upgradeNotice, ToolTipIcon.Info);
        }

        // Grow the window with the display scale so the embedded browser gets a
        // canvas at the screen's real resolution instead of a small one that
        // Windows then stretches (which is what makes the UI look low resolution).
        int dpi = RealDpi();
        float scale = dpi > 0 ? dpi / 96f : 1f;
        if (scale > 1.005f)
        {
            MinimumSize = new Size(
                (int)Math.Round(BaseMinWidth * scale), (int)Math.Round(BaseMinHeight * scale));
            Width = (int)Math.Round(BaseWidth * scale);
            Height = (int)Math.Round(BaseHeight * scale);
            AppLogger.Write("dpi " + dpi + " -> window scaled by " + scale.ToString("0.##")
                + " (" + Width + "x" + Height + ")");
        }
        else
        {
            AppLogger.Write("dpi " + dpi + " -> no window scaling");
        }

        try
        {
            // Serve the UI over loopback HTTP. A file:// page is an opaque origin
            // and Chromium treats its requests as cross-site, which strips the
            // SameSite=Strict harness cookie and lands the user on a 401 page.
            if (!uiServer.Start(uiDir))
            {
                FatalDialog("无法启动本地界面服务。\n\n请检查是否有安全软件拦截了本程序的本地端口。");
                return;
            }
            uiUrl = uiServer.BaseUrl + "app.html";

            string userData = Path.Combine(dataDir, "WebView2");
            Directory.CreateDirectory(userData);

            // Development hook: DSH_APP_DEBUG_PORT=<n> exposes the Chromium
            // DevTools endpoint so an automated test can drive the UI. Without
            // that variable the browser cannot be inspected from outside.
            CoreWebView2EnvironmentOptions options = null;
            string debugPort = Environment.GetEnvironmentVariable("DSH_APP_DEBUG_PORT");
            if (!string.IsNullOrEmpty(debugPort))
            {
                options = new CoreWebView2EnvironmentOptions(
                    "--remote-debugging-port=" + debugPort + " --remote-allow-origins=*");
                AppLogger.Write("devtools endpoint enabled on port " + debugPort);
            }

            var env = CoreWebView2Environment.CreateAsync(null, userData, options).GetAwaiter().GetResult();
            webView.EnsureCoreWebView2Async(env);
            webView.CoreWebView2InitializationCompleted += OnCoreInit;
        }
        catch (Exception ex)
        {
            AppLogger.Write("webview init threw: " + ex);
            FatalDialog("内置浏览器内核初始化失败。\n\n" + ex.Message
                + "\n\n请确认已安装 Microsoft Edge WebView2 运行时后重试。");
        }
    }

    private void OnCoreInit(object sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            AppLogger.Write("webview init failed: "
                + (e.InitializationException == null ? "unknown" : e.InitializationException.ToString()));
            FatalDialog("内置浏览器内核初始化失败。\n\n请安装 Microsoft Edge WebView2 运行时后重试。");
            return;
        }

        AppLogger.Write("webview ready, browser " + webView.CoreWebView2.Environment.BrowserVersionString);

        // Chrome-style browser shortcuts, plus F12 for diagnostics.
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
        webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
        webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;

        webView.CoreWebView2.WebMessageReceived += OnWebMessage;
        webView.CoreWebView2.ProcessFailed += OnProcessFailed;
        webView.NavigationStarting += OnNavigationStarting;
        webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        webView.NavigationCompleted += OnHarnessNavigationCompleted;
        AppLogger.Write("loading ui from " + uiUrl);
        webView.CoreWebView2.Navigate(uiUrl);
    }

    private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
    {
        AppLogger.Write("webview process failed: " + e.ProcessFailedKind);
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            AppLogger.Write("browser process exited; restarting the control");
            try { webView.Reload(); } catch { }
        }
    }

    /// <summary>
    /// Drop any cookie this origin left behind, then run a continuation.
    ///
    /// Strictly callback-based: WebView2's async calls resume on the UI thread,
    /// so blocking that thread with .GetAwaiter().GetResult() deadlocks the whole
    /// window -- the continuation can never be posted, and the app freezes while
    /// looking perfectly healthy from outside. Never block on these calls.
    ///
    /// Why clear at all: the WebView2 user-data folder is persistent, but each
    /// harness process signs its session cookie with a fresh secret, so a cookie
    /// from an earlier run is unusable and can leave the app on the
    /// "authentication required" page.
    /// </summary>
    private void ClearHarnessCookiesThen(string url, Action then)
    {
        try
        {
            if (webView.CoreWebView2 == null || string.IsNullOrEmpty(url))
            {
                if (then != null) then();
                return;
            }

            string origin;
            try
            {
                var uri = new Uri(url);
                origin = uri.Scheme + "://" + uri.Authority;
            }
            catch
            {
                if (then != null) then();
                return;
            }

            webView.CoreWebView2.CookieManager.GetCookiesAsync(origin).ContinueWith(
                delegate(System.Threading.Tasks.Task<System.Collections.Generic.List<CoreWebView2Cookie>> t)
                {
                    try
                    {
                        if (t.IsFaulted)
                        {
                            AppLogger.Write("cookie read failed: " + t.Exception.GetBaseException().Message);
                        }
                        else
                        {
                            int n = 0;
                            foreach (var c in t.Result)
                            {
                                webView.CoreWebView2.CookieManager.DeleteCookie(c);
                                n++;
                            }
                            if (n > 0) AppLogger.Write("cleared " + n + " stale cookie(s) for " + origin);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Write("cookie cleanup skipped: " + ex.Message);
                    }

                    if (then != null)
                    {
                        try { then(); }
                        catch (Exception ex) { AppLogger.Write("continuation failed: " + ex); }
                    }
                },
                System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex)
        {
            AppLogger.Write("cookie cleanup could not start: " + ex.Message);
            if (then != null) then();
        }
    }

    private bool recoveryTried;
    private string harnessOrigin;

    /// <summary>
    /// Keep the window on the two origins this program owns, and hand everything else to
    /// the user's own browser.
    ///
    /// Why this is a security control and not just tidiness: the page -> host bridge is
    /// reachable from whatever document the window happens to hold. A chat answer (or the
    /// model output behind it) can contain a link; clicking it used to navigate this window
    /// to an arbitrary site, and that site could then post messages to the host exactly as
    /// the control panel does. Refusing the navigation removes the situation rather than
    /// trying to filter what a hostile page may ask for. Opening it in the real browser is
    /// also the behaviour a user expects from a link in a chat.
    ///
    /// Measured: the harness serves its whole UI from one origin, and the token URL, the
    /// 401 recovery page and the in-app routing are all same-origin, so nothing the product
    /// needs is caught by this.
    /// </summary>
    private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        string target = e.Uri;
        if (IsAllowedNavigation(target)) return;

        e.Cancel = true;
        AppLogger.Write("blocked navigation out of the app window: " + target);
        if (target != null
            && (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            OpenInDefaultBrowser(target);
        }
    }

    /// <summary>
    /// A link with target="_blank" (or window.open) must not spawn a second, unmanaged
    /// browser window either: WebView2 would create one with no host attached, and there is
    /// nothing in it the app can control. Send it to the default browser instead.
    /// </summary>
    private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        try
        {
            e.Handled = true;   // no NewWindow set, so the popup is refused
            AppLogger.Write("refused a popup for " + e.Uri + "; opening it in the browser");
            if (!string.IsNullOrEmpty(e.Uri)) OpenInDefaultBrowser(e.Uri);
        }
        catch (Exception ex)
        {
            AppLogger.Write("popup handling failed: " + ex.Message);
        }
    }

    /// <summary>
    /// The app's own interface (the UI server), the harness it started, and about:blank.
    /// Anything else is somebody else's page.
    /// </summary>
    private bool IsAllowedNavigation(string url)
    {
        if (string.IsNullOrEmpty(url)) return true;          // nothing to judge; let WebView2 decide
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (uiServer != null && !string.IsNullOrEmpty(uiServer.BaseUrl)
            && url.StartsWith(uiServer.BaseUrl, StringComparison.OrdinalIgnoreCase)) return true;

        // The harness's exact origin, taken from the URL the app itself discovered. This is
        // deliberately an origin comparison and not a prefix one: a link to
        // http://127.0.0.1:3099.evil.com/ must not pass.
        string harness = server == null ? null : server.Url;
        return SameOrigin(url, harness);
    }

    private static bool SameOrigin(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            var ua = new Uri(a);
            var ub = new Uri(b);
            return string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(ua.Authority, ub.Authority, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// If the harness answers its 401 page, the token exchange did not take.
    /// Re-enter through the tokenised URL once rather than leaving the user
    /// staring at "authentication required". The probe is asynchronous for the
    /// same reason as the cookie call: blocking the UI thread would deadlock.
    /// </summary>
    private void OnHarnessNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            string current = webView.Source == null ? "" : webView.Source.ToString();
            AppLogger.Write("navigation completed: success=" + e.IsSuccess
                + " status=" + e.WebErrorStatus + " url=" + current);

            if (!e.IsSuccess) return;

            // Applied to whichever page just loaded, so the startup card and the chat
            // window never disagree about scale.
            ApplyConfiguredZoom();

            if (server.Url == null) return;
            if (!current.StartsWith(harnessOrigin ?? "\u0000", StringComparison.OrdinalIgnoreCase)) return;

            string script = "(document.body && document.body.innerText || '').indexOf('authentication required') >= 0";
            webView.CoreWebView2.ExecuteScriptAsync(script).ContinueWith(
                delegate(System.Threading.Tasks.Task<string> t)
                {
                    try
                    {
                        string probe = t.IsFaulted ? "(probe failed)" : t.Result;
                        AppLogger.Write("auth-page probe returned " + probe);

                        if (probe != null && probe.Trim().ToLowerInvariant() == "true")
                        {
                            AppLogger.Write("harness asked for authentication after navigation");
                            if (recoveryTried)
                            {
                                AppLogger.Write("recovery already attempted; leaving the page as-is");
                                return;
                            }
                            recoveryTried = true;
                            string target = server.Url;
                            ClearHarnessCookiesThen(target, delegate
                            {
                                AppLogger.Write("retrying with a fresh token");
                                try { webView.CoreWebView2.Navigate(target); }
                                catch (Exception ex) { AppLogger.Write("retry navigate failed: " + ex.Message); }
                            });
                        }
                        else
                        {
                            AppLogger.Write("harness UI loaded successfully");
                            ProbeRenderingScale();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Write("navigation check failed: " + ex.Message);
                    }
                },
                System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex)
        {
            AppLogger.Write("navigation check skipped: " + ex.Message);
        }
    }

    private void FatalDialog(string message)
    {
        MessageBox.Show(message, "DSH-App", MessageBoxButtons.OK, MessageBoxIcon.Error);
        quitting = true;
        Close();
    }

}
