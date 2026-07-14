using System.Globalization;
using System.Text.RegularExpressions;

namespace BgRecorder.Logs;

/// <summary>
/// Finds the newest <c>&lt;install&gt;\Logs\Hearthstone_YYYY_MM_DD_HH_MM_SS\Power.log</c>.
///
/// Modern clients (patch ~25.0.4+) write per-session timestamped subfolders. Only the newest session folder
/// holds a live "Power.log"; older folders keep a rotated "Power_old.log" and have no "Power.log" at all.
/// Folders are ranked by their parsed name timestamp (falling back to directory creation time when the name
/// does not parse); the chosen log is the newest folder that actually has a Power.log. The folder-name
/// timestamp also seeds the <see cref="DateCursor"/> (log lines carry no date).
/// </summary>
internal static partial class LogSessionDiscovery
{
    [GeneratedRegex(@"^Hearthstone_(\d{4})_(\d{2})_(\d{2})_(\d{2})_(\d{2})_(\d{2})$")]
    private static partial Regex FolderName();

    public sealed record Folder(string Path, string Name, DateTime Stamp, bool StampParsed, bool HasPowerLog);

    public sealed record Result(
        string LogsDir,
        bool LogsDirExists,
        IReadOnlyList<Folder> Folders,
        Folder? Chosen,
        string? PowerLogPath,
        DateTime? SeedDate,
        TimeSpan? SeedTime);

    public static Result Discover(string installDir)
    {
        string logsDir = Path.Combine(installDir, "Logs");
        if (!Directory.Exists(logsDir))
            return new Result(logsDir, false, Array.Empty<Folder>(), null, null, null, null);

        var folders = new List<Folder>();
        foreach (string dir in Directory.EnumerateDirectories(logsDir, "Hearthstone_*"))
        {
            string name = Path.GetFileName(dir);
            var m = FolderName().Match(name);
            DateTime stamp;
            bool parsed = false;
            if (m.Success &&
                DateTime.TryParseExact(
                    $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value} {m.Groups[4].Value}:{m.Groups[5].Value}:{m.Groups[6].Value}",
                    "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
            {
                parsed = true;
            }
            else
            {
                stamp = Directory.GetCreationTime(dir);
            }

            bool hasLog = File.Exists(Path.Combine(dir, "Power.log"));
            folders.Add(new Folder(dir, name, stamp, parsed, hasLog));
        }

        // Newest first.
        folders.Sort((a, b) => b.Stamp.CompareTo(a.Stamp));

        // Chosen = newest folder that has a Power.log; else the newest folder overall (for context/seeding).
        Folder? chosen = folders.FirstOrDefault(f => f.HasPowerLog) ?? folders.FirstOrDefault();
        string? logPath = chosen is { HasPowerLog: true } ? Path.Combine(chosen.Path, "Power.log") : null;

        DateTime? seedDate = null;
        TimeSpan? seedTime = null;
        if (chosen is { StampParsed: true })
        {
            seedDate = chosen.Stamp.Date;
            seedTime = chosen.Stamp.TimeOfDay;
        }

        return new Result(logsDir, true, folders, chosen, logPath, seedDate, seedTime);
    }
}
