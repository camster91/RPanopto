# AV Status, Series Operations and Office Ship — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Panopto Scheduler 1.4.0 so any office user can install it from a network share, sign in with their own Panopto account, see live room status at low API cost, and change whole course series in one previewed run.

**Architecture:** New behaviour lives in `PanoptoScheduler.Core` as pure, unit-tested units (`ScheduleCache`, `CheckpointPlanner`, `RoomStatusEvaluator`, `StatusMonitor`, `SeriesSelector`, `RoomChangePlan`/`RoomChanger`, `ApiCallCounter`, `ShareFeed`). The WPF app wires them in through delegates, so a new `PanoptoScheduler.App.Tests` project can test the new view models without a `PanoptoConnection`. Distribution moves from private GitHub releases + gist to an office share with `latest.json` and an in-app **Update now**.

**Tech Stack:** .NET 9, WPF (+ WinForms `NotifyIcon` for the tray), xUnit 2.9, PowerShell 5.1 install scripts, PowerShell 7 publish/release scripts.

**Spec:** `docs/superpowers/specs/2026-09-28-av-status-and-series-design.md` — read it, **including the Amendments section at the end**, before any task.

## Global Constraints

- Target release **1.4.0**: `<Version>1.4.0</Version>`, `<AssemblyVersion>1.4.0.0</AssemblyVersion>`, `<FileVersion>1.4.0.0</FileVersion>` in `src\PanoptoScheduler.App\PanoptoScheduler.App.csproj`.
- **No background API polling.** The only automatic calls are checkpoint `ListRecorders` reads (start − 10 min and start + 3 min, today only, distinct times). Moving between weeks makes **zero** calls.
- Cache re-read rules, exactly: (1) Refresh, (2) sign-in, (3) first action after **15 minutes**, (4) before a bulk series run. A `ProblemText.IsTimeout` write result marks the cache stale.
- Room times are the room's wall clock (`RoomClock`, zone from `PanoptoConnection.RoomZone`). Never compare room wall clock with `DateTime.Now`.
- Status mapping: Recording/Paused → Recording; Stopped/Previewing/RecorderRunning → Idle; Disconnected/Faulted → Offline; else Unknown. Late = session's recorder not Recording at start + 3 min. **Unknown never renders green.**
- Change room: book first, delete **only** originals whose new booking succeeded, in one batched delete through `BulkSessionEditor.DeleteAsync` with a `DestructiveAction` permit.
- Preview before every bulk write; nothing written until confirmed; per-row JSONL audit; keep *did not go* separate from *may have gone*.
- Install scripts stay **pure ASCII** (Windows PowerShell 5.1 reads BOM-less files as ANSI).
- The share never holds the unzipped build outside a versioned folder; `latest.json` is written **last**.
- No new NuGet packages.
- Builds: run `dotnet` from the **PowerShell** tool with the sandbox disabled (WPF build needs it). `dotnet test` may kill the shell — always redirect: `dotnet test src\PanoptoScheduler.sln *> src\test-results.log` and read the log's last lines for `Passed!`/`Failed!`.

## Review Focus

1. **Laptop sleeps through checkpoints** — on wake, the monitor must not fire a burst of calls for every missed checkpoint; at most one check, and none if the newest missed checkpoint is over 5 minutes old. (Test: `StatusMonitorTests.Wake_after_sleep_runs_at_most_one_check`.)
2. **Share unreachable (off-VPN, share down)** — the update check must not freeze startup; it gives up after 5 s and shows no banner. (Test: `ShareFeedTests.Unreachable_share_returns_null_within_timeout`.)
3. **User without Panopto write rights on a folder** — a change-room or shift run must report per-row refusals from Panopto, not crash or claim success; the originals stay. (Test: `RoomChangerTests.Booking_throws_keeps_original_and_reports_not_booked`.)
4. **Change room where the delete fails after bookings succeeded** — the report must say "new booking exists, original not removed — duplicate", not "moved". (Test: `RoomChangerTests.Delete_failure_reports_duplicates`.)
5. **Session edited on the Panopto website between loads** — a checkpoint uses the cache, so a session booked elsewhere after the last read has no checkpoint; the status line's "Schedule as of hh:mm" makes the age visible, and the first action after 15 minutes re-reads. (Test: `ScheduleCacheTests.Stale_after_fifteen_minutes`.)

---

## File Structure

**Core (new):**
- `src/PanoptoScheduler.Core/Diagnostics/ApiCallCounter.cs` — per-day, per-endpoint call counts.
- `src/PanoptoScheduler.Core/Scheduling/ScheduleCache.cs` — the one cached scheduled set + staleness + patches.
- `src/PanoptoScheduler.Core/Scheduling/SessionTargets.cs` — `PanoptoSession` → `SessionTarget`.
- `src/PanoptoScheduler.Core/Status/RoomState.cs` — `RoomState`, `RoomStatus`, `StatusSnapshot`, `StatusAlert`, `RecorderStateMap`.
- `src/PanoptoScheduler.Core/Status/RoomStatusEvaluator.cs` — recorders + sessions + now → room states + alerts.
- `src/PanoptoScheduler.Core/Status/CheckpointPlanner.cs` — a day's sessions → checkpoint times.
- `src/PanoptoScheduler.Core/Status/StatusMonitor.cs` — schedules checkpoints, reads, evaluates, raises events.
- `src/PanoptoScheduler.Core/Scheduling/SeriesSelector.cs` — folder series from the cache.
- `src/PanoptoScheduler.Core/Scheduling/RoomChange.cs` — `RoomChangePlan` + `RoomChanger`.
- `src/PanoptoScheduler.Core/Updates/ShareFeed.cs` — `latest.json` on the share.

**Core (modified):** `RateLimiting/EndpointRateLimiter.cs`, `RateLimiting/RateLimiterRegistry.cs`, `Configuration/CredentialStore.cs` (+`UpdateShare`), `Updates/VersionFeed.cs` (gist read removed, `IsNewer` kept).

**App (new):** `ViewModels/StatusStripViewModel.cs`, `TrayNotifier.cs`, `ViewModels/SeriesViewModel.cs`, `Updater.cs`.
**App (modified):** `PanoptoScheduler.App.csproj`, `ViewModels/CalendarViewModel.cs`, `MainWindow.xaml`, `BulkWindow.xaml`, `ViewModels/BulkWindowViewModel.cs`, `App.xaml.cs`, `SignInDialog.xaml`.
**Tests (new):** `src/PanoptoScheduler.App.Tests/` project; new test files in `src/PanoptoScheduler.Core.Tests/`.
**Probe:** `src/PanoptoScheduler.Probe/Program.cs` (+ two read-only commands).
**Scripts:** `src/package/install.ps1` (+ `-WaitForPid`, `-Relaunch`), new `src/deploy-share.ps1`, `src/release.ps1` (gist → share), `src/publish.ps1` (+ `updateShare` key).
**Docs:** `README.md`, new `src/package/How to install.txt`, `AV-ALLOWLIST.md`.

---

### Task 1: Probe commands (measure first)

**Files:**
- Modify: `src/PanoptoScheduler.Probe/Program.cs` (flag dispatch near line 111; usage text in `PrintUsage`)

**Interfaces:**
- Consumes: `PanoptoConnection` as the probe already builds it; `connection.Recorders.ListRecordersAsync()`; the probe's existing authenticated `HttpClient` path used by `RestProbePath`.
- Produces: console output only. Nothing later compiles against it.

- [ ] **Step 1: Add `--status-now`.** After the existing `--dump-raw` block, add:

```csharp
if (args.Contains("--status-now"))
{
    // One SOAP ListRecorders call: the State strings this app will map.
    var recorders = await connection.Recorders.ListRecordersAsync();
    Console.WriteLine($"ListRecorders: {recorders.Count} recorder(s), complete={recorders.Complete}");
    foreach (var r in recorders.OrderBy(r => r.Name))
        Console.WriteLine($"  {r.Name,-40} State={r.State ?? "(null)"}");

    // One REST call, for the record only (Amendment 1): which ids it carries.
    using var request = new HttpRequestMessage(HttpMethod.Get, "/Panopto/api/v1/sessions/inProgress/recording");
    await connection.Auth.ApplyAsync(request);
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"inProgress/recording: {(int)response.StatusCode}");
    Console.WriteLine(body.Length > 1500 ? body[..1500] + "…" : body);
    return 0;
}
```

(`http` is the tenant-based `HttpClient` the probe already creates for `RestProbePath`; if it is named differently in `Main`, use that variable.)

- [ ] **Step 2: Add `--current-schedule`.**

```csharp
if (args.Contains("--current-schedule"))
{
    using var request = new HttpRequestMessage(HttpMethod.Get,
        "/Panopto/api/v1/scheduledRecordings/bulk/downloadResources/currentSchedule");
    await connection.Auth.ApplyAsync(request);
    using var response = await http.SendAsync(request);
    Console.WriteLine($"currentSchedule: {(int)response.StatusCode} {response.Content.Headers.ContentType}");
    var csv = await response.Content.ReadAsStringAsync();
    var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Console.WriteLine($"rows (incl. header): {lines.Length}");
    foreach (var line in lines.Take(4)) Console.WriteLine("  " + line.TrimEnd('\r'));

    var dataSvc = await connection.Reads.GetAllSessionsAsync([1]);
    var ids = dataSvc.Items.SelectMany(s => new[] { s.SessionID, s.DeliveryID })
        .Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var overlap = lines.Skip(1).Count(l => ids.Any(id => l.Contains(id!, StringComparison.OrdinalIgnoreCase)));
    Console.WriteLine($"Data.svc scheduled: {dataSvc.Count}; CSV rows containing a Data.svc id: {overlap}");
    return 0;
}
```

- [ ] **Step 3: Add both flags to `PrintUsage`** as read-only lines: `--status-now  One ListRecorders + one inProgress read; prints states and ids.` and `--current-schedule  One currentSchedule.csv download; prints columns and id overlap.`

- [ ] **Step 4: Build.** `dotnet build src\PanoptoScheduler.sln` → `Build succeeded`.

- [ ] **Step 5: HAND-OFF — the user runs it.** Ask the user to run, in their own terminal:
  `! dotnet run --project src\PanoptoScheduler.Probe -- --status-now` and `! dotnet run --project src\PanoptoScheduler.Probe -- --current-schedule`.
  Record the State strings actually seen and the CSV header in the spec's Amendments section as "Measured 2026-…". **If any State string is not in the mapping list, add it to `RecorderStateMap` in Task 4 before writing that task's tests.** If `ListRecorders` returns `State=(null)` for every recorder, STOP and tell the user: status then needs the REST path and the design must be revisited.

- [ ] **Step 6: Commit.**

```bash
git add src/PanoptoScheduler.Probe/Program.cs docs/superpowers/specs/2026-09-28-av-status-and-series-design.md
git commit -m "Probe: read-only --status-now and --current-schedule measurements"
```

---

### Task 2: ApiCallCounter, counted at the limiter

**Files:**
- Create: `src/PanoptoScheduler.Core/Diagnostics/ApiCallCounter.cs`
- Modify: `src/PanoptoScheduler.Core/RateLimiting/EndpointRateLimiter.cs` (ctor + end of `WaitAsync`)
- Modify: `src/PanoptoScheduler.Core/RateLimiting/RateLimiterRegistry.cs`
- Modify: `src/PanoptoScheduler.App/App.xaml.cs` (log summary on exit)
- Test: `src/PanoptoScheduler.Core.Tests/ApiCallCounterTests.cs`

**Interfaces:**
- Produces: `ApiCallCounter` with `ApiCallCounter.Shared`, `Record(string endpoint)`, `int Total`, `IReadOnlyDictionary<string,int> Snapshot()`, `string Summary()`; `EndpointRateLimiter(string endpoint, (int, TimeSpan)[]? limits = null, ApiCallCounter? counter = null)`; `RateLimiterRegistry((int, TimeSpan)[]? limits = null, ApiCallCounter? counter = null)`.

Every real call already passes through exactly one `limiter.WaitAsync` (`DataSvcClient.cs:150`, `LegacyCookieProvider.cs:75`, `PanoptoSoapClient.cs:233`), so counting there counts every request without touching the clients.

- [ ] **Step 1: Write the failing tests.**

```csharp
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.RateLimiting;

namespace PanoptoScheduler.Core.Tests;

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
```

- [ ] **Step 2: Run to see it fail.** `dotnet test src\PanoptoScheduler.sln --filter FullyQualifiedName~ApiCallCounterTests *> src\test-results.log` → compile error, `ApiCallCounter` not found.

- [ ] **Step 3: Implement `ApiCallCounter`.**

```csharp
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
```

- [ ] **Step 4: Wire the limiter.** In `EndpointRateLimiter`: add field `private readonly ApiCallCounter _counter;`, extend the ctor to `public EndpointRateLimiter(string endpoint, (int Limit, TimeSpan Window)[]? limits = null, ApiCallCounter? counter = null)` and set `_counter = counter ?? ApiCallCounter.Shared;`. In `WaitAsync`, at the point where the call is recorded and the method returns (the branch that enqueues the hit and exits the loop), add `_counter.Record(Endpoint);` immediately before the `return`. Add `using PanoptoScheduler.Core.Diagnostics;`.
  In `RateLimiterRegistry`: add `private readonly ApiCallCounter? _counter;`, ctor `public RateLimiterRegistry((int Limit, TimeSpan Window)[]? limits = null, ApiCallCounter? counter = null)` storing both, and `For` → `new EndpointRateLimiter(e, _limits, _counter)`.

- [ ] **Step 5: Log on exit.** In `App.xaml.cs` `OnExit` (create the override if absent, calling `base.OnExit(e)` last): `AppLog.Info(ApiCallCounter.Shared.Summary());`.

