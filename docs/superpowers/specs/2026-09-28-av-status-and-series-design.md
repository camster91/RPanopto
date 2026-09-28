# AV status and series operations — design

Date: 2026-09-28 · Target release: 1.4.0 · Branch: `modern-app`

## Intent

**Said:** ship Panopto Scheduler as the AV team's tool for scheduling and
recording, covering what the Panopto web dashboard does not — the calendar and
bulk changes. For this release the team needs **live room status** and **bulk
series changes**. API usage must be **very low**.

**Assumed (confirmed during design):** it stays an internal Windows app;
Panopto is the only source of truth (no database of our own); every endpoint
new to the app is measured against the tenant before code depends on it;
leaving `Data.svc`/SOAP for REST is a separate, later decision.

**Success:**
- An operator sees, without asking, when a room's recorder is offline before
  its session or a session has not started three minutes after its start.
- An operator can shift, skip dates of, or change the room of a whole course
  series in one previewed run.
- A normal day's automatic status checking costs roughly 20–40 calls; moving
  between calendar weeks costs none; the app reports its own call count.

**Out of scope for 1.4.0:** term setup through Panopto's bulk CSV API, caption
requests, folder permissions/owners, migrating the read off `Data.svc`.

## Chosen approach

Build on the proven backend (Data.svc read, SOAP writes). Add REST only where
nothing else answers the question (`sessions/inProgress/recording`). Considered
and rejected for this release: migrating to REST first (blocks the ship on an
unmeasured migration and the two-id-spaces problem) and routing series changes
through the bulk CSV API (it creates recordings; nothing in the spec shows it
shifting or cancelling existing ones).

## 0. API diet (whole app)

Today every week navigation (`CalendarViewModel.cs` ~1882, ~1895) and every
edit (~991, ~1803) calls `LoadAsync`, which walks **every page of every
scheduled session on the tenant** — `Data.svc` ignores its date parameters, so
filtering is client-side. That is the app's largest cost.

`ScheduleCache` (Core) holds the last full read with its time and completeness.

- **Views read the cache.** Week navigation, series selection, previews and
  status evaluation make no calls.
- **The cache is re-read only when:** (1) the operator presses Refresh;
  (2) sign-in; (3) the first action after it is more than 15 minutes old;
  (4) immediately before any write run, so conflict checks never use stale data.
- **After a write, patch instead of re-reading.** Apply the change to the
  cached copy using the write's response. A write classified *may have gone*
  (`ProblemText.IsTimeout`) marks the cache stale, forcing rule 4 before the
  next write.
- A patched cache keeps the time of its last full read; staleness is judged
  from that, not from the patch.
- **Incomplete reads stay visible.** If the last full read was incomplete
  (`PagedResult.Complete == false`), the existing warning stays on screen until
  a complete read replaces it.
- The status line shows the schedule's age: "Schedule as of 10:42 · Refresh".
- `ApiCallCounter` (Core) counts calls per endpoint per day; `AppLog` writes the
  running total with each call and a day summary on exit.

## 1. Live room status (checkpoints, no polling)

**Display:** a status strip above the calendar, one chip per room: Recording,
Idle, Offline, Late, or Unknown. Today's calendar blocks show the same state as
a badge.

**When calls happen — only:**
- On a Refresh or cache load: one `GET /api/v1/sessions/inProgress/recording`
  alongside it.
- At scheduled checkpoints, **today only**, computed by `CheckpointPlanner`
  from the cached day: one at **start − 10 min** (is the recorder online?) and
  one at **start + 3 min** (did it start?) for each *distinct* start time.
  Sessions sharing a start time share a checkpoint.

A checkpoint makes at most two calls: one `inProgress/recording`, and one
recorder listing (SOAP `ListRecorders`, which already carries `State`) —
the pre-start checkpoint needs only the listing, the post-start one only
`inProgress`. None happen outside scheduled hours, on days with nothing
booked, or while the app is closed. When the app starts mid-day, checkpoints
already in the past are skipped; the load-time `inProgress` read covers them.

**Evaluation:** `RoomStatusEvaluator` (Core, pure) combines the cached
sessions, the in-progress list and recorder states into a state per room and
per session. Times use `RoomClock` (the room's wall clock).

**Alerts:** a tray notification when a checkpoint finds a recorder offline
before its session or a session not started — once per session per day.
Clicking it brings the app forward on that room.

**Failures:** a failed checkpoint is not retried. Chips show "Unknown (checked
10:42)"; the next checkpoint or a manual Refresh tries again. Unknown never
renders as green.

**Open until measured (Step 0):** whether `inProgress` ids equal the `Data.svc`
session ids, the `ScheduleRecording` DeliveryIDs, or neither. If neither,
matching falls back to recorder + time window. The spec is updated with the
measured answer before `RoomStatusEvaluator` is written.

## 2. Bulk series operations (folder = series)

**Selection:** choose a folder; its future scheduled sessions come from the
cache, optionally narrowed by room, weekday and date range. No calls.

**Operations:**
- **Shift time** — by an offset or to a new start and length. Existing
  `RetimePlan` + SOAP `UpdateRecordingTime`, one call per session.
- **Skip dates** — pick dates on a mini-calendar; one `DeleteSessions` call for
  all matched sessions.
- **Change room** — `RoomChangePlan`: conflict-check the target room against
  the cache; book each session with `ScheduleRecording`; then delete, in one
  batched `DeleteSessions`, **only** the originals whose new booking succeeded.
  A failed or ambiguous booking leaves its original untouched and is listed.
  The preview states that moved sessions get new ids.

**Unchanged guarantees:** preview first, built from the cache, nothing written
until confirmed; JSONL audit log per row; results keep *did not go* separate
from *may have gone*; Escape-during-run guard. Each run starts with one fresh
full read (rule 4), then writes paced by `EndpointRateLimiter`.

**Cost for a 13-session series:** shift ≈ 14 calls, skip dates ≈ 2, change
room ≈ 15, each including the one pre-run read.

## 3. Measurement, testing, shipping

**Step 0 — measure (read-only, the user runs it).** Two commands in
`PanoptoScheduler.Probe`, which signs in through the app's own authenticator:

- `--status-now` — one `inProgress/recording` and one `remoteRecorders/search`
  call; prints field names, `State` values, and which id space the in-progress
  ids belong to.
- `--current-schedule` — one `GET /api/v1/scheduledRecordings/bulk/downloadResources/currentSchedule`;
  prints columns, row count, time format, and id overlap with a `Data.svc`
  read. Evidence for the later migration spec; does not block 1.4.0.

The measured answers are written into this spec before dependent code starts.

**Core units (pure, unit-tested):** `ScheduleCache`, `CheckpointPlanner`,
`RoomStatusEvaluator`, `SeriesSelector`, `RoomChangePlan`, `ApiCallCounter`.
About 60–90 new tests on top of the existing 460.

**App layer:** new `PanoptoScheduler.App.Tests` project covering the new view
models (status strip, checkpoint scheduling, series window) with fake clients
and no window. Then one real signed-in run against the tenant.

**Ship:** bump to 1.4.0; `publish.ps1 -CertificateThumbprint …` (signs);
`release.ps1 -Version 1.4.0` (GitHub release, then version gist). CI stays
build + test.

## Risks

- `inProgress` ids may match neither id space — handled by the fallback match.
- The tray notification needs a WinForms `NotifyIcon` reference in the WPF app;
  if it trips the EDR allowlist, fall back to an in-app banner only.
- Caching makes the calendar up to 15 minutes old between refreshes; the age
  shown in the status line and rule 4 before writes contain that.
