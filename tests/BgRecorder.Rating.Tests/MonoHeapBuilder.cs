using System.Text;

namespace BgRecorder.Rating.Tests;

/// <summary>
/// Lays out a synthetic Mono heap in the <see cref="FakeProcessMemory"/> using the SAME
/// <see cref="MonoOffsets"/> the production walker reads with — so a passing test proves the walker's
/// pointer arithmetic, not a second copy of it. Every allocation is a disjoint, 8-aligned region with a gap
/// after it, so a stray pointer into the gap fails the read (an unmapped page).
/// </summary>
internal sealed class MonoHeapBuilder
{
    private const ulong Gap = 0x1000;

    private readonly MonoOffsets _off;
    private readonly FakeProcessMemory _memory = new();
    private readonly List<(ulong Base, byte[] Data)> _arrays = new();
    private ulong _next = 0x0010_0000;

    public MonoHeapBuilder(MonoOffsets offsets) => _off = offsets;

    public FakeProcessMemory Memory => _memory;

    public MonoOffsets Offsets => _off;

    public ulong Alloc(int size)
    {
        int bytes = Math.Max(size, 8);
        ulong va = (_next + 7) & ~7UL;
        var data = new byte[bytes];
        _arrays.Add((va, data));
        _memory.AddRegion(va, data);
        _next = va + (ulong)bytes + Gap;
        return va;
    }

    public void WriteBytes(ulong va, ReadOnlySpan<byte> bytes)
    {
        foreach ((ulong baseVa, byte[] data) in _arrays)
        {
            if (va >= baseVa && va + (ulong)bytes.Length <= baseVa + (ulong)data.Length)
            {
                bytes.CopyTo(data.AsSpan((int)(va - baseVa)));
                return;
            }
        }

        throw new InvalidOperationException($"write at 0x{va:X} is outside any allocated region");
    }

    public void WritePointer(ulong va, ulong value) => WriteBytes(va, BitConverter.GetBytes(value));

    public void WriteInt32(ulong va, int value) => WriteBytes(va, BitConverter.GetBytes(value));

    public void WriteUInt16(ulong va, ushort value) => WriteBytes(va, BitConverter.GetBytes(value));

    public ulong AllocAscii(string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        ulong va = Alloc(bytes.Length + 1); // trailing NUL is already zero
        WriteBytes(va, bytes);
        return va;
    }

    public ulong AllocMonoString(string value)
    {
        ulong va = Alloc(_off.StringChars + value.Length * 2 + 8);
        WriteInt32(va + (ulong)_off.StringLength, value.Length);
        WriteBytes(va + (ulong)_off.StringChars, Encoding.Unicode.GetBytes(value));
        return va;
    }

    /// <summary>A MonoClassField[] array; returns (base, count).</summary>
    public (ulong Base, int Count) BuildFields(params (string Name, int Offset)[] fields)
    {
        ulong baseVa = Alloc(fields.Length * _off.FieldStride + 16);
        for (int i = 0; i < fields.Length; i++)
        {
            ulong field = baseVa + (ulong)(i * _off.FieldStride);
            WritePointer(field + (ulong)_off.FieldName, AllocAscii(fields[i].Name));
            WriteInt32(field + (ulong)_off.FieldOffset, fields[i].Offset);
        }

        return (baseVa, fields.Length);
    }

    public ulong BuildClass(
        string name,
        string ns,
        (ulong Base, int Count) fields = default,
        ulong parent = 0,
        ulong runtimeInfo = 0,
        int vtableSize = 0)
    {
        ulong klass = Alloc(_off.SizeofMonoClass + 0x40);
        WritePointer(klass + (ulong)_off.ClassName, AllocAscii(name));
        WritePointer(klass + (ulong)_off.ClassNamespace, AllocAscii(ns));
        WritePointer(klass + (ulong)_off.ClassFields, fields.Base);
        WriteInt32(klass + (ulong)_off.ClassDefFieldCount, fields.Count);
        WritePointer(klass + (ulong)_off.ClassParent, parent);
        WritePointer(klass + (ulong)_off.ClassRuntimeInfo, runtimeInfo);
        WriteInt32(klass + (ulong)_off.ClassVtableSize, vtableSize);
        WriteInt32(klass + (ulong)_off.ClassInstanceSize, 0x20);
        return klass;
    }

