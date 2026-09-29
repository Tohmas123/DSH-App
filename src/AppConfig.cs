using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Tiny key=value settings store. A deliberate choice over JSON: no dependency,
/// trivially diffable by a human, and impossible to corrupt into a state that
/// needs a parser to diagnose.
/// </summary>
public static class AppConfig
{
    private static readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static string path;

    public const string KeyPort = "port";
    public const string KeyWorkspace = "workspace";
    public const string KeySetupDone = "setupDone";
    public const string KeyZoom = "zoom";

    /// <summary>
    /// The launcher version that last ran on this machine. Written on every start.
    /// Comparing it with the running build is how an upgrade is detected: a value
    /// that is present and differs means "you just updated"; an absent value means
    /// this is the first version that knows about versioning at all.
    /// </summary>
    public const string KeyLastRunVersion = "lastRunVersion";

    public static void Load(string dataDir)
    {
        path = Path.Combine(dataDir, "config.ini");
        Values.Clear();
        Values[KeyPort] = "3080";
        Values[KeyWorkspace] = DefaultWorkspace();
        Values[KeySetupDone] = "false";

        try
        {
            if (File.Exists(path))
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    if (k.Length > 0) Values[k] = v;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("config load failed, using defaults: " + ex.Message);
        }
        AppLogger.Write("config loaded: port=" + GetInt(KeyPort, 3080) + " setupDone=" + GetBool(KeySetupDone, false));
    }

    public static string DefaultWorkspace()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string ws = Path.Combine(docs, "DSH-App-Workspace");
        try { Directory.CreateDirectory(ws); } catch { }
        return ws;
    }

    public static string Get(string key, string fallback)
    {
        string v;
        if (Values.TryGetValue(key, out v) && !string.IsNullOrEmpty(v)) return v;
        return fallback;
    }

    public static int GetInt(string key, int fallback)
    {
        int v;
        if (int.TryParse(Get(key, ""), out v)) return v;
        return fallback;
    }

    /// <summary>
    /// Float setting, parsed culture-independently first so a hand-edited
    /// "zoom=1.15" behaves the same on every machine.
    /// </summary>
    public static float GetFloat(string key, float fallback)
    {
        string v = Get(key, "");
        float f;
        if (float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out f)) return f;
        if (float.TryParse(v, out f)) return f;
        return fallback;
    }

    public static bool GetBool(string key, bool fallback)
    {
        string v = Get(key, "").ToLowerInvariant();
        if (v == "true" || v == "1" || v == "yes") return true;
        if (v == "false" || v == "0" || v == "no") return false;
        return fallback;
    }

    public static void Set(string key, string value)
    {
        Values[key] = value ?? "";
    }

    public static void SetBool(string key, bool value)
    {
        Values[key] = value ? "true" : "false";
    }

    /// <summary>
    /// Drops a setting entirely, so the file goes back to "this was never configured"
    /// rather than carrying an empty value. Those are different states on purpose: an
    /// explicit zoom of 1.0 would keep overwriting whatever zoom the user set with
    /// Ctrl + / Ctrl + wheel, while an absent line leaves their choice alone.
    /// </summary>
    public static void Remove(string key)
    {
        if (key != null) Values.Remove(key);
    }

    public static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DSH-App settings. Safe to edit by hand.");
            sb.AppendLine("# workspace = the folder the assistant may read and write");
            sb.AppendLine("# zoom = interface scale, e.g. 1.15. Omit the line for 1.0 (Ctrl +/- still works)");
            foreach (var kv in Values)
            {
                sb.AppendLine(kv.Key + "=" + kv.Value);
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            AppLogger.Write("config saved to " + path);
        }
        catch (Exception ex)
        {
            AppLogger.Write("config save failed: " + ex.Message);
        }
    }
}

/// <summary>
/// Stores the DeepSeek API key.
///
/// The key is kept in the app's own data folder and handed to the harness
/// through its launching environment. The harness credentials file is read only
/// to answer "does a key already exist?", never written, so records belonging to
/// other clients in that file stay untouched.
/// </summary>
public static class KeyStore
{
    private static string keyFile;

    public static void Init(string dataDir)
    {
        keyFile = Path.Combine(dataDir, "apikey.txt");
    }

    public static string Load()
    {
        try
        {
            if (File.Exists(keyFile))
            {
                string s = File.ReadAllText(keyFile, Encoding.UTF8).Trim();
                if (s.Length > 0) return s;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("apikey load failed: " + ex.Message);
        }

        string env = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrEmpty(env))
        {
            AppLogger.Write("apikey taken from environment");
            return env.Trim();
        }
        return "";
    }

    public static void Save(string key)
    {
        try
        {
            File.WriteAllText(keyFile, (key ?? "").Trim(), new UTF8Encoding(false));
            AppLogger.Write("apikey saved (" + Describe(key) + ")");
        }
        catch (Exception ex)
        {
            AppLogger.Write("apikey save failed: " + ex.Message);
        }
    }

    /// <summary>Look for a key the harness itself already holds.</summary>
    public static bool HarnessHasKey()
    {
        try
        {
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            }
            string cred = Path.Combine(home, ".credentials.yaml");
            if (!File.Exists(cred)) return false;
            string text = File.ReadAllText(cred, Encoding.UTF8);
            return text.IndexOf("DEEPSEEK_API_KEY", StringComparison.Ordinal) >= 0;
        }
        catch { return false; }
    }

    /// <summary>Never log a secret; log only enough to tell keys apart.</summary>
    public static string Describe(string key)
    {
        if (string.IsNullOrEmpty(key)) return "empty";
        string head = key.Length >= 7 ? key.Substring(0, 7) : key.Substring(0, Math.Min(3, key.Length));
        return "len=" + key.Length + " prefix=" + head + "...";
    }
}
