using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.Core.Scheduling;

/// <summary>
/// One row of a bulk operation, as the audit trail records it.
///
/// <para><b>One row per session, written as that row finishes</b> rather than
/// once at the end. The failure this exists for is the run that stops halfway —
/// a crash, a dropped network, a laptop lid — and a trail written at the end
/// records nothing at all in exactly that case, which is the case it was built
/// for.</para>
///
/// <para><b>Deliberately flat and string-heavy.</b> It is read by a person
/// looking at what happened on a date, and grepped by whoever is answering
/// "what did this account change". It spans operations whose outcome vocabularies
/// differ — an edit is applied or refused, a booking is scheduled, clashed,
/// skipped or failed — so <see cref="Outcome"/> is the producer's own word for
/// what happened rather than an enum this type has to own.</para>
/// </summary>
public sealed record BulkAuditEntry
{
    /// <summary>When the row finished, in UTC. UTC because a trail is compared across machines.</summary>
    [JsonPropertyName("at")]
    public required DateTime TimestampUtc { get; init; }

    /// <summary>
    /// Which run this row belongs to. Every row of one press of one button
    /// carries the same id, which is what makes "that move I did on Tuesday"
    /// answerable without guessing from timestamps.
    /// </summary>
    [JsonPropertyName("run")]
    public required Guid RunId { get; init; }

    /// <summary>The operation, as the UI names it: "rename", "delete", "retime", "book".</summary>
    [JsonPropertyName("op")]
    public required string Operation { get; init; }

    /// <summary>
    /// Whether this row was a preview. Recorded rather than skipped, because the
    /// preview is what the operator was shown before they pressed the button —
    /// so the trail holds the thing they agreed to as well as the thing they got.
    /// </summary>
    [JsonPropertyName("dry")]
    public required bool DryRun { get; init; }

    /// <summary>
    /// The session this row is about, or null when the row never reached one — a
    /// booking refused before anything was scheduled has a line number and a title
    /// and nothing else.
    ///
    /// <para>Null rather than <see cref="Guid.Empty"/>, so a trail can be filtered
    /// on this field without whoever writes the filter having to know that an id of
    /// all zeros means "there was no session" rather than "the session is
    /// 00000000-0000-0000-0000-000000000000".</para>
    /// </summary>
    [JsonPropertyName("session")]
    public required Guid? SessionId { get; init; }

    /// <summary>Absent when the row was refused before a name was known.</summary>
    [JsonPropertyName("name")]
    public string? SessionName { get; init; }

    /// <summary>Applied, WouldApply, Failed, Scheduled, Conflict, Skipped — the producer's word.</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    /// <summary>What the operator was told about this row. The whole sentence, not a code.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// The machine-readable specifics of the operation, when there are any — a
    /// retime's old and new slot, say. Kept apart from <see cref="Message"/>
    /// because a message is written to be read and this one to be compared.
    /// </summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
/// Where the rows of a bulk operation go as they complete.
///
/// <para><b>Returns whether the row was written, rather than throwing and rather
/// than swallowing.</b> Throwing would let a full disk fail a move that had
/// already gone through; swallowing would let someone work through a term's
/// recordings believing there was a record of it. So the row is attempted, the
/// answer comes back, and the run says once that its trail is incomplete. That
/// distinction is the whole reason this is an interface with a return value.</para>
/// </summary>
public interface IBulkAuditLog
{
    /// <summary>Appends one row. False means the trail is missing this row.</summary>
    bool TryRecord(BulkAuditEntry entry);
}

/// <summary>
/// A sink that records nothing and always succeeds, for the callers that have no
/// trail to write — every existing test, and any caller that has not been given a
/// log. Named rather than expressed as a null check at each call site so the
/// "no trail" case reads as a decision.
/// </summary>
public sealed class NullBulkAuditLog : IBulkAuditLog
{
    public static readonly NullBulkAuditLog Instance = new();

    public bool TryRecord(BulkAuditEntry entry) => true;
}

/// <summary>
/// The rows of one run, written as each one completes.
///
/// <para><b>Holds the run's identity and its count of unwritten rows</b>, which are
/// the two things every producer of a trail needs and neither of which it should be
/// composing for itself. The id makes every row of one press of one button findable
/// as a group; the count exists because the sentence that tells an operator their
/// trail is short has to read the same whether the run edited two hundred sessions
/// or booked them, and two copies of a warning is one copy that says something
/// different.</para>
/// </summary>
/// <param name="log">Where the rows go.</param>
/// <param name="operation">
/// The operation as the UI names it — "rename", "delete", "retime", "book". A word
/// rather than an enum because the edit and booking vocabularies differ and this
/// type serves both; see <see cref="BulkAuditEntry.Operation"/>.
/// </param>
/// <param name="dryRun">Whether these rows describe a preview or a write.</param>
public sealed class BulkAuditRun(IBulkAuditLog log, string operation, bool dryRun)
{
    private readonly Guid _runId = Guid.NewGuid();
    private int _rows;
    private int _missed;

