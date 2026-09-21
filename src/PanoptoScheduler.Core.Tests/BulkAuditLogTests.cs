using System.Globalization;
using System.Text.Json;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// Collects what a run hands the trail, so a test can assert on the rows
/// themselves rather than on a file. The file's own behaviour is
/// <see cref="BulkAuditLogTests"/>'s subject; this is for the callers that produce
/// rows — the bulk editor and the scheduler — where the question is what they
/// <i>said</i>, not where it landed.
/// </summary>
internal sealed class RecordingAuditLog : IBulkAuditLog
{
    public List<BulkAuditEntry> Entries { get; } = [];

    /// <summary>
    /// The session each row is about, in order. Nullable because the trail's id is —
    /// a refused booking row has none — so a test can see that an edit row always
    /// has one.
    /// </summary>
    public IEnumerable<Guid?> Sessions => Entries.Select(e => e.SessionId);

    /// <summary>
    /// When set, every write after this many have succeeded is refused. This is how
    /// a partial shortfall is reached without a real disk filling up.
    /// </summary>
    public int? FailFrom { get; init; }

    public bool TryRecord(BulkAuditEntry entry)
    {
        if (FailFrom is { } limit && Entries.Count >= limit) return false;

        Entries.Add(entry);
        return true;
    }
}