- [ ] **Step 6: Run tests** (filter as Step 2, then the full suite) → all pass; full suite count = previous + 3.

- [ ] **Step 7: Commit.** `git commit -am "Count every Panopto request per endpoint per day and log the total"` (add the new files first).

---

### Task 3: ScheduleCache and SessionTargets

**Files:**
- Create: `src/PanoptoScheduler.Core/Scheduling/ScheduleCache.cs`, `src/PanoptoScheduler.Core/Scheduling/SessionTargets.cs`
- Test: `src/PanoptoScheduler.Core.Tests/ScheduleCacheTests.cs`

**Interfaces:**
- Consumes: `PagedResult<PanoptoSession>`, `PanoptoSession.ApplyReschedule(DateTime start, TimeSpan duration)`.
- Produces:
  - `ScheduleCache(Func<DateTime>? utcNow = null)`; `static readonly TimeSpan MaxAge` (15 min); `IReadOnlyList<PanoptoSession> Sessions`; `bool HasData`; `bool Complete`; `DateTime? ReadAtUtc`; `bool NeedsRead`; `void Replace(PagedResult<PanoptoSession> read)`; `void MarkStale()`; `void Clear()`; `bool PatchTime(Guid id, DateTime start, TimeSpan duration)`; `bool PatchName(Guid id, string name)`; `bool PatchDescription(Guid id, string? description)`; `bool PatchBroadcast(Guid id, bool isBroadcast)`; `int Remove(IEnumerable<Guid> ids)`; `event Action? Changed`.
  - `SessionTargets.From(PanoptoSession s) : SessionTarget?` (null when `SessionID` is not a Guid).

- [ ] **Step 1: Write the failing tests.**

```csharp
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
        cache.Replace(new PagedResult<PanoptoSession>(sessions, true, sessions.Length));
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
        cache.Replace(new PagedResult<PanoptoSession>([], true, 0));
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
        cache.Replace(new PagedResult<PanoptoSession>([], false, 900));
        Assert.False(cache.Complete);
    }

    [Fact]
    public void SessionTargets_skips_non_guid_ids()
        => Assert.Null(SessionTargets.From(new PanoptoSession { SessionID = "not-a-guid" }));
}
```

