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

/// <summary>
/// The grant stands: who it is for, its scope, the client's next refresh token (the only copy of it) and the access
/// token the caller's signer made, if it gave one.
/// </summary>
public sealed record GrantIssued(OAuthUser User, string Scope, string RefreshToken, string? AccessToken = null) : GrantOutcome;

/// <summary>Signs an access token for a grant about to be issued. It may fail; the grant is then left as it was.</summary>
public delegate Task<string> AccessTokenSigner(OAuthUser user, string scope, CancellationToken ct);

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
    internal async Task<string?> AuthorizeAsync(OAuthUser user, Client client, CodeRequest request, CancellationToken ct = default)
    {
        var (code, stored) = NewCode(user, client, request);
        // One transaction: the consent is checked, the code saved into its family and the consent marked used, so a
        // disconnect or a fresh approval at the same moment is never undone.
        return await store.IssueCodeUnderConsentAsync(stored, client.ConsentKey, ct) ? code : null;
    }

    /// <summary>The owner approves a client: a consent with a fresh family (replacing any earlier one) and its first code.</summary>
    internal async Task<string> ApproveAsync(OAuthUser user, Client client, CodeRequest request, CancellationToken ct = default)
    {
        var consent = new Consent
        {
            UserId = user.Subject, ClientId = client.ConsentKey, ClientName = client.DisplayName, ClientUri = client.FromMetadataDocument ? client.ClientId : null,
            Scope = request.Scope, FamilyId = TokenIssuer.NewSecret(), CreatedAt = time.GetUtcNow(), LastUsedAt = time.GetUtcNow(),
        };
        var (code, stored) = NewCode(user, client, request);
        await store.ApproveAsync(consent, stored, ct);
        return code;
    }

    /// <summary>A single-use code and the record of it the store keeps (its hash only; the store fills in its family).</summary>
    private (string Code, AuthorizationCode Stored) NewCode(OAuthUser user, Client client, CodeRequest request)
    {
        var code = TokenIssuer.NewSecret();
        return (code, new AuthorizationCode
        {
            CodeHash = TokenIssuer.Hash(code), ClientId = client.ClientId, RedirectUri = request.RedirectUri, CodeChallenge = request.CodeChallenge,
            Scope = request.Scope, Resource = request.Resource, User = user, ExpiresAt = time.GetUtcNow().Add(CodeLifetime),
        });
    }

    private const string InvalidRefresh = "Refresh token is invalid or expired.";
    private const string InvalidCode = "Authorization code is invalid, expired or already used.";
    private const string ReuseDetected = "Refresh token reuse detected; access was revoked. Reconnect the application.";

    /// <summary>
    /// Trades a refresh token for the next one in its family. A token presented again after it was used means it leaked,
    /// so the whole family is revoked; so is a family whose owner is no longer admitted. <paramref name="sign"/> makes
    /// the access token before anything is written, so if it fails the presented token still works.
    /// </summary>
    internal async Task<GrantOutcome> RotateAsync(string presented, Client client, AccessTokenSigner? sign = null, CancellationToken ct = default)
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
        // A grant that no longer stands is refused before signing, so a signer that cannot work never hides that refusal.
        // The write checks again, for a change made while the access token is signed.
        if (RotationRefused(await store.GetStandingAsync(stored.User.Subject, client.ConsentKey, stored.FamilyId, ct), who) is { } early)
            return early;
        var access = sign is null ? null : await sign(stored.User, stored.Scope, ct);
        var (next, successor) = NewRefreshToken(client, stored.FamilyId, stored.Scope, stored.User);
        var written = await store.RotateRefreshTokenAsync(hash, successor, client.ConsentKey, ct);
        if (written == GrantWrite.Done) return new GrantIssued(stored.User, stored.Scope, next, access);
        if (RotationRefused(written, who) is { } refused) return refused;
        // Another request redeemed (or revoked) this token between our read and our write: treat as reuse.
        await store.RevokeFamilyAsync(stored.FamilyId, ct);
        return new GrantRefused(GrantRefusal.Reuse, ReuseDetected, who, "concurrent refresh token reuse; family revoked");
    }

    /// <summary>Why a rotation cannot be written, or null when it is done or its token was spent by someone else.</summary>
    private static GrantRefused? RotationRefused(GrantWrite write, string who) => write switch
    {
        GrantWrite.FamilyRevoked => new GrantRefused(GrantRefusal.Revoked, "Access was revoked.", who, "refresh token's family is revoked"),
        GrantWrite.ConsentWithdrawn => new GrantRefused(GrantRefusal.ConsentWithdrawn, "Consent was withdrawn.", who),
        // Ran out, unused, while the access token was signed: nobody else holds it, so it is not reuse.
        GrantWrite.Expired => new GrantRefused(GrantRefusal.Invalid, InvalidRefresh, who, "refresh token expired"),
        _ => null,
    };

    /// <summary>
    /// Trades an authorization code for the first refresh token of the family it was issued under, while that family is
    /// still the owner's consent. The code is spent before it is checked, so a wrong verifier or redirect burns it. An
    /// empty <paramref name="redirectUri"/> means the client sent none. <paramref name="sign"/> makes the access token
    /// before the refresh token is saved; the code is already spent by then.
    /// </summary>
    internal async Task<GrantOutcome> ExchangeAsync(string code, string verifier, string redirectUri, Client client, AccessTokenSigner? sign = null,
        CancellationToken ct = default)
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

        // A code mints only into the family it was issued under, and only while that family is the owner's consent: a
        // code from before a disconnect, or from a consent given again since, yields nothing. The next authorize shows
        // the consent page again, and that starts a fresh family. Checked before signing, so a signer that cannot work
        // never hides that refusal, and again as the token is saved.
        if (ExchangeRefused(await store.GetStandingAsync(stored.User.Subject, client.ConsentKey, stored.FamilyId, ct), who) is { } early)
            return early;
        var access = sign is null ? null : await sign(stored.User, stored.Scope, ct);
        var (refresh, first) = NewRefreshToken(client, stored.FamilyId, stored.Scope, stored.User);
        var written = await store.SaveFirstRefreshTokenAsync(first, client.ConsentKey, ct);
        return (GrantOutcome?)ExchangeRefused(written, who) ?? new GrantIssued(stored.User, stored.Scope, refresh, access);
    }

    /// <summary>Why a code's first refresh token cannot be written, or null when it can.</summary>
    private static GrantRefused? ExchangeRefused(GrantWrite write, string who) => write switch
    {
        GrantWrite.Done => null,
        GrantWrite.FamilyRevoked => new GrantRefused(GrantRefusal.Revoked, "Access was revoked. Connect the application again.", who, "consent's family is revoked"),
        _ => new GrantRefused(GrantRefusal.ConsentWithdrawn, "Consent was withdrawn.", who),
    };

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
    /// The owner withdraws their consent for a client (by its consent key): its family is revoked and the consent removed
    /// with it, so the client has to be approved again. False when the owner has no such consent; another owner's
    /// consent, or one given again since the read, is never touched.
    /// </summary>
    internal async Task<bool> DisconnectAsync(OAuthUser owner, string consentKey, CancellationToken ct = default)
    {
        if (await store.GetConsentAsync(owner.Subject, consentKey, ct) is not { } consent) return false;
        await store.RevokeFamilyAsync(consent.FamilyId, ct);
        return true;
    }

    /// <summary>The owner's consents, for the Connections page.</summary>
    internal Task<IReadOnlyList<Consent>> ListConsentsAsync(OAuthUser owner, CancellationToken ct = default) =>
        store.ListConsentsAsync(owner.Subject, ct);

    /// <summary>Removes expired codes and refresh tokens and old family tombstones; returns how many rows went.</summary>
    internal Task<int> SweepAsync(CancellationToken ct = default) => store.SweepExpiredAsync(ct);

    /// <summary>A refresh token in a family and the record of it the store keeps (its hash only).</summary>
    private (string Token, RefreshToken Stored) NewRefreshToken(Client client, string familyId, string scope, OAuthUser user)
    {
        var token = TokenIssuer.NewSecret();
        return (token, new RefreshToken
        {
            TokenHash = TokenIssuer.Hash(token), ClientId = client.ClientId, FamilyId = familyId, Scope = scope,
            User = user, ExpiresAt = time.GetUtcNow().Add(RefreshTokenLifetime),
        });
    }

    private static string NotAllowedAnyMore(Settings settings) =>
        settings.SignIn == SignInMode.Code ? "This sign-in isn't allowed any more." : "This Google account isn't allowed any more.";
}
