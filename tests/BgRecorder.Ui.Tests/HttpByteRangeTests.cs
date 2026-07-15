using BgRecorder.Ui;
using Xunit;

namespace BgRecorder.Ui.Tests;

public sealed class HttpByteRangeTests
{
    [Fact]
    public void Missing_range_returns_the_full_resource()
    {
        var result = HttpByteRange.Resolve(null, 1_000);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(0, result.Offset);
        Assert.Equal(1_000, result.Length);
        Assert.Null(result.ContentRange);
    }

    [Theory]
    [InlineData("bytes=0-99", 0, 100, "bytes 0-99/1000")]
    [InlineData("bytes=900-", 900, 100, "bytes 900-999/1000")]
    [InlineData("bytes=-125", 875, 125, "bytes 875-999/1000")]
    [InlineData("bytes=950-5000", 950, 50, "bytes 950-999/1000")]
    [InlineData("BYTES=10-19", 10, 10, "bytes 10-19/1000")]
    public void Valid_ranges_return_partial_content(
        string header,
        long expectedOffset,
        long expectedLength,
        string expectedContentRange)
    {
        var result = HttpByteRange.Resolve(header, 1_000);

        Assert.Equal(206, result.StatusCode);
        Assert.Equal(expectedOffset, result.Offset);
        Assert.Equal(expectedLength, result.Length);
        Assert.Equal(expectedContentRange, result.ContentRange);
    }

    [Theory]
    [InlineData("items=0-5")]
    [InlineData("bytes=")]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=20-10")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("bytes=abc-def")]
    public void Invalid_or_unavailable_ranges_return_416(string header)
    {
        var result = HttpByteRange.Resolve(header, 1_000);

        Assert.Equal(416, result.StatusCode);
        Assert.False(result.IsSatisfiable);
        Assert.Equal("bytes */1000", result.ContentRange);
    }

    [Fact]
    public void Range_math_stays_64_bit_for_multi_gigabyte_videos()
    {
        const long fiveGiB = 5L * 1024 * 1024 * 1024;
        const long finalChunk = 4L * 1024 * 1024;

        var result = HttpByteRange.Resolve($"bytes={fiveGiB - finalChunk}-", fiveGiB);

        Assert.Equal(206, result.StatusCode);
        Assert.Equal(fiveGiB - finalChunk, result.Offset);
        Assert.Equal(finalChunk, result.Length);
        Assert.Equal($"bytes {fiveGiB - finalChunk}-{fiveGiB - 1}/{fiveGiB}", result.ContentRange);
    }

    [Fact]
    public void Any_range_against_an_empty_resource_is_unsatisfiable()
        => Assert.Equal(416, HttpByteRange.Resolve("bytes=0-", 0).StatusCode);
}
