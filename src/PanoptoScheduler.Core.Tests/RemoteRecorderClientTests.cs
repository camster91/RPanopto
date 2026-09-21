using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.RateLimiting;
using static PanoptoScheduler.Core.Tests.SoapListings;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The listings that are read a page at a time.
///
/// <para>The bug these pin: <c>ListRecorders</c> asked for page 0 with a 250 cap
/// and never followed the rest, so every recorder past the first 250 by name was
/// invisible. Not only a display gap — <c>BulkScheduler</c> resolves a room by
/// name and classifies a name it cannot find as <c>Skipped</c> rather than
/// <c>Failed</c>, so those rows did not book and did not complain.</para>
/// </summary>
public class RemoteRecorderClientTests
{
    private const string FirstGuid = "11111111-1111-1111-1111-111111111111";
    private const string SecondGuid = "22222222-2222-2222-2222-222222222222";
    private const string ThirdGuid = "33333333-3333-3333-3333-333333333333";

    /// <summary>
    /// <c>TotalNumber</c> is the size of the whole set, so it ends the walk exactly
    /// — no wasted request past the end, and no risk of stopping early.
    /// </summary>
    [Fact]
    public async Task Lists_every_page_when_the_first_page_reports_a_larger_total()
    {
        var (soap, handler) = Build();

        handler
            .RespondOnce("ListRecorders", RecorderPage(3, (FirstGuid, "JMHH240"), (SecondGuid, "JMHH242")))
            .RespondOnce("ListRecorders", RecorderPage(3, (ThirdGuid, "Event Space 214")));

        var recorders = await new RemoteRecorderClient(soap).ListRecordersAsync();

        Assert.Equal(3, recorders.Count);
        Assert.Contains(recorders, r => r.Name == "Event Space 214");

        // Two, not one: the first page said three and held two. The second page
        // completes it, so there is nothing left to ask for.
        Assert.Equal(2, handler.Calls.Count(c => c == "ListRecorders"));
    }

    /// <summary>
    /// The rule that is deliberately <b>not</b> implemented, pinned so nobody adds
    /// it back.
    ///
    /// <para>"A short page means the end" reads as the obvious rule and it is the
    /// wrong one: the server clamps a page below the size asked for, so a short
    /// page is the normal case rather than the last one. Stopping on it returns
    /// the first clamped page and calls it complete — the same truncation in a new
    /// place, and harder to notice because the code looks careful.</para>
    ///
    /// <para>Each page here holds one item against a 250-item page size. A
    /// short-page rule ends the walk after the first request with a single
    /// recorder, and this fact fails.</para>
    ///
    /// <para>No <c>TotalNumber</c> on purpose. With one, the total would end the
    /// walk correctly and this fact would pass for a reason that has nothing to do
    /// with the rule it is meant to pin — which is how the first version of it was
    /// written, and why it is spelled out here.</para>
    /// </summary>
    [Fact]
    public async Task Does_not_treat_a_short_page_as_the_end()
    {
        var (soap, handler) = Build();

        handler
            .RespondOnce("ListRecorders", RecorderListingWithoutTotal((FirstGuid, "Alpha")))
            .RespondOnce("ListRecorders", RecorderListingWithoutTotal((SecondGuid, "Beta")))
            .RespondOnce("ListRecorders", RecorderListingWithoutTotal());

        var recorders = await new RemoteRecorderClient(soap).ListRecordersAsync();

        Assert.Equal(new[] { "Alpha", "Beta" }, recorders.Select(r => r.Name));

        // Three: two pages of one, then the empty page that proves it is over.
        Assert.Equal(3, handler.Calls.Count(c => c == "ListRecorders"));
    }

    /// <summary>
    /// A page that adds nothing new ends the walk. Either the set is exhausted or
    /// <c>PageNumber</c> is being ignored and this is the first page again — and
    /// neither is worth another request to tell apart.
    /// </summary>
    [Fact]
    public async Task Stops_when_a_page_repeats_itself_rather_than_looping_forever()
    {
        var (soap, handler) = Build();

        // The same body for every call, and no total: a server honouring neither
        // PageNumber nor a count. Without the duplicate guard this never ends.
        handler.Respond("ListRecorders",
            RecorderListingWithoutTotal((FirstGuid, "JMHH240"), (SecondGuid, "JMHH242")));

        var recorders = await new RemoteRecorderClient(soap).ListRecordersAsync();

        Assert.Equal(2, recorders.Count);

        // The second request is what discovers it: it came back with nothing new.
        Assert.Equal(2, handler.Calls.Count(c => c == "ListRecorders"));
    }

