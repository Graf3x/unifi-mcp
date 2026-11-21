namespace UnifiMcp;

/// <summary>Why the ledger turned a request down. Every kind is an OAuth <c>invalid_grant</c>.</summary>
public enum GrantRefusal
{
    /// <summary>Unknown, expired, or issued to another client.</summary>
    Invalid,
    /// <summary>The token or its family was revoked.</summary>
    Revoked,
    /// <summary>A refresh token presented after it was used: the family is revoked.</summary>
    Reuse,
    /// <summary>The identity is no longer the owner (allow-list or sign-in mode changed): the family is revoked.</summary>
    NotAdmitted,
    /// <summary>The consent is gone, or was given again since and started a new family.</summary>
    ConsentWithdrawn,
}

/// <summary>What a code is bound to: where it may be redeemed from, its PKCE challenge, scope and resource.</summary>
public sealed record CodeRequest(string RedirectUri, string CodeChallenge, string Scope, string? Resource);

/// <summary>A family revoked by a client returning one of its refresh tokens: whose it was, for the log.</summary>
public sealed record GrantRevoked(OAuthUser User, string ClientId);

public abstract record GrantOutcome;

/// <summary>The grant stands: who it is for, its scope, and the client's next refresh token (the only copy of it).</summary>
public sealed record GrantIssued(OAuthUser User, string Scope, string RefreshToken) : GrantOutcome;

/// <summary>
/// The grant does not stand. <see cref="Description"/> is safe to send to the client; <see cref="Label"/> (who, when
/// known) and <see cref="Reason"/> are for the log only.
/// </summary>
public sealed record GrantRefused(GrantRefusal Kind, string Description, string? Label = null, string? Reason = null) : GrantOutcome;

/// <summary>
/// The grant ledger: every consent, its refresh-token family and the rules that decide whether a grant still stands.
/// Callers present what the client sent; the ledger checks, rotates and revokes in the right order and says what
/// happened. It knows nothing of HTTP and writes no logs.
/// </summary>
public sealed class GrantLedger(SqliteOAuthStore store, TimeProvider time, Settings settings)
{
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The signed-in owner asks again for a client they approved before: a code if that consent still stands, or null
    /// when the owner has to approve (no consent, or its family was revoked, since every token minted into a revoked
    /// family is born revoked).
    /// </summary>
    internal async Task<string?> AuthorizeAsync(OAuthUser user, OAuthEndpoints.ResolvedClient client, CodeRequest request, CancellationToken ct = default)
    {
        var existing = await store.GetConsentAsync(user.Subject, client.Key, ct);
        if (existing is null || await store.IsFamilyRevokedAsync(existing.FamilyId, ct)) return null;
        return await IssueCodeAsync(user, client, existing, request, ct);
    }

    /// <summary>The owner approves a client: a consent with a fresh family (replacing any earlier one) and its first code.</summary>
    internal async Task<string> ApproveAsync(OAuthUser user, OAuthEndpoints.ResolvedClient client, CodeRequest request, CancellationToken ct = default)
    {
        var consent = new Consent
        {
            UserId = user.Subject, ClientId = client.Key, ClientName = client.DisplayName, ClientUri = client.FromMetadataDocument ? client.ClientId : null,
            Scope = request.Scope, FamilyId = TokenIssuer.NewSecret(), CreatedAt = time.GetUtcNow(), LastUsedAt = time.GetUtcNow(),
        };
        await store.UpsertConsentAsync(consent, ct);
        return await IssueCodeAsync(user, client, consent, request, ct);
    }

    /// <summary>Saves a single-use code (its hash only) and marks the consent used; returns the code itself.</summary>
    private async Task<string> IssueCodeAsync(OAuthUser user, OAuthEndpoints.ResolvedClient client, Consent consent, CodeRequest request, CancellationToken ct)
    {
        var code = TokenIssuer.NewSecret();
        await store.SaveCodeAsync(new AuthorizationCode
        {
            CodeHash = TokenIssuer.Hash(code), ClientId = client.ClientId, RedirectUri = request.RedirectUri, CodeChallenge = request.CodeChallenge,
            Scope = request.Scope, Resource = request.Resource, User = user, ExpiresAt = time.GetUtcNow().Add(CodeLifetime),
        }, ct);
        consent.LastUsedAt = time.GetUtcNow();
        await store.UpsertConsentAsync(consent, ct);
        return code;
    }

