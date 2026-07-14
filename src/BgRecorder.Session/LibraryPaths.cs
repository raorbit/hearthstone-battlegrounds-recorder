namespace BgRecorder.Session;

/// <summary>Final library file naming: one MP4 per match, named from the match start time.</summary>
public static class LibraryPaths
{
    /// <summary>
    /// Builds the library output path for a recording, e.g. <c>BG_2026-07-14_20-31-05_1a2b3c4d.mp4</c>,
    /// creating the folder if needed. The name is DETERMINISTIC in the session id: the same session
    /// always maps to the same path, so a startup-recovery re-mux of a crashed session overwrites its
    /// own partial output rather than leaving an orphan copy. The session-id suffix also keeps two
    /// different matches that share a start second from colliding.
    /// </summary>
    public static string CreateSessionMp4Path(string libraryDir, DateTimeOffset matchStartedAt, string sessionId)
    {
        Directory.CreateDirectory(libraryDir);
        var stamp = matchStartedAt.LocalDateTime.ToString("yyyy-MM-dd_HH-mm-ss");
        var suffix = string.IsNullOrEmpty(sessionId)
            ? "session"
            : sessionId.Length > 8 ? sessionId[..8] : sessionId;
        return Path.Combine(libraryDir, $"BG_{stamp}_{suffix}.mp4");
    }
}
