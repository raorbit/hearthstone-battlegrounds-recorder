using System.Buffers.Binary;

namespace BgRecorder.Rating;

/// <summary>
/// Pure navigation over <see cref="IProcessMemory"/>: root-domain discovery, class-by-name lookup, static
/// and instance field resolution, and MonoString decode. Every step is total and bounded — a wrong offset
/// or garbage pointer yields <c>false</c> (mapped to PatchBroken upstream), never a crash or an unbounded walk.
/// Fully unit-testable against a synthetic heap behind the memory seam.
/// </summary>
internal sealed class MonoImageReader
{
    private const ulong MinUserVa = 0x10000;
    private const ulong MaxUserVa = 0x7FFF_FFFF_FFFF;

    private readonly IProcessMemory _mem;
    private readonly MonoOffsets _off;

    private ulong _rootDomain;              // cached once resolved (stable for the process lifetime)
    private int _domainAssembliesOffset = -1; // cached calibration result

    public MonoImageReader(IProcessMemory memory, MonoOffsets offsets)
    {
        _mem = memory ?? throw new ArgumentNullException(nameof(memory));
        _off = offsets ?? throw new ArgumentNullException(nameof(offsets));
    }

    /// <summary>Raw reads for the field-path reader; keeps all pointer math on one seam.</summary>
    public IProcessMemory Memory => _mem;

    /// <summary>A struct pointer must be non-null, in canonical user space, and 8-aligned (Mono allocations are).</summary>
    private static bool Followable(ulong p) => p is >= MinUserVa and < MaxUserVa && (p & 7) == 0;

    /// <summary>A data pointer (char*/string) need not be aligned; the read itself validates the bytes.</summary>
    private static bool Plausible(ulong p) => p is >= MinUserVa and < MaxUserVa;

    // ---- Root domain (PE export walk + RIP-relative decode of mono_get_root_domain) ----

    /// <summary>
    /// Resolve the root <c>MonoDomain*</c> by reading the static <c>mono_root_domain</c> global that the
    /// exported <c>mono_get_root_domain</c> stub loads from — no in-process call, purely external.
    /// Retries across polls (the domain is null for a moment right after launch).
    /// </summary>
    public bool TryGetRootDomain(out ulong domain)
    {
        if (_rootDomain != 0)
        {
            domain = _rootDomain;
            return true;
        }

        domain = 0;
        if (!TryFindMonoExport("mono_get_root_domain", out ulong funcVa))
        {
            return false;
        }

        if (!TryDecodeRipTarget(funcVa, out ulong slot))
        {
            return false;
        }

        if (!_mem.TryReadPointer(slot, out ulong candidate) || !Followable(candidate))
        {
            return false;
        }

        _rootDomain = candidate;
        domain = candidate;
        return true;
    }

