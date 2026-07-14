using System.Diagnostics;
using System.Text;

namespace SpikeA.LogFidelity;

/// <summary>
/// Splits a Power.log into per-match segments and writes two files per match:
///   raw       → fixtures/raw/match-NN.log   (gitignored: fixtures/raw/ AND *.log)
///   sanitized → fixtures/sanitized/match-NN.txt (committed; .txt because the repo ignores *.log)
///
/// A match runs from a GameState CREATE_GAME line up to (but not including) the next CREATE_GAME,
/// or to EOF for the final match.
/// </summary>
public sealed class Extractor
{
    public const string CreateGameMarker = "GameState.DebugPrintPower() - CREATE_GAME";

    public sealed record MatchFiles(int Index, string RawPath, string SanitizedPath, int RawLineCount, int SanitizedLineCount, IReadOnlyDictionary<string, string> Mapping);

    public sealed record Report(IReadOnlyList<MatchFiles> Matches, string RawDir, string SanitizedDir, bool Distilled);

    private readonly string _projectDir;

    public Extractor(string projectDir) => _projectDir = projectDir;

    /// <summary>
    /// Split <paramref name="powerLogPath"/> into per-match files. raw/ always gets the full slice;
    /// sanitized/ gets a distilled (parser-relevant, ~MB) sanitized fixture unless <paramref name="fullSanitized"/>.
    /// </summary>
    public Report Extract(string powerLogPath, bool fullSanitized = false)
    {
        string rawDir = Path.Combine(_projectDir, "fixtures", "raw");
        string sanitizedDir = Path.Combine(_projectDir, "fixtures", "sanitized");
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(sanitizedDir);

        // Safety gate: a raw fixture path MUST be gitignored before we ever write private data.
        string probe = Path.Combine(rawDir, "match-01.log");
        if (!GitCheckIgnored(probe))
            throw new InvalidOperationException(
                $"Refusing to write raw fixtures: '{probe}' is NOT gitignored (git check-ignore returned non-zero). " +
                "Fix .gitignore before extracting so unsanitized logs cannot enter Git.");

        var matches = new List<MatchFiles>();
        var buffer = new List<string>();
        int index = 0;

        void Flush()
        {
            if (buffer.Count == 0) return;
            index++;
            string rawPath = Path.Combine(rawDir, $"match-{index:D2}.log");
            string sanPath = Path.Combine(sanitizedDir, $"match-{index:D2}.txt");

            // raw/: full-fidelity slice (gitignored, local only).
            File.WriteAllLines(rawPath, buffer);

            // sanitized/: distilled (default) + redacted, committed to Git.
            var sanitizer = new Sanitizer();
            var sanitized = new List<string>(buffer.Count);
            foreach (var l in buffer)
            {
                if (!fullSanitized && !LineFilter.IsParserRelevant(l))
                    continue;
                sanitized.Add(sanitizer.Sanitize(l));
            }
            File.WriteAllLines(sanPath, sanitized);

            matches.Add(new MatchFiles(index, rawPath, sanPath, buffer.Count, sanitized.Count,
                new Dictionary<string, string>(sanitizer.Mapping)));
            buffer.Clear();
        }

        // Open with FileShare.ReadWrite: the game holds Power.log open for writing.
        using var fs = new FileStream(powerLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        bool started = false; // ignore any preamble before the first CREATE_GAME
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Contains(CreateGameMarker, StringComparison.Ordinal))
            {
                if (started) Flush(); // close the previous match before starting this one
                started = true;
            }
            if (started) buffer.Add(line);
        }
        Flush(); // final match

        return new Report(matches, rawDir, sanitizedDir, !fullSanitized);
    }

    /// <summary>Returns true if 'git check-ignore &lt;path&gt;' reports the path as ignored (exit code 0).</summary>
    public static bool GitCheckIgnored(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"check-ignore -- \"{path}\"")
            {
                WorkingDirectory = Path.GetDirectoryName(path) ?? ".",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
