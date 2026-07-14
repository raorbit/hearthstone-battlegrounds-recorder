using System.Text;

namespace BgRecorder.Logs;

/// <summary>
/// Writes Power.log-formatted lines into a fake install directory's newest session folder, exactly the way
/// the game would append them. Backs two flows:
///   * onboarding's live test-feed verifier — write a short synthetic feed, then confirm
///     <see cref="GameEventSource"/> discovers the folder and emits events;
///   * the M2 end-to-end test — replay a sanitized fixture through the real discovery + tail + parse path.
///
/// On construction it creates <c>&lt;install&gt;\Logs\Hearthstone_YYYY_MM_DD_HH_MM_SS\Power.log</c> (folder
/// name derived from <paramref name="sessionStart"/> so the discovery seed is deterministic) as an empty
/// file. Lines are appended verbatim (fixture lines already carry the "D HH:mm:ss…" prefix) with
/// FileShare.ReadWrite so a concurrent tailer can read while writing.
/// </summary>
public sealed class TestFeedWriter
{
    /// <summary>The <c>Hearthstone_YYYY_MM_DD_HH_MM_SS</c> session folder created under <c>&lt;install&gt;\Logs</c>.</summary>
    public string SessionFolder { get; }

    /// <summary>Full path to the Power.log being appended.</summary>
    public string PowerLogPath { get; }

    /// <summary>The session start used to name the folder (seeds the discovery/parse date cursor).</summary>
    public DateTimeOffset SessionStart { get; }

    public TestFeedWriter(string installDir, DateTimeOffset sessionStart)
    {
        SessionStart = sessionStart;
        string folderName = "Hearthstone_" + sessionStart.ToString("yyyy_MM_dd_HH_mm_ss");
        SessionFolder = Path.Combine(installDir, "Logs", folderName);
        Directory.CreateDirectory(SessionFolder);
        PowerLogPath = Path.Combine(SessionFolder, "Power.log");
        if (!File.Exists(PowerLogPath))
            using (File.Create(PowerLogPath)) { }
    }

    /// <summary>Appends one raw line (a trailing newline is added).</summary>
    public void AppendLine(string line) => AppendLines(new[] { line });

    /// <summary>Appends raw lines verbatim, each terminated with '\n'.</summary>
    public void AppendLines(IEnumerable<string> lines)
    {
        using var fs = new FileStream(PowerLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" };
        foreach (var line in lines)
            writer.Write(line + "\n");
    }

    /// <summary>Appends every line of a fixture file (e.g. a sanitized match-NN.txt) verbatim.</summary>
    public void AppendFixture(string fixturePath) => AppendLines(File.ReadLines(fixturePath));
}
