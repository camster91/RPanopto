using System.Collections.Concurrent;
using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.Core.RateLimiting;

/// <summary>
/// Hands out one <see cref="EndpointRateLimiter"/> per endpoint path.
///
/// Because Panopto meters per endpoint, two different endpoints may be called
/// concurrently at full speed — the registry exists so that parallelism is
/// safe rather than accidental.
/// </summary>
public sealed class RateLimiterRegistry
{
    private readonly ConcurrentDictionary<string, EndpointRateLimiter> _limiters =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly (int Limit, TimeSpan Window)[] _limits;
    private readonly ApiCallCounter? _counter;

    public RateLimiterRegistry((int Limit, TimeSpan Window)[]? limits = null, ApiCallCounter? counter = null)
    {
        _limits = limits ?? EndpointRateLimiter.DefaultLimits;
        _counter = counter;
    }

    public EndpointRateLimiter For(string endpoint)
        => _limiters.GetOrAdd(endpoint, e => new EndpointRateLimiter(e, _limits, _counter));

    public IReadOnlyDictionary<string, EndpointRateLimiter> All => _limiters;
}
