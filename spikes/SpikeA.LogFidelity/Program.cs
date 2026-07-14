using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SpikeA.LogFidelity;

// Spike A: Hearthstone Battlegrounds Power.log fidelity.
// Subcommands: discover | extract | parse | tail   (see README.md).

// Default location for the JSON parse report (this session's scratchpad).
const string DefaultReport =
    @"C:\Users\raorb\AppData\Local\Temp\claude\C--Users-raorb-Projects-hearthstone-battlegrounds-recorder\4db57b15-f4e1-4676-b4f3-2e908ff61ea9\scratchpad\spikeA-parse-report.json";

// This project's directory (…/spikes/SpikeA.LogFidelity), independent of cwd.
static string ProjectDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SpikeA.LogFidelity.csproj")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
};

if (args.Length == 0)
{
    Console.WriteLine("usage: SpikeA.LogFidelity <discover|extract|parse|tail> [args]");
    return 1;
}

string cmd = args[0].ToLowerInvariant();
try
{
    switch (cmd)
    {
        case "discover": return CmdDiscover(args);
        case "extract": return CmdExtract(args);
        case "parse": return CmdParse(args);
        case "tail": return CmdTail(args);
        default:
            Console.WriteLine($"unknown command '{cmd}'");
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 2;
}

int CmdDiscover(string[] a)
{
    string install = a.Length > 1 ? a[1] : Discovery.DefaultInstallDir;
    var r = Discovery.Discover(install);
    Console.WriteLine($"install : {install}");
    Console.WriteLine($"logsDir : {r.LogsDir}  (exists={r.LogsDirExists})");
    if (!r.LogsDirExists)
    {
        Console.WriteLine("no Logs directory — cannot discover.");
        return 1;
    }
    int withLog = r.Folders.Count(f => f.HasPowerLog);
    Console.WriteLine($"folders : {r.Folders.Count} session folder(s), {withLog} with Power.log, {r.Folders.Count - withLog} without");
    foreach (var f in r.Folders)
        Console.WriteLine($"   {(ReferenceEquals(f, r.Chosen) ? "*" : " ")} {f.Name}  stamp={f.Stamp:yyyy-MM-dd HH:mm:ss}{(f.StampParsed ? "" : " (from ctime)")}  Power.log={(f.HasPowerLog ? "yes" : "no")}");
    Console.WriteLine();
    if (r.PowerLogPath is null)
    {
        Console.WriteLine("no folder contained a Power.log.");
        return 1;
    }
    Console.WriteLine($"CHOSEN  : {r.PowerLogPath}");
    Console.WriteLine($"seed    : {r.SeedDate:yyyy-MM-dd} {r.SeedTime}");
    return 0;
}

int CmdExtract(string[] a)
{
    if (a.Length < 2)
    {
        Console.WriteLine("usage: extract <powerLogPath> [--out <projectDir>]");
        return 1;
    }
    string logPath = a[1];
    string projectDir = ProjectDir();
    bool full = false;
    for (int i = 2; i < a.Length; i++)
    {
        if (a[i] == "--out" && i + 1 < a.Length) projectDir = a[i + 1];
        if (a[i] == "--full") full = true; // full sanitized copies instead of distilled (large)
    }

    if (!File.Exists(logPath))
    {
        Console.WriteLine($"Power.log not found: {logPath}");
        return 1;
    }

    var extractor = new Extractor(projectDir);
    var rep = extractor.Extract(logPath, fullSanitized: full);
    Console.WriteLine($"extracted {rep.Matches.Count} matches  (sanitized = {(rep.Distilled ? "DISTILLED parser-relevant lines" : "FULL copy")})");
    Console.WriteLine($"  raw       : {rep.RawDir}   (gitignored, full fidelity)");
    Console.WriteLine($"  sanitized : {rep.SanitizedDir} (committed)");
    foreach (var m in rep.Matches)
    {
        string map = string.Join(", ", m.Mapping.Select(kv => $"{kv.Key}→{kv.Value}"));
        Console.WriteLine($"  match-{m.Index:D2}  raw={m.RawLineCount,7:N0} lines  sanitized={m.SanitizedLineCount,6:N0} lines  tags[{map}]");
    }

    // Prove no privacy leaks in the committed (sanitized) fixtures.
    var (scanned, leaks) = LeakScan.Scan(rep.SanitizedDir);
    Console.WriteLine();
    Console.WriteLine($"leak scan : {scanned} sanitized files scanned");
    if (leaks.Count == 0)
    {
        Console.WriteLine("  PASS — zero hits for BattleTags, non-zero GameAccountId, 'raorbit', account numbers, or user paths.");
        return 0;
    }
    Console.WriteLine("  FAIL — leaks detected:");
    foreach (var l in leaks) Console.WriteLine($"    {l}");
    return 3;
}

int CmdParse(string[] a)
{
    if (a.Length < 2)
    {
        Console.WriteLine("usage: parse <fileOrDir> [--seed-date YYYY-MM-DD] [--report <path>]");
        return 1;
    }
    string target = a[1];
    var seedDate = new DateOnly(2026, 7, 13);
    var seedTime = new TimeSpan(21, 56, 22);
    string reportPath = DefaultReport;
    for (int i = 2; i < a.Length; i++)
    {
        if (a[i] == "--seed-date" && i + 1 < a.Length && DateOnly.TryParse(a[i + 1], CultureInfo.InvariantCulture, out var d)) seedDate = d;
        if (a[i] == "--report" && i + 1 < a.Length) reportPath = a[i + 1];
    }

    var parser = new BgMatchParser();
    var all = new List<MatchResult>();

    List<string> files;
    if (Directory.Exists(target))
    {
        files = Directory.EnumerateFiles(target, "match-*.txt").ToList();
        if (files.Count == 0) files = Directory.EnumerateFiles(target, "match-*.log").ToList();
        files.Sort(StringComparer.OrdinalIgnoreCase);
    }
    else if (File.Exists(target))
    {
        files = new List<string> { target };
    }
    else
    {
        Console.WriteLine($"not found: {target}");
        return 1;
    }

    foreach (var f in files)
    {
        // Fresh cursor per file (each fixture is independent); one cursor handles a multi-match file.
        var cursor = new DateCursor(seedDate, seedTime);
        all.AddRange(parser.Parse(ReadShared(f), cursor));
    }
    for (int i = 0; i < all.Count; i++) all[i].Index = i + 1;

    // Human table.
    Console.WriteLine($"parsed {all.Count} match(es) from {(Directory.Exists(target) ? "dir" : "file")} {target}");
    Console.WriteLine();
    Console.WriteLine($"{"#",2}  {"start",-19}  {"gameType",-18}  {"hero",-22}  {"pl",2}  {"tvn",3}  {"cmb",3}  {"bv",3}  {"playstate",-9}  {"trunc",5}");
    Console.WriteLine(new string('-', 110));
    foreach (var m in all)
    {
        Console.WriteLine(
            $"{m.Index,2}  {m.StartTimestamp,-19}  {m.GameType,-18}  {m.HeroCardId,-22}  {m.Place,2}  {m.TavernTurns,3}  {m.CombatCount,3}  {m.BoardVisualCombatCount,3}  {m.Playstate,-9}  {(m.Truncated ? "YES" : "no"),5}");
        foreach (var an in m.Anomalies)
            Console.WriteLine($"        ! {an}");
    }

    // Full JSON report.
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(all, json));
    Console.WriteLine();
    Console.WriteLine($"JSON report → {reportPath}");

    // One JSON object per match to stdout as well (task requirement).
    Console.WriteLine();
    foreach (var m in all)
        Console.WriteLine(JsonSerializer.Serialize(m, json));

    return 0;
}

int CmdTail(string[] a)
{
    string install = a.Length > 1 ? a[1] : Discovery.DefaultInstallDir;
    int seconds = 20;
    for (int i = 2; i < a.Length - 1; i++)
        if (a[i] == "--seconds" && int.TryParse(a[i + 1], out var s)) seconds = s;
    var tailer = new Tailer(install);
    tailer.Run(TimeSpan.FromSeconds(seconds));
    return 0;
}

static IEnumerable<string> ReadShared(string path)
{
    // FileShare.ReadWrite so we can also parse a live Power.log the game holds open.
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs);
    string? line;
    while ((line = reader.ReadLine()) is not null)
        yield return line;
}

