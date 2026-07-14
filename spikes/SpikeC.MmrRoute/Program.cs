using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SpikeC.MmrRoute;

// Spike C probe — read-only-attach feasibility for the (gated) MMR route.
//
// SCOPE: this program only proves that a passive read-only handle to the live
// Hearthstone process can be opened and its module list enumerated — the exact
// footprint HDT/Firestone use. It performs NO memory reads, NO heap walking, NO
// injection, and contains ZERO HearthMirror code. It never opens a handle with
// write/inject rights. See README.md for how this feeds the licensing-gated
// RatingProvider (BaconRatingMgr.s_instance -> m_lastRatingResponse -> Rating).
internal static class Program
{
    private const string ProcessName = "Hearthstone";

    // Exit codes (consumed by humans and CI).
    private const int ExitPass = 0;
    private const int ExitProcessNotFound = 1;
    private const int ExitAccessDenied = 2;
    private const int ExitEnumFailed = 3;

    private static int Main()
    {
        Console.WriteLine("=== Spike C: read-only attach feasibility probe ===");
        Console.WriteLine($"Target process name : {ProcessName}");
        Console.WriteLine("Requested access    : PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ (read-only)");
        Console.WriteLine("Footprint           : passive handle only — no ReadProcessMemory, no injection, no writes");
        Console.WriteLine();

        // 1) Find the process by name.
        Process? game = FindGameProcess();
        if (game is null)
        {
            Console.WriteLine($"FAIL: no running process named '{ProcessName}' found.");
            Console.WriteLine("      Start Hearthstone and retry.");
            PrintVerdict(false);
            return ExitProcessNotFound;
        }

        Console.WriteLine($"Found process       : PID {game.Id}");
        try { Console.WriteLine($"Executable          : {game.MainModule?.FileName}"); }
        catch { /* MainModule can throw under partial trust; non-fatal for the probe. */ }
        Console.WriteLine();

        // 2) Open a READ-ONLY handle.
        const uint access = NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_VM_READ;
        SafeProcessHandle handle = NativeMethods.OpenProcess(access, false, (uint)game.Id);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            Console.WriteLine($"FAIL: OpenProcess denied (Win32 error {err} — {DescribeError(err)}).");
            if (err == NativeMethods.ERROR_ACCESS_DENIED)
            {
                Console.WriteLine("      Access denied. On this OS the game runs at the same integrity as a");
                Console.WriteLine("      normal user app, so a standard-user handle should succeed. If it does");
                Console.WriteLine("      not, an anti-cheat / protection layer is blocking read handles — that");
                Console.WriteLine("      would be a hard blocker for any memory-read MMR route.");
            }
            PrintVerdict(false);
            return ExitAccessDenied;
        }

        using (handle)
        {
            // 3) Confirm bitness with IsWow64Process2 (report both machines).
            (string bitnessLabel, bool is64Bit) = DescribeBitness(handle);
            Console.WriteLine($"Process bitness     : {bitnessLabel}");
            if (!is64Bit)
            {
                Console.WriteLine("      WARNING: target is not 64-bit — module enumeration below assumes a 64-bit");
                Console.WriteLine("      target and a 64-bit probe. Field offsets in the real reader would differ.");
            }
            Console.WriteLine();

            // 4) Enumerate all modules (LIST_MODULES_ALL).
            if (!TryEnumerateModules(handle, out IntPtr[] modules, out int enumError))
            {
                Console.WriteLine($"FAIL: EnumProcessModulesEx failed (Win32 error {enumError} — {DescribeError(enumError)}).");
                PrintVerdict(false);
                return ExitEnumFailed;
            }

            Console.WriteLine($"Module count        : {modules.Length}");

            // 5) Look for a Mono runtime module and (informationally) an IL2CPP module.
            ModuleRecord? mono = null;
            ModuleRecord? il2cpp = null;
            var allNames = new List<string>(modules.Length);

            foreach (IntPtr hModule in modules)
            {
                string name = GetModuleBaseName(handle, hModule);
                allNames.Add(name);

                if (mono is null && LooksLikeMono(name)
                    && TryGetModuleInfo(handle, hModule, out MODULEINFO miMono))
                {
                    mono = new ModuleRecord(name, GetModuleFileName(handle, hModule), miMono);
                }

                if (il2cpp is null && name.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)
                    && TryGetModuleInfo(handle, hModule, out MODULEINFO miIl2cpp))
                {
                    il2cpp = new ModuleRecord(name, GetModuleFileName(handle, hModule), miIl2cpp);
                }
            }

