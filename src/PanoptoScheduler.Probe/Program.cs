using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PanoptoScheduler.Core.Auth;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Configuration;
using PanoptoScheduler.Core.Json;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.RateLimiting;

namespace PanoptoScheduler.Probe;

/// <summary>
/// Verifies tenant connectivity end to end, without the UI.
///
/// Answers the questions the app's architecture depends on but which cannot be
/// settled from Panopto's documentation:
///   1. Does the OAuth client work at all?
///   2. Does the public REST API accept the token?
///   3. Does the internal Data.svc accept a bearer token, or does it need the
///      browser session cookie? This decides whether the app needs one auth
///      mechanism or two.
/// </summary>
internal static class Program
{
    private const string RestProbePath = "/Panopto/api/v1/remoteRecorders/search?searchQuery=a&maxResults=1";
    private const string DataSvcProbePath = "/Panopto/Services/Data.svc/GetSessions";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        // Everything except --verify-write is read-only. That one is the only
        // thing in this program that writes to the tenant, and it cleans up
        // after itself.
        var verifyWrite = args.Contains("--verify-write");

        PanoptoCredentials credentials;
        try
        {
            // Same resolution the app uses, so the probe exercises the path a
            // packaged install actually takes rather than a parallel one.
            credentials = CredentialStore.Resolve()
                ?? throw new FileNotFoundException(
                    $"No credentials at {CredentialStore.DefaultPath} and none shipped beside " +
                    "the probe. Create one with the tenant URL, client id and client secret.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not load credentials: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Tenant : {credentials.TenantUrl}");
        Console.WriteLine($"Client : {credentials.ClientId}");
        Console.WriteLine();

        using var http = new HttpClient { BaseAddress = new Uri(credentials.TenantUrl) };
        http.Timeout = TimeSpan.FromSeconds(60);

        // The same cache the app uses, so signing in once here makes every later
        // probe run non-interactive.
        await using var auth = new OAuthPkceAuthenticator(
            credentials.ToOAuthOptions(), http, new DpapiTokenStore());

        auth.AuthorizationUrlReady += url =>
        {
            Console.WriteLine("Opening your browser to sign in to Panopto…");
            Console.WriteLine($"If it does not open, visit:\n{url}\n");
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* headless — the URL is printed above */ }
            return Task.CompletedTask;
        };

        // The cache only helps if it is read, and Restore() is what reads it.
        // Leaving this out is what made every earlier run demand a fresh browser
        // round-trip despite carrying a valid refresh token.
        var resumed = auth.Restore();
        Console.WriteLine(resumed
            ? "Found a saved session; refreshing it (no browser needed)."
            : "No saved session on this machine — a sign-in is required.");

        if (!resumed || !await TryResumeAsync(auth))
        {
            Console.WriteLine("Waiting for you to finish signing in (5 minute limit)…");
            try
            {
                await auth.SignInAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Sign-in failed: {ex.Message}");
                return 1;
            }
        }

        var tokens = auth.Tokens!;
        Console.WriteLine($"Signed in. Access token acquired, expires {tokens.ExpiresAt.ToLocalTime():HH:mm:ss}.");
        Console.WriteLine($"Refresh token: {(tokens.CanRefresh ? "yes" : "NO — offline_access was not granted")}");
        Console.WriteLine();

        if (args.Contains("--dump-raw"))
        {
            return await DumpRawAsync(http, auth);
        }

        if (args.Contains("--dump-pages"))
        {
            return await DumpPagesAsync(http, auth);
        }

        if (verifyWrite)
        {
            return await VerifyWriteAsync(
                http, auth,
                Flag(args, "--recorder") ?? "",
                Flag(args, "--folder"),
                RequestedStart(Flag(args, "--at")),
                RequestedMinutes(Flag(args, "--minutes")));
        }

        var rest = await ProbeAsync(http, auth, RestProbePath,
            HttpMethod.Get, "public REST API (bearer)", body: null);

        var dataSvcBearer = await ProbeAsync(http, auth, DataSvcProbePath,
            HttpMethod.Post, "Data.svc with BEARER token", BuildGetSessionsBody());

        // Which write path is available decides the whole scheduling design.
        // Every call below is read-only: no scheduling method is invoked, and
        // nothing is created, moved or deleted in the tenant.
        var limiters = new RateLimiterRegistry();
        var cookieProvider = new LegacyCookieProvider(http, limiters, auth);
        var soap = new PanoptoSoapClient(http, limiters, auth, cookieProvider);
        var recorderClient = new RemoteRecorderClient(soap);
        var sessionClient = new SessionManagementClient(soap);

        if (args.Contains("--dump-listings"))
        {
            return await DumpListingsAsync(recorderClient, sessionClient);
        }

        Console.WriteLine("── Candidate write paths (all read-only) ──");

        var recorders = await TryAsync(
            async () => (await recorderClient.ListRecordersAsync()).Count, "SOAP ListRecorders");

        var folders = await TryAsync(
            async () => (await sessionClient.ListFoldersAsync()).Count, "SOAP GetFolders");

        var restRecorders = await TryAsync(
            async () => await CountAsync(http, auth, RestRecordersPath), "REST recorders/search");

        var restFolders = await TryAsync(
            async () => await CountAsync(http, auth, RestFoldersPath), "REST folders/search");

        // There is no REST list for scheduled recordings, so the route is
        // confirmed instead: a real route answers "no scheduled recording was
        // found for this Id" for a bogus id, whereas a missing route answers
        // "no HTTP resource was found that matches the request URI".
        var route = await CheckRouteAsync(http, auth, RestScheduledProbePath);

        // Reported on its own because it is the load-bearing step: every write
        // the app makes depends on the cookie this call returns, and a failure
        // here is indistinguishable downstream from an empty tenant.
        string legacyCookie;
        try
        {
            await cookieProvider.GetAsync();
            legacyCookie = "OK — .ASPXAUTH obtained";
        }
        catch (Exception ex)
        {
            legacyCookie = $"FAILED — {ex.Message}";
        }

        // The one question documentation cannot answer and the legacy source only
        // answers by inference. Read-only, and settled by comparing the last
        // column against what the Panopto web UI shows for the same recording.
        Console.WriteLine();
        await PrintTimeModelAsync(http, auth);

