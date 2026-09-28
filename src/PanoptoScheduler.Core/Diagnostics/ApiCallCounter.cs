namespace PanoptoScheduler.Core.Diagnostics;

/// <summary>
/// How many requests this copy of the app sent to Panopto today, by endpoint.
///
/// <para>"Very low API usage" is a requirement, so it is measured rather than
/// assumed: every request passes through one rate limiter wait, and that wait
/// records here. The day's summary is written to the log on exit and when the
/// day rolls over.</para>
/// </summary>
public sealed class ApiCallCounter
{
    public static ApiCallCounter Shared { get; } = new();

    private readonly Func<DateOnly> _today;
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();
    private DateOnly _day;

    public ApiCallCounter(Func<DateOnly>? today = null)
    {
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _day = _today();
    }

    public int Total
    {
        get { lock (_sync) { RollIfNewDay(); return _counts.Values.Sum(); } }
    }

    public void Record(string endpoint)
    {
        lock (_sync)
        {
            RollIfNewDay();
            _counts[endpoint] = _counts.GetValueOrDefault(endpoint) + 1;
        }
    }

    public IReadOnlyDictionary<string, int> Snapshot()
    {
        lock (_sync) { RollIfNewDay(); return new Dictionary<string, int>(_counts, StringComparer.OrdinalIgnoreCase); }
    }

    public string Summary()
    {
        lock (_sync)
        {
            RollIfNewDay();
            return Format(_day, _counts);
        }
    }

    private static string Format(DateOnly day, Dictionary<string, int> counts)
    {
        var parts = counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key} {p.Value}");
        return $"API calls {day:yyyy-MM-dd}: {counts.Values.Sum()} ({string.Join(", ", parts)})";
    }

    private void RollIfNewDay()
    {
        var today = _today();
        if (today == _day) return;

        if (_counts.Count > 0) AppLog.Info(Format(_day, _counts));
        _counts.Clear();
        _day = today;
    }
}
