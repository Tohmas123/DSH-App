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
/// MainForm, part: the environment check
///
/// Split out of MainForm.cs along its existing section comments to keep each file
/// navigable. Same class, same behaviour -- a partial declaration, nothing moved
/// but the text.
/// </summary>
public sealed partial class MainForm : Form
{

// ---------------------------------------------------------------- the environment check
//
    // ───────────────────────── environment check ─────────────────────────

    private void MaybeStartCheck()
    {
        var t = new Thread(RunCheck);
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>
    /// Debug switch: DSH_APP_FORCE_WIZARD=1 shows the key step even when a key is
    /// already resolvable. Needed to exercise the first-run path on a machine
    /// that already has credentials, and it is the hook a future "change my key"
    /// action will reuse.
    /// </summary>
    private static bool ForceKeyWizard()
    {
        string v = Environment.GetEnvironmentVariable("DSH_APP_FORCE_WIZARD");
        bool on = !string.IsNullOrEmpty(v) && v != "0";
        if (on) AppLogger.Write("key wizard forced on by DSH_APP_FORCE_WIZARD");
        return on;
    }

    private void RunCheck()
    {
        AppLogger.Write("=== environment check start");

        try
        {
            // 1. WebView2 runtime (reported, not fatal: the UI is already up)
            Step("wv", "busy", "检测中");
            var wv = EnvProbe.ProbeWebView();
            if (wv.Found) { Step("wv", "ok", "v" + wv.Version); Send("{\"type\":\"guide\",\"key\":\"wv\",\"on\":false}"); }
            else { Step("wv", "bad", "缺失"); Send("{\"type\":\"guide\",\"key\":\"wv\",\"on\":true}"); }

            // 2. Node.js -- presence AND version. Running an unsupported Node gets
            // as far as "the harness exited immediately", which tells a
            // non-technical user nothing. Checking here turns that into a plain
            // "your Node is too old, install the new one".
            Step("node", "busy", "检测中");
            var node = EnvProbe.ProbeNode();
            if (node.Found && node.TooOld)
            {
                AppLogger.Write("check stopped: node " + node.Version + " is older than the required major "
                    + EnvProbe.NodeInfo.MinimumMajor);
                Step("node", "bad", node.Version + "（过旧）");
                Send("{\"type\":\"oldNode\",\"found\":" + Json.Quote(node.Version)
                     + ",\"required\":" + EnvProbe.NodeInfo.MinimumMajor + "}");
                Send("{\"type\":\"bar\",\"on\":false}");
                Step("dsh", "warn", "需要新版 Node.js");
                Step("key", "warn", "未检测");
                return;
            }
            if (node.Found)
            {
                Step("node", "ok", node.Version);
                Send("{\"type\":\"guide\",\"key\":\"node\",\"on\":false}");
            }
            else
            {
                Step("node", "bad", "未安装");
                Send("{\"type\":\"guide\",\"key\":\"node\",\"on\":true}");
                Send("{\"type\":\"bar\",\"on\":false}");
                Step("dsh", "warn", "需要 Node.js");
                Step("key", "warn", "未检测");
                AppLogger.Write("check stopped: node missing");
                return;
            }

            // 3. Harness availability
            Step("dsh", "busy", "检测中");
            var dsh = EnvProbe.ProbeDsh(node);
            if (dsh.Ready)
            {
                if (dsh.How == "npx-download")
                {
                    Step("dsh", "warn", "首次需下载");
                    Send("{\"type\":\"guide\",\"key\":\"dsh\",\"on\":true}");
                }
                else
                {
                    Step("dsh", "ok", dsh.Version);
                    Send("{\"type\":\"guide\",\"key\":\"dsh\",\"on\":false}");
                }
            }
            else
            {
                Step("dsh", "bad", "无法启动");
                Send("{\"type\":\"failed\",\"text\":\"无法找到 DeepSeek Harness，且系统中没有 npx。请重新安装 Node.js 后重试。\"}");
                return;
            }

            // 4. API key
            Step("key", "busy", "检测中");
            string stored = KeyStore.Load();
            if (!string.IsNullOrEmpty(stored) && !ForceKeyWizard())
            {
                Step("key", "ok", "已保存");
                Send("{\"type\":\"bar\",\"on\":false}");
                StartServerAsync();
                return;
            }
            if (KeyStore.HarnessHasKey() && !ForceKeyWizard())
            {
                Step("key", "ok", "已由 Harness 提供");
                Send("{\"type\":\"bar\",\"on\":false}");
                StartServerAsync();
                return;
            }

            Step("key", "warn", "待填写");
            Send("{\"type\":\"bar\",\"on\":false}");
            Send("{\"type\":\"needKey\"}");
            AppLogger.Write("check complete: waiting for the user to enter a key");
        }
        catch (Exception ex)
        {
            AppLogger.Write("check threw: " + ex);
            Send("{\"type\":\"failed\",\"text\":" + Json.Quote("检测过程出错：" + ex.Message) + "}");
        }
    }

}