(If `PagedResult`'s constructor parameter order differs from `(items, complete, reportedTotal)`, match `src/PanoptoScheduler.Core/Clients/PagedResult.cs:34`.)

- [ ] **Step 2: Run to see it fail** (filter `ScheduleCacheTests`) → compile error.

- [ ] **Step 3: Implement.**

```csharp
// SessionTargets.cs
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Scheduling;

public static class SessionTargets
{
    /// <summary>The bulk tools' view of a cached session; null when it has no usable id.</summary>
    public static SessionTarget? From(PanoptoSession s)
        => Guid.TryParse(s.SessionID, out var id)
            ? new SessionTarget(id, s.SessionName ?? "(untitled)", s.EffectiveStart, s.EffectiveEnd, s.Description)
            : null;
}
```

```csharp
// ScheduleCache.cs
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

    public event Action? Changed;

    public IReadOnlyList<PanoptoSession> Sessions => _sessions;
    public bool HasData { get; private set; }
    public bool Complete { get; private set; }

    /// <summary>When the last full read landed. Patches do not move it.</summary>
    public DateTime? ReadAtUtc { get; private set; }

    public bool NeedsRead =>
        !HasData || _stale || ReadAtUtc is not { } at || _utcNow() - at > MaxAge;

    public void Replace(PagedResult<PanoptoSession> read)
    {
        _sessions = read.Items.ToList();
        Complete = read.Complete;
        HasData = true;
        _stale = false;
        ReadAtUtc = _utcNow();
        Changed?.Invoke();
    }

    /// <summary>A write whose fate is unknown, or one this cache cannot mirror truthfully.</summary>
    public void MarkStale() => _stale = true;

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
```

- [ ] **Step 4: Run tests** → pass.
- [ ] **Step 5: Commit.** `git add` both files + test; `git commit -m "ScheduleCache: one cached scheduled set with the spec's four re-read rules"`.

---

### Task 4: Room state model, mapping and evaluator

**Files:**
- Create: `src/PanoptoScheduler.Core/Status/RoomState.cs`, `src/PanoptoScheduler.Core/Status/RoomStatusEvaluator.cs`
- Test: `src/PanoptoScheduler.Core.Tests/RoomStatusEvaluatorTests.cs`

**Interfaces:**
- Consumes: `RemoteRecorder` (`Id`, `Name`, `State`), `PanoptoSession` (`RemoteRecorderID`, `RemoteRecorderName`, `EffectiveStart`, `EffectiveEnd`, `SessionID`, `SessionName`).
- Produces:
  - `enum RoomState { Unknown, Idle, Recording, Offline, Late }`
  - `record RoomStatus(Guid RecorderId, string RecorderName, RoomState State, string? SessionName)`
  - `record StatusSnapshot(IReadOnlyList<RoomStatus> Rooms, DateTime CheckedAtRoom, bool Failed)`
  - `record StatusAlert(string Key, string RecorderName, string Message)`
  - `static class RecorderStateMap { RoomState From(string? state) }`
  - `static class RoomStatusEvaluator { static readonly TimeSpan LateAfter; static readonly TimeSpan OfflineWarnBefore; (IReadOnlyList<RoomStatus> Rooms, IReadOnlyList<StatusAlert> Alerts) Evaluate(IReadOnlyList<RemoteRecorder> recorders, IReadOnlyList<PanoptoSession> sessions, DateTime nowRoom) }`

- [ ] **Step 1: Write the failing tests.**

```csharp
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Status;

namespace PanoptoScheduler.Core.Tests;

public class RoomStatusEvaluatorTests
{
    private static readonly Guid Rec = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Sess = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly DateTime Nine = new(2026, 9, 28, 9, 0, 0);

    private static RemoteRecorder Recorder(string? state) => new() { Id = Rec, Name = "Room 101", State = state };

    private static PanoptoSession At(DateTime start) => new()
    {
        SessionID = Sess.ToString("D"), SessionName = "RSM1234 L3",
        RemoteRecorderID = Rec.ToString("D"), RemoteRecorderName = "Room 101",
        StartTime = start, Duration = 5400,
    };

    [Theory]
    [InlineData("Recording", RoomState.Recording)]
    [InlineData("Paused", RoomState.Recording)]
    [InlineData("Stopped", RoomState.Idle)]
    [InlineData("Previewing", RoomState.Idle)]
    [InlineData("RecorderRunning", RoomState.Idle)]
    [InlineData("Disconnected", RoomState.Offline)]
    [InlineData("Faulted", RoomState.Offline)]
    [InlineData("SomethingNew", RoomState.Unknown)]
    [InlineData(null, RoomState.Unknown)]
    public void Maps_recorder_state(string? raw, RoomState expected)
        => Assert.Equal(expected, RecorderStateMap.From(raw));

    [Fact]
    public void Idle_three_minutes_after_start_is_late_and_alerts()
    {
        var (rooms, alerts) = RoomStatusEvaluator.Evaluate([Recorder("Stopped")], [At(Nine)], Nine.AddMinutes(3));
        Assert.Equal(RoomState.Late, rooms[0].State);
        Assert.Equal($"late|{Sess:D}", Assert.Single(alerts).Key);
    }

    [Fact]
    public void Idle_two_minutes_after_start_is_not_late_yet()
    {
        var (rooms, alerts) = RoomStatusEvaluator.Evaluate([Recorder("Stopped")], [At(Nine)], Nine.AddMinutes(2));
        Assert.Equal(RoomState.Idle, rooms[0].State);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Recording_during_session_is_fine()
    {
        var (rooms, alerts) = RoomStatusEvaluator.Evaluate([Recorder("Recording")], [At(Nine)], Nine.AddMinutes(10));
        Assert.Equal(RoomState.Recording, rooms[0].State);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Offline_ten_minutes_before_a_session_alerts_once_keyed_by_session()
    {
        var (rooms, alerts) = RoomStatusEvaluator.Evaluate([Recorder("Disconnected")], [At(Nine)], Nine.AddMinutes(-10));
        Assert.Equal(RoomState.Offline, rooms[0].State);
        Assert.Equal($"offline|{Sess:D}", Assert.Single(alerts).Key);
    }

    [Fact]
    public void Offline_with_nothing_booked_soon_does_not_alert()
    {
        var (_, alerts) = RoomStatusEvaluator.Evaluate([Recorder("Disconnected")], [At(Nine)], Nine.AddHours(-3));
        Assert.Empty(alerts);
    }

    [Fact]
    public void Unknown_state_during_a_session_stays_unknown_not_late()
    {
        var (rooms, alerts) = RoomStatusEvaluator.Evaluate([Recorder(null)], [At(Nine)], Nine.AddMinutes(5));
        Assert.Equal(RoomState.Unknown, rooms[0].State);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Session_matched_by_recorder_name_when_id_missing()
    {
        var s = At(Nine); s.RemoteRecorderID = null;
        var (rooms, _) = RoomStatusEvaluator.Evaluate([Recorder("Stopped")], [s], Nine.AddMinutes(4));
        Assert.Equal(RoomState.Late, rooms[0].State);
    }
}
```

- [ ] **Step 2: Run to see it fail** (filter `RoomStatusEvaluatorTests`).

- [ ] **Step 3: Implement.**

```csharp
// RoomState.cs
namespace PanoptoScheduler.Core.Status;

public enum RoomState { Unknown, Idle, Recording, Offline, Late }

public sealed record RoomStatus(Guid RecorderId, string RecorderName, RoomState State, string? SessionName);

/// <param name="Failed">The read behind this snapshot did not succeed; every room is Unknown.</param>
public sealed record StatusSnapshot(IReadOnlyList<RoomStatus> Rooms, DateTime CheckedAtRoom, bool Failed);

/// <param name="Key">Stable per session and kind, so one problem alerts once.</param>
public sealed record StatusAlert(string Key, string RecorderName, string Message);

/// <summary>SOAP <c>RemoteRecorder.State</c> strings → what the strip shows (spec Amendment 1).</summary>
public static class RecorderStateMap
{
    public static RoomState From(string? state) => state?.Trim() switch
    {
        "Recording" or "Paused" => RoomState.Recording,
        "Stopped" or "Previewing" or "RecorderRunning" => RoomState.Idle,
        "Disconnected" or "Faulted" => RoomState.Offline,
        _ => RoomState.Unknown,
    };
}
```

```csharp
// RoomStatusEvaluator.cs
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Status;

public static class RoomStatusEvaluator
{
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan OfflineWarnBefore = TimeSpan.FromMinutes(10);

    /// <param name="nowRoom">The room's wall clock — the same clock the sessions carry.</param>
    public static (IReadOnlyList<RoomStatus> Rooms, IReadOnlyList<StatusAlert> Alerts) Evaluate(
        IReadOnlyList<RemoteRecorder> recorders,
        IReadOnlyList<PanoptoSession> sessions,
        DateTime nowRoom)
    {
        var rooms = new List<RoomStatus>(recorders.Count);
        var alerts = new List<StatusAlert>();

        foreach (var recorder in recorders)
        {
            var mine = sessions.Where(s => BelongsTo(s, recorder) && s.EffectiveStart is not null).ToList();
            var current = mine.FirstOrDefault(s => s.EffectiveStart <= nowRoom && nowRoom < (s.EffectiveEnd ?? s.EffectiveStart));
            var upcoming = mine.Where(s => s.EffectiveStart > nowRoom && s.EffectiveStart - nowRoom <= OfflineWarnBefore)
                               .OrderBy(s => s.EffectiveStart).FirstOrDefault();

            var state = RecorderStateMap.From(recorder.State);

            if (state == RoomState.Idle && current is not null && nowRoom - current.EffectiveStart >= LateAfter)
            {
                state = RoomState.Late;
                alerts.Add(new StatusAlert($"late|{current.SessionID}", recorder.Name,
                    $"{recorder.Name}: \"{current.SessionName}\" has not started recording ({current.EffectiveStart:h:mm tt})."));
            }
            else if (state == RoomState.Offline && (upcoming ?? current) is { } due)
            {
                alerts.Add(new StatusAlert($"offline|{due.SessionID}", recorder.Name,
                    $"{recorder.Name} is offline; \"{due.SessionName}\" is booked at {due.EffectiveStart:h:mm tt}."));
            }

            rooms.Add(new RoomStatus(recorder.Id, recorder.Name, state, (current ?? upcoming)?.SessionName));
        }

        return (rooms, alerts);
    }

    private static bool BelongsTo(PanoptoSession s, RemoteRecorder r)
        => Guid.TryParse(s.RemoteRecorderID, out var id)
            ? id == r.Id
            : string.Equals(s.RemoteRecorderName?.Trim(), r.Name.Trim(), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Run tests** → pass. If Task 1 measured extra State strings, add them to `RecorderStateMap` and one `InlineData` each now.
- [ ] **Step 5: Commit.** `git commit -m "Room status: map recorder State, detect Late and Offline-before-session"`.

---

### Task 5: CheckpointPlanner

**Files:**
- Create: `src/PanoptoScheduler.Core/Status/CheckpointPlanner.cs`
- Test: `src/PanoptoScheduler.Core.Tests/CheckpointPlannerTests.cs`

**Interfaces:**
- Produces: `static class CheckpointPlanner { static readonly TimeSpan MergeWithin; static IReadOnlyList<DateTime> ForDay(IEnumerable<PanoptoSession> sessions, DateOnly day) }` — sorted room-wall-clock times: start − `RoomStatusEvaluator.OfflineWarnBefore` and start + `RoomStatusEvaluator.LateAfter` per distinct start on `day`, merged when within 1 minute (the earlier kept).

- [ ] **Step 1: Write the failing tests.**

```csharp
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Status;

namespace PanoptoScheduler.Core.Tests;

public class CheckpointPlannerTests
{
    private static readonly DateOnly Day = new(2026, 9, 28);
    private static PanoptoSession At(int h, int m, int dayOffset = 0)
        => new() { StartTime = Day.ToDateTime(new TimeOnly(h, m)).AddDays(dayOffset), Duration = 3600 };

    [Fact]
    public void Two_checkpoints_per_distinct_start()
    {
        var times = CheckpointPlanner.ForDay([At(9, 0), At(9, 0), At(14, 0)], Day);
        Assert.Equal(
            new[] { Day.ToDateTime(new TimeOnly(8, 50)), Day.ToDateTime(new TimeOnly(9, 3)), Day.ToDateTime(new TimeOnly(13, 50)), Day.ToDateTime(new TimeOnly(14, 3)) },
            times);
    }

    [Fact]
    public void Other_days_are_ignored()
        => Assert.Empty(CheckpointPlanner.ForDay([At(9, 0, dayOffset: 1)], Day));

    [Fact]
    public void Sessions_without_a_time_are_ignored()
        => Assert.Empty(CheckpointPlanner.ForDay([new PanoptoSession()], Day));

    [Fact]
    public void Checkpoints_within_a_minute_merge()
    {
        // 9:00's post-start (9:03) and 9:13's pre-start (9:03) coincide.
        var times = CheckpointPlanner.ForDay([At(9, 0), At(9, 13)], Day);
        Assert.Equal(3, times.Count);
    }
}
```

- [ ] **Step 2: Run to see it fail.**

- [ ] **Step 3: Implement.**

```csharp
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Status;

/// <summary>
/// When to look at the rooms today — the only automatic reads the app makes
/// (spec §1: two per distinct start time, never a timer poll).
/// </summary>
public static class CheckpointPlanner
{
    public static readonly TimeSpan MergeWithin = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<DateTime> ForDay(IEnumerable<PanoptoSession> sessions, DateOnly day)
    {
        var raw = sessions
            .Select(s => s.EffectiveStart)
            .Where(s => s is { } start && DateOnly.FromDateTime(start) == day)
            .Select(s => s!.Value)
            .Distinct()
            .SelectMany(start => new[] { start - RoomStatusEvaluator.OfflineWarnBefore, start + RoomStatusEvaluator.LateAfter })
            .Order()
            .ToList();

        var merged = new List<DateTime>(raw.Count);
        foreach (var t in raw)
            if (merged.Count == 0 || t - merged[^1] > MergeWithin) merged.Add(t);
        return merged;
    }
}
```

- [ ] **Step 4: Run tests** → pass.
- [ ] **Step 5: Commit.** `git commit -m "CheckpointPlanner: today's status checks from distinct start times"`.

---

### Task 6: StatusMonitor

**Files:**
- Create: `src/PanoptoScheduler.Core/Status/StatusMonitor.cs`
- Test: `src/PanoptoScheduler.Core.Tests/StatusMonitorTests.cs`

**Interfaces:**
- Consumes: `CheckpointPlanner.ForDay`, `RoomStatusEvaluator.Evaluate`, `RemoteRecorder`.
- Produces:

```csharp
public sealed class StatusMonitor(
    Func<CancellationToken, Task<IReadOnlyList<RemoteRecorder>>> readRecorders,
    Func<IReadOnlyList<PanoptoSession>> sessions,
    Func<DateTime> nowRoom,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    public static readonly TimeSpan MaxSleepSlice;   // 60 s — re-reads the clock, survives sleep
    public static readonly TimeSpan MissedGrace;     // 5 min
    public event Action<StatusSnapshot>? Updated;
    public event Action<StatusAlert>? Alert;
    public StatusSnapshot? Last { get; }
    public Task CheckNowAsync(CancellationToken ct = default);
    public Task RunAsync(CancellationToken ct);
}
```

- [ ] **Step 1: Write the failing tests.** A fake clock whose `Delay` advances time instantly.

```csharp
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Status;

namespace PanoptoScheduler.Core.Tests;

public class StatusMonitorTests
{
    private sealed class Clock(DateTime start)
    {
        public DateTime Now = start;
        public Task Delay(TimeSpan by, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Now += by;
            return Task.CompletedTask;
        }
    }

    private static readonly Guid Rec = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTime Nine = new(2026, 9, 28, 9, 0, 0);

    private static PanoptoSession Session(DateTime start) => new()
    {
        SessionID = Guid.NewGuid().ToString("D"), SessionName = "L", RemoteRecorderID = Rec.ToString("D"),
        StartTime = start, Duration = 3600,
    };

    [Fact]
    public async Task One_read_per_checkpoint_and_none_between()
    {
        var s = Session(Nine);
        var clock = new Clock(Nine.AddHours(-1));
        var reads = 0;
        using var cts = new CancellationTokenSource();
        var monitor = new StatusMonitor(
            _ => { reads++; if (clock.Now >= Nine.AddMinutes(3)) cts.Cancel();
                   return Task.FromResult<IReadOnlyList<RemoteRecorder>>([new() { Id = Rec, Name = "R", State = "Recording" }]); },
            () => [s], () => clock.Now, clock.Delay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(cts.Token));

        Assert.Equal(2, reads); // 8:50 and 9:03
    }

    [Fact]
    public async Task Wake_after_sleep_runs_at_most_one_check()
    {
        var s = Session(Nine);
        var clock = new Clock(Nine.AddHours(-1));
        var reads = 0;
        using var cts = new CancellationTokenSource();
        Func<TimeSpan, CancellationToken, Task> sleepyDelay = (by, ct) =>
        {
            // First wait: the laptop sleeps until 9:05 — both checkpoints passed.
            if (clock.Now < Nine) clock.Now = Nine.AddMinutes(5); else cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var monitor = new StatusMonitor(
            _ => { reads++; return Task.FromResult<IReadOnlyList<RemoteRecorder>>([]); },
            () => [s], () => clock.Now, sleepyDelay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(cts.Token));

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Missed_checkpoint_older_than_grace_is_skipped()
    {
        var s = Session(Nine);
        var clock = new Clock(Nine.AddHours(-1));
        var reads = 0;
        using var cts = new CancellationTokenSource();
        Func<TimeSpan, CancellationToken, Task> sleepyDelay = (by, ct) =>
        {
            if (clock.Now < Nine) clock.Now = Nine.AddMinutes(30); else cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var monitor = new StatusMonitor(
            _ => { reads++; return Task.FromResult<IReadOnlyList<RemoteRecorder>>([]); },
            () => [s], () => clock.Now, sleepyDelay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(cts.Token));

        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task Failed_read_publishes_an_unknown_snapshot_and_does_not_retry()
    {
        var s = Session(Nine);
        var clock = new Clock(Nine.AddMinutes(3));
        var reads = 0;
        var monitor = new StatusMonitor(
            _ => { reads++; throw new HttpRequestException("down"); },
            () => [s], () => clock.Now, clock.Delay);
        StatusSnapshot? seen = null;
        monitor.Updated += snapshot => seen = snapshot;

        await monitor.CheckNowAsync();

        Assert.Equal(1, reads);
        Assert.True(seen!.Failed);
    }

    [Fact]
    public async Task Same_alert_is_raised_once_per_day()
    {
        var s = Session(Nine);
        var clock = new Clock(Nine.AddMinutes(4));
        var monitor = new StatusMonitor(
            _ => Task.FromResult<IReadOnlyList<RemoteRecorder>>([new() { Id = Rec, Name = "R", State = "Stopped" }]),
            () => [s], () => clock.Now, clock.Delay);
        var alerts = 0;
        monitor.Alert += _ => alerts++;

        await monitor.CheckNowAsync();
        await monitor.CheckNowAsync();

        Assert.Equal(1, alerts);
    }
}
```

Each test declares `var s = Session(Nine);` first and passes `() => [s]`, so the session id is stable across calls (the once-per-day alert key depends on it).

- [ ] **Step 2: Run to see it fail.**

- [ ] **Step 3: Implement.**

```csharp
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Status;

/// <summary>
/// Looks at the rooms at today's checkpoints and nowhere else (spec §1).
///
/// <para><b>Waits in slices of at most a minute and re-reads the clock.</b> A
/// single long delay does not advance while the machine sleeps, so a laptop
/// closed at 8:40 and opened at 9:05 would otherwise either miss the 9:03
/// check entirely or fire every missed one at once. Slicing is free — no call
/// is made on wake unless a checkpoint is due — and a missed checkpoint older
/// than <see cref="MissedGrace"/> is dropped, because its answer is history.</para>
/// </summary>
public sealed class StatusMonitor(
    Func<CancellationToken, Task<IReadOnlyList<RemoteRecorder>>> readRecorders,
    Func<IReadOnlyList<PanoptoSession>> sessions,
    Func<DateTime> nowRoom,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    public static readonly TimeSpan MaxSleepSlice = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _alertDay;

    public event Action<StatusSnapshot>? Updated;
    public event Action<StatusAlert>? Alert;

    public StatusSnapshot? Last { get; private set; }

    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        var now = nowRoom();
        try
        {
            var recorders = await readRecorders(ct).ConfigureAwait(false);
            var (rooms, alerts) = RoomStatusEvaluator.Evaluate(recorders, sessions(), now);
            Publish(new StatusSnapshot(rooms, now, Failed: false));

            var today = DateOnly.FromDateTime(now);
            if (today != _alertDay) { _alerted.Clear(); _alertDay = today; }
            foreach (var alert in alerts)
                if (_alerted.Add(alert.Key)) Alert?.Invoke(alert);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Not retried: the next checkpoint or a Refresh tries again (spec §1).
            AppLog.Warn($"Room status check failed: {ex.Message}");
            var unknown = (Last?.Rooms ?? []).Select(r => r with { State = RoomState.Unknown }).ToList();
            Publish(new StatusSnapshot(unknown, now, Failed: true));
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var lastChecked = DateTime.MinValue;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = nowRoom();
            var today = DateOnly.FromDateTime(now);
            var plan = CheckpointPlanner.ForDay(sessions(), today);

            var due = plan.Where(t => t <= now && t > lastChecked).ToList();
            if (due.Count > 0)
            {
                lastChecked = due[^1];
                if (now - due[^1] <= MissedGrace) await CheckNowAsync(ct).ConfigureAwait(false);
                continue;
            }

            var next = plan.FirstOrDefault(t => t > now);
            var until = next == default ? today.AddDays(1).ToDateTime(TimeOnly.MinValue) - now : next - now;
            await delay(until < MaxSleepSlice ? until : MaxSleepSlice, ct).ConfigureAwait(false);
        }
    }

    private void Publish(StatusSnapshot snapshot)
    {
        Last = snapshot;
        Updated?.Invoke(snapshot);
    }
}
```

- [ ] **Step 4: Run tests** → pass. If `One_read_per_checkpoint_and_none_between` sees 0 reads, check that `due` uses `t <= now` — the fake delay lands exactly on the checkpoint.
- [ ] **Step 5: Commit.** `git commit -m "StatusMonitor: checkpoint-only room checks that survive sleep and never retry-loop"`.

---

### Task 7: Calendar on the cache (the API diet in the app)

**Files:**
- Modify: `src/PanoptoScheduler.App/ViewModels/CalendarViewModel.cs` — `LoadAsync` (~1918), `ShiftWeekAsync` (~1869), `GoToThisWeekAsync` (~1886), `ReloadSelectedAsync` (~987), the drag-timeout path (~1803), `SignOut` handling, status text.
- Modify: `src/PanoptoScheduler.App/MainWindow.xaml` — status line (~161).

**Interfaces:**
- Consumes: `ScheduleCache` (Task 3).
- Produces: `CalendarViewModel.Cache : ScheduleCache` (public get), `Task<bool> EnsureFreshAsync(bool force = false)` (public — Tasks 9 and 11 call it), `string ScheduleAge` (bound in XAML).

- [ ] **Step 1: Add the cache.** Field `private readonly ScheduleCache _cache = new();` and `public ScheduleCache Cache => _cache;`. Replace every read of the `_sessions` field with `_cache.Sessions` and delete `_sessions` and `_sessionsComplete` (use `_cache.Complete`).

- [ ] **Step 2: Split reading from drawing.** Rename the read half of `LoadAsync` to `EnsureFreshAsync`:

```csharp
/// <summary>
/// Reads the scheduled set only when the cache says it must (spec §0 rules 1–4).
/// </summary>
/// <param name="force">Refresh, sign-in, and the pre-run read of a bulk series run.</param>
public async Task<bool> EnsureFreshAsync(bool force = false)
{
    if (!force && !_cache.NeedsRead) return true;
    var read = await _panopto.Reads.GetAllSessionsAsync([1]);
    _cache.Replace(read);
    Raise(nameof(ScheduleAge));
    return true;
}
```

`LoadAsync(bool jumpToFirst = true, bool force = false)` then calls `await EnsureFreshAsync(force)` where it used to call `GetAllSessionsAsync` and keeps everything after it (rooms, jump, `Rebuild`, status) unchanged. Its existing `catch` blocks stay around the whole body.

- [ ] **Step 3: Choose `force` per caller.**
  - `RefreshCommand` → `LoadAsync(force: true)`.
  - Sign-in completion (the `LoadAsync()` calls at ~1410 and ~1439) → `LoadAsync(force: true)`.
  - `ShiftWeekAsync` / `GoToThisWeekAsync` → keep `LoadAsync()` (no force): with a fresh cache this makes **zero** calls; after 15 minutes it re-reads once (rule 3).
  - `ReloadSelectedAsync` (after single edits) → replace the unconditional re-read with a patch where one exists and a forced read otherwise. At each single-edit success site, call the matching patch before `ReloadSelectedAsync`: rename → `_cache.PatchName(id, newName)`; description → `_cache.PatchDescription(id, text)`; broadcast → `_cache.PatchBroadcast(id, value)`; retime → `_cache.PatchTime(id, start, end - start)`; delete → `_cache.Remove([id])`; folder move → `_cache.MarkStale()`. Then `ReloadSelectedAsync` calls `LoadAsync(jumpToFirst: false)` (not forced): it re-reads only when the cache was marked stale.
  - The drag-timeout path (~1803) and any other `ProblemText.IsTimeout` catch → `_cache.MarkStale();` before its existing `LoadAsync(jumpToFirst: false)`.
  - Sign-out → `_cache.Clear();`.

- [ ] **Step 4: Show the age.** Add

```csharp
public string ScheduleAge => _cache.ReadAtUtc is { } at
    ? $"Schedule as of {at.ToLocalTime():h:mm tt}"
    : "";
```

and in `MainWindow.xaml` beside the existing `Status` TextBlock (~161) add `<TextBlock Text="{Binding ScheduleAge}" Margin="0,0,12,0" Foreground="{DynamicResource MutedText}" />` (use the muted brush key the file already uses for `Detail`).

- [ ] **Step 5: Build and run the whole suite** → `Build succeeded`, all tests pass (no Core behaviour changed).

- [ ] **Step 6: Real run, counting calls.** Launch `src\PanoptoScheduler.App\bin\Debug\net9.0-windows\PanoptoScheduler.App.exe` with `Start-Process` (memory: `& $exe` does not wait for a WinExe), sign in, click next week five times, previous week five times, close. Read the day's log: the `API calls …` line shows **one** `GetSessions` read (per page), not eleven. Paste the line into the task report.

- [ ] **Step 7: Commit.** `git commit -am "Calendar reads once and navigates from the cache; patches single edits"`.

---

### Task 8: App.Tests project, status strip and tray alerts

**Files:**
- Create: `src/PanoptoScheduler.App.Tests/PanoptoScheduler.App.Tests.csproj`, `src/PanoptoScheduler.App.Tests/StatusStripViewModelTests.cs`
- Create: `src/PanoptoScheduler.App/ViewModels/StatusStripViewModel.cs`, `src/PanoptoScheduler.App/TrayNotifier.cs`
- Modify: `src/PanoptoScheduler.App/PanoptoScheduler.App.csproj` (`UseWindowsForms`, `InternalsVisibleTo`), `src/PanoptoScheduler.sln`, `CalendarViewModel.cs` (own a monitor), `MainWindow.xaml` (strip), `MainWindow.xaml.cs` (tray), `.github/workflows/build.yml` (tests run on the solution already — confirm no change needed).

**Interfaces:**
- Consumes: `StatusMonitor`, `StatusSnapshot`, `StatusAlert`, `RoomState` (Tasks 4, 6); `CalendarViewModel.Cache` (Task 7).
- Produces: `StatusStripViewModel(StatusMonitor monitor, Action<Action> dispatch)` with `ObservableCollection<RoomChipViewModel> Rooms`, `string Summary`, `AsyncRelayCommand CheckNowCommand`; `RoomChipViewModel { string Name; string StateText; string Tone; string? Session }` (`Tone` ∈ `"ok"`, `"idle"`, `"bad"`, `"unknown"`); `TrayNotifier : IDisposable` with `Show(string title, string text)` and `event Action? Clicked`.

- [ ] **Step 1: Create the test project.**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.2" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\PanoptoScheduler.App\PanoptoScheduler.App.csproj" />
    <ProjectReference Include="..\PanoptoScheduler.Core\PanoptoScheduler.Core.csproj" />
  </ItemGroup>
</Project>
```

Run `dotnet sln src\PanoptoScheduler.sln add src\PanoptoScheduler.App.Tests\PanoptoScheduler.App.Tests.csproj`. (Same package versions as `PanoptoScheduler.Core.Tests.csproj`: no new packages.)

- [ ] **Step 2: App csproj.** Add `<UseWindowsForms>true</UseWindowsForms>` beside `<UseWPF>true</UseWPF>`, and — so `Application`, `MessageBox` etc. stay WPF's — an item group:

```xml
<ItemGroup>
  <Using Remove="System.Windows.Forms" />
  <Using Remove="System.Drawing" />
  <InternalsVisibleTo Include="PanoptoScheduler.App.Tests" />
</ItemGroup>
```

Build. If any file now reports an ambiguous type, qualify it with `System.Windows.` — do not remove `UseWindowsForms`.

- [ ] **Step 3: Write the failing tests.**

```csharp
using PanoptoScheduler.App.ViewModels;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Status;

namespace PanoptoScheduler.App.Tests;

public class StatusStripViewModelTests
{
    private static readonly DateTime Nine = new(2026, 9, 28, 9, 0, 0);

    private static (StatusMonitor, StatusStripViewModel) Build(Func<IReadOnlyList<RemoteRecorder>> recorders)
    {
        var monitor = new StatusMonitor(_ => Task.FromResult(recorders()), () => [], () => Nine, (_, _) => Task.CompletedTask);
        return (monitor, new StatusStripViewModel(monitor, a => a()));
    }

    [Fact]
    public async Task One_chip_per_room_with_tone()
    {
        var (_, vm) = Build(() => [
            new() { Id = Guid.NewGuid(), Name = "Room 101", State = "Recording" },
            new() { Id = Guid.NewGuid(), Name = "Room 102", State = "Disconnected" },
        ]);

        await vm.CheckNowCommand.ExecuteAsync();

        Assert.Equal(new[] { "ok", "bad" }, vm.Rooms.Select(r => r.Tone));
        Assert.Equal("Rooms checked 9:00 AM", vm.Summary);
    }

    [Fact]
    public async Task Failed_check_says_unknown_and_never_green()
    {
        var calls = 0;
        var (_, vm) = Build(() => ++calls == 1
            ? [new() { Id = Guid.NewGuid(), Name = "Room 101", State = "Recording" }]
            : throw new HttpRequestException("down"));

        await vm.CheckNowCommand.ExecuteAsync();
        await vm.CheckNowCommand.ExecuteAsync();

        Assert.All(vm.Rooms, r => Assert.Equal("unknown", r.Tone));
        Assert.Equal("Status unknown (checked 9:00 AM)", vm.Summary);
    }
}
```

(If `AsyncRelayCommand` in `ViewModels/Mvvm.cs` has no awaitable `ExecuteAsync`, add `public Task ExecuteAsync(object? parameter = null)` there that runs the same body `Execute` runs, and have `Execute` call it — the tests need to await it.)

- [ ] **Step 4: Run to see it fail** (`--filter FullyQualifiedName~StatusStripViewModelTests`).

- [ ] **Step 5: Implement the view model.**

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using PanoptoScheduler.Core.Status;

namespace PanoptoScheduler.App.ViewModels;

public sealed class RoomChipViewModel
{
    public required string Name { get; init; }
    public required string StateText { get; init; }
    public required string Tone { get; init; }
    public string? Session { get; init; }
}

/// <summary>The strip of room chips above the calendar (spec §1).</summary>
public sealed class StatusStripViewModel : ObservableObject
{
    private readonly StatusMonitor _monitor;
    private readonly Action<Action> _dispatch;
    private string _summary = "";

    public StatusStripViewModel(StatusMonitor monitor, Action<Action> dispatch)
    {
        _monitor = monitor;
        _dispatch = dispatch;
        _monitor.Updated += s => _dispatch(() => Show(s));
        CheckNowCommand = new AsyncRelayCommand(() => _monitor.CheckNowAsync());
    }

    public ObservableCollection<RoomChipViewModel> Rooms { get; } = [];
    public AsyncRelayCommand CheckNowCommand { get; }

    public string Summary
    {
        get => _summary;
        private set { _summary = value; Raise(); }
    }

    private void Show(StatusSnapshot snapshot)
    {
        Rooms.Clear();
        foreach (var room in snapshot.Rooms.OrderBy(r => r.RecorderName, StringComparer.CurrentCultureIgnoreCase))
            Rooms.Add(new RoomChipViewModel
            {
                Name = room.RecorderName,
                StateText = room.State.ToString(),
                Tone = room.State switch
                {
                    RoomState.Recording => "ok",
                    RoomState.Idle => "idle",
                    RoomState.Offline or RoomState.Late => "bad",
                    _ => "unknown",
                },
                Session = room.SessionName,
            });

        Summary = snapshot.Failed
            ? $"Status unknown (checked {snapshot.CheckedAtRoom.ToString("h:mm tt", CultureInfo.InvariantCulture)})"
            : $"Rooms checked {snapshot.CheckedAtRoom.ToString("h:mm tt", CultureInfo.InvariantCulture)}";
    }
}
```

(Use the base class and `Raise` helper names exactly as `ViewModels/Mvvm.cs` defines them; `CalendarViewModel` derives from `ObservableObject` and calls `Raise(nameof(...))`.)

- [ ] **Step 6: Run tests** → pass.

- [ ] **Step 7: Tray notifier.**

```csharp
using System.Drawing;
using Forms = System.Windows.Forms;

namespace PanoptoScheduler.App;

/// <summary>
/// The one place the app speaks while it is not in front: a status alert
/// (spec §1). Shows only what a checkpoint found; never polls.
/// </summary>
public sealed class TrayNotifier : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayNotifier()
    {
        _icon = new Forms.NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = "Panopto Scheduler",
            Visible = true,
        };
        _icon.BalloonTipClicked += (_, _) => Clicked?.Invoke();
        _icon.DoubleClick += (_, _) => Clicked?.Invoke();
    }

    public event Action? Clicked;

    public void Show(string title, string text) => _icon.ShowBalloonTip(10_000, title, text, Forms.ToolTipIcon.Warning);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
```

- [ ] **Step 8: Wire it.** In `CalendarViewModel`: after a successful sign-in load, create

```csharp
_monitor = new StatusMonitor(
    async ct => (await _panopto.Recorders.ListRecordersAsync(ct)).Items,
    () => _cache.Sessions,
    () => RoomClock.Now(_roomZone),
    Task.Delay);
StatusStrip = new StatusStripViewModel(_monitor, a => System.Windows.Application.Current.Dispatcher.Invoke(a));
_monitorCts = new CancellationTokenSource();
_ = RunMonitorAsync(_monitorCts.Token);
```

with `public StatusStripViewModel? StatusStrip { get; private set; }` (raise on set), `public event Action<StatusAlert>? StatusAlert;` forwarded from `_monitor.Alert`, and

```csharp
private async Task RunMonitorAsync(CancellationToken ct)
{
    try { await _monitor!.RunAsync(ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { }
    catch (Exception ex) { AppLog.Error("Room status monitor stopped.", ex); }
}
```

On sign-out: `_monitorCts?.Cancel(); StatusStrip = null;`. In `RefreshCommand`'s body, after `LoadAsync(force: true)`, call `await (StatusStrip?.CheckNowCommand.ExecuteAsync() ?? Task.CompletedTask);` — the one extra call a manual refresh costs.
In `MainWindow.xaml.cs`: create one `TrayNotifier` in the constructor, subscribe `vm.StatusAlert += a => Dispatcher.Invoke(() => _tray.Show("Room needs attention", a.Message));`, `_tray.Clicked += () => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });`, dispose it in `OnClosed`.

- [ ] **Step 9: Strip XAML.** Above the calendar grid in `MainWindow.xaml`:

```xml
<ItemsControl ItemsSource="{Binding StatusStrip.Rooms}" Margin="0,0,0,6"
              AutomationProperties.Name="Room status">
  <ItemsControl.ItemsPanel>
    <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
  </ItemsControl.ItemsPanel>
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <Border x:Name="Chip" CornerRadius="10" Padding="8,2" Margin="0,0,6,4" Background="#E5E7EB"
              ToolTip="{Binding Session}" Focusable="True"
              AutomationProperties.Name="{Binding Name}" AutomationProperties.HelpText="{Binding StateText}">
        <TextBlock><Run Text="{Binding Name}" FontWeight="SemiBold" /><Run Text=" · " /><Run Text="{Binding StateText}" /></TextBlock>
      </Border>
      <DataTemplate.Triggers>
        <DataTrigger Binding="{Binding Tone}" Value="ok"><Setter TargetName="Chip" Property="Background" Value="#D1FAE5" /></DataTrigger>
        <DataTrigger Binding="{Binding Tone}" Value="bad"><Setter TargetName="Chip" Property="Background" Value="#FEE2E2" /></DataTrigger>
        <DataTrigger Binding="{Binding Tone}" Value="idle"><Setter TargetName="Chip" Property="Background" Value="#F3F4F6" /></DataTrigger>
      </DataTemplate.Triggers>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
<TextBlock Text="{Binding StatusStrip.Summary}" Margin="0,0,0,6" />
```

(The `unknown` tone keeps the neutral grey default — never green.)

- [ ] **Step 10: Full suite, then a real run.** All tests pass. Launch, sign in, confirm chips appear after Refresh; `API calls` in the log shows one `ListRecorders` per Refresh/checkpoint.
- [ ] **Step 11: Commit.** `git commit -m "Room status strip and tray alerts, with an App test project"` (add new files).

---

### Task 9: SeriesSelector

**Files:**
- Create: `src/PanoptoScheduler.Core/Scheduling/SeriesSelector.cs`
- Test: `src/PanoptoScheduler.Core.Tests/SeriesSelectorTests.cs`

**Interfaces:**
- Consumes: `SessionTargets.From` (Task 3).
- Produces: `record SeriesFolder(string FolderId, string FolderName, int Upcoming)`; `record SeriesFilter(string FolderId, string? RecorderName = null, IReadOnlySet<DayOfWeek>? Weekdays = null, DateOnly? From = null, DateOnly? To = null)`; `static class SeriesSelector { IReadOnlyList<SeriesFolder> Folders(IEnumerable<PanoptoSession>, DateTime nowRoom); IReadOnlyList<PanoptoSession> Select(IEnumerable<PanoptoSession>, SeriesFilter, DateTime nowRoom); IReadOnlyList<PanoptoSession> OnDates(IEnumerable<PanoptoSession>, IReadOnlySet<DateOnly>) }`.

- [ ] **Step 1: Write the failing tests.**

```csharp
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

public class SeriesSelectorTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0); // Monday noon

    private static PanoptoSession S(string folder, DateTime start, string room = "Room 101") => new()
    {
        SessionID = Guid.NewGuid().ToString("D"), SessionName = "L", FolderID = folder, FolderName = folder.ToUpperInvariant(),
        RemoteRecorderName = room, StartTime = start, Duration = 5400,
    };

    [Fact]
    public void Folders_count_only_future_sessions()
    {
        var folders = SeriesSelector.Folders([S("a", Now.AddDays(-7)), S("a", Now.AddDays(1)), S("b", Now.AddDays(2))], Now);
        Assert.Equal(new[] { ("a", 1), ("b", 1) }, folders.Select(f => (f.FolderId, f.Upcoming)));
    }

    [Fact]
    public void Select_filters_folder_room_weekday_and_range()
    {
        var tue = Now.AddDays(1); var wed = Now.AddDays(2);
        var sessions = new[] { S("a", tue), S("a", wed), S("a", tue.AddDays(7), "Room 202"), S("b", tue) };
        var picked = SeriesSelector.Select(sessions,
            new SeriesFilter("a", RecorderName: "Room 101", Weekdays: new HashSet<DayOfWeek> { DayOfWeek.Tuesday }), Now);
        Assert.Single(picked);
    }

    [Fact]
    public void Select_excludes_sessions_already_started()
        => Assert.Empty(SeriesSelector.Select([S("a", Now.AddMinutes(-5))], new SeriesFilter("a"), Now));

    [Fact]
    public void OnDates_picks_by_room_day()
    {
        var reading = DateOnly.FromDateTime(Now.AddDays(8));
        var picked = SeriesSelector.OnDates([S("a", Now.AddDays(1)), S("a", Now.AddDays(8))], new HashSet<DateOnly> { reading });
        Assert.Single(picked);
    }
}
```

- [ ] **Step 2: Run to see it fail.**

- [ ] **Step 3: Implement.**

```csharp
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Scheduling;

public sealed record SeriesFolder(string FolderId, string FolderName, int Upcoming);

public sealed record SeriesFilter(
    string FolderId,
    string? RecorderName = null,
    IReadOnlySet<DayOfWeek>? Weekdays = null,
    DateOnly? From = null,
    DateOnly? To = null);

/// <summary>A course series is a folder's future scheduled sessions (spec §2). No calls: reads the cache.</summary>
public static class SeriesSelector
{
    public static IReadOnlyList<SeriesFolder> Folders(IEnumerable<PanoptoSession> sessions, DateTime nowRoom)
        => sessions.Where(s => s.FolderID is not null && s.EffectiveStart > nowRoom)
            .GroupBy(s => s.FolderID!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SeriesFolder(g.Key, g.First().FolderName ?? g.Key, g.Count()))
            .OrderBy(f => f.FolderName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static IReadOnlyList<PanoptoSession> Select(IEnumerable<PanoptoSession> sessions, SeriesFilter f, DateTime nowRoom)
        => sessions.Where(s =>
                string.Equals(s.FolderID, f.FolderId, StringComparison.OrdinalIgnoreCase)
                && s.EffectiveStart is { } start && start > nowRoom
                && (f.RecorderName is null || string.Equals(s.RemoteRecorderName?.Trim(), f.RecorderName.Trim(), StringComparison.OrdinalIgnoreCase))
                && (f.Weekdays is null || f.Weekdays.Count == 0 || f.Weekdays.Contains(start.DayOfWeek))
                && (f.From is null || DateOnly.FromDateTime(start) >= f.From)
                && (f.To is null || DateOnly.FromDateTime(start) <= f.To))
            .OrderBy(s => s.EffectiveStart)
            .ToList();

    public static IReadOnlyList<PanoptoSession> OnDates(IEnumerable<PanoptoSession> series, IReadOnlySet<DateOnly> dates)
        => series.Where(s => s.EffectiveStart is { } start && dates.Contains(DateOnly.FromDateTime(start))).ToList();
}
```

- [ ] **Step 4: Run tests** → pass.
- [ ] **Step 5: Commit.** `git commit -m "SeriesSelector: a folder's future sessions from the cache"`.

---

### Task 10: RoomChangePlan and RoomChanger

**Files:**
- Create: `src/PanoptoScheduler.Core/Scheduling/RoomChange.cs`
- Test: `src/PanoptoScheduler.Core.Tests/RoomChangerTests.cs`

**Interfaces:**
- Consumes: `ScheduledRecordingResult` (`ConflictsExist`, `SessionIds`, `SessionId`, `Conflicts`), `ProblemText.IsTimeout(OperationCanceledException)`, `IBulkAuditLog.TryRecord(BulkAuditEntry)`, `SessionTargets.From`.
- Produces:

```csharp
public sealed record RoomChangeRow(PanoptoSession Original, string? Refusal) { bool CanMove; }
public static class RoomChangePlan
{
    static IReadOnlyList<RoomChangeRow> Build(IReadOnlyList<PanoptoSession> series, Guid targetRecorderId, string targetRecorderName,
                                              IReadOnlyList<PanoptoSession> allCached, DateTime nowRoom);
}
public enum RoomChangeOutcome { WouldMove, Moved, Refused, NotBooked, MayHaveGone, Duplicate }
public sealed record RoomChangeResult(PanoptoSession Original, RoomChangeOutcome Outcome, string Message, Guid? NewSessionId);
public delegate Task<ScheduledRecordingResult> BookRecording(string name, Guid folderId, bool isBroadcast, DateTime start, DateTime end, Guid recorderId, CancellationToken ct);
public delegate Task DeleteOriginals(IReadOnlyList<(Guid Id, string Name)> originals, CancellationToken ct);
public sealed class RoomChanger(BookRecording book, DeleteOriginals delete, IBulkAuditLog? audit = null)
{
    Task<IReadOnlyList<RoomChangeResult>> RunAsync(IReadOnlyList<RoomChangeRow> rows, Guid targetRecorderId, bool dryRun,
                                                   IProgress<int>? progress = null, CancellationToken ct = default);
}
```

- [ ] **Step 1: Write the failing tests.**

```csharp
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

public class RoomChangerTests
{
    private static readonly Guid Old = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid New = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Folder = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0);

    private static PanoptoSession S(DateTime start, Guid recorder) => new()
    {
        SessionID = Guid.NewGuid().ToString("D"), SessionName = "RSM1234", FolderID = Folder.ToString("D"),
        RemoteRecorderID = recorder.ToString("D"), StartTime = start, Duration = 5400,
    };

    private static ScheduledRecordingResult Booked() => new() { SessionIds = [Guid.NewGuid()] };

    [Fact]
    public void Plan_refuses_a_busy_target_room()
    {
        var mine = S(Now.AddDays(1), Old);
        var blocker = S(Now.AddDays(1).AddMinutes(30), New);
        var rows = RoomChangePlan.Build([mine], New, "Room 202", [mine, blocker], Now);
        Assert.Contains("Room 202 is booked", rows[0].Refusal);
    }

    [Fact]
    public void Plan_refuses_sessions_already_in_the_target_room()
    {
        var mine = S(Now.AddDays(1), New);
        Assert.NotNull(RoomChangePlan.Build([mine], New, "Room 202", [mine], Now)[0].Refusal);
    }

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        var mine = S(Now.AddDays(1), Old);
        var calls = 0;
        var changer = new RoomChanger((_, _, _, _, _, _, _) => { calls++; return Task.FromResult(Booked()); },
                                      (_, _) => { calls++; return Task.CompletedTask; });
        var results = await changer.RunAsync(RoomChangePlan.Build([mine], New, "Room 202", [mine], Now), New, dryRun: true);
        Assert.Equal(0, calls);
        Assert.Equal(RoomChangeOutcome.WouldMove, results[0].Outcome);
    }

    [Fact]
    public async Task Deletes_only_originals_whose_booking_succeeded()
    {
        var a = S(Now.AddDays(1), Old); var b = S(Now.AddDays(8), Old);
        IReadOnlyList<(Guid, string)>? deleted = null;
        var changer = new RoomChanger(
            (_, _, _, start, _, _, _) => Task.FromResult(start == b.EffectiveStart
                ? new ScheduledRecordingResult { ConflictsExist = true, Conflicts = ["X"] }
                : Booked()),
            (ids, _) => { deleted = ids; return Task.CompletedTask; });

        var results = await changer.RunAsync(RoomChangePlan.Build([a, b], New, "Room 202", [a, b], Now), New, dryRun: false);

        Assert.Equal(new[] { Guid.Parse(a.SessionID!) }, deleted!.Select(d => d.Item1));
        Assert.Equal(new[] { RoomChangeOutcome.Moved, RoomChangeOutcome.NotBooked }, results.Select(r => r.Outcome));
    }

    [Fact]
    public async Task Booking_throws_keeps_original_and_reports_not_booked()
    {
        var a = S(Now.AddDays(1), Old);
        var deleteCalled = false;
        var changer = new RoomChanger((_, _, _, _, _, _, _) => throw new InvalidOperationException("Access denied"),
                                      (_, _) => { deleteCalled = true; return Task.CompletedTask; });
        var results = await changer.RunAsync(RoomChangePlan.Build([a], New, "Room 202", [a], Now), New, dryRun: false);
        Assert.False(deleteCalled);
        Assert.Equal(RoomChangeOutcome.NotBooked, results[0].Outcome);
        Assert.Contains("Access denied", results[0].Message);
    }

    [Fact]
    public async Task Booking_timeout_is_may_have_gone_and_original_kept()
    {
        var a = S(Now.AddDays(1), Old);
        var deleteCalled = false;
        var changer = new RoomChanger((_, _, _, _, _, _, _) => throw new TaskCanceledException("timeout", new TimeoutException()),
                                      (_, _) => { deleteCalled = true; return Task.CompletedTask; });
        var results = await changer.RunAsync(RoomChangePlan.Build([a], New, "Room 202", [a], Now), New, dryRun: false);
        Assert.False(deleteCalled);
        Assert.Equal(RoomChangeOutcome.MayHaveGone, results[0].Outcome);
    }

    [Fact]
    public async Task Delete_failure_reports_duplicates()
    {
        var a = S(Now.AddDays(1), Old);
        var changer = new RoomChanger((_, _, _, _, _, _, _) => Task.FromResult(Booked()),
                                      (_, _) => throw new InvalidOperationException("fault"));
        var results = await changer.RunAsync(RoomChangePlan.Build([a], New, "Room 202", [a], Now), New, dryRun: false);
        Assert.Equal(RoomChangeOutcome.Duplicate, results[0].Outcome);
        Assert.Contains("original was not removed", results[0].Message);
    }
}
```

(`ProblemText.IsTimeout` decides what counts as a timeout; if it does not classify a `TaskCanceledException` with an inner `TimeoutException` as one, build the test exception the way `ProblemTextTests.cs` builds its timeout case.)

- [ ] **Step 2: Run to see it fail.**

- [ ] **Step 3: Implement.**

```csharp
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Scheduling;