    private const string InvalidRefresh = "Refresh token is invalid or expired.";
    private const string InvalidCode = "Authorization code is invalid, expired or already used.";
    private const string ReuseDetected = "Refresh token reuse detected; access was revoked. Reconnect the application.";

    /// <summary>
    /// Trades a refresh token for the next one in its family. A token presented again after it was used means it leaked,
    /// so the whole family is revoked; so is a family whose owner is no longer admitted.
    /// </summary>
    internal async Task<GrantOutcome> RotateAsync(string presented, OAuthEndpoints.ResolvedClient client, CancellationToken ct = default)
    {
        var hash = TokenIssuer.Hash(presented);
        var stored = await store.GetRefreshTokenAsync(hash, ct);
        // The store keeps expired rows until the hourly sweep, so expiry is checked here.
        if (stored is null) return new GrantRefused(GrantRefusal.Invalid, InvalidRefresh);
        var who = stored.User.Label;
        if (stored.ClientId != client.ClientId)
            return new GrantRefused(GrantRefusal.Invalid, InvalidRefresh, who, "refresh token was issued to another client");
        if (stored.ExpiresAt <= time.GetUtcNow())
            return new GrantRefused(GrantRefusal.Invalid, InvalidRefresh, who, "refresh token expired");
        if (stored.Revoked || await store.IsFamilyRevokedAsync(stored.FamilyId, ct))
            return new GrantRefused(GrantRefusal.Revoked, "Access was revoked.", who, "refresh token's family is revoked");
        if (stored.Redeemed)
        {
            // A rotated-out token presented again: either a replay or a leak. Kill the whole family.
            await store.RevokeFamilyAsync(stored.FamilyId, ct);
            return new GrantRefused(GrantRefusal.Reuse, ReuseDetected, who, "refresh token reuse; family revoked");
        }
        // Admission is decided again on every refresh, so removing the address ends access within the hour.
        if (OwnerAdmission.Admit(stored.User, settings) is var admitted && admitted != Admission.Allowed)
        {
            await store.RevokeFamilyAsync(stored.FamilyId, ct);
            return new GrantRefused(GrantRefusal.NotAdmitted, NotAllowedAnyMore(settings), who, $"{admitted}; family revoked");
        }
        var consent = await store.GetConsentAsync(stored.User.Subject, client.Key, ct);
        if (consent is null || consent.FamilyId != stored.FamilyId)
            return new GrantRefused(GrantRefusal.ConsentWithdrawn, "Consent was withdrawn.", who);

        if (!await store.TryMarkRefreshTokenRedeemedAsync(hash, ct))
        {
            // Another request redeemed (or revoked) this token between our read and our write: treat as reuse.
            await store.RevokeFamilyAsync(stored.FamilyId, ct);
            return new GrantRefused(GrantRefusal.Reuse, ReuseDetected, who, "concurrent refresh token reuse; family revoked");
        }
        var next = await SaveRefreshTokenAsync(client, stored.FamilyId, stored.Scope, stored.User, ct);
        consent.LastUsedAt = time.GetUtcNow();
        await store.UpsertConsentAsync(consent, ct);
        return new GrantIssued(stored.User, stored.Scope, next);
    }