            Console.WriteLine();
            if (mono is not null)
            {
                Console.WriteLine("Mono runtime module : FOUND");
                Console.WriteLine($"  name              : {mono.Name}");
                Console.WriteLine($"  base address      : 0x{mono.Info.lpBaseOfDll.ToInt64():X}");
                Console.WriteLine($"  size              : {mono.Info.SizeOfImage:N0} bytes ({mono.Info.SizeOfImage / (1024.0 * 1024.0):F1} MiB)");
                Console.WriteLine($"  path              : {mono.FullPath}");
            }
            else
            {
                Console.WriteLine("Mono runtime module : NOT FOUND");
                if (il2cpp is not null)
                {
                    Console.WriteLine("  NOTE: found GameAssembly.dll (IL2CPP). If the game has moved to an IL2CPP");
                    Console.WriteLine("        backend, the Mono-heap field path no longer applies and the MMR reader");
                    Console.WriteLine("        route would need a full re-evaluation.");
                    Console.WriteLine($"        GameAssembly.dll base 0x{il2cpp.Info.lpBaseOfDll.ToInt64():X}, {il2cpp.Info.SizeOfImage:N0} bytes");
                }
                else
                {
                    Console.WriteLine("  NOTE: neither a mono-*.dll nor GameAssembly.dll was seen. The scripting");
                    Console.WriteLine("        backend could not be identified from module names alone.");
                }
            }

            // Show a few module names as evidence the enumeration really read the target.
            Console.WriteLine();
            Console.WriteLine("Sample modules (first 8):");
            foreach (string name in allNames.Take(8))
            {
                Console.WriteLine($"  - {name}");
            }

