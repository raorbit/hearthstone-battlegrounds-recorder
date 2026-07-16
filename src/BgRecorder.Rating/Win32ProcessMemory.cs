using Microsoft.Win32.SafeHandles;

namespace BgRecorder.Rating;

/// <summary>
/// <see cref="IProcessMemory"/> over ReadProcessMemory against a read-only process handle. A read counts as
/// success only if the full buffer was copied; any partial/failed read (unmapped page, process gone) is
/// reported as false, never thrown.
/// </summary>
internal sealed class Win32ProcessMemory : IProcessMemory, IDisposable
{
    private readonly SafeProcessHandle _handle;

    public Win32ProcessMemory(SafeProcessHandle handle, ulong moduleBase, ulong moduleSize)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        ModuleBase = moduleBase;
        ModuleSize = moduleSize;
    }

    public ulong ModuleBase { get; }

    public ulong ModuleSize { get; }

    public unsafe bool TryReadBytes(ulong address, Span<byte> buffer)
    {
        if (address == 0 || buffer.IsEmpty)
        {
            return false;
        }

        if (_handle.IsClosed || _handle.IsInvalid)
        {
            return false;
        }

        try
        {
            fixed (byte* p = buffer)
            {
                bool ok = NativeMethods.ReadProcessMemory(
                    _handle, (IntPtr)address, p, (nuint)buffer.Length, out nuint read);
                return ok && read == (nuint)buffer.Length;
            }
        }
        catch
        {
            // RPM should never throw, but the reader must survive even a marshalling surprise.
            return false;
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Attaches to the game and produces an <see cref="IProcessMemory"/>; a fake stands in for tests.</summary>
internal interface IProcessMemoryFactory
{
    bool TryAttach(out IProcessMemory memory, out AttachFault fault);
}

internal sealed class Win32ProcessMemoryFactory : IProcessMemoryFactory
{
    public bool TryAttach(out IProcessMemory memory, out AttachFault fault)
    {
        memory = null!;
        if (!MonoModuleLocator.TryAttach(out SafeProcessHandle? handle, out ulong moduleBase, out ulong moduleSize, out fault))
        {
            return false;
        }

        memory = new Win32ProcessMemory(handle!, moduleBase, moduleSize);
        return true;
    }
}
