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
/// MainForm, part: page -> host messages
///
/// Split out of MainForm.cs along its existing section comments to keep each file
/// navigable. Same class, same behaviour -- a partial declaration, nothing moved
/// but the text.
/// </summary>
public sealed partial class MainForm : Form
{

// ---------------------------------------------------------------- page -> host messages
//
    // ───────────────────────── page -> host ─────────────────────────

    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch (Exception ex) { AppLogger.Write("message read failed: " + ex.Message); return; }

        string type = Json.GetString(raw, "type");

        // ── Who sent this? ───────────────────────────────────────────────────────
        // The bridge belongs to the WINDOW, not to a page: every document loaded in this
        // WebView2 can call chrome.webview.postMessage -- a link the user clicked in a chat
        // answer, a page the model's output navigated to, a cross-origin iframe inside one
        // of those. Without this check any of them could post {"type":"saveKey"} and have
        // the user's requests quietly run through somebody else's API key, or quit the app,
        // or move the service port. Only the control panel this program wrote may talk to
        // the host. (The window is also kept on allowed origins by OnNavigationStarting; the
        // two together are what make the bridge trustworthy.)
        string source = null;
        try { source = e.Source; } catch { }
        if (!IsOwnUiMessage(source))
        {
            AppLogger.Write("ignored a page message from " + (source ?? "(unknown source)")
                            + " (type=" + type + "): not this program's own interface");
            return;
        }

        AppLogger.Write("ui -> host: " + type);