    /// <summary>A MonoClassRuntimeInfo whose root-domain slot (id 1) points at <paramref name="vtable"/>.</summary>
    public ulong BuildRuntimeInfo(ulong vtable, int domainId = 1, int maxDomain = 1)
    {
        ulong rti = Alloc(_off.RtiDomainVtables + (maxDomain + 1) * 8 + 8);
        WriteUInt16(rti + (ulong)_off.RtiMaxDomain, (ushort)maxDomain);
        WritePointer(rti + (ulong)_off.RtiDomainVtables + (ulong)(domainId * 8), vtable);
        return rti;
    }

    /// <summary>A MonoVTable carrying its class back-pointer and the static-data block at vtable[vtable_size].</summary>
    public ulong BuildVTable(ulong klass, ulong staticData, int vtableSize = 0)
    {
        ulong vtable = Alloc(_off.VtableArrayBase + (vtableSize + 2) * 8 + 8);
        WritePointer(vtable + (ulong)_off.VtableKlass, klass);
        WritePointer(vtable + (ulong)_off.VtableArrayBase + (ulong)(vtableSize * 8), staticData);
        return vtable;
    }

    /// <summary>A pre-refactor MonoVTable storing the static block directly at MonoVTable.data.</summary>
    public ulong BuildVTableClassic(ulong klass, ulong staticData)
    {
        ulong vtable = Alloc(_off.VtableClassicData + 16);
        WritePointer(vtable + (ulong)_off.VtableKlass, klass);
        WritePointer(vtable + (ulong)_off.VtableClassicData, staticData);
        return vtable;
    }

    public ulong AllocStaticBlock(int size = 0x40) => Alloc(size);

    /// <summary>Wire a class's runtime_info after the fact (the vtable/rti reference the class, so the cycle
    /// can only be closed once both ends exist).</summary>
    public void SetRuntimeInfo(ulong klass, ulong runtimeInfo) =>
        WritePointer(klass + (ulong)_off.ClassRuntimeInfo, runtimeInfo);

    /// <summary>A managed object whose class is <paramref name="klass"/> (object → vtable → klass).</summary>
    public ulong BuildObject(ulong klass, int size = 0x80)
    {
        ulong vtable = Alloc(_off.VtableArrayBase + 8);
        WritePointer(vtable + (ulong)_off.VtableKlass, klass);
        ulong obj = Alloc(Math.Max(size, _off.ObjectHeaderSize + 8));
        WritePointer(obj + (ulong)_off.ObjectVTable, vtable);
        return obj;
    }

    /// <summary>An image with an embedded class_cache holding <paramref name="classes"/>, all chained in one bucket.</summary>
    public ulong BuildImage(string assemblyName, params ulong[] classes)
    {
        const int buckets = 8;
        ulong table = Alloc(buckets * 8 + 8);
        if (classes.Length > 0)
        {
            WritePointer(table, classes[0]); // bucket 0 head
            for (int i = 0; i < classes.Length - 1; i++)
            {
                WritePointer(classes[i] + (ulong)_off.ClassDefNextCache, classes[i + 1]);
            }
        }

        ulong image = Alloc(_off.ImageClassCache + 0x40);
        WritePointer(image + (ulong)_off.ImageAssemblyName, AllocAscii(assemblyName));
        WriteInt32(image + (ulong)_off.ImageClassCache + (ulong)_off.HashSize, buckets);
        WritePointer(image + (ulong)_off.ImageClassCache + (ulong)_off.HashTable, table);
        return image;
    }

