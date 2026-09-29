using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

/// <summary>
/// The installation steps themselves, shared by the interactive window and the
/// unattended mode so the two can never diverge.
///
/// Everything here is deliberately non-interactive: it reports progress through
/// a callback and throws on failure, leaving the caller to decide how to talk to
/// the user.
/// </summary>
internal static class InstallSteps
{
    public const string ExecutableName = "DSH-App.exe";
    public const string ShortcutName = "DSH-App.lnk";

    /// <summary>
    /// Marker file proving a directory really is a DSH-App install.
    ///
    /// The uninstaller refuses to delete anything without it. That matters because a
    /// self-extracting installer runs from a temporary folder, so "the directory I am
    /// running from" is not something to delete blindly.
    /// </summary>
    public const string MarkerName = "install.marker";

    /// <summary>
    /// The list of files this installer put into the install folder.
    ///
    /// The uninstaller removes these and nothing else. It used to remove the whole folder,
    /// which is correct for the normal case (a dedicated folder) and destructive for the
    /// other one: a user who unpacks the portable build into a folder that already holds
    /// their own files gets the marker written into that folder, and the uninstaller then
    /// deleted everything in it.
    ///
    /// Written last, like the marker, so a half-finished install never claims files it did
    /// not write.
    /// </summary>
    public const string ManifestName = "install.files";