        switch (type)
        {
            case "uiReady":
                // Tell the page which port the service will use, so the port row on
                // the startup card shows the real value from the first moment.
                Send("{\"type\":\"portInfo\",\"port\":" + ReadCurrentPort() + "}");
                MaybeStartCheck();
                break;

            case "saveKey":
                string key = Json.GetString(raw, "key");
                if (string.IsNullOrEmpty(key) || key.Trim().Length < 10)
                {
                    Send("{\"type\":\"keyError\",\"text\":\"这个 Key 看起来不完整，请检查后重试\"}");
                    return;
                }
                KeyStore.Save(key.Trim());
                AppConfig.SetBool(AppConfig.KeySetupDone, true);
                AppConfig.Save();
                StartServerAsync();
                break;

            case "recheck":
                MaybeStartCheck();
                break;

            case "openUrl":
                string url = Json.GetString(raw, "url");
                if (!string.IsNullOrEmpty(url)
                    && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    OpenInDefaultBrowser(url);
                }
                break;

            case "repair":
                AppLogger.Write("user asked for the automatic repair");
                RepairHarnessAsync();
                break;

            case "useFreePort":
                AppLogger.Write("user chose: start on a free port");
                StartServerAsync(true);
                break;

            case "retry":
                // A genuine retry: same port, same settings. This used to be wired to
                // useFreePort, so the button labelled 重试 silently changed the port --
                // which is not a retry and does nothing for a download failure.
                AppLogger.Write("user chose: retry on port " + AppConfig.GetInt(AppConfig.KeyPort, 3080));
                serverStarted = false;
                StartServerAsync();
                break;

            case "setPort":
                SetPortFromUi(Json.GetInt(raw, "port"));
                break;

            case "openEmbedded":
                OpenEmbeddedWindow();
                break;

            case "openInBrowser":
                OpenHarnessInBrowser();
                break;

            case "endOrphan":
                EndOrphanHarness(Json.GetInt(raw, "pid"));
                break;

            case "quit":
                QuitApp();
                break;
        }
    }

    /// <summary>
    /// Apply a port chosen on the startup card.
    ///
    /// The port is written to config.ini immediately so it survives a restart. If
    /// the service is already up it is moved to the new port right away; if the new
    /// port turns out to be taken, that is reported before anything is changed --
    /// saving a port the service then cannot use would just move the failure later.
    /// </summary>
    private void SetPortFromUi(int port)
    {
        if (port < 1024 || port > 65535)
        {
            Send("{\"type\":\"portError\",\"text\":" + Json.Quote("端口要填 1024 到 65535 之间的数字。") + "}");
            return;
        }

        if (port != server.Port && EnvProbe.TestPortOpen(port, 250))
        {
            int owner;
            var state = EnvProbe.ClassifyPort(port, out owner);
            if (state == EnvProbe.PortState.Reserved)
            {
                // Windows owns this range. No process to name, and nothing the user could
                // close -- only a different number to type.
                AppLogger.Write("port change refused: " + port + " is reserved by Windows");
                Send("{\"type\":\"portError\",\"text\":" + Json.Quote(
                    "这个端口用不了：Windows 自己把它预留了，所以没有哪个程序占用它，关掉谁也腾不出来。换一个数字再试。") + "}");
            }
            else
            {
                AppLogger.Write("port change refused: " + port + " is already in use"
                    + (owner > 0 ? " (pid " + owner + ")" : ""));
                Send("{\"type\":\"portError\",\"text\":" + Json.Quote(
                    "这个端口已经被占用了，换一个再试。" + (owner > 0 ? "（占用者进程号 " + owner + "）" : "")) + "}");
            }
            return;
        }

        // A start attempt may be in flight right now (it blocks for seconds waiting
        // for the URL). Changing the port in that window used to start a second
        // harness while the first one was still coming up, orphaning it on the old
        // port. Wait for it, then take the service down before moving it.
        WaitForInFlightStart("port change");

        AppConfig.Set(AppConfig.KeyPort, port.ToString());
        AppConfig.Save();
        AppLogger.Write("port set from the interface: " + port);

        bool restart = serverStarted || server.IsRunning || server.Ready;
        if (restart)
        {
            AppLogger.Write("restarting the service on port " + port
                + " (serverStarted=" + serverStarted + " IsRunning=" + server.IsRunning + ")");
            // Stop first. Without this the harness on the old port is simply
            // abandoned and keeps holding that port.
            server.Stop();
            serverStarted = false;
            StartServerAsync();
        }

        Send("{\"type\":\"portChanged\",\"port\":" + port
             + ",\"restarting\":" + (restart ? "true" : "false") + "}");
        UpdateStatusLine();
    }

    /// <summary>
    /// Send "failed" with the port attached, so a panel that is showing the port
    /// line keeps showing the truth in the failure state too.
    /// </summary>
    private void SendFailed(string json)
    {
        string withPort = json;
        int idx = withPort.LastIndexOf('}');
        if (idx > 0)
        {
            withPort = withPort.Substring(0, idx) + ",\"port\":" + ReadCurrentPort() + "}";
        }
        Send(withPort);
    }

    /// <summary>
    /// Is this message from the control panel this program serves itself?
    ///
    /// Compared against the UI server's own base URL, so the answer is "the document we
    /// wrote", not "some page that happens to be on loopback". A page served by the harness
    /// or by anything else on 127.0.0.1 is not our interface and gets no say here.
    /// </summary>
    private bool IsOwnUiMessage(string source)
    {
        if (string.IsNullOrEmpty(source)) return false;
        if (uiServer == null || string.IsNullOrEmpty(uiServer.BaseUrl)) return false;
        return source.StartsWith(uiServer.BaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Post a message to the page. Callable from any thread: the environment
    /// check and the server startup both run off the UI thread, and WebView2
    /// refuses to be touched from anywhere but the UI thread.
    /// </summary>
    private void Send(string json)
    {
        try
        {
            if (IsHandleCreated && InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { SendOnUiThread(json); });
                return;
            }
            SendOnUiThread(json);
        }
        catch (Exception ex)
        {
            AppLogger.Write("send failed: " + ex.Message);
        }
    }

    private void SendOnUiThread(string json)
    {
        if (webView.CoreWebView2 == null) return;
        try { webView.CoreWebView2.PostWebMessageAsString(json); }
        catch (Exception ex) { AppLogger.Write("post to ui failed: " + ex.Message); }
    }

    private void Step(string key, string state, string text)
    {
        Send("{\"type\":\"step\",\"key\":" + Json.Quote(key)
             + ",\"state\":" + Json.Quote(state)
             + ",\"text\":" + Json.Quote(text) + "}");
    }

}
