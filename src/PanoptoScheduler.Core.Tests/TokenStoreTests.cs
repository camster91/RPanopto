using System.Net;
using System.Text;
using System.Text.Json;
using PanoptoScheduler.Core.Auth;
using PanoptoScheduler.Core.Configuration;

namespace PanoptoScheduler.Core.Tests;

/// <summary>In-memory stand-in, so auth tests never touch the disk or DPAPI.</summary>
internal sealed class FakeTokenStore : ITokenStore
{
    private readonly Dictionary<string, TokenSet> _items = new(StringComparer.Ordinal);

    public int Saves { get; private set; }
    public int Clears { get; private set; }

    public TokenSet? Load(string tenantUrl, string clientId)
        => _items.TryGetValue($"{tenantUrl}|{clientId}", out var t) ? t : null;

    public void Save(string tenantUrl, string clientId, TokenSet tokens)
    {
        Saves++;
        _items[$"{tenantUrl}|{clientId}"] = tokens;
    }

    public void Clear(string tenantUrl, string clientId)
    {
        Clears++;
        _items.Remove($"{tenantUrl}|{clientId}");
    }
}

/// <summary>Serves one canned token response for any request.</summary>
internal sealed class StubTokenEndpoint(string json) : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }
}

public class DpapiTokenStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"panopto-tokens-{Guid.NewGuid():N}.dat");

    private const string Tenant = "https://rotman.ca.panopto.com";
    private const string Client = "client-a";

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Round_trips_a_session()
    {
        var store = new DpapiTokenStore(_path);
        var tokens = new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        store.Save(Tenant, Client, tokens);
        var loaded = store.Load(Tenant, Client);

        Assert.NotNull(loaded);
        Assert.Equal("access-1", loaded!.AccessToken);
        Assert.Equal("refresh-1", loaded.RefreshToken);
    }

    /// <summary>
    /// The whole reason for DPAPI: a refresh token is a long-lived credential,
    /// and this file can end up in a backup or a synced profile.
    /// </summary>
    [Fact]
    public void Does_not_leave_the_refresh_token_readable_on_disk()
    {
        var store = new DpapiTokenStore(_path);
        store.Save(Tenant, Client, new TokenSet(
            "access-secret-value", "refresh-secret-value", DateTimeOffset.UtcNow.AddHours(1)));

        var raw = File.ReadAllBytes(_path);
        var asText = Encoding.UTF8.GetString(raw);

        Assert.DoesNotContain("refresh-secret-value", asText);
        Assert.DoesNotContain("access-secret-value", asText);
    }

    [Fact]
    public void Sessions_are_keyed_by_tenant_and_client()
    {
        var store = new DpapiTokenStore(_path);
        store.Save(Tenant, Client, new TokenSet("a", "r", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.NotNull(store.Load(Tenant, Client));
        Assert.Null(store.Load(Tenant, "client-b"));
        Assert.Null(store.Load("https://other.ca.panopto.com", Client));
    }

    [Fact]
    public void Missing_file_is_a_cache_miss_not_an_error()
    {
        Assert.Null(new DpapiTokenStore(_path).Load(Tenant, Client));
    }

    [Fact]
    public void Clears_a_session()
    {
        var store = new DpapiTokenStore(_path);
        store.Save(Tenant, Client, new TokenSet("a", "r", DateTimeOffset.UtcNow.AddHours(1)));

        store.Clear(Tenant, Client);

        Assert.Null(store.Load(Tenant, Client));
    }

    /// <summary>
    /// A file written under another account, or truncated, must not wedge the
    /// app on every launch.
    /// </summary>
    [Fact]
    public void Unreadable_cache_is_discarded_rather_than_thrown()
    {
        File.WriteAllBytes(_path, [1, 2, 3, 4, 5, 6, 7, 8]);
        var store = new DpapiTokenStore(_path);

        Assert.Null(store.Load(Tenant, Client));
        Assert.False(File.Exists(_path));

        // And it is usable again afterwards.
        store.Save(Tenant, Client, new TokenSet("a", "r", DateTimeOffset.UtcNow.AddHours(1)));
        Assert.NotNull(store.Load(Tenant, Client));
    }

    [Fact]
    public void Keeps_other_sessions_when_writing_one()
    {
        var store = new DpapiTokenStore(_path);
        store.Save(Tenant, Client, new TokenSet("a", "r", DateTimeOffset.UtcNow.AddHours(1)));
        store.Save(Tenant, "client-b", new TokenSet("b", "r2", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal("a", store.Load(Tenant, Client)!.AccessToken);
        Assert.Equal("b", store.Load(Tenant, "client-b")!.AccessToken);
    }

    [Fact]
    public void Computed_expiry_is_not_persisted()
    {
        // A stale IsExpired on disk would be read back and believed.
        var store = new DpapiTokenStore(_path);
        store.Save(Tenant, Client, new TokenSet("a", "r", DateTimeOffset.UtcNow.AddHours(1)));

        var json = JsonSerializer.Serialize(store.Load(Tenant, Client));

        Assert.DoesNotContain("IsExpired", json);
        Assert.DoesNotContain("CanRefresh", json);
    }
}

public class AuthenticatorTokenCacheTests
{
    private static OAuthOptions Options() => new()
    {
        TenantUrl = "https://rotman.ca.panopto.com",
        ClientId = "client-a",
        ClientSecret = "shh",
    };

    private static HttpClient ClientFor(string json, out StubTokenEndpoint handler)
    {
        handler = new StubTokenEndpoint(json);
        return new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };
    }

    [Fact]
    public async Task Restore_reports_a_saved_session_as_usable()
    {
        var store = new FakeTokenStore();
        store.Save(Options().TenantUrl, "client-a",
            new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));

        var http = ClientFor("{}", out _);
        await using var auth = new OAuthPkceAuthenticator(Options(), http, store);

        Assert.True(auth.Restore());
        Assert.True(auth.HasStoredSession);
        Assert.True(auth.IsAuthenticated);
    }

    [Fact]
    public async Task Restore_is_false_when_nothing_is_cached()
    {
        var http = ClientFor("{}", out _);
        await using var auth = new OAuthPkceAuthenticator(Options(), http, new FakeTokenStore());

        Assert.False(auth.Restore());
        Assert.False(auth.IsAuthenticated);
    }

    [Fact]
    public async Task Restore_is_false_without_a_store()
    {
        var http = ClientFor("{}", out _);
        await using var auth = new OAuthPkceAuthenticator(Options(), http);

        Assert.False(auth.Restore());
    }

    /// <summary>
    /// Panopto can rotate the refresh token. If the rotated value is not written
    /// back, the next launch presents a token that has already been spent.
    /// </summary>
    [Fact]
    public async Task Refreshing_writes_the_rotated_refresh_token_back()
    {
        var store = new FakeTokenStore();
        store.Save(Options().TenantUrl, "client-a",
            new TokenSet("stale-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-5)));

        // Seeding goes through the same counter, so measure from here.
        var savesAfterSeed = store.Saves;

        var http = ClientFor(
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""",
            out var handler);

        await using var auth = new OAuthPkceAuthenticator(Options(), http, store);
        Assert.True(auth.Restore());

        var token = await auth.GetValidAccessTokenAsync();

        Assert.Equal("new-access", token);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(savesAfterSeed + 1, store.Saves);
        Assert.Equal("new-refresh", store.Load(Options().TenantUrl, "client-a")!.RefreshToken);
    }

    /// <summary>
    /// RFC 6749 §6 lets a refresh grant omit <c>refresh_token</c>. Reading that
    /// as "no refresh token" would persist a set that cannot refresh, so the
    /// next launch demands an interactive browser sign-in — with the
    /// previously good token already overwritten on disk. The old refresh
    /// token is carried forward instead.
    /// </summary>
    [Fact]
    public async Task A_refresh_response_without_a_refresh_token_keeps_the_old_one()
    {
        var store = new FakeTokenStore();
        store.Save(Options().TenantUrl, "client-a",
            new TokenSet("stale-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-5)));

        var http = ClientFor(
            """{"access_token":"new-access","expires_in":3600}""",
            out var handler);

        await using var auth = new OAuthPkceAuthenticator(Options(), http, store);
        Assert.True(auth.Restore());

        var token = await auth.GetValidAccessTokenAsync();

        Assert.Equal("new-access", token);
        Assert.Equal(1, handler.Calls);
        var persisted = store.Load(Options().TenantUrl, "client-a")!;
        Assert.Equal("old-refresh", persisted.RefreshToken);
        Assert.True(persisted.CanRefresh);
    }

    [Fact]
    public async Task A_live_access_token_is_reused_without_calling_the_endpoint()
    {
        var store = new FakeTokenStore();
        store.Save(Options().TenantUrl, "client-a",
            new TokenSet("good-access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));

        var http = ClientFor("{}", out var handler);
        await using var auth = new OAuthPkceAuthenticator(Options(), http, store);
        auth.Restore();

        Assert.Equal("good-access", await auth.GetValidAccessTokenAsync());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Signing_out_clears_the_cached_session()
    {
        var store = new FakeTokenStore();
        store.Save(Options().TenantUrl, "client-a",
            new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));

        var http = ClientFor("{}", out _);
        await using var auth = new OAuthPkceAuthenticator(Options(), http, store);
        auth.Restore();

        auth.SignOut();

        Assert.Equal(1, store.Clears);
        Assert.False(auth.IsAuthenticated);
        Assert.False(auth.HasStoredSession);
    }

    [Fact]
    public async Task An_expired_session_with_no_refresh_token_cannot_be_reused()
    {
        var store = new FakeTokenStore();
        store.Save(Options().TenantUrl, "client-a",
            new TokenSet("old", null, DateTimeOffset.UtcNow.AddMinutes(-5)));

        var http = ClientFor("{}", out _);
        await using var auth = new OAuthPkceAuthenticator(Options(), http, store);

        // Nothing to refresh with, so the user must sign in again.
        Assert.False(auth.Restore());
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.GetValidAccessTokenAsync());
    }
}

public class CredentialStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"panopto-creds-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Round_trips_credentials()
    {
        var saved = new PanoptoCredentials
        {
            TenantUrl = "https://rotman.ca.panopto.com",
            ClientId = "abc",
            ClientSecret = "def",
        };

        CredentialStore.Save(saved, _path);

        Assert.True(CredentialStore.Exists(_path));
        var loaded = CredentialStore.Load(_path);
        Assert.Equal(saved.TenantUrl, loaded.TenantUrl);
        Assert.Equal("abc", loaded.ClientId);
        Assert.Equal("def", loaded.ClientSecret);
    }

    /// <summary>The file is hand-edited, so the keys a person types must work.</summary>
    [Fact]
    public void Reads_the_camel_case_keys_a_person_would_type()
    {
        File.WriteAllText(_path, """
            {
              "tenantUrl": "https://rotman.ca.panopto.com",
              "clientId": "abc",
              "clientSecret": "def"
            }
            """);

        Assert.Equal("abc", CredentialStore.Load(_path).ClientId);
    }

    [Fact]
    public void TryLoad_returns_null_when_absent()
    {
        Assert.Null(CredentialStore.TryLoad(_path));
    }

    [Fact]
    public void Rejects_a_tenant_that_is_not_a_url()
    {
        var bad = new PanoptoCredentials
        {
            TenantUrl = "rotman.ca.panopto.com",
            ClientId = "abc",
            ClientSecret = "def",
        };

        Assert.Throws<InvalidOperationException>(() => CredentialStore.Save(bad, _path));
    }

    [Fact]
    public void Rejects_a_blank_secret()
    {
        var bad = new PanoptoCredentials
        {
            TenantUrl = "https://rotman.ca.panopto.com",
            ClientId = "abc",
            ClientSecret = "   ",
        };

        Assert.Throws<InvalidOperationException>(() => CredentialStore.Save(bad, _path));
    }
}