            // 6) Verdict. We successfully opened a read-only handle and enumerated
            //    modules, which is exactly the attach footprint the MMR reader needs.
            bool feasible = true;
            Console.WriteLine();
            PrintVerdict(feasible, monoFound: mono is not null, is64Bit: is64Bit);
            return ExitPass;
        }
    }

    private static Process? FindGameProcess()
    {
        // Re-resolve by name every run; the PID changes across game launches.
        Process[] matches = Process.GetProcessesByName(ProcessName);
        if (matches.Length == 0)
        {
            return null;
        }

        // If somehow more than one, prefer the one with a real main window / earliest start.
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

    private static (string label, bool is64Bit) DescribeBitness(SafeProcessHandle handle)
    {
        if (!NativeMethods.IsWow64Process2(handle, out ushort processMachine, out ushort nativeMachine))
        {
            int err = Marshal.GetLastWin32Error();
            return ($"UNKNOWN (IsWow64Process2 failed, Win32 error {err})", false);
        }

        string nativeLabel = DescribeMachine(nativeMachine);

        // processMachine == IMAGE_FILE_MACHINE_UNKNOWN means the process is NOT
        // running under WOW64, i.e. it is a native process for nativeMachine.
        if (processMachine == NativeMethods.IMAGE_FILE_MACHINE_UNKNOWN)
        {
            bool is64 = nativeMachine is NativeMethods.IMAGE_FILE_MACHINE_AMD64 or NativeMethods.IMAGE_FILE_MACHINE_ARM64;
            return ($"64-bit native (not under WOW64; OS/native arch = {nativeLabel})", is64);
        }

        // Otherwise the process runs under WOW64 emulating processMachine.
        string procLabel = DescribeMachine(processMachine);
        return ($"{procLabel} under WOW64 (native arch = {nativeLabel})",
                processMachine is NativeMethods.IMAGE_FILE_MACHINE_AMD64 or NativeMethods.IMAGE_FILE_MACHINE_ARM64);
    }

    private static string DescribeMachine(ushort machine) => machine switch
    {
        NativeMethods.IMAGE_FILE_MACHINE_UNKNOWN => "UNKNOWN",
        NativeMethods.IMAGE_FILE_MACHINE_I386 => "x86 (I386)",
        NativeMethods.IMAGE_FILE_MACHINE_AMD64 => "x64 (AMD64)",
        NativeMethods.IMAGE_FILE_MACHINE_ARM64 => "ARM64",
        _ => $"0x{machine:X4}",
    };

    private static bool TryEnumerateModules(SafeProcessHandle handle, out IntPtr[] modules, out int win32Error)
    {
        modules = Array.Empty<IntPtr>();
        win32Error = 0;

        // Start with a generous buffer and grow if the module set is larger (or
        // grows between calls). Avoids the ambiguous null-buffer sizing call.
        int capacity = 1024;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new IntPtr[capacity];
            uint bytes = (uint)(buffer.Length * IntPtr.Size);
            if (!NativeMethods.EnumProcessModulesEx(handle, buffer, bytes, out uint needed, NativeMethods.LIST_MODULES_ALL))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            int actual = (int)(needed / (uint)IntPtr.Size);
            if (actual <= buffer.Length)
            {
                modules = actual == buffer.Length ? buffer : buffer[..actual];
                return true;
            }

            capacity = actual; // Truncated; retry with the exact size reported.
        }

        modules = Array.Empty<IntPtr>();
        win32Error = NativeMethods.ERROR_INSUFFICIENT_BUFFER;
        return false;
    }

    private static bool LooksLikeMono(string moduleName) =>
        moduleName.StartsWith("mono", StringComparison.OrdinalIgnoreCase)
        && moduleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static string GetModuleBaseName(SafeProcessHandle handle, IntPtr hModule)
    {
        var sb = new StringBuilder(260);
        uint len = NativeMethods.GetModuleBaseName(handle, hModule, sb, (uint)sb.Capacity);
        return len == 0 ? "(unknown)" : sb.ToString();
    }

    private static string GetModuleFileName(SafeProcessHandle handle, IntPtr hModule)
    {
        var sb = new StringBuilder(1024);
        uint len = NativeMethods.GetModuleFileNameEx(handle, hModule, sb, (uint)sb.Capacity);
        return len == 0 ? "(unknown)" : sb.ToString();
    }

    private static bool TryGetModuleInfo(SafeProcessHandle handle, IntPtr hModule, out MODULEINFO info)
    {
        return NativeMethods.GetModuleInformation(handle, hModule, out info, (uint)Marshal.SizeOf<MODULEINFO>());
    }

    private static string DescribeError(int win32Error) => win32Error switch
    {
        NativeMethods.ERROR_ACCESS_DENIED => "ERROR_ACCESS_DENIED",
        NativeMethods.ERROR_INVALID_PARAMETER => "ERROR_INVALID_PARAMETER",
        NativeMethods.ERROR_INSUFFICIENT_BUFFER => "ERROR_INSUFFICIENT_BUFFER",
        NativeMethods.ERROR_PARTIAL_COPY => "ERROR_PARTIAL_COPY",
        0 => "no error",
        _ => "see Win32 error code",
    };

    private static void PrintVerdict(bool feasible, bool monoFound = false, bool is64Bit = false)
    {
        Console.WriteLine();
        Console.WriteLine("--------------------------------------------------");
        if (feasible)
        {
            Console.WriteLine("RESULT: PASS — read-only attach is feasible.");
            Console.WriteLine("  A passive PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ handle opened");
            Console.WriteLine("  and the module list was enumerated with no elevation and no injection.");
            Console.WriteLine($"  Mono runtime present: {(monoFound ? "yes" : "no")}; target 64-bit: {(is64Bit ? "yes" : "no")}.");
            Console.WriteLine("  This is the same passive footprint deck trackers use. Whether the MMR");
            Console.WriteLine("  READER is actually built remains GATED on the licensing verdict (see");
            Console.WriteLine("  LICENSING.md / HEARTHSIM-EMAIL-DRAFT.md).");
        }
        else
        {
            Console.WriteLine("RESULT: FAIL — read-only attach not feasible in this run (see message above).");
        }
        Console.WriteLine("--------------------------------------------------");
    }
}

internal sealed record ModuleRecord(string Name, string FullPath, MODULEINFO Info);

[StructLayout(LayoutKind.Sequential)]
internal struct MODULEINFO
{
    public IntPtr lpBaseOfDll;
    public uint SizeOfImage;
    public IntPtr EntryPoint;
}

internal static class NativeMethods
{
    // --- Access rights (read-only footprint only) ---
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_VM_READ = 0x0010;

    // --- EnumProcessModulesEx filter ---
    public const uint LIST_MODULES_ALL = 0x03;

    // --- IMAGE_FILE_MACHINE_* ---
    public const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0x0000;
    public const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;
    public const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
    public const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

    // --- Win32 errors we surface by name ---
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_PARTIAL_COPY = 299;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);

    // psapi.dll exports; CharSet.Unicode selects the ...W entry points.
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumProcessModulesEx(
        SafeProcessHandle hProcess,
        [Out] IntPtr[]? lphModule,
        uint cb,
        out uint lpcbNeeded,
        uint dwFilterFlag);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetModuleBaseName(SafeProcessHandle hProcess, IntPtr hModule, StringBuilder lpBaseName, uint nSize);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetModuleFileNameEx(SafeProcessHandle hProcess, IntPtr hModule, StringBuilder lpFilename, uint nSize);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetModuleInformation(SafeProcessHandle hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);
}
