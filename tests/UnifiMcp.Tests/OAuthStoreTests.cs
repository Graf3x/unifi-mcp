using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace UnifiMcp.Tests;

public sealed class OAuthStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"oauth-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private SqliteOAuthStore NewStore() => new(_path, _time);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
            File.Delete(f);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private AuthorizationCode Code(string hash) => new()
    {
        CodeHash = hash,
        ClientId = "c1",
        RedirectUri = "https://claude.ai/cb",
        CodeChallenge = "challenge",
        Scope = "mcp",
        User = new OAuthUser { Subject = "u1", Email = "a@b.c", Name = "A" },
        ExpiresAt = _time.GetUtcNow().AddMinutes(5),
    };

    private RefreshToken Refresh(string hash, string family = "f1") => new()
    {
        TokenHash = hash,
        ClientId = "c1",
        FamilyId = family,
        Scope = "mcp",
        User = new OAuthUser { Subject = "u1", Email = "a@b.c", Name = "A" },
        ExpiresAt = _time.GetUtcNow().AddDays(30),
    };

    [Fact]
    public async Task Code_redeems_once()
    {
        var store = NewStore();
        await store.SaveCodeAsync(Code("h1"));

        var first = await store.RedeemCodeAsync("h1");
        Assert.NotNull(first);
        Assert.Equal("c1", first.ClientId);
        Assert.Equal("u1", first.User.Subject);
        Assert.Null(await store.RedeemCodeAsync("h1"));
    }

    [Fact]
    public async Task Expired_code_does_not_redeem()
    {
        var store = NewStore();
        await store.SaveCodeAsync(Code("h1"));
        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(await store.RedeemCodeAsync("h1"));
    }

    [Fact]
    public async Task Parallel_redeem_has_one_winner()
    {
        var store = NewStore();
        await store.SaveCodeAsync(Code("h1"));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.RedeemCodeAsync("h1"))));
        Assert.Equal(1, results.Count(r => r is not null));
    }

    [Fact]
    public async Task Revoking_one_family_does_not_block_another_family()
    {
        var store = NewStore();
        await store.SaveRefreshTokenAsync(Refresh("bad", "famA"));
        await store.SaveRefreshTokenAsync(Refresh("good", "famB"));
        await store.RevokeFamilyAsync("famA");

        Assert.True(await store.TryMarkRefreshTokenRedeemedAsync("good"));
        await store.SaveRefreshTokenAsync(Refresh("good2", "famB"));
        Assert.False((await store.GetRefreshTokenAsync("good2"))!.Revoked);
    }

    [Fact]
    public async Task Parallel_redeem_and_mark_stay_atomic_over_many_rounds()
    {
        var store = NewStore();
        for (var i = 0; i < 50; i++)
        {
            await store.SaveCodeAsync(Code($"c{i}"));
            await store.SaveRefreshTokenAsync(Refresh($"r{i}", $"f{i}"));
            var codes = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => store.RedeemCodeAsync($"c{i}"))));
            var marks = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => store.TryMarkRefreshTokenRedeemedAsync($"r{i}"))));
            Assert.Equal(1, (await codes).Count(r => r is not null));
            Assert.Equal(1, (await marks).Count(r => r));
        }
    }

    [Fact]
    public async Task Revoked_or_expired_refresh_token_cannot_be_marked_redeemed()
    {
        var store = NewStore();
        await store.SaveRefreshTokenAsync(Refresh("r1", "fam"));
        await store.RevokeFamilyAsync("fam");
        Assert.False(await store.TryMarkRefreshTokenRedeemedAsync("r1"));

        // A token saved into an already revoked family is born revoked.
        await store.SaveRefreshTokenAsync(Refresh("r2", "fam"));
        Assert.True((await store.GetRefreshTokenAsync("r2"))!.Revoked);
        Assert.False(await store.TryMarkRefreshTokenRedeemedAsync("r2"));

        await store.SaveRefreshTokenAsync(Refresh("r3", "other"));
        _time.Advance(TimeSpan.FromDays(31));
        Assert.False(await store.TryMarkRefreshTokenRedeemedAsync("r3"));
    }

    [Fact]
    public async Task Refresh_mark_redeemed_once()
    {
        var store = NewStore();
        await store.SaveRefreshTokenAsync(Refresh("r1"));
        Assert.True(await store.TryMarkRefreshTokenRedeemedAsync("r1"));
        Assert.False(await store.TryMarkRefreshTokenRedeemedAsync("r1"));
        Assert.True((await store.GetRefreshTokenAsync("r1"))!.Redeemed);

        await store.SaveRefreshTokenAsync(Refresh("r2"));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.TryMarkRefreshTokenRedeemedAsync("r2"))));
        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task Revoked_family_stays_revoked_for_60_days()
    {
        var store = NewStore();
        await store.SaveRefreshTokenAsync(Refresh("r1", "fam"));
        Assert.False(await store.IsFamilyRevokedAsync("fam"));

        await store.RevokeFamilyAsync("fam");
        Assert.True((await store.GetRefreshTokenAsync("r1"))!.Revoked);

        _time.Advance(TimeSpan.FromDays(59));
        Assert.True(await store.IsFamilyRevokedAsync("fam"));

        _time.Advance(TimeSpan.FromDays(2));
        Assert.True(await store.SweepExpiredAsync() > 0);
        Assert.False(await store.IsFamilyRevokedAsync("fam"));
        Assert.Null(await store.GetRefreshTokenAsync("r1"));
    }

    [Fact]
    public async Task Sweep_removes_expired_codes_and_refresh_tokens_only()
    {
        var store = NewStore();
        await store.SaveCodeAsync(Code("c-old"));
        await store.SaveRefreshTokenAsync(Refresh("r-old"));
        _time.Advance(TimeSpan.FromDays(31));
        await store.SaveCodeAsync(Code("c-new"));
        await store.SaveRefreshTokenAsync(Refresh("r-new"));

        Assert.Equal(2, await store.SweepExpiredAsync());
        Assert.NotNull(await store.RedeemCodeAsync("c-new"));
        Assert.NotNull(await store.GetRefreshTokenAsync("r-new"));
        Assert.Null(await store.GetRefreshTokenAsync("r-old"));
    }

    [Fact]
    public async Task Signing_key_first_writer_wins()
    {
        var store = NewStore();
        Assert.Null(await store.GetSigningKeyAsync());
        var first = await store.SaveSigningKeyIfAbsentAsync(new SigningKey { KeyId = "k1", ProtectedPrivateKey = "p1" });
        var second = await store.SaveSigningKeyIfAbsentAsync(new SigningKey { KeyId = "k2", ProtectedPrivateKey = "p2" });
        Assert.Equal("k1", first.KeyId);
        Assert.Equal("k1", second.KeyId);
        Assert.Equal("p1", (await store.GetSigningKeyAsync())!.ProtectedPrivateKey);
    }

    [Fact]
    public async Task Client_and_consent_round_trip_and_delete()
    {
        var store = NewStore();
        var created = await store.CreateClientAsync(new OAuthClient { ClientId = "c1", ClientName = "Claude", RedirectUris = ["https://claude.ai/cb"] });
        Assert.Equal(_time.GetUtcNow(), created.CreatedAt);
        Assert.Null((await store.GetClientAsync("c1"))!.LastUsedAt);
        _time.Advance(TimeSpan.FromHours(1));
        await store.TouchClientAsync("c1");
        Assert.Equal(_time.GetUtcNow(), (await store.GetClientAsync("c1"))!.LastUsedAt);
        Assert.Null(await store.GetClientAsync("nope"));

        await store.UpsertConsentAsync(new Consent { UserId = "u1", ClientId = "c1", ClientName = "Claude", Scope = "mcp", FamilyId = "f1", LastUsedAt = _time.GetUtcNow() });
        await store.UpsertConsentAsync(new Consent { UserId = "u1", ClientId = "c1", ClientName = "Claude", Scope = "mcp", FamilyId = "f2", LastUsedAt = _time.GetUtcNow() });
        await store.UpsertConsentAsync(new Consent { UserId = "u2", ClientId = "c1", ClientName = "Claude", Scope = "mcp", FamilyId = "f3", LastUsedAt = _time.GetUtcNow() });
        Assert.Equal("f2", (await store.GetConsentAsync("u1", "c1"))!.FamilyId);
        Assert.Single(await store.ListConsentsAsync("u1"));
        await store.DeleteConsentAsync("u1", "c1");
        Assert.Null(await store.GetConsentAsync("u1", "c1"));
        Assert.Empty(await store.ListConsentsAsync("u1"));
    }

    private long UserVersion()
    {
        using var conn = new SqliteConnection($"Data Source={_path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void A_new_database_is_schema_version_1()
    {
        NewStore();
        Assert.Equal(1, UserVersion());
        NewStore(); // reopening leaves it alone
        Assert.Equal(1, UserVersion());
    }

    [Fact]
    public async Task A_database_from_before_the_version_marker_is_upgraded_to_version_1()
    {
        // A development database: user_version 0 and a consents table without created_at.
        using (var conn = new SqliteConnection($"Data Source={_path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE consents (
                    user_id TEXT NOT NULL, client_id TEXT NOT NULL,
                    client_name TEXT NOT NULL, client_uri TEXT, scope TEXT NOT NULL, family_id TEXT NOT NULL,
                    last_used_at INTEGER NOT NULL,
                    PRIMARY KEY (user_id, client_id)
                );
                INSERT INTO consents VALUES ('u1', 'c1', 'Claude', NULL, 'mcp', 'f1', 1000);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(0, UserVersion());

        var store = NewStore();
        Assert.Equal(1, UserVersion());
        var consent = await store.GetConsentAsync("u1", "c1");
        Assert.Equal("f1", consent!.FamilyId);
        Assert.Equal(0, consent.CreatedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Store_survives_reopen()
    {
        var first = NewStore();
        await first.CreateClientAsync(new OAuthClient { ClientId = "c1", ClientName = "Claude", RedirectUris = ["https://claude.ai/cb"] });
        await first.UpsertConsentAsync(new Consent { UserId = "u1", ClientId = "c1", ClientName = "Claude", Scope = "mcp", FamilyId = "f1", LastUsedAt = _time.GetUtcNow() });
        SqliteConnection.ClearAllPools();

        var second = NewStore();
        var client = await second.GetClientAsync("c1");
        Assert.Equal("Claude", client!.ClientName);
        Assert.Equal(["https://claude.ai/cb"], client.RedirectUris);
        Assert.Equal("f1", (await second.GetConsentAsync("u1", "c1"))!.FamilyId);
    }

    [Fact]
    public async Task Nothing_is_stored_in_the_clear()
    {
        var store = NewStore();
        var hash = Hash("SECRET-VALUE");
        await store.SaveCodeAsync(Code(hash));
        await store.SaveRefreshTokenAsync(Refresh(hash));
        SqliteConnection.ClearAllPools();

        var bytes = await File.ReadAllBytesAsync(_path);
        Assert.False(bytes.AsSpan().IndexOf("SECRET-VALUE"u8) >= 0);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(hash)) >= 0);
    }

    [Fact]
    public void Models_do_not_print_secrets()
    {
        var key = new SigningKey { KeyId = "k", ProtectedPrivateKey = "PRIVATE-MATERIAL" };
        Assert.DoesNotContain("PRIVATE-MATERIAL", key.ToString());
        Assert.DoesNotContain("SECRET", new RefreshToken { TokenHash = "SECRET-HASH" }.ToString());
    }
}
