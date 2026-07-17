using Xunit;

namespace BgRecorder.Rating.Tests;

public sealed class MonoImageReaderTests
{
    private static MonoImageReader ReaderFor(BaconScenario scenario) =>
        new(scenario.Memory, scenario.Offsets);

    [Fact]
    public void Root_domain_is_recovered_from_the_export_stub()
    {
        var scenario = BaconScenario.Build();
        var reader = ReaderFor(scenario);

        Assert.True(reader.TryGetRootDomain(out ulong domain));
        Assert.Equal(scenario.Domain, domain);
    }

    [Fact]
    public void A_class_is_found_across_the_domain_by_namespace_and_name()
    {
        var scenario = BaconScenario.Build();
        var reader = ReaderFor(scenario);

        Assert.True(reader.TryFindClass(scenario.Domain, "", "BaconRatingMgr", out ulong klass));
        Assert.NotEqual(0ul, klass);
    }

    [Fact]
    public void A_missing_class_is_reported_not_crashed()
    {
        var scenario = BaconScenario.Build(includeBaconClass: false);
        var reader = ReaderFor(scenario);

        Assert.False(reader.TryFindClass(scenario.Domain, "", "BaconRatingMgr", out _));
    }

    [Fact]
    public void The_assemblies_offset_is_calibrated_even_when_it_is_not_the_seed()
    {
        // Plant the list well away from the seed offset; the scan must still locate it.
        var offsets = MonoOffsets.UnityMasterDefault;
        var b = new MonoHeapBuilder(offsets);
        (ulong, int) fields = b.BuildFields(("s_instance", 0x10));
        ulong klass = b.BuildClass("BaconRatingMgr", "", fields);
        ulong image = b.BuildImage("Assembly-CSharp", klass);
        ulong assembly = b.BuildAssembly(image);
        ulong domain = b.BuildDomain(0x1F8, assembly); // not the 0xA8 seed
        b.BuildModule(domain);
        var reader = new MonoImageReader(b.Memory, offsets);

        Assert.True(reader.TryFindClass(domain, "", "BaconRatingMgr", out ulong found));
        Assert.Equal(klass, found);
    }

    [Fact]
    public void A_cyclic_class_chain_terminates_within_the_cap()
    {
        var offsets = MonoOffsets.UnityMasterDefault;
        var b = new MonoHeapBuilder(offsets);
        ulong klass = b.BuildClass("Decoy", "", b.BuildFields());
        ulong image = b.BuildImage("Assembly-CSharp", klass);
        b.WritePointer(klass + (ulong)offsets.ClassDefNextCache, klass); // self-cycle in the bucket chain
        ulong assembly = b.BuildAssembly(image);
        ulong domain = b.BuildDomain(0xA8, assembly);
        b.BuildModule(domain);
        var reader = new MonoImageReader(b.Memory, offsets);

        // Searching for an absent class walks the cyclic chain and must return (bounded), not hang.
        Assert.False(reader.TryFindClass(domain, "", "BaconRatingMgr", out _));
    }

    [Fact]
    public void A_garbage_static_instance_pointer_does_not_crash_the_object_walk()
    {
        var scenario = BaconScenario.Build();
        var reader = ReaderFor(scenario);

        // An address in an unmapped gap: resolving its class must fail cleanly.
        Assert.False(reader.TryReadObjectClass(0x7000_0000, out _));
    }

    [Fact]
    public void The_root_domain_is_recovered_through_a_cet_endbr64_prologue()
    {
        // Modern MSVC/CET builds emit endbr64 before the mov; the scanner must find the mov at i>0.
        var offsets = MonoOffsets.UnityMasterDefault;
        var b = new MonoHeapBuilder(offsets);
        ulong domain = b.BuildDomain(0xA8, b.BuildAssembly(b.BuildImage("Assembly-CSharp", b.BuildClass("X", "", b.BuildFields()))));
        b.BuildModule(domain, prologue: new byte[] { 0xF3, 0x0F, 0x1E, 0xFA });
        var reader = new MonoImageReader(b.Memory, offsets);

        Assert.True(reader.TryGetRootDomain(out ulong found));
        Assert.Equal(domain, found);
    }

    [Fact]
    public void An_inherited_field_is_resolved_by_walking_the_parent_chain()
    {
        var offsets = MonoOffsets.UnityMasterDefault;
        var b = new MonoHeapBuilder(offsets);
        ulong baseClass = b.BuildClass("Base", "", b.BuildFields(("Inherited", 0x28)));
        ulong child = b.BuildClass("Child", "", b.BuildFields(("Own", 0x18)), parent: baseClass);
        var reader = new MonoImageReader(b.Memory, offsets);

        Assert.True(reader.TryFindFieldOffset(child, "Inherited", out int offset));
        Assert.Equal(0x28, offset);
    }

