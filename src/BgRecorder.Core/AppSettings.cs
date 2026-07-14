namespace BgRecorder.Core;

/// <summary>Persisted settings (JSON in %AppData%\BgRecorder). M2 surface only; M6 grows this.</summary>
public sealed record AppSettings
{
    public string? HearthstoneInstallDir { get; init; } = @"C:\Program Files (x86)\Hearthstone";

    public string LibraryDir { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "BG Recorder");

    /// <summary>Partial files never enter the library; staging lives on the same volume for cheap moves.</summary>
    public string StagingDir { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "BG Recorder", ".staging");

    public int Fps { get; init; } = 60;
    public int BitrateMbps { get; init; } = 12;

    /// <summary>Game-only audio where supported; the engine falls back to system loopback below build 20348.</summary>
    public bool GameOnlyAudio { get; init; } = true;

    /// <summary>Default OFF per the plan's privacy-respecting defaults.</summary>
    public bool MixMicrophone { get; init; }
}