    /// <summary>Where the product goes when the user expresses no preference.</summary>
    public static string DefaultInstallDir()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(Path.Combine(root, "Programs"), "DSH-App");
    }

    /// <summary>Run every step. Returns the resolved install directory.</summary>
    public static string Run(string payloadPath, string target, bool createDesktopShortcut,
                             Action<string> progress)
    {
        return Run(payloadPath, target, createDesktopShortcut, true, progress);
    }

    /// <summary>
    /// Run every step, optionally skipping the "Apps &amp; features" registration.
    /// The flag exists for the automated uninstall test: an install that registers would
    /// overwrite the machine's real entry and leave it pointing at a test folder.
    /// </summary>
    public static string Run(string payloadPath, string target, bool createDesktopShortcut,
                             bool registerInAppsAndFeatures, Action<string> progress)
    {
        if (string.IsNullOrEmpty(target)) target = DefaultInstallDir();
        target = Path.GetFullPath(target);
        if (progress != null) progress("安装位置：" + target);

        try { Directory.CreateDirectory(target); }
        catch (Exception ex)
        {
            throw new IOException("无法创建安装目录：" + target + "\n" + ex.Message, ex);
        }

        // Must happen BEFORE extraction. Upgrading over a running copy fails: the
        // running exe is an image with no write sharing, so expand.exe cannot replace
        // it and the whole install aborts with an error nobody can act on. Measured
        // behaviour of the uninstaller shows taskkill is the way to stop it.
        StopRunningInstances(progress);

        if (progress != null) progress("正在解压程序文件…");

        // Ask the cabinet what it holds BEFORE unpacking, so the manifest can name the
        // program files without guessing. Asking afterwards would work for a fresh install
        // and be wrong for an upgrade over a folder that already contained same-named files.
        //
        // It is also the gate on the payload: ListEntries refuses a name that is not a simple
        // relative path (an absolute path, a "..\" walk, a quote, a wildcard), and a cabinet
        // like that is not ours. This runs BEFORE expand.exe on purpose -- the system
        // extractor is handed the same names, and is not the place to find out they are bad.
        var programFiles = new List<string>();
        try
        {
            programFiles = CabReader.ListEntries(payloadPath);
        }
        catch (InvalidDataException ex)
        {
            // Refused, not worked around: half-installing an untrusted payload is worse than
            // not installing it. Nothing has been written yet at this point.
            throw new InvalidOperationException(
                "安装数据文件不像是 DSH-App 的安装包，已停止安装，没有改动任何文件。\n\n" + ex.Message, ex);
        }
        catch (Exception ex)
        {
            // Anything else (an unreadable or unusual cabinet) is not fatal: the manifest is
            // written from the directory listing further down if this fails, and a manifest is
            // still better than none.
            Note(progress, "读取安装清单失败（将按目录内容记录）：" + ex.Message);
        }

        ExtractPayload(payloadPath, target, progress);

        string exePath = Path.Combine(target, ExecutableName);
        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException(
                "解压后没有找到 " + ExecutableName + "，安装数据可能已损坏。");
        }
        if (progress != null)
        {
            progress("已解压：" + ExecutableName + "（" + new FileInfo(exePath).Length + " 字节）");
        }

        if (progress != null) progress("正在写入卸载信息…");

        // Prove later that this directory is really ours. Written last, so a
        // half-finished install does not leave a marker that authorises deletion.
        try
        {
            File.WriteAllText(Path.Combine(target, MarkerName),
                "DSH-App install marker" + Environment.NewLine
                + "installed=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
                + "version=" + LauncherVersion(target) + Environment.NewLine,
                new UTF8Encoding(false));
            Note(progress, "已写入安装标记 " + MarkerName);
        }
        catch (Exception ex)
        {
            // Without the marker the uninstaller will refuse to run, so this is worth
            // reporting -- but it must not fail the install itself.
            Note(progress, "写入安装标记失败（卸载时将需要手动删除）：" + ex.Message);
        }

        // What this installer actually created, listed for the uninstaller. Relative paths
        // only: the install folder can be moved, and the uninstaller always works relative
        // to wherever it finds itself.
        //
        // The list is BUILT, not discovered. Listing the directory instead was the first
        // attempt and the test caught it immediately: the install folder is allowed to
        // contain files that were there before us, and a directory listing would put those
        // in the manifest -- which is precisely the mistake this whole change exists to
        // stop making. So: what the cabinet holds, plus the few files written below.
        try
        {
            var listed = new List<string>(programFiles);

            foreach (string name in new string[]
                     {
                         "Uninstaller.exe", "install.marker", "install.files",
                         "uninstall-notes.txt", "uninstall.cmd", "cleanup.cmd",
                         Path.Combine("ui", "app.html"), Path.Combine("ui", "dsh.ico")
                     })
            {
                // Only claim a file that is actually there; "cleanup.cmd" and friends are
                // no longer written, and claiming a file we did not create would delete it.
                if (File.Exists(Path.Combine(target, name)) && !listed.Contains(name)) listed.Add(name);
            }
            listed.Sort(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine("# Files installed by DSH-App. The uninstaller removes these, and only these.");
            sb.AppendLine("# One path per line, relative to this folder.");
            foreach (string rel in listed) sb.AppendLine(rel);
            File.WriteAllText(Path.Combine(target, ManifestName), sb.ToString(), new UTF8Encoding(false));
            Note(progress, "已记录安装清单 " + ManifestName + "（" + listed.Count + " 个文件）");
        }
        catch (Exception ex)
        {
            // Without the manifest the uninstaller falls back to removing the known program
            // files by name. Say so, because it is the weaker of the two.
            Note(progress, "记录安装清单失败（卸载将按已知文件名清理）：" + ex.Message);
        }

        // Register in "Apps & features". Without this the product cannot be found or
        // removed through Windows' own list, which is the first place a user looks.
        // HKCU needs no administrator rights, matching the rest of the install.
        if (!registerInAppsAndFeatures)
        {
            if (progress != null) progress("按测试要求跳过注册卸载项");
        }
        else
        {
            if (progress != null) progress("正在注册到「应用和功能」…");
            try
            {
                WriteUninstallRegistryEntry(target, progress);
            }
            catch (Exception ex)
            {
                // Never fatal: the uninstaller is still in the folder and still works.
                if (progress != null) progress("注册卸载项失败（不影响使用）：" + ex.Message);
            }
        }

        if (createDesktopShortcut)
        {
            if (progress != null) progress("正在创建桌面快捷方式…");
            try
            {
                CreateDesktopShortcut(target, exePath, progress);
            }
            catch (Exception ex)
            {
                // A missing shortcut must never fail the whole install.
                if (progress != null) progress("创建快捷方式失败（不影响使用）：" + ex.Message);
            }
        }

        return target;
    }

    /// <summary>Where the launcher keeps its own data (settings, key, logs).</summary>
    public static string DataDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH-App");
    }

    /// <summary>
    /// True when a previous install left its settings behind, i.e. this is an
    /// upgrade rather than a first-time install. Used only to word the messages
    /// correctly -- the install steps themselves are identical either way, which is
    /// what keeps the original purpose of this package unchanged.
    /// </summary>
    public static bool LooksLikeUpgrade()
    {
        try { return File.Exists(Path.Combine(DataDir(), "config.ini")); }
        catch { return false; }
    }

    /// <summary>
    /// Stop a launcher that is already running, so its files can be replaced.
    ///
    /// The app parks itself in the tray when its window is closed (by design), so
    /// "I closed it" does not mean the process is gone -- and a running exe cannot be
    /// overwritten.
    /// </summary>
    public static void StopRunningInstances(Action<string> progress)
    {
        int running = 0;
        try
        {
            foreach (Process p in Process.GetProcessesByName("DSH-App")) running++;
        }
        catch { }

        if (running == 0)
        {
            Note(progress, "没有正在运行的 DSH-App");
            return;
        }

        Note(progress, "发现 " + running + " 个正在运行的 DSH-App，正在结束它以便替换文件…");
        try
        {
            var psi = new ProcessStartInfo("taskkill", "/f /im " + ExecutableName);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(20000);
                Note(progress, "taskkill 退出码 = " + (p.HasExited ? p.ExitCode.ToString() : "超时"));
            }
        }
        catch (Exception ex)
        {
            Note(progress, "结束旧实例失败（可能需要手动关掉它）：" + ex.Message);
        }

        // Give Windows a moment to release the file handles.
        Thread.Sleep(1200);
    }

    /// <summary>
    /// Unpack the cabinet. expand.exe is preferred because it handles every
    /// cabinet compression; the managed reader is a fallback so a machine with a
    /// restricted System32 still gets a working install.
    /// </summary>
    public static void ExtractPayload(string payloadPath, string target, Action<string> progress)
    {
        string expand = Path.Combine(Environment.SystemDirectory, "expand.exe");
        bool ok = false;

        if (File.Exists(expand))
        {
            // -F:* extracts every file; -R clears any read-only attribute.
            string args = "-F:* -R \"" + payloadPath + "\" \"" + target + "\"";
            Note(progress, "expand.exe " + args);
            try
            {
                var psi = new ProcessStartInfo(expand, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string er = p.StandardError.ReadToEnd();
                    p.WaitForExit(180000);
                    if (o.Trim().Length > 0) Note(progress, o.Trim());
                    if (er.Trim().Length > 0) Note(progress, "stderr: " + er.Trim());
                    ok = p.HasExited && p.ExitCode == 0;
                    Note(progress, "expand 退出码 = " + (p.HasExited ? p.ExitCode.ToString() : "超时"));
                }
            }
            catch (Exception ex)
            {
                Note(progress, "expand 调用失败：" + ex.Message);
            }
        }
        else
        {
            Note(progress, "系统里没有 expand.exe");
        }

        if (!ok)
        {
            Note(progress, "改用内置解包…");
            CabReader.ExtractAll(payloadPath, target, progress);
        }
    }

    /// <summary>Version of the launcher in an install directory, from its file metadata.</summary>
    private static string LauncherVersion(string target)
    {
        try
        {
            string exe = Path.Combine(target, ExecutableName);
            if (File.Exists(exe))
            {
                string v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion;
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch { }
        return "unknown";
    }

    /// <summary>
    /// Add the product to the Windows "Apps &amp; features" list.
    ///
    /// Uses HKEY_CURRENT_USER so no elevation is needed. DisplayVersion is read from
    /// the launcher's own file version, so the number a user sees in Settings matches
    /// the one the app shows -- no second place to keep in step by hand.
    /// </summary>
    public static void WriteUninstallRegistryEntry(string target, Action<string> progress)
    {
        // Prefer the real uninstaller; fall back to the legacy .cmd so an entry is
        // never registered that cannot run.
        string uninstaller = Path.Combine(target, "Uninstaller.exe");
        if (!File.Exists(uninstaller))
        {
            string cmd = Path.Combine(target, "uninstall.cmd");
            if (File.Exists(cmd)) uninstaller = cmd;
            else { Note(progress, "没有卸载程序，跳过注册"); return; }
        }

        string version = LauncherVersion(target);

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                   @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DSH-App"))
        {
            if (key == null) throw new IOException("无法创建注册表项");
            key.SetValue("DisplayName", "DSH-App");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "DSH-App");
            key.SetValue("InstallLocation", target);
            key.SetValue("DisplayIcon", Path.Combine(target, "dsh.ico"));
            key.SetValue("UninstallString", "\"" + uninstaller + "\"");
            key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /S");
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        Note(progress, "已在「应用和功能」里注册（版本 " + version + "）");
    }

    public static void CreateDesktopShortcut(string target, string exePath, Action<string> progress)
    {
        string lnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用");

        object shell = Activator.CreateInstance(shellType);
        try
        {
            object shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = shortcut.GetType();
            st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
            st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { target });
            st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "DSH-App" });
            string ico = Path.Combine(target, "dsh.ico");
            if (File.Exists(ico))
            {
                st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { ico + ",0" });
            }
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            Note(progress, "已创建桌面快捷方式：" + lnk);
        }
        finally
        {
            try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { }
        }
    }

    public static void Launch(string target, Action<string> progress)
    {
        string exe = Path.Combine(target, ExecutableName);
        try
        {
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = target });
            Note(progress, "已启动 " + exe);
        }
        catch (Exception ex)
        {
            Note(progress, "自动启动失败：" + ex.Message);
            throw new InvalidOperationException("已安装，但自动启动失败，请从桌面图标启动。", ex);
        }
    }

    private static void Note(Action<string> progress, string text)
    {
        Installer.Log(text);
        if (progress != null) progress(text);
    }
}

/// <summary>
/// Unattended install path: no window, no prompts, exit code says what happened.
/// Useful for scripted deployment and for verifying the package automatically.
/// </summary>
internal static class SilentInstall
{
    /// <returns>0 on success, 1 on failure.</returns>
    public static int Run(string payloadPath, string target, bool desktop, bool launch, bool register)
    {
        try
        {
            string installed = InstallSteps.Run(payloadPath, target, desktop, register, null);
            Installer.Log("unattended install OK: " + installed);

            if (launch)
            {
                try { InstallSteps.Launch(installed, null); }
                catch (Exception ex) { Installer.Log("launch skipped: " + ex.Message); }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Installer.Log("unattended install FAILED: " + ex);
            return 1;
        }
    }
}