public sealed record RoomChangeRow(PanoptoSession Original, string? Refusal)
{
    public bool CanMove => Refusal is null;
}

/// <summary>What a change of room would do, decided from the cache (spec §2). No calls.</summary>
public static class RoomChangePlan
{
    public static IReadOnlyList<RoomChangeRow> Build(
        IReadOnlyList<PanoptoSession> series, Guid targetRecorderId, string targetRecorderName,
        IReadOnlyList<PanoptoSession> allCached, DateTime nowRoom)
    {
        var seriesIds = series.Select(s => s.SessionID).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetBusy = allCached
            .Where(s => !seriesIds.Contains(s.SessionID ?? "")
                        && Guid.TryParse(s.RemoteRecorderID, out var r) && r == targetRecorderId
                        && s.EffectiveStart is not null)
            .ToList();

        return series.Select(s =>
        {
            if (s.EffectiveStart is not { } start || s.EffectiveEnd is not { } end)
                return new RoomChangeRow(s, "Panopto reported no time for this session.");
            if (start <= nowRoom)
                return new RoomChangeRow(s, "Already started or in the past.");
            if (Guid.TryParse(s.RemoteRecorderID, out var current) && current == targetRecorderId)
                return new RoomChangeRow(s, $"Already in {targetRecorderName}.");
            if (!Guid.TryParse(s.FolderID, out _))
                return new RoomChangeRow(s, "The session's folder is unknown, so it cannot be rebooked there.");
            var clash = targetBusy.FirstOrDefault(b => b.EffectiveStart < end && start < (b.EffectiveEnd ?? b.EffectiveStart));
            return clash is null
                ? new RoomChangeRow(s, null)
                : new RoomChangeRow(s, $"{targetRecorderName} is booked then (\"{clash.SessionName}\").");
        }).ToList();
    }
}