    private bool TryFindMonoExport(string exportName, out ulong funcVa)
    {
        funcVa = 0;
        ulong b = _mem.ModuleBase;
        if (b == 0)
        {
            return false;
        }

        if (!_mem.TryReadInt32(b + 0x3C, out int lfanew) || lfanew <= 0)
        {
            return false;
        }

        ulong nt = b + (uint)lfanew;
        if (!_mem.TryReadUInt32(nt, out uint sig) || sig != 0x0000_4550) // "PE\0\0"
        {
            return false;
        }

        if (!_mem.TryReadUInt16(nt + 0x18, out ushort magic) || magic != 0x20B) // PE32+ (x64)
        {
            return false;
        }

        ulong dataDir = nt + 0x18 + 0x70; // optional header + data-directory array; entry 0 = export table
        if (!_mem.TryReadUInt32(dataDir, out uint expRva) || expRva == 0)
        {
            return false;
        }

        if (!_mem.TryReadUInt32(dataDir + 4, out uint expSize))
        {
            return false;
        }

        ulong ed = b + expRva;
        if (!_mem.TryReadUInt32(ed + 0x18, out uint numNames) ||
            !_mem.TryReadUInt32(ed + 0x1C, out uint addrFuncs) ||
            !_mem.TryReadUInt32(ed + 0x20, out uint addrNames) ||
            !_mem.TryReadUInt32(ed + 0x24, out uint addrOrds))
        {
            return false;
        }

        uint cap = Math.Min(numNames, 65536u);
        for (uint i = 0; i < cap; i++)
        {
            if (!_mem.TryReadUInt32(b + addrNames + 4 * i, out uint nameRva))
            {
                return false;
            }

            if (!_mem.TryReadAsciiString(b + nameRva, 64, out string name) || name != exportName)
            {
                continue;
            }

            if (!_mem.TryReadUInt16(b + addrOrds + 2 * i, out ushort ordinal) ||
                !_mem.TryReadUInt32(b + addrFuncs + 4u * ordinal, out uint funcRva))
            {
                return false;
            }

            if (funcRva >= expRva && funcRva < expRva + expSize)
            {
                return false; // forwarded export, not code
            }

            funcVa = b + funcRva;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extract the RIP-relative target of the single <c>mov r64, [rip+disp32]</c> in the accessor stub,
    /// tolerating a leading endbr64 / frame setup by scanning for the instruction rather than assuming its offset.
    /// </summary>
    private bool TryDecodeRipTarget(ulong funcVa, out ulong slot)
    {
        slot = 0;
        Span<byte> stub = stackalloc byte[16];
        if (!_mem.TryReadBytes(funcVa, stub))
        {
            return false;
        }

        for (int i = 0; i + 7 <= stub.Length; i++)
        {
            // REX.W (0x48) or REX.WR (0x4C) + 8B (MOV r64, r/m64) + ModR/M with mod=00, rm=101 (RIP-relative).
            bool rex = stub[i] == 0x48 || stub[i] == 0x4C;
            if (rex && stub[i + 1] == 0x8B && (stub[i + 2] & 0xC7) == 0x05)
            {
                int disp = BinaryPrimitives.ReadInt32LittleEndian(stub.Slice(i + 3, 4));
                ulong instrEnd = funcVa + (ulong)i + 7; // 48 8B modrm + disp32
                slot = (ulong)((long)instrEnd + disp);
                return Plausible(slot);
            }
        }

        return false;
    }

    // ---- Assembly-list calibration (domain_assemblies offset varies with mutex size; scan for it) ----

    /// <summary>
    /// Discover <c>MonoDomain.domain_assemblies</c> by scanning the domain's pointer slots for the first that
    /// heads a clean GSList of assemblies whose images carry a readable name. This sidesteps the mutex-size
    /// fragility that makes the offset unknowable from a hardcoded table.
    /// </summary>
    public bool TryGetDomainAssemblies(ulong domain, out ulong listHead)
    {
        listHead = 0;
        if (_domainAssembliesOffset >= 0)
        {
            return _mem.TryReadPointer(domain + (ulong)_domainAssembliesOffset, out listHead);
        }

        for (int probe = _off.DomainScanStart; probe <= _off.DomainScanEnd; probe += 8)
        {
            if (!_mem.TryReadPointer(domain + (ulong)probe, out ulong head) || head == 0)
            {
                continue;
            }

            if (LooksLikeAssemblyList(head))
            {
                _domainAssembliesOffset = probe;
                listHead = head;
                return true;
            }
        }

        return false;
    }

    private bool LooksLikeAssemblyList(ulong head)
    {
        ulong node = head;
        int count = 0;
        bool sawNamedImage = false;

        while (node != 0 && count < _off.MaxAssemblies)
        {
            if (!Followable(node))
            {
                return false;
            }

            if (!_mem.TryReadPointer(node + (ulong)_off.GSListData, out ulong assembly) || !Followable(assembly))
            {
                return false;
            }

            // Confirm the list is really assemblies by resolving an image name for one of the first few nodes.
            if (count < 8 && !sawNamedImage &&
                TryGetImageFromAssembly(assembly, out ulong image) &&
                _mem.TryReadPointer(image + (ulong)_off.ImageAssemblyName, out ulong namePtr) && Plausible(namePtr) &&
                _mem.TryReadAsciiString(namePtr, ProcessMemoryExtensions.MaxNameLength, out string name) && name.Length > 0)
            {
                sawNamedImage = true;
            }

            if (!_mem.TryReadPointer(node + (ulong)_off.GSListNext, out node))
            {
                return false;
            }

            count++;
        }

        // A clean, null-terminated chain of assemblies with at least one resolvable image name.
        return node == 0 && count >= 1 && sawNamedImage;
    }

    private bool TryGetImageFromAssembly(ulong assembly, out ulong image)
    {
        image = 0;
        return _mem.TryReadPointer(assembly + (ulong)_off.AssemblyImage, out image) && Followable(image);
    }

    // ---- Class lookup ----

    /// <summary>Find a class by namespace + name across every image loaded in the domain.</summary>
    public bool TryFindClass(ulong domain, string ns, string name, out ulong klass)
    {
        klass = 0;
        if (!TryGetDomainAssemblies(domain, out ulong node))
        {
            return false;
        }

        // One shared read budget across all images: a wrong FRAGILE offset can make a bogus bucket count
        // (or unmapped table) look plausible, and empty buckets each still cost a read. Without a global cap
        // the scan could grind through millions of reads per poll under the provider lock. Exhaustion means
        // the offsets are wrong — degrade to PatchBroken fast instead of hanging.
        int budget = _off.MaxScanReads;
        int guard = 0;
        while (node != 0 && guard++ < _off.MaxAssemblies)
        {
            if (!Followable(node))
            {
                return false;
            }

            if (_mem.TryReadPointer(node + (ulong)_off.GSListData, out ulong assembly) && Followable(assembly) &&
                TryGetImageFromAssembly(assembly, out ulong image) &&
                TryFindClassInImage(image, ns, name, ref budget, out klass))
            {
                return true;
            }

            if (budget <= 0 || !_mem.TryReadPointer(node + (ulong)_off.GSListNext, out node))
            {
                return false;
            }
        }

        return false;
    }

    private bool TryFindClassInImage(ulong image, string ns, string name, ref int budget, out ulong klass)
    {
        klass = 0;
        ulong cache = image + (ulong)_off.ImageClassCache;
        if (!_mem.TryReadInt32(cache + (ulong)_off.HashSize, out int size) || size < 1 || size > _off.MaxHashBuckets)
        {
            return false;
        }

        if (!_mem.TryReadPointer(cache + (ulong)_off.HashTable, out ulong table) || !Followable(table))
        {
            return false;
        }

        for (int bucket = 0; bucket < size; bucket++)
        {
            if (--budget <= 0)
            {
                return false; // global budget spent — offsets are almost certainly wrong; fail fast
            }

            if (!_mem.TryReadPointer(table + (ulong)(bucket * 8), out ulong current))
            {
                continue;
            }

            int chain = 0;
            while (current != 0)
            {
                if (!Followable(current) || ++chain > _off.MaxChainNodes || --budget <= 0)
                {
                    break;
                }

                if (TryReadClassNames(current, out string cns, out string cname) && cname == name && cns == ns)
                {
                    klass = current;
                    return true;
                }

                if (!_mem.TryReadPointer(current + (ulong)_off.ClassDefNextCache, out current))
                {
                    break;
                }
            }
        }

        return false;
    }

    private bool TryReadClassNames(ulong klass, out string ns, out string name)
    {
        ns = string.Empty;
        name = string.Empty;

        if (!_mem.TryReadPointer(klass + (ulong)_off.ClassName, out ulong namePtr) || !Plausible(namePtr) ||
            !_mem.TryReadAsciiString(namePtr, ProcessMemoryExtensions.MaxNameLength, out name))
        {
            return false;
        }

        if (!_mem.TryReadPointer(klass + (ulong)_off.ClassNamespace, out ulong nsPtr) || !Plausible(nsPtr) ||
            !_mem.TryReadAsciiString(nsPtr, ProcessMemoryExtensions.MaxNameLength, out ns))
        {
            return false;
        }

        return true;
    }

    // ---- Static-field storage ----

    /// <summary>
    /// Resolve the class's static-data block in the root domain via <c>runtime_info → domain_vtables[id]</c>,
    /// selecting the right domain by confirming the vtable's <c>klass</c> back-pointer.
    /// </summary>
    public bool TryGetStaticData(ulong klass, out ulong staticData)
    {
        staticData = 0;
        if (!_mem.TryReadPointer(klass + (ulong)_off.ClassRuntimeInfo, out ulong rti) || !Followable(rti))
        {
            return false;
        }

        if (!_mem.TryReadUInt16(rti + (ulong)_off.RtiMaxDomain, out ushort maxDomain))
        {
            return false;
        }

        int cap = Math.Min(maxDomain, _off.MaxDomainScan);
        ulong vtable = 0;
        for (int id = 1; id <= cap; id++)
        {
            if (!_mem.TryReadPointer(rti + (ulong)_off.RtiDomainVtables + (ulong)(id * 8), out ulong candidate) ||
                !Followable(candidate))
            {
                continue;
            }

            if (_mem.TryReadPointer(candidate + (ulong)_off.VtableKlass, out ulong back) && back == klass)
            {
                vtable = candidate;
                break;
            }
        }

        if (vtable == 0)
        {
            return false;
        }

        if (_off.ClassicVTableData)
        {
            return _mem.TryReadPointer(vtable + (ulong)_off.VtableClassicData, out staticData) && Plausible(staticData);
        }

        if (!_mem.TryReadInt32(klass + (ulong)_off.ClassVtableSize, out int vtableSize) || vtableSize < 0 || vtableSize > 0xFFFF)
        {
            return false;
        }

        ulong slot = vtable + (ulong)_off.VtableArrayBase + (ulong)(vtableSize * 8);
        return _mem.TryReadPointer(slot, out staticData) && Plausible(staticData);
    }

    // ---- Field resolution ----

    /// <summary>Find a field's byte offset by name, walking the parent chain for inherited fields.</summary>
    public bool TryFindFieldOffset(ulong klass, string fieldName, out int fieldOffset)
    {
        fieldOffset = 0;
        ulong current = klass;
        for (int depth = 0; depth < _off.MaxParentDepth && Followable(current); depth++)
        {
            if (TryFindFieldInClass(current, fieldName, out fieldOffset))
            {
                return true;
            }

            if (!_mem.TryReadPointer(current + (ulong)_off.ClassParent, out current) || current == 0)
            {
                break;
            }
        }

        return false;
    }

    private bool TryFindFieldInClass(ulong klass, string fieldName, out int fieldOffset)
    {
        fieldOffset = 0;
        if (!_mem.TryReadInt32(klass + (ulong)_off.ClassDefFieldCount, out int count) || count < 0 || count > _off.MaxFields)
        {
            return false;
        }

        if (count == 0)
        {
            return false;
        }

        if (!_mem.TryReadPointer(klass + (ulong)_off.ClassFields, out ulong fields) || !Followable(fields))
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            ulong field = fields + (ulong)(i * _off.FieldStride);
            if (!_mem.TryReadPointer(field + (ulong)_off.FieldName, out ulong namePtr) || !Plausible(namePtr))
            {
                continue;
            }

            if (_mem.TryReadAsciiString(namePtr, ProcessMemoryExtensions.MaxNameLength, out string name) && name == fieldName)
            {
                return _mem.TryReadInt32(field + (ulong)_off.FieldOffset, out fieldOffset);
            }
        }

        return false;
    }

    /// <summary>Resolve an object's class via <c>object → vtable → klass</c>.</summary>
    public bool TryReadObjectClass(ulong obj, out ulong klass)
    {
        klass = 0;
        if (!_mem.TryReadPointer(obj + (ulong)_off.ObjectVTable, out ulong vtable) || !Followable(vtable))
        {
            return false;
        }

        return _mem.TryReadPointer(vtable + (ulong)_off.VtableKlass, out klass) && Followable(klass);
    }

    /// <summary>Decode a length-gated MonoString.</summary>
    public bool TryReadMonoString(ulong str, out string text)
    {
        text = string.Empty;
        if (!Followable(str))
        {
            return false;
        }

        if (!_mem.TryReadInt32(str + (ulong)_off.StringLength, out int length) || length < 0 || length > _off.MaxStringLength)
        {
            return false;
        }

        return _mem.TryReadUtf16(str + (ulong)_off.StringChars, length, out text);
    }
}
