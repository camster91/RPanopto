using PanoptoScheduler.Core.Import;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The pattern that fills the grid, and the two ceilings that stop it running
/// away.
///
/// <para>What is deliberately not tested here is the booking: the generator's
/// whole output is <see cref="ScheduleImportRow"/>, the record a CSV already
/// produces, so everything downstream — the preview, the dry run, the rate limit,
/// the per-row report — is the code <c>BulkSchedulerTests</c> already covers.
/// A pattern that booked by its own route would be a second set of guards, and one
/// of them would be missing something. These facts are only about the rows.</para>
///
/// <para>Every date here is written out in full. Nothing asks this machine what
/// day it is, for the same reason <c>RoomClockTests</c> does not: the answer would
/// change with the workstation.</para>
/// </summary>
public class BookingPatternGeneratorTests
{
    private static TimeZoneInfo Toronto => RoomClock.Resolve("America/Toronto");

    /// <summary>
    /// A pattern that generates: one room, Monday/Wednesday/Friday, 10:00–11:00,
    /// across three weeks of September 2026 — nine dates, because the 7th is a
    /// Monday and the 25th is a Friday.
    /// </summary>
    private static BookingPattern Base() => new()
    {
        Recorders = ["RSM 1210"],
        From = new DateOnly(2026, 9, 7),
        To = new DateOnly(2026, 9, 25),
        Weekdays = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
        Start = new TimeSpan(10, 0, 0),
        End = new TimeSpan(11, 0, 0),
        TitleFormat = "Lec {n}",
    };

    private static IReadOnlyList<DateOnly> DaysOf(IEnumerable<ScheduleImportRow> rows)
        => rows.Select(r => DateOnly.FromDateTime(r.Start)).Distinct().ToList();

    // ---- what it generates ----------------------------------------------