    public ulong BuildAssembly(ulong image)
    {
        ulong assembly = Alloc(_off.AssemblyImage + 0x10);
        WritePointer(assembly + (ulong)_off.AssemblyImage, image);
        return assembly;
    }

    /// <summary>A null-terminated GSList over the given elements; returns the head node (0 if empty).</summary>
    public ulong BuildGSList(params ulong[] items)
    {
        ulong head = 0;
        for (int i = items.Length - 1; i >= 0; i--)
        {
            ulong node = Alloc(0x10);
            WritePointer(node + (ulong)_off.GSListData, items[i]);
            WritePointer(node + (ulong)_off.GSListNext, head);
            head = node;
        }

        return head;
    }

    /// <summary>Allocate an empty domain region; plant lists into it with <see cref="WritePointer"/>.</summary>
    public ulong AllocDomain() => Alloc(_off.DomainScanEnd + 0x40);

    /// <summary>A MonoDomain with a GSList of assemblies planted at <paramref name="assembliesOffset"/>.</summary>
    public ulong BuildDomain(int assembliesOffset, params ulong[] assemblies)
    {
        ulong domain = AllocDomain();
        WritePointer(domain + (ulong)assembliesOffset, BuildGSList(assemblies));
        return domain;
    }

    /// <summary>
    /// Lay out a fake mono-2.0-bdwgc PE image whose <c>mono_get_root_domain</c> export loads
    /// <paramref name="domain"/> via a RIP-relative mov, and point ModuleBase/Size at it. An optional
    /// <paramref name="prologue"/> (e.g. a CET endbr64) precedes the mov so the RIP scanner's i&gt;0 path is exercised.
    /// </summary>
    public void BuildModule(ulong domain, byte[]? prologue = null)
    {
        const int size = 0x400;
        ulong mb = Alloc(size);
        _memory.ModuleBase = mb;
        _memory.ModuleSize = size;

        WriteInt32(mb + 0x3C, 0x80);                       // e_lfanew
        WriteBytes(mb + 0x80, "PE\0\0"u8);                 // NT signature
        WriteUInt16(mb + 0x80 + 0x18, 0x20B);              // PE32+ optional-header magic
        WriteInt32(mb + 0x80 + 0x18 + 0x70, 0x200);        // data dir[0].VirtualAddress (export table RVA)
        WriteInt32(mb + 0x80 + 0x18 + 0x70 + 4, 0x100);    // data dir[0].Size

        ulong ed = mb + 0x200;
        WriteInt32(ed + 0x14, 1);      // NumberOfFunctions
        WriteInt32(ed + 0x18, 1);      // NumberOfNames
        WriteInt32(ed + 0x1C, 0x260);  // AddressOfFunctions
        WriteInt32(ed + 0x20, 0x270);  // AddressOfNames
        WriteInt32(ed + 0x24, 0x280);  // AddressOfNameOrdinals

        WriteInt32(mb + 0x260, 0x300);        // functions[0] = stub RVA
        WriteInt32(mb + 0x270, 0x2A0);        // names[0] = name-string RVA
        WriteUInt16(mb + 0x280, 0);           // ordinals[0]
        WriteBytes(mb + 0x2A0, "mono_get_root_domain\0"u8);

        // Stub: [prologue] 48 8B 05 <disp32> C3  →  mov rax,[rip+disp]; ret.  slot = movEnd + disp.
        const ulong stubRva = 0x300;
        const ulong slotRva = 0x320;
        ulong movRva = stubRva + (ulong)(prologue?.Length ?? 0);
        if (prologue is { Length: > 0 })
        {
            WriteBytes(mb + stubRva, prologue);
        }

        int disp = (int)(slotRva - (movRva + 7));
        WriteBytes(mb + movRva, new byte[] { 0x48, 0x8B, 0x05 });
        WriteInt32(mb + movRva + 3, disp);
        WriteBytes(mb + movRva + 7, new byte[] { 0xC3 });
        WritePointer(mb + slotRva, domain);
    }
}
