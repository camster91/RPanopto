using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.Core.RateLimiting;

/// <summary>
/// Sliding-window rate limiter enforcing Panopto's published limits.
///
/// Panopto meters <b>per endpoint as well as per client</b>, so a single global
/// limiter is both wrong (it would throttle unrelated endpoints against each
/// other) and unsafe (it would let one hot endpoint exceed its own budget while
/// other endpoints sat idle). Callers should therefore hold one limiter per
/// endpoint path — see <see cref="RateLimiterRegistry"/>.
///
/// Windows are enforced simultaneously: a call must have room in every window
/// before it is allowed through.
/// </summary>
public sealed class EndpointRateLimiter
{
    /// <summary>Panopto's published ceilings.</summary>
    public static readonly (int Limit, TimeSpan Window)[] DefaultLimits =
    [
        (5, TimeSpan.FromSeconds(1)),
        (100, TimeSpan.FromMinutes(1)),
        (5_000, TimeSpan.FromHours(1)),
    ];

    private readonly (int Limit, TimeSpan Window)[] _limits;
    private readonly Queue<DateTimeOffset>[] _hits;
    private readonly Lock _sync = new();
    private readonly ApiCallCounter _counter;

    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    public EndpointRateLimiter(string endpoint, (int Limit, TimeSpan Window)[]? limits = null, ApiCallCounter? counter = null)
    {
        Endpoint = endpoint;
        _limits = limits ?? DefaultLimits;
        _hits = new Queue<DateTimeOffset>[_limits.Length];
        for (var i = 0; i < _hits.Length; i++) _hits[i] = new Queue<DateTimeOffset>();
        _counter = counter ?? ApiCallCounter.Shared;
    }

    public string Endpoint { get; }

    /// <summary>
    /// Blocks until the endpoint has capacity in all windows, then records the
    /// call. Check and record happen under one lock so that concurrent callers
    /// cannot both observe room and both consume it.
    /// </summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;
                delay = TimeSpan.Zero;

                // An explicit server-imposed pause outranks our own accounting.
                if (_pausedUntil > now)
                {
                    delay = _pausedUntil - now;
                }
                else
                {
                    for (var i = 0; i < _limits.Length; i++)
                    {
                        Prune(_hits[i], now - _limits[i].Window);

                        if (_hits[i].Count >= _limits[i].Limit)
                        {
                            // When the oldest hit in this window ages out, room appears.
                            var readyAt = _hits[i].Peek() + _limits[i].Window;
                            var wait = readyAt - now;
                            if (wait > delay) delay = wait;
                        }
                    }

                    if (delay <= TimeSpan.Zero)
                    {
                        foreach (var window in _hits) window.Enqueue(now);
                        _counter.Record(Endpoint);
                        return;
                    }
                }
            }

            // Sleep outside the lock so other endpoints and other waiters proceed.
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called when the server returns 429. Panopto may be enforcing a limit we
    /// are not modelling, so this hard-pauses the endpoint until
    /// <paramref name="retryAfter"/> elapses, regardless of our own headroom.
    /// </summary>
    public void PauseFor(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero) return;

        lock (_sync)
        {
            var until = DateTimeOffset.UtcNow + retryAfter;
            // Never shorten an existing pause.
            if (until > _pausedUntil) _pausedUntil = until;
        }
    }

    /// <summary>
    /// Current occupancy, for surfacing headroom in the UI so the team can see
    /// they are inside the limits rather than guessing.
    /// </summary>
    public IReadOnlyList<(int Limit, TimeSpan Window, int Used)> Snapshot()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var result = new (int, TimeSpan, int)[_limits.Length];

            for (var i = 0; i < _limits.Length; i++)
            {
                Prune(_hits[i], now - _limits[i].Window);
                result[i] = (_limits[i].Limit, _limits[i].Window, _hits[i].Count);
            }

            return result;
        }
    }

    private static void Prune(Queue<DateTimeOffset> window, DateTimeOffset cutoff)
    {
        while (window.Count > 0 && window.Peek() <= cutoff) window.Dequeue();
    }
}