    /// <summary>
    /// One sentence naming how many rows the trail is short, or null when it is
    /// whole.
    ///
    /// <para>Composed at the end rather than written on the first failure: a full
    /// disk fails every row, and two hundred identical warnings would bury the one
    /// number worth reading. Null is the ordinary case and stays free, because it is
    /// what every caller tests.</para>
    /// </summary>
    public string? Warning => _missed == 0 ? null
        : $"The audit trail could not be written and is missing {_missed} of "
          + $"{_rows} row(s) of this run. The run itself was unaffected.";

    /// <summary>How many rows were handed to this run, written or not.</summary>
    public int Rows => _rows;

    /// <summary>
    /// Appends one row, and returns whether it was written.
    ///
    /// <para>The interface's own answer, passed through rather than second-guessed.
    /// A caller is given the chance to react — the bulk editor turns it into one
    /// warning on the report — but nothing here decides that a missing row is
    /// acceptable, which is the failure mode a return value exists to prevent.</para>
    /// </summary>
    /// <param name="detail">
    /// Machine-readable specifics, when there are any — the line of the file a
    /// booking came from, say, which is a refused booking row's only real identity.
    /// </param>
    public bool Row(
        Guid? sessionId,
        string? name,
        string outcome,
        string message,
        string? detail = null)
    {
        _rows++;

        var written = log.TryRecord(new BulkAuditEntry
        {
            // UTC: a trail is read on another machine, on another day, and compared
            // against one written somewhere else.
            TimestampUtc = DateTime.UtcNow,
            RunId = _runId,
            Operation = operation,
            DryRun = dryRun,
            SessionId = sessionId,
            SessionName = name,
            Outcome = outcome,
            Message = message,
            Detail = detail,
        });

        if (!written) _missed++;

        return written;
    }
}

/// <summary>
/// The audit trail as a file of JSON lines, one file per day, beside the app log.
///
/// <para><b>Opened, appended and closed per row</b>, following
/// <see cref="AppLog"/>. Buffering would be faster and would lose precisely the
/// rows this exists to keep: the ones written just before the process died. A
/// two-hundred-row run pays two hundred small appends, which is not a cost worth
/// trading the guarantee for.</para>
///
/// <para><b>.jsonl rather than more log text</b>, so the trail can be counted and
/// filtered — "every delete this account made in September" is one line of
/// script against this and a parsing exercise against prose. It also settles the
/// newline problem for free: a message carrying one is a server's or an
/// exception's text, and escaping it is what keeps a row on a single line without
/// this app editing the text it is trying to record.</para>
/// </summary>
public sealed class FileBulkAuditLog : IBulkAuditLog
{
    /// <summary>Days a trail file is kept before <see cref="Prune"/> deletes it.</summary>
    private const int RetentionDays = 30;

    private static readonly object Gate = new();

    // Indented output would break the one-row-per-line format, and the default
    // already writes nulls — which this wants, because "name": null and a missing
    // "name" mean different things to whoever reads the trail back.
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _directory;

    /// <param name="directory">
    /// Where the trail goes. Defaults to the app log's own folder; a test passes a
    /// temporary one rather than writing into the user's real trail.
    /// </param>
    public FileBulkAuditLog(string? directory = null)
        => _directory = directory ?? AppLog.Directory;

    /// <summary>Today's file, so a support call asks for one predictable name.</summary>
    public string CurrentFile => Path.Combine(_directory,
        $"bulk-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.jsonl");

    public bool TryRecord(BulkAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            var line = JsonSerializer.Serialize(entry, Json);

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.AppendAllText(CurrentFile, line + Environment.NewLine);
            }

            return true;
        }
        catch
        {
            // Reported through the return value rather than here: the run knows
            // how many rows it has done and is the only thing that can tell the
            // operator their trail is short.
            return false;
        }
    }

    /// <summary>
    /// Deletes trails past the retention window, so a machine that has been
    /// running this for years does not accumulate them. Called at startup beside
    /// <see cref="AppLog.Prune"/>, and matching its window.
    /// </summary>
    public void Prune()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return;

            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "bulk-*.jsonl"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // Housekeeping only; never worth surfacing.
        }
    }
}