    /// <summary>
    /// The backstop for a tenant genuinely larger than the ceiling. Hitting it is
    /// logged rather than silent, because a listing that quietly stops short is
    /// the exact failure this type was written to remove.
    /// </summary>
    [Fact]
    public async Task Stops_at_the_page_ceiling_rather_than_paging_forever()
    {
        var (soap, handler) = Build();

        // Every page fresh, no total, and more pages available than the ceiling
        // allows: only the ceiling can end this.
        for (var page = 0; page < SoapPaging.MaxPages + 5; page++)
        {
            handler.RespondOnce("ListRecorders", RecorderListingWithoutTotal(
                [.. Enumerable.Range(page * SoapPaging.PageSize, SoapPaging.PageSize).Select(Recorder)]));
        }

        var recorders = await new RemoteRecorderClient(soap).ListRecordersAsync();

        // Named through SoapPaging rather than by value: a test carrying its own
        // copy of the number would pass after someone changed the real one and
        // stop testing anything.
        Assert.Equal(SoapPaging.MaxPages * SoapPaging.PageSize, recorders.Count);
        Assert.Equal(SoapPaging.MaxPages, handler.Calls.Count(c => c == "ListRecorders"));
    }

    /// <summary>
    /// A loop that sends page 0 every time is a loop of successful requests, so
    /// counting calls does not catch it. The request has to actually name the page.
    /// </summary>
    [Fact]
    public async Task Asks_for_the_next_page_by_number()
    {
        var (soap, handler) = Build();

        handler
            .RespondOnce("ListRecorders", RecorderPage(2, (FirstGuid, "Alpha")))
            .RespondOnce("ListRecorders", RecorderPage(2, (SecondGuid, "Beta")));

        await new RemoteRecorderClient(soap).ListRecordersAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("PageNumber", handler.Bodies[1]);

        // Not byte-identical: the only field that may differ between the two
        // requests is the page index, and if it does not, the second request asked
        // for the first page again.
        Assert.NotEqual(handler.Bodies[0], handler.Bodies[1]);
    }

    /// <summary>
    /// The reported bug, directly: a room on the second page resolves by name.
    /// </summary>
    [Fact]
    public async Task Finds_a_recorder_that_is_only_on_the_second_page()
    {
        var (soap, handler) = Build();

        handler
            .RespondOnce("ListRecorders", RecorderPage(2, (FirstGuid, "JMHH240")))
            .RespondOnce("ListRecorders", RecorderPage(2, (SecondGuid, "Event Space 214")));

        var recorder = await new RemoteRecorderClient(soap).FindRecorderAsync("Event Space 214");

        Assert.NotNull(recorder);
        Assert.Equal(Guid.Parse(SecondGuid), recorder!.Id);
    }

    /// <summary>
    /// Folders page for the same reason recorders do, and the folder picker is the
    /// other half of the booking path.
    /// </summary>
    [Fact]
    public async Task Lists_every_folder_page()
    {
        var (soap, handler) = Build();

        handler
            .RespondOnce("GetFoldersList", FolderPage(3, (FirstGuid, "AV Scratch")))
            .RespondOnce("GetFoldersList", FolderPage(3, (SecondGuid, "Rotman Courses"), (ThirdGuid, "Events")));

        var folders = await new SessionManagementClient(soap).ListFoldersAsync();

        Assert.Equal(3, folders.Count);
        Assert.Contains(folders, f => f.Name == "Events");
        Assert.Equal(2, handler.Calls.Count(c => c == "GetFoldersList"));
    }

