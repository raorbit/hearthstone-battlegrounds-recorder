using BgRecorder.Core.Events;
using BgRecorder.Core.Rating;
using Xunit;

namespace BgRecorder.Rating.Tests;

public sealed class MemoryRatingProviderTests
{
    private static MemoryRatingProvider Provider(
        IProcessMemoryFactory factory,
        Func<DateTimeOffset> clock,
        TimeSpan? throttle = null,
        TimeSpan? backoff = null,
        TimeSpan? scanRetry = null) =>
        new(
            factory,
            MonoOffsets.UnityMasterDefault,
            _ => { },
            clock,
            throttle ?? TimeSpan.FromSeconds(2),
            backoff ?? TimeSpan.FromSeconds(10),
            scanRetry ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Solo_and_duos_project_from_one_read_to_distinct_snapshots()
    {
        var scenario = BaconScenario.Build(solo: 8421, duos: 6200);
        var now = DateTimeOffset.UnixEpoch;
        var provider = Provider(FakeProcessMemoryFactory.Attaches(scenario.Memory), () => now);

        RatingSnapshot? solo = await provider.TryGetAsync(BgGameType.Solo);
        RatingSnapshot? duos = await provider.TryGetAsync(BgGameType.Duos);

        Assert.Equal(RatingHealth.Ok, provider.Health);
        Assert.NotNull(solo);
        Assert.Equal(8421, solo!.Rating);
        Assert.Equal(BgGameType.Solo, solo.Mode);
        Assert.NotNull(duos);
        Assert.Equal(6200, duos!.Rating);
        Assert.Equal(BgGameType.Duos, duos.Mode);
    }

    [Fact]
    public async Task An_unreadable_process_reports_attach_failed()
    {
        var now = DateTimeOffset.UnixEpoch;
        var provider = Provider(FakeProcessMemoryFactory.Fails(AttachFault.ProcessNotFound), () => now);

        RatingSnapshot? snapshot = await provider.TryGetAsync(BgGameType.Solo);

        Assert.Equal(RatingHealth.AttachFailed, provider.Health);
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Il2cpp_migration_reports_patch_broken()
    {
        var now = DateTimeOffset.UnixEpoch;
        var provider = Provider(FakeProcessMemoryFactory.Fails(AttachFault.Il2Cpp), () => now);

        await provider.TryGetAsync(BgGameType.Solo);

        Assert.Equal(RatingHealth.PatchBroken, provider.Health);
    }

    [Fact]
    public async Task Attached_but_unresolvable_offsets_report_patch_broken()
    {
        var scenario = BaconScenario.Build(includeBaconClass: false);
        var now = DateTimeOffset.UnixEpoch;
        var provider = Provider(FakeProcessMemoryFactory.Attaches(scenario.Memory), () => now);

        RatingSnapshot? snapshot = await provider.TryGetAsync(BgGameType.Solo);

        Assert.Equal(RatingHealth.PatchBroken, provider.Health);
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task An_unpopulated_manager_is_healthy_with_a_null_snapshot()
    {
        var scenario = BaconScenario.Build(managerNull: true);
        var now = DateTimeOffset.UnixEpoch;
        var provider = Provider(FakeProcessMemoryFactory.Attaches(scenario.Memory), () => now);

        RatingSnapshot? snapshot = await provider.TryGetAsync(BgGameType.Solo);

        Assert.Equal(RatingHealth.Ok, provider.Health);
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task A_zero_rating_is_treated_as_unset_for_that_mode_only()
    {
        var scenario = BaconScenario.Build(solo: 0, duos: 6200);
        var now = DateTimeOffset.UnixEpoch;
        var provider = Provider(FakeProcessMemoryFactory.Attaches(scenario.Memory), () => now);

        Assert.Null(await provider.TryGetAsync(BgGameType.Solo));
        Assert.NotNull(await provider.TryGetAsync(BgGameType.Duos));
        Assert.Equal(RatingHealth.Ok, provider.Health);
    }

    [Fact]
    public async Task Reads_are_throttled_between_polls_then_refresh_after_the_window()
    {
        var scenario = BaconScenario.Build();
        var now = DateTimeOffset.UnixEpoch;
        var provider = Provider(FakeProcessMemoryFactory.Attaches(scenario.Memory), () => now, throttle: TimeSpan.FromSeconds(2));

        await provider.TryGetAsync(BgGameType.Solo);
        int afterFirst = scenario.Memory.ReadCount;

        await provider.TryGetAsync(BgGameType.Duos); // same instant → throttled, serves cache
        Assert.Equal(afterFirst, scenario.Memory.ReadCount);

        now = now.AddSeconds(3); // past the throttle → a fresh poll
        await provider.TryGetAsync(BgGameType.Solo);
        Assert.True(scenario.Memory.ReadCount > afterFirst);
    }

    [Fact]
    public async Task An_unresolved_class_scan_is_retried_on_the_scan_cadence_not_every_poll()
    {
        var scenario = BaconScenario.Build(includeBaconClass: false);
        var now = DateTimeOffset.UnixEpoch;
        var provider = Provider(
            FakeProcessMemoryFactory.Attaches(scenario.Memory),
            () => now,
            throttle: TimeSpan.FromSeconds(2),
            scanRetry: TimeSpan.FromSeconds(30));

        await provider.TryGetAsync(BgGameType.Solo); // first poll runs the full (failing) class scan
        Assert.Equal(RatingHealth.PatchBroken, provider.Health);
        int afterFirst = scenario.Memory.ReadCount;

        now = now.AddSeconds(3); // past the poll throttle but inside the scan-retry window
        Assert.Null(await provider.TryGetAsync(BgGameType.Solo));
        Assert.Equal(afterFirst, scenario.Memory.ReadCount); // no rescan
        Assert.Equal(RatingHealth.PatchBroken, provider.Health);

        now = now.AddSeconds(29); // past the scan-retry window → the scan runs again
        await provider.TryGetAsync(BgGameType.Solo);
        Assert.True(scenario.Memory.ReadCount > afterFirst);
    }

    [Fact]
    public async Task A_resolved_reader_is_not_gated_by_an_expired_scan_backoff()
    {
        // The class is present: the first poll resolves and caches the statics. Later polls must keep
        // refreshing on the ordinary throttle — the scan backoff only ever applies while unresolved.
        var scenario = BaconScenario.Build(solo: 8421, duos: 6200);
        var now = DateTimeOffset.UnixEpoch;
        var provider = Provider(
            FakeProcessMemoryFactory.Attaches(scenario.Memory),
            () => now,
            throttle: TimeSpan.FromSeconds(2),
            scanRetry: TimeSpan.FromSeconds(30));

        await provider.TryGetAsync(BgGameType.Solo);
        Assert.Equal(RatingHealth.Ok, provider.Health);
        int afterFirst = scenario.Memory.ReadCount;

        now = now.AddSeconds(3); // past the throttle; well inside what a scan backoff would have been
        Assert.NotNull(await provider.TryGetAsync(BgGameType.Solo));
        Assert.True(scenario.Memory.ReadCount > afterFirst); // the poll ran — no scan gate in the way
    }

    [Fact]
    public async Task Re_attach_is_backed_off_after_a_failure()
    {
        var factory = FakeProcessMemoryFactory.Fails(AttachFault.ProcessNotFound);
        var now = DateTimeOffset.UnixEpoch;
        var provider = Provider(factory, () => now, backoff: TimeSpan.FromSeconds(10));

        await provider.TryGetAsync(BgGameType.Solo);
        Assert.Equal(1, factory.AttachCount);

        await provider.TryGetAsync(BgGameType.Solo); // within backoff → no re-attempt
        Assert.Equal(1, factory.AttachCount);

        now = now.AddSeconds(11); // past backoff → re-attempt
        await provider.TryGetAsync(BgGameType.Solo);
        Assert.Equal(2, factory.AttachCount);
    }
}
