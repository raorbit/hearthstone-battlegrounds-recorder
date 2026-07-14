using System.Text;

namespace BgRecorder.Logs;

/// <summary>Result of an <see cref="LogConfigWriter.Ensure"/> call.</summary>
public enum LogConfigOutcome
{
    /// <summary>The file already satisfied every required key — nothing was written, no backup made.</summary>
    AlreadyCompliant = 0,

    /// <summary>The file did not exist and was created.</summary>
    Created = 1,

    /// <summary>An existing file was patched/extended in place (a <c>.bak</c> was written first).</summary>
    Updated = 2,
}

public sealed record LogConfigResult(LogConfigOutcome Outcome, string? BackupPath);

/// <summary>
/// Onboarding writer for Hearthstone's <c>log.config</c>. Ensures the <c>[Power]</c> section enables
/// <c>LogLevel=1</c>, <c>FilePrinting=true</c>, and <c>Verbose=true</c> so the game writes the Power.log
/// this recorder tails.
///
/// It MERGES, never clobbers: unknown sections, keys, ordering, and comments are preserved (foreign sections
/// byte-for-byte); only the required keys are patched or appended. An existing file is backed up to
/// <c>&lt;path&gt;.bak</c> before the first byte is changed. If the file is already compliant the call is a
/// pure no-op — no write, no backup. The config path is always supplied by the caller; tests never touch the
/// real file.
/// </summary>
public static class LogConfigWriter
{
    private const string Section = "Power";

    // Ordered so appended keys land in a stable, readable order.
    private static readonly (string Key, string Value)[] Required =
    {
        ("LogLevel", "1"),
        ("FilePrinting", "true"),
        ("Verbose", "true"),
    };

    /// <summary>The real log.config path for this machine (App onboarding uses this; tests do not).</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Blizzard", "Hearthstone", "log.config");

    public static LogConfigResult Ensure(string configPath)
    {
        if (!File.Exists(configPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var fresh = new StringBuilder();
            fresh.Append('[').Append(Section).Append(']').Append('\n');
            foreach (var (k, v) in Required)
                fresh.Append(k).Append('=').Append(v).Append('\n');
            File.WriteAllText(configPath, fresh.ToString(), new UTF8Encoding(false));
            return new LogConfigResult(LogConfigOutcome.Created, null);
        }

        string original = File.ReadAllText(configPath);
        string nl = original.Contains("\r\n") ? "\r\n" : "\n";
        // Lossless decomposition: string.Join(nl, lines) == original.
        var lines = original.Split(nl).ToList();

        int powerIdx = FindSection(lines, Section);
        bool changed;

        if (powerIdx < 0)
        {
            AppendSection(lines);
            changed = true;
        }
        else
        {
            int sectionEnd = powerIdx + 1;
            while (sectionEnd < lines.Count && !IsSectionHeader(lines[sectionEnd], out _))
                sectionEnd++;

            changed = false;
            foreach (var (key, value) in Required)
            {
                int keyIdx = FindKey(lines, powerIdx + 1, sectionEnd, key);
                if (keyIdx < 0)
                {
                    // Append inside the section, after its last non-blank line.
                    int insertAt = sectionEnd;
                    while (insertAt - 1 > powerIdx && lines[insertAt - 1].Trim().Length == 0)
                        insertAt--;
                    lines.Insert(insertAt, $"{key}={value}");
                    sectionEnd++;
                    changed = true;
                }
                else if (!ValueCompliant(lines[keyIdx], value))
                {
                    int eq = lines[keyIdx].IndexOf('=');
                    lines[keyIdx] = lines[keyIdx][..(eq + 1)] + value; // keep the key token, replace value
                    changed = true;
                }
            }
        }

        if (!changed)
            return new LogConfigResult(LogConfigOutcome.AlreadyCompliant, null);

        string backupPath = configPath + ".bak";
        // Back up the pristine original ONCE and never clobber an existing .bak: a prior run that crashed
        // mid-write could have left a truncated config, and overwriting would destroy the only good copy.
        if (!File.Exists(backupPath))
            File.Copy(configPath, backupPath); // exact original bytes (no overwrite)

        // Write atomically: stage into a temp file, then swap it in with File.Replace so a crash can never
        // leave a half-written config in place (readers see either the old or the fully-updated file).
        string tempPath = configPath + ".tmp";
        File.WriteAllText(tempPath, string.Join(nl, lines), new UTF8Encoding(false));
        File.Replace(tempPath, configPath, destinationBackupFileName: null);
        return new LogConfigResult(LogConfigOutcome.Updated, backupPath);
    }

    private static void AppendSection(List<string> lines)
    {
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0)
            lines.Add(""); // blank separator before the new section
        lines.Add($"[{Section}]");
        foreach (var (k, v) in Required)
            lines.Add($"{k}={v}");
        lines.Add(""); // trailing newline
    }

    private static int FindSection(List<string> lines, string name)
    {
        for (int i = 0; i < lines.Count; i++)
            if (IsSectionHeader(lines[i], out var n) && n.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static bool IsSectionHeader(string line, out string name)
    {
        name = "";
        string t = line.Trim();
        if (t.Length >= 2 && t[0] == '[' && t[^1] == ']')
        {
            name = t[1..^1].Trim();
            return true;
        }
        return false;
    }

    private static int FindKey(List<string> lines, int start, int end, string key)
    {
        for (int i = start; i < end; i++)
            if (TryKey(lines[i], out var k) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static bool TryKey(string line, out string key)
    {
        key = "";
        string t = line.TrimStart();
        if (t.Length == 0 || t[0] == ';' || t[0] == '#') return false; // comment / blank
        int eq = line.IndexOf('=');
        if (eq < 0) return false;
        key = line[..eq].Trim();
        return key.Length > 0;
    }

    private static bool ValueCompliant(string line, string required)
    {
        int eq = line.IndexOf('=');
        if (eq < 0) return false;
        return line[(eq + 1)..].Trim().Equals(required, StringComparison.OrdinalIgnoreCase);
    }
}
