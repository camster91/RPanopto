using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

public class ScheduleCacheTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static DateTime _now = new(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);

    private static PanoptoSession Session(Guid id, string name, DateTime start) => new()
    {
        SessionID = id.ToString("D"), SessionName = name, StartTime = start, Duration = 3600,
    };

    private static ScheduleCache Loaded(params PanoptoSession[] sessions)
    {
        var cache = new ScheduleCache(() => _now);
        cache.Replace(new PagedResult<PanoptoSession>(sessions, true, sessions.Length), cache.BeginRead());
        return cache;
    }

    [Fact]
    public void Empty_cache_needs_a_read()
        => Assert.True(new ScheduleCache(() => _now).NeedsRead);

    [Fact]
    public void Fresh_read_does_not_need_another()
        => Assert.False(Loaded(Session(A, "x", new DateTime(2026, 9, 29, 9, 0, 0))).NeedsRead);

    [Fact]
    public void Stale_after_fifteen_minutes()
    {
        var cache = Loaded(Session(A, "x", new DateTime(2026, 9, 29, 9, 0, 0)));
        _now = _now.AddMinutes(15).AddSeconds(1);
        try { Assert.True(cache.NeedsRead); }
        finally { _now = _now.AddMinutes(-15).AddSeconds(-1); }
    }

    [Fact]
    public void MarkStale_forces_a_read_and_Replace_clears_it()
    {
        var cache = Loaded(Session(A, "x", new DateTime(2026, 9, 29, 9, 0, 0)));
        cache.MarkStale();
        Assert.True(cache.NeedsRead);
        cache.Replace(new PagedResult<PanoptoSession>([], true, 0), cache.BeginRead());
        Assert.False(cache.NeedsRead);
    }

    [Fact]
    public void A_MarkStale_during_a_read_survives_that_read()
    {
        // The bulk window closing while the calendar is mid-load: its writes may
        // not be in the pages already walked, so that read must not clear the mark.
        var cache = Loaded(Session(A, "x", new DateTime(2026, 9, 29, 9, 0, 0)));
        cache.MarkStale();

        var token = cache.BeginRead();
        cache.MarkStale();
        cache.Replace(
            new PagedResult<PanoptoSession>([Session(B, "y", new DateTime(2026, 9, 29, 10, 0, 0))], true, 1),
            token);

        Assert.True(cache.NeedsRead);

        // The read's rows are still taken — they are newer than what was held.
        Assert.Equal(B.ToString("D"), Assert.Single(cache.Sessions).SessionID);
    }

    [Fact]
    public void A_MarkStale_before_the_read_started_is_cleared_by_it()
    {
        var cache = Loaded(Session(A, "x", new DateTime(2026, 9, 29, 9, 0, 0)));
        cache.MarkStale();
        var token = cache.BeginRead();
        cache.Replace(new PagedResult<PanoptoSession>([], true, 0), token);
        Assert.False(cache.NeedsRead);
    }

    [Fact]
    public void The_next_read_after_a_surviving_mark_clears_it()
    {
        var cache = Loaded();
        var first = cache.BeginRead();
        cache.MarkStale();
        cache.Replace(new PagedResult<PanoptoSession>([], true, 0), first);
        Assert.True(cache.NeedsRead);

        cache.Replace(new PagedResult<PanoptoSession>([], true, 0), cache.BeginRead());
        Assert.False(cache.NeedsRead);
    }

    [Fact]
    public void A_patch_does_not_refresh_the_read_time()
    {
        var cache = Loaded(Session(A, "x", new DateTime(2026, 9, 29, 9, 0, 0)));
        var readAt = cache.ReadAtUtc;
        Assert.True(cache.PatchName(A, "renamed"));
        Assert.Equal(readAt, cache.ReadAtUtc);
        Assert.Equal("renamed", cache.Sessions[0].SessionName);
    }

    [Fact]
    public void PatchTime_moves_the_session()
    {
        var cache = Loaded(Session(A, "x", new DateTime(2026, 9, 29, 9, 0, 0)));
        Assert.True(cache.PatchTime(A, new DateTime(2026, 9, 29, 11, 0, 0), TimeSpan.FromMinutes(90)));
        Assert.Equal(new DateTime(2026, 9, 29, 11, 0, 0), cache.Sessions[0].EffectiveStart);
    }

    [Fact]
    public void Patching_an_unknown_id_reports_false()
        => Assert.False(Loaded().PatchName(B, "nope"));

    [Fact]
    public void Remove_drops_matching_sessions_and_raises_Changed()
    {
        var cache = Loaded(Session(A, "a", new DateTime(2026, 9, 29, 9, 0, 0)),
                           Session(B, "b", new DateTime(2026, 9, 29, 10, 0, 0)));
        var raised = 0;
        cache.Changed += () => raised++;
        Assert.Equal(1, cache.Remove([A]));
        Assert.Single(cache.Sessions);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Incomplete_read_is_remembered()
    {
        var cache = new ScheduleCache(() => _now);
        cache.Replace(new PagedResult<PanoptoSession>([], false, 900), cache.BeginRead());
        Assert.False(cache.Complete);
    }

    [Fact]
    public void SessionTargets_skips_non_guid_ids()
        => Assert.Null(SessionTargets.From(new PanoptoSession { SessionID = "not-a-guid" }));
}