    [Fact]
    public void A_parent_chain_cycle_terminates_within_the_depth_cap()
    {
        var offsets = MonoOffsets.UnityMasterDefault;
        var b = new MonoHeapBuilder(offsets);
        ulong klass = b.BuildClass("Loop", "", b.BuildFields(("Own", 0x18)), parent: 0);
        b.WritePointer(klass + (ulong)offsets.ClassParent, klass); // self-parent cycle
        var reader = new MonoImageReader(b.Memory, offsets);

        Assert.False(reader.TryFindFieldOffset(klass, "Missing", out _));
    }

    [Fact]
    public void Calibration_skips_decoy_slots_and_locks_onto_the_real_assembly_list()
    {
        var offsets = MonoOffsets.UnityMasterDefault;
        var b = new MonoHeapBuilder(offsets);

        // The real list (named image + resolvable class) is at 0xA8.
        ulong baconClass = b.BuildClass("BaconRatingMgr", "", b.BuildFields(("s_instance", 0x10)));
        ulong realList = b.BuildGSList(b.BuildAssembly(b.BuildImage("Assembly-CSharp", baconClass)));

        // Decoy A (earlier): a followable pointer into a region whose GSList "data" is null.
        ulong nonList = b.Alloc(0x20);
        // Decoy B (earlier): a structurally valid assembly list whose image carries no name.
        ulong namelessList = b.BuildGSList(b.BuildAssembly(b.BuildImage("", b.BuildClass("Z", "", b.BuildFields()))));

        ulong domain = b.AllocDomain();
        b.WritePointer(domain + 0x30, nonList);       // scanned first, rejected (data not followable)
        b.WritePointer(domain + 0x40, namelessList);  // scanned next, rejected (no named image)
        b.WritePointer(domain + 0xA8, realList);      // the genuine list
        b.BuildModule(domain);
        var reader = new MonoImageReader(b.Memory, offsets);

        Assert.True(reader.TryFindClass(domain, "", "BaconRatingMgr", out ulong found));
        Assert.Equal(baconClass, found);
    }

    [Fact]
    public void Static_data_resolves_when_the_class_vtable_sits_at_domain_slot_zero()
    {
        // Some builds/classes hold the vtable at domain_vtables[0]; the back-pointer check selects it.
        var offsets = MonoOffsets.UnityMasterDefault;
        var b = new MonoHeapBuilder(offsets);
        const int vtableSize = 2;
        ulong staticBlock = b.AllocStaticBlock();
        ulong klass = b.BuildClass("C", "", b.BuildFields(("s_instance", 0x10)), vtableSize: vtableSize);
        ulong vtable = b.BuildVTable(klass, staticBlock, vtableSize);
        b.SetRuntimeInfo(klass, b.BuildRuntimeInfo(vtable, domainId: 0, maxDomain: 0));
        var reader = new MonoImageReader(b.Memory, offsets);

        Assert.True(reader.TryGetStaticData(klass, out ulong staticData));
        Assert.Equal(staticBlock, staticData);
    }

    [Fact]
    public void Class_lookup_honors_cancellation()
    {
        var scenario = BaconScenario.Build();
        var reader = ReaderFor(scenario);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The class exists, but a pre-cancelled token must abort the scan rather than resolve it.
        Assert.False(reader.TryFindClass(scenario.Domain, "", "BaconRatingMgr", out _, cts.Token));
    }

    [Fact]
    public void A_bogus_class_cache_size_fails_fast_instead_of_grinding()
    {
        // Simulate a wrong FRAGILE offset: a plausible-but-huge bucket count over a followable-but-empty table,
        // replicated across two assemblies. Without a global budget the scan would issue 2 x 40000 bucket reads;
        // the shared budget must abort well before that so TryGetAsync degrades to PatchBroken promptly.
        var offsets = MonoOffsets.UnityMasterDefault with { MaxScanReads = 5000 };
        var b = new MonoHeapBuilder(offsets);
        ulong image = b.BuildImage("Assembly-CSharp"); // no classes; overwrite its size/table below
        ulong cache = image + (ulong)offsets.ImageClassCache;
        b.WriteInt32(cache + (ulong)offsets.HashSize, 40000); // huge but under the ceiling → passes the sanity gate
        ulong emptyTable = b.Alloc(40000 * 8);               // followable, all-zero buckets
        b.WritePointer(cache + (ulong)offsets.HashTable, emptyTable);
        ulong domain = b.BuildDomain(0xA8, b.BuildAssembly(image), b.BuildAssembly(image));
        b.BuildModule(domain);
        var reader = new MonoImageReader(b.Memory, offsets);

        int before = b.Memory.ReadCount;
        Assert.False(reader.TryFindClass(domain, "", "BaconRatingMgr", out _));
        int reads = b.Memory.ReadCount - before;
        Assert.True(reads <= offsets.MaxScanReads + 1024, $"scan issued {reads} reads");
    }
}
