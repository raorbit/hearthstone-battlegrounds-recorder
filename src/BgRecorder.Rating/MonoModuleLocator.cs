using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BgRecorder.Rating;

/// <summary>
/// Opens a read-only handle to the live Hearthstone process and locates the Mono runtime module —
/// the passive attach footprint the Spike C probe proved feasible (no elevation, no injection).
/// </summary>
internal static class MonoModuleLocator
{
    private const string ProcessName = "Hearthstone";
    private const string MonoModulePrefix = "mono";
    private const string Il2CppModule = "GameAssembly.dll";

    /// <summary>
    /// Attach read-only and resolve the Mono module's base/size. On failure the handle is disposed and
    /// <paramref name="fault"/> explains why. Never throws for the expected failure modes.
    /// </summary>
    public static bool TryAttach(out SafeProcessHandle? handle, out ulong moduleBase, out ulong moduleSize, out AttachFault fault)
    {
        handle = null;
        moduleBase = 0;
        moduleSize = 0;
        fault = AttachFault.None;

        Process? game = FindGameProcess();
        if (game is null)
        {
            fault = AttachFault.ProcessNotFound;
            return false;
        }

        int pid = game.Id;
        game.Dispose();

        const uint access = NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_VM_READ;
        SafeProcessHandle h = NativeMethods.OpenProcess(access, false, (uint)pid);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            fault = err == NativeMethods.ERROR_ACCESS_DENIED ? AttachFault.AccessDenied : AttachFault.ProcessNotFound;
            return false;
        }

        if (!IsNative64(h))
        {
            h.Dispose();
            fault = AttachFault.NotNative64;
            return false;
        }

        if (!TryEnumerateModules(h, out IntPtr[] modules))
        {
            h.Dispose();
            fault = AttachFault.MonoModuleMissing;
            return false;
        }

        bool sawIl2Cpp = false;
        foreach (IntPtr hModule in modules)
        {
            string name = GetModuleBaseName(h, hModule);
            if (LooksLikeMono(name) && TryGetModuleInfo(h, hModule, out MODULEINFO info))
            {
                handle = h;
                moduleBase = (ulong)info.lpBaseOfDll.ToInt64();
                moduleSize = info.SizeOfImage;
                return true;
            }

            if (name.Equals(Il2CppModule, StringComparison.OrdinalIgnoreCase))
            {
                sawIl2Cpp = true;
            }
        }

        h.Dispose();
        // Mono is gone but IL2CPP is present → the field path no longer applies; distinguish it so the
        // provider can report PatchBroken rather than a transient AttachFailed.
        fault = sawIl2Cpp ? AttachFault.Il2Cpp : AttachFault.MonoModuleMissing;
        return false;
    }

    private static Process? FindGameProcess()
    {
        Process[] matches = Process.GetProcessesByName(ProcessName);
        if (matches.Length == 0)
        {
            return null;
        }

        Process chosen = matches.OrderBy(p =>
        {
            try { return p.StartTime; }
            catch { return DateTime.MaxValue; }
        }).First();

        foreach (Process p in matches)
        {
            if (!ReferenceEquals(p, chosen))
            {
                p.Dispose();
            }
        }

        return chosen;
    }

    private static bool IsNative64(SafeProcessHandle handle)
    {
        if (!NativeMethods.IsWow64Process2(handle, out ushort processMachine, out ushort nativeMachine))
        {
            return false;
        }

        // processMachine == UNKNOWN means "not under WOW64" — a native process for nativeMachine.
        if (processMachine != NativeMethods.IMAGE_FILE_MACHINE_UNKNOWN)
        {
            return false;
        }

        return nativeMachine is NativeMethods.IMAGE_FILE_MACHINE_AMD64 or NativeMethods.IMAGE_FILE_MACHINE_ARM64;
    }

    private static bool TryEnumerateModules(SafeProcessHandle handle, out IntPtr[] modules)
    {
        modules = Array.Empty<IntPtr>();
        int capacity = 1024;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new IntPtr[capacity];
            uint bytes = (uint)(buffer.Length * IntPtr.Size);
            if (!NativeMethods.EnumProcessModulesEx(handle, buffer, bytes, out uint needed, NativeMethods.LIST_MODULES_ALL))
            {
                return false;
            }

            int actual = (int)(needed / (uint)IntPtr.Size);
            if (actual <= buffer.Length)
            {
                modules = actual == buffer.Length ? buffer : buffer[..actual];
                return true;
            }

            capacity = actual;
        }

        return false;
    }

    private static bool LooksLikeMono(string moduleName) =>
        moduleName.StartsWith(MonoModulePrefix, StringComparison.OrdinalIgnoreCase)
        && moduleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static string GetModuleBaseName(SafeProcessHandle handle, IntPtr hModule)
    {
        var sb = new StringBuilder(260);
        uint len = NativeMethods.GetModuleBaseName(handle, hModule, sb, (uint)sb.Capacity);
        return len == 0 ? string.Empty : sb.ToString();
    }

    private static bool TryGetModuleInfo(SafeProcessHandle handle, IntPtr hModule, out MODULEINFO info) =>
        NativeMethods.GetModuleInformation(handle, hModule, out info, (uint)Marshal.SizeOf<MODULEINFO>());
}
