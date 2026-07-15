using System.Text.Json;
using BgRecorder.Core;

namespace BgRecorder.Data;

/// <summary>
/// File-backed <see cref="ISettingsService"/>: the single reader and writer of settings.json. Loads
/// once at construction (falling back to defaults on a missing or corrupt file, and writing the defaults
/// so the file exists), then serves the in-memory value and rewrites the file atomically on update.
/// </summary>
/// <remarks>
/// The on-disk shape uses default (PascalCase) JSON naming — deliberately NOT the camelCase Web policy the
/// UI bridge uses on the wire — so the file stays human-editable and matches the settings written before a
/// settings service existed. Writes go through a temp file + atomic replace so a crash mid-write can never
/// leave a truncated settings.json.
/// </remarks>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
        _current = Load(path, out var loadedFromDisk);
        if (!loadedFromDisk)
        {
            // Persist the defaults so the file exists and is discoverable/editable, mirroring the
            // first-run behaviour the composition root had before this service owned persistence.
            TryWrite(_current);
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

    public Task<AppSettings> UpdateAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            Write(settings); // let a write failure surface; a settings.set that could not persist must fail
            _current = settings;
            return Task.FromResult(settings);
        }
    }

    private AppSettings Load(string path, out bool loadedFromDisk)
    {
        loadedFromDisk = false;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    loadedFromDisk = true;
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _diagnostic?.Invoke($"Settings load failed at {path}; using defaults: {ex.Message}");
        }

        return new AppSettings();
    }

    private void Write(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a sibling temp file, then atomically replace, so a crash mid-write never truncates
        // the live settings.json.
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
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
