using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace PanoptoScheduler.Core.Import;

public sealed record ScheduleImportOptions
{
    /// <summary>
    /// Read <c>10/04/2012</c> as 10 April rather than 4 October.
    ///
    /// <para>Default is month-first, which is what the original uploader did in
    /// practice. Whenever a date is genuinely ambiguous the row carries a
    /// warning, so a wrong guess is visible rather than silent.</para>
    /// </summary>
    public bool DayFirstDates { get; init; }

    /// <summary>Delimiter for CSV. The original tool made this configurable.</summary>
    public char Delimiter { get; init; } = ',';
}

/// <summary>
/// Reads the schedule files the original Panopto uploader accepted: the
/// <c>RecorderScheduleImport</c> XML and the equivalent CSV.
///
/// <para>Two deliberate departures from the original parsers, both because the
/// originals could fail in ways that were hard to diagnose:</para>
/// <list type="bullet">
///   <item><description>
///     Dates and times are parsed with <see cref="CultureInfo.InvariantCulture"/>
///     and an explicit format list. The originals used <c>DateTime.Parse</c> with
///     the machine's current culture, so the same file could import differently
///     on two workstations. Where a date is ambiguous, the row says so.
///   </description></item>
///   <item><description>
///     A header row is detected and skipped. <c>sample.csv</c> in the original
///     repository has one and <c>sample2.csv</c> does not, so both must work.
///   </description></item>
/// </list>
/// </summary>
public static class LegacyScheduleReader
{
    /// <summary>Column order in the legacy CSV.</summary>
    private const int ColTitle = 0;
    private const int ColRecorder = 1;
    private const int ColDate = 2;
    private const int ColStart = 3;
    private const int ColEnd = 4;
    private const int ColPresenter = 5;
    private const int ColFolder = 6;
    private const int ColWebcast = 7;

    private static readonly string[] UnambiguousDateFormats =
        ["yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd"];

    /// <summary>
    /// Dates that spell the month out, e.g. <c>26-feb-2013</c>.
    ///
    /// <para><b>These are not a nicety — they are what the real files contain.</b>
    /// <c>panoptoSchedule.cfm.xml</c> spells all eighteen of its rows
    /// <c>d-MMM-yyyy</c>, and <c>local_test.xml</c> has <c>30-Oct-2013</c>, so
    /// without this list the import reads neither. The parsers this replaced used
    /// <c>DateTime.Parse</c> against the machine's culture, which accepted them
    /// without anyone deciding to.</para>
    ///
    /// <para>They sit with the unambiguous formats because a spelled month cannot
    /// be mistaken for a day, whichever order the two are written in — the
    /// day-first/month-first question the numeric lists have to ask does not
    /// arise. Invariant month names match case-insensitively, which is what lets
    /// one list cover both <c>feb</c> and <c>Oct</c>.</para>
    ///
    /// <para>Two-digit years are deliberately absent. <c>ParseExact</c> pivots
    /// them through <c>TwoDigitYearMax</c> (2029 under the invariant culture),
    /// so <c>26-feb-30</c> reads as 1930 — a wrong-century import that only a
    /// future term's file could trigger. The real files all spell the year
    /// out, so the entry would buy coverage for a spelling that reads the
    /// century wrong.</para>
    /// </summary>
    private static readonly string[] MonthNameDateFormats =
    [
        "d-MMM-yyyy", "dd-MMM-yyyy",
        "d MMM yyyy", "dd MMM yyyy",
        "d/MMM/yyyy", "MMM d, yyyy",
    ];

    private static readonly string[] MonthFirstFormats = ["M/d/yyyy", "M/d/yy", "M-d-yyyy"];

    private static readonly string[] DayFirstFormats = ["d/M/yyyy", "d/M/yy", "d-M-yyyy"];

    private static readonly string[] TimeFormats =
    [
        "h:mm tt", "h:mm:ss tt", "hh:mm tt", "H:mm", "H:mm:ss", "HH:mm", "HH:mm:ss", "h tt", "H tt",
    ];

    /// <summary>Longer than this is almost certainly a typo, not a real session.</summary>
    private static readonly TimeSpan SuspiciousDuration = TimeSpan.FromHours(12);

