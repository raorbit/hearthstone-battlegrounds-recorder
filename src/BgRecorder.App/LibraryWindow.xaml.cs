using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using BgRecorder.Core.Session;
using BgRecorder.Ui;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace BgRecorder.App;

/// <summary>
/// Singleton WPF host for the bundled library SPA. Static assets are mapped read-only, while videos
/// use a native opaque-id route with explicit HTTP range responses for multi-gigabyte seeking.
/// </summary>
public partial class LibraryWindow : Window
{
    private const string AppHost = "app.bgrecorder.local";
    private const string MediaOrigin = "https://media.bgrecorder.local";

    private readonly UiBridge _bridge;
    private readonly ISessionCoordinator _coordinator;
    private readonly string _assetsDirectory;
    private readonly CancellationTokenSource _closed = new();

    // WebView2 does not dispose a response stream that it abandons during a seek. The pool parks
    // idle file handles and reopens them at the logical response position if WebView2 later resumes,
    // bounding handles without invalidating a buffered response.
    private readonly MediaStreamLeasePool _mediaStreams = new();
    private bool _initialized;

    public LibraryWindow(UiBridge bridge, ISessionCoordinator coordinator, string assetsDirectory)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _assetsDirectory = Path.GetFullPath(assetsDirectory);

        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        _coordinator.StateChanged += OnCoordinatorStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            var indexPath = Path.Combine(_assetsDirectory, "index.html");
            if (!File.Exists(indexPath))
            {
                throw new FileNotFoundException(
                    "Bundled web assets are missing. Rebuild the Web project and the desktop app.",
                    indexPath);
            }

