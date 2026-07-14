using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using BgRecorder.Core.Session;

namespace BgRecorder.App;

/// <summary>
/// Owns the tray icon: a distinct runtime-generated glyph per <see cref="CoordinatorState"/>, the
/// state tooltip, and the context menu. Raises intent events; the App wires them to the coordinator.
/// All members must be touched on the UI thread.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon = new();
    private readonly Dictionary<CoordinatorState, Icon> _glyphs = new();

    private MenuItem _statusItem = null!;
    private MenuItem _stopItem = null!;
    private MenuItem _pauseResumeItem = null!;

    public event Action? StopRecordingRequested;
    public event Action? PauseResumeRequested;
    public event Action? OpenLibraryRequested;
    public event Action? ExitRequested;

    public void Initialize()
    {
        BuildGlyphs();
        _icon.ContextMenu = BuildMenu();
        SetState(CoordinatorState.GameNotFound);
        // Materialize the icon without a hosting window (efficiency mode off so the tray behaves
        // normally during the short-lived smoke run).
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void SetState(CoordinatorState state)
    {
        if (_glyphs.TryGetValue(state, out var glyph))
        {
            _icon.Icon = glyph;
        }

        _icon.ToolTipText = ToolTipFor(state);
        _statusItem.Header = StatusLineFor(state);
        _stopItem.IsEnabled = state == CoordinatorState.Recording;
        _pauseResumeItem.Header = state == CoordinatorState.Paused ? "Resume now" : "Pause auto-recording";
    }

    public void ShowFatalBalloon(string title, string message)
    {
        try
        {
            _icon.ShowNotification(title, message, NotificationIcon.Error);
        }
        catch
        {
            // Balloons are best-effort; a headless/uninitialized tray must not throw here.
        }
    }

    private ContextMenu BuildMenu()
    {
        _statusItem = new MenuItem { Header = "Starting…", IsEnabled = false };

        _stopItem = new MenuItem { Header = "Stop this recording", IsEnabled = false };
        _stopItem.Click += (_, _) => StopRecordingRequested?.Invoke();

        _pauseResumeItem = new MenuItem { Header = "Pause auto-recording" };
        _pauseResumeItem.Click += (_, _) => PauseResumeRequested?.Invoke();

        var openItem = new MenuItem { Header = "Open library folder" };
        openItem.Click += (_, _) => OpenLibraryRequested?.Invoke();

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_stopItem);
        menu.Items.Add(_pauseResumeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        return menu;
    }

    private void BuildGlyphs()
    {
        _glyphs[CoordinatorState.GameNotFound] = CreateGlyph(Color.FromArgb(128, 128, 128), slashed: false);
        _glyphs[CoordinatorState.Armed] = CreateGlyph(Color.FromArgb(56, 158, 72), slashed: false);
        _glyphs[CoordinatorState.Recording] = CreateGlyph(Color.FromArgb(208, 48, 44), slashed: false);
        _glyphs[CoordinatorState.Finalizing] = CreateGlyph(Color.FromArgb(232, 158, 28), slashed: false);
        _glyphs[CoordinatorState.Paused] = CreateGlyph(Color.FromArgb(56, 158, 72), slashed: true);
    }

    private static string ToolTipFor(CoordinatorState state) => state switch
    {
        CoordinatorState.GameNotFound => "BG Recorder — Hearthstone not detected",
        CoordinatorState.Armed => "BG Recorder — waiting for a match",
        CoordinatorState.Recording => "BG Recorder — recording",
        CoordinatorState.Finalizing => "BG Recorder — finalizing",
        CoordinatorState.Paused => "BG Recorder — auto-recording paused",
        _ => "BG Recorder",
    };

    private static string StatusLineFor(CoordinatorState state) => state switch
    {
        CoordinatorState.GameNotFound => "Hearthstone not detected",
        CoordinatorState.Armed => "Armed — waiting for a match",
        CoordinatorState.Recording => "Recording this match",
        CoordinatorState.Finalizing => "Finalizing recording…",
        CoordinatorState.Paused => "Auto-recording paused",
        _ => "Starting…",
    };

    private static Icon CreateGlyph(Color fill, bool slashed)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var brush = new SolidBrush(fill))
            {
                g.FillEllipse(brush, 4, 4, 23, 23);
            }

            using (var border = new Pen(Color.FromArgb(180, 24, 24, 24), 1.5f))
            {
                g.DrawEllipse(border, 4, 4, 23, 23);
            }

            if (slashed)
            {
                using var shadow = new Pen(Color.FromArgb(210, 24, 24, 24), 5.5f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                g.DrawLine(shadow, 9, 23, 23, 9);

                using var slash = new Pen(Color.White, 3f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                g.DrawLine(slash, 9, 23, 23, 9);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            // GetHicon allocates an unmanaged HICON; the managed clone above owns its own copy.
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Dispose();
        foreach (var glyph in _glyphs.Values)
        {
            glyph.Dispose();
        }

        _glyphs.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