    public static ScheduleImportResult ReadFile(string path, ScheduleImportOptions? options = null)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".csv" => ReadCsv(File.ReadAllText(path), options),
            ".xml" => ReadXml(File.ReadAllText(path), options),
            _ => new ScheduleImportResult([], [new ScheduleImportError(0,
                $"Expected a .csv or .xml file, got '{Path.GetFileName(path)}'.")]),
        };
    }

    public static ScheduleImportResult ReadCsv(string content, ScheduleImportOptions? options = null)
    {
        options ??= new ScheduleImportOptions();

        var rows = new List<ScheduleImportRow>();
        var errors = new List<ScheduleImportError>();

        var (records, unterminatedQuoteLine) = CsvReader.Read(content, options.Delimiter);

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];

            // Only the first record can be a header, and only if it clearly is one.
            if (i == 0 && LooksLikeHeader(record)) continue;

            var row = ParseRow(
                record.Line,
                title: Field(record, ColTitle),
                recorder: Field(record, ColRecorder),
                date: Field(record, ColDate),
                start: Field(record, ColStart),
                end: Field(record, ColEnd),
                presenter: Field(record, ColPresenter),
                folder: Field(record, ColFolder),
                webcast: Field(record, ColWebcast),
                options,
                errors);

            if (row is not null) rows.Add(row);
        }

        // Reported after the loop, so it reads as the file-level problem it is:
        // the rows before the quote were fine and are returned; every row from
        // the quote onward is not recoverable from this file.
        if (unterminatedQuoteLine is { } quoteLine)
            errors.Add(new ScheduleImportError(quoteLine,
                "A quoted field was left open here and never closed, so everything " +
                "from this line to the end of the file was read as one field and has " +
                "been dropped. Fix the missing quote and read the file again."));

        return new ScheduleImportResult(rows, errors);
    }

    /// <param name="options">
    /// Only <see cref="ScheduleImportOptions.DayFirstDates"/> applies — the XML has
    /// no delimiter. It is taken at all because the ambiguous-date warning tells the
    /// operator to "re-import with day-first dates to swap", and that sentence is
    /// the same for both formats; an XML read that dropped the option would offer a
    /// fix the toggle then silently failed to apply.
    /// </param>
    public static ScheduleImportResult ReadXml(string content, ScheduleImportOptions? options = null)
    {
        options ??= new ScheduleImportOptions();

        var rows = new List<ScheduleImportRow>();
        var errors = new List<ScheduleImportError>();

        XDocument document;
        try
        {
            document = XDocument.Parse(content, LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException ex)
        {
            return new ScheduleImportResult([], [new ScheduleImportError(
                ex.LineNumber is > 0 ? (int)ex.LineNumber : 0, $"Not valid XML: {ex.Message}")]);
        }

        var elements = document.Descendants().Where(e => e.Name.LocalName == "RecorderSchedule");

        var index = 0;
        foreach (var element in elements)
        {
            index++;

            var row = ParseRow(
                line: (element as System.Xml.IXmlLineInfo)?.LineNumber ?? index,
                title: Element(element, "Class"),
                recorder: Element(element, "Classroom"),
                date: Element(element, "RecordingDate"),
                start: Element(element, "RecordingStartTime"),
                end: Element(element, "RecordingEndTime"),
                presenter: Element(element, "Presenter"),
                folder: Element(element, "CourseTitle"),
                // The XML format has no webcast column; the original hard-coded false.
                webcast: null,
                options,
                errors);

            if (row is not null) rows.Add(row);
        }

        if (index == 0)
            errors.Add(new ScheduleImportError(0, "No <RecorderSchedule> elements found."));

        return new ScheduleImportResult(rows, errors);
    }

    private static ScheduleImportRow? ParseRow(
        int line,
        string? title,
        string? recorder,
        string? date,
        string? start,
        string? end,
        string? presenter,
        string? folder,
        string? webcast,
        ScheduleImportOptions options,
        List<ScheduleImportError> errors)
    {
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(title))
        {
            errors.Add(new ScheduleImportError(line, "Missing title."));
            return null;
        }

        if (string.IsNullOrWhiteSpace(recorder))
        {
            errors.Add(new ScheduleImportError(line, "Missing recorder name."));
            return null;
        }

        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(start) ||
            string.IsNullOrWhiteSpace(end))
        {
            errors.Add(new ScheduleImportError(line, "Missing date, start time or end time."));
            return null;
        }

        if (!TryParseDate(date, options.DayFirstDates, out var day, out var ambiguous))
        {
            errors.Add(new ScheduleImportError(line, $"Could not read the date '{date}'."));
            return null;
        }

        if (ambiguous)
        {
            // Both readings parsed, so swapping day and month is always a valid date.
            var other = new DateTime(day.Year, day.Day, day.Month);

            // FormattableString.Invariant, not plain interpolation: '/' in a
            // custom format string expands to the *current* culture's date
            // separator, so on a Canadian machine this warning would read
            // "10-4-2012" and on a US one "10/4/2012". The warning describes the
            // file, so it must not change with the reader's locale. It has to
            // stay a single interpolated string — concatenating two of them
            // makes a string, and Invariant only accepts the FormattableString.
            warnings.Add(FormattableString.Invariant(
                $"'{date}' could be {day:M/d/yyyy} or {other:M/d/yyyy} — read as {day:M/d/yyyy}. Re-import with day-first dates to swap."));
        }

        if (!TryParseTime(start, out var startTime))
        {
            errors.Add(new ScheduleImportError(line, $"Could not read the start time '{start}'."));
            return null;
        }

        if (!TryParseTime(end, out var endTime))
        {
            errors.Add(new ScheduleImportError(line, $"Could not read the end time '{end}'."));
            return null;
        }

        var startAt = day.Add(startTime);
        var endAt = day.Add(endTime);

        // An end earlier on the clock than the start runs past midnight. A row
        // carries one date and two clock times, so "10:00 PM to 12:00 AM" can only
        // mean the midnight that ends that evening — and putting the end on the
        // start's own date made it the midnight that began the day, an end before
        // the start, and the row an error. That also made the next-day warning
        // below unreachable: the end could never be on another date. Rolled by a
        // day and warned about rather than refused, the same way the pattern
        // generator treats an overnight slot. An end equal to the start is not
        // rolled — a zero-length row is a typo, not a 24-hour recording — so the
        // refusal below still catches it.
        if (endTime < startTime)
            endAt = endAt.AddDays(1);

        if (endAt <= startAt)
        {
            errors.Add(new ScheduleImportError(line, FormattableString.Invariant(
                $"End time {endAt:h:mm tt} is not after start time {startAt:h:mm tt}.")));
            return null;
        }

        if (endAt - startAt > SuspiciousDuration)
            warnings.Add(FormattableString.Invariant(
                $"Runs for {(endAt - startAt).TotalHours:0.#} hours — check the end time."));

        if (endAt.Date != startAt.Date)
            warnings.Add("The end time falls on the following day.");

        if (string.IsNullOrWhiteSpace(presenter))
            warnings.Add("No presenter; the session description will be left empty.");

        if (string.IsNullOrWhiteSpace(folder))
            warnings.Add("No folder given; the default recording folder will be used.");

        return new ScheduleImportRow
        {
            Line = line,
            Title = title.Trim(),
            RecorderName = recorder.Trim(),
            Start = startAt,
            End = endAt,
            Presenter = Blank(presenter),
            FolderHint = Blank(folder),
            IsBroadcast = ParseBroadcast(webcast),
            Warnings = warnings,
        };
    }

    /// <summary>
    /// A header is only treated as one when the date column fails to parse and
    /// the row reads like the documented header — anything else is a real row
    /// with a real problem, and saying so beats skipping it silently.
    /// </summary>
    private static bool LooksLikeHeader(CsvRecord record)
    {
        if (TryParseDate(Field(record, ColDate) ?? "", false, out _, out _)) return false;

        var lower = record.Fields.Select(f => (f ?? "").Trim().ToLowerInvariant()).ToList();

        return lower.Contains("title") || lower.Contains("recorder name") || lower.Contains("date");
    }

    /// <summary>
    /// Reads a date the way the file import does.
    ///
    /// <para><b>Public so the details panel can use the same one.</b> A date typed
    /// into a form and a date read from a spreadsheet column are the same kind of
    /// value, and a second parser written for the form would be a second set of
    /// accepted formats — so "10/4/2012" would mean one thing pasted into the
    /// panel and another in the file it was copied from. Two parsers is also two
    /// places for the ambiguous-day-first rule to drift.</para>
    /// </summary>
    /// <param name="ambiguous">
    /// Both readings parsed and they disagree. The date returned is the one the
    /// caller asked for; the flag is so the caller can say so rather than
    /// silently choosing.
    /// </param>
    public static bool TryReadDate(string? text, bool dayFirst, out DateTime date, out bool ambiguous)
        => TryParseDate(text ?? string.Empty, dayFirst, out date, out ambiguous);

    /// <summary>
    /// Reads a clock time the way the file import does. Public for the same reason
    /// as <see cref="TryReadDate"/>.
    /// </summary>
    /// <param name="time">Midnight when the text is not a time, so check the result.</param>
    public static bool TryReadTime(string? text, out TimeSpan time)
        => TryParseTime(text ?? string.Empty, out time);

    private static bool TryParseDate(string text, bool dayFirst, out DateTime date, out bool ambiguous)
    {
        date = default;
        ambiguous = false;

        text = text.Trim();

        if (TryExact(text, UnambiguousDateFormats, out date)) return true;

        // Before the numeric families, and never alongside them: a month name is
        // decisive on its own, so a row that spells one is never ambiguous and
        // must not be offered the day-first/month-first warning.
        if (TryExact(text, MonthNameDateFormats, out date)) return true;

        var monthFirst = TryExact(text, MonthFirstFormats, out var asMonthFirst);
        var dayFirstOk = TryExact(text, DayFirstFormats, out var asDayFirst);

        if (monthFirst && dayFirstOk)
        {
            if (asMonthFirst == asDayFirst)
            {
                date = asMonthFirst;
                return true;
            }

            // Both readings are valid and they disagree. Take the configured
            // one and let the caller warn.
            ambiguous = true;
            date = dayFirst ? asDayFirst : asMonthFirst;
            return true;
        }

        if (monthFirst) { date = asMonthFirst; return true; }
        if (dayFirstOk) { date = asDayFirst; return true; }

        return false;
    }

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;

        if (!DateTime.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return false;

        time = parsed.TimeOfDay;
        return true;
    }

    private static bool TryExact(string text, string[] formats, out DateTime value)
        => DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out value);

    private static string? Field(CsvRecord record, int index)
        => index < record.Fields.Count ? Blank(record.Fields[index]) : null;

    private static string? Element(XElement parent, string name)
        => Blank(parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value);

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The legacy column held <c>1</c>, <c>0</c>, or a boolean. The XML format
    /// had no such column and the original always passed false.
    /// </summary>
    private static bool ParseBroadcast(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" => true,
            _ => false,
        };
}

