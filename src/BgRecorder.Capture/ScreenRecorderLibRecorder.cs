using BgRecorder.Capture.Internal;
using BgRecorder.Core.Capture;
using ScreenRecorderLib;

namespace BgRecorder.Capture;

/// <summary>
/// <see cref="IRecorder"/> over ScreenRecorderLib: Windows.Graphics.Capture of the game window,
/// hardware H.264 CBR into a fragmented MP4, audio disabled (a separate engine owns audio).
/// The window is bound to the game process (never a same-titled deck-tracker overlay) via
/// <see cref="WindowResolver"/>.
/// </summary>
public sealed class ScreenRecorderLibRecorder : IRecorder
{
    /// <summary>Thrown when no recordable window can be bound to the target process.</summary>
    public sealed class WindowNotFoundException(string message) : Exception(message);

    public Task<IRecordingSession> StartAsync(
        RecordingTarget target,
        VideoOptions options,
        string stagingMp4Path,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingMp4Path);
        ct.ThrowIfCancellationRequested();

        var candidates = NativeWindows.Enumerate();
        var mainHandle = NativeWindows.MainWindowHandleOf(target.ProcessId);

        var resolution = WindowResolver.ResolveWithReason(
            candidates, target.ProcessId, mainHandle, target.WindowTitleHint);
        var window = resolution.Candidate;
        if (window is null)
        {
            throw new WindowNotFoundException(
                resolution.Failure == WindowResolutionFailure.TargetMinimized
                    ? $"The Hearthstone window for process {target.ProcessId} is minimized. " +
                      "Restore it before the next match; minimized window capture would produce a black recording."
                    : $"No recordable window found for process {target.ProcessId} " +
                      $"(title hint \"{target.WindowTitleHint}\").");
        }

        // Partial files must never land outside staging: make sure the folder exists first.
        RecorderOptionsFactory.EnsureStagingDirectory(stagingMp4Path);

        var source = RecorderOptionsFactory.CreateSource(window.Handle);
        var recorderOptions = RecorderOptionsFactory.Build(source, options);
        var recorder = Recorder.CreateRecorder(recorderOptions);

        // Once CreateRecorder succeeds, the native recorder owns WGC/D3D/MediaFoundation COM
        // resources. If anything below throws (adapter/session construction or Start), we must
        // release it — otherwise it is orphaned until finalization, holding the capture device.
        // Disposing the recorder tears down those native resources regardless of how far we got.
        try
        {
            var adapter = new SrlRecorderAdapter(recorder, source);
            var session = new RecordingSessionImpl(adapter, Path.GetFullPath(stagingMp4Path));
            session.Start();

            return Task.FromResult<IRecordingSession>(session);
        }
        catch
        {
            recorder.Dispose();
            throw;
        }
    }
}