        Console.WriteLine();
        Console.WriteLine("─── Summary ───────────────────────────────────────────");
        Console.WriteLine($"  OAuth sign-in          : OK");
        Console.WriteLine($"  Public REST + bearer   : {(rest ? "OK" : "FAILED")}");
        Console.WriteLine($"  Data.svc  + bearer     : {(dataSvcBearer ? "OK — reads need one auth mechanism"
                                                                   : "FAILED — Data.svc needs the session cookie")}");
        Console.WriteLine($"  legacyLogin → cookie   : {legacyCookie}");
        Console.WriteLine($"  SOAP      + cookie     : {(recorders is not null ? "OK — write path available"
                                                                      : "FAILED — no usable write path")}");
        Console.WriteLine($"  SOAP      + bearer     : FAILED without the exchange above —"
                          + " a bearer-only call is anonymous, and GetFolders answers 200 with 0.");
        Console.WriteLine($"  REST scheduledRecordings: {route}");
        Console.WriteLine($"  REST search endpoints  : {(restRecorders is not null && restFolders is not null ? "OK" : "PARTIAL")}");
        Console.WriteLine("───────────────────────────────────────────────────────");

        return rest ? 0 : 1;
    }

    /// <summary>
    /// Prints the wire shape of every field in a session listing — each JSON kind
    /// a field actually takes, with a sample and a count.
    ///
    /// <para><b>Why this exists.</b> A typed model asserts one shape per field,
    /// and a field that is occasionally something else is invisible from the
    /// model: it parses fine on the rows that match and throws on the ones that
    /// do not. <c>PanoptoSession.Duration</c> was declared <c>long?</c> and refused
    /// a live response, which is not a modelling opinion — it is the calendar's
    /// own load path failing. Reading the raw bytes is what settled it: the wire
    /// sends fractional seconds, so the field is <c>double?</c>. The same run
    /// caught the presenter name fields, declared <c>string?</c> against arrays.</para>
    /// </summary>
    private static async Task<int> DumpRawAsync(HttpClient http, OAuthPkceAuthenticator auth)
    {
        Console.WriteLine("── Raw session shapes (read-only) ──");

        // The model's own query object, not a hand-written body. The two are not
        // the same request — SessionQuery carries flags this probe would not think
        // to send — and the difference is which rows come back, which is exactly
        // what is under investigation.
        using var request = new HttpRequestMessage(HttpMethod.Post, DataSvcProbePath)
        {
            Content = JsonContent.Create(
                new GetSessionsRequest
                {
                    QueryParameters = new SessionQuery
                    {
                        Status = [1],
                        MaxResults = DataSvcClient.MaxPageSize,
                    },
                },
                options: ModelOptions),
        };

        await auth.ApplyAsync(request);

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"   status {(int)response.StatusCode}");

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"   {text[..Math.Min(400, text.Length)]}");
            return 1;
        }

        using var doc = JsonDocument.Parse(text);

        if (!doc.RootElement.TryGetProperty("d", out var d) ||
            !d.TryGetProperty("Results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("   no d.Results array in the response");
            return 1;
        }

        Console.WriteLine($"   {results.GetArrayLength()} row(s)");
        Console.WriteLine();

        // field -> kind -> (how many rows, one sample)
        var shapes = new Dictionary<string, Dictionary<string, (int Count, string Sample)>>(
            StringComparer.Ordinal);

        foreach (var row in results.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;

            foreach (var prop in row.EnumerateObject())
            {
                if (!shapes.TryGetValue(prop.Name, out var byKind))
                    shapes[prop.Name] = byKind =
                        new Dictionary<string, (int, string)>(StringComparer.Ordinal);

                // JsonValueKind.Number covers 7200 and 7200.5 alike, and a typed
                // model accepts only the first — so the two have to be told apart
                // here, or this reports a field as uniform when it is not.
                var kind = prop.Value.ValueKind switch
                {
                    JsonValueKind.Number => prop.Value.TryGetInt64(out _) ? "Int" : "Real",
                    var other => other.ToString(),
                };

                byKind[kind] = byKind.TryGetValue(kind, out var seen)
                    ? (seen.Count + 1, seen.Sample)
                    : (1, prop.Value.GetRawText());
            }
        }

        foreach (var name in shapes.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            foreach (var (kind, shape) in shapes[name])
            {
                var sample = shape.Sample.Length > 58
                    ? shape.Sample[..58] + "…"
                    : shape.Sample;

                Console.WriteLine($"   {name,-30} {kind,-8} x{shape.Count,-4} {sample}");
            }
        }

        // A few rows with the fields under suspicion. A unit question is settled
        // by arithmetic against a name that states the class length, not by
        // reading the field name and assuming.
        Console.WriteLine();
        Console.WriteLine("   samples (name, and what the number means at each scale):");

        foreach (var row in results.EnumerateArray().Take(8))
        {
            var name = row.TryGetProperty("SessionName", out var n) ? n.GetString() : null;
            var raw = row.TryGetProperty("Duration", out var dd) ? dd.GetRawText() : "?";

            var scale = double.TryParse(raw, CultureInfo.InvariantCulture, out var seconds)
                ? $"={seconds / 60:0.#} min  ={seconds / 3600:0.##} h"
                : "";

            Console.WriteLine($"     {raw,-12} {scale,-24} {name}");
        }

        // Then reproduce the model's own read against these same bytes. The shape
        // table above can look innocent — every field one kind, every row alike —
        // while a typed model still refuses it, so the failure and the bytes
        // behind it are worth printing rather than inferred.
        Console.WriteLine();

        try
        {
            var envelope = JsonSerializer.Deserialize<RawEnvelope>(text, ModelOptions);
            Console.WriteLine($"   model read : OK — {envelope?.D?.Results?.Count ?? 0} row(s)");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"   model read : FAILED — {ex.Message}");

            if (ex.BytePositionInLine is { } at)
            {
                var from = (int)Math.Max(0, at - 140);
                var length = (int)Math.Min(280, text.Length - from);
                Console.WriteLine();
                Console.WriteLine($"   bytes {from}–{from + length}:");
                Console.WriteLine($"     {text.Substring(from, length)}");
            }
        }

        return 0;
    }

    /// <summary>The response envelope, trimmed to the part that carries sessions.</summary>
    private sealed record RawEnvelope(RawData? D);

    private sealed record RawData(List<PanoptoSession>? Results);

    /// <summary>Matches what <c>DataSvcClient</c> deserializes with.</summary>
    private static readonly JsonSerializerOptions ModelOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Spends the cached refresh token up front, rather than letting the first
    /// real request discover it is no longer good.
    ///
    /// <para>A saved session the server has since revoked is a cache miss, not a
    /// dead end — the caller falls back to the browser. Doing it here means that
    /// fallback happens before a probe has half-run, and that the expiry printed
    /// below is the one actually in force.</para>
    /// </summary>
    private static async Task<bool> TryResumeAsync(OAuthPkceAuthenticator auth)
    {
        try
        {
            await auth.GetValidAccessTokenAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"The saved session was refused: {ex.Message}");
            return false;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            PanoptoScheduler.Probe — tenant diagnostics.

              (no arguments)     Read-only. Sign-in, auth paths, the time model,
                                 and a sample of real sessions. Creates nothing.
              --verify-write     THE ONLY MODE THAT WRITES. Books one short
                                 session, reads it back, deletes it again.
                  --recorder <name>   required; as the schedule files spell it
                  --folder <name|guid>  optional; defaults to the recorder's own
                  --at <yyyy-MM-ddTHH:mm>  optional; defaults to tomorrow 14:30
                  --minutes <n>       optional; defaults to 5
              --dump-raw         Read-only. Every field in a session listing,
                                 with the JSON kinds it actually takes and a
                                 sample of each. For settling what a field is
                                 when a typed model refuses it.
              --help             this.

            --verify-write exists because the time model's read side was settled by
            measuring the tenant, and the write side can only be settled by
            writing. A wrong conversion here produces no error at all: the booking
            is accepted, the app draws it correctly, and the room is empty at the
            hour it was asked for.
            """);
    }

    /// <summary>The value after a flag, or null when the flag is absent or bare.</summary>
    private static string? Flag(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// When to book the test session — as a wall clock, deliberately
    /// <see cref="DateTimeKind.Unspecified"/>, because that is what the schedule
    /// files carry and what the wire is supposed to receive unconverted.
    /// </summary>
    private static DateTime RequestedStart(string? raw)
        => raw is not null
           && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
            : DateTime.SpecifyKind(
                DateTime.Today.AddDays(1).AddHours(14).AddMinutes(30), DateTimeKind.Unspecified);

    private static int RequestedMinutes(string? raw)
        => int.TryParse(raw, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? minutes
            : 5;

    /// <summary>
    /// Proves the write path end to end, and leaves the tenant as it found it.
    ///
    /// <para><b>This is the only thing in this program that writes.</b> It books
    /// one short session, reads it back through the same <c>Data.svc</c> path the
    /// calendar uses, compares the time it gets against the time it asked for,
    /// and deletes the session — including when the comparison fails, so a failed
    /// run does not leave a stray recording in a real room.</para>
    ///
    /// <para><b>Why it has to be live.</b> Every other question about the time
    /// model was answerable by reading the tenant. This one is not: the write and
    /// the read are opposite directions, and only a booking proves which way the
    /// write points. The trailing <c>Z</c> in the request <i>means UTC</i>, so the
    /// room's wall clock has to be converted into the instant that hour names
    /// before it goes out — and sending the digits unconverted, which is what this
    /// probe first did, was accepted, confirmed, and drew correctly here while
    /// landing four hours early. That failure is invisible: the call succeeds, the
    /// app draws the session correctly, and the recording happens hours away from
    /// when it was asked for.</para>
    ///
    /// <para>Read back through <see cref="DataSvcClient"/> rather than a second
    /// parser, so what is compared is exactly what the calendar would show.</para>
    /// </summary>
    /// <summary>
    /// Sends session queries side by side and reports what each actually came
    /// back with — row count, the server's own <c>TotalNumber</c>, the status
    /// values present, and the first row's id.
    ///
    /// <para><b>Why this exists.</b> A listing that returns 250 rows looks like a
    /// complete listing. Several different faults produce exactly that: the
    /// server ignoring <c>Page</c>, the server ignoring the <c>Status</c> filter
    /// and handing back a default slice, or the slice genuinely being the whole
    /// set. Measured on the live tenant: <c>TotalNumber</c> is 2024 while every
    /// page returned the same 250 rows, all of them completed rather than
    /// scheduled — the filter and the paging were both being dropped. Running the
    /// bodies side by side settled it: <c>GetSessions</c> binds its JSON
    /// case-sensitively and silently ignores a mis-cased field, so the model's
    /// PascalCase request bound nothing and the server answered with its default
    /// slice. The casing is fixed; this mode stays because a field the server does
    /// not recognise still produces no error, only a wrong answer.</para>
    ///
    /// <para>The bodies are spelled out rather than built from the model, because
    /// the model is one of the things under test: a field the server does not
    /// recognise is silently ignored, so a request that binds <i>nothing</i>
    /// still looks like a successful request from here.</para>
    ///
    /// <para>Read-only.</para>
    /// </summary>
    /// <summary>
    /// Calls each listing several times, in a row and then at the same time, and
    /// reports what came back.
    ///
    /// <para>Written because the app's own log shows something impossible: the
    /// Rotman tenant has 19 recorders and 447 folders, yet the app records
    /// <c>stopped at the 60-page ceiling after 15000 item(s)</c> for
    /// <i>both</i> listings — 15000 being exactly the ceiling. A single call from
    /// this probe returns 19 and 447 correctly, so the fault is not in the paging
    /// and not in the tenant: it is something about calling these repeatedly, or
    /// while another listing is in flight, which is exactly what the app does and
    /// this probe previously never did.</para>
    /// </summary>
    private static async Task<int> DumpListingsAsync(
        RemoteRecorderClient recorders,
        SessionManagementClient sessions)
    {
        Console.WriteLine("── Listings, called the way the app calls them (read-only) ──");
        Console.WriteLine();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var r = await recorders.ListRecordersAsync();
            var f = await sessions.ListFoldersAsync();

            Console.WriteLine($"  attempt {attempt}, in sequence:");
            Console.WriteLine($"    ListRecorders  count={r.Count,-6} complete={r.Complete,-6} reported={r.ReportedTotal}");
            Console.WriteLine($"    GetFoldersList count={f.Count,-6} complete={f.Complete,-6} reported={f.ReportedTotal}");
            Console.WriteLine();

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        // The app calls these from two view models at once, which is the one shape
        // this probe never exercised. If the shared cookie or the limiter is the
        // fault, it shows up here and nowhere else.
        Console.WriteLine("  both at once:");

        var both = await Task.WhenAll(
            Task.Run(async () => (await recorders.ListRecordersAsync()).Count),
            Task.Run(async () => (await sessions.ListFoldersAsync()).Count));

        Console.WriteLine($"    ListRecorders  count={both[0]}");
        Console.WriteLine($"    GetFoldersList count={both[1]}");
        Console.WriteLine();

        // The calendar, the pattern tab and the bulk window can each ask for the
        // room list, and a week change can land while the previous walk is still
        // in flight — so the app's real concurrency is a burst, not a pair. This
        // is the last shape that could explain 60 full pages on a 19-room tenant.
        Console.WriteLine("  a burst of 8 at once:");

        var burst = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(async () => i % 2 == 0
                ? $"ListRecorders={((await recorders.ListRecordersAsync())).Count}"
                : $"GetFoldersList={((await sessions.ListFoldersAsync())).Count}")));

        foreach (var line in burst) Console.WriteLine($"    {line}");
        Console.WriteLine();

        var wrong = burst.Where(l => !l.EndsWith("=19") && !l.EndsWith("=447")).ToList();
        if (both[0] > 1000 || both[1] > 1000 || wrong.Count > 0)
        {
            Console.WriteLine("  REPRODUCED: a listing returned a size the tenant does not have.");
            foreach (var line in wrong) Console.WriteLine($"    {line}");
            return 2;
        }

        Console.WriteLine("  Not reproduced: every call matched the tenant's real size.");
        return 0;
    }

    private static async Task<int> DumpPagesAsync(HttpClient http, OAuthPkceAuthenticator auth)
    {
        Console.WriteLine("── Session queries, side by side (read-only) ──");
        Console.WriteLine();

        // Spell out what the model produces, so a wrong field name is visible
        // rather than merely suspected.
        var fromModel = JsonSerializer.Serialize(
            new GetSessionsRequest
            {
                QueryParameters = new SessionQuery { Status = [1], MaxResults = DataSvcClient.MaxPageSize },
            },
            ModelOptions);

        Console.WriteLine("  the model sends:");
        Console.WriteLine($"    {fromModel}");
        Console.WriteLine();

        var variants = new (string Label, string Body)[]
        {
            ("model body, as-is", fromModel),

            // The same intent, all-lowercase initial letters: the casing the
            // Panopto web UI's own XHR uses.
            ("camelCase fields",
                """{"queryParameters":{"status":[1],"maxResults":500,"page":0,"sortColumn":1,"sortAscending":false}}"""),

            // And paging on top of the casing that binds, in case one implies
            // the other.
            ("camelCase, page 1",
                """{"queryParameters":{"status":[1],"maxResults":500,"page":1,"sortColumn":1,"sortAscending":false}}"""),
        };

        foreach (var (label, body) in variants)
        {
            Console.WriteLine($"  ── {label} ──");

            var outcome = await ReportListingAsync(http, auth, body);
            if (outcome is null) return 1;

            Console.WriteLine();
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        // The control: a query with no status filter at all. If this returns the
        // same rows as the filtered one, the filter is not what selects them.
        Console.WriteLine("  ── no status filter ──");
        if (await ReportListingAsync(http, auth, """{"queryParameters":{"maxResults":500}}""") is null)
            return 1;

        return 0;
    }

    /// <summary>
    /// One query, reported. Returns null when the call itself failed.
    /// </summary>
    private static async Task<string?> ReportListingAsync(
        HttpClient http, OAuthPkceAuthenticator auth, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DataSvcProbePath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        await auth.ApplyAsync(request);

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"    HTTP {(int)response.StatusCode} — {text[..Math.Min(200, text.Length)]}");
            return null;
        }

        using var doc = JsonDocument.Parse(text);

        if (!doc.RootElement.TryGetProperty("d", out var d) ||
            !d.TryGetProperty("Results", out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("    no d.Results array in the response");
            return null;
        }

        var statuses = new Dictionary<int, int>();
        string? firstId = null;
        string? firstName = null;

        foreach (var row in rows.EnumerateArray())
        {
            firstId ??= row.TryGetProperty("SessionID", out var id) ? id.GetString() : null;
            firstName ??= row.TryGetProperty("SessionName", out var n) ? n.GetString() : null;

            var status = row.TryGetProperty("Status", out var s) && s.TryGetInt32(out var value)
                ? value
                : -1;

            statuses[status] = statuses.GetValueOrDefault(status) + 1;
        }

        var histogram = string.Join(", ",
            statuses.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key.Label()}×{kv.Value}"));

        Console.WriteLine($"    rows {rows.GetArrayLength()}, " +
            $"TotalNumber {(d.TryGetProperty("TotalNumber", out var t) ? t.GetRawText() : "(absent)")}");
        Console.WriteLine($"    statuses: {histogram}");
        Console.WriteLine($"    first: {firstId}  {firstName}");

        return firstId;
    }

    private static async Task<int> VerifyWriteAsync(
        HttpClient http,
        IPanoptoAuthenticator auth,
        string recorderName,
        string? folderHint,
        DateTime startsAt,
        int minutes)
    {
        Console.WriteLine("── Verify write: one session, booked then removed ──");
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(recorderName))
        {
            Console.WriteLine("  --verify-write needs --recorder \"<name>\".");
            Console.WriteLine("  Run once with no arguments to list the tenant read-only.");
            Console.WriteLine();
            return 1;
        }

        var limiters = new RateLimiterRegistry();
        var soap = new PanoptoSoapClient(
            http, limiters, auth, new LegacyCookieProvider(http, limiters, auth));

        var recorderClient = new RemoteRecorderClient(soap);
        var sessionClient = new SessionManagementClient(soap);
        var dataSvc = new DataSvcClient(http, limiters, auth);

        RemoteRecorder? recorder;
        try
        {
            recorder = await recorderClient.FindRecorderAsync(recorderName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not list recorders: {ex.Message}");
            return 1;
        }

        if (recorder is null)
        {
            Console.WriteLine($"  No recorder named \"{recorderName}\". The tenant has:");
            foreach (var r in (await recorderClient.ListRecordersAsync()).Take(20))
                Console.WriteLine($"    {r.Name}");
            return 1;
        }

        Guid folderId;
        string folderLabel;
        try
        {
            if (!string.IsNullOrWhiteSpace(folderHint))
            {
                if (await sessionClient.FindFolderAsync(folderHint) is not { } folder)
                {
                    Console.WriteLine($"  No folder matching \"{folderHint}\".");
                    return 1;
                }

                folderId = folder.Id;
                folderLabel = folder.Name;
            }
            else
            {
                folderId = await recorderClient.GetDefaultFolderAsync(recorder.Id);
                folderLabel = "(this recorder's default folder)";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not resolve the folder: {ex.Message}");
            return 1;
        }

        if (folderId == Guid.Empty)
        {
            Console.WriteLine("  The recorder resolved to no folder, so there is nowhere to book.");
            Console.WriteLine("  Pass --folder to name one.");
            return 1;
        }

        var endsAt = startsAt.AddMinutes(minutes);

        Console.WriteLine($"  Recorder : {recorder.Name}   {recorder.Id:D}");
        Console.WriteLine($"  Folder   : {folderLabel}   {folderId:D}");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  Booking  : {0:yyyy-MM-dd HH:mm} → {1:HH:mm}   {2} min, wall clock",
            startsAt, endsAt, minutes));
        Console.WriteLine();

        // Refuse an hour that is already taken. Panopto would accept the call and
        // merely report ConflictsExist on the way back, which means a second
        // session in a room that may be mid-lecture — the one outcome a
        // verification run must not produce. Checking first turns that into a
        // message instead.
        IReadOnlyList<PanoptoSession> clashes;
        try
        {
            clashes = await ClashesOnAsync(dataSvc, recorder.Name, startsAt, endsAt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not read what {recorder.Name} already has: {ex.Message}");
            Console.WriteLine("  Not booking — a room in unknown state is not a quiet one.");
            return 1;
        }

        if (clashes.Count > 0)
        {
            Console.WriteLine($"  {recorder.Name} is not free across that slot:");
            foreach (var clash in clashes)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "    {0:yyyy-MM-dd HH:mm}  {1}",
                    clash.EffectiveStart!.Value, clash.SessionName));

            Console.WriteLine();
            Console.WriteLine("  Choose an --at that misses those, or a different --recorder.");
            return 1;
        }

        Console.WriteLine($"  {recorder.Name} is free across that slot. Nothing has been written yet.");
        Console.WriteLine();

        ScheduledRecordingResult scheduled;
        try
        {
            scheduled = await recorderClient.ScheduleAsync(
                $"ZZ probe, safe to delete ({startsAt:yyyy-MM-dd HH:mm})",
                folderId, isBroadcast: false, startsAt, endsAt, [recorder.Id]);
        }
        catch (Exception ex)
        {
            // A refusal is never retried, deliberately — a resent
            // ScheduleRecording is a second recording, not a repeat of the
            // first. So this is a clean answer, not a lost one.
            Console.WriteLine($"  REFUSED — {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("  Nothing was created, and the call was not resent.");
            return 1;
        }

        if (scheduled.SessionId == Guid.Empty)
        {
            Console.WriteLine("  FAILED — Panopto accepted the call but scheduled nothing.");
            return 1;
        }

        var id = scheduled.SessionId;
        Console.WriteLine($"  Scheduled : {id:D}");

        if (scheduled.ConflictsExist)
        {
            Console.WriteLine();
            Console.WriteLine("  Note: that hour was already booked, so the slot was not quiet —");
            foreach (var clash in scheduled.Conflicts) Console.WriteLine($"        {clash}");
            Console.WriteLine("        Re-run against a free hour for a clean reading.");
        }

        Console.WriteLine();

        try
        {
            // Data.svc can lag a booking by a moment, so an immediate read is
            // retried rather than reported as a failure to write.
            var readBack = await ReadBackAsync(dataSvc, id, recorder.Name, startsAt);

            Console.WriteLine("  Read back through Data.svc — the calendar's own path:");
            Console.WriteLine($"    listing held {readBack.Rows} scheduled session(s)");

            if (readBack.Match is null)
            {
                Console.WriteLine("  FAILED — booked, but it never read back. The booking is");
                Console.WriteLine("           real, so this is a read problem, not a write one.");

                if (readBack.Nearest.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"    {recorder.Name}'s nearest bookings to what was asked for:");
                    foreach (var near in readBack.Nearest)
                    {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "      {0:yyyy-MM-dd HH:mm}  {1}  [{2}]  session {3}",
                            near.EffectiveStart!.Value,
                            near.SessionName,
                            near.Status.Label(),
                            near.SessionID));
                    }
                }

                return 1;
            }

            if (readBack.MatchedOn != "SessionID")
            {
                Console.WriteLine();
                Console.WriteLine($"  Note: the returned id is the row's {readBack.MatchedOn}, not its");
                Console.WriteLine("        SessionID. The write is correct; anything keying on the id");
                Console.WriteLine("        ScheduleRecording returns needs to know that.");
            }

            var readBackRow = readBack.Match;

            if (readBackRow.EffectiveStart is not { } landed)
            {
                Console.WriteLine("  FAILED — the row read back, but carries no usable time:");
                Console.WriteLine($"           startTime={readBackRow.StartTime?.ToString("O") ?? "null"}");
                Console.WriteLine($"           scheduledStartTime={readBackRow.ScheduledStartTime?.ToString("O") ?? "null"}");
                return 1;
            }

            // The comparison is between two wall clocks, which is now simply what
            // both of them are: `startsAt` is the room's clock, and what came back
            // is the room's clock. Neither carries an offset, so there is nothing
            // here that could be converted by mistake — which is what a four-hour
            // invented drift on a booking that landed exactly where it was asked to
            // used to be.
            var gotWallClock = landed;
            var drift = gotWallClock - startsAt;

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "    asked for    : {0:yyyy-MM-dd HH:mm}", startsAt));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "    raw value    : {0}", landed.ToString("O")));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "    digits as sent: {0:yyyy-MM-dd HH:mm}", gotWallClock));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "    drift        : {0:+0.##;-0.##;0} hours", drift.TotalHours));
            Console.WriteLine();

            if (drift != TimeSpan.Zero)
            {
                Console.WriteLine("  ✗ MISMATCH — the digits moved. The write form is wrong, and a");
                Console.WriteLine("    booking would land at the 'digits as sent' time above, not");
                Console.WriteLine("    the one asked for. Nothing else should be scheduled until");
                Console.WriteLine("    this is understood.");
                return 1;
            }

            Console.WriteLine("  ✓ MATCH — what was asked for is what the tenant stored.");
            Console.WriteLine();
            Console.WriteLine("  What this does and does not prove: the digits survived the round");
            Console.WriteLine("  trip unchanged, so the write form is right. It does not prove");
            Console.WriteLine("  the room records at that hour — the remote recorder applies its");
            Console.WriteLine("  own zone when it acts on this, and nothing readable from here");
            Console.WriteLine("  reports that. Worth one booking in a room someone can watch.");
            Console.WriteLine();

            // The description round trip, on the same scratch session.
            //
            // The app writes a description with UpdateSessionDescription and reads
            // one back from a field called "Abstract", and those two names are
            // different enough that assuming they are the same storage would be a
            // guess. If they are not, the details panel would show one field and
            // overwrite another — silently replacing text the operator never saw,
            // which is the specific accident the delete gate exists to prevent.
            // Nothing here can settle it but a write and a read.
            //
            // Both ids are tried, because which one the write accepts is a fact
            // and not a deduction. They are not interchangeable: a run that keyed
            // the write on the SessionID Data.svc reports was refused outright —
            // "Invalid Session Id … at accessLevel: Creator" — which reads like a
            // permissions problem and is not one. Whichever id lands is the one
            // the app must key its own description edits on, and picking wrong
            // ships a presenter field that silently never writes.
            var candidates = new List<(string Label, Guid Id)>
            {
                ("DeliveryID (what ScheduleRecording returned)", id),
            };

            if (Guid.TryParse(readBackRow.SessionID, out var sessionId))
                candidates.Add(("SessionID (what Data.svc reports)", sessionId));
            else
                Console.WriteLine($"    note: the row reports no usable SessionID (\"{readBackRow.SessionID}\").");

            var roundTrip = false;

            foreach (var (label, candidate) in candidates)
                roundTrip |= await VerifyDescriptionAsync(sessionClient, dataSvc, candidate, label);

            Console.WriteLine("  Cleaning up…");

            return roundTrip ? 0 : 1;
        }
        finally
        {
            await RemoveAsync(sessionClient, id);
        }
    }

    /// <summary>
    /// Scheduled sessions on one recorder that overlap a window.
    ///
    /// <para>Read through the same <c>Data.svc</c> path the calendar uses, so
    /// "free" here means free as the operators see it rather than as some
    /// separate query guesses.</para>
    ///
    /// <para>A session whose length is unknown is flagged only when it starts
    /// inside the window. Assuming a length for it would invent clashes, and the
    /// cost of that is a test that refuses to run where a real one would have
    /// been fine.</para>
    /// </summary>
    private static async Task<IReadOnlyList<PanoptoSession>> ClashesOnAsync(
        DataSvcClient dataSvc,
        string recorderName,
        DateTime start,
        DateTime end)
    {
        // Every page, not the first one. A single request returns a clamped
        // slice, and a clash outside it reads as a free room.
        var all = await dataSvc.GetAllSessionsAsync([1]);

        return all.Where(s =>
        {
            if (!string.Equals(s.RemoteRecorderName, recorderName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (s.EffectiveStart is not { } at) return false;

            // Strict on both sides where a length is known, so a session ending
            // exactly when this one starts is not reported as a clash.
            return s.EffectiveDuration is { } length
                ? at < end && at + length > start
                : at >= start && at < end;
        }).ToList();
    }

    /// <summary>
    /// What a read-back found, and enough to explain a miss.
    /// </summary>
    /// <param name="Match">The row, or null when the id was not in the listing.</param>
    /// <param name="MatchedOn">Which id field matched — the two are not the same thing.</param>
    /// <param name="Rows">How many scheduled sessions the listing held.</param>
    /// <param name="Nearest">Sessions on the same recorder, closest in time to the booking.</param>
    private sealed record ReadBackResult(
        PanoptoSession? Match,
        string MatchedOn,
        int Rows,
        IReadOnlyList<PanoptoSession> Nearest);

    /// <summary>
    /// Finds a session by id, retrying briefly: <c>Data.svc</c> does not always
    /// show a booking the instant it is made, and treating that lag as a failure
    /// would report a working write as broken.
    ///
    /// <para>Matches <see cref="PanoptoSession.DeliveryID"/> as well as
    /// <see cref="PanoptoSession.SessionID"/>. A schedule call hands back one id
    /// and the listing keys rows by both, so matching only one field can report a
    /// working write as one that never happened. Which field hit is reported
    /// rather than assumed.</para>
    ///
    /// <para>On a miss it also says what the recorder's nearest bookings were, and
    /// how many rows the listing held. "The write is broken" and "the read is
    /// looking in the wrong place" are indistinguishable from a bare failure, and
    /// they call for opposite responses.</para>
    /// </summary>
    private static async Task<ReadBackResult> ReadBackAsync(
        DataSvcClient dataSvc, Guid id, string recorderName, DateTime startsAt)
    {
        var wanted = id.ToString("D");
        var rows = 0;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var all = await dataSvc.GetAllSessionsAsync([1]);
            rows = all.Count;

            var onSession = all.FirstOrDefault(s =>
                string.Equals(s.SessionID, wanted, StringComparison.OrdinalIgnoreCase));
            if (onSession is not null) return new(onSession, "SessionID", rows, []);

            var onDelivery = all.FirstOrDefault(s =>
                string.Equals(s.DeliveryID, wanted, StringComparison.OrdinalIgnoreCase));
            if (onDelivery is not null) return new(onDelivery, "DeliveryID", rows, []);

            if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(3));
        }

        var nearest = (await dataSvc.GetAllSessionsAsync([1]))
            .Where(s => string.Equals(s.RemoteRecorderName, recorderName, StringComparison.OrdinalIgnoreCase))
            .Where(s => s.EffectiveStart is not null)
            .OrderBy(s => Math.Abs((s.EffectiveStart!.Value - startsAt).TotalMinutes))
            .Take(5)
            .ToList();

        return new(null, string.Empty, rows, nearest);
    }

    /// <summary>
    /// Writes a description and reads it back, to settle whether
    /// <c>UpdateSessionDescription</c> and the <c>Abstract</c> field are the same
    /// storage.
    ///
    /// <para>They are named differently, and the details panel is about to let
    /// someone edit a description it displays from <c>Abstract</c>. If the two are
    /// not the same field, that panel shows one value and overwrites another —
    /// silently replacing text nobody looked at. Reading the code cannot answer
    /// it; Panopto's own dashboard could, by hand, once, and this is that check
    /// with the session deleted afterwards either way.</para>
    ///
    /// <para>Runs on the scratch session <c>--verify-write</c> has already created
    /// and is about to remove, so it adds no session to the tenant and no window
    /// in which one exists that would not have existed anyway.</para>
    /// </summary>
    /// <param name="label">
    /// Which id <paramref name="id"/> is, for the report. Two of them are tried and
    /// only one is accepted, so a result that does not say which id produced it
    /// cannot be acted on.
    /// </param>
    /// <returns>True when the write and the read agreed.</returns>
    private static async Task<bool> VerifyDescriptionAsync(
        SessionManagementClient sessions, DataSvcClient dataSvc, Guid id, string label)
    {
        // Distinctive on purpose. A marker that could plausibly have been there
        // already would let a stale value read as a successful round trip.
        var marker = $"probe description round trip {Guid.NewGuid():N}";

        Console.WriteLine($"  Description round trip — UpdateSessionDescription → Abstract, keyed on {label}:");
        Console.WriteLine($"    writing   : \"{marker}\"");
        Console.WriteLine($"    to id     : {id:D}");

        string? before;

        try
        {
            var (found, value) = await ReadDescriptionAsync(dataSvc, id);

            if (!found)
            {
                Console.WriteLine("    ✗ the row itself did not read back, so this check cannot run.");
                Console.WriteLine("      Not writing — a field in unknown state is not one to overwrite.");
                return false;
            }

            before = value;
            Console.WriteLine($"    it held   : {(before is null ? "(null)" : $"\"{before}\"")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    could not read it first: {ex.Message}");
            Console.WriteLine("    Not writing — a field in unknown state is not one to overwrite.");
            return false;
        }

        try
        {
            await sessions.UpdateSessionDescriptionAsync(id, marker);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ FAILED — the write was refused: {ex.Message}");
            Console.WriteLine("    Nothing was overwritten. A description edit in the panel would");
            Console.WriteLine("    fail the same way, so do not offer one until this is understood.");
            return false;
        }

        string? after;

        try
        {
            var (found, value) = await ReadDescriptionAsync(dataSvc, id);

            if (!found)
            {
                Console.WriteLine("  ✗ FAILED — the write went, but the row stopped reading back.");
                return false;
            }

            after = value;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ FAILED — written, but could not be read back: {ex.Message}");
            return false;
        }

        Console.WriteLine($"    reads back : {(after is null ? "(null)" : $"\"{after}\"")}");
        Console.WriteLine();

        if (string.Equals(after, marker, StringComparison.Ordinal))
        {
            Console.WriteLine("  ✓ MATCH — UpdateSessionDescription and Abstract are the same field.");
            Console.WriteLine("    The panel can show what it is about to overwrite: the read and the");
            Console.WriteLine("    write agree on where a description lives.");
            return true;
        }

        Console.WriteLine("  ✗ MISMATCH — they are different fields, or the read lags further than");
        Console.WriteLine("    four attempts. A panel that edits this would display one value and");
        Console.WriteLine("    replace another, so the description edit stays out until this is");
        Console.WriteLine("    settled by hand in Panopto's own dashboard.");
        return false;
    }

    /// <summary>
    /// The description as the calendar's own read path sees it, retried because
    /// <c>Data.svc</c> can lag a write by a moment.
    /// </summary>
    /// <remarks>
    /// <para>Looks the row up by <c>SessionID</c> only. <see cref="ReadBackAsync"/>
    /// also tries <c>DeliveryID</c>, which is right when asking whether a booking
    /// exists and wrong here: the id already read back by <c>SessionID</c>, so
    /// matching a different field would answer a question nobody asked.</para>
    ///
    /// <para>Returns whether the <b>row</b> was found as well as what the field
    /// held, because those are different facts and collapsing them would let "the
    /// read never worked" print as "the description is empty" — in the one check
    /// whose whole job is to tell those apart.</para>
    /// </remarks>
    private static async Task<(bool Found, string? Value)> ReadDescriptionAsync(
        DataSvcClient dataSvc, Guid id)
    {
        var wanted = id.ToString("D");

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var all = await dataSvc.GetAllSessionsAsync([1]);

            if (all.FirstOrDefault(s =>
                    string.Equals(s.SessionID, wanted, StringComparison.OrdinalIgnoreCase)) is { } row)
            {
                return (true, row.Description);
            }

            if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return (false, null);
    }

    /// <summary>
    /// Deletes the test session and says whether it went. Reported even on the
    /// failure path, because a verification run that leaves a recording behind in
    /// a real room is worse than one that never ran.
    /// </summary>
    private static async Task RemoveAsync(SessionManagementClient client, Guid id)
    {
        try
        {
            await client.DeleteSessionsAsync([id]);
            Console.WriteLine($"    removed {id:D}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    COULD NOT REMOVE {id:D} — {ex.Message}");
            Console.WriteLine("    Delete it by hand in the Panopto web UI.");
        }
    }

    private const string RestRecordersPath =
        "/Panopto/api/v1/remoteRecorders/search?searchQuery=a&maxResults=2";

    private const string RestFoldersPath =
        "/Panopto/api/v1/folders/search?searchQuery=a&maxResults=2";

    private const string RestScheduledProbePath =
        "/Panopto/api/v1/scheduledRecordings/00000000-0000-0000-0000-000000000000";

    /// <summary>How many results a REST search returned.</summary>
    private static async Task<int> CountAsync(HttpClient http, OAuthPkceAuthenticator auth, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        await auth.ApplyAsync(request);

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"{(int)response.StatusCode} {TrimBody(text)}");

        using var document = JsonDocument.Parse(text);
        return document.RootElement.TryGetProperty("Results", out var results)
            ? results.GetArrayLength()
            : 0;
    }

    /// <summary>
    /// Distinguishes "the route exists but that id does not" from "there is no
    /// such route". Both are 404, so only the message tells them apart.
    /// </summary>
    private static async Task<string> CheckRouteAsync(
        HttpClient http, OAuthPkceAuthenticator auth, string path)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            await auth.ApplyAsync(request);

            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (text.Contains("No scheduled recording was found", StringComparison.OrdinalIgnoreCase))
                return "route exists — no list endpoint, as documented";

            if (text.Contains("No HTTP resource was found", StringComparison.OrdinalIgnoreCase))
                return "NO SUCH ROUTE on this tenant";

            return response.IsSuccessStatusCode
                ? "route exists"
                : $"{(int)response.StatusCode} {TrimBody(text)}";
        }
        catch (Exception ex)
        {
            return $"threw: {ex.Message}";
        }
    }

    private static string TrimBody(string text)
        => text.Length <= 160 ? text : text[..160] + "…";

    /// <summary>
    /// Runs one read-only SOAP call and reports what came back. Returns null on
    /// failure so the summary can distinguish "not verified" from "returned
    /// nothing", which look identical in a count alone.
    /// </summary>
    private static async Task<int?> TryAsync(Func<Task<int>> call, string label)
    {
        try
        {
            var count = await call();
            Console.WriteLine($"   {label,-18} : OK — {count} result(s)");
            return count;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   {label,-18} : FAILED — {ex.Message}");
            return null;
        }
    }

    private static async Task<bool> ProbeAsync(
        HttpClient http,
        OAuthPkceAuthenticator auth,
        string path,
        HttpMethod method,
        string label,
        HttpContent? body)
    {
        Console.WriteLine($"── {label} ──");
        Console.WriteLine($"   {method} {path}");

        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = body };
            await auth.ApplyAsync(request);

            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"   status {response.StatusCode} ({(int)response.StatusCode})");

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"   {Summarise(text)}");
            }
            else
            {
                Console.WriteLine($"   {text[..Math.Min(300, text.Length)]}");
            }

            Console.WriteLine();
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   threw: {ex.Message}\n");
            return false;
        }
    }

    /// <summary>
    /// The exact queryParameters wrapper Data.svc requires — sending the fields
    /// unwrapped returns a WCF null-reference fault rather than a clear error.
    /// </summary>
    private static HttpContent BuildGetSessionsBody(int maxResults = 3, bool ascending = false)
        => JsonContent.Create(new
        {
            queryParameters = new
            {
                query = (string?)null,
                sortColumn = 1,
                sortAscending = ascending,
                maxResults,
                page = 0,
                startDate = (string?)null,
                endDate = (string?)null,
                folderID = (string?)null,
                status = new[] { 1 },
                bookmarked = false,
                getFolderData = false,
                isSharedWithMe = false,
                isSubscriptionsPage = false,
                includeArchived = true,
                includeArchivedStateCount = false,
                sessionListOnlyArchived = false,
                includePlaylists = true,
            },
        });

    /// <summary>
    /// Prints the time the tenant put on the wire next to the wall clock this
    /// machine reads it as, for real upcoming sessions.
    ///
    /// <para><b>Read-only.</b> One query, nothing created, moved or deleted.
    /// The question it answers is whether the scheduling parameters mean UTC or
    /// the room's own clock — which no Panopto document states, and which cannot
    /// be settled by reading the legacy uploader, because reading code tells you
    /// what its author believed, not what the service does with it.</para>
    ///
    /// <para>An operator recognises the times in the Panopto web UI. If the last
    /// column matches what the UI shows, the wire value is an instant and this
    /// machine's local conversion is already right. If it is out by a fixed
    /// number of hours, that gap is the tenant's offset — and a scheduler that
    /// derives times from the workstation's clock rather than the tenant's will
    /// book by it.</para>
    /// </summary>
    private static async Task PrintTimeModelAsync(HttpClient http, IPanoptoAuthenticator auth)
    {
        Console.WriteLine("── Time model: the wire value vs this machine's wall clock ──");

        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);
        Console.WriteLine($"   this machine : {TimeZoneInfo.Local.Id}, UTC{offset.Hours:+00;-00}:{offset.Minutes:00}");
        Console.WriteLine();

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, DataSvcProbePath)
            {
                Content = BuildGetSessionsBody(maxResults: 8, ascending: true),
            };

            await auth.ApplyAsync(request);

            using var response = await http.SendAsync(request);
            json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"   FAILED — {(int)response.StatusCode} {TrimBody(json)}\n");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAILED — {ex.Message}\n");
            return;
        }

        using var document = JsonDocument.Parse(json);
        var results = ResultsOf(document.RootElement);

        if (results is not { ValueKind: JsonValueKind.Array } sessions ||
            sessions.GetArrayLength() == 0)
        {
            Console.WriteLine("   No scheduled sessions came back, so there is nothing to");
            Console.WriteLine("   compare. Schedule one in the web UI and run this again.\n");
            return;
        }

        // The reading is settled — these milliseconds are the room's wall clock.
        // Both are printed anyway, because a bare /Date(…)/ cannot say so on its
        // own and a future shift should be visible rather than silent.
        Console.WriteLine("   The milliseconds are the room's wall clock, not an instant. Measured");
        Console.WriteLine("   against these sessions, whose names carry the time the operator meant;");
        Console.WriteLine("   the second line is what reading them as an instant gives, so a shift");
        Console.WriteLine("   shows up here rather than in an empty room.");
        Console.WriteLine();

        var shown = 0;
        var ids = new List<string>();

        foreach (var session in sessions.EnumerateArray())
        {
            if (shown++ == 8) break;

            var name = RawProp(session, "SessionName") ?? "(unnamed)";
            var raw = RawProp(session, "StartTime")
                      ?? RawProp(session, "ScheduledStartTime")
                      ?? "(none)";

            if (RawProp(session, "SessionID") is { Length: > 0 } id) ids.Add(id);

            Console.WriteLine($"   {name}");

            if (!WcfDateTimeConverter.TryParse(raw, out var value))
            {
                Console.WriteLine($"      wire    : {raw}   (unreadable)");
                Console.WriteLine();
                continue;
            }

            // These two are the room's time, and the four-hours-early reading the
            // app produced before it, side by side. The value is already the wall
            // clock; the second line reproduces what treating those same digits as
            // a time on this machine and converting to UTC would have given, which
            // is what the old reading did.
            var wallClock = value;
            var asInstant = TimeZoneInfo.ConvertTimeToUtc(value, TimeZoneInfo.Local);

            Console.WriteLine($"      wire       : {raw}");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "      wall clock : {0:yyyy-MM-dd HH:mm}   ← the room's time; matches the name",
                wallClock));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "      as instant : {0:yyyy-MM-dd HH:mm}   ← what reading it as a real UTC stamp gave",
                asInstant));
            Console.WriteLine();
        }

        // Data.svc cannot settle it, so ask the documented API, which renders
        // times as ISO-8601 with an offset. The offset is the tell: a value that
        // arrives as 05:25-04:00 was an instant, one that arrives as 09:25-04:00
        // was a wall clock the tenant stores stamped as UTC.
        Console.WriteLine("   The public REST API's view of the first few, for comparison:");
        Console.WriteLine();

        foreach (var id in ids.Take(3))
            await PrintRestTimesAsync(http, auth, id);
    }

    /// <summary>
    /// The documented API's view of one session — read-only, and unlike
    /// <c>Data.svc</c> it renders times as ISO-8601 rather than a bare
    /// <c>/Date(ms)/</c>, so the offset survives to be seen.
    ///
    /// <para>Two paths are tried because a scheduled recording and a completed
    /// session are different resources, and a future booking exists only in the
    /// first.</para>
    /// </summary>
    private static async Task PrintRestTimesAsync(
        HttpClient http, IPanoptoAuthenticator auth, string sessionId)
    {
        Console.WriteLine($"   session {sessionId}");

        foreach (var path in new[]
                 {
                     $"/Panopto/api/v1/scheduledRecordings/{sessionId}",
                     $"/Panopto/api/v1/sessions/{sessionId}",
                 })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                await auth.ApplyAsync(request);

                using var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();

                Console.WriteLine(response.IsSuccessStatusCode
                    ? $"      {Route(path),-26} {TimeFieldsOf(text)}"
                    : $"      {Route(path),-26} {(int)response.StatusCode} {TrimBody(text)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      {Route(path),-26} threw: {ex.Message}");
            }
        }

        Console.WriteLine();
    }

    private static string Route(string path)
        => path.StartsWith("/Panopto/api/v1/", StringComparison.Ordinal)
            ? path["/Panopto/api/v1/".Length..].Split('/')[0] + "/{id}"
            : path;

    /// <summary>
    /// Every time-bearing field in a REST body, printed <i>raw</i>. Parsing them
    /// into a <see cref="DateTime"/> first would throw away the offset, which is
    /// the one thing this is looking for.
    /// </summary>
    private static string TimeFieldsOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("d", out var d)) root = d;

            var fields = new List<string>();
            CollectTimes(root, "", fields);

            return fields.Count == 0 ? "(no time fields)" : string.Join(", ", fields);
        }
        catch (Exception ex)
        {
            return $"unparseable: {ex.Message}";
        }
    }

    private static void CollectTimes(JsonElement element, string prefix, List<string> fields)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                // One level down only: the session's own times are what matter,
                // and recursing further buries them under folder and viewer
                // metadata.
                if (prefix.Length == 0) CollectTimes(property.Value, property.Name + ".", fields);
                continue;
            }

            if (property.Name.Contains("Time", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Date", StringComparison.OrdinalIgnoreCase))
                fields.Add($"{prefix}{property.Name}={property.Value}");
        }
    }

    /// <summary>
    /// The results array, matched case-insensitively: Data.svc has returned both
    /// <c>Results</c> and <c>results</c> across versions, and a case-sensitive
    /// miss here reads as "no scheduled sessions" rather than as a parse failure.
    /// </summary>
    private static JsonElement? ResultsOf(JsonElement root)
    {
        if (!root.TryGetProperty("d", out var d)) return null;

        foreach (var property in d.EnumerateObject())
            if (string.Equals(property.Name, "Results", StringComparison.OrdinalIgnoreCase))
                return property.Value;

        return null;
    }

    /// <summary>A property's text, whatever case the tenant spelled it in.</summary>
    private static string? RawProp(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }

        return null;
    }

    private static string Summarise(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("d", out var d))
            {
                if (d.TryGetProperty("Results", out var results))
                    return $"returned {results.GetArrayLength()} session(s), TotalNumber={Get(d, "TotalNumber")}";
                return $"d = {Trim(d)}";
            }

            return Trim(root);
        }
        catch
        {
            return json[..Math.Min(200, json.Length)];
        }
    }

    private static string Get(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.ToString() : "?";

    private static string Trim(JsonElement element, int max = 200)
    {
        var text = element.ToString();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
