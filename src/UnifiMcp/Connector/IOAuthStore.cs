namespace UnifiMcp;

public interface IOAuthStore
{
    Task<OAuthClient> CreateClientAsync(OAuthClient client, CancellationToken ct = default);
    Task<OAuthClient?> GetClientAsync(string clientId, CancellationToken ct = default);
    Task TouchClientAsync(string clientId, CancellationToken ct = default);

    Task SaveCodeAsync(AuthorizationCode code, CancellationToken ct = default);
    /// <summary>Reads the code and marks it redeemed in one step; null when unknown, expired or already redeemed.</summary>
    Task<AuthorizationCode?> RedeemCodeAsync(string codeHash, CancellationToken ct = default);

    Task SaveRefreshTokenAsync(RefreshToken token, CancellationToken ct = default);
    Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken ct = default);
    /// <summary>Marks a refresh token redeemed only if it is not already; false means another request got there first (treat as reuse).</summary>
    Task<bool> TryMarkRefreshTokenRedeemedAsync(string tokenHash, CancellationToken ct = default);
    /// <summary>Revokes every token in the family and leaves a tombstone so the family stays revoked for 60 days.</summary>
    Task RevokeFamilyAsync(string familyId, CancellationToken ct = default);
    Task<bool> IsFamilyRevokedAsync(string familyId, CancellationToken ct = default);

    Task UpsertConsentAsync(Consent consent, CancellationToken ct = default);
    Task<Consent?> GetConsentAsync(string userId, string clientId, CancellationToken ct = default);
    Task<IReadOnlyList<Consent>> ListConsentsAsync(string userId, CancellationToken ct = default);
    Task DeleteConsentAsync(string userId, string clientId, CancellationToken ct = default);

    /// <summary>The wrapped signing key, or null when none has been generated yet.</summary>
    Task<SigningKey?> GetSigningKeyAsync(CancellationToken ct = default);
    /// <summary>Stores the key unless one already exists; returns whichever is now stored (first writer wins).</summary>
    Task<SigningKey> SaveSigningKeyIfAbsentAsync(SigningKey key, CancellationToken ct = default);

    /// <summary>Removes expired codes, expired refresh tokens and family tombstones older than 60 days; returns the rows removed.</summary>
    Task<int> SweepExpiredAsync(CancellationToken ct = default);
}
