using BgRecorder.Core.Audio;

namespace BgRecorder.Session;

/// <summary>
/// Muxing with a video-only safety net. A hard crash can leave the staged audio WAV in a
/// state the muxer rejects forever (0-byte, truncated below its RIFF header, or garbage —
/// NAudio's writer had no chance to flush), and a mux that keeps failing on that poison
/// audio would strand the perfectly good video in staging on every attempt: the live
/// finalize and then every startup-recovery pass. The video must always win.
/// </summary>
internal static class MuxFallback
{
    /// <summary>
    /// Runs <see cref="IMuxer.MuxAsync"/> with the given inputs. When audio was supplied and
    /// the mux fails for any reason other than cancellation, deletes the partial output,
    /// invokes <paramref name="onFallback"/> with the original failure message, and retries
    /// once video-only. Throws only when the video-only attempt (or a mux that never had
    /// audio) itself fails.
    /// </summary>
    public static async Task MuxWithVideoOnlyRetryAsync(
        IMuxer muxer,
        string videoMp4,
        string audioWav,
        TimeSpan audioOffset,
        string outputMp4,
        Action<string> onFallback,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(audioWav))
        {
            await muxer.MuxAsync(videoMp4, string.Empty, TimeSpan.Zero, outputMp4, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await muxer.MuxAsync(videoMp4, audioWav, audioOffset, outputMp4, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onFallback(ex.Message);
            TryDeleteFile(outputMp4); // the failed attempt may have left a partial output
            await muxer.MuxAsync(videoMp4, string.Empty, TimeSpan.Zero, outputMp4, ct).ConfigureAwait(false);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort; the retry overwrite will surface any real problem
        }
    }
}