/// <summary>
/// The trail a bulk run leaves. Every fact here is about the one case the trail
/// exists for — the run that does not reach the end — because that is the case a
/// trail written after the fact cannot serve.
/// </summary>
public sealed class BulkAuditLogTests : IDisposable
{
    private static readonly Guid Run = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Session = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTime At = new(2026, 5, 4, 14, 30, 0, DateTimeKind.Utc);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"panopto-audit-{Guid.NewGuid():N}");

    private FileBulkAuditLog Log() => new(_folder);

    private static BulkAuditEntry Entry(string outcome = "Applied", string message = "Moved.") => new()
    {
        TimestampUtc = At,
        RunId = Run,
        Operation = "retime",
        DryRun = false,
        SessionId = Session,
        SessionName = "FIN 101 — Lecture 01",
        Outcome = outcome,
        Message = message,
    };

    private string[] Lines() => File.ReadAllLines(new FileBulkAuditLog(_folder).CurrentFile);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // A test that leaves a temporary folder behind has not proved anything
            // wrong; failing it here would only obscure the real result.
        }
    }

    // ---- One row per line, written as it finishes -------------------------

    /// <summary>
    /// The property the whole type exists for: rows are on disk individually, so a
    /// run that stops at row three of twelve has a trail describing three rows
    /// rather than nothing. Asserted by reading the file back after each write,
    /// which is what a post-crash reader would see.
    /// </summary>
    [Fact]
    public void Each_row_is_on_disk_before_the_next_one_starts()
    {
        var log = Log();

        Assert.True(log.TryRecord(Entry()));

        // No flush, no dispose, no run finished — the file already holds it.
        Assert.Single(Lines());

        Assert.True(log.TryRecord(Entry()));
        Assert.Equal(2, Lines().Length);
    }

    /// <summary>
    /// One row is one line, and everything a reader needs to reconstruct what
    /// happened is on it. Asserted field by field rather than on a substring,
    /// because the names are the file's contract with whoever greps it later.
    /// </summary>
    [Fact]
    public void A_row_carries_the_run_the_session_the_outcome_and_the_message()
    {
        Assert.True(Log().TryRecord(Entry()));

        using var row = JsonDocument.Parse(Assert.Single(Lines()));
        var root = row.RootElement;

        Assert.Equal(Run, root.GetProperty("run").GetGuid());
        Assert.Equal("retime", root.GetProperty("op").GetString());
        Assert.False(root.GetProperty("dry").GetBoolean());
        Assert.Equal(Session, root.GetProperty("session").GetGuid());
        Assert.Equal("FIN 101 — Lecture 01", root.GetProperty("name").GetString());
        Assert.Equal("Applied", root.GetProperty("outcome").GetString());
        Assert.Equal("Moved.", root.GetProperty("message").GetString());
        Assert.Equal(At, root.GetProperty("at").GetDateTime().ToUniversalTime());
    }

    /// <summary>
    /// A preview belongs in the trail. It is the thing the operator was shown
    /// before they pressed the button, so a trail holding only the writes cannot
    /// answer "what was this run authorised against" — and the two are told apart
    /// by the flag rather than by the operation name.
    /// </summary>
    [Fact]
    public void A_preview_is_recorded_and_flagged_as_one()
    {
        Assert.True(Log().TryRecord(Entry() with { DryRun = true, Outcome = "WouldApply" }));

        using var row = JsonDocument.Parse(Assert.Single(Lines()));

        Assert.True(row.RootElement.GetProperty("dry").GetBoolean());
        Assert.Equal("WouldApply", row.RootElement.GetProperty("outcome").GetString());
    }

    // ---- The things that would corrupt the file ---------------------------

    /// <summary>
    /// The message is the server's text or an exception's, so a newline in it is
    /// not this app's to assume away — and a literal one would split a row across
    /// two lines and make every line after it unparseable.
    ///
    /// <para><b>Escaped by the serialiser, not mangled by this app.</b> An earlier
    /// version replaced the newlines with spaces, which kept the file valid and
    /// silently rewrote the text: a two-line server error would come back as one
    /// line to the person reading it. So this asserts both halves — the row is one
    /// line on disk, and the message parses back exactly as it arrived.</para>
    /// </summary>
    [Fact]
    public void A_message_containing_a_newline_still_occupies_one_line()
    {
        var message = "First line\r\nSecond line\nThird";

        Assert.True(Log().TryRecord(Entry(message: message)));

        var only = Assert.Single(Lines());

        // One line on disk: the breaks are the two characters \ and n.
        Assert.Contains("\\n", only);

        // And unchanged when read back, which is what the collapsing got wrong.
        using var row = JsonDocument.Parse(only);
        Assert.Equal(message, row.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// A null name is written as null rather than omitted, so "this row never got
    /// as far as a name" and "this row's name was not recorded" stay distinguishable
    /// in the file. Documented by a fact because it is the kind of thing a later
    /// tidy-up would helpfully configure away.
    /// </summary>
    [Fact]
    public void An_absent_name_is_written_as_null_rather_than_left_out()
    {
        Assert.True(Log().TryRecord(Entry() with { SessionName = null }));

        var only = Assert.Single(Lines());

        Assert.Contains("\"name\":null", only);
    }

    // ---- Failure is reported, never thrown and never swallowed -----------

    /// <summary>
    /// A destination that cannot be written answers false. Not an exception, which
    /// would fail a move that had already gone through; and not a silent true,
    /// which would let someone work through a term's recordings believing there
    /// was a record of it.
    /// </summary>
    [Fact]
    public void A_destination_that_cannot_be_written_reports_failure_instead_of_throwing()
    {
        // A file where the directory should be: creating the directory must fail.
        var occupied = Path.Combine(Path.GetTempPath(), $"panopto-audit-file-{Guid.NewGuid():N}");
        File.WriteAllText(occupied, "not a directory");

        try
        {
            Assert.False(new FileBulkAuditLog(occupied).TryRecord(Entry()));
        }
        finally
        {
            File.Delete(occupied);
        }
    }

    [Fact]
    public void A_run_with_no_trail_to_write_succeeds_and_records_nothing()
    {
        Assert.True(NullBulkAuditLog.Instance.TryRecord(Entry()));
    }

    /// <summary>
    /// A row that never reached a session says so, rather than carrying an id of all
    /// zeros. A booking refused before anything was scheduled has a line number and a
    /// title and no session at all, and someone filtering this file on the session
    /// field should not have to know that zeros stands for none.
    /// </summary>
    [Fact]
    public void A_row_that_never_reached_a_session_says_so_rather_than_carrying_zeros()
    {
        Assert.True(Log().TryRecord(Entry() with { SessionId = null }));

        var only = Assert.Single(Lines());

        Assert.Contains("\"session\":null", only);
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", only);
    }

    // ---- The run that produces the rows -----------------------------------

    /// <summary>
    /// Every row of one run carries the same id, and a run whose rows all land says
    /// nothing about the trail. Those two together are the ordinary case: the id is
    /// what makes a run findable, and the silence is what every caller tests.
    /// </summary>
    [Fact]
    public void A_runs_rows_share_an_id_and_a_whole_trail_is_silent()
    {
        var log = Log();
        var run = new BulkAuditRun(log, "book", dryRun: true);

        Assert.True(run.Row(Session, "FIN 101", "WouldSchedule", "Would record.", detail: "line 7"));
        Assert.True(run.Row(null, "A refused row", "Skipped", "No recorder named 'X'."));

        Assert.Equal(2, run.Rows);
        Assert.Null(run.Warning);

        var rows = Lines();
        Assert.Equal(2, rows.Length);

        using var first = JsonDocument.Parse(rows[0]);
        using var second = JsonDocument.Parse(rows[1]);

        Assert.Equal("book", first.RootElement.GetProperty("op").GetString());
        Assert.True(first.RootElement.GetProperty("dry").GetBoolean());
        Assert.Equal(
            first.RootElement.GetProperty("run").GetGuid(),
            second.RootElement.GetProperty("run").GetGuid());

        Assert.Equal("line 7", first.RootElement.GetProperty("detail").GetString());
    }

    /// <summary>
    /// The shortfall sentence is composed at the end and counts what is missing
    /// rather than what was attempted — the number that matters to whoever has to
    /// reconstruct the run is how much of the record is gone.
    ///
    /// <para>Asserted through a run whose third row cannot be written, because a
    /// partial failure is the case a per-row warning cannot describe: it does not
    /// know yet whether the next row will also fail, and "2 of 3" is only knowable
    /// once there is no next row.</para>
    /// </summary>
    [Fact]
    public void A_shortfall_is_counted_and_reported_in_one_sentence()
    {
        // A file where the directory should be: every write fails.
        var occupied = Path.Combine(Path.GetTempPath(), $"panopto-audit-file-{Guid.NewGuid():N}");
        File.WriteAllText(occupied, "not a directory");

        try
        {
            var run = new BulkAuditRun(new FileBulkAuditLog(occupied), "move", dryRun: false);

            Assert.False(run.Row(Session, "One", "Applied", "Moved."));
            Assert.False(run.Row(Session, "Two", "Applied", "Moved."));

            Assert.Equal(2, run.Rows);

            var warning = Assert.IsType<string>(run.Warning);

            Assert.Contains("2 of 2", warning);
            Assert.Contains("The run itself was unaffected.", warning);
        }
        finally
        {
            File.Delete(occupied);
        }
    }

    // ---- Naming and retention --------------------------------------------

    /// <summary>One predictable name a day, so a support call asks for a date.</summary>
    [Fact]
    public void The_file_is_named_for_the_day_it_covers()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var name = Path.GetFileName(Log().CurrentFile);

        Assert.Equal($"bulk-{today}.jsonl", name);
    }

    /// <summary>
    /// Retention is this log's own file pattern only: the app's own log is a
    /// separate trail with its own pruner, and an audit that deleted the file
    /// someone was about to read would be worse than one that grew.
    /// </summary>
    [Fact]
    public void Pruning_removes_old_trails_and_leaves_everything_else_alone()
    {
        Directory.CreateDirectory(_folder);

        var log = Log();
        var fresh = log.CurrentFile;
        File.WriteAllText(fresh, "{}");

        var old = Path.Combine(_folder, "bulk-2020-01-01.jsonl");
        File.WriteAllText(old, "{}");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-60));

        var appLog = Path.Combine(_folder, "app-2020-01-01.log");
        File.WriteAllText(appLog, "old");
        File.SetLastWriteTime(appLog, DateTime.Now.AddDays(-60));

        log.Prune();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(appLog));
    }
}