public enum RoomChangeOutcome { WouldMove, Moved, Refused, NotBooked, MayHaveGone, Duplicate }

public sealed record RoomChangeResult(PanoptoSession Original, RoomChangeOutcome Outcome, string Message, Guid? NewSessionId);

public delegate Task<ScheduledRecordingResult> BookRecording(
    string name, Guid folderId, bool isBroadcast, DateTime start, DateTime end, Guid recorderId, CancellationToken ct);

public delegate Task DeleteOriginals(IReadOnlyList<(Guid Id, string Name)> originals, CancellationToken ct);

/// <summary>
/// Moves sessions to another room: book the new one, then delete the old ones
/// whose booking succeeded — in one batch (spec §2). A booking that failed or
/// may have landed leaves its original alone, so a bad run never loses a
/// recording; the worst case is a reported duplicate.
/// </summary>
public sealed class RoomChanger(BookRecording book, DeleteOriginals delete, IBulkAuditLog? audit = null)
{
    public async Task<IReadOnlyList<RoomChangeResult>> RunAsync(
        IReadOnlyList<RoomChangeRow> rows, Guid targetRecorderId, bool dryRun,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var run = Guid.NewGuid();
        var results = new List<RoomChangeResult>(rows.Count);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var s = row.Original;

            RoomChangeResult result;
            if (!row.CanMove)
                result = new(s, RoomChangeOutcome.Refused, row.Refusal!, null);
            else if (dryRun)
                result = new(s, RoomChangeOutcome.WouldMove, "Would book in the new room, then remove the original.", null);
            else
                result = await BookOneAsync(s, targetRecorderId, ct).ConfigureAwait(false);

            results.Add(result);
            Audit(run, dryRun, result);
            progress?.Report(results.Count);
        }

