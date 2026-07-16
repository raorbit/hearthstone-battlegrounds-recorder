using System.Buffers.Binary;
using System.Text;

namespace BgRecorder.Rating;

/// <summary>
/// Read-only view over another process's address space — the single native seam for the memory
/// rating reader. A byte-buffer fake stands in for the whole Mono heap in unit tests, so every
/// offset-walking decision is provable without the live game.
/// </summary>
/// <remarks>
/// Every read is TOTAL: an unmapped/partial/zero address yields <c>false</c>, never an exception.
/// Callers must check the bool before using the out value; no pointer is ever dereferenced unverified.
/// </remarks>
internal interface IProcessMemory
{
    /// <summary>Base VA of mono-2.0-bdwgc.dll in the target; 0 when not attached.</summary>
    ulong ModuleBase { get; }

    /// <summary>SizeOfImage of the Mono module, for pointer sanity-bounding.</summary>
    ulong ModuleSize { get; }

    /// <summary>
    /// Fill <paramref name="buffer"/> from <paramref name="address"/>. Returns false on any partial or
    /// failed read (unmapped page, ERROR_PARTIAL_COPY, address 0). Total — never throws.
    /// </summary>
    bool TryReadBytes(ulong address, Span<byte> buffer);
}

/// <summary>
/// Typed reads over <see cref="IProcessMemory"/>. Extension methods so the Win32 implementation and the
/// test fake share one decode path and the walker reads cleanly. All return bool (total) and out the value.
/// </summary>
internal static class ProcessMemoryExtensions
{
    /// <summary>Longest ASCII identifier (class/field/assembly name) we will read before giving up.</summary>
    internal const int MaxNameLength = 256;

    public static bool TryReadPointer(this IProcessMemory memory, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (!memory.TryReadBytes(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    public static bool TryReadInt32(this IProcessMemory memory, ulong address, out int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (!memory.TryReadBytes(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        return true;
    }

    public static bool TryReadUInt32(this IProcessMemory memory, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (!memory.TryReadBytes(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    public static bool TryReadUInt16(this IProcessMemory memory, ulong address, out ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        if (!memory.TryReadBytes(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        return true;
    }

    /// <summary>
    /// Read a NUL-terminated ASCII string. A byte outside printable ASCII (other than the terminator)
    /// fails the read — that doubles as a sanity gate so a wrong offset landing on binary data is
    /// rejected rather than decoded into garbage that could false-match a name. Reads one byte at a
    /// time, so a name near a page boundary is never over-read.
    /// </summary>
    public static bool TryReadAsciiString(this IProcessMemory memory, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return false;
        }

        var builder = new StringBuilder();
        Span<byte> one = stackalloc byte[1];
        for (int i = 0; i < maxLength; i++)
        {
            if (!memory.TryReadBytes(address + (ulong)i, one))
            {
                return false;
            }

            byte b = one[0];
            if (b == 0)
            {
                value = builder.ToString();
                return true;
            }

            if (b < 0x20 || b > 0x7E)
            {
                return false;
            }

            builder.Append((char)b);
        }

        // No terminator within maxLength — treat as not-a-name.
        return false;
    }

    /// <summary>
    /// Decode a MonoString body: <paramref name="charCount"/> UTF-16 code units at <paramref name="address"/>.
    /// The caller must have already validated the length. Not on the rating path (ratings are ints); kept
    /// for calibration/diagnostics and covered by tests.
    /// </summary>
    public static bool TryReadUtf16(this IProcessMemory memory, ulong address, int charCount, out string value)
    {
        value = string.Empty;
        if (charCount < 0)
        {
            return false;
        }

        if (charCount == 0)
        {
            return true;
        }

        byte[] bytes = new byte[charCount * 2];
        if (!memory.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = Encoding.Unicode.GetString(bytes);
        return true;
    }
}
