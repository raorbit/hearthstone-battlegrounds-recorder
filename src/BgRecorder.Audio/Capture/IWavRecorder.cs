namespace BgRecorder.Audio.Capture;

/// <summary>
/// A single audio stream captured to a staging WAV. Runs on its own thread and
/// keeps whatever it captured even if the device drops out mid-recording — a lost
/// device raises <see cref="Failed"/> but never tears the process down, so the
/// caller can still finalize a partial WAV.
/// </summary>
internal interface IWavRecorder : IDisposable
{
    /// <summary>Absolute path of the WAV being written.</summary>
    string OutputPath { get; }

    /// <summary>Wall clock stamped at the first delivered data packet (frames &gt; 0).</summary>
    DateTimeOffset? FirstSampleWallClock { get; }

    /// <summary>Raised once if the capture stops abnormally (device loss, driver error).</summary>
    event Action<string>? Failed;

    /// <summary>Begin capturing. Throws if the stream cannot be opened at all.</summary>
    void Start();

    /// <summary>Stop capturing, flush the WAV, and return the captured duration.</summary>
    TimeSpan Stop();
}