        var booked = results.Where(r => r.Outcome == RoomChangeOutcome.Moved).ToList();
        if (booked.Count == 0) return results;

        try
        {
            await delete(booked.Select(r => (Guid.Parse(r.Original.SessionID!), r.Original.SessionName ?? "")).ToList(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            for (var i = 0; i < results.Count; i++)
                if (results[i].Outcome == RoomChangeOutcome.Moved)
                {
                    results[i] = results[i] with
                    {
                        Outcome = RoomChangeOutcome.Duplicate,
                        Message = $"Booked in the new room, but the original was not removed ({ex.Message}). Delete it by hand.",
                    };
                    Audit(run, dryRun, results[i]);
                }
        }

        return results;
    }

    private async Task<RoomChangeResult> BookOneAsync(PanoptoSession s, Guid recorderId, CancellationToken ct)
    {
        try
        {
            var booked = await book(s.SessionName ?? "(untitled)", Guid.Parse(s.FolderID!), s.IsBroadcast,
                s.EffectiveStart!.Value, s.EffectiveEnd!.Value, recorderId, ct).ConfigureAwait(false);

            if (booked.ConflictsExist)
                return new(s, RoomChangeOutcome.NotBooked, $"Panopto reported a clash: {string.Join("; ", booked.Conflicts)}", null);
            if (booked.SessionIds.Count == 0)
                return new(s, RoomChangeOutcome.NotBooked, "Panopto returned no new session.", null);

            return new(s, RoomChangeOutcome.Moved, "Moved. The new session has a new id; its description was not carried.", booked.SessionId);
        }
        catch (OperationCanceledException ex) when (ProblemText.IsTimeout(ex))
        {
            return new(s, RoomChangeOutcome.MayHaveGone,
                "Panopto did not answer in time. The new booking may exist; the original was kept. Check the new room.", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(s, RoomChangeOutcome.NotBooked, ex.Message, null);
        }
    }

    private void Audit(Guid run, bool dryRun, RoomChangeResult r) => audit?.TryRecord(new BulkAuditEntry
    {
        TimestampUtc = DateTime.UtcNow,
        RunId = run,
        Operation = "change-room",
        DryRun = dryRun,
        SessionId = Guid.TryParse(r.Original.SessionID, out var id) ? id : null,
        SessionName = r.Original.SessionName,
        Outcome = r.Outcome.ToString(),
        Message = r.Message,
        Detail = r.NewSessionId?.ToString("D"),
    });
}
```

- [ ] **Step 4: Run tests** → pass.
- [ ] **Step 5: Commit.** `git commit -m "Change room: book first, delete only what moved, report duplicates"`.

---

### Task 11: Course series tab

**Files:**
- Create: `src/PanoptoScheduler.App/ViewModels/SeriesViewModel.cs`
- Test: `src/PanoptoScheduler.App.Tests/SeriesViewModelTests.cs`
- Modify: `src/PanoptoScheduler.App/ViewModels/BulkWindowViewModel.cs` (ctor + `Series` property), `src/PanoptoScheduler.App/BulkWindow.xaml` (new `TabItem` after "Edit selected sessions", ~535), `src/PanoptoScheduler.App/MainWindow.xaml.cs` / wherever `BulkWindowViewModel` is constructed (pass the calendar's cache + refresh).

**Interfaces:**
- Consumes: `SeriesSelector`, `SeriesFilter`, `SeriesFolder` (Task 9); `RoomChangePlan`, `RoomChanger` (Task 10); `SessionTargets.From`, `RetimePlan.Shift(IReadOnlyList<SessionTarget>, TimeSpan)`, `RetimePlan.SetStartTimeOfDay(...)`, `BulkSessionEditor.RetimeAsync(RetimePlanResult, bool dryRun, ...)`, `BulkSessionEditor.DeleteAsync(targets, dryRun, DestructiveAction? permit, ...)`; `CalendarViewModel.Cache`, `CalendarViewModel.EnsureFreshAsync(bool force)`.
- Produces: `SeriesViewModel(SeriesServices services)` where

```csharp
public sealed record SeriesServices(
    Func<IReadOnlyList<PanoptoSession>> Cached,
    Func<Task<bool>> ForceRead,                  // rule 4
    Func<DateTime> NowRoom,
    Func<IReadOnlyList<RemoteRecorder>> Recorders,
    Func<RetimePlanResult, bool, Task<BulkEditReport>> Retime,
    Func<IReadOnlyList<(Guid Id, string CurrentName)>, bool, Task<BulkEditReport>> Delete,  // wraps the permit
    RoomChanger RoomChanger,
    Action MarkCacheStale);
```

and members: `ObservableCollection<SeriesFolder> Folders`, `SeriesFolder? SelectedFolder`, `string? RoomFilter`, `ObservableCollection<PanoptoSession> Matched`, `ObservableCollection<DateOnly> SkipDates`, `TimeSpan ShiftBy`, `RemoteRecorder? TargetRoom`, `ObservableCollection<string> PreviewLines`, `bool HasPreview`, commands `PreviewShiftCommand`, `PreviewSkipCommand`, `PreviewRoomCommand`, `ConfirmCommand`, `CancelPreviewCommand`.

- [ ] **Step 1: Write the failing tests.** They pin the spec's guarantees: preview makes no writes, confirm re-reads first (rule 4), a change-room confirm marks the cache stale.

```csharp
using PanoptoScheduler.App.ViewModels;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.App.Tests;

public class SeriesViewModelTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0);
    private static readonly Guid Rec2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private sealed class Log { public int Reads, Retimes, Deletes, Books, Stale; }

    private static (SeriesViewModel, Log) Build()
    {
        var log = new Log();
        var sessions = new List<PanoptoSession>
        {
            new() { SessionID = Guid.NewGuid().ToString("D"), SessionName = "L1", FolderID = "f", FolderName = "RSM1234",
                    RemoteRecorderID = Guid.NewGuid().ToString("D"), StartTime = Now.AddDays(1), Duration = 5400 },
            new() { SessionID = Guid.NewGuid().ToString("D"), SessionName = "L2", FolderID = Guid.NewGuid().ToString("D"), FolderName = "RSM1234",
                    RemoteRecorderID = Guid.NewGuid().ToString("D"), StartTime = Now.AddDays(8), Duration = 5400 },
        };
        sessions[0].FolderID = sessions[1].FolderID;
        var services = new SeriesServices(
            () => sessions,
            () => { log.Reads++; return Task.FromResult(true); },
            () => Now,
            () => [new RemoteRecorder { Id = Rec2, Name = "Room 202", State = "Stopped" }],
            (plan, dry) => { if (!dry) log.Retimes++; return Task.FromResult(new BulkEditReport([])); },
            (ids, dry) => { if (!dry) log.Deletes++; return Task.FromResult(new BulkEditReport([])); },
            new RoomChanger((_, _, _, _, _, _, _) => { log.Books++; return Task.FromResult(new ScheduledRecordingResult { SessionIds = [Guid.NewGuid()] }); },
                            (_, _) => { log.Deletes++; return Task.CompletedTask; }),
            () => log.Stale++);
        var vm = new SeriesViewModel(services);
        vm.SelectedFolder = vm.Folders.Single();
        return (vm, log);
    }

    [Fact]
    public async Task Preview_writes_nothing_and_reads_nothing()
    {
        var (vm, log) = Build();
        vm.ShiftBy = TimeSpan.FromMinutes(30);
        await vm.PreviewShiftCommand.ExecuteAsync();
        Assert.True(vm.HasPreview);
        Assert.Equal(2, vm.PreviewLines.Count);
        Assert.Equal((0, 0, 0), (log.Reads, log.Retimes, log.Books));
    }

    [Fact]
    public async Task Confirm_rereads_then_writes()
    {
        var (vm, log) = Build();
        vm.ShiftBy = TimeSpan.FromMinutes(30);
        await vm.PreviewShiftCommand.ExecuteAsync();
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(1, log.Reads);
        Assert.Equal(1, log.Retimes);
    }

    [Fact]
    public async Task Change_room_marks_the_cache_stale()
    {
        var (vm, log) = Build();
        vm.TargetRoom = vm.Rooms.Single();
        await vm.PreviewRoomCommand.ExecuteAsync();
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(2, log.Books);
        Assert.Equal(1, log.Stale);
    }

    [Fact]
    public async Task Skip_with_no_dates_offers_nothing_to_confirm()
    {
        var (vm, _) = Build();
        await vm.PreviewSkipCommand.ExecuteAsync();
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }
}
```

(If `BulkEditReport`'s constructor needs `AuditWarning`, pass it as in `BulkSessionEditorTests.cs`.)

- [ ] **Step 2: Run to see it fail.**

- [ ] **Step 3: Implement `SeriesViewModel`.** Structure:

```csharp
using System.Collections.ObjectModel;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.App.ViewModels;

public sealed record SeriesServices(
    Func<IReadOnlyList<PanoptoSession>> Cached,
    Func<Task<bool>> ForceRead,
    Func<DateTime> NowRoom,
    Func<IReadOnlyList<RemoteRecorder>> Recorders,
    Func<RetimePlanResult, bool, Task<BulkEditReport>> Retime,
    Func<IReadOnlyList<(Guid Id, string CurrentName)>, bool, Task<BulkEditReport>> Delete,
    RoomChanger RoomChanger,
    Action MarkCacheStale);

/// <summary>Course series tab: pick a folder, preview a change, confirm it (spec §2).</summary>
public sealed class SeriesViewModel : ObservableObject
{
    private enum Pending { None, Shift, Skip, Room }

    private readonly SeriesServices _s;
    private SeriesFolder? _folder;
    private Pending _pending;
    private string _result = "";

