using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BgRecorder.Core.Events;
using BgRecorder.Core.Session;

namespace BgRecorder.Session;

/// <summary>
/// Reads and writes the crash-recovery <see cref="StagingManifest"/> sidecar.
/// Writes are atomic (temp file + rename/replace) so a crash mid-write can never
/// leave a half-written manifest that shadows the previous good one.
/// </summary>
public static class ManifestStore
{
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string PathFor(string sessionDir) => Path.Combine(sessionDir, FileName);

    /// <summary>Atomically writes the manifest into the staging session folder.</summary>
    public static void Write(string sessionDir, StagingManifest manifest)
    {
        var path = PathFor(sessionDir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, Options));
        if (File.Exists(path))
        {
            File.Replace(tmp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    /// <summary>Reads the manifest from a staging session folder; null when missing or corrupt.</summary>
    public static StagingManifest? TryRead(string sessionDir)
    {
        try
        {
            var path = PathFor(sessionDir);
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<StagingManifest>(File.ReadAllText(path), Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Core's <see cref="GameEvent"/> hierarchy carries no serialization attributes (contracts stay
    /// dependency-free), so polymorphism is configured here via a contract resolver instead.
    /// </summary>
    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            if (typeInfo.Type != typeof(GameEvent))
            {
                return;
            }
            typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$event",
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                DerivedTypes =
                {
                    new JsonDerivedType(typeof(LogSessionChanged), nameof(LogSessionChanged)),
                    new JsonDerivedType(typeof(MatchStarted), nameof(MatchStarted)),
                    new JsonDerivedType(typeof(GameTypeResolved), nameof(GameTypeResolved)),
                    new JsonDerivedType(typeof(LocalHeroResolved), nameof(LocalHeroResolved)),
                    new JsonDerivedType(typeof(TurnStarted), nameof(TurnStarted)),
                    new JsonDerivedType(typeof(CombatStarted), nameof(CombatStarted)),
                    new JsonDerivedType(typeof(PlacementChanged), nameof(PlacementChanged)),
                    new JsonDerivedType(typeof(MatchEnded), nameof(MatchEnded)),
                },
            };
        });
        return new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            WriteIndented = true,
        };
    }
}
