namespace BgRecorder.Rating.Tests;

/// <summary>
/// A byte-buffer <see cref="IProcessMemory"/>: a set of disjoint regions at chosen VAs. A read succeeds only
/// when the whole range sits inside one region, so gaps between regions model unmapped pages exactly the way a
/// real ReadProcessMemory partial-copy would fail. Counts reads so throttle behaviour is observable.
/// </summary>
internal sealed class FakeProcessMemory : IProcessMemory
{
    private readonly List<(ulong Base, byte[] Data)> _regions = new();

    public ulong ModuleBase { get; set; }

    public ulong ModuleSize { get; set; }

    public int ReadCount { get; private set; }

    public void AddRegion(ulong baseVa, byte[] data) => _regions.Add((baseVa, data));

    public bool TryReadBytes(ulong address, Span<byte> buffer)
    {
        ReadCount++;
        if (address == 0 || buffer.IsEmpty)
        {
            return false;
        }

        ulong end = address + (ulong)buffer.Length;
        if (end < address)
        {
            return false; // overflow
        }

        foreach ((ulong baseVa, byte[] data) in _regions)
        {
            if (address >= baseVa && end <= baseVa + (ulong)data.Length)
            {
                new ReadOnlySpan<byte>(data, (int)(address - baseVa), buffer.Length).CopyTo(buffer);
                return true;
            }
        }

        return false;
    }
}
