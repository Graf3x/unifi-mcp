using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace UnifiMcp.Tests;

/// <summary>The grant rules, straight through the ledger: a real SQLite file, a fake clock, real settings. No HTTP.</summary>
public sealed class GrantLedgerTests : IDisposable
{
    private const string Owner = "owner@example.test";
    private const string Family = "family-1";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"grants-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteOAuthStore _store;
    private Settings _settings = new() { SignIn = SignInMode.Google, AllowedGoogleEmailsValue = Owner };

    private static readonly OAuthUser User = new() { Subject = "google-sub-1", Email = Owner, Name = "Owner" };
    private static readonly Client Client =
        Client.Registered(new ClientRegistration { ClientId = "client-1", ClientName = "Claude", RedirectUris = ["https://claude.ai/cb"] });
    private static readonly Client OtherClient =
        Client.Registered(new ClientRegistration { ClientId = "client-2", ClientName = "Other", RedirectUris = ["https://other.example/cb"] });

    public GrantLedgerTests() => _store = new SqliteOAuthStore(_path, _time);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
            File.Delete(f);
    }

    private GrantLedger Ledger() => new(_store, _time, _settings);

    /// <summary>A standing grant: the owner's consent for <see cref="Client"/> and one live refresh token in its family.</summary>
    private async Task<string> GrantAsync(string family = Family, TimeSpan? expiresIn = null)
    {
        await _store.UpsertConsentAsync(new Consent
        {
            UserId = User.Subject, ClientId = Client.ConsentKey, ClientName = Client.DisplayName, Scope = "mcp:tools", FamilyId = family,
            CreatedAt = _time.GetUtcNow(), LastUsedAt = _time.GetUtcNow(),
        });
        var token = TokenIssuer.NewSecret();
        await _store.SaveRefreshTokenAsync(new RefreshToken
        {
            TokenHash = TokenIssuer.Hash(token), ClientId = Client.ClientId, FamilyId = family, Scope = "mcp:tools", User = User,
            ExpiresAt = _time.GetUtcNow().Add(expiresIn ?? TimeSpan.FromDays(30)),
        });
        return token;
    }

    private static GrantIssued Issued(GrantOutcome outcome) => Assert.IsType<GrantIssued>(outcome);
    private static GrantRefused Refused(GrantOutcome outcome) => Assert.IsType<GrantRefused>(outcome);

    // ---- rotation --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rotating_a_live_refresh_token_issues_the_next_one_in_the_same_family()
    {
        var token = await GrantAsync();

        var issued = Issued(await Ledger().RotateAsync(token, Client));

        Assert.NotEqual(token, issued.RefreshToken);
        Assert.Equal(Owner, issued.User.Email);
        Assert.Equal("mcp:tools", issued.Scope);
        var next = await _store.GetRefreshTokenAsync(TokenIssuer.Hash(issued.RefreshToken));
        Assert.NotNull(next);
        Assert.Equal(Family, next.FamilyId);
        Assert.Equal(_time.GetUtcNow().Add(GrantLedger.RefreshTokenLifetime), next.ExpiresAt);
        Assert.True((await _store.GetRefreshTokenAsync(TokenIssuer.Hash(token)))!.Redeemed);
    }

    [Fact]
    public async Task Rotating_marks_the_consent_as_used()
    {
        var token = await GrantAsync();
        _time.Advance(TimeSpan.FromHours(3));

        Issued(await Ledger().RotateAsync(token, Client));

        Assert.Equal(_time.GetUtcNow(), (await _store.GetConsentAsync(User.Subject, Client.ConsentKey))!.LastUsedAt);
    }

    [Fact]
    public async Task The_next_refresh_token_rotates_again()
    {
        var first = await GrantAsync();
        var second = Issued(await Ledger().RotateAsync(first, Client)).RefreshToken;

        Issued(await Ledger().RotateAsync(second, Client));
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_refused_as_invalid()
    {
        var refused = Refused(await Ledger().RotateAsync("not-a-token", Client));

        Assert.Equal(GrantRefusal.Invalid, refused.Kind);
        Assert.Equal("Refresh token is invalid or expired.", refused.Description);
        Assert.Null(refused.Label);
    }

    [Fact]
    public async Task A_refresh_token_presented_by_another_client_is_refused_and_the_family_survives()
    {
        var token = await GrantAsync();

        var refused = Refused(await Ledger().RotateAsync(token, OtherClient));

        Assert.Equal(GrantRefusal.Invalid, refused.Kind);
        Assert.Equal(Owner, refused.Label);
        Assert.Equal("refresh token was issued to another client", refused.Reason);
        Assert.False(await _store.IsFamilyRevokedAsync(Family));
        Issued(await Ledger().RotateAsync(token, Client));
    }

    [Fact]
    public async Task An_expired_refresh_token_is_refused_as_invalid()
    {
        var token = await GrantAsync(expiresIn: TimeSpan.FromMinutes(5));
        _time.Advance(TimeSpan.FromMinutes(5));

        var refused = Refused(await Ledger().RotateAsync(token, Client));

        Assert.Equal(GrantRefusal.Invalid, refused.Kind);
        Assert.Equal("refresh token expired", refused.Reason);
    }

    [Fact]
    public async Task A_refresh_token_of_a_revoked_family_is_refused_as_revoked()
    {
        var token = await GrantAsync();
        await _store.RevokeFamilyAsync(Family);

        var refused = Refused(await Ledger().RotateAsync(token, Client));

        Assert.Equal(GrantRefusal.Revoked, refused.Kind);
        Assert.Equal("Access was revoked.", refused.Description);
    }

    [Fact]
    public async Task Reusing_a_rotated_out_refresh_token_revokes_the_whole_family()
    {
        var first = await GrantAsync();
        var second = Issued(await Ledger().RotateAsync(first, Client)).RefreshToken;

        var refused = Refused(await Ledger().RotateAsync(first, Client));

        Assert.Equal(GrantRefusal.Reuse, refused.Kind);
        Assert.Equal("Refresh token reuse detected; access was revoked. Reconnect the application.", refused.Description);
        Assert.Equal("refresh token reuse; family revoked", refused.Reason);
        Assert.True(await _store.IsFamilyRevokedAsync(Family));
        Assert.Equal(GrantRefusal.Revoked, Refused(await Ledger().RotateAsync(second, Client)).Kind);
    }

    /// <summary>Runs the same call twice on two threads, released together, and returns both outcomes.</summary>
    private static async Task<GrantOutcome[]> TwiceAtOnceAsync(Func<Task<GrantOutcome>> call)
    {
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new[] { 0, 1 }.Select(_ => Task.Run(async () => { await go.Task; return await call(); })).ToArray();
        go.SetResult();
        return await Task.WhenAll(calls);
    }

    private const int Rounds = 20;

    [Fact]
    public async Task One_refresh_token_presented_twice_at_once_issues_one_successor_and_revokes_the_family()
    {
        // Whichever way the two requests interleave (the second reads the token after the first redeemed it, or both
        // read it first and the second loses the conditional write), one wins and the other is reuse.
        for (var round = 0; round < Rounds; round++)
        {
            var family = $"family-{round}";
            var token = await GrantAsync(family);
            var ledger = Ledger();

            var outcomes = await TwiceAtOnceAsync(() => ledger.RotateAsync(token, Client));

            var issued = Assert.Single(outcomes.OfType<GrantIssued>());
            Assert.Equal(GrantRefusal.Reuse, Assert.Single(outcomes.OfType<GrantRefused>()).Kind);
            Assert.True(await _store.IsFamilyRevokedAsync(family));
            Assert.Equal(GrantRefusal.Revoked, Refused(await ledger.RotateAsync(issued.RefreshToken, Client)).Kind);
        }
    }

    [Fact]
    public async Task An_owner_no_longer_on_the_allow_list_is_refused_and_the_family_revoked()
    {
        var token = await GrantAsync();
        _settings = _settings with { AllowedGoogleEmailsValue = "someone-else@example.test" };

        var refused = Refused(await Ledger().RotateAsync(token, Client));

        Assert.Equal(GrantRefusal.NotAdmitted, refused.Kind);
        Assert.Equal("This Google account isn't allowed any more.", refused.Description);
        Assert.Equal("NotListed; family revoked", refused.Reason);
        Assert.True(await _store.IsFamilyRevokedAsync(Family));
    }

    [Fact]
    public async Task A_google_grant_is_refused_after_switching_to_code_sign_in()
    {
        var token = await GrantAsync();
        _settings = _settings with { SignIn = SignInMode.Code };

        var refused = Refused(await Ledger().RotateAsync(token, Client));

        Assert.Equal(GrantRefusal.NotAdmitted, refused.Kind);
        Assert.Equal("This sign-in isn't allowed any more.", refused.Description);
        Assert.Equal("OtherSignIn; family revoked", refused.Reason);
        Assert.True(await _store.IsFamilyRevokedAsync(Family));
    }

    [Fact]
    public async Task A_withdrawn_consent_refuses_the_refresh_token()
    {
        var token = await GrantAsync();
        await _store.DeleteConsentAsync(User.Subject, Client.ConsentKey);

        var refused = Refused(await Ledger().RotateAsync(token, Client));

        Assert.Equal(GrantRefusal.ConsentWithdrawn, refused.Kind);
        Assert.Equal("Consent was withdrawn.", refused.Description);
    }

    // ---- authorize and approve -------------------------------------------------------------------------------------

    private static readonly CodeRequest Request = new(Redirect, Challenge(Verifier), "mcp:tools", null);

    [Fact]
    public async Task Authorize_without_a_consent_asks_for_one()
    {
        Assert.Null(await Ledger().AuthorizeAsync(User, Client, Request));
    }

    [Fact]
    public async Task Approve_starts_a_family_and_its_code_exchanges_into_it()
    {
        var code = await Ledger().ApproveAsync(User, Client, Request);

        var consent = await _store.GetConsentAsync(User.Subject, Client.ConsentKey);
        Assert.NotNull(consent);
        Assert.Equal("Claude", consent.ClientName);
        Assert.Null(consent.ClientUri);
        Assert.Equal("mcp:tools", consent.Scope);
        Assert.Equal(_time.GetUtcNow(), consent.CreatedAt);
        var issued = Issued(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client));
        Assert.Equal(consent.FamilyId, (await _store.GetRefreshTokenAsync(TokenIssuer.Hash(issued.RefreshToken)))!.FamilyId);
    }

    [Fact]
    public async Task Authorize_with_a_standing_consent_issues_a_code_in_the_same_family_and_marks_it_used()
    {
        await Ledger().ApproveAsync(User, Client, Request);
        var family = (await _store.GetConsentAsync(User.Subject, Client.ConsentKey))!.FamilyId;
        _time.Advance(TimeSpan.FromDays(2));

        var code = await Ledger().AuthorizeAsync(User, Client, Request);

        Assert.NotNull(code);
        var consent = (await _store.GetConsentAsync(User.Subject, Client.ConsentKey))!;
        Assert.Equal(family, consent.FamilyId);
        Assert.Equal(_time.GetUtcNow(), consent.LastUsedAt);
        Issued(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client));
    }

    [Fact]
    public async Task Authorize_asks_again_once_the_consents_family_was_revoked()
    {
        await Ledger().ApproveAsync(User, Client, Request);
        await _store.RevokeFamilyAsync((await _store.GetConsentAsync(User.Subject, Client.ConsentKey))!.FamilyId);

        Assert.Null(await Ledger().AuthorizeAsync(User, Client, Request));
    }

    [Fact]
    public async Task Approving_again_after_a_revocation_starts_a_fresh_family()
    {
        await Ledger().ApproveAsync(User, Client, Request);
        var old = (await _store.GetConsentAsync(User.Subject, Client.ConsentKey))!.FamilyId;
        await _store.RevokeFamilyAsync(old);

        var code = await Ledger().ApproveAsync(User, Client, Request);

        var fresh = (await _store.GetConsentAsync(User.Subject, Client.ConsentKey))!.FamilyId;
        Assert.NotEqual(old, fresh);
        Issued(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client));
    }

    [Fact]
    public async Task A_code_lasts_five_minutes()
    {
        var code = await Ledger().ApproveAsync(User, Client, Request);
        _time.Advance(GrantLedger.CodeLifetime);

        Assert.Equal(GrantRefusal.Invalid, Refused(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client)).Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), GrantLedger.CodeLifetime);
    }

    [Fact]
    public async Task A_code_carries_the_requested_redirect_and_challenge()
    {
        var code = await Ledger().ApproveAsync(User, Client, Request with { RedirectUri = "https://claude.ai/other" });

        Assert.Equal("redirect_uri does not match.", Refused(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client)).Description);
    }

    [Fact]
    public async Task Approving_a_metadata_document_client_records_its_document_url()
    {
        const string url = "https://claude.ai/oauth/claude-code-client-metadata";
        var client = new Client(url, Client.ConsentKeyFor(url), "claude.ai", [Redirect], true);

        await Ledger().ApproveAsync(User, client, Request);

        var consent = (await _store.GetConsentAsync(User.Subject, client.ConsentKey))!;
        Assert.Equal(url, consent.ClientUri);
        Assert.Equal("claude.ai", consent.ClientName);
    }

    // ---- revoke, disconnect, list, sweep ---------------------------------------------------------------------------

    private static readonly OAuthUser Stranger = new() { Subject = "google-sub-2", Email = "stranger@example.test", Name = "Stranger" };

    [Fact]
    public async Task Revoking_a_refresh_token_revokes_its_whole_family()
    {
        var first = await GrantAsync();
        var second = Issued(await Ledger().RotateAsync(first, Client)).RefreshToken;

        var revoked = await Ledger().RevokeAsync(first);

        Assert.NotNull(revoked);
        Assert.Equal(Owner, revoked.User.Email);
        Assert.Equal(Client.ClientId, revoked.ClientId);
        Assert.Equal(GrantRefusal.Revoked, Refused(await Ledger().RotateAsync(second, Client)).Kind);
        Assert.Null(await Ledger().AuthorizeAsync(User, Client, Request));
    }

    [Fact]
    public async Task Revoking_an_unknown_token_changes_nothing()
    {
        var token = await GrantAsync();

        Assert.Null(await Ledger().RevokeAsync("not-a-token"));
        Issued(await Ledger().RotateAsync(token, Client));
    }

    [Fact]
    public async Task Disconnecting_withdraws_the_consent_and_revokes_its_family()
    {
        var code = await Ledger().ApproveAsync(User, Client, Request);
        var token = Issued(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client)).RefreshToken;

        Assert.True(await Ledger().DisconnectAsync(User, Client.ConsentKey));

        Assert.Empty(await Ledger().ListConsentsAsync(User));
        Assert.Equal(GrantRefusal.Revoked, Refused(await Ledger().RotateAsync(token, Client)).Kind);
        Assert.Null(await Ledger().AuthorizeAsync(User, Client, Request));
    }

    [Fact]
    public async Task Disconnecting_a_client_that_is_not_connected_changes_nothing()
    {
        Assert.False(await Ledger().DisconnectAsync(User, Client.ConsentKey));
    }

    [Fact]
    public async Task An_owner_cannot_disconnect_someone_elses_consent()
    {
        await Ledger().ApproveAsync(Stranger, Client, Request);

        Assert.False(await Ledger().DisconnectAsync(User, Client.ConsentKey));

        Assert.NotNull(await Ledger().AuthorizeAsync(Stranger, Client, Request));
    }

    [Fact]
    public async Task Listing_shows_only_the_owners_own_consents()
    {
        await Ledger().ApproveAsync(User, Client, Request);
        await Ledger().ApproveAsync(User, OtherClient, Request);
        await Ledger().ApproveAsync(Stranger, Client, Request);

        var mine = await Ledger().ListConsentsAsync(User);

        Assert.Equal(["client-1", "client-2"], mine.Select(c => c.ClientId).Order());
    }

    [Fact]
    public async Task The_sweep_removes_expired_codes_and_refresh_tokens()
    {
        await Ledger().ApproveAsync(User, Client, Request);
        await GrantAsync(expiresIn: TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(2, await Ledger().SweepAsync());
        Assert.Equal(0, await Ledger().SweepAsync());
    }

    // ---- code exchange ---------------------------------------------------------------------------------------------

    private const string Verifier = "verifier-0123456789-0123456789-0123456789-0123456789";
    private const string Redirect = "https://claude.ai/cb";

    private static string Challenge(string verifier) =>
        Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

    /// <summary>A consent for <see cref="Client"/> (no tokens yet) and a fresh code issued under it.</summary>
    private async Task<string> CodeAsync(string family = Family, bool withConsent = true, TimeSpan? expiresIn = null)
    {
        if (withConsent)
            await _store.UpsertConsentAsync(new Consent
            {
                UserId = User.Subject, ClientId = Client.ConsentKey, ClientName = Client.DisplayName, Scope = "mcp:tools", FamilyId = family,
                CreatedAt = _time.GetUtcNow(), LastUsedAt = _time.GetUtcNow(),
            });
        var code = TokenIssuer.NewSecret();
        await _store.SaveCodeAsync(new AuthorizationCode
        {
            CodeHash = TokenIssuer.Hash(code), ClientId = Client.ClientId, RedirectUri = Redirect, CodeChallenge = Challenge(Verifier),
            Scope = "mcp:tools", User = User, ExpiresAt = _time.GetUtcNow().Add(expiresIn ?? TimeSpan.FromMinutes(5)),
        });
        return code;
    }

    [Fact]
    public async Task Exchanging_a_code_issues_the_first_refresh_token_into_the_consents_family()
    {
        var code = await CodeAsync();

        var issued = Issued(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client));

        Assert.Equal(Owner, issued.User.Email);
        Assert.Equal("mcp:tools", issued.Scope);
        var stored = await _store.GetRefreshTokenAsync(TokenIssuer.Hash(issued.RefreshToken));
        Assert.NotNull(stored);
        Assert.Equal(Family, stored.FamilyId);
        Assert.Equal(_time.GetUtcNow().Add(GrantLedger.RefreshTokenLifetime), stored.ExpiresAt);
        Issued(await Ledger().RotateAsync(issued.RefreshToken, Client));
    }

    [Fact]
    public async Task A_redirect_uri_left_out_of_the_exchange_is_accepted()
    {
        var code = await CodeAsync();

        Issued(await Ledger().ExchangeAsync(code, Verifier, "", Client));
    }

    [Fact]
    public async Task One_code_exchanged_twice_at_once_issues_one_grant()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var family = $"family-{round}";
            var code = await CodeAsync(family);
            var ledger = Ledger();

            var outcomes = await TwiceAtOnceAsync(() => ledger.ExchangeAsync(code, Verifier, Redirect, Client));

            var issued = Assert.Single(outcomes.OfType<GrantIssued>());
            Assert.Equal(GrantRefusal.Invalid, Assert.Single(outcomes.OfType<GrantRefused>()).Kind);
            Assert.False(await _store.IsFamilyRevokedAsync(family));
            Issued(await ledger.RotateAsync(issued.RefreshToken, Client));
        }
    }

    [Fact]
    public async Task A_code_works_once()
    {
        var code = await CodeAsync();
        Issued(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client));

        var refused = Refused(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client));

        Assert.Equal(GrantRefusal.Invalid, refused.Kind);
        Assert.Equal("Authorization code is invalid, expired or already used.", refused.Description);
        Assert.Null(refused.Label);
    }

    [Fact]
    public async Task An_expired_code_is_refused_as_invalid()
    {
        var code = await CodeAsync(expiresIn: TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(GrantRefusal.Invalid, Refused(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client)).Kind);
    }

    [Fact]
    public async Task A_code_presented_by_another_client_is_refused()
    {
        var code = await CodeAsync();

        var refused = Refused(await Ledger().ExchangeAsync(code, Verifier, Redirect, OtherClient));

        Assert.Equal(GrantRefusal.Invalid, refused.Kind);
        Assert.Equal(Owner, refused.Label);
        Assert.Equal("code was issued to another client", refused.Reason);
    }

    [Fact]
    public async Task A_different_redirect_uri_is_refused()
    {
        var code = await CodeAsync();

        var refused = Refused(await Ledger().ExchangeAsync(code, Verifier, "https://evil.example/cb", Client));

        Assert.Equal(GrantRefusal.Invalid, refused.Kind);
        Assert.Equal("redirect_uri does not match.", refused.Description);
    }

    [Fact]
    public async Task A_wrong_verifier_is_refused_and_spends_the_code()
    {
        var code = await CodeAsync();

        var refused = Refused(await Ledger().ExchangeAsync(code, Verifier + "x", Redirect, Client));

        Assert.Equal(GrantRefusal.Invalid, refused.Kind);
        Assert.Equal("PKCE verification failed.", refused.Description);
        Assert.Equal(GrantRefusal.Invalid, Refused(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client)).Kind);
    }

    [Fact]
    public async Task A_code_for_an_owner_no_longer_allowed_is_refused_without_revoking_the_family()
    {
        var code = await CodeAsync();
        _settings = _settings with { AllowedGoogleEmailsValue = "someone-else@example.test" };

        var refused = Refused(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client));

        Assert.Equal(GrantRefusal.NotAdmitted, refused.Kind);
        Assert.Equal("This Google account isn't allowed any more.", refused.Description);
        Assert.Equal("NotListed", refused.Reason);
        Assert.False(await _store.IsFamilyRevokedAsync(Family));
    }

    [Fact]
    public async Task A_code_whose_consent_was_withdrawn_is_refused()
    {
        var code = await CodeAsync(withConsent: false);

        var refused = Refused(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client));

        Assert.Equal(GrantRefusal.ConsentWithdrawn, refused.Kind);
        Assert.Equal("Consent was withdrawn.", refused.Description);
    }

    [Fact]
    public async Task A_code_issued_under_a_family_revoked_since_is_refused()
    {
        var code = await CodeAsync();
        await _store.RevokeFamilyAsync(Family);

        var refused = Refused(await Ledger().ExchangeAsync(code, Verifier, Redirect, Client));

        Assert.Equal(GrantRefusal.Revoked, refused.Kind);
        Assert.Equal("Access was revoked. Connect the application again.", refused.Description);
        Assert.Equal("consent's family is revoked", refused.Reason);
    }

    [Fact]
    public async Task A_consent_given_again_since_refuses_tokens_of_the_old_family()
    {
        var old = await GrantAsync("old-family");
        await GrantAsync("new-family");

        var refused = Refused(await Ledger().RotateAsync(old, Client));

        Assert.Equal(GrantRefusal.ConsentWithdrawn, refused.Kind);
        Assert.False(await _store.IsFamilyRevokedAsync("old-family"));
    }
}
