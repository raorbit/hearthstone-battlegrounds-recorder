using System.Globalization;

namespace BgRecorder.Ui;

/// <summary>
/// Resolves one HTTP byte range without allocating or narrowing file lengths to 32 bits. Multiple
/// ranges are deliberately rejected: HTML media seeking only needs a single range and multipart
/// responses would add complexity with no player benefit.
/// </summary>
public static class HttpByteRange
{
    public static HttpByteRangeResult Resolve(string? rangeHeader, long resourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resourceLength);

        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return HttpByteRangeResult.Full(resourceLength);
        }

        const string prefix = "bytes=";
        var header = rangeHeader.Trim();
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return HttpByteRangeResult.Unsatisfiable(resourceLength);
        }

        var spec = header[prefix.Length..].Trim();
        if (resourceLength == 0 || spec.Length == 0 || spec.Contains(','))
        {
            return HttpByteRangeResult.Unsatisfiable(resourceLength);
        }

        var dash = spec.IndexOf('-');
        if (dash < 0 || dash != spec.LastIndexOf('-'))
        {
            return HttpByteRangeResult.Unsatisfiable(resourceLength);
        }

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();

        if (startText.Length == 0)
        {
            // Suffix form: bytes=-N means the final N bytes.
            if (!TryParseNonNegative(endText, out var suffixLength) || suffixLength == 0)
            {
                return HttpByteRangeResult.Unsatisfiable(resourceLength);
            }

            var length = Math.Min(suffixLength, resourceLength);
            return HttpByteRangeResult.Partial(resourceLength - length, length, resourceLength);
        }

        if (!TryParseNonNegative(startText, out var start) || start >= resourceLength)
        {
            return HttpByteRangeResult.Unsatisfiable(resourceLength);
        }

        long end;
        if (endText.Length == 0)
        {
            end = resourceLength - 1;
        }
        else if (!TryParseNonNegative(endText, out end) || end < start)
        {
            return HttpByteRangeResult.Unsatisfiable(resourceLength);
        }
        else
        {
            end = Math.Min(end, resourceLength - 1);
        }

        return HttpByteRangeResult.Partial(start, checked(end - start + 1), resourceLength);
    }

    private static bool TryParseNonNegative(string value, out long parsed)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;
}

public sealed record HttpByteRangeResult(
    int StatusCode,
    long Offset,
    long Length,
    string? ContentRange)
{
    public bool IsSatisfiable => StatusCode is 200 or 206;

    public static HttpByteRangeResult Full(long resourceLength)
        => new(200, 0, resourceLength, null);

    public static HttpByteRangeResult Partial(long offset, long length, long resourceLength)
        => new(
            206,
            offset,
            length,
            $"bytes {offset.ToString(CultureInfo.InvariantCulture)}-" +
            $"{checked(offset + length - 1).ToString(CultureInfo.InvariantCulture)}/" +
            resourceLength.ToString(CultureInfo.InvariantCulture));

    public static HttpByteRangeResult Unsatisfiable(long resourceLength)
        => new(416, 0, 0, $"bytes */{resourceLength.ToString(CultureInfo.InvariantCulture)}");
}
