using PanoptoScheduler.Core.Import;

namespace PanoptoScheduler.Core.Tests;

public class LegacyScheduleReaderTests
{
    /// <summary>Verbatim from the original repository's sample.csv, header and all.</summary>
    private const string SampleCsvWithHeader = """
        Title,Recorder Name,Date,StartTime,EndTime,Presenter Description,Folder Id or Name,isWebcast
        MGEC611002_STAFF,JMHH240,10/04/2012,10:30 AM,12:00 PM,,RRFolder123,1
        STAT500401_BSTA550401_PSYC611401_ROSENBAUM_P,JMHH240,10/04/2012,12:00 PM,1:30 PM,,46d0ddd6-5919-4f09-ac42-abdf00832627,0
        MGEC611003_STAFF,JMHH240,10/04/2012,1:30 PM,3:00 PM,,Folder 23,1
        Leadership_Prog_Venture_Info_Session,JMHH240,10/04/2012,3:00 PM,4:00 PM,,TestFolder,1
        """;

    /// <summary>Verbatim from sample2.csv: no header, ISO dates, 24-hour times.</summary>
    private const string SampleCsvNoHeader = """
        My Recording,SCRIBE-2175680,2021-01-26,11:10:00,12:00:00,Stew Mixalot,TestFolder5,0
        My Recording,SCRIBE-2175680,2021-01-27,12:00:00,13:00:00,Stew Mixalot,TestFolder5,0
        """;