/// <summary>Scans sanitized fixtures for anything that would be a privacy leak.</summary>
static partial class LeakScan
{
    [GeneratedRegex(@"(?<![\w#])[\p{L}\p{N}_]+#\d+\b")]
    private static partial Regex AnyBattleTag();

    [GeneratedRegex(@"GameAccountId=\[hi=(\d+) lo=(\d+)\]")]
    private static partial Regex AnyAccount();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\")]
    private static partial Regex AnyUserPath();

    // Known originals from this machine's session — explicit proof they never survive sanitization.
    private static readonly string[] KnownOriginals = { "raorbit", "0", "0" };

    public static (int scanned, List<string> leaks) Scan(string dir)
    {
        var leaks = new List<string>();
        int scanned = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "match-*.txt"))
        {
            scanned++;
            string name = Path.GetFileName(file);
            foreach (var line in File.ReadLines(file))
            {
                foreach (var orig in KnownOriginals)
                    if (line.Contains(orig, StringComparison.Ordinal))
                        leaks.Add($"{name}: known-original '{orig}' → {Trunc(line)}");

                foreach (Match m in AnyBattleTag().Matches(line))
                    if (!Regex.IsMatch(m.Value, @"^Player\d+#00000$"))
                        leaks.Add($"{name}: un-mapped BattleTag '{m.Value}'");

                foreach (Match m in AnyAccount().Matches(line))
                    if (m.Groups[1].Value != "0" || m.Groups[2].Value != "0")
                        leaks.Add($"{name}: non-zero GameAccountId '{m.Value}'");

                if (AnyUserPath().IsMatch(line))
                    leaks.Add($"{name}: user path → {Trunc(line)}");
            }
        }
        // De-dup for readability.
        return (scanned, leaks.Distinct().Take(50).ToList());
    }

    private static string Trunc(string s) => s.Length <= 120 ? s : s[..120] + "…";
}
