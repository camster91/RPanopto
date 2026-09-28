using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.RateLimiting;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// "Very low API usage" is a requirement rather than an impression, so it has
/// to be measured. These guard the counter's arithmetic, its day rollover, and
/// that the rate limiter — the one place every real request passes through —
/// actually records through it.
/// </summary>
public class ApiCallCounterTests
{
    [Fact]
    public void Counts_per_endpoint_and_total()
    {
        var day = new DateOnly(2026, 9, 28);
        var counter = new ApiCallCounter(() => day);

        counter.Record("GetSessions");
        counter.Record("ListRecorders");
        counter.Record("ListRecorders");

        Assert.Equal(3, counter.Total);
        Assert.Equal(2, counter.Snapshot()["ListRecorders"]);
        Assert.Equal("API calls 2026-09-28: 3 (ListRecorders 2, GetSessions 1)", counter.Summary());
    }

    [Fact]
    public void A_new_day_starts_from_zero()
    {
        var day = new DateOnly(2026, 9, 28);
        var counter = new ApiCallCounter(() => day);
        counter.Record("GetSessions");

        day = day.AddDays(1);
        counter.Record("GetSessions");

        Assert.Equal(1, counter.Total);
    }

    [Fact]
    public async Task Limiter_records_one_call_per_wait()
    {
        var counter = new ApiCallCounter(() => new DateOnly(2026, 9, 28));
        var limiter = new EndpointRateLimiter("ListRecorders", counter: counter);

        await limiter.WaitAsync();
        await limiter.WaitAsync();

        Assert.Equal(2, counter.Snapshot()["ListRecorders"]);
    }
}
