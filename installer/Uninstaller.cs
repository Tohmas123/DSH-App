using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

/// <summary>
/// DSH-App uninstaller.
///
/// Replaces the old uninstall.cmd. Why it is a real executable now:
///   * it can show WHAT will be deleted and how big each item is, instead of
///     printing a wall of ASCII into a console window;
///   * it can ask before touching user data;
///   * it can be listed in Windows "Apps &amp; features" (the installer registers it).
///
/// The one rule that shapes the whole design: the harness itself is NOT part of this
/// product. Removing the launcher must not remove the DeepSeek Harness code
/// (%LOCALAPPDATA%\npm-cache) or the user's data, so:
///   * program files and shortcuts are always removed;
///   * %LOCALAPPDATA%\DSH-App, ~\.dsh and the workspace are ONLY removed when the
///     user ticks them, and ~\.dsh asks a second time because it also drives the
///     harness when run from a terminal.
/// </summary>
internal static class Uninstaller
{
    private const string AppName = "DSH-App";
    private const string ExeName = "DSH-App.exe";
    private const string ShortcutName = "DSH-App.lnk";
    /// <summary>
    /// HKCU subkey the installer registers the product under, and the uninstaller
    /// removes. Must match InstallSteps.WriteUninstallRegistryEntry.
    /// </summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DSH-App";

    /// <summary>Exposed so UninstallLogic can use the same value.</summary>
    internal const string UninstallKeyForLogic = UninstallKey;

    /// <summary>
    /// Must match InstallSteps.MarkerName. The two are separate executables and
    /// cannot share a constant, so if one changes the other has to change too.
    /// </summary>
    private const string MarkerName = "install.marker";

    /// <summary>Exposed so UninstallLogic can use the same value.</summary>
    internal const string MarkerNameForLogic = MarkerName;

    /// <summary>Must match InstallSteps.ManifestName, for the same reason as the marker.</summary>
    private const string ManifestName = "install.files";

    /// <summary>Exposed so UninstallLogic can use the same value.</summary>
    internal const string ManifestNameForLogic = ManifestName;

