using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Scheduling;

/// <summary>
/// The scheduled set, read once and shared by every view.
///
/// <para><c>Data.svc</c> ignores its date parameters, so every read is the whole
/// tenant's schedule. Reading it again to show a different week bought nothing
/// and cost a full walk per click; this holds the last read and says when it
/// has to be replaced (spec §0).</para>
/// </summary>
public sealed class ScheduleCache(Func<DateTime>? utcNow = null)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private List<PanoptoSession> _sessions = [];
    private bool _stale;

    /// <summary>
    /// Bumped by every <see cref="MarkStale"/>, so a read can tell whether the
    /// cache was marked stale while it was in flight. See <see cref="BeginRead"/>.
    /// </summary>
    private int _generation;

    public event Action? Changed;

    public IReadOnlyList<PanoptoSession> Sessions => _sessions;
    public bool HasData { get; private set; }
    public bool Complete { get; private set; }

    /// <summary>When the last full read landed. Patches do not move it.</summary>
    public DateTime? ReadAtUtc { get; private set; }

    public bool NeedsRead =>
        !HasData || _stale || ReadAtUtc is not { } at || _utcNow() - at > MaxAge;

    /// <summary>
    /// Taken before a read is sent, and handed back to <see cref="Replace"/> with
    /// what the read returned.
    ///
    /// <para><b>Why a token and not just "the read landed, so it is fresh".</b>
    /// A read is a walk of every page, and it takes long enough for something
    /// else to write to the tenant while it runs — the bulk window closing over
    /// a calendar load is the measured case, and it marks the cache stale
    /// precisely because its writes may or may not be in the pages already
    /// walked. If <see cref="Replace"/> cleared the flag unconditionally, that
    /// mark would be erased by a read that started before it, and the next
    /// action would draw the pre-write schedule as current — the exact thing
    /// <see cref="MarkStale"/> exists to stop. So the read says when it
    /// started, and a mark made since then survives it.</para>
    /// </summary>
    public int BeginRead() => _generation;

    /// <summary>
    /// Takes a full read. Its sessions are always kept — they are newer than
    /// what was held — but the stale flag is cleared only if nothing marked the
    /// cache stale after <paramref name="readStartedAt"/> was taken.
    /// </summary>
    /// <param name="readStartedAt">What <see cref="BeginRead"/> returned before the read was sent.</param>
    public void Replace(PagedResult<PanoptoSession> read, int readStartedAt)
    {
        _sessions = read.Items.ToList();
        Complete = read.Complete;
        HasData = true;
        if (readStartedAt == _generation) _stale = false;
        ReadAtUtc = _utcNow();
        Changed?.Invoke();
    }

    /// <summary>A write whose fate is unknown, or one this cache cannot mirror truthfully.</summary>
    public void MarkStale()
    {
        _stale = true;
        _generation++;
    }

    public void Clear()
    {
        _sessions = [];
        HasData = false;
        Complete = false;
        ReadAtUtc = null;
        Changed?.Invoke();
    }

    public bool PatchTime(Guid id, DateTime start, TimeSpan duration)
        => Patch(id, s => s.ApplyReschedule(start, duration));

    public bool PatchName(Guid id, string name) => Patch(id, s => s.SessionName = name);

    public bool PatchDescription(Guid id, string? description) => Patch(id, s => s.Description = description);

    public bool PatchBroadcast(Guid id, bool isBroadcast) => Patch(id, s => s.IsBroadcast = isBroadcast);

    public int Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        var removed = _sessions.RemoveAll(s => Guid.TryParse(s.SessionID, out var id) && set.Contains(id));
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    private bool Patch(Guid id, Action<PanoptoSession> change)
    {
        var session = _sessions.FirstOrDefault(s => Guid.TryParse(s.SessionID, out var sid) && sid == id);
        if (session is null) return false;
        change(session);
        Changed?.Invoke();
        return true;
    }
}