    public SeriesViewModel(SeriesServices services)
    {
        _s = services;
        foreach (var f in SeriesSelector.Folders(_s.Cached(), _s.NowRoom())) Folders.Add(f);
        foreach (var r in _s.Recorders().OrderBy(r => r.Name)) Rooms.Add(r);

        PreviewShiftCommand = new AsyncRelayCommand(() => PreviewAsync(Pending.Shift));
        PreviewSkipCommand = new AsyncRelayCommand(() => PreviewAsync(Pending.Skip));
        PreviewRoomCommand = new AsyncRelayCommand(() => PreviewAsync(Pending.Room), () => TargetRoom is not null);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => HasPreview);
        CancelPreviewCommand = new RelayCommand(ClearPreview);
    }

    public ObservableCollection<SeriesFolder> Folders { get; } = [];
    public ObservableCollection<RemoteRecorder> Rooms { get; } = [];
    public ObservableCollection<PanoptoSession> Matched { get; } = [];
    public ObservableCollection<DateOnly> SkipDates { get; } = [];
    public ObservableCollection<string> PreviewLines { get; } = [];

    public string? RoomFilter { get; set; }
    public TimeSpan ShiftBy { get; set; }
    public RemoteRecorder? TargetRoom { get; set; }

    public SeriesFolder? SelectedFolder
    {
        get => _folder;
        set { _folder = value; Raise(); RefreshMatched(); ClearPreview(); }
    }

    public bool HasPreview => _pending != Pending.None && PreviewLines.Count > 0;

    public string Result { get => _result; private set { _result = value; Raise(); } }

    public AsyncRelayCommand PreviewShiftCommand { get; }
    public AsyncRelayCommand PreviewSkipCommand { get; }
    public AsyncRelayCommand PreviewRoomCommand { get; }
    public AsyncRelayCommand ConfirmCommand { get; }
    public RelayCommand CancelPreviewCommand { get; }

    private void RefreshMatched()
    {
        Matched.Clear();
        if (_folder is null) return;
        foreach (var s in SeriesSelector.Select(_s.Cached(), new SeriesFilter(_folder.FolderId, RoomFilter), _s.NowRoom()))
            Matched.Add(s);
    }

    private IReadOnlyList<PanoptoSession> Scope(Pending kind) => kind == Pending.Skip
        ? SeriesSelector.OnDates(Matched, SkipDates.ToHashSet())
        : Matched.ToList();

    private Task PreviewAsync(Pending kind)
    {
        PreviewLines.Clear();
        _pending = kind;
        var scope = Scope(kind);

        switch (kind)
        {
            case Pending.Shift:
                foreach (var e in RetimePlan.Shift(Targets(scope), ShiftBy).Entries)
                    PreviewLines.Add(e.CanMove ? $"{e.Target.CurrentName}: {e.Target.Start:ddd MMM d h:mm tt} → {e.Slot!.Start:ddd MMM d h:mm tt}"
                                               : $"{e.Target.CurrentName}: not moved — {e.Refusal}");
                break;
            case Pending.Skip:
                foreach (var s in scope) PreviewLines.Add($"Delete {s.SessionName} on {s.EffectiveStart:ddd MMM d h:mm tt}");
                break;
            case Pending.Room:
                foreach (var row in RoomChangePlan.Build(scope, TargetRoom!.Id, TargetRoom.Name, _s.Cached(), _s.NowRoom()))
                    PreviewLines.Add(row.CanMove ? $"{row.Original.SessionName} {row.Original.EffectiveStart:ddd MMM d h:mm tt} → {TargetRoom.Name} (new id; description not carried)"
                                                 : $"{row.Original.SessionName}: not moved — {row.Refusal}");
                break;
        }

        RaiseAll();
        return Task.CompletedTask;
    }

    private async Task ConfirmAsync()
    {
        var kind = _pending;
        if (!await _s.ForceRead()) { Result = "Could not re-read the schedule; nothing was changed."; return; }
        RefreshMatched();                     // rule 4: rebuild from what Panopto holds now
        var scope = Scope(kind);

        switch (kind)
        {
            case Pending.Shift:
                var retime = await _s.Retime(RetimePlan.Shift(Targets(scope), ShiftBy), false);
                Result = $"Moved {retime.Applied}, failed {retime.Failed}.";
                break;
            case Pending.Skip:
                var deleted = await _s.Delete(scope.Select(s => (Guid.Parse(s.SessionID!), s.SessionName ?? "")).ToList(), false);
                Result = $"Deleted {deleted.Applied}, failed {deleted.Failed}.";
                break;
            case Pending.Room:
                var rows = RoomChangePlan.Build(scope, TargetRoom!.Id, TargetRoom.Name, _s.Cached(), _s.NowRoom());
                var results = await _s.RoomChanger.RunAsync(rows, TargetRoom.Id, dryRun: false);
                _s.MarkCacheStale();          // new ids are DeliveryIDs — the cache cannot mirror them
                Result = string.Join(" · ", results.GroupBy(r => r.Outcome).Select(g => $"{g.Key} {g.Count()}"));
                break;
        }

        ClearPreview();
    }

    private static IReadOnlyList<SessionTarget> Targets(IEnumerable<PanoptoSession> sessions)
        => sessions.Select(SessionTargets.From).OfType<SessionTarget>().ToList();

    private void ClearPreview()
    {
        _pending = Pending.None;
        PreviewLines.Clear();
        RaiseAll();
    }

    private void RaiseAll()
    {
        Raise(nameof(HasPreview));
        ConfirmCommand.RaiseCanExecuteChanged();
    }
}
```

(Match `RelayCommand`/`AsyncRelayCommand` constructor shapes and the `RaiseCanExecuteChanged` name to `ViewModels/Mvvm.cs`. The existing bulk tab's result-row DataGrid can be reused for a detailed per-row report in a follow-up; `Result` is the summary line.)

- [ ] **Step 4: Run tests** → pass.

- [ ] **Step 5: Wire services.** Where `BulkWindowViewModel` is constructed, pass the calendar view model and build:

```csharp
var editor = panopto.BulkEditing;
Series = new SeriesViewModel(new SeriesServices(
    () => calendar.Cache.Sessions,
    () => calendar.EnsureFreshAsync(force: true),
    () => RoomClock.Now(panopto.RoomZone),
    () => calendar.KnownRecorders,
    (plan, dry) => editor.RetimeAsync(plan, dry),
    (ids, dry) => editor.DeleteAsync(ids, dry, dry ? null : new DestructiveAction(DestructiveAction.DeleteVerb, ids.Select(i => i.Id).ToList(), Acknowledged: true)),
    new RoomChanger(
        (name, folder, bc, start, end, rec, ct) => panopto.Recorders.ScheduleAsync(name, folder, bc, start, end, [rec], ct),
        (ids, ct) => editor.DeleteAsync(ids, false, new DestructiveAction(DestructiveAction.DeleteVerb, ids.Select(i => i.Id).ToList(), Acknowledged: true), ct: ct),
        auditLog),
    calendar.Cache.MarkStale));
```

`calendar.KnownRecorders` is the recorder list `LoadRoomsAsync` already reads (~2176) — expose it as `public IReadOnlyList<RemoteRecorder> KnownRecorders` if it is not public. `auditLog` is the same `IBulkAuditLog` instance handed to `PanoptoConnection` in `App.xaml.cs`; pass it through. The `DestructiveAction` permit is created only on confirm, after the operator has seen the preview — that is its purpose.

- [ ] **Step 6: Tab XAML** (after the "Edit selected sessions" tab):

```xml
<TabItem Header="Course series" DataContext="{Binding Series}">
  <DockPanel Margin="12">
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
      <TextBlock Text="Course folder" VerticalAlignment="Center" Margin="0,0,8,0" />
      <ComboBox Width="320" ItemsSource="{Binding Folders}" SelectedItem="{Binding SelectedFolder}"
                DisplayMemberPath="FolderName" AutomationProperties.Name="Course folder" />
      <TextBlock Text="{Binding Matched.Count, StringFormat={}{0} upcoming session(s)}" VerticalAlignment="Center" Margin="12,0,0,0" />
    </StackPanel>
    <WrapPanel DockPanel.Dock="Top" Margin="0,0,0,8">
      <GroupBox Header="Shift time" Margin="0,0,8,0" Padding="6">
        <StackPanel Orientation="Horizontal">
          <TextBox Width="80" Text="{Binding ShiftBy}" AutomationProperties.Name="Shift by (hh:mm, negative for earlier)" />
          <Button Content="Preview" Command="{Binding PreviewShiftCommand}" Margin="6,0,0,0" />
        </StackPanel>
      </GroupBox>
      <GroupBox Header="Skip dates" Margin="0,0,8,0" Padding="6">
        <StackPanel Orientation="Horizontal">
          <DatePicker x:Name="SkipPicker" AutomationProperties.Name="Date to skip" />
          <Button Content="Add" Margin="6,0,0,0" Click="AddSkipDate_Click" />
          <ItemsControl ItemsSource="{Binding SkipDates}" Margin="6,0,0,0" />
          <Button Content="Preview" Command="{Binding PreviewSkipCommand}" Margin="6,0,0,0" />
        </StackPanel>
      </GroupBox>
      <GroupBox Header="Change room" Padding="6">
        <StackPanel Orientation="Horizontal">
          <ComboBox Width="200" ItemsSource="{Binding Rooms}" SelectedItem="{Binding TargetRoom}" DisplayMemberPath="Name"
                    AutomationProperties.Name="New room" />
          <Button Content="Preview" Command="{Binding PreviewRoomCommand}" Margin="6,0,0,0" />
        </StackPanel>
      </GroupBox>
    </WrapPanel>
    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,8,0,0">
      <TextBlock Text="{Binding Result}" VerticalAlignment="Center" Margin="0,0,12,0" />
      <Button Content="Cancel" Command="{Binding CancelPreviewCommand}" Margin="0,0,6,0" />
      <Button Content="Confirm changes" Command="{Binding ConfirmCommand}" IsDefault="False" />
    </StackPanel>
    <ListBox ItemsSource="{Binding PreviewLines}" AutomationProperties.Name="What would happen" />
  </DockPanel>
</TabItem>
```

`AddSkipDate_Click` in `BulkWindow.xaml.cs`: `if (SkipPicker.SelectedDate is { } d && DataContext is BulkWindowViewModel vm && !vm.Series.SkipDates.Contains(DateOnly.FromDateTime(d))) vm.Series.SkipDates.Add(DateOnly.FromDateTime(d));`.

- [ ] **Step 7: Full suite + real dry run.** All tests pass. Launch, open the bulk window, pick a real course folder, preview each of the three operations; **do not confirm** against production in this task. Screenshot/describe the previews in the task report.
- [ ] **Step 8: Commit.** `git commit -m "Course series tab: shift, skip dates and change room with preview and re-read"`.

---

### Task 12: Update check from the share

**Files:**
- Create: `src/PanoptoScheduler.Core/Updates/ShareFeed.cs`
- Modify: `src/PanoptoScheduler.Core/Configuration/CredentialStore.cs` (`UpdateShare`), `src/PanoptoScheduler.Core/Updates/VersionFeed.cs` (remove gist read), `src/PanoptoScheduler.Core.Tests/VersionFeedTests.cs` (drop gist-read tests, keep `IsNewer` tests), `src/PanoptoScheduler.App/App.xaml.cs` (`CheckForUpdateAsync`), `src/PanoptoScheduler.App/ViewModels/CalendarViewModel.cs` (`OpenReleasesPage` → update command)
- Create: `src/PanoptoScheduler.App/Updater.cs`
- Test: `src/PanoptoScheduler.Core.Tests/ShareFeedTests.cs`

**Interfaces:**
- Produces: `PanoptoCredentials.UpdateShare : string?` (JSON `updateShare`); `ShareFeed.TryReadAsync(string shareRoot, TimeSpan? timeout = null, CancellationToken ct = default) : Task<ShareUpdate?>`; `record ShareUpdate(string Version, string InstallerPath)`; `Updater.Start(ShareUpdate update) : bool` (App).

- [ ] **Step 1: Write the failing tests.**

```csharp
using PanoptoScheduler.Core.Updates;

namespace PanoptoScheduler.Core.Tests;