    [STAThread]
    private static void Main(string[] rawArgs)
    {
        bool silent = false, removeData = false, removeDsh = false, removeWorkspace = false;
        string dataDirOverride = null, dshOverride = null;
        string desktopOverride = null, startMenuOverride = null, registryOverride = null;
        bool hookGiven = false;
        foreach (string a in rawArgs)
        {
            if (a.Equals("/S", StringComparison.OrdinalIgnoreCase)) silent = true;
            else if (a.Equals("/removedata", StringComparison.OrdinalIgnoreCase)) removeData = true;
            else if (a.Equals("/removedsh", StringComparison.OrdinalIgnoreCase)) removeDsh = true;
            else if (a.Equals("/removeworkspace", StringComparison.OrdinalIgnoreCase)) removeWorkspace = true;
            // Test hooks. Without these there is no way to exercise the data-removal
            // path without aiming it at the real profile directories -- which is
            // exactly how a test once deleted the live %LOCALAPPDATA%\DSH-App.
            //
            // They are gated on DSH_APP_TEST_HOOKS=1 (see the check right below the loop):
            // "/S /removedata /data=<any path>" would otherwise turn this shipped
            // executable into a general-purpose "recursively delete this directory" tool
            // for anything that can pass a command line to it.
            else if (a.StartsWith("/data=", StringComparison.OrdinalIgnoreCase)) { dataDirOverride = a.Substring(6).Trim('"'); hookGiven = true; }
            else if (a.StartsWith("/dshhome=", StringComparison.OrdinalIgnoreCase)) { dshOverride = a.Substring(9).Trim('"'); hookGiven = true; }
            // The same reasoning for the three locations that live OUTSIDE the install
            // folder. A test that redeclares the uninstaller's own shortcuts and
            // registry entry can run on a real desktop without deleting the user's
            // desktop icon or their "Apps & features" entry.
            else if (a.StartsWith("/desktop=", StringComparison.OrdinalIgnoreCase)) { desktopOverride = a.Substring(9).Trim('"'); hookGiven = true; }
            else if (a.StartsWith("/startmenu=", StringComparison.OrdinalIgnoreCase)) { startMenuOverride = a.Substring(11).Trim('"'); hookGiven = true; }
            else if (a.StartsWith("/regkey=", StringComparison.OrdinalIgnoreCase)) { registryOverride = a.Substring(8).Trim('"'); hookGiven = true; }
        }

        // A hook without the environment flag is REFUSED, not ignored. Ignoring it would be
        // the more dangerous of the two: a test that forgot to set the variable would then be
        // pointed at the real profile directories, which is precisely the accident the hooks
        // exist to prevent.
        if (hookGiven && !TestHooksEnabled())
        {
            Refuse(silent, "refusing to run: a test-only location override (/data=, /dshhome=, "
                           + "/desktop=, /startmenu=, /regkey=) was given without "
                           + TestHookVariable + "=1 in the environment. Nothing was deleted.");
            return;
        }

        if (!string.IsNullOrEmpty(dataDirOverride)) DataOverride = dataDirOverride;
        if (!string.IsNullOrEmpty(dshOverride)) DshOverride = dshOverride;
        if (!string.IsNullOrEmpty(desktopOverride)) DesktopOverride = desktopOverride;
        if (!string.IsNullOrEmpty(startMenuOverride)) StartMenuOverride = startMenuOverride;
        if (!string.IsNullOrEmpty(registryOverride)) UninstallLogic.RegistryKeyPath = registryOverride;

        // Second gate, independent of the environment flag: even a test may not aim these at a
        // drive root or walk upwards out of where it was told to look. The flag says "this is
        // a test", not "delete whatever the argument happens to resolve to" -- and a typo like
        // /data=C:\ would not be recoverable. A RELATIVE value is refused too: it would be
        // resolved against the current directory, which is not something the caller controls
        // as tightly as it looks (/data=.. is the whole parent folder).
        string implausible = FirstImplausibleOverride(dataDirOverride, dshOverride,
                                                      desktopOverride, startMenuOverride);
        if (implausible != null)
        {
            Refuse(silent, "refusing to run: " + implausible + " is not a plausible test location "
                           + "(absolute, not a drive root, no \"..\"). Nothing was deleted.");
            return;
        }
        if (!string.IsNullOrEmpty(registryOverride) && !IsPlausibleRegistryOverride(registryOverride))
        {
            Refuse(silent, "refusing to run: /regkey= must name a subkey of "
                           + @"Software\Microsoft\Windows\CurrentVersion\Uninstall"
                           + ", and must not contain \"..\". Nothing was deleted.");
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (silent)
        {
            // Scriptable path. The directory removal is still handed to the detached
            // helper, because this process is running from the directory in question.
            int code = UninstallLogic.Run(false, removeData, removeDsh, removeWorkspace, null);
            Environment.Exit(code);
        }

        Application.Run(new UninstallForm());
    }

    // ------------------------------------------------------------------ paths

    /// <summary>
    /// Name of the environment variable that unlocks the /data= style location overrides.
    /// Only the automated tests set it; without it the overrides are refused, so the
    /// uninstaller that ships cannot be told to delete an arbitrary directory or registry
    /// key by a stray command line.
    /// </summary>
    internal const string TestHookVariable = "DSH_APP_TEST_HOOKS";

    private static bool TestHooksEnabled()
    {
        try { return Environment.GetEnvironmentVariable(TestHookVariable) == "1"; }
        catch { return false; }
    }

    /// <summary>
    /// Log a refusal and stop without deleting anything. The window is used only when there
    /// is one: in silent mode there is nobody to show a dialog to.
    /// </summary>
    private static void Refuse(bool silent, string message)
    {
        Uninstaller.Log(message);
        if (!silent)
        {
            System.Windows.Forms.MessageBox.Show(message, "DSH-App 卸载程序",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
        Environment.Exit(4);
    }

    /// <summary>
    /// The first override value that must not be used for a recursive delete, or null when
    /// all of them are acceptable. See the call site for why this exists on top of the
    /// environment gate.
    /// </summary>
    private static string FirstImplausibleOverride(params string[] values)
    {
        foreach (string v in values)
        {
            if (string.IsNullOrEmpty(v)) continue;
            if (v.IndexOf("..", StringComparison.Ordinal) >= 0) return v;
            try
            {
                if (!Path.IsPathRooted(v)) return v;
                string full = Path.GetFullPath(v);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return v;
                // "C:\" or "\\server\share" on their own: a whole drive is never a test folder.
                if (string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return v;
                }
                // Two characters ("C:") means "the current directory of drive C:", which is
                // wherever the process happens to be -- not something a caller can reason about.
                if (full.Length < 4) return v;
            }
            catch { return v; }
        }
        return null;
    }

    /// <summary>
    /// /regkey= may only name a key under the Uninstall branch. Without this the option is a
    /// "delete any HKCU subtree" primitive (DeleteSubKeyTree("Software") removes everything
    /// the user has configured for every application).
    /// </summary>
    private static bool IsPlausibleRegistryOverride(string key)
    {
        const string prefix = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
        if (key.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
        return key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && key.Length > prefix.Length;
    }

    /// <summary>Test override for the app data directory; null in normal use.</summary>
    internal static string DataOverride;
    /// <summary>Test override for the harness home; null in normal use.</summary>
    internal static string DshOverride;
    /// <summary>Test override for the desktop shortcut; null in normal use.</summary>
    internal static string DesktopOverride;
    /// <summary>Test override for the Start menu shortcut; null in normal use.</summary>
    internal static string StartMenuOverride;

    internal static string DataDir()
    {
        if (!string.IsNullOrEmpty(DataOverride)) return DataOverride;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH-App");
    }

    internal static string DshHome()
    {
        if (!string.IsNullOrEmpty(DshOverride)) return DshOverride;
        string home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrEmpty(home))
        {
            home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }
        return home;
    }

    internal static string WorkspaceDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSH-App-Workspace");
    }

    internal static string DesktopShortcut()
    {
        if (!string.IsNullOrEmpty(DesktopOverride)) return DesktopOverride;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);
    }

    internal static string StartMenuShortcut()
    {
        if (!string.IsNullOrEmpty(StartMenuOverride)) return StartMenuOverride;
        return Path.Combine(Path.Combine(Path.Combine(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft"), "Windows"), "Start Menu"), Path.Combine("Programs", ShortcutName));
    }

    /// <summary>Bytes under a directory, or -1 when it does not exist.</summary>
    internal static long DirSize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return -1;
            long total = 0;
            foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }
        catch { return -1; }
    }

    internal static string Human(long bytes)
    {
        if (bytes < 0) return "不存在";
        if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.0") + " GB";
        if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return bytes + " 字节";
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDir(), "uninstall.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }
    }
}

/// <summary>The destructive part, kept away from the window so silent mode can share it.</summary>
internal static class UninstallLogic
{
    /// <summary>See Uninstaller.MarkerName -- one value, referenced from two classes.</summary>
    private const string MarkerName = Uninstaller.MarkerNameForLogic;

    /// <summary>
    /// HKCU subkey this uninstaller removes. Overridable so a test can point it at a
    /// throwaway key instead of the machine's real "Apps &amp; features" entry.
    /// </summary>
    internal static string RegistryKeyPath = Uninstaller.UninstallKeyForLogic;
    /// <summary>
    /// Do the removal. <paramref name="fromTempCopy"/> is true when this process is the
    /// copy that was moved to %TEMP%, which is the only one allowed to delete the
    /// install directory (a running image cannot delete its own folder).
    /// </summary>
    internal static int Run(bool fromTempCopy, bool removeData, bool removeDsh,
                            bool removeWorkspace, Action<string> progress)
    {
        string installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

        // A drive root cannot be expressed with the trailing separator trimmed ("D:\" becomes
        // "D:", which means "the current directory of drive D:"). The helper would then remove
        // a directory of that drive it was never pointed at. Refusing is the only safe answer:
        // nobody installs this into a whole drive on purpose, and the files can be deleted by
        // hand.
        if (installDir.EndsWith(":", StringComparison.Ordinal))
        {
            Uninstaller.Log("REFUSING: the install directory is a drive root: " + installDir);
            Step(progress, "已停止：程序目录是整个磁盘根目录，请手动删除。");
            if (progress == null) return 3;
            System.Windows.Forms.MessageBox.Show(
                "程序被直接放到了磁盘根目录，这种情况下自动清理不安全，已停止。\n\n"
                + "可以手动删除这个目录里的 DSH-App 相关文件。",
                "DSH-App 卸载程序", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            return 3;
        }

        // ------------------------------------------------------------------
        // SAFETY: never delete anything that is not a real DSH-App install.
        //
        // Why this exists: when the installer is a single self-extracting file it
        // unpacks itself into a temporary folder and runs from there, so
        // BaseDirectory is that temporary folder rather than the directory the user
        // chose. Deleting it is harmless in itself -- but it proves the uninstaller
        // cannot tell "my install folder" from "wherever I happen to be running".
        // The marker file written by the installer is the only trustworthy answer,
        // and without it nothing destructive happens at all.
        string marker = Path.Combine(installDir, MarkerName);
        bool trusted = File.Exists(marker);

        // Second gate: a real install never lives inside %TEMP%. Refusing there costs
        // nothing and removes any chance of a self-extracting copy deleting a scratch
        // folder it happens to be running from.
        bool insideTempDir = IsInside(installDir, Path.GetTempPath())
                             || IsInside(installDir, Environment.GetEnvironmentVariable("TEMP"))
                             || IsInside(installDir, Environment.GetEnvironmentVariable("TMP"));
        if (insideTempDir)
        {
            Uninstaller.Log("REFUSING: install dir resolves inside a temp directory: " + installDir);
            Step(progress, "已停止：这个目录在临时目录里，不是真正的安装位置。");
            if (progress == null) return 3;
            System.Windows.Forms.MessageBox.Show(
                "这个程序正从临时目录运行，不是真正的安装位置，已停止卸载。\n\n目录：" + installDir,
                "DSH-App 卸载程序", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            return 3;
        }
        // ------------------------------------------------------------------

        Uninstaller.Log("=== DSH-App uninstaller ===");
        Uninstaller.Log("install dir : " + installDir);
        Uninstaller.Log("marker      : " + (trusted ? "found" : "MISSING"));
        Uninstaller.Log("remove data : " + removeData + ", .dsh: " + removeDsh
                        + ", workspace: " + removeWorkspace + ", temp copy: " + fromTempCopy);

        Step(progress, "正在检查安装目录…");
        if (!trusted && !fromTempCopy)
        {
            string message = "这个目录不是 DSH-App 的安装目录（缺少 " + MarkerName
                             + "），已停止卸载，没有删除任何东西。\n\n目录：" + installDir;
            Uninstaller.Log("REFUSING to uninstall: " + message);
            Step(progress, "已停止：目录校验失败。");
            if (progress == null)
            {
                // Silent mode: there is nobody to show a dialog to.
                return 2;
            }
            System.Windows.Forms.MessageBox.Show(message, "DSH-App 卸载程序",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return 2;
        }

        Step(progress, "正在结束正在运行的 DSH-App…");
        try
        {
            foreach (Process p in Process.GetProcessesByName("DSH-App"))
            {
                try { p.Kill(); p.WaitForExit(8000); } catch { }
            }
            // The launcher's harness child would otherwise keep the port and keep
            // running after its parent is gone.
            Thread.Sleep(800);
        }
        catch (Exception ex) { Uninstaller.Log("stop instances failed: " + ex.Message); }

        Step(progress, "正在删除快捷方式…");
        TryDeleteFile(Uninstaller.DesktopShortcut());
        TryDeleteFile(Uninstaller.StartMenuShortcut());

        if (removeData)
        {
            Step(progress, "正在删除 DSH-App 的设置与密钥…");
            TryDeleteDir(Uninstaller.DataDir());
        }
        if (removeDsh)
        {
            Step(progress, "正在删除 ~\\.dsh（对话、附件、插件）…");
            TryDeleteDir(Uninstaller.DshHome());
        }
        if (removeWorkspace)
        {
            Step(progress, "正在删除工作区…");
            TryDeleteDir(Uninstaller.WorkspaceDir());
        }

        Step(progress, "正在移除系统里的卸载项…");
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(RegistryKeyPath, false);
        }
        catch (Exception ex) { Uninstaller.Log("registry cleanup failed: " + ex.Message); }

        if (!fromTempCopy)
        {
            Step(progress, "正在清理程序文件…");
            // Hand the deletion to a detached helper: this process is an image inside the
            // install folder, so it cannot remove its own folder while it is running.
            //
            // The helper now removes only the files the manifest lists, and then the folder
            // only if it is empty. It no longer runs "rmdir /s /q" over the whole folder --
            // that deleted whatever else the user happened to keep there.
            try
            {
                // Unique names, not "dsh-app-cleanup.cmd". A fixed name in %TEMP% is a file
                // any other process on this machine can create first, or replace between the
                // write and the run -- and this file is handed to cmd.exe to execute. The
                // guid costs nothing and removes the whole question.
                string tag = Guid.NewGuid().ToString("N");
                string helper = Path.Combine(Path.GetTempPath(), "dsh-app-cleanup-" + tag + ".cmd");
                string listPath = Path.Combine(Path.GetTempPath(), "dsh-app-cleanup-" + tag + ".txt");

                var files = new List<string>();
                try
                {
                    foreach (string line in File.ReadAllLines(
                                 Path.Combine(installDir, Uninstaller.ManifestNameForLogic), Encoding.UTF8))
                    {
                        string rel = line.Trim();
                        if (rel.Length == 0 || rel.StartsWith("#", StringComparison.Ordinal)) continue;
                        // The manifest is a plain file in a folder the user can write to, and
                        // every name in it ends up inside a command line the helper executes.
                        // Only names that are provably simple relative paths get through.
                        if (!IsSafeRelativeName(rel))
                        {
                            Uninstaller.Log("ignoring an unsafe entry in the install manifest: " + rel);
                            continue;
                        }
                        files.Add(rel);
                    }
                }
                catch (Exception ex)
                {
                    Uninstaller.Log("could not read the install manifest: " + ex.Message);
                }

                bool haveManifest = files.Count > 0;
                if (!haveManifest)
                {
                    Uninstaller.Log("no manifest; the helper will remove the known program files by name");
                    files.AddRange(new string[]
                    {
                        "DSH-App.exe", "Uninstaller.exe", "dsh.ico",
                        "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll",
                        "WebView2Loader.dll", "uninstall.cmd", "cleanup.cmd",
                        "uninstall-notes.txt", "install.marker", "install.files",
                        "ui\\app.html", "ui\\dsh.ico"
                    });
                }

                // One path per line, read by the helper with "for /f". A newline inside a
                // path cannot be represented, and would be unreachable on Windows anyway.
                //
                // The manifest names itself last. It has to be deleted and it cannot be in
                // its own list at write time, so the helper is told about it explicitly --
                // without this the manifest survived every uninstall and kept the folder
                // alive.
                files.Add(Uninstaller.ManifestNameForLogic);
                // Written in the code page cmd.exe will read it with, not UTF-8. Every name
                // this product records is ASCII (makecab cannot even carry a non-ASCII name),
                // so the two agree today -- writing it the way the reader expects removes the
                // question for good. A list cmd mis-decodes matches nothing and the folder
                // silently survives.
                File.WriteAllLines(listPath, files, ListEncoding());

                // The two paths are passed to the helper as ARGUMENTS and used as "%~1"/"%~2"
                // rather than being pasted into the script text. Pasting them was a quietly
                // wrong-question bug: cmd.exe expands %NAME% inside the script, so an install
                // folder like "C:\100% done\DSH-App" would have had "% done%" substituted away
                // and the helper would have deleted the wrong paths. A parameter's value is
                // not re-scanned for % expansion, so this is immune to the whole class.
                //
                // setlocal DisableDelayedExpansion is belt and braces for the same reason: a
                // machine with HKCU\...\Command Processor\DelayedExpansion=1 would otherwise
                // let a "!" inside a file name start a variable substitution.
                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("setlocal DisableDelayedExpansion");
                sb.AppendLine("ping -n 3 127.0.0.1 >nul 2>nul");
                sb.AppendLine("cd /d \"%SystemRoot%\"");
                // Last line of defence, and the only check the helper can make on its own: the
                // marker has to still be there when this runs, two seconds after it was
                // scheduled. If the folder was swapped for something else in the meantime,
                // nothing is deleted.
                sb.AppendLine("if not exist \"%~1\\" + Uninstaller.MarkerNameForLogic + "\" goto :dsh_no_marker");
                sb.AppendLine("for /f \"usebackq delims=\" %%F in (\"%~2\") do (");
                sb.AppendLine("  del /f /q \"%~1\\%%F\" >nul 2>nul");
                sb.AppendLine(")");
                // Remove directories only when they are empty: "rmdir" without /s refuses
                // to touch a folder that still holds something.
                sb.AppendLine("rmdir \"%~1\\ui\" >nul 2>nul");
                sb.AppendLine("rmdir \"%~1\" >nul 2>nul");
                sb.AppendLine(":dsh_no_marker");
                sb.AppendLine("del /f /q \"%~2\" >nul 2>nul");
                sb.AppendLine("del /f /q \"%~f0\" >nul 2>nul");
                File.WriteAllText(helper, sb.ToString(), new UTF8Encoding(false));

                // Quoted because the install folder may contain spaces. A quote inside the
                // path is impossible on Windows, so it cannot escape these.
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c \"\"" + helper + "\" \"" + installDir + "\" \"" + listPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
                Uninstaller.Log("cleanup helper started for " + installDir
                                + " (" + files.Count + " listed file(s), manifest=" + haveManifest + ")");
                if (!haveManifest)
                {
                    // Worth saying out loud: without a manifest the folder is only emptied by
                    // name, and a leftover file would keep the folder alive.
                    Step(progress, "（本次按已知文件名清理；安装目录若仍有其他文件会自动保留）");
                }
            }
            catch (Exception ex)
            {
                Uninstaller.Log("could not start the cleanup helper: " + ex.Message);
                Step(progress, "程序目录需要手动删除：" + installDir);
            }
        }
        else
        {
            // Running from %TEMP%: nothing of ours is left where we are.
            Uninstaller.Log("running as the temp copy; nothing to schedule here");
        }

        Uninstaller.Log("uninstall finished");
        return 0;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the same as, or below, one of the
    /// given directories. Used to refuse operating inside a temp folder.
    /// </summary>
    private static bool IsInside(string candidate, string parent)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(parent)) return false;
        try
        {
            string c = Path.GetFullPath(candidate).TrimEnd('\\') + "\\";
            string p = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void Step(Action<string> progress, string text)
    {
        Uninstaller.Log(text);
        if (progress != null) progress(text);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); Uninstaller.Log("deleted file: " + path); }
        }
        catch (Exception ex) { Uninstaller.Log("could not delete " + path + ": " + ex.Message); }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                Uninstaller.Log("deleted dir: " + path);
            }
        }
        catch (Exception ex) { Uninstaller.Log("could not delete " + path + ": " + ex.Message); }
    }

