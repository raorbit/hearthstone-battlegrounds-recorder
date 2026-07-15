namespace BgRecorder.Core;

/// <summary>
/// Owns the persisted <see cref="AppSettings"/>: exposes the current value and durably applies updates.
/// The concrete implementation is the single writer of settings.json, so the whole app reads and mutates
/// settings through this seam rather than re-serializing the file in more than one place.
/// </summary>
public interface ISettingsService
{
    /// <summary>The settings in force right now. Replaced atomically by <see cref="UpdateAsync"/>.</summary>
    AppSettings Current { get; }

    /// <summary>
    /// Persists <paramref name="settings"/> to disk and adopts it as <see cref="Current"/>. Returns the
    /// stored value. Note: subsystems constructed at startup captured the previous instance by reference,
    /// so recording/storage changes take effect on the next launch — callers must not imply otherwise.
    /// </summary>
    Task<AppSettings> UpdateAsync(AppSettings settings, CancellationToken ct = default);
}