public class ShareFeedTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pss-share-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Publish(string version, bool withInstaller = true)
    {
        File.WriteAllText(Path.Combine(_root, "latest.json"), $$"""{"version":"{{version}}"}""");
        if (withInstaller)
        {
            Directory.CreateDirectory(Path.Combine(_root, version));
            File.WriteAllText(Path.Combine(_root, version, "install.ps1"), "# test");
        }
    }

    [Fact]
    public async Task Reads_version_and_installer()
    {
        Publish("1.4.0");
        var update = await ShareFeed.TryReadAsync(_root);
        Assert.Equal("1.4.0", update!.Version);
        Assert.Equal(Path.Combine(_root, "1.4.0", "install.ps1"), update.InstallerPath);
    }

    [Fact]
    public async Task Missing_installer_folder_is_no_update()
    {
        Publish("1.4.0", withInstaller: false);
        Assert.Null(await ShareFeed.TryReadAsync(_root));
    }

    [Fact]
    public async Task Junk_version_is_no_update()
    {
        Publish("banana", withInstaller: false);
        Assert.Null(await ShareFeed.TryReadAsync(_root));
    }

    [Fact]
    public async Task Unreachable_share_returns_null_within_timeout()
    {
        var started = DateTime.UtcNow;
        var update = await ShareFeed.TryReadAsync(@"\\192.0.2.1\nowhere", TimeSpan.FromSeconds(2));
        Assert.Null(update);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
```

- [ ] **Step 2: Run to see it fail.**

- [ ] **Step 3: Implement.**

```csharp
using System.Text.Json;
using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.Core.Updates;

public sealed record ShareUpdate(string Version, string InstallerPath);

/// <summary>
/// The office share's <c>latest.json</c> (spec Amendment 3). A file read, not
/// an API call; given a hard timeout because an unreachable UNC path can hang
/// for tens of seconds and the check must never hold up startup.
/// </summary>
public static class ShareFeed
{
    public const string FileName = "latest.json";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task<ShareUpdate?> TryReadAsync(string shareRoot, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => Read(shareRoot), ct).WaitAsync(timeout ?? DefaultTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AppLog.Warn($"Update share {shareRoot} did not answer in time; no update check this launch.");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static ShareUpdate? Read(string root)
    {
        try
        {
            var path = Path.Combine(root, FileName);
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (!Version.TryParse(version, out _))
            {
                AppLog.Warn($"{path} carried '{version}', which is not a version.");
                return null;
            }

            var installer = Path.Combine(root, version!, "install.ps1");
            return File.Exists(installer) ? new ShareUpdate(version!, installer) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Update share could not be read: {ex.Message}");
            return null;
        }
    }
}
```

- [ ] **Step 4: Config.** In `PanoptoCredentials` add

```csharp
/// <summary>UNC folder the office installs and updates from (spec Amendment 3). Null: no update check.</summary>
public string? UpdateShare { get; init; }
```

Confirm the JSON options used by `CredentialStore`/`ShippedDefaults` are case-insensitive (so `updateShare` binds); if they are not, add `[JsonPropertyName("updateShare")]`. Add a `ConfigurationTests` case asserting a `defaults.json` with `"updateShare": "\\\\srv\\share"` round-trips.

- [ ] **Step 5: Retire the gist read.** Delete `VersionFeed.FeedUrl`, `UpdateInfo`, `Http`, `TryReadAsync` and their tests (the loopback-server tests in `VersionFeedTests.cs`); keep `IsNewer` and its tests.

- [ ] **Step 6: Updater (App).**

```csharp
using System.Diagnostics;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Updates;

namespace PanoptoScheduler.App;

/// <summary>
/// Update now: run the share's installer for the new version, which waits for
/// this process to exit, installs, and relaunches (install.ps1 -WaitForPid -Relaunch).
/// </summary>
public static class Updater
{
    public static bool Start(ShareUpdate update)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", update.InstallerPath,
                                        "-Force", "-WaitForPid", Environment.ProcessId.ToString(), "-Relaunch" })
                psi.ArgumentList.Add(arg);

            Process.Start(psi);
            AppLog.Info($"Update to {update.Version} started from {update.InstallerPath}.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not start the update.", ex);
            return false;
        }
    }
}
```

- [ ] **Step 7: Wire the check.** `App.CheckForUpdateAsync`: replace `VersionFeed.TryReadAsync()` with `credentials.UpdateShare is { Length: > 0 } share ? await ShareFeed.TryReadAsync(share) : null` (pass the resolved credentials in), keep the `IsNewer` guard and the banner. Store the `ShareUpdate` on the calendar view model via `AnnounceUpdate(update, Version)`. `GetUpdateCommand` → `UpdateNow`: `if (Updater.Start(_update!)) System.Windows.Application.Current.Shutdown();` else set `Status = "Could not start the update. Run Install.cmd on the share.";`. Change the banner text to "Version {x} is available — Update now". Remove `ReleasesUrl` and `OpenReleasesPage`.

- [ ] **Step 8: Full suite** → pass (test count drops by the removed gist tests and rises by the new ones — state both numbers in the commit message body).
- [ ] **Step 9: Commit.** `git commit -m "Update from the office share: latest.json check and Update now"`.

---

### Task 13: Share deployment and installer update mode

**Files:**
- Modify: `src/package/install.ps1` (params `-WaitForPid`, `-Relaunch`)
- Create: `src/deploy-share.ps1`, `src/package/How to install.txt`
- Modify: `src/release.ps1` (gist step → `deploy-share.ps1`), `src/publish.ps1` (copy `updateShare` into `defaults.json`; include `How to install.txt` in the zip)

**Interfaces:**
- Produces: `install.ps1 [-NoDesktop] [-Force] [-WaitForPid <int>] [-Relaunch]`; `deploy-share.ps1 -SharePath <UNC> -Version <x.y.z> [-Force]`; `release.ps1 -Version <x.y.z> -SharePath <UNC>`.

All three scripts: **pure ASCII**, no `…` or `—` characters.

- [ ] **Step 1: install.ps1 update mode.** Extend `param(...)`:

```powershell
param(
    [switch] $NoDesktop,
    [switch] $Force,
    # Update now: wait for the running copy to exit before replacing its files.
    [int] $WaitForPid = 0,
    # Start the installed app when done.
    [switch] $Relaunch
)
```

Immediately after the param block's validation (before any copy), add:

```powershell
if ($WaitForPid -gt 0) {
    Write-Host "Waiting for Panopto Scheduler to close..."
    $running = Get-Process -Id $WaitForPid -ErrorAction SilentlyContinue
    if ($running) {
        $running | Wait-Process -Timeout 60 -ErrorAction SilentlyContinue
        if (-not $running.HasExited) { Fail "Panopto Scheduler is still running. Close it and run Install.cmd again." }
    }
}
```

At the end of a successful install (after the shortcuts and registry key), add:

```powershell
if ($Relaunch) {
    Start-Process -FilePath (Join-Path $target 'PanoptoScheduler.App.exe')
}
```

(`$target` is the variable install.ps1 already uses for the install folder; use that name.) Save as ASCII: `[IO.File]::WriteAllText($path, $text, [Text.Encoding]::ASCII)` and verify with `(Get-Content $path -Raw) -match '[^\x00-\x7F]'` → `False`.

- [ ] **Step 2: Test update mode locally.** Unzip the current `src\dist\PanoptoScheduler-1.3.1-win-x64.zip` to a temp folder, copy the edited `install.ps1` into it, start `notepad` and run `powershell -NoProfile -ExecutionPolicy Bypass -File <tmp>\install.ps1 -Force -WaitForPid <notepad pid>`; confirm it prints "Waiting…", then close notepad and it completes. Then run with `-Relaunch` and confirm the app starts. Record both in the task report.

- [ ] **Step 3: `deploy-share.ps1`.**

```powershell
<#
.SYNOPSIS
    Puts a built release on the office share so anyone can install it.
.DESCRIPTION
    Layout on the share:
        <SharePath>\<Version>\...           the unzipped package (never overwritten without -Force)
        <SharePath>\Install.cmd             double-click installer for the newest version
        <SharePath>\How to install.txt      the guide for office users
        <SharePath>\latest.json             {"version":"<Version>"} -- written LAST
    latest.json is last so no running copy is offered a version whose files
    are not all there yet.
#>
param(
    [Parameter(Mandatory)] [string] $SharePath,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [switch] $Force
)
$ErrorActionPreference = 'Stop'

$zip = Join-Path $PSScriptRoot "dist\PanoptoScheduler-$Version-win-x64.zip"
if (-not (Test-Path $zip)) { throw "No package at $zip. Run publish.ps1 first." }
if (-not (Test-Path $SharePath)) { throw "Share $SharePath is not reachable." }

$dest = Join-Path $SharePath $Version
if (Test-Path $dest) {
    if (-not $Force) { throw "$dest already exists. Re-run with -Force to replace it." }
    Remove-Item $dest -Recurse -Force
}

$staging = Join-Path $SharePath ".staging-$Version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $staging
Rename-Item $staging $dest

$cmd = @"
@echo off
rem Installs Panopto Scheduler $Version for the current user. No administrator needed.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0$Version\install.ps1" %*
set "code=%ERRORLEVEL%"
if not "%code%"=="0" echo Install did not complete. The reason is printed above.
echo.
pause
exit /b %code%
"@
[IO.File]::WriteAllText((Join-Path $SharePath 'Install.cmd'), $cmd, [Text.Encoding]::ASCII)
Copy-Item (Join-Path $dest 'How to install.txt') (Join-Path $SharePath 'How to install.txt') -Force

[IO.File]::WriteAllText((Join-Path $SharePath 'latest.json'), "{`"version`":`"$Version`"}", [Text.Encoding]::ASCII)
Write-Host "Deployed $Version to $SharePath. Office users: double-click $SharePath\Install.cmd"
```

- [ ] **Step 4: Test deploy against a local folder.** `pwsh src\deploy-share.ps1 -SharePath $env:TEMP\fake-share -Version 1.3.1` → the folder has `1.3.1\PanoptoScheduler.App.exe`, `Install.cmd`, `How to install.txt`, `latest.json` = `{"version":"1.3.1"}`. Run it again without `-Force` → it refuses. Run `ShareFeed` against it (the `ShareFeedTests` already cover the reader; here just `Get-Content latest.json`).

- [ ] **Step 5: `How to install.txt`** (ASCII):

```text
PANOPTO SCHEDULER - HOW TO INSTALL

1. Open this folder in File Explorer and double-click Install.cmd.
   It installs for you only. It does not need an administrator.
2. If Windows shows "Windows protected your PC", click "More info", then
   "Run anyway". This happens at most once per computer. The app is signed
   with the AV team's own certificate, which Windows does not know yet.
3. Start "Panopto Scheduler" from the Start menu.
4. Click Sign in. Your browser opens the Rotman Panopto sign-in page.
   Sign in with your own Panopto account.

WHAT YOU WILL SEE
The calendar shows the recordings your Panopto account can see. Changing
a recording needs the same rights you would need on the Panopto website.

UPDATES
When a new version is on this share, the app shows "Update now".
Click it; the app closes, updates, and opens again.

PROBLEMS
Send the newest file from %USERPROFILE%\.panopto-scheduler\logs to the AV team.
```

- [ ] **Step 6: publish.ps1.** Where it rebuilds `defaults.json` key by key from `credentials.json`, add `updateShare` when present (same pattern as `timeZone`). Where it copies `Install.cmd`/`install.ps1`/`uninstall.ps1` from `src\package\`, also copy `How to install.txt`.

- [ ] **Step 7: release.ps1.** Add `[Parameter(Mandatory)] [string] $SharePath` to `param`. Keep the csproj == zip == tag assertion and the GitHub release creation (the maintainer's archive). Replace the gist bump section (from `$gistId = ...` through the gist edit and its recovery message) with:

```powershell
# Share second, like the gist was: the GitHub release is the archive, the
# share is what the office installs from. deploy-share.ps1 writes latest.json
# last, so the banner never offers a half-copied version.
& (Join-Path $PSScriptRoot 'deploy-share.ps1') -SharePath $SharePath -Version $Version
```

Update the script's header comment to describe share-not-gist, and drop the gist half-ship resume logic (a re-run with `-Force` on `deploy-share.ps1` is the recovery). Run `pwsh -NoProfile -Command "Get-Command -Syntax src\release.ps1"` to confirm it parses.

- [ ] **Step 8: Commit.** `git commit -m "Ship to an office share: versioned folders, Install.cmd, latest.json last; installer update mode"`.

---

### Task 14: Docs and first-sign-in wording

**Files:**
- Modify: `README.md`, `AV-ALLOWLIST.md`, `src/PanoptoScheduler.App/SignInDialog.xaml`

- [ ] **Step 1: Sign-in dialog.** Under the dialog's existing explanatory text add one `TextBlock` (`TextWrapping="Wrap"`, `Margin="0,8,0,0"`): "Sign in with your own Panopto account. You will see the recordings that account can see, and you can change the ones it is allowed to change."
- [ ] **Step 2: README.** Replace the Install section's first steps with the share path ("Open `\\<share>\PanoptoScheduler` and double-click Install.cmd") while keeping the unzip-anywhere path as the alternative. In "Cutting a release" replace step 3 with `.\src\release.ps1 -Version <version> -SharePath \\<share>\PanoptoScheduler` and describe the share layout; delete the "release page is the download" / collaborator paragraphs and the gist references. Add rows to **What it does**: *Room status* ("Chips above the calendar; checked 10 minutes before and 3 minutes after each start, today only; a tray alert when a room is offline or late") and *Course series* ("Shift, skip dates, or change the room of a folder's upcoming sessions, previewed first"). Add a short **API use** paragraph: the cache rules and that the log's `API calls` line shows the daily total.
- [ ] **Step 3: AV-ALLOWLIST.md.** Add the share as the install source and the update flow (the app starts `powershell.exe … install.ps1 -WaitForPid … -Relaunch` from the share, then exits); add the tray icon; remove the gist host.
- [ ] **Step 4: Commit.** `git commit -am "Docs: office install from the share, room status, course series, API use"`.

---

### Task 15: Release 1.4.0 and office acceptance

**Files:**
- Modify: `src/PanoptoScheduler.App/PanoptoScheduler.App.csproj` (version)
- Modify: `~\.panopto-scheduler\credentials.json` (maintainer's machine — add `updateShare`) — **user action**

- [ ] **Step 1: Ask the user for the share path** (e.g. `\\rotman-fs\AV\PanoptoScheduler`) and confirm every office user can read it and the maintainer can write it. Add `"updateShare": "<that path>"` to the maintainer's `credentials.json` — the user edits it (the file holds the client secret; do not print it).
- [ ] **Step 2: Bump version** to 1.4.0 / 1.4.0.0 / 1.4.0.0. Full build + full suite (`*> src\test-results.log`) → `Passed!`, 0 failed; record the count.
- [ ] **Step 3: Push the branch** `git push camster91 modern-app`; confirm the CI run for the head commit is green (`gh run list -R camster91/panopto-scheduler -L 1`).
- [ ] **Step 4: Package.** `.\src\publish.ps1 -CertificateThumbprint C183B7EC2EEF106AC77464B85C149C49C528332F` (PowerShell tool, sandbox off). Confirm `defaults.json` inside the zip has `updateShare` (read it with `Expand-Archive` to a temp dir and `Select-String updateShare` — do not print the secret line).
- [ ] **Step 5: Release.** `.\src\release.ps1 -Version 1.4.0 -SharePath <share>` → GitHub release v1.4.0 exists; share has `1.4.0\`, `Install.cmd`, `How to install.txt`, `latest.json`.
- [ ] **Step 6: Office acceptance — the user does this on a colleague's machine** (fresh Windows profile, not the maintainer's):
  1. Open the share, double-click `Install.cmd` → installs without admin. **Record whether SmartScreen prompted** (Amendment 4).
  2. Launch from Start, sign in with the colleague's own Panopto account → calendar loads; the sign-in text is shown.
  3. Status chips appear after Refresh.
  4. Open Course series, pick a folder, preview a shift → preview lines, no writes.
  5. Close. Log `API calls` line present.
- [ ] **Step 7: Update path.** On the maintainer's machine with 1.3.1 installed: the 1.3.1 build still reads the gist, so it will *not* see the share — tell 1.3.1 users (from the release notes) to run `Install.cmd` on the share once. Then verify 1.4.0 → a test `1.4.1`-labelled deploy to a **temporary** share folder shows **Update now** and relaunches (use `-SharePath $env:TEMP\fake-share` with a locally edited `defaults.json` `updateShare`); remove the temp share afterwards.
- [ ] **Step 8: Retire the gist's banner for 1.3.1 users.** Bump the gist to `1.4.0` once (`gh gist edit f55820430e6625c7e862078ac0eb8bbb --filename version.json <file>`) so 1.3.1 copies show their banner, whose link opens the GitHub release page; the release notes there say "install from the share: `\\<share>\PanoptoScheduler\Install.cmd`".
- [ ] **Step 9: Update memory** (`panopto-scheduler-packaging.md`: share layout, gist retired after 1.4.0; `panopto-rest-facade-announcement.md`: measured Probe results). Commit any doc touch-ups: `git commit -am "Release 1.4.0"`; `git push camster91 modern-app`.
