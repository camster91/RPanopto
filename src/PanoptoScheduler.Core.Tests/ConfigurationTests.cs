using System.Net;
using System.Net.Sockets;
using PanoptoScheduler.Core.Auth;
using PanoptoScheduler.Core.Configuration;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// Where a packaged install gets its configuration, and what it says when a
/// machine cannot be signed in to at all.
///
/// <para>These exist because the packaged path is the one nobody exercises by
/// hand: a developer checkout has no <c>defaults.json</c> and always takes the
/// dialog, so the shipped-defaults branch would ship untested. A mistake there
/// is not subtle either — it is the difference between a new user seeing the
/// calendar and a new user being asked for a client secret they do not have.</para>
///
/// <para>Every test writes its own files in a temporary directory, so none of
/// them depends on, or disturbs, the machine they run on.</para>
/// </summary>
public class ConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "panopto-config-tests", Guid.NewGuid().ToString("N"));

    public ConfigurationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string? tenant = "https://tenant.example",
        string? clientId = "client-id", string? secret = "client-secret")
    {
        var file = Path.Combine(_dir, name);

        var json =
            $$"""
              {
                "tenantUrl": {{Quote(tenant)}},
                "clientId": {{Quote(clientId)}},
                "clientSecret": {{Quote(secret)}}
              }
              """;

        File.WriteAllText(file, json);
        return file;
    }

    private static string Quote(string? value) =>
        value is null ? "null" : $"\"{value}\"";

    [Fact]
    public void The_persons_own_file_wins_over_the_shipped_one()
    {
        var mine = Write("credentials.json", tenant: "https://mine.example");
        var shipped = Write("defaults.json", tenant: "https://shipped.example");

        var resolved = CredentialStore.Resolve(mine, shipped);

        // The point of the precedence: an admin can repoint one machine, or
        // rotate the secret on it, without repackaging the app for everyone.
        Assert.Equal("https://mine.example", resolved!.TenantUrl);
    }

    [Fact]
    public void The_shipped_file_is_used_when_the_person_has_none()
    {
        var shipped = Write("defaults.json", tenant: "https://shipped.example");

        var resolved = CredentialStore.Resolve(
            Path.Combine(_dir, "absent.json"), shipped);

        Assert.Equal("https://shipped.example", resolved!.TenantUrl);
    }

    [Fact]
    public void A_machine_with_nothing_configured_resolves_to_nothing()
    {
        // Not an error: this is the development checkout, and the caller's
        // answer is to ask, which is what the dialog is for.
        Assert.Null(CredentialStore.Resolve(
            Path.Combine(_dir, "absent.json"),
            Path.Combine(_dir, "also-absent.json")));
    }

    [Fact]
    public void A_shipped_file_that_cannot_be_read_is_an_error_not_a_fallback()
    {
        var shipped = Path.Combine(_dir, "defaults.json");
        File.WriteAllText(shipped, "{ this is not json");

        // Falling back to the dialog would be friendlier and worse: it would
        // hide a packaging mistake, and the app would then behave differently
        // on one machine with nothing on screen to say why.
        Assert.ThrowsAny<Exception>(() => CredentialStore.Resolve(
            Path.Combine(_dir, "absent.json"), shipped));
    }

    [Fact]
    public void A_shipped_file_missing_a_field_is_an_error()
    {
        var shipped = Write("defaults.json", secret: null);

        Assert.Throws<InvalidOperationException>(() => CredentialStore.Resolve(
            Path.Combine(_dir, "absent.json"), shipped));
    }

    [Fact]
    public void The_shipped_file_is_looked_for_beside_the_app()
    {
        // Resolved against the executable's folder rather than the working
        // directory: the app is launched by double-click, by shortcut, and by a
        // shortcut with a different "start in", and all three must find it.
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "defaults.json"),
            ShippedDefaults.DefaultPath);
    }

    [Fact]
    public void The_rooms_zone_travels_with_the_shipped_file()
    {
        var file = Path.Combine(_dir, "defaults.json");
        File.WriteAllText(file, """
            {
              "tenantUrl": "https://tenant.example",
              "clientId": "client-id",
              "clientSecret": "client-secret",
              "timeZone": "America/Toronto"
            }
            """);

        var resolved = ShippedDefaults.TryLoad(file);

        // Load-bearing for writes, so a packaged file that carries it is the
        // difference between booking the hour asked for and four hours off.
        Assert.Equal("America/Toronto", resolved!.TimeZone);
        Assert.Equal("America/Toronto", resolved.ResolveTimeZone().Id);
    }

    /// <summary>
    /// The port the browser hands the sign-in back on. The failure was a raw
    /// socket error in eleven-point grey text, which told the person holding the
    /// laptop nothing they could act on — and the usual cause is that they
    /// already have the app open.
    /// </summary>
    [Fact]
    public void A_port_already_in_use_says_what_to_do_about_it()
    {
        // A real listener on a real port, so this is the actual failure rather
        // than a mock of it. Port 0 lets the OS pick a free one.
        var blocking = new TcpListener(IPAddress.Loopback, 0);
        blocking.Start();
        var port = ((IPEndPoint)blocking.LocalEndpoint).Port;

        try
        {
            var listener = new LoopbackListener(port, "/oauth/callback");
            var error = Assert.Throws<InvalidOperationException>(() => listener.Start());

            Assert.Contains($"Port {port}", error.Message, StringComparison.Ordinal);

            // Names the likely cause and the remedy, not just the symptom.
            Assert.Contains("already", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Panopto Scheduler", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            blocking.Stop();
        }
    }

    /// <summary>The free-port case still works, so the guard above is not vacuous.</summary>
    [Fact]
    public async Task A_free_port_binds()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        await using var listener = new LoopbackListener(port, "/oauth/callback");
        listener.Start();
    }
}
