using System.Diagnostics;
using PanoptoScheduler.Core.RateLimiting;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// Timing assertions use generous margins — they are checking that a window is
/// enforced at all, not measuring the clock. Windows are kept short so the
/// suite stays fast.
/// </summary>
public class EndpointRateLimiterTests
{
    [Fact]
    public async Task Allows_burst_up_to_limit_then_blocks()
    {
        var limiter = new EndpointRateLimiter("t", [(3, TimeSpan.FromMilliseconds(300))]);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) await limiter.WaitAsync();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(150),
            $"first 3 calls should not be delayed, took {sw.ElapsedMilliseconds}ms");

        var before = sw.Elapsed;
        await limiter.WaitAsync();   // 4th exceeds the limit
        var waited = sw.Elapsed - before;

        Assert.True(waited >= TimeSpan.FromMilliseconds(200),
            $"4th call should have waited for the window, waited only {waited.TotalMilliseconds:0}ms");
    }

    [Fact]
    public async Task Tightest_window_governs()
    {
        // Loose long window, tight short window: the short one must decide.
        var limiter = new EndpointRateLimiter("t",
        [
            (10, TimeSpan.FromSeconds(30)),
            (1, TimeSpan.FromMilliseconds(200)),
        ]);

        await limiter.WaitAsync();

        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(150),
            $"tight window should dominate, waited only {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Endpoints_are_metered_independently()
    {
        // Panopto meters per endpoint, so a saturated endpoint must not stall
        // an unrelated one. This is what makes cross-endpoint parallelism safe.
        var registry = new RateLimiterRegistry([(1, TimeSpan.FromSeconds(30))]);

        await registry.For("endpoint-a").WaitAsync();

        var sw = Stopwatch.StartNew();
        await registry.For("endpoint-b").WaitAsync();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(150),
            $"a different endpoint should be unaffected, waited {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task PauseFor_blocks_even_with_headroom()
    {
        // A 429 must stop the endpoint even though our own counters are empty.
        // A naive implementation that only adds one "hit" would not block,
        // because one hit is far below a 5-per-second limit.
        var limiter = new EndpointRateLimiter("t");

        limiter.PauseFor(TimeSpan.FromMilliseconds(400));

        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(300),
            $"429 pause should block despite free headroom, waited {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task PauseFor_never_shortens_an_existing_pause()
    {
        var limiter = new EndpointRateLimiter("t");

        limiter.PauseFor(TimeSpan.FromMilliseconds(600));
        limiter.PauseFor(TimeSpan.FromMilliseconds(1));   // a later, shorter 429

        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(450),
            $"a short Retry-After must not cut short a longer pause, waited {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Snapshot_reports_headroom()
    {
        var limiter = new EndpointRateLimiter("t", [(5, TimeSpan.FromSeconds(30))]);
        await limiter.WaitAsync();
        await limiter.WaitAsync();

        var snapshot = limiter.Snapshot();

        Assert.Single(snapshot);
        Assert.Equal(5, snapshot[0].Limit);
        Assert.Equal(2, snapshot[0].Used);
    }
}
