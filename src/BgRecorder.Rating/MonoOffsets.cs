namespace BgRecorder.Rating;

/// <summary>
/// The x64 byte offsets an external reader needs to walk Unity's mono-2.0-bdwgc heap. Isolated in one
/// immutable record so a live-verification pass tunes numbers without touching walker logic.
/// </summary>
/// <remarks>
/// Provenance: derived solely from Mono's own open-source headers (Unity-Technologies/mono, unity-master:
/// object-internals.h, class-internals.h, class-private-definition.h, metadata-internals.h,
/// mono-internal-hash.h, domain-internals.h) plus canonical glib/CLR ABI. No tracker/reader source was
/// consulted. Confidence tags:
///   ROCK    — ABI-frozen across all 2.0-bdwgc versions; trust it.
///   STABLE  — stable within a regime; the relative layout holds even where a base shifts.
///   FRAGILE — version-sensitive; the seed values below are unity-master and MUST be verified against the
///             live DLL (see docs/mmr-offset-verification.md). Every use is guarded so a wrong FRAGILE
///             value degrades to PatchBroken, never silent garbage.
/// </remarks>
internal sealed record MonoOffsets
{
    // ---- MonoObject / MonoString (ROCK, object-internals.h) ----
    public int ObjectVTable { get; init; } = 0x00;   // ROCK — MonoObject.vtable
    public int ObjectHeaderSize { get; init; } = 0x10; // ROCK — instance fields begin here
    public int StringLength { get; init; } = 0x10;   // ROCK — int32 code-unit count
    public int StringChars { get; init; } = 0x14;    // ROCK — inline UTF-16, no padding after length

    // ---- MonoClassField (ROCK, class-internals.h; stride 0x20) ----
    public int FieldName { get; init; } = 0x08;      // ROCK — const char* name
    public int FieldOffset { get; init; } = 0x18;    // ROCK — instance: from object base; static: from static block
    public int FieldStride { get; init; } = 0x20;    // ROCK — {type,name,parent,offset} padded to 32

    // ---- MonoClassRuntimeInfo (ROCK, class-internals.h) ----
    public int RtiMaxDomain { get; init; } = 0x00;   // ROCK — guint16
    public int RtiDomainVtables { get; init; } = 0x08; // ROCK — MonoVTable*[] indexed by domain_id (stride 8)
    public int MaxDomainScan { get; init; } = 16;    // safety cap when probing domain_vtables for the root vtable

    // ---- MonoVTable (klass ROCK; array base FRAGILE) ----
    public int VtableKlass { get; init; } = 0x00;    // ROCK — back-pointer, used to confirm the right domain vtable
    public int VtableArrayBase { get; init; } = 0x48; // FRAGILE — 0x40 (non-interp) / 0x48 (interp); static block at +8*vtable_size
    public bool ClassicVTableData { get; init; }      // FRAGILE — pre-refactor builds store static block directly...
    public int VtableClassicData { get; init; } = 0x18; // FRAGILE — ...at MonoVTable.data

    // ---- MonoInternalHashTable / GSList (ROCK) ----
    public int HashSize { get; init; } = 0x18;       // ROCK — gint bucket count
    public int HashTable { get; init; } = 0x20;      // ROCK — gpointer*[] bucket heads
    public int GSListData { get; init; } = 0x00;     // ROCK — glib
    public int GSListNext { get; init; } = 0x08;     // ROCK — glib

    // ---- MonoClass (FRAGILE — unity-master x64; verify on the live DLL) ----
    public int ClassInstanceSize { get; init; } = 0x1C; // FRAGILE — sanity gate (small positive)
    public int ClassParent { get; init; } = 0x30;    // FRAGILE — base class for inherited-field search
    public int ClassName { get; init; } = 0x48;      // FRAGILE — const char*
    public int ClassNamespace { get; init; } = 0x50; // FRAGILE — const char*
    public int ClassVtableSize { get; init; } = 0x5C; // FRAGILE — int, drives the static-block formula
    public int ClassFields { get; init; } = 0x98;    // FRAGILE — MonoClassField* array (stride 0x20)
    public int ClassRuntimeInfo { get; init; } = 0xD0; // FRAGILE — MonoClassRuntimeInfo*

    // ---- MonoClassDef trailing region (STABLE relative to SizeofMonoClass) ----
    public int SizeofMonoClass { get; init; } = 0xF0; // FRAGILE — drives the two offsets below
    public int ClassDefFieldCount => SizeofMonoClass + 0x10; // STABLE — guint32 field_count
    public int ClassDefNextCache => SizeofMonoClass + 0x18;  // STABLE — MonoClass* next_class_cache (8-aligned after field_count)

    // ---- MonoAssembly / MonoImage (FRAGILE) ----
    public int AssemblyImage { get; init; } = 0x60;  // FRAGILE — MonoImage* after ref_count/basedir/MonoAssemblyName
    public int ImageAssemblyName { get; init; } = 0x28; // FRAGILE — const char*, near the top of MonoImage (diagnostics)
    public int ImageClassCache { get; init; } = 0x4A0; // FRAGILE — embedded MonoInternalHashTable, deep past tables[]

    // ---- MonoDomain (domain_assemblies discovered by calibration; see MonoImageReader) ----
    public int DomainAssembliesSeed { get; init; } = 0xA8; // FRAGILE seed; the scan overrides it per build
    public int DomainScanStart { get; init; } = 0x08;      // first slot the domain_assemblies scan probes
    public int DomainScanEnd { get; init; } = 0x400;       // last slot the scan probes

    // ---- Iteration / value safety caps ----
    public int MaxAssemblies { get; init; } = 512;     // GSList walk cap
    public int MaxHashBuckets { get; init; } = 1 << 16; // class_cache.size sanity ceiling (real caches hold a few thousand)
    public int MaxChainNodes { get; init; } = 4096;    // per-bucket next_class_cache chain cap
    public int MaxScanReads { get; init; } = 1 << 20;  // total bucket+node reads budgeted across one whole-domain class scan
    public int MaxFields { get; init; } = 8192;        // per-class field-array cap
    public int MaxParentDepth { get; init; } = 64;     // inherited-field search cap
    public int MaxStringLength { get; init; } = 4096;  // MonoString length gate

    /// <summary>The seed table for current Unity/Hearthstone builds. FRAGILE values are verified live.</summary>
    public static MonoOffsets UnityMasterDefault => new();
}
