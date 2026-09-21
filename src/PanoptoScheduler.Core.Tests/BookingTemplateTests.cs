using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// Saved booking templates: what survives a round trip to disk, and what
/// deliberately does not.
///
/// <para>The load-bearing claim is that <b>the dates are not saved</b>. A
/// template is the part of a pattern that is the same next term, and the dates
/// are the part that is always stale — so the span is stored and the caller
/// re-anchors it. The fact that catches a future change of mind is
/// <see cref="A_template_re_anchors_to_a_different_term"/>: it saves a pattern
/// from one term, loads it, and books a different term, asserting the length came
/// back and the dates did not.</para>
///
/// <para>Asserted field by field rather than with record equality.
/// <see cref="BookingTemplate"/> is a record but its collections are
/// <see cref="IReadOnlyList{T}"/>, which a record compares <i>by reference</i> —
/// so two templates that read identically would compare unequal, and a record
/// equality assert here would fail for a reason that has nothing to do with the
/// behaviour under test.</para>
///
/// <para>Every fact writes its own file in a temporary directory, so none of them
/// reads or disturbs the real <c>~/.panopto-scheduler/templates.json</c>.</para>
/// </summary>
public class BookingTemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "panopto-template-tests", Guid.NewGuid().ToString("N"));

    private readonly string _file;

    public BookingTemplateTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "templates.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A term's worth of MWF, 10:00 to 11:00, in three rooms.</summary>
    private static BookingPattern Pattern() => new()
    {
        Recorders = ["Event Space 214", "Classroom 106", "Studio B"],
        From = new DateOnly(2026, 9, 7),
        To = new DateOnly(2026, 12, 18),
        Weekdays = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
        Start = new TimeSpan(10, 0, 0),
        End = new TimeSpan(11, 0, 0),
        SkipDates = [new DateOnly(2026, 10, 12)],
        TitleFormat = "MGT100 L{nn} {dow}",
        Presenter = "A. Person",
        FolderHint = "AV Scratch",
        IsBroadcast = true,
        AllowOvernight = true,
    };

    [Fact]
    public void A_template_survives_a_round_trip_field_by_field()
    {
        BookingTemplateStore.Upsert(BookingTemplate.From(Pattern(), "MWF 10am"), _file);

        var saved = Assert.Single(BookingTemplateStore.Load(_file));

        Assert.Equal("MWF 10am", saved.Name);
        Assert.Equal(new[] { "Event Space 214", "Classroom 106", "Studio B" }, saved.Recorders);
        Assert.Equal(
            new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }, saved.Weekdays);
        Assert.Equal(new TimeSpan(10, 0, 0), saved.Start);
        Assert.Equal(new TimeSpan(11, 0, 0), saved.End);
        Assert.Equal(new[] { new DateOnly(2026, 10, 12) }, saved.SkipDates);
        Assert.Equal("MGT100 L{nn} {dow}", saved.TitleFormat);
        Assert.Equal("A. Person", saved.Presenter);
        Assert.Equal("AV Scratch", saved.FolderHint);
        Assert.True(saved.IsBroadcast);
        Assert.True(saved.AllowOvernight);

        // 7 September to 18 December inclusive.
        Assert.Equal(103, saved.SpanDays);
    }

    [Fact]
    public void A_template_re_anchors_to_a_different_term()
    {
        // Saved from the autumn term...
        BookingTemplateStore.Upsert(BookingTemplate.From(Pattern(), "MWF 10am"), _file);
        var saved = Assert.Single(BookingTemplateStore.Load(_file));

        // ...and used for the winter one, which is the whole point of saving it.
        var winter = new DateOnly(2027, 1, 11);
        var pattern = saved.ToPattern(winter);

        Assert.Equal(winter, pattern.From);
        Assert.Equal(new DateOnly(2027, 4, 23), pattern.To);

        // The same length, which is what travelled: 103 days either way.
        Assert.Equal(
            saved.SpanDays - 1, pattern.To.DayNumber - pattern.From.DayNumber);

        // And the rest of it came back untouched, so re-anchoring changed the
        // dates and nothing else.
        Assert.Equal(new[] { "Event Space 214", "Classroom 106", "Studio B" }, pattern.Recorders);
        Assert.Equal(new TimeSpan(10, 0, 0), pattern.Start);
        Assert.Equal("MGT100 L{nn} {dow}", pattern.TitleFormat);
        Assert.Equal("A. Person", pattern.Presenter);
        Assert.True(pattern.IsBroadcast);
        Assert.True(pattern.AllowOvernight);
    }

    [Fact]
    public void The_dates_are_not_written_to_the_file_at_all()
    {
        BookingTemplateStore.Upsert(BookingTemplate.From(Pattern(), "MWF 10am"), _file);

        var json = File.ReadAllText(_file);

        // Not merely ignored on the way back in — absent, so a stale file cannot
        // be read as carrying a term and someone reading the file by hand cannot
        // conclude that editing a date there would do anything.
        Assert.DoesNotContain("\"from\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"to\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2026-09-07", json, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-12-18", json, StringComparison.Ordinal);

        // A skip date is a date and is kept, so the absence above is the range
        // being dropped rather than date-bearing JSON in general.
        Assert.Contains("2026-10-12", json, StringComparison.Ordinal);
        Assert.Contains("\"spanDays\": 103", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_is_readable_by_a_person_editing_it()
    {
        BookingTemplateStore.Upsert(BookingTemplate.From(Pattern(), "MWF 10am"), _file);

        var json = File.ReadAllText(_file);

        // The file lives in the folder people are invited to edit by hand, beside
        // credentials.json. Weekdays written as numbers would make that invitation
        // a lie on the one field nobody remembers the numbering of.
        Assert.Contains("Monday", json, StringComparison.Ordinal);
        Assert.Contains("Wednesday", json, StringComparison.Ordinal);

        // camelCase keys and indentation, matching the credentials file.
        Assert.Contains("\"titleFormat\"", json, StringComparison.Ordinal);
        Assert.Contains("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_day_pattern_carries_a_span_of_one()
    {
        var oneDay = Pattern() with
        {
            From = new DateOnly(2026, 9, 7),
            To = new DateOnly(2026, 9, 7),
        };

        var saved = BookingTemplate.From(oneDay, "One off");

        Assert.Equal(1, saved.SpanDays);

        var anchored = saved.ToPattern(new DateOnly(2027, 3, 4));
        Assert.Equal(anchored.From, anchored.To);
    }

    [Fact]
    public void Saving_under_a_name_that_is_already_there_replaces_it()
    {
        BookingTemplateStore.Upsert(BookingTemplate.From(Pattern(), "MWF 10am"), _file);

        var moved = Pattern() with { Start = new TimeSpan(14, 0, 0), End = new TimeSpan(15, 0, 0) };
        BookingTemplateStore.Upsert(BookingTemplate.From(moved, "mwf 10AM"), _file);

        // One entry, not two that read the same — the name is what the picker
        // shows, so a case-different duplicate would be an entry nobody can tell
        // apart from another.
        var saved = Assert.Single(BookingTemplateStore.Load(_file));

        // And it is the new one, so the update took.
        Assert.Equal(new TimeSpan(14, 0, 0), saved.Start);
    }

    [Fact]
    public void Saving_a_new_name_keeps_the_ones_already_there()
    {
        BookingTemplateStore.Upsert(BookingTemplate.From(Pattern(), "MWF 10am"), _file);
        BookingTemplateStore.Upsert(BookingTemplate.From(Pattern(), "TTh 2pm"), _file);

        var saved = BookingTemplateStore.Load(_file);

        Assert.Equal(2, saved.Count);
        Assert.Contains(saved, t => t.Name == "MWF 10am");
        Assert.Contains(saved, t => t.Name == "TTh 2pm");
    }

    [Fact]
    public void Removing_by_name_drops_only_that_one()
    {
        BookingTemplateStore.Upsert(BookingTemplate.From(Pattern(), "MWF 10am"), _file);
        BookingTemplateStore.Upsert(BookingTemplate.From(Pattern(), "TTh 2pm"), _file);

        var left = BookingTemplateStore.Remove("MWf 10Am", _file);

        Assert.Equal("TTh 2pm", Assert.Single(left).Name);

        // And it stayed removed, rather than the in-memory list being the only
        // thing that changed.
        Assert.Equal("TTh 2pm", Assert.Single(BookingTemplateStore.Load(_file)).Name);
    }

    [Fact]
    public void Removing_a_name_that_is_not_there_changes_nothing()
    {
        BookingTemplateStore.Upsert(BookingTemplate.From(Pattern(), "MWF 10am"), _file);

        var left = BookingTemplateStore.Remove("nothing by this name", _file);

        Assert.Single(left);
    }

    [Fact]
    public void A_machine_with_no_templates_has_none_rather_than_an_error()
    {
        // Where this differs from CredentialStore: credentials that are absent
        // mean the app cannot run, and templates that are absent mean nobody has
        // saved one yet — the ordinary state of a new install. Throwing here would
        // put an error in front of everyone who has never used the feature.
        Assert.Empty(BookingTemplateStore.Load(Path.Combine(_dir, "absent.json")));
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_an_error_not_an_empty_list()
    {
        File.WriteAllText(_file, "{ this is not json");

        // The opposite call from the absent case, and deliberately so: showing an
        // empty list here would look exactly like the templates having been
        // deleted, which is the one thing a person would then act on.
        Assert.ThrowsAny<Exception>(() => BookingTemplateStore.Load(_file));
    }

    [Fact]
    public void A_span_outside_the_range_is_refused_by_name()
    {
        File.WriteAllText(_file, """
            [
              { "name": "Typo", "spanDays": 0, "start": "10:00:00", "end": "11:00:00" }
            ]
            """);

        // This file is hand-editable JSON, so a mistyped span has to be caught
        // where it can be named rather than surfacing as an overflow part-way
        // through generating rows.
        var error = Assert.Throws<InvalidOperationException>(
            () => BookingTemplateStore.Load(_file));

        Assert.Contains("Typo", error.Message, StringComparison.Ordinal);
        Assert.Contains("0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_span_beyond_the_ceiling_is_refused()
    {
        var tooLong = BookingTemplate.From(Pattern(), "Whole degree")
            with { SpanDays = BookingTemplate.MaxSpanDays + 1 };

        var error = Assert.Throws<InvalidOperationException>(
            () => BookingTemplateStore.Upsert(tooLong, _file));

        Assert.Contains("Whole degree", error.Message, StringComparison.Ordinal);

        // Refused on the way in means nothing was written, so a rejected save
        // cannot leave a half-good file behind.
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void The_ceiling_itself_is_allowed()
    {
        // The boundary, so the guard above is a ceiling rather than an
        // off-by-one that refuses a legitimate template.
        var atTheLimit = BookingTemplate.From(Pattern(), "Long") with
        {
            SpanDays = BookingTemplate.MaxSpanDays,
        };

        var saved = Assert.Single(BookingTemplateStore.Upsert(atTheLimit, _file));

        Assert.Equal(BookingTemplate.MaxSpanDays, saved.SpanDays);
    }

    [Fact]
    public void A_time_that_is_not_a_time_of_day_is_refused()
    {
        File.WriteAllText(_file, """
            [
              { "name": "All day", "start": "10:00:00", "end": "1.00:00:00" }
            ]
            """);

        // 24:00 is a duration, not a clock reading. Nothing in the app can produce
        // one — the form's reader accepts "h:mm tt" and similar, none of which parse
        // 24:00 — but the file is hand-editable, and the one place that renders a
        // time as a clock reading refuses anything outside a day. Left to throw
        // there it would throw out of a command, which reaches the dispatcher and
        // closes the app.
        var error = Assert.Throws<InvalidOperationException>(
            () => BookingTemplateStore.Load(_file));

        Assert.Contains("All day", error.Message, StringComparison.Ordinal);
        Assert.Contains("end", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overnight_end_is_still_a_time_of_day()
    {
        // An end earlier than the start is how an overnight booking is written, so
        // the guard above has to let it through — otherwise the fix for one fault
        // forbids a legitimate pattern.
        var overnight = Pattern() with
        {
            Start = new TimeSpan(23, 0, 0),
            End = new TimeSpan(1, 0, 0),
            AllowOvernight = true,
        };

        var saved = Assert.Single(
            BookingTemplateStore.Upsert(BookingTemplate.From(overnight, "Overnight"), _file));

        Assert.Equal(new TimeSpan(23, 0, 0), saved.Start);
        Assert.Equal(new TimeSpan(1, 0, 0), saved.End);
        Assert.True(saved.AllowOvernight);
    }

    [Fact]
    public void Times_render_back_into_the_formats_the_form_reads()
    {
        // The value that travels is a TimeSpan, but the form's boxes hold "2:30 PM"
        // and re-read it with the same reader a pasted file goes through. So the
        // round trip that matters is TimeSpan → the form's clock format → TimeSpan,
        // on a real calendar day, without asking this machine what time it is.
        var awkward = Pattern() with
        {
            Start = new TimeSpan(0, 30, 0),
            End = new TimeSpan(12, 0, 0),
        };

        var saved = Assert.Single(
            BookingTemplateStore.Upsert(BookingTemplate.From(awkward, "Awkward"), _file));

        var anchor = new DateOnly(2027, 3, 4);
        var pattern = saved.ToPattern(anchor);

        foreach (var time in new[] { pattern.Start, pattern.End })
        {
            var text = anchor.ToDateTime(TimeOnly.FromTimeSpan(time))
                .ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(
                PanoptoScheduler.Core.Import.LegacyScheduleReader.TryReadTime(text, out var back),
                $"\"{text}\" is what the form would show, and the form cannot read it back.");

            Assert.Equal(time, back);
        }
    }

    [Fact]
    public void A_template_with_no_name_is_refused()
    {
        var nameless = BookingTemplate.From(Pattern(), "   ");

        Assert.Throws<InvalidOperationException>(
            () => BookingTemplateStore.Upsert(nameless, _file));
    }

    [Fact]
    public void A_saved_template_keeps_its_times_to_the_minute()
    {
        // Times travel as TimeSpan, which .NET 9's serializer handles natively —
        // but a template that came back 10:00:00.0000001 would be a booking in
        // the wrong second, so the round trip is worth pinning exactly.
        var odd = Pattern() with
        {
            Start = new TimeSpan(9, 5, 30),
            End = new TimeSpan(10, 35, 15),
        };

        BookingTemplateStore.Upsert(BookingTemplate.From(odd, "Odd times"), _file);
        var saved = Assert.Single(BookingTemplateStore.Load(_file));

        Assert.Equal(new TimeSpan(9, 5, 30), saved.Start);
        Assert.Equal(new TimeSpan(10, 35, 15), saved.End);
    }

    [Fact]
    public void The_default_file_sits_beside_the_credentials()
    {
        // Not inside the repository: anything in the repo is something to
        // remember not to commit, and a template is one person's working set.
        Assert.Equal(
            Path.Combine(PanoptoScheduler.Core.Configuration.CredentialStore.Directory,
                "templates.json"),
            BookingTemplateStore.DefaultPath);
    }
}
