using System.Text.Json;
using BgRecorder.Core;

namespace BgRecorder.Data;

/// <summary>
/// File-backed <see cref="ISettingsService"/>: the single reader and writer of settings.json. Loads
/// once at construction — writing defaults only when the file is genuinely absent — then serves the
/// in-memory value and rewrites the file atomically and durably on update.
/// </summary>
/// <remarks>
/// The on-disk shape uses default (PascalCase) JSON naming — deliberately NOT the camelCase Web policy the
/// UI bridge uses on the wire — so the file stays human-editable and matches the settings written before a
/// settings service existed. Because it is hand-editable, a present-but-unparseable file is treated as
/// recoverable: it is preserved as a <c>.corrupt</c> sidecar rather than silently overwritten with
/// defaults, so a stray typo never destroys the user's configuration. Writes go through a temp file that
/// is flushed to disk and then atomically renamed, so neither an in-process crash nor a power/OS failure
/// can leave a truncated settings.json (the rename lands the new file whole or leaves the old one intact).
/// </remarks>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private enum LoadOutcome
    {
        /// <summary>No file on disk — first run. Safe to write defaults.</summary>
        Missing,

        /// <summary>Loaded a valid settings file.</summary>
        Loaded,

        /// <summary>The file exists but could not be read/parsed (corrupt or locked). Never overwrite it.</summary>
        Unreadable,
    }

    private readonly string _path;
    private readonly Action<string>? _diagnostic;
    private readonly object _gate = new();
    private AppSettings _current;

    /// <summary>
    /// Loads settings from <paramref name="path"/>, or writes and adopts defaults if unreadable.
    /// The load runs here, so <paramref name="onDiagnostic"/> is passed in (rather than an event
    /// subscribed after construction) to capture a corrupt-file or write warning from first load.
    /// </summary>
    public JsonSettingsService(string path, Action<string>? onDiagnostic = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _diagnostic = onDiagnostic;
        _current = Load(path, out var outcome);

        switch (outcome)
        {
            case LoadOutcome.Missing:
                // First run: persist the defaults so the file exists and is discoverable/editable,
                // mirroring the behaviour the composition root had before this service owned persistence.
                TryWrite(_current);
                break;

            case LoadOutcome.Unreadable:
                // The file EXISTS but could not be parsed (a hand-edit typo) or was locked. Never
                // silently overwrite the user's config with defaults — move the original aside as a
                // .corrupt sidecar they can recover from, and only then write a fresh defaults file so
                // the app keeps working. If the original can't be moved (e.g. locked), leave it in place
                // and run on in-memory defaults rather than destroying it.
                if (TryPreserveUnreadable(path))
                {
                    TryWrite(_current);
                }
                break;
        }
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event Action<AppSettings>? Changed;

    public Task<AppSettings> UpdateAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            Write(settings); // let a write failure surface; a settings.set that could not persist must fail
            _current = settings;
        }

        Changed?.Invoke(settings); // outside the lock: a slow handler must not block Current readers
        return Task.FromResult(settings);
    }

    private AppSettings Load(string path, out LoadOutcome outcome)
    {
        if (!File.Exists(path))
        {
            outcome = LoadOutcome.Missing;
            return new AppSettings();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
            if (loaded is not null)
            {
                outcome = LoadOutcome.Loaded;
                return loaded;
            }

            // The file is present but deserialized to null (empty, or the literal "null"). Treat it as
            // unreadable so the original is preserved rather than clobbered.
            outcome = LoadOutcome.Unreadable;
            _diagnostic?.Invoke($"Settings at {path} were empty; preserving the file and using defaults.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            outcome = LoadOutcome.Unreadable;
            _diagnostic?.Invoke($"Settings at {path} could not be read; preserving the file and using defaults: {ex.Message}");
        }

        return new AppSettings();
    }

    /// <summary>
    /// Moves an unreadable settings file aside to a <c>.corrupt</c> sidecar so a fresh defaults file can
    /// be written without destroying the original. Returns false when the original could not be moved
    /// (e.g. it is locked), signalling the caller to leave it untouched rather than overwrite it.
    /// </summary>
    private bool TryPreserveUnreadable(string path)
    {
        var backupPath = path + ".corrupt";
        try
        {
            File.Move(path, backupPath, overwrite: true);
            _diagnostic?.Invoke($"Unreadable settings backed up to {backupPath}; wrote fresh defaults.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostic?.Invoke($"Left unreadable settings at {path} in place (could not back it up): {ex.Message}");
            return false;
        }
    }

    private void Write(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a sibling temp file, flush it all the way to disk, then atomically rename. The flush
        // is what makes this durable: without it the rename can be journaled (NTFS metadata) while the
        // temp file's data pages are still cached, so a power/OS crash could surface a truncated file.
        // With the flush, a crash at any point leaves either the whole new file or the intact old one.
        var tempPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, _path, overwrite: true);
    }

    private void TryWrite(AppSettings settings)
    {
        try
        {
            Write(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostic?.Invoke($"Could not persist settings to {_path}: {ex.Message}");
        }
    }
}
