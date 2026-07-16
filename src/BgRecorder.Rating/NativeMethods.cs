using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BgRecorder.Rating;

/// <summary>Read-only-footprint P/Invoke, lifted from the Spike C probe plus the ReadProcessMemory read path.</summary>
internal static class NativeMethods
{
    // Access rights — read-only footprint only (never write/inject).
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_VM_READ = 0x0010;

    public const uint LIST_MODULES_ALL = 0x03;

    public const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0x0000;
    public const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
    public const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_PARTIAL_COPY = 299;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool ReadProcessMemory(
        SafeProcessHandle hProcess, IntPtr baseAddress, byte* buffer, nuint size, out nuint numberOfBytesRead);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumProcessModulesEx(
        SafeProcessHandle hProcess, [Out] IntPtr[]? lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetModuleBaseName(SafeProcessHandle hProcess, IntPtr hModule, StringBuilder lpBaseName, uint nSize);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetModuleInformation(SafeProcessHandle hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MODULEINFO
{
    public IntPtr lpBaseOfDll;
    public uint SizeOfImage;
    public IntPtr EntryPoint;
}

/// <summary>Why an attach did not yield a readable Mono target. Maps to a <c>RatingHealth</c> in the provider.</summary>
internal enum AttachFault
{
    None = 0,
    ProcessNotFound,
    AccessDenied,
    NotNative64,
    MonoModuleMissing,
    Il2Cpp,
}
