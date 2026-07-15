using BgRecorder.Ui;
using Xunit;

namespace BgRecorder.Ui.Tests;

public sealed class BoundedReadStreamTests
{
    [Fact]
    public async Task Reads_only_the_requested_window_and_then_returns_eof()
    {
        var inner = new MemoryStream(Enumerable.Range(0, 10).Select(i => (byte)i).ToArray());
        await using var stream = new BoundedReadStream(inner, offset: 3, length: 4);
        var buffer = new byte[10];

        var read = await stream.ReadAsync(buffer);

        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, buffer[..read]);
        Assert.Equal(0, await stream.ReadAsync(buffer));
    }

    [Fact]
    public void Seek_is_relative_to_the_bounded_window()
    {
        using var stream = new BoundedReadStream(
            new MemoryStream(Enumerable.Range(0, 12).Select(i => (byte)i).ToArray()),
            offset: 4,
            length: 5);

        Assert.Equal(3, stream.Seek(-2, SeekOrigin.End));
        Assert.Equal(7, stream.ReadByte());
        Assert.Equal(4, stream.Position);
        Assert.Throws<IOException>(() => stream.Seek(1, SeekOrigin.End));
    }

    [Fact]
    public void Dispose_honors_leave_open()
    {
        var inner = new MemoryStream(new byte[4]);
        var stream = new BoundedReadStream(inner, 0, 4, leaveOpen: true);

        stream.Dispose();

        Assert.True(inner.CanRead);
        inner.Dispose();
    }

    [Fact]
    public void Eof_releases_the_owned_inner_stream()
    {
        var inner = new TrackingStream(new byte[] { 1, 2, 3 });
        var stream = new BoundedReadStream(inner, 0, 3);
        var buffer = new byte[3];

        Assert.Equal(3, stream.Read(buffer));
        Assert.False(inner.WasDisposed);
        Assert.Equal(0, stream.Read(buffer));
        Assert.True(inner.WasDisposed);
    }

    [Fact]
    public void Read_failure_releases_the_owned_inner_stream()
    {
        var inner = new TrackingStream(new byte[] { 1 }) { ThrowOnRead = true };
        var stream = new BoundedReadStream(inner, 0, 1);

        Assert.Throws<IOException>(() => stream.Read(new byte[1], 0, 1));
        Assert.True(inner.WasDisposed);
    }

    [Fact]
    public void Zero_length_read_before_eof_does_not_release_the_inner_stream()
    {
        var inner = new TrackingStream(new byte[] { 1 });
        using var stream = new BoundedReadStream(inner, 0, 1);

        Assert.Equal(0, stream.Read(Array.Empty<byte>()));
        Assert.False(inner.WasDisposed);
        Assert.Equal(1, stream.ReadByte());
    }

    [Fact]
    public void Window_offsets_remain_64_bit_beyond_four_gibibytes()
    {
        const long fiveGiB = 5L * 1024 * 1024 * 1024;
        const long offset = fiveGiB - 4096;
        using var inner = new VirtualLargeStream(fiveGiB);
        using var stream = new BoundedReadStream(inner, offset, length: 4096, leaveOpen: true);
        var buffer = new byte[32];

        Assert.Equal(32, stream.Read(buffer));
        Assert.Equal(offset + 32, inner.Position);
        Assert.Equal(4096, stream.Seek(0, SeekOrigin.End));
        Assert.Equal(fiveGiB, inner.Position);
    }

    /// <summary>Seekable multi-GB test stream that allocates no backing buffer.</summary>
    private sealed class VirtualLargeStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            var read = (int)Math.Min(count, Length - Position);
            Array.Clear(buffer, offset, read);
            Position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0 || target > Length)
            {
                throw new IOException();
            }

            return Position = target;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackingStream(byte[] buffer) : MemoryStream(buffer)
    {
        public bool ThrowOnRead { get; init; }
        public bool WasDisposed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (ThrowOnRead)
            {
                throw new IOException("synthetic read failure");
            }
            return base.Read(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