    /// <summary>
    /// Trades an authorization code for the first refresh token of its consent's family. The code is spent before it is
    /// checked, so a wrong verifier or redirect burns it. An empty <paramref name="redirectUri"/> means the client sent none.
    /// </summary>
    internal async Task<GrantOutcome> ExchangeAsync(string code, string verifier, string redirectUri, OAuthEndpoints.ResolvedClient client, CancellationToken ct = default)
    {
        var stored = await store.RedeemCodeAsync(TokenIssuer.Hash(code), ct);
        if (stored is null) return new GrantRefused(GrantRefusal.Invalid, InvalidCode);
        var who = stored.User.Label;
        if (stored.ClientId != client.ClientId)
            return new GrantRefused(GrantRefusal.Invalid, InvalidCode, who, "code was issued to another client");
        if (!string.IsNullOrEmpty(redirectUri) && redirectUri != stored.RedirectUri)
            return new GrantRefused(GrantRefusal.Invalid, "redirect_uri does not match.", who);
        if (!TokenIssuer.VerifyPkce(verifier, stored.CodeChallenge))
            return new GrantRefused(GrantRefusal.Invalid, "PKCE verification failed.", who);
        if (OwnerAdmission.Admit(stored.User, settings) is var admitted && admitted != Admission.Allowed)
            return new GrantRefused(GrantRefusal.NotAdmitted, NotAllowedAnyMore(settings), who, admitted.ToString());

        // A code issued under a family revoked since then never mints into it (the token would be born revoked). The
        // next authorize shows the consent page again, and that starts a fresh family.
        var consent = await store.GetConsentAsync(stored.User.Subject, client.Key, ct);
        if (consent is null) return new GrantRefused(GrantRefusal.ConsentWithdrawn, "Consent was withdrawn.", who);
        if (await store.IsFamilyRevokedAsync(consent.FamilyId, ct))
            return new GrantRefused(GrantRefusal.Revoked, "Access was revoked. Connect the application again.", who, "consent's family is revoked");

        var refresh = await SaveRefreshTokenAsync(client, consent.FamilyId, stored.Scope, stored.User, ct);
        return new GrantIssued(stored.User, stored.Scope, refresh);
    }

    /// <summary>
    /// RFC 7009: a client gives back a refresh token (live or already used) and its whole family is revoked. Null for a
    /// token the ledger does not know, which changes nothing.
    /// </summary>
    internal async Task<GrantRevoked?> RevokeAsync(string presented, CancellationToken ct = default)
    {
        if (await store.GetRefreshTokenAsync(TokenIssuer.Hash(presented), ct) is not { } stored) return null;
        await store.RevokeFamilyAsync(stored.FamilyId, ct);
        return new GrantRevoked(stored.User, stored.ClientId);
    }

    /// <summary>
    /// The owner withdraws their consent for a client (by its consent key): its family is revoked, then the consent is
    /// removed, so the client has to be approved again. False when the owner has no such consent; another owner's
    /// consent is never touched.
    /// </summary>
    internal async Task<bool> DisconnectAsync(OAuthUser owner, string consentKey, CancellationToken ct = default)
    {
        if (await store.GetConsentAsync(owner.Subject, consentKey, ct) is not { } consent) return false;
        await store.RevokeFamilyAsync(consent.FamilyId, ct);
        await store.DeleteConsentAsync(owner.Subject, consentKey, ct);
        return true;
    }

    /// <summary>The owner's consents, for the Connections page.</summary>
    internal Task<IReadOnlyList<Consent>> ListConsentsAsync(OAuthUser owner, CancellationToken ct = default) =>
        store.ListConsentsAsync(owner.Subject, ct);

    /// <summary>Removes expired codes and refresh tokens and old family tombstones; returns how many rows went.</summary>
    internal Task<int> SweepAsync(CancellationToken ct = default) => store.SweepExpiredAsync(ct);

    /// <summary>Mints a refresh token into a family and stores only its hash; returns the token itself.</summary>
    private async Task<string> SaveRefreshTokenAsync(OAuthEndpoints.ResolvedClient client, string familyId, string scope, OAuthUser user, CancellationToken ct)
    {
        var token = TokenIssuer.NewSecret();
        await store.SaveRefreshTokenAsync(new RefreshToken
        {
            TokenHash = TokenIssuer.Hash(token), ClientId = client.ClientId, FamilyId = familyId, Scope = scope,
            User = user, ExpiresAt = time.GetUtcNow().Add(RefreshTokenLifetime),
        }, ct);
        return token;
    }

    private static string NotAllowedAnyMore(Settings settings) =>
        settings.SignIn == SignInMode.Code ? "This sign-in isn't allowed any more." : "This Google account isn't allowed any more.";
}
