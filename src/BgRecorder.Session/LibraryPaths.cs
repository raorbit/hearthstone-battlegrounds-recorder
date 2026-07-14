namespace BgRecorder.Session;

/// <summary>Final library file naming: one MP4 per match, named from the match start time.</summary>
public static class LibraryPaths
{
    /// <summary>
    /// Builds a unique output path like <c>BG_2026-07-14_20-31-05.mp4</c> in the library folder,
    /// creating the folder if needed and suffixing on collision.
    /// </summary>
    public static string CreateUniqueMp4Path(string libraryDir, DateTimeOffset matchStartedAt)
    {
        Directory.CreateDirectory(libraryDir);
        var baseName = $"BG_{matchStartedAt.LocalDateTime:yyyy-MM-dd_HH-mm-ss}";
        var path = Path.Combine(libraryDir, baseName + ".mp4");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(libraryDir, $"{baseName}_{i}.mp4");
        }
        return path;
    }
}