    /// <summary>
    /// A finished walk says so.
    ///
    /// <para>The flag is only worth having if it is true in the ordinary case —
    /// a caller told "incomplete" on every listing would learn to ignore it, and
    /// the caveat on screen would become wallpaper.</para>
    /// </summary>
    [Fact]
    public async Task Reports_a_listing_that_finished_as_complete()
    {
        var (soap, handler) = Build();

        handler
            .RespondOnce("ListRecorders", RecorderPage(2, (FirstGuid, "JMHH240")))
            .RespondOnce("ListRecorders", RecorderPage(2, (SecondGuid, "Event Space 214")));

        var recorders = await new RemoteRecorderClient(soap).ListRecordersAsync();

        Assert.True(recorders.Complete);
        Assert.Equal(2, recorders.ReportedTotal);

        // The list is still a list. Every caller written before this flag existed
        // reads it exactly as it did: index, Count, and LINQ.
        Assert.Equal(2, recorders.Count);
        Assert.Equal("JMHH240", recorders[0].Name);
        Assert.Contains(recorders, r => r.Name == "Event Space 214");
    }

    /// <summary>
    /// A walk that ran out of pages says that instead. This is the fact the whole
    /// flag exists for: the 60-page ceiling is a wall, and the only thing standing
    /// between it and the original silent truncation is this being visible to the
    /// caller rather than only to whoever reads the log.
    /// </summary>
    [Fact]
    public async Task Reports_a_listing_that_ran_out_of_pages_as_incomplete()
    {
        var (soap, handler) = Build();

        for (var page = 0; page < SoapPaging.MaxPages; page++)
        {
            handler.RespondOnce("ListRecorders", RecorderListingWithoutTotal(
                [.. Enumerable.Range(page * SoapPaging.PageSize, SoapPaging.PageSize).Select(Recorder)]));
        }

        var recorders = await new RemoteRecorderClient(soap).ListRecordersAsync();

        Assert.False(recorders.Complete);

        // Count is what was read, not what exists. That distinction is the whole
        // reason the note on screen gives a number and a caveat together.
        Assert.Equal(SoapPaging.MaxPages * SoapPaging.PageSize, recorders.Count);

        // No total was sent, so there is nothing to report. Not zero-as-a-count:
        // the note's wording branches on this.
        Assert.Equal(0, recorders.ReportedTotal);
    }

    /// <summary>
    /// A name that was found is found, whether or not the walk finished — the
    /// listing only ever adds items.
    /// </summary>
    [Fact]
    public async Task Finds_a_recorder_in_an_incomplete_listing_without_complaint()
    {
        var (soap, handler) = Build();

        for (var page = 0; page < SoapPaging.MaxPages; page++)
        {
            handler.RespondOnce("ListRecorders", RecorderListingWithoutTotal(
                [.. Enumerable.Range(page * SoapPaging.PageSize, SoapPaging.PageSize).Select(Recorder)]));
        }

        var recorder = await new RemoteRecorderClient(soap).FindRecorderAsync("Room 0007");

        Assert.NotNull(recorder);
    }

    /// <summary>
    /// The lie this closes: a name absent from a list that stopped short is not
    /// the same answer as a name absent from the tenant, and returning null would
    /// state the second while having earned only the first.
    /// </summary>
    [Fact]
    public async Task Refuses_to_report_a_name_missing_from_an_incomplete_listing()
    {
        var (soap, handler) = Build();

        for (var page = 0; page < SoapPaging.MaxPages; page++)
        {
            handler.RespondOnce("ListRecorders", RecorderListingWithoutTotal(
                [.. Enumerable.Range(page * SoapPaging.PageSize, SoapPaging.PageSize).Select(Recorder)]));
        }

        var ex = await Assert.ThrowsAsync<ListingIncompleteException>(
            () => new RemoteRecorderClient(soap).FindRecorderAsync("Event Space 214"));

        // The message has to carry the number, because the number is the fix.
        Assert.Contains($"{SoapPaging.MaxPages * SoapPaging.PageSize}", ex.Message);
        Assert.Equal(SoapPaging.MaxPages * SoapPaging.PageSize, ex.Read);
    }

    private static (PanoptoSoapClient Soap, ScriptedSoapHandler Handler) Build()
    {
        var handler = new ScriptedSoapHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };

        return (new PanoptoSoapClient(http, new RateLimiterRegistry(), new StubAuthenticator()), handler);
    }
}
