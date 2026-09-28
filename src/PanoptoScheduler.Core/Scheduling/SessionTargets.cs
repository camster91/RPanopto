using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Scheduling;

/// <summary>The bulk tools' view of a cached session; null when it has no usable id.</summary>
public static class SessionTargets
{
    public static SessionTarget? From(PanoptoSession s)
        => Guid.TryParse(s.SessionID, out var id)
            ? new SessionTarget(id, s.SessionName ?? "(untitled)", s.EffectiveStart, s.EffectiveEnd, s.Description)
            : null;
}