            var userDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BgRecorder",
                "WebView2");
            Directory.CreateDirectory(userDataDirectory);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataDirectory);
            await WebView.EnsureCoreWebView2Async(environment);

            var core = WebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.SetVirtualHostNameToFolderMapping(
                AppHost,
                _assetsDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);

            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.AddWebResourceRequestedFilter(
                $"{MediaOrigin}/*",
                CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.Navigate($"https://{AppHost}/index.html");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not initialize the library WebView");
            ShowError(ex is FileNotFoundException
                ? ex.Message
                : "WebView2 could not start. Install or repair the Microsoft Edge WebView2 Runtime, then reopen the library.");
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!IsTrustedAppUri(e.Source))
            {
                Log.Warning("Ignored a WebView message from untrusted origin {Origin}", e.Source);
                return;
            }

            var response = await _bridge.HandleRequestAsync(e.WebMessageAsJson, _closed.Token);
            if (!_closed.IsCancellationRequested && WebView.CoreWebView2 is { } core)
            {
                core.PostWebMessageAsJson(response);
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            // The window closed while a SQLite read was in flight.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not process a library WebView message");
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var core = WebView.CoreWebView2;
        if (core is null)
        {
            return;
        }

        Stream? responseStreamOwner = null;
        try
        {
            if (!TryParseMediaRoute(e.Request.Uri, out var kind, out var matchId))
            {
                e.Response = EmptyResponse(core, 404, "Not Found");
                return;
            }

            var method = e.Request.Method;
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                e.Response = EmptyResponse(core, 405, "Method Not Allowed", "Allow: GET, HEAD\r\n");
                return;
            }

            if (kind == MediaKind.Thumbnail)
            {
                ServeThumbnail(core, e, matchId, method);
                return;
            }

            if (!_bridge.TryResolveVideoPath(matchId, out var path))
            {
                e.Response = EmptyResponse(core, 404, "Not Found");
                return;
            }

            var file = OpenMediaFile(path);
            responseStreamOwner = file;
            var range = HttpByteRange.Resolve(TryGetHeader(e.Request.Headers, "Range"), file.Length);
            if (!range.IsSatisfiable)
            {
                e.Response = EmptyResponse(
                    core,
                    range.StatusCode,
                    "Range Not Satisfiable",
                    $"Accept-Ranges: bytes\r\nContent-Range: {range.ContentRange}\r\n");
                return;
            }

            Stream content;
            if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) || range.Length == 0)
            {
                // With Content-Length: 0 WebView2 is not required to call Read, so creating a media
                // lease is unnecessary. Keep ownership here and let the finally block close the
                // file after the response object has been created.
                content = Stream.Null;
            }
            else
            {
                content = _mediaStreams.Lease(
                    file,
                    () => OpenMediaFile(path),
                    range.Offset,
                    range.Length);
                responseStreamOwner = content;
            }

            var headers = new StringBuilder()
                .Append("Content-Type: video/mp4\r\n")
                .Append("Accept-Ranges: bytes\r\n")
                .Append("Cache-Control: no-store\r\n")
                .Append("Access-Control-Allow-Origin: https://app.bgrecorder.local\r\n")
                .Append("Content-Length: ")
                .Append(range.Length.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n");
            if (range.ContentRange is not null)
            {
                headers.Append("Content-Range: ").Append(range.ContentRange).Append("\r\n");
            }

            e.Response = core.Environment.CreateWebResourceResponse(
                content,
                range.StatusCode,
                range.StatusCode == 206 ? "Partial Content" : "OK",
                headers.ToString());

            if (!ReferenceEquals(content, Stream.Null))
            {
                // WebView2 owns the response wrapper while it reads. Its underlying file handle is
                // independently parked by MediaStreamLeasePool when the response becomes idle.
                responseStreamOwner = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not serve a library video range");
            e.Response = EmptyResponse(core, 404, "Not Found");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unexpected media response failure");
            e.Response = EmptyResponse(core, 500, "Internal Server Error");
        }
        finally
        {
            // Covers HEAD/416 responses and failures before ownership reaches WebView2.
            responseStreamOwner?.Dispose();
        }
    }

    /// <summary>
    /// Serves a thumbnail as a whole small image (no HTTP range needed). The file is read fully into
    /// memory so no file handle outlives the response — the media-stream lease pool exists only for the
    /// large, seekable video route.
    /// </summary>
    private void ServeThumbnail(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, long matchId, string method)
    {
        if (!_bridge.TryResolveThumbnailPath(matchId, out var path))
        {
            e.Response = EmptyResponse(core, 404, "Not Found");
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var isHead = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
            Stream content = isHead ? Stream.Null : new MemoryStream(bytes, writable: false);

            var headers = new StringBuilder()
                .Append("Content-Type: image/bmp\r\n")
                .Append("Cache-Control: no-store\r\n")
                .Append("Access-Control-Allow-Origin: https://app.bgrecorder.local\r\n")
                .Append("Content-Length: ")
                .Append(bytes.Length.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n")
                .ToString();

            e.Response = core.Environment.CreateWebResourceResponse(content, 200, "OK", headers);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not serve a library thumbnail");
            e.Response = EmptyResponse(core, 404, "Not Found");
        }
    }

    private void OnCoordinatorStateChanged(CoordinatorState state)
    {
        if (_closed.IsCancellationRequested)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            try
            {
                WebView.CoreWebView2?.PostWebMessageAsJson(UiBridge.CreateStateNotification(state));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not publish recorder state to the library window");
            }
        });
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The JSON-RPC bridge is privileged. Keep the top-level document pinned to the bundled app
        // origin so a navigated remote page can never inherit access to native commands.
        if (!IsTrustedAppUri(e.Uri))
        {
            e.Cancel = true;
        }
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        => e.Handled = true;

    private void OnClosed(object? sender, EventArgs e)
    {
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _closed.Cancel();

        if (WebView.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.WebMessageReceived -= OnWebMessageReceived;
            core.WebResourceRequested -= OnWebResourceRequested;
        }

        _mediaStreams.Dispose();
        WebView.Dispose();
        _closed.Dispose();
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private enum MediaKind
    {
        Video,
        Thumbnail,
    }

    private static bool TryParseMediaRoute(string requestUri, out MediaKind kind, out long matchId)
    {
        kind = MediaKind.Video;
        matchId = 0;
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "media.bgrecorder.local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 ||
            !long.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out matchId) ||
            matchId <= 0)
        {
            return false;
        }

        if (string.Equals(segments[0], "matches", StringComparison.Ordinal))
        {
            kind = MediaKind.Video;
            return true;
        }

        if (string.Equals(segments[0], "thumbnails", StringComparison.Ordinal))
        {
            kind = MediaKind.Thumbnail;
            return true;
        }

        return false;
    }

    private static bool IsTrustedAppUri(string candidate)
        => Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
           string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(uri.Host, AppHost, StringComparison.OrdinalIgnoreCase);

    private static string? TryGetHeader(CoreWebView2HttpRequestHeaders headers, string name)
    {
        try
        {
            if (!headers.Contains(name))
            {
                return null;
            }

            var value = headers.GetHeader(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
    }

    private static FileStream OpenMediaFile(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static CoreWebView2WebResourceResponse EmptyResponse(
        CoreWebView2 core,
        int statusCode,
        string reason,
        string extraHeaders = "")
        => core.Environment.CreateWebResourceResponse(
            Stream.Null,
            statusCode,
            reason,
            $"Content-Length: 0\r\nCache-Control: no-store\r\n{extraHeaders}");
}