    [Fact]
    public void An_MWF_pattern_covers_every_ticked_day_and_no_other()
    {
        var rows = BookingPatternGenerator.Generate(Base());

        Assert.Equal(9, rows.Count);

        DateOnly[] expected =
        [
            new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 11),
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 18),
            new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 25),
        ];

        Assert.Equal(expected, DaysOf(rows));

        Assert.All(rows, row => Assert.Contains(
            row.Start.DayOfWeek,
            new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }));

        Assert.All(rows, row => Assert.Equal(TimeSpan.FromHours(1), row.End - row.Start));
    }

    /// <summary>
    /// Date-major, room-minor. A room reads down a column rather than along a
    /// diagonal, which is the whole reason for the order: "is every Tuesday there?"
    /// is then a glance down one column.
    /// </summary>
    [Fact]
    public void A_room_reads_down_a_column_not_across_a_row()
    {
        var rows = BookingPatternGenerator.Generate(Base() with { Recorders = ["RSM 1210", "RSM 1212"] });

        Assert.Equal(18, rows.Count);

        // The first two rows are one date, two rooms...
        Assert.Equal(new DateOnly(2026, 9, 7), DateOnly.FromDateTime(rows[0].Start));
        Assert.Equal("RSM 1210", rows[0].RecorderName);
        Assert.Equal(new DateOnly(2026, 9, 7), DateOnly.FromDateTime(rows[1].Start));
        Assert.Equal("RSM 1212", rows[1].RecorderName);

        // ...and the third moves on a date rather than on a room.
        Assert.Equal(new DateOnly(2026, 9, 9), DateOnly.FromDateTime(rows[2].Start));
        Assert.Equal("RSM 1210", rows[2].RecorderName);

        // The two rooms repeat in the same order on every date, which is what makes
        // a room a fixed column rather than a diagonal.
        for (var i = 0; i < rows.Count; i++)
            Assert.Equal(i % 2 == 0 ? "RSM 1210" : "RSM 1212", rows[i].RecorderName);
    }

    /// <summary>
    /// <c>{n}</c> counts within a room. Three rooms over twelve weeks is three
    /// series of 1 to 12, not one series of 1 to 36 — "Lecture 5" means the fifth
    /// week of that course, and a run-wide counter would make it the fifth booking
    /// of the batch.
    /// </summary>
    [Fact]
    public void The_sequence_number_counts_within_a_room_not_across_the_run()
    {
        var rows = BookingPatternGenerator.Generate(Base() with
        {
            Recorders = ["RSM 1210", "RSM 1212"],
            TitleFormat = "{room} {n}",
        });

        foreach (var room in new[] { "RSM 1210", "RSM 1212" })
        {
            Assert.Equal(
                Enumerable.Range(1, 9).Select(n => $"{room} {n}"),
                rows.Where(r => r.RecorderName == room).Select(r => r.Title));
        }
    }

    /// <summary>
    /// <c>{nn}</c> pads, and padding it must not leave a stray <c>n</c> behind:
    /// <c>{n}</c> is a prefix of <c>{nn}</c>, so the reverse order of replacement
    /// would turn every padded number into <c>0n</c>.
    /// </summary>
    [Fact]
    public void A_padded_number_does_not_leave_a_stray_n()
    {
        var rows = BookingPatternGenerator.Generate(Base() with { TitleFormat = "W{nn}/{n}" });

        Assert.Equal("W01/1", rows[0].Title);
        Assert.Equal("W09/9", rows[8].Title);
    }

    [Fact]
    public void The_name_carries_the_room_the_date_the_day_the_number_and_the_total()
    {
        var rows = BookingPatternGenerator.Generate(Base() with
        {
            TitleFormat = "{room} {date} {dow} ({n} of {total})",
        });

        Assert.Equal("RSM 1210 2026-09-07 Mon (1 of 9)", rows[0].Title);
        Assert.Equal("RSM 1210 2026-09-25 Fri (9 of 9)", rows[8].Title);
    }

    /// <summary>A skip date is reading week, or the day the room is being painted.</summary>
    [Fact]
    public void A_skip_date_is_left_out()
    {
        var rows = BookingPatternGenerator.Generate(Base() with
        {
            SkipDates = [new DateOnly(2026, 9, 16)],
        });

        Assert.Equal(8, rows.Count);
        Assert.DoesNotContain(new DateOnly(2026, 9, 16), DaysOf(rows));
    }

    /// <summary>
    /// A skip date outside the range is harmless rather than an error — the list
    /// is compared by date, and a term's holidays outlive one pattern.
    /// </summary>
    [Fact]
    public void A_skip_date_outside_the_range_changes_nothing()
    {
        var rows = BookingPatternGenerator.Generate(Base() with
        {
            SkipDates = [new DateOnly(2027, 1, 4)],
        });

        Assert.Equal(9, rows.Count);
    }

    // ---- rooms -----------------------------------------------------------

    /// <summary>
    /// The same room on the same date is one recording, and the row that survives
    /// says so. Panopto would take both, and the second would be a duplicate
    /// somebody has to find later — or a double booking of a room.
    /// </summary>
    [Fact]
    public void A_room_listed_twice_for_a_date_collapses_and_the_survivor_says_so()
    {
        var rows = BookingPatternGenerator.Generate(Base() with
        {
            Recorders = ["RSM 1210", "RSM 1210"],
        });

        Assert.Equal(9, rows.Count);
        Assert.All(rows, row => Assert.Contains(
            row.Warnings, w => w.Contains("more than once", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Two spellings of one room are one room. Recorder names resolve
    /// case-insensitively when they are booked, so <c>RSM 1210</c> and
    /// <c>rsm 1210</c> reach the same recorder — counting them separately would be
    /// the same double booking by another route.
    /// </summary>
    [Fact]
    public void Two_spellings_of_one_room_are_the_same_room()
    {
        var rows = BookingPatternGenerator.Generate(Base() with
        {
            Recorders = ["RSM 1210", "rsm 1210"],
        });

        Assert.Equal(9, rows.Count);
        Assert.All(rows, row => Assert.Contains(
            row.Warnings, w => w.Contains("more than once", StringComparison.Ordinal)));
    }

    /// <summary>An end before the start is the next morning, but only on request.</summary>
    [Fact]
    public void An_end_before_the_start_runs_into_the_morning_only_when_it_is_allowed()
    {
        var evening = Base() with
        {
            From = new DateOnly(2026, 9, 7),
            To = new DateOnly(2026, 9, 7),
            Weekdays = [DayOfWeek.Monday],
            Start = new TimeSpan(23, 0, 0),
            End = new TimeSpan(1, 0, 0),
        };

        Assert.Contains(BookingPatternGenerator.Problems(evening),
            p => p.Contains("overnight", StringComparison.OrdinalIgnoreCase));

        var rows = BookingPatternGenerator.Generate(evening with { AllowOvernight = true });

        var row = Assert.Single(rows);
        Assert.Equal(new DateTime(2026, 9, 7, 23, 0, 0), row.Start);
        Assert.Equal(new DateTime(2026, 9, 8, 1, 0, 0), row.End);
        Assert.Contains(row.Warnings, w => w.Contains("morning", StringComparison.Ordinal));
    }

    // ---- the two ceilings ------------------------------------------------

    /// <summary>
    /// A mistyped year is answered by arithmetic, not by walking it. The complaint
    /// names the span; it does not first enumerate a hundred years of days to count
    /// the rows, which is what makes a wrong year freeze the window instead of
    /// reporting itself.
    /// </summary>
    [Fact]
    public void A_range_years_too_long_is_refused_and_names_the_typo()
    {
        var problems = BookingPatternGenerator.Problems(Base() with
        {
            To = new DateOnly(2126, 9, 25),
        });

        var problem = Assert.Single(problems);
        Assert.Contains("typo", problem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("over the", problem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The row ceiling, reported with the count so the message says what to narrow.
    /// Four rooms over two years is 2920 recordings.
    /// </summary>
    [Fact]
    public void A_pattern_over_the_row_ceiling_is_refused_with_the_count()
    {
        var tooMany = Base() with
        {
            Recorders = ["RSM 1210", "RSM 1212", "RSM 1214", "RSM 1216"],
            From = new DateOnly(2026, 1, 1),
            To = new DateOnly(2027, 12, 31),
            Weekdays =
            [
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
            ],
        };

        var problem = Assert.Single(BookingPatternGenerator.Problems(tooMany));

        Assert.Contains("2920", problem, StringComparison.Ordinal);
        Assert.Contains(BookingPatternGenerator.MaxRows.ToString(), problem, StringComparison.Ordinal);

        Assert.Empty(BookingPatternGenerator.Generate(tooMany));
    }

    /// <summary>
    /// The hour that does not exist. It arrives twice a year in Toronto, and
    /// without this check it would reach the operator as one failed row per date —
    /// because <c>RoomClock.ToWire</c> is right to throw for a wall clock the zone
    /// jumps over. Said once, before anything is drawn, is the version that can be
    /// acted on.
    /// </summary>
    [Fact]
    public void A_start_inside_the_hour_that_does_not_exist_is_flagged()
    {
        // 2 March 2026 is the second Sunday of March: 02:00–03:00 does not happen.
        var springForward = Base() with
        {
            From = new DateOnly(2026, 3, 8),
            To = new DateOnly(2026, 3, 8),
            Weekdays = [DayOfWeek.Sunday],
            Start = new TimeSpan(2, 30, 0),
            End = new TimeSpan(3, 30, 0),
        };

        var problem = Assert.Single(BookingPatternGenerator.Problems(springForward, Toronto));

        Assert.Contains("does not exist", problem, StringComparison.Ordinal);
        Assert.Contains("2026-03-08", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The end goes through <c>RoomClock.ToWire</c> too. Only the start used to be
    /// checked, so a slot that starts at an hour that exists and ends inside the
    /// gap was generated and then failed at write time on every one of those
    /// dates. The overnight case ends on the next morning, which is the date whose
    /// 02:30 is missing: the Saturday-night slot is the one to flag.
    /// </summary>
    [Theory]
    [InlineData(1, 30, 8, DayOfWeek.Sunday, false)]   // 01:30–02:30 on the Sunday
    [InlineData(22, 0, 7, DayOfWeek.Saturday, true)]  // 22:00 Saturday – 02:30 Sunday
    public void An_end_inside_the_hour_that_does_not_exist_is_flagged(
        int startHour, int startMinute, int day, DayOfWeek weekday, bool overnight)
    {
        var pattern = Base() with
        {
            From = new DateOnly(2026, 3, day),
            To = new DateOnly(2026, 3, day),
            Weekdays = [weekday],
            Start = new TimeSpan(startHour, startMinute, 0),
            End = new TimeSpan(2, 30, 0),
            AllowOvernight = overnight,
        };

        var problem = Assert.Single(BookingPatternGenerator.Problems(pattern, Toronto));

        Assert.Contains("does not exist", problem, StringComparison.Ordinal);
        Assert.Empty(BookingPatternGenerator.Generate(pattern, Toronto));
    }

    /// <summary>
    /// The other half of it: the same date and room at a time that does exist is
    /// not flagged, so the check above is not simply refusing the whole day.
    /// </summary>
    [Fact]
    public void A_start_outside_that_hour_is_not_flagged()
    {
        var ordinary = Base() with
        {
            From = new DateOnly(2026, 3, 8),
            To = new DateOnly(2026, 3, 8),
            Weekdays = [DayOfWeek.Sunday],
        };

        Assert.Empty(BookingPatternGenerator.Problems(ordinary, Toronto));
        Assert.Single(BookingPatternGenerator.Generate(ordinary, Toronto));
    }

    // ---- what a row carries ---------------------------------------------

    /// <summary>
    /// Times are the room's wall clock, unmarked. That is the same shape every
    /// other time in this app has, and the reason is measured:
    /// <see cref="RoomClock.ToWire"/> is what turns a wall clock into the instant
    /// Panopto stores, so a row that had already been marked would be converted
    /// twice.
    /// </summary>
    [Fact]
    public void Generated_times_are_wall_clocks_with_no_marker()
    {
        var row = Assert.Single(BookingPatternGenerator.Generate(Base() with
        {
            To = new DateOnly(2026, 9, 7),
            Weekdays = [DayOfWeek.Monday],
        }));

        Assert.Equal(new DateTime(2026, 9, 7, 10, 0, 0), row.Start);
        Assert.Equal(new DateTime(2026, 9, 7, 11, 0, 0), row.End);
        Assert.Equal(DateTimeKind.Unspecified, row.Start.Kind);
        Assert.Equal(DateTimeKind.Unspecified, row.End.Kind);
    }

    /// <summary>A blank presenter or folder is absent, not an empty string.</summary>
    [Fact]
    public void A_blank_presenter_or_folder_becomes_absent_and_a_real_one_is_trimmed()
    {
        var blank = Assert.Single(BookingPatternGenerator.Generate(Base() with
        {
            To = new DateOnly(2026, 9, 7),
            Weekdays = [DayOfWeek.Monday],
            Presenter = "   ",
            FolderHint = "",
        }));

        Assert.Null(blank.Presenter);
        Assert.Null(blank.FolderHint);

        var filled = Assert.Single(BookingPatternGenerator.Generate(Base() with
        {
            To = new DateOnly(2026, 9, 7),
            Weekdays = [DayOfWeek.Monday],
            Presenter = "  Stew Mixalot  ",
            FolderHint = " TestFolder5 ",
            IsBroadcast = true,
        }));

        Assert.Equal("Stew Mixalot", filled.Presenter);
        Assert.Equal("TestFolder5", filled.FolderHint);
        Assert.True(filled.IsBroadcast);
    }

    /// <summary>
    /// An unknown token is named, and so is the list that works — because the
    /// operator's next move is to fix the name, and they cannot from
    /// "invalid format".
    /// </summary>
    [Fact]
    public void An_unknown_token_is_named_along_with_the_ones_that_work()
    {
        var problem = Assert.Single(
            BookingPatternGenerator.Problems(Base() with { TitleFormat = "Lec {course} {n}" }));

        Assert.Contains("{course}", problem, StringComparison.Ordinal);
        Assert.Contains("{room}", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every blocked pattern produces no rows at all. The count on the Preview
    /// button is then never a promise the grid cannot keep, which is the reason
    /// <see cref="BookingPatternGenerator.Generate"/> asks before it draws.
    /// </summary>
    [Fact]
    public void A_pattern_that_cannot_be_booked_generates_nothing()
    {
        var blocked = new (string What, BookingPattern Pattern)[]
        {
            ("no rooms", Base() with { Recorders = [] }),
            ("a room that is only spaces", Base() with { Recorders = ["   "] }),
            ("no weekdays ticked", Base() with { Weekdays = [] }),
            ("a range that ends before it starts",
                Base() with { From = new DateOnly(2026, 9, 25), To = new DateOnly(2026, 9, 7) }),
            ("no name at all", Base() with { TitleFormat = "   " }),
            ("a name using an unknown token", Base() with { TitleFormat = "Lec {course}" }),
            ("a start and end at the same time", Base() with { End = new TimeSpan(10, 0, 0) }),
            ("an end before the start with overnight off",
                Base() with { Start = new TimeSpan(23, 0, 0), End = new TimeSpan(1, 0, 0) }),
            // Monday to Saturday, asking only for Sundays. Every weekday occurs in
            // three weeks, so the range has to be narrowed below seven days for
            // there to be a date the pattern genuinely cannot reach.
            ("dates that fall on no ticked day", Base() with
            {
                To = new DateOnly(2026, 9, 12),
                Weekdays = [DayOfWeek.Sunday],
            }),
        };

        foreach (var (what, pattern) in blocked)
        {
            Assert.True(BookingPatternGenerator.Problems(pattern).Count > 0,
                $"{what} should have been a problem.");

            Assert.Empty(BookingPatternGenerator.Generate(pattern));
        }
    }
}
