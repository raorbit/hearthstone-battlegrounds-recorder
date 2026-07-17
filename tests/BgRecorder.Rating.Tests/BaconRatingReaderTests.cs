using Xunit;

namespace BgRecorder.Rating.Tests;

public sealed class BaconRatingReaderTests
{
    private static BaconRatingReader ReaderFor(BaconScenario scenario) =>
        new(new MonoImageReader(scenario.Memory, scenario.Offsets));

    [Fact]
    public void The_full_chain_reads_both_solo_and_duos_from_distinct_fields()
    {
        var reader = ReaderFor(BaconScenario.Build(solo: 8421, duos: 6200));

        RatingReadResult result = reader.Read();

        Assert.Equal(RatingReadState.Ok, result.State);
        Assert.Equal(8421, result.Rating);
        Assert.Equal(6200, result.DuosRating);
    }

    [Fact]
    public void A_null_manager_singleton_is_a_clean_unset_not_an_error()
    {
        var reader = ReaderFor(BaconScenario.Build(managerNull: true));

        Assert.Equal(RatingReadState.ManagerNull, reader.Read().State);
    }

    [Fact]
    public void A_null_rating_response_is_a_clean_unset_not_an_error()
    {
        var reader = ReaderFor(BaconScenario.Build(responseNull: true));

        Assert.Equal(RatingReadState.ResponseNull, reader.Read().State);
    }

    [Fact]
    public void A_missing_manager_class_reports_statics_unresolved()
    {
        var reader = ReaderFor(BaconScenario.Build(includeBaconClass: false));

        Assert.Equal(RatingReadState.StaticsUnresolved, reader.Read().State);
    }

    [Fact]
    public void A_renamed_rating_field_is_not_resolvable()
    {
        var reader = ReaderFor(BaconScenario.Build(includeRatingField: false));

        Assert.Equal(RatingReadState.NotResolvable, reader.Read().State);
    }

    [Fact]
    public void The_classic_pre_refactor_vtable_static_data_path_reads_the_rating()
    {
        var reader = ReaderFor(BaconScenario.Build(solo: 7777, duos: 3333, classicVtable: true));

        RatingReadResult result = reader.Read();

        Assert.Equal(RatingReadState.Ok, result.State);
        Assert.Equal(7777, result.Rating);
        Assert.Equal(3333, result.DuosRating);
    }

    [Fact]
    public void Resolved_metadata_is_cached_so_later_reads_are_far_cheaper()
    {
        var scenario = BaconScenario.Build(solo: 9000, duos: 4500);
        var reader = ReaderFor(scenario);

        RatingReadResult first = reader.Read(); // resolves the whole chain (export scan, class scan, fields)
        int firstReads = scenario.Memory.ReadCount;

        RatingReadResult again = reader.Read(); // metadata cached; only the object chain + ints re-read
        int secondReads = scenario.Memory.ReadCount - firstReads;

        Assert.Equal(9000, first.Rating);
        Assert.Equal(9000, again.Rating);
        Assert.Equal(4500, again.DuosRating);
        // Caching is the contract: the second read must issue dramatically fewer reads than the first.
        Assert.True(secondReads < firstReads / 2, $"first={firstReads}, second={secondReads}");
    }
}