    // The file removal itself lives in the detached helper script built above. It cannot
    // live here: this process is an image inside the install folder, and Windows will not
    // let a running image delete the file it was loaded from. An earlier revision carried a
    // second, managed implementation of the same logic for the %TEMP%-copy case; it was
    // never reachable and has been removed, so there is exactly ONE place that decides what
    // gets deleted.

    /// <summary>
    /// True when a name from the install manifest may be handed to the cleanup helper: a
    /// plain relative path with no way out of the install folder and nothing in it that the
    /// command line would act on.
    ///
    /// Why this is not paranoia: the manifest is a text file in a folder the user can write
    /// to, and each of its lines becomes one <c>del /f /q "&lt;install&gt;\&lt;name&gt;"</c> in
    /// a batch file. A name containing a quote ends the argument and the rest of the line
    /// runs as a command; "..\" walks out of the folder this program is allowed to touch;
    /// "*" empties whatever the folder happens to hold. The same names arrive from the
    /// cabinet (CabReader) and from the installer, and the equivalent check lives there too
    /// -- two executables, so two copies of the rule. Keep them in step.
    /// </summary>
    internal static bool IsSafeRelativeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 200) return false;
        try { if (Path.IsPathRooted(name)) return false; }
        catch { return false; }

        // A drive letter or an NTFS alternate data stream ("file.txt:stream").
        if (name.IndexOf(':') >= 0) return false;

        foreach (char c in name)
        {
            if (c < 0x20 || c == 0x7f) return false;
            // cmd.exe metacharacters that survive the surrounding quotes, wildcards that
            // would widen the delete, and the quote itself.
            if (c == '"' || c == '%' || c == '!' || c == '&' || c == '|'
                || c == '<' || c == '>' || c == '^' || c == '*' || c == '?') return false;
        }

        // ".." as a whole segment only, so a name like "a..b.dll" stays legal.
        foreach (string segment in name.Split(new char[] { '\\', '/' }))
        {
            if (segment == "..") return false;
        }
        return true;
    }

    /// <summary>
    /// The encoding cmd.exe will read a plain text file in: the console's OEM code page.
    /// Falls back to UTF-8 without a BOM if the code page cannot be resolved.
    /// </summary>
    internal static Encoding ListEncoding()
    {
        try
        {
            return Encoding.GetEncoding(
                System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch { return new UTF8Encoding(false); }
    }
}

internal sealed class UninstallForm : Form
{
    private readonly CheckBox dataBox = new CheckBox();
    private readonly CheckBox dshBox = new CheckBox();
    private readonly CheckBox wsBox = new CheckBox();
    private readonly Label status = new Label();
    private readonly Button goButton = new Button();
    private readonly Button cancelButton = new Button();
    private bool finished;

    internal UninstallForm()
    {
        Text = "卸载 DSH-App";
        ClientSize = new Size(580, 452);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9f);
        AutoScaleMode = AutoScaleMode.None;
        BuildUi();
    }

    private void BuildUi()
    {
        var title = new Label();
        title.Text = "卸载 DSH-App";
        title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(20, 24, 33);
        title.Location = new Point(24, 18);
        title.AutoSize = true;
        Controls.Add(title);

        var sub = new Label();
        sub.Text = "将删除 DSH-App 这个启动器。DeepSeek Harness 本体不受影响。";
        sub.ForeColor = Color.FromArgb(100, 108, 122);
        sub.Location = new Point(26, 52);
        sub.AutoSize = true;
        Controls.Add(sub);

        var line = new Label();
        line.BorderStyle = BorderStyle.Fixed3D;
        line.Location = new Point(24, 80);
        line.Size = new Size(532, 2);
        Controls.Add(line);

        long progSize = Uninstaller.DirSize(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));

        var willDelete = new Label();
        willDelete.Text = "会被删除：\n"
                          + "    程序文件　　" + AppDomain.CurrentDomain.BaseDirectory + "（" + Uninstaller.Human(progSize) + "）\n"
                          + "    桌面和开始菜单快捷方式";
        willDelete.ForeColor = Color.FromArgb(60, 66, 78);
        willDelete.Location = new Point(26, 92);
        willDelete.AutoSize = true;
        Controls.Add(willDelete);

        var keepTitle = new Label();
        keepTitle.Text = "默认保留（勾选后才会删除）：";
        keepTitle.ForeColor = Color.FromArgb(60, 66, 78);
        keepTitle.Location = new Point(26, 162);
        keepTitle.AutoSize = true;
        Controls.Add(keepTitle);

        long dataSize = Uninstaller.DirSize(Uninstaller.DataDir());
        long dshSize = Uninstaller.DirSize(Uninstaller.DshHome());
        long wsSize = Uninstaller.DirSize(Uninstaller.WorkspaceDir());

        SetupBox(dataBox, "DSH-App 的设置、密钥和日志（" + Uninstaller.Human(dataSize) + "）", 186,
                 Uninstaller.DataDir());
        SetupBox(dshBox, "对话、附件、插件和登录凭据（" + Uninstaller.Human(dshSize) + "）", 210,
                 Uninstaller.DshHome());
        SetupBox(wsBox, "工作区文件（" + Uninstaller.Human(wsSize) + "）", 234, Uninstaller.WorkspaceDir());

        var warn = new Label();
        warn.Text = "⚠ 第二项删掉后不可恢复：全部对话会消失（插件可以重装）。\n"
                    + "　　它也是终端里 dsh 的数据目录，删了那边也会变成全新状态。";
        warn.ForeColor = Color.FromArgb(190, 90, 30);
        warn.Location = new Point(48, 258);
        warn.AutoSize = true;
        Controls.Add(warn);

        var note = new Label();
        note.Text = "以上都不会删除 DeepSeek Harness 本体（npm 缓存里约 200 MB），\n"
                    + "要彻底清干净需要另行处理。";
        note.ForeColor = Color.FromArgb(120, 128, 140);
        note.Location = new Point(26, 300);
        note.AutoSize = true;
        Controls.Add(note);

        status.Text = "";
        status.ForeColor = Color.FromArgb(60, 66, 78);
        status.Location = new Point(26, 348);
        status.Size = new Size(530, 20);
        Controls.Add(status);

        goButton.Text = "开始卸载";
        goButton.Location = new Point(336, 386);
        goButton.Size = new Size(110, 32);
        goButton.Click += OnGo;
        Controls.Add(goButton);

        cancelButton.Text = "取消";
        cancelButton.Location = new Point(452, 386);
        cancelButton.Size = new Size(94, 32);
        cancelButton.Click += delegate { Close(); };
        Controls.Add(cancelButton);

        AcceptButton = goButton;
        CancelButton = cancelButton;
    }

    private void SetupBox(CheckBox box, string text, int y, string path)
    {
        box.Text = text;
        box.Checked = false;
        box.Location = new Point(30, y);
        box.AutoSize = true;
        box.Enabled = Directory.Exists(path);
        Controls.Add(box);
    }

    private void OnGo(object sender, EventArgs e)
    {
        // Both destructive opt-ins name the folder they resolve to. The checkbox can only
        // show a size, and these paths are not always what the label implies: DSH_HOME (an
        // environment variable the harness itself honours) moves the harness data folder
        // somewhere else entirely, and %LOCALAPPDATA% can be redirected. Deleting a folder
        // is not something to do on a label the user has to guess at.
        if (dshBox.Checked)
        {
            var answer = MessageBox.Show(
                "你勾选了删除对话、附件、插件和登录凭据。\n\n"
                + "将被删除的文件夹是：\n" + Uninstaller.DshHome() + "\n\n"
                + "删除后无法恢复，而且终端里运行的 DeepSeek Harness 也会变成全新状态。\n\n"
                + "确定要删除吗？",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                dshBox.Checked = false;
                return;
            }
        }

        if (dataBox.Checked || wsBox.Checked)
        {
            var list = new System.Text.StringBuilder();
            if (dataBox.Checked) list.AppendLine("　　" + Uninstaller.DataDir() + "（设置、密钥、日志）");
            if (wsBox.Checked) list.AppendLine("　　" + Uninstaller.WorkspaceDir() + "（工作区文件）");
            var answer = MessageBox.Show(
                "将被删除的文件夹是：\n\n" + list.ToString() + "\n里面的内容删除后无法恢复。\n\n确定要删除吗？",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                dataBox.Checked = false;
                wsBox.Checked = false;
                return;
            }
        }

        SetBusy(true);
        status.Text = "正在卸载…";
        Application.DoEvents();

        try
        {
            var t = new Thread(delegate()
            {
                try
                {
                    UninstallLogic.Run(false, dataBox.Checked, dshBox.Checked, wsBox.Checked,
                        delegate(string s) { SetStatus(s); });
                    BeginInvoke((MethodInvoker)delegate
                    {
                        finished = true;
                        status.Text = "卸载完成。程序文件已删除。";
                        MessageBox.Show(
                            "DSH-App 已卸载。\n\n"
                            + (dataBox.Checked || dshBox.Checked || wsBox.Checked
                                ? "你勾选的数据也已删除。\n\n"
                                : "你的对话、设置和插件都保留着。\n\n")
                            + "程序目录里的残留会在几秒内自动清理干净。",
                            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Close();
                    });
                }
                catch (Exception ex)
                {
                    Uninstaller.Log("uninstall threw: " + ex);
                    BeginInvoke((MethodInvoker)delegate
                    {
                        status.Text = "卸载失败：" + ex.Message;
                        SetBusy(false);
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }
        catch (Exception ex)
        {
            Uninstaller.Log("could not start uninstall: " + ex);
            status.Text = "卸载失败：" + ex.Message;
            SetBusy(false);
        }
    }

    private void SetStatus(string text)
    {
        try
        {
            if (IsHandleCreated && InvokeRequired) { BeginInvoke((MethodInvoker)delegate { status.Text = text; }); return; }
            status.Text = text;
            Application.DoEvents();
        }
        catch { }
    }

    private void SetBusy(bool busy)
    {
        goButton.Enabled = !busy;
        cancelButton.Enabled = !busy;
        dataBox.Enabled = !busy && Directory.Exists(Uninstaller.DataDir());
        dshBox.Enabled = !busy && Directory.Exists(Uninstaller.DshHome());
        wsBox.Enabled = !busy && Directory.Exists(Uninstaller.WorkspaceDir());
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!finished && !goButton.Enabled) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }
}