    private const string SampleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <RecorderScheduleImport xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
        	<RecorderSchedules>
        		<RecorderSchedule>
        			<Class>Title</Class>
        			<Classroom>Recorder Name</Classroom>
        			<RecordingDate>10/04/2012</RecordingDate>
        			<RecordingStartTime>10:30 AM</RecordingStartTime>
        			<RecordingEndTime>12:00 PM</RecordingEndTime>
        			<Presenter>Presenter Description</Presenter>
        			<CourseTitle>Folder Id or Name</CourseTitle>
        		</RecorderSchedule>
        		<RecorderSchedule>
        			<Class>MGEC611002_STAFF</Class>
        			<Classroom>JMHH240</Classroom>
        			<RecordingDate>10/04/2012</RecordingDate>
        			<RecordingStartTime>10:30 AM</RecordingStartTime>
        			<RecordingEndTime>12:00 PM</RecordingEndTime>
        		</RecorderSchedule>
        	</RecorderSchedules>
        </RecorderScheduleImport>
        """;

    // ---- CSV ------------------------------------------------------------

    [Fact]
    public void Reads_the_sample_csv_and_skips_its_header()
    {
        var result = LegacyScheduleReader.ReadCsv(SampleCsvWithHeader);

        Assert.Empty(result.Errors);
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal("MGEC611002_STAFF", result.Rows[0].Title);
        Assert.Equal("JMHH240", result.Rows[0].RecorderName);
    }

    [Fact]
    public void Reads_a_csv_that_has_no_header()
    {
        var result = LegacyScheduleReader.ReadCsv(SampleCsvNoHeader);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Stew Mixalot", result.Rows[0].Presenter);
    }

    [Fact]
    public void Reads_iso_dates_and_24_hour_times()
    {
        var result = LegacyScheduleReader.ReadCsv(SampleCsvNoHeader);
        var first = result.Rows[0];

        Assert.Equal(new DateTime(2021, 1, 26, 11, 10, 0), first.Start);
        Assert.Equal(new DateTime(2021, 1, 26, 12, 0, 0), first.End);
    }

    [Fact]
    public void Reads_am_pm_times()
    {
        var result = LegacyScheduleReader.ReadCsv(SampleCsvWithHeader);
        var last = result.Rows[3];

        Assert.Equal(new DateTime(2012, 10, 4, 15, 0, 0), last.Start);
        Assert.Equal(new DateTime(2012, 10, 4, 16, 0, 0), last.End);
    }

    [Fact]
    public void Reads_the_webcast_flag_in_every_spelling_the_legacy_tool_accepted()
    {
        var result = LegacyScheduleReader.ReadCsv("""
            A,R,2021-01-26,10:00,11:00,,F,1
            B,R,2021-01-26,10:00,11:00,,F,0
            C,R,2021-01-26,10:00,11:00,,F,true
            D,R,2021-01-26,10:00,11:00,,F,false
            E,R,2021-01-26,10:00,11:00,,F,
            """);

        Assert.Equal([true, false, true, false, false], result.Rows.Select(r => r.IsBroadcast));
    }

    [Fact]
    public void Tolerates_rows_that_stop_after_the_required_columns()
    {
        // Presenter and folder are genuinely optional; the original tool threw
        // IndexOutOfRange on these.
        var result = LegacyScheduleReader.ReadCsv("A,R,2021-01-26,10:00,11:00");

        Assert.Empty(result.Errors);
        Assert.Null(result.Rows[0].Presenter);
        Assert.Null(result.Rows[0].FolderHint);
    }

    [Fact]
    public void Keeps_commas_inside_quoted_fields()
    {
        // Column order is Title, Recorder, Date, Start, End, Presenter, Folder,
        // Webcast — so the quoted presenter sits in column 5, not column 6.
        var result = LegacyScheduleReader.ReadCsv(
            "\"STAT500, section 2\",JMHH240,2021-01-26,10:00,11:00,\"Rosenbaum, P\",\"Folder, 23\",1");

        Assert.Empty(result.Errors);
        Assert.Equal("STAT500, section 2", result.Rows[0].Title);
        Assert.Equal("Rosenbaum, P", result.Rows[0].Presenter);
        Assert.Equal("Folder, 23", result.Rows[0].FolderHint);
        Assert.True(result.Rows[0].IsBroadcast);
    }

    [Fact]
    public void Keeps_newlines_inside_quoted_fields_and_still_reports_later_line_numbers()
    {
        var result = LegacyScheduleReader.ReadCsv(
            "\"Line one\nLine two\",R,2021-01-26,10:00,11:00,,F,0\nB,R,2021-01-26,10:00,11:00,,F,0");

        Assert.Equal(2, result.Rows.Count);
        Assert.Contains("Line two", result.Rows[0].Title);

        // The second record starts on physical line 3, not record index 1.
        Assert.Equal(3, result.Rows[1].Line);
    }

    [Fact]
    public void Skips_blank_lines()
    {
        var result = LegacyScheduleReader.ReadCsv(
            "A,R,2021-01-26,10:00,11:00,,F,0\n\n\nB,R,2021-01-26,10:00,11:00,,F,0\n");

        Assert.Equal(2, result.Rows.Count);
        Assert.Empty(result.Errors);
    }

    // ---- Dates ----------------------------------------------------------

    [Fact]
    public void Flags_an_ambiguous_date_but_still_reads_it()
    {
        var result = LegacyScheduleReader.ReadCsv("A,R,10/04/2012,10:00 AM,11:00 AM,,F,0");
        var row = result.Rows[0];

        Assert.Equal(new DateTime(2012, 10, 4), row.Start.Date);

        // The warning names both readings, so it has to render with the same
        // separators the file used regardless of the machine's locale.
        Assert.Contains(row.Warnings, w => w.Contains("could be 10/4/2012 or 4/10/2012"));
        Assert.Contains(row.Warnings, w => w.Contains("read as 10/4/2012"));
    }

    [Fact]
    public void Day_first_option_swaps_an_ambiguous_date()
    {
        var result = LegacyScheduleReader.ReadCsv(
            "A,R,10/04/2012,10:00 AM,11:00 AM,,F,0",
            new ScheduleImportOptions { DayFirstDates = true });

        Assert.Equal(new DateTime(2012, 4, 10), result.Rows[0].Start.Date);
    }

    [Fact]
    public void An_unambiguous_date_is_never_flagged()
    {
        // 25 cannot be a month, so there is nothing to guess at.
        var result = LegacyScheduleReader.ReadCsv("A,R,25/04/2012,10:00 AM,11:00 AM,,F,0");

        Assert.Equal(new DateTime(2012, 4, 25), result.Rows[0].Start.Date);
        Assert.DoesNotContain(result.Rows[0].Warnings, w => w.Contains("could be"));
    }

    [Fact]
    public void Reports_an_unreadable_date()
    {
        var result = LegacyScheduleReader.ReadCsv("A,R,not-a-date,10:00,11:00,,F,0");

        Assert.Empty(result.Rows);
        Assert.Single(result.Errors);
        Assert.Contains("not-a-date", result.Errors[0].Message);
    }

    /// <summary>
    /// The date format the real files in this repository actually use.
    ///
    /// <para><b>Measured, not imagined.</b> <c>panoptoSchedule.cfm.xml</c> spells
    /// every one of its eighteen rows <c>d-MMM-yyyy</c> — <c>26-feb-2013</c> — and
    /// <c>local_test.xml</c> carries <c>30-Oct-2013</c>. The parsers this replaced
    /// handed the text to <c>DateTime.Parse</c> under the machine's culture, which
    /// accepted both without anyone deciding to. An invariant parser that knew
    /// only the numeric formats read neither file, and reported every row of both
    /// as an error — so the import whose whole job is moving this data moved none
    /// of it, and said so only in a per-row message nobody reads until later.</para>
    ///
    /// <para>Lower case in the first row and upper in the second, deliberately:
    /// invariant month names match case-insensitively, and if that ever stops
    /// being true, this is the test that says which half broke.</para>
    /// </summary>
    [Fact]
    public void Reads_the_month_name_dates_the_real_schedule_files_use()
    {
        var result = LegacyScheduleReader.ReadCsv("""
            A,JMHH240,26-feb-2013,10:00 AM,11:00 AM,,F,0
            B,JMHH240,30-Oct-2013,10:00 AM,11:00 AM,,F,0
            """);

        Assert.Empty(result.Errors);
        Assert.Equal(new DateTime(2013, 2, 26), result.Rows[0].Start.Date);
        Assert.Equal(new DateTime(2013, 10, 30), result.Rows[1].Start.Date);
    }

    /// <summary>
    /// A spelled month settles the day/month order by itself, so such a row is
    /// never offered the day-first/month-first guess and must not carry the
    /// warning either — the option is set here precisely to show it changes
    /// nothing. A warning on a date that reads exactly one way is how people
    /// learn to ignore warnings.
    /// </summary>
    [Fact]
    public void A_month_name_date_is_never_ambiguous()
    {
        var result = LegacyScheduleReader.ReadCsv(
            "A,R,26-feb-2013,10:00 AM,11:00 AM,,F,0",
            new ScheduleImportOptions { DayFirstDates = true });

        Assert.Equal(new DateTime(2013, 2, 26), result.Rows[0].Start.Date);
        Assert.DoesNotContain(result.Rows[0].Warnings, w => w.Contains("could be"));
    }

    /// <summary>
    /// And by the path the real file takes: the date lives in
    /// <c>&lt;RecordingDate&gt;</c>, not in a CSV column, so the XML reader has to
    /// reach the same parser with it.
    /// </summary>
    [Fact]
    public void Xml_reads_a_month_name_date()
    {
        var result = LegacyScheduleReader.ReadXml("""
            <RecorderScheduleImport>
              <RecorderSchedules>
                <RecorderSchedule>
                  <Class>STAT500401</Class>
                  <Classroom>JMHH240</Classroom>
                  <RecordingDate>30-Oct-2013</RecordingDate>
                  <RecordingStartTime>10:30 AM</RecordingStartTime>
                  <RecordingEndTime>12:00 PM</RecordingEndTime>
                </RecorderSchedule>
              </RecorderSchedules>
            </RecorderScheduleImport>
            """);

        Assert.Empty(result.Errors);
        Assert.Equal(new DateTime(2013, 10, 30, 10, 30, 0), result.Rows[0].Start);
    }

    // ---- Validation -----------------------------------------------------

    [Fact]
    public void Reports_an_end_time_that_is_not_after_the_start()
    {
        var result = LegacyScheduleReader.ReadCsv("A,R,2021-01-26,11:00,10:00,,F,0");

        Assert.Empty(result.Rows);
        Assert.Contains("not after", result.Errors[0].Message);
    }

    [Fact]
    public void Reports_a_missing_title_or_recorder()
    {
        var result = LegacyScheduleReader.ReadCsv(",R,2021-01-26,10:00,11:00,,F,0");

        Assert.Empty(result.Rows);
        Assert.Contains("title", result.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Warns_about_an_implausibly_long_session()
    {
        var result = LegacyScheduleReader.ReadCsv("A,R,2021-01-26,01:00,23:00,,F,0");

        Assert.Contains(result.Rows[0].Warnings, w => w.Contains("check the end time"));
    }

    [Fact]
    public void An_end_before_midnight_on_the_same_day_does_not_warn_about_the_next_day()
    {
        var result = LegacyScheduleReader.ReadCsv("A,R,2021-01-26,10:00,11:00,,F,0");

        Assert.DoesNotContain(result.Rows[0].Warnings,
            w => w.Contains("following day", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Does_not_treat_a_broken_first_row_as_a_header()
    {
        // It fails to parse, and nothing about it says "header" — so it is an
        // error, not something to silently discard.
        var result = LegacyScheduleReader.ReadCsv("oops,R,2021-01-26,10:00,11:00,,F,0");

        Assert.Single(result.Rows);
        Assert.Empty(result.Errors);
    }

    // ---- XML ------------------------------------------------------------

    [Fact]
    public void Reads_the_sample_xml()
    {
        var result = LegacyScheduleReader.ReadXml(SampleXml);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("MGEC611002_STAFF", result.Rows[1].Title);
        Assert.Equal("JMHH240", result.Rows[1].RecorderName);
    }

    [Fact]
    public void Xml_tolerates_a_row_without_presenter_or_course_title()
    {
        var result = LegacyScheduleReader.ReadXml(SampleXml);
        var second = result.Rows[1];

        Assert.Null(second.Presenter);
        Assert.Null(second.FolderHint);
        Assert.Equal(new DateTime(2012, 10, 4, 10, 30, 0), second.Start);
    }

    [Fact]
    public void Xml_never_sets_broadcast_because_the_format_has_no_such_field()
    {
        var result = LegacyScheduleReader.ReadXml(SampleXml);

        Assert.All(result.Rows, r => Assert.False(r.IsBroadcast));
    }

    [Fact]
    public void Xml_reports_real_line_numbers()
    {
        var result = LegacyScheduleReader.ReadXml(SampleXml);

        // The second RecorderSchedule starts around line 11 of the literal above.
        Assert.True(result.Rows[1].Line > result.Rows[0].Line);
    }

    [Fact]
    public void Xml_reports_when_there_are_no_schedule_elements()
    {
        var result = LegacyScheduleReader.ReadXml("<RecorderScheduleImport />");

        Assert.Empty(result.Rows);
        Assert.Contains("No <RecorderSchedule> elements", result.Errors[0].Message);
    }

    [Fact]
    public void Xml_reports_malformed_input_with_a_line_number()
    {
        var result = LegacyScheduleReader.ReadXml("<a>\n<b>\n</a>");

        Assert.Empty(result.Rows);
        Assert.Single(result.Errors);
        Assert.Contains("Not valid XML", result.Errors[0].Message);
    }

    [Fact]
    public void Reports_an_unsupported_extension()
    {
        var result = LegacyScheduleReader.ReadFile("schedule.xlsx");

        Assert.Empty(result.Rows);
        Assert.Contains(".csv or .xml", result.Errors[0].Message);
    }

    // ---- The parsers, on their own --------------------------------------
    //
    // Public so the details panel can read a typed date and a typed time with the
    // same code the import uses. These facts pin the contract that makes sharing
    // it safe: the panel hands over whatever is in a text box, which includes
    // null and whitespace, and a text box that empties must not throw.

    [Fact]
    public void Reads_a_time_the_way_a_schedule_file_spells_it()
    {
        Assert.True(LegacyScheduleReader.TryReadTime("11:10 AM", out var morning));
        Assert.Equal(new TimeSpan(11, 10, 0), morning);

        Assert.True(LegacyScheduleReader.TryReadTime("14:05", out var afternoon));
        Assert.Equal(new TimeSpan(14, 5, 0), afternoon);
    }

    [Fact]
    public void Rejects_a_time_it_cannot_read()
    {
        Assert.False(LegacyScheduleReader.TryReadTime("half past eleven", out _));
        Assert.False(LegacyScheduleReader.TryReadTime("", out _));
        Assert.False(LegacyScheduleReader.TryReadTime("25:00", out _));
    }

    /// <summary>
    /// The panel binds a text box straight through, so an empty box arrives as
    /// null. A parser that threw on that would take the window down when someone
    /// cleared a field, which is a thing people do.
    /// </summary>
    [Fact]
    public void An_empty_date_or_time_is_refused_rather_than_thrown_on()
    {
        Assert.False(LegacyScheduleReader.TryReadDate(null, false, out _, out _));
        Assert.False(LegacyScheduleReader.TryReadDate("   ", false, out _, out _));
        Assert.False(LegacyScheduleReader.TryReadTime(null, out _));
        Assert.False(LegacyScheduleReader.TryReadTime("   ", out _));
    }

    /// <summary>
    /// The ambiguous case, which is the whole reason this parser is shared rather
    /// than reimplemented: the panel has to warn about it exactly as the import
    /// does, and the flag is how the caller finds out.
    /// </summary>
    [Fact]
    public void Says_when_a_date_could_be_read_either_way_round()
    {
        Assert.True(LegacyScheduleReader.TryReadDate("10/4/2012", false, out var monthFirst, out var ambiguous));

        Assert.True(ambiguous);
        Assert.Equal(new DateTime(2012, 10, 4), monthFirst);

        Assert.True(LegacyScheduleReader.TryReadDate("10/4/2012", true, out var dayFirst, out _));
        Assert.Equal(new DateTime(2012, 4, 10), dayFirst);
    }

    [Fact]
    public void A_date_that_only_reads_one_way_is_not_ambiguous()
    {
        Assert.True(LegacyScheduleReader.TryReadDate("25/4/2012", false, out var day, out var ambiguous));

        Assert.False(ambiguous);
        Assert.Equal(new DateTime(2012, 4, 25), day);
    }
}
