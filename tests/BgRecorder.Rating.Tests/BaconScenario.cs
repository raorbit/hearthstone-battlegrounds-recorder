namespace BgRecorder.Rating.Tests;

/// <summary>
/// Composes a full BaconRatingMgr object graph — module → domain → assembly → image → class_cache →
/// BaconRatingMgr.s_instance → m_lastRatingResponse → Rating/DuosRating — with knobs for the negative paths.
/// The field offsets here are arbitrary-but-consistent; the point is that the walker follows them.
/// </summary>
internal sealed class BaconScenario
{
    // Static-block-relative and object-relative field offsets planted in the heap.
    public const int SInstanceOffset = 0x10;
    public const int ResponseOffset = 0x18;
    public const int RatingOffset = 0x18;
    public const int DuosOffset = 0x1C;

    public required FakeProcessMemory Memory { get; init; }
    public required ulong Domain { get; init; }
    public required MonoOffsets Offsets { get; init; }

    public static BaconScenario Build(
        int solo = 8421,
        int duos = 6200,
        bool managerNull = false,
        bool responseNull = false,
        bool includeBaconClass = true,
        bool includeRatingField = true,
        bool classicVtable = false,
        string managerName = "BaconRatingMgr")
    {
        var offsets = classicVtable
            ? MonoOffsets.UnityMasterDefault with { ClassicVTableData = true }
            : MonoOffsets.UnityMasterDefault;
        var b = new MonoHeapBuilder(offsets);

        // Response object: class with Rating/DuosRating, then a live instance carrying the values.
        (ulong, int) responseFields = includeRatingField
            ? b.BuildFields(("Rating", RatingOffset), ("DuosRating", DuosOffset))
            : b.BuildFields(("Wins", RatingOffset), ("DuosRating", DuosOffset)); // "Rating" renamed → not found
        ulong responseClass = b.BuildClass("NetCacheBaconRating", "SomeNs", responseFields);
        ulong response = 0;
        if (!responseNull)
        {
            response = b.BuildObject(responseClass);
            b.WriteInt32(response + RatingOffset, solo);
            b.WriteInt32(response + DuosOffset, duos);
        }

        // BaconRatingMgr: static s_instance + instance m_lastRatingResponse; a static block behind the vtable.
        (ulong, int) baconFields = b.BuildFields(("s_instance", SInstanceOffset), ("m_lastRatingResponse", ResponseOffset));
        const int vtableSize = 3;
        ulong staticBlock = b.AllocStaticBlock();
        ulong baconClass = b.BuildClass(managerName, "", baconFields, vtableSize: vtableSize);
        ulong vtable = classicVtable
            ? b.BuildVTableClassic(baconClass, staticBlock)
            : b.BuildVTable(baconClass, staticBlock, vtableSize);
        b.SetRuntimeInfo(baconClass, b.BuildRuntimeInfo(vtable));

        ulong manager = 0;
        if (!managerNull)
        {
            manager = b.BuildObject(baconClass);
            b.WritePointer(manager + ResponseOffset, response);
        }

        b.WritePointer(staticBlock + SInstanceOffset, manager);

        // A decoy class shares the bucket so the scan must chain past a non-match.
        ulong decoy = b.BuildClass("BaconTavernUpgradeMgr", "", b.BuildFields(("s_instance", 0x10)));
        ulong[] classes = includeBaconClass ? new[] { decoy, baconClass } : new[] { decoy };
        ulong image = b.BuildImage("Assembly-CSharp", classes);
        ulong assembly = b.BuildAssembly(image);
        ulong domain = b.BuildDomain(0xA8, assembly);
        b.BuildModule(domain);

        return new BaconScenario { Memory = b.Memory, Domain = domain, Offsets = offsets };
    }
}