internal sealed record CsvRecord(int Line, IReadOnlyList<string> Fields);

/// <summary>
/// Minimal RFC 4180 reader.
///
/// <para>Hand-written rather than using the Visual Basic
/// <c>TextFieldParser</c> the original tool relied on, because this one reports
/// the physical line a record started on — which is what makes an import error
/// actionable when fields contain embedded newlines.</para>
/// </summary>
internal static class CsvReader
{
    /// <summary>
    /// Reads the whole file. The second value is the line an unterminated quote
    /// opened on, or null when every quote closed; when set, the record it
    /// belonged to is absent from the list, because its one field held the
    /// entire rest of the file.
    /// </summary>
    public static (IReadOnlyList<CsvRecord> Records, int? UnterminatedQuoteLine) Read(
        string text, char delimiter = ',')
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();

        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var fieldStarted = false;

        // Set once a quoted field has closed, until the delimiter that ends it.
        // Spaces and tabs in that stretch are held in `padding` until it is clear
        // whether they are padding around the quotes or part of the value. See
        // the opening-quote branch below.
        var closedQuote = false;
        var padding = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { inQuotes = false; closedQuote = true; }
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }

                continue;
            }

            // Whitespace before the opening quote does not make the quote
            // literal. RFC 4180 says it should, and Excel writes no such padding,
            // but a hand-edited file with ", " between columns is the ordinary
            // case — and taking the quote literally there splits
            // `…, "Smith, John", Finance` at the comma inside the name, which
            // moves every later column one place to the right: the folder lands in
            // the webcast column and the row books into the wrong place without a
            // word. Only blanks are skipped, and only before a quote: an unquoted
            // field keeps its padding here and is trimmed where it is read, as it
            // always was.
            if (c == '"' && !closedQuote && (!fieldStarted || IsPadding(field)))
            {
                field.Clear();
                inQuotes = true;
                fieldStarted = true;
                continue;
            }

            if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                closedQuote = false;
                padding.Clear();
                continue;
            }

            if (c == '\r') continue;

            if (c == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                closedQuote = false;
                padding.Clear();

                Add(records, fields, recordLine);
                line++;
                recordLine = line;
                continue;
            }

            // The other side of the same padding: `"Smith, John" ,` — blanks
            // between a closing quote and the delimiter belong to neither field.
            // Held rather than dropped, because they are only padding if the
            // delimiter is what comes next: in `"abc" def` they sit inside the
            // value, and keep the place they always had there.
            if (closedQuote && c is (' ' or '\t'))
            {
                padding.Append(c);
                continue;
            }

            if (padding.Length > 0)
            {
                field.Append(padding);
                padding.Clear();
            }

            field.Append(c);
            fieldStarted = true;
        }

        // EOF inside a quoted field: everything after the opening quote has
        // been accumulating into one giant field, and emitting that record
        // would report "1 broken row" while quietly eating every row after the
        // quote. Drop it and name the line instead, so the operator knows the
        // file ends mid-quote and where to look.
        if (inQuotes)
            return (records, recordLine);

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            Add(records, fields, recordLine);
        }

        return (records, null);
    }

    /// <summary>Whether what has accumulated so far is only spaces and tabs.</summary>
    private static bool IsPadding(StringBuilder field)
    {
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] is not (' ' or '\t')) return false;
        }

        return true;
    }

    private static void Add(List<CsvRecord> records, List<string> fields, int line)
    {
        // Skip blank lines rather than reporting them as broken rows.
        if (fields.Any(f => !string.IsNullOrWhiteSpace(f)))
            records.Add(new CsvRecord(line, fields.ToArray()));

        fields.Clear();
    }
}
