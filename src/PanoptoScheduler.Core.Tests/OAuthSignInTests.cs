using System.Net;
using System.Net.Sockets;
using PanoptoScheduler.Core.Auth;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// What the interactive sign-in does when nobody finishes it.
///
/// <para>The window expiring used to propagate as a bare
/// <see cref="OperationCanceledException"/>, which summarises as "Panopto did
/// not answer in time… the server busy" — advice about a network that had not
/// been consulted, while the browser tab the operator forgot stayed open. The
/// translation into <see cref="SignInWindowExpiredException"/> is what the
/// summary's own sentence is keyed off, and these are the tests that pin it.</para>
/// </summary>
public class OAuthSignInTests
{
    /// <summary>
    /// Binds port 0 to have the OS hand back a free port, then releases it. The
    /// redirect URI is per-test here rather than fixed, because a fixed port is
    /// exactly what cannot be relied on while several suites run at once.
    /// </summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static OAuthPkceAuthenticator Authenticator(int port)
        => new(
            new OAuthOptions
            {
                TenantUrl = "https://demo-host.example.edu",
                ClientId = "client-id",
                ClientSecret = "client-secret",
                RedirectUri = $"http://localhost:{port}/oauth/callback",
            },
            new HttpClient())
        {
            // Five real minutes is not a wait a suite can afford, and the expiry
            // path has to be exercised like any other.
            AuthorizationWindow = TimeSpan.FromMilliseconds(250),
        };

    [Fact]
    public async Task AnUnfinishedBrowserStepEndsAsItsOwnKindOfTimeout()
    {
        var authenticator = Authenticator(FreePort());

        // No redirect ever arrives — nobody opens the browser in a test, which
        // is the whole scenario: the operator started a sign-in and never
        // finished it.
        var expired = await Assert.ThrowsAsync<SignInWindowExpiredException>(
            () => authenticator.SignInAsync());

        Assert.Equal(TimeSpan.FromMilliseconds(250), expired.Window);
    }

    /// <summary>
    /// The translation is for the window only. A cancellation the caller asked
    /// for is an instruction that was obeyed, and reporting it as a five-minute
    /// expiry would claim the operator waited when they did not.
    /// </summary>
    [Fact]
    public async Task ACallerSCancellationIsNotCalledAnExpiredWindow()
    {
        var authenticator = Authenticator(FreePort());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // OperationCanceledException, not SignInWindowExpiredException — the
        // assert-any form is the point: this is the caller's own exception,
        // whatever flavour it arrives in, and it must reach them untouched.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => authenticator.SignInAsync(cts.Token));
    }
}