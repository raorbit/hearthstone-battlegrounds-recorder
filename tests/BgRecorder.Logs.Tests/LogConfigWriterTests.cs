using BgRecorder.Logs;
using Xunit;

namespace BgRecorder.Logs.Tests;

/// <summary>
/// The onboarding log.config merge matrix. Every case uses a throwaway temp file — the real
/// %LocalAppData%\Blizzard\Hearthstone\log.config is never touched.
/// </summary>
public sealed class LogConfigWriterTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public LogConfigWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bgrec-logcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "log.config");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string[] PowerSection(string text)
    {
        // Extract the [Power] section body lines (between the header and the next section / EOF).
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => l.Trim().Equals("[Power]", StringComparison.OrdinalIgnoreCase));
        Assert.True(start >= 0, "no [Power] section");
        var body = new List<string>();
        for (int i = start + 1; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']')) break;
            body.Add(lines[i]);
        }
        return body.ToArray();
    }

    private void AssertCompliant()
    {
        var body = PowerSection(File.ReadAllText(_path));
        bool Has(string k, string v) => body.Any(l =>
        {
            int eq = l.IndexOf('=');
            return eq > 0 && l[..eq].Trim().Equals(k, StringComparison.OrdinalIgnoreCase)
                          && l[(eq + 1)..].Trim().Equals(v, StringComparison.OrdinalIgnoreCase);
        });
        Assert.True(Has("LogLevel", "1"), "LogLevel=1 missing");
        Assert.True(Has("FilePrinting", "true"), "FilePrinting=true missing");
        Assert.True(Has("Verbose", "true"), "Verbose=true missing");
    }

    [Fact]
    public void Missing_file_is_created_with_no_backup()
    {
        var result = LogConfigWriter.Ensure(_path);

        Assert.Equal(LogConfigOutcome.Created, result.Outcome);
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(_path + ".bak"));
        AssertCompliant();
    }

    [Fact]
    public void Already_compliant_file_is_a_pure_no_op()
    {
        File.WriteAllText(_path, "[Power]\nLogLevel=1\nFilePrinting=true\nVerbose=true\n");
        byte[] before = File.ReadAllBytes(_path);

        var result = LogConfigWriter.Ensure(_path);

        Assert.Equal(LogConfigOutcome.AlreadyCompliant, result.Outcome);
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(_path + ".bak"), "no .bak on a no-op");
        Assert.Equal(before, File.ReadAllBytes(_path)); // byte-for-byte untouched
    }

    [Fact]
    public void Already_compliant_tolerates_case_and_extra_keys()
    {
        // HDT-style config: mixed casing, extra keys, other sections — must stay a no-op.
        string original = "[Power]\r\nLogLevel=1\r\nFilePrinting=True\r\nConsolePrinting=false\r\nVerbose=TRUE\r\n";
        File.WriteAllText(_path, original);

        var result = LogConfigWriter.Ensure(_path);

        Assert.Equal(LogConfigOutcome.AlreadyCompliant, result.Outcome);
        Assert.Equal(original, File.ReadAllText(_path));
    }

    [Fact]
    public void Partial_power_section_gets_missing_keys_appended_and_a_backup()
    {
        File.WriteAllText(_path, "[Power]\nLogLevel=1\n");

        var result = LogConfigWriter.Ensure(_path);

        Assert.Equal(LogConfigOutcome.Updated, result.Outcome);
        Assert.Equal(_path + ".bak", result.BackupPath);
        Assert.True(File.Exists(_path + ".bak"));
        Assert.Equal("[Power]\nLogLevel=1\n", File.ReadAllText(_path + ".bak")); // original preserved
        AssertCompliant();
    }

    [Fact]
    public void Wrong_value_is_patched_in_place()
    {
        File.WriteAllText(_path, "[Power]\nLogLevel=0\nFilePrinting=true\nVerbose=true\n");

        var result = LogConfigWriter.Ensure(_path);

        Assert.Equal(LogConfigOutcome.Updated, result.Outcome);
        AssertCompliant();
        Assert.DoesNotContain("LogLevel=0", File.ReadAllText(_path));
    }

    [Fact]
    public void Foreign_sections_are_preserved_byte_for_byte()
    {
        string foreign =
            "; user comment kept verbatim\r\n" +
            "[LoadingScreen]\r\n" +
            "LogLevel=1\r\n" +
            "FilePrinting=false\r\n" +
            "\r\n" +
            "[Zone]\r\n" +
            "LogLevel=2\r\n";
        // [Power] missing entirely, plus a partial-nothing — writer must append [Power] and not touch foreign.
        File.WriteAllText(_path, foreign);

        var result = LogConfigWriter.Ensure(_path);

        Assert.Equal(LogConfigOutcome.Updated, result.Outcome);
        string after = File.ReadAllText(_path);
        // The two foreign sections (and the comment) survive unchanged, in order.
        Assert.Contains(foreign.TrimEnd('\r', '\n'), after);
        Assert.Contains("; user comment kept verbatim", after);
        Assert.Contains("[LoadingScreen]", after);
        Assert.Contains("[Zone]", after);
        AssertCompliant();
        // And the backup holds the exact original bytes.
        Assert.Equal(foreign, File.ReadAllText(_path + ".bak"));
    }

    [Fact]
    public void Existing_power_among_foreign_sections_patches_only_power()
    {
        string original =
            "[Achievement]\nLogLevel=1\n\n" +
            "[Power]\nFilePrinting=true\n\n" +   // missing LogLevel + Verbose
            "[Net]\nLogLevel=3\n";
        File.WriteAllText(_path, original);

        var result = LogConfigWriter.Ensure(_path);

        Assert.Equal(LogConfigOutcome.Updated, result.Outcome);
        string after = File.ReadAllText(_path);
        Assert.Contains("[Achievement]\nLogLevel=1", after); // foreign untouched
        Assert.Contains("[Net]\nLogLevel=3", after);
        AssertCompliant();
    }
}
