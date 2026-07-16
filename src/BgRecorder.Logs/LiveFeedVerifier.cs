using BgRecorder.Core.Events;

namespace BgRecorder.Logs;

/// <summary>Outcome of one <see cref="LiveFeedVerifier.RunAsync"/> pass.</summary>
public sealed record LiveFeedVerdict(bool Passed, string Detail);

/// <summary>
/// Onboarding's live test-feed self-test: proves the whole log-watching pipeline — session-folder
/// discovery, the FileShare.ReadWrite tail, and the Power.log parser — actually works on this machine,
/// without needing the game. It stands up a scratch install directory, feeds it a synthetic
/// <c>CREATE_GAME</c> line through <see cref="TestFeedWriter"/> (the same writer the E2E tests use), and
/// watches a real <see cref="GameEventSource"/> until it emits the parsed <see cref="MatchStarted"/>.
///
/// A pass means a silent no-recording failure later can only come from the game side (log.config not in
/// effect, or a log-format change) — not from this machine's IO behaviour. The two failure details name
/// which half broke: discovery/tail (folder watching, share modes) vs parse (event production).
/// </summary>
public static class LiveFeedVerifier
{
    /// <param name="scratchDir">
    /// A directory the verifier may create, fill, and delete — callers pass a unique temp path. Best-effort
    /// removed before returning.
    /// </param>
    /// <param name="timeout">Upper bound applied to each stage (discovery, then parse) separately.</param>
    public static async Task<LiveFeedVerdict> RunAsync(string scratchDir, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(scratchDir);

            var seed = DateTimeOffset.Now;
            var writer = new TestFeedWriter(scratchDir, seed);

            var discovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var parsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using (var source = new GameEventSource(
                scratchDir,
                pollInterval: TimeSpan.FromMilliseconds(25),
                rediscoverInterval: TimeSpan.FromMilliseconds(200)))
            {
                source.EventReceived += e =>
                {
                    if (e is LogSessionChanged) discovered.TrySetResult();
                    if (e is MatchStarted) parsed.TrySetResult();
                };

                await source.StartAsync(ct);

                try
                {
                    await discovered.Task.WaitAsync(timeout, ct);
                }
                catch (TimeoutException)
                {
                    return new LiveFeedVerdict(false,
                        $"discovery/tail failed: the watcher did not open the test session folder within {timeout.TotalSeconds:F0}s " +
                        "(folder enumeration or shared-read open is not working here)");
                }

                writer.AppendLine("D " + seed.ToString("HH:mm:ss.fffffff") + " GameState.DebugPrintPower() - CREATE_GAME");

                try
                {
                    await parsed.Task.WaitAsync(timeout, ct);
                }
                catch (TimeoutException)
                {
                    return new LiveFeedVerdict(false,
                        $"parse failed: the test session was opened but the synthetic feed produced no event within {timeout.TotalSeconds:F0}s " +
                        "(the live tail is not delivering appended lines to the parser)");
                }
            }

            return new LiveFeedVerdict(true, "discovery, tail, and parse all verified against a synthetic feed");
        }
        catch (OperationCanceledException)
        {
            throw; // shutdown, not a verdict — the caller decides what a cancelled run means
        }
        catch (Exception ex)
        {
            return new LiveFeedVerdict(false, "self-test crashed: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(scratchDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
