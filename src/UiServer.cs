using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

/// <summary>
/// Serves the app's own UI over HTTP on the loopback interface.
///
/// Why not load it from disk with a file:// URL? A file:// page is an opaque
/// origin, and Chromium treats its requests as cross-site. The harness session
/// cookie is SameSite=Strict, so any renderer-initiated navigation from a
/// file:// page to the harness would arrive without that cookie and be answered
/// with 401 -- the app would open onto an "authentication required" page.
/// Serving the UI from http://127.0.0.1 keeps everything same-site and removes
/// the whole class of problem.
///
/// The listener binds the loopback address only: nothing on the network can
/// reach this server.
/// </summary>
public sealed class UiServer
{
    private HttpListener listener;
    private Thread worker;
    private volatile bool running;
    private string rootDir;

    public int Port { get; private set; }
    public string BaseUrl { get { return "http://127.0.0.1:" + Port + "/"; } }

    /// <summary>
    /// The port preferred on this start, kept only to tell "chose the usual port"
    /// apart from "the usual port was unusable and we drifted" in the log. Drift
    /// matters: the page origin changes with it, so browser state keyed to the old
    /// origin (a zoom the user set with Ctrl +/- is stored per origin) is left
    /// behind. It is also the first sign that Windows has started excluding ports
    /// inside the 31700-31719 range.
    /// </summary>
    private int preferredPort;

    public bool Start(string directory)
    {
        rootDir = directory;
        preferredPort = PickInitialPort();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int port = attempt == 0 ? preferredPort : EnvProbe.FindFreePort();
            var l = new HttpListener();
            l.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try
            {
                l.Start();
                listener = l;
                Port = port;
                running = true;
                worker = new Thread(Loop);
                worker.IsBackground = true;
                worker.Name = "ui-server";
                worker.Start();
                AppLogger.Write("ui server listening on " + BaseUrl + " (root " + rootDir + ")");
                if (port != preferredPort)
                {
                    // Worth one line: the page origin moved, so anything the browser
                    // stored per origin no longer applies.
                    AppLogger.Write("ui port " + preferredPort + " was unusable, using " + port
                        + " instead (the interface origin changed)");
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write("ui server bind failed on port " + port + ": " + ex.Message);
                try { l.Close(); } catch { }
            }
        }
        AppLogger.Write("ui server could not bind any port");
        return false;
    }

    /// <summary>
    /// Prefer a stable private port in 31700-31719, so the origin (and anything
    /// keyed to it) stays recognisable across runs; fall back to an ephemeral one.
    ///
    /// The probe is deliberately a TCP connect rather than an HttpListener bind.
    /// Measured on this machine (Windows excluded port range 31700-31700): the
    /// kernel answers the SYN of an excluded port even with nothing listening, so
    /// TestPortOpen reports it as busy and it gets skipped -- which is the correct
    /// outcome, because starting a listener there fails with "conflicts with an
    /// existing registration" (the bind really is impossible; only the reason
    /// differs from "someone is listening"). Do not "simplify" this into a bind
    /// probe: TestPortOpen answers True both for a live listener and for an
    /// excluded port, and the port must be treated as unusable in both cases.
    /// </summary>
    private static int PickInitialPort()
    {
        for (int p = 31700; p < 31720; p++)
        {
            if (!EnvProbe.TestPortOpen(p, 60)) return p;
        }
        return EnvProbe.FindFreePort();
    }

    private void Loop()
    {
        while (running)
        {
            HttpListenerContext ctx = null;
            try { ctx = listener.GetContext(); }
            catch { if (!running) return; continue; }

            try { Handle(ctx); }
            catch (Exception ex) { AppLogger.Write("ui request failed: " + ex.Message); }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url.AbsolutePath;
        if (path == "/" || path.Length == 0) path = "/app.html";

        // Only ever serve files from the UI directory: reject traversal outright.
        string name = Path.GetFileName(path);
        string full = Path.Combine(rootDir, name);

        if (!File.Exists(full))
        {
            AppLogger.Write("ui 404: " + path);
            ctx.Response.StatusCode = 404;
            Write(ctx, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"), false);
            return;
        }

        byte[] bytes = File.ReadAllBytes(full);
        string type = MimeFor(name);
        Write(ctx, type, bytes, true);
    }

    private static void Write(HttpListenerContext ctx, string contentType, byte[] body, bool cache)
    {
        try
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = contentType;
            ctx.Response.Headers["Cache-Control"] = cache ? "no-store" : "no-cache";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
        }
        catch (Exception ex)
        {
            AppLogger.Write("ui write failed: " + ex.Message);
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { }
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static string MimeFor(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        switch (ext)
        {
            case ".html": return "text/html; charset=utf-8";
            case ".css": return "text/css; charset=utf-8";
            case ".js": return "application/javascript; charset=utf-8";
            case ".json": return "application/json; charset=utf-8";
            case ".svg": return "image/svg+xml";
            case ".png": return "image/png";
            case ".ico": return "image/x-icon";
            case ".woff2": return "font/woff2";
            default: return "application/octet-stream";
        }
    }

    public void Stop()
    {
        running = false;
        try { if (listener != null) listener.Stop(); } catch { }
        try { if (listener != null) listener.Close(); } catch { }
        listener = null;
        AppLogger.Write("ui server stopped");
    }
}
