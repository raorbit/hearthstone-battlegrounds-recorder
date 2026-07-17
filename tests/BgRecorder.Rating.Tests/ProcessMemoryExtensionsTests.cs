using Xunit;

namespace BgRecorder.Rating.Tests;

public sealed class ProcessMemoryExtensionsTests
{
    private static FakeProcessMemory WithRegion(ulong baseVa, byte[] data)
    {
        var mem = new FakeProcessMemory();
        mem.AddRegion(baseVa, data);
        return mem;
    }

    [Fact]
    public void Typed_reads_decode_little_endian_values()
    {
        var data = new byte[32];
        BitConverter.GetBytes(0x1122334455667788ul).CopyTo(data, 0);
        BitConverter.GetBytes(-12345).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)0xBEEF).CopyTo(data, 12);
        var mem = WithRegion(0x1000, data);

        Assert.True(mem.TryReadPointer(0x1000, out ulong p));
        Assert.Equal(0x1122334455667788ul, p);
        Assert.True(mem.TryReadInt32(0x1008, out int i));
        Assert.Equal(-12345, i);
        Assert.True(mem.TryReadUInt16(0x100C, out ushort u));
        Assert.Equal((ushort)0xBEEF, u);
    }

    [Fact]
    public void A_read_that_leaves_its_region_fails_totally()
    {
        var mem = WithRegion(0x1000, new byte[8]);

        Assert.False(mem.TryReadPointer(0x1004, out _)); // straddles the region end
        Assert.False(mem.TryReadPointer(0x2000, out _)); // unmapped gap
        Assert.False(mem.TryReadPointer(0, out _));       // null
    }

    [Fact]
    public void Ascii_reads_stop_at_the_terminator()
    {
        var data = new byte[16];
        "Rating\0"u8.CopyTo(data);
        var mem = WithRegion(0x1000, data);

        Assert.True(mem.TryReadAsciiString(0x1000, 256, out string s));
        Assert.Equal("Rating", s);
    }

    [Fact]
    public void Ascii_reads_reject_non_printable_bytes_as_a_sanity_gate()
    {
        var data = new byte[] { 0x41, 0x01, 0x42, 0x00 }; // 'A', control, 'B', NUL
        var mem = WithRegion(0x1000, data);

        Assert.False(mem.TryReadAsciiString(0x1000, 256, out _));
    }

    [Fact]
    public void Ascii_reads_reject_an_unterminated_name()
    {
        var data = new byte[] { 0x41, 0x42, 0x43 }; // "ABC" with no NUL, then region ends
        var mem = WithRegion(0x1000, data);

        Assert.False(mem.TryReadAsciiString(0x1000, 256, out _));
    }

    [Fact]
    public void Utf16_decodes_a_known_length_body()
    {
        var data = new byte[32];
        System.Text.Encoding.Unicode.GetBytes("Duos").CopyTo(data, 0);
        var mem = WithRegion(0x1000, data);

        Assert.True(mem.TryReadUtf16(0x1000, 4, out string s));
        Assert.Equal("Duos", s);
    }
}
