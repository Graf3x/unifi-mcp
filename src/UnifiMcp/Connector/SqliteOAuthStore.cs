using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace UnifiMcp;

/// <summary>What a grant write found: done, or why nothing was written.</summary>
public enum GrantWrite
{
    Done,
    /// <summary>The family is revoked.</summary>
    FamilyRevoked,
    /// <summary>The owner's consent for the client is gone, or now belongs to another family.</summary>
    ConsentWithdrawn,
    /// <summary>The presented refresh token was already used or revoked by the time of the write.</summary>
    Spent,
    /// <summary>The presented refresh token expired, unused, by the time of the write: nobody redeemed it.</summary>
    Expired,
}

/// <summary>
/// SQLite-backed OAuth store. Codes and refresh tokens are keyed by the hash callers pass in; this class never sees
/// the raw values. Every write that issues or spends a grant (approve, a code under a standing consent, redeem a code,
/// the first refresh token, a rotation, revoke a family) is one statement or one transaction that checks what it
/// depends on, so parallel requests get exactly one winner and a failure leaves nothing half done.
/// </summary>
public sealed class SqliteOAuthStore
{
    public static readonly TimeSpan FamilyTombstoneRetention = TimeSpan.FromDays(60);

    private readonly string _connectionString;
    private readonly TimeProvider _time;

    public SqliteOAuthStore(string databasePath, TimeProvider time)
    {
        _time = time;
        if (Path.GetDirectoryName(Path.GetFullPath(databasePath)) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        // Default Timeout is also the busy timeout, so parallel connections wait for the write lock instead of failing.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
        CreateSchema();
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private void CreateSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // WAL: readers don't block the writer, and the transactions below (BEGIN IMMEDIATE) serialize writers.
        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS clients (
                client_id TEXT PRIMARY KEY,
                client_name TEXT NOT NULL,
                redirect_uris TEXT NOT NULL,
                client_uri TEXT, logo_uri TEXT, software_id TEXT, software_version TEXT, registered_from TEXT,
                created_at INTEGER NOT NULL,
                last_used_at INTEGER,
                disabled INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS codes (
                code_hash TEXT PRIMARY KEY,
                client_id TEXT NOT NULL, redirect_uri TEXT NOT NULL, code_challenge TEXT NOT NULL,
                scope TEXT NOT NULL, resource TEXT,
                subject TEXT NOT NULL, email TEXT NOT NULL, name TEXT NOT NULL,
                expires_at INTEGER NOT NULL,
                redeemed INTEGER NOT NULL DEFAULT 0,
                family_id TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS refresh_tokens (
                token_hash TEXT PRIMARY KEY,
                client_id TEXT NOT NULL, family_id TEXT NOT NULL, scope TEXT NOT NULL,
                subject TEXT NOT NULL, email TEXT NOT NULL, name TEXT NOT NULL,
                expires_at INTEGER NOT NULL,
                redeemed INTEGER NOT NULL DEFAULT 0,
                revoked INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_refresh_family ON refresh_tokens (family_id);
            CREATE TABLE IF NOT EXISTS revoked_families (
                family_id TEXT PRIMARY KEY,
                revoked_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS consents (
                user_id TEXT NOT NULL, client_id TEXT NOT NULL,
                client_name TEXT NOT NULL, client_uri TEXT, scope TEXT NOT NULL, family_id TEXT NOT NULL,
                last_used_at INTEGER NOT NULL,
                created_at INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (user_id, client_id)
            );
            CREATE TABLE IF NOT EXISTS signing_key (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                key_id TEXT NOT NULL,
                protected_private_key TEXT NOT NULL
            );
            """);
        // A consents table from before created_at existed (a development database): add the column.
        if (!HasColumn(conn, "consents", "created_at"))
            Exec(conn, "ALTER TABLE consents ADD COLUMN created_at INTEGER NOT NULL DEFAULT 0");

        long version;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version";
            version = (long)read.ExecuteScalar()!;
        }
        // Version 1 is the marker later migrations start from: a new database, or one from before the marker that the
        // line above brought up to date. Each step below is safe on a database the CREATE above has just made.
        if (version < 2) MigrateToVersion2(conn);
        if (version < SchemaVersion) Exec(conn, $"PRAGMA user_version = {SchemaVersion}");
    }

    /// <summary>The schema this class creates, kept in SQLite's <c>user_version</c>.</summary>
    public const int SchemaVersion = 2;

    /// <summary>
    /// Version 2: a code records the family of the consent that issued it, and a revoked family's consent is withdrawn
    /// with it. A code from before cannot be traced to its family, so it is expired (it lasted minutes anyway); a consent
    /// whose family has revocation evidence (a tombstone, or a token marked revoked) is removed. Registrations, live
    /// grants and the signing key are left alone. One transaction, so a failure leaves version 1 to try again.
    /// </summary>
    private static void MigrateToVersion2(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        if (!HasColumn(conn, "codes", "family_id", tx))
            Exec(conn, "ALTER TABLE codes ADD COLUMN family_id TEXT NOT NULL DEFAULT ''", tx);
        Exec(conn, "UPDATE codes SET expires_at = 0 WHERE family_id = ''", tx);
        Exec(conn, """
            DELETE FROM consents
            WHERE family_id IN (SELECT family_id FROM revoked_families)
               OR family_id IN (SELECT family_id FROM refresh_tokens WHERE revoked = 1)
            """, tx);
        Exec(conn, "PRAGMA user_version = 2", tx);
        tx.Commit();
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column, SqliteTransaction? tx = null)
    {
        using var info = conn.CreateCommand();
        info.Transaction = tx;
        info.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column";
        info.Parameters.AddWithValue("$column", column);
        return info.ExecuteScalar() is not null;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        // SQLite's own busy handler backs off in 1-100 ms steps; Microsoft.Data.Sqlite's retry loop sleeps 150 ms.
        Exec(conn, "PRAGMA busy_timeout=30000");
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Cmd(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    // ---- client registrations ----

    public async Task<ClientRegistration> CreateRegistrationAsync(ClientRegistration client, CancellationToken ct = default)
    {
        client.CreatedAt = _time.GetUtcNow();
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, """
            INSERT INTO clients (client_id, client_name, redirect_uris, client_uri, logo_uri, software_id, software_version,
                                 registered_from, created_at, last_used_at, disabled)
            VALUES ($id, $name, $uris, $curi, $logo, $sid, $sver, $from, $created, $used, $disabled)
            """,
            ("$id", client.ClientId), ("$name", client.ClientName),
            ("$uris", JsonSerializer.Serialize(client.RedirectUris)),
            ("$curi", client.ClientUri), ("$logo", client.LogoUri),
            ("$sid", client.SoftwareId), ("$sver", client.SoftwareVersion), ("$from", client.RegisteredFrom),
            ("$created", Ms(client.CreatedAt)),
            ("$used", client.LastUsedAt is { } u ? Ms(u) : null),
            ("$disabled", client.Disabled ? 1 : 0));
        await cmd.ExecuteNonQueryAsync(ct);
        return client;
    }

    public async Task<ClientRegistration?> GetRegistrationAsync(string clientId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, """
            SELECT client_id, client_name, redirect_uris, client_uri, logo_uri, software_id, software_version,
                   registered_from, created_at, last_used_at, disabled
            FROM clients WHERE client_id = $id
            """, ("$id", clientId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ClientRegistration
        {
            ClientId = r.GetString(0),
            ClientName = r.GetString(1),
            RedirectUris = JsonSerializer.Deserialize<List<string>>(r.GetString(2)) ?? [],
            ClientUri = r.IsDBNull(3) ? null : r.GetString(3),
            LogoUri = r.IsDBNull(4) ? null : r.GetString(4),
            SoftwareId = r.IsDBNull(5) ? null : r.GetString(5),
            SoftwareVersion = r.IsDBNull(6) ? null : r.GetString(6),
            RegisteredFrom = r.IsDBNull(7) ? null : r.GetString(7),
            CreatedAt = FromMs(r.GetInt64(8)),
            LastUsedAt = r.IsDBNull(9) ? null : FromMs(r.GetInt64(9)),
            Disabled = r.GetInt64(10) != 0,
        };
    }

    public async Task TouchRegistrationAsync(string clientId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "UPDATE clients SET last_used_at = $now WHERE client_id = $id",
            ("$now", Now), ("$id", clientId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- authorization codes ----

    public async Task SaveCodeAsync(AuthorizationCode code, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = InsertCode(conn, code);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static SqliteCommand InsertCode(SqliteConnection conn, AuthorizationCode code, SqliteTransaction? tx = null)
    {
        var cmd = Cmd(conn, """
            INSERT OR REPLACE INTO codes (code_hash, client_id, redirect_uri, code_challenge, scope, resource,
                                          subject, email, name, expires_at, redeemed, family_id)
            VALUES ($hash, $client, $redirect, $challenge, $scope, $resource, $sub, $email, $name, $exp, $redeemed, $family)
            """,
            ("$hash", code.CodeHash), ("$client", code.ClientId), ("$redirect", code.RedirectUri),
            ("$challenge", code.CodeChallenge), ("$scope", code.Scope), ("$resource", code.Resource),
            ("$sub", code.User.Subject), ("$email", code.User.Email), ("$name", code.User.Name),
            ("$exp", Ms(code.ExpiresAt)), ("$redeemed", code.Redeemed ? 1 : 0), ("$family", code.FamilyId));
        cmd.Transaction = tx;
        return cmd;
    }

    /// <summary>
    /// The owner approves a client: their consent (replacing any earlier one for the client) and its first code, in
    /// one transaction. The only way a consent is created.
    /// </summary>
    public async Task ApproveAsync(Consent consent, AuthorizationCode code, CancellationToken ct = default)
    {
        code.FamilyId = consent.FamilyId;
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using (var save = Cmd(conn, """
            INSERT OR REPLACE INTO consents (user_id, client_id, client_name, client_uri, scope, family_id, last_used_at, created_at)
            VALUES ($user, $client, $name, $uri, $scope, $family, $used, $created)
            """,
            ("$user", consent.UserId), ("$client", consent.ClientId), ("$name", consent.ClientName),
            ("$uri", consent.ClientUri), ("$scope", consent.Scope), ("$family", consent.FamilyId),
            ("$used", Ms(consent.LastUsedAt)), ("$created", Ms(consent.CreatedAt))))
        {
            save.Transaction = tx;
            await save.ExecuteNonQueryAsync(ct);
        }
        await using (var insert = InsertCode(conn, code, tx))
            await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// A code under the owner's standing consent for a client, if it stands (exists, and its family is not revoked): the
    /// code is saved into the consent's family and the consent marked used, in one transaction. False, and nothing
    /// written, when there is no such consent.
    /// </summary>
    public async Task<bool> IssueCodeUnderConsentAsync(AuthorizationCode code, string consentKey, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var now = Now;
        await using (var find = Cmd(conn, $"""
            SELECT family_id FROM consents
            WHERE user_id = $user AND client_id = $client AND NOT {FamilyRevokedSql("consents.family_id", "$cutoff")}
            """, ("$user", code.User.Subject), ("$client", consentKey), ("$cutoff", CutoffAt(now))))
        {
            find.Transaction = tx;
            if (await find.ExecuteScalarAsync(ct) is not string family) return false;
            code.FamilyId = family;
        }
        await using (var insert = InsertCode(conn, code, tx))
            await insert.ExecuteNonQueryAsync(ct);
        await TouchConsentAsync(conn, tx, code.User.Subject, consentKey, code.FamilyId, now, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Reads the code and marks it redeemed in one step; null when unknown, expired or already redeemed.</summary>
    public async Task<AuthorizationCode?> RedeemCodeAsync(string codeHash, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        // BEGIN IMMEDIATE takes the write lock up front (waiting on the busy timeout), so the conditional UPDATE
        // ... RETURNING and its commit can't hit SQLITE_BUSY mid-statement. Whichever request flips redeemed 0 -> 1
        // gets the row, the rest get nothing.
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        AuthorizationCode? code = null;
        await using (var cmd = Cmd(conn, """
            UPDATE codes SET redeemed = 1
            WHERE code_hash = $hash AND redeemed = 0 AND expires_at > $now
            RETURNING code_hash, client_id, redirect_uri, code_challenge, scope, resource, subject, email, name, expires_at, family_id
            """, ("$hash", codeHash), ("$now", Now)))
        {
            cmd.Transaction = tx;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                code = new AuthorizationCode
                {
                    CodeHash = r.GetString(0),
                    ClientId = r.GetString(1),
                    RedirectUri = r.GetString(2),
                    CodeChallenge = r.GetString(3),
                    Scope = r.GetString(4),
                    Resource = r.IsDBNull(5) ? null : r.GetString(5),
                    User = new OAuthUser { Subject = r.GetString(6), Email = r.GetString(7), Name = r.GetString(8) },
                    ExpiresAt = FromMs(r.GetInt64(9)),
                    FamilyId = r.GetString(10),
                    Redeemed = true,
                };
                while (await r.ReadAsync(ct)) { }
            }
        }
        await tx.CommitAsync(ct);
        return code;
    }

    // ---- refresh tokens ----

    public async Task SaveRefreshTokenAsync(RefreshToken token, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        // A token saved into a family that already has a tombstone is born revoked.
        await using var cmd = Cmd(conn, $"""
            INSERT OR REPLACE INTO refresh_tokens (token_hash, client_id, family_id, scope, subject, email, name,
                                                   expires_at, redeemed, revoked)
            VALUES ($hash, $client, $family, $scope, $sub, $email, $name, $exp, $redeemed,
                    CASE WHEN $revoked = 1 OR {FamilyRevokedSql("$family", "$cutoff")} THEN 1 ELSE 0 END)
            """,
            ("$hash", token.TokenHash), ("$client", token.ClientId), ("$family", token.FamilyId), ("$scope", token.Scope),
            ("$sub", token.User.Subject), ("$email", token.User.Email), ("$name", token.User.Name),
            ("$exp", Ms(token.ExpiresAt)), ("$redeemed", token.Redeemed ? 1 : 0), ("$revoked", token.Revoked ? 1 : 0),
            ("$cutoff", TombstoneCutoff));
        // Immediate transaction: the tombstone check and the insert can't interleave with RevokeFamilyAsync.
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    private long TombstoneCutoff => CutoffAt(Now);

    private static long CutoffAt(long now) => now - (long)FamilyTombstoneRetention.TotalMilliseconds;

    // Inner columns are table-qualified: an unqualified family_id would bind to revoked_families itself and match any tombstone.
    private static string FamilyRevokedSql(string family, string cutoff) =>
        $"EXISTS (SELECT 1 FROM revoked_families WHERE revoked_families.family_id = {family} AND revoked_families.revoked_at > {cutoff})";

    public async Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, """
            SELECT token_hash, client_id, family_id, scope, subject, email, name, expires_at, redeemed, revoked
            FROM refresh_tokens WHERE token_hash = $hash
            """, ("$hash", tokenHash));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new RefreshToken
        {
            TokenHash = r.GetString(0),
            ClientId = r.GetString(1),
            FamilyId = r.GetString(2),
            Scope = r.GetString(3),
            User = new OAuthUser { Subject = r.GetString(4), Email = r.GetString(5), Name = r.GetString(6) },
            ExpiresAt = FromMs(r.GetInt64(7)),
            Redeemed = r.GetInt64(8) != 0,
            Revoked = r.GetInt64(9) != 0,
        };
    }

    /// <summary>Marks a refresh token redeemed only if it is not already; false means another request got there first (treat as reuse).</summary>
    public async Task<bool> TryMarkRefreshTokenRedeemedAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        bool won;
        // Revoked, expired and tombstoned-family tokens can never be marked redeemed (and so never rotated).
        await using (var cmd = Cmd(conn, $"""
            UPDATE refresh_tokens SET redeemed = 1
            WHERE token_hash = $hash AND redeemed = 0 AND revoked = 0 AND expires_at > $now
              AND NOT {FamilyRevokedSql("refresh_tokens.family_id", "$cutoff")}
            RETURNING 1
            """, ("$hash", tokenHash), ("$now", Now), ("$cutoff", TombstoneCutoff)))
        {
            cmd.Transaction = tx;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            won = await r.ReadAsync(ct);
            while (await r.ReadAsync(ct)) { }
        }
        await tx.CommitAsync(ct);
        return won;
    }

    /// <summary>
    /// The first refresh token of a code's family, saved only if that family is still the owner's standing consent for
    /// the client and is not revoked. The check and the insert are one transaction.
    /// </summary>
    public async Task<GrantWrite> SaveFirstRefreshTokenAsync(RefreshToken token, string consentKey, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var now = Now;
        if (await StandingAsync(conn, tx, token.FamilyId, token.User.Subject, consentKey, now, ct) is var standing and not GrantWrite.Done) return standing;
        await InsertRefreshTokenAsync(conn, tx, token, ct);
        await tx.CommitAsync(ct);
        return GrantWrite.Done;
    }

    /// <summary>
    /// Trades the presented refresh token for <paramref name="successor"/> in one transaction: the family must still be
    /// the owner's standing consent and not revoked, the presented token must still be live and unused, then it is
    /// spent, the successor saved and that consent (and only while it is still that family's) marked used. Any failure
    /// leaves all of it as it was, the presented token included. Only a token that is still there and was already used is
    /// <see cref="GrantWrite.Spent"/> (reuse); one that ran out unused, or was swept away meanwhile, is
    /// <see cref="GrantWrite.Expired"/>: neither is a sign that anyone else holds it.
    /// </summary>
    public async Task<GrantWrite> RotateRefreshTokenAsync(string presentedHash, RefreshToken successor, string consentKey, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var now = Now;
        if (await StandingAsync(conn, tx, successor.FamilyId, successor.User.Subject, consentKey, now, ct) is var standing and not GrantWrite.Done)
            return standing;
        await using (var spend = Cmd(conn, """
            UPDATE refresh_tokens SET redeemed = 1
            WHERE token_hash = $hash AND family_id = $family AND redeemed = 0 AND revoked = 0 AND expires_at > $now
            RETURNING 1
            """, ("$hash", presentedHash), ("$family", successor.FamilyId), ("$now", now)))
        {
            spend.Transaction = tx;
            await using var r = await spend.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return await RedeemedAsync(conn, tx, presentedHash, ct) ? GrantWrite.Spent : GrantWrite.Expired;
            while (await r.ReadAsync(ct)) { }
        }
        await InsertRefreshTokenAsync(conn, tx, successor, ct);
        await TouchConsentAsync(conn, tx, successor.User.Subject, consentKey, successor.FamilyId, now, ct);
        await tx.CommitAsync(ct);
        return GrantWrite.Done;
    }

    /// <summary>Whether the token is still there and was already used: the only case that is reuse.</summary>
    private static async Task<bool> RedeemedAsync(SqliteConnection conn, SqliteTransaction tx, string tokenHash, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, """
            SELECT 1 FROM refresh_tokens WHERE token_hash = $hash AND redeemed = 1
            """, ("$hash", tokenHash));
        cmd.Transaction = tx;
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Whether a token of <paramref name="familyId"/> may be issued now: <see cref="GrantWrite.Done"/>, or why not. The
    /// same check the issuing writes make, for a caller that wants to refuse before doing anything costly; those writes
    /// check again.
    /// </summary>
    public async Task<GrantWrite> GetStandingAsync(string subject, string consentKey, string familyId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await StandingAsync(conn, null, familyId, subject, consentKey, Now, ct);
    }

    /// <summary>Whether a token of this family may be issued now: its family not revoked, and still the owner's consent.</summary>
    private static async Task<GrantWrite> StandingAsync(SqliteConnection conn, SqliteTransaction? tx, string familyId, string subject, string consentKey,
        long now, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, $"""
            SELECT {FamilyRevokedSql("$family", "$cutoff")},
                   EXISTS (SELECT 1 FROM consents WHERE user_id = $user AND client_id = $client AND family_id = $family)
            """, ("$family", familyId), ("$cutoff", CutoffAt(now)), ("$user", subject), ("$client", consentKey));
        cmd.Transaction = tx;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return r.GetInt64(0) != 0 ? GrantWrite.FamilyRevoked : r.GetInt64(1) == 0 ? GrantWrite.ConsentWithdrawn : GrantWrite.Done;
    }

    private static async Task InsertRefreshTokenAsync(SqliteConnection conn, SqliteTransaction tx, RefreshToken token, CancellationToken ct)
    {
        // A plain INSERT: a successor never replaces a row, and StandingAsync has just found its family unrevoked.
        await using var cmd = Cmd(conn, """
            INSERT INTO refresh_tokens (token_hash, client_id, family_id, scope, subject, email, name, expires_at, redeemed, revoked)
            VALUES ($hash, $client, $family, $scope, $sub, $email, $name, $exp, 0, 0)
            """,
            ("$hash", token.TokenHash), ("$client", token.ClientId), ("$family", token.FamilyId), ("$scope", token.Scope),
            ("$sub", token.User.Subject), ("$email", token.User.Email), ("$name", token.User.Name), ("$exp", Ms(token.ExpiresAt)));
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Marks the consent used, only while it is still this family's: a disconnect or a fresh approval meanwhile stands.</summary>
    private static async Task TouchConsentAsync(SqliteConnection conn, SqliteTransaction tx, string userId, string consentKey, string familyId, long now,
        CancellationToken ct)
    {
        await using var cmd = Cmd(conn, "UPDATE consents SET last_used_at = $now WHERE user_id = $user AND client_id = $client AND family_id = $family",
            ("$now", now), ("$user", userId), ("$client", consentKey), ("$family", familyId));
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Revokes every token in the family, withdraws the consent that started it (only that family's, so an old token
    /// never touches a newer consent) and leaves a tombstone so the family stays revoked for 60 days. One transaction.
    /// </summary>
    public async Task RevokeFamilyAsync(string familyId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using (var tomb = Cmd(conn,
            "INSERT OR IGNORE INTO revoked_families (family_id, revoked_at) VALUES ($family, $now)",
            ("$family", familyId), ("$now", Now)))
        {
            tomb.Transaction = tx;
            await tomb.ExecuteNonQueryAsync(ct);
        }
        await using (var revoke = Cmd(conn, "UPDATE refresh_tokens SET revoked = 1 WHERE family_id = $family", ("$family", familyId)))
        {
            revoke.Transaction = tx;
            await revoke.ExecuteNonQueryAsync(ct);
        }
        await using (var withdraw = Cmd(conn, "DELETE FROM consents WHERE family_id = $family", ("$family", familyId)))
        {
            withdraw.Transaction = tx;
            await withdraw.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<bool> IsFamilyRevokedAsync(string familyId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            "SELECT 1 FROM revoked_families WHERE family_id = $family AND revoked_at > $cutoff",
            ("$family", familyId), ("$cutoff", Now - (long)FamilyTombstoneRetention.TotalMilliseconds));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // ---- consents ----

    /// <summary>Writes the row as given. The ledger never calls this: a consent is created only by <see cref="ApproveAsync"/>.</summary>
    public async Task UpsertConsentAsync(Consent consent, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, """
            INSERT OR REPLACE INTO consents (user_id, client_id, client_name, client_uri, scope, family_id, last_used_at, created_at)
            VALUES ($user, $client, $name, $uri, $scope, $family, $used, $created)
            """,
            ("$user", consent.UserId), ("$client", consent.ClientId), ("$name", consent.ClientName),
            ("$uri", consent.ClientUri), ("$scope", consent.Scope), ("$family", consent.FamilyId),
            ("$used", Ms(consent.LastUsedAt)), ("$created", Ms(consent.CreatedAt)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string ConsentColumns = "user_id, client_id, client_name, client_uri, scope, family_id, last_used_at, created_at";

    private static Consent ReadConsent(SqliteDataReader r) => new()
    {
        UserId = r.GetString(0),
        ClientId = r.GetString(1),
        ClientName = r.GetString(2),
        ClientUri = r.IsDBNull(3) ? null : r.GetString(3),
        Scope = r.GetString(4),
        FamilyId = r.GetString(5),
        LastUsedAt = FromMs(r.GetInt64(6)),
        CreatedAt = FromMs(r.GetInt64(7)),
    };

    public async Task<Consent?> GetConsentAsync(string userId, string clientId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, $"SELECT {ConsentColumns} FROM consents WHERE user_id = $user AND client_id = $client",
            ("$user", userId), ("$client", clientId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadConsent(r) : null;
    }

    public async Task<IReadOnlyList<Consent>> ListConsentsAsync(string userId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, $"SELECT {ConsentColumns} FROM consents WHERE user_id = $user ORDER BY last_used_at DESC",
            ("$user", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Consent>();
        while (await r.ReadAsync(ct)) list.Add(ReadConsent(r));
        return list;
    }

    public async Task DeleteConsentAsync(string userId, string clientId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "DELETE FROM consents WHERE user_id = $user AND client_id = $client",
            ("$user", userId), ("$client", clientId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- signing key ----

    /// <summary>The wrapped signing key, or null when none has been generated yet.</summary>
    public async Task<SigningKey?> GetSigningKeyAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await ReadSigningKeyAsync(conn, ct);
    }

    private static async Task<SigningKey?> ReadSigningKeyAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, "SELECT key_id, protected_private_key FROM signing_key WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new SigningKey { KeyId = r.GetString(0), ProtectedPrivateKey = r.GetString(1) } : null;
    }

    /// <summary>Stores the key unless one already exists; returns whichever is now stored (first writer wins).</summary>
    public async Task<SigningKey> SaveSigningKeyIfAbsentAsync(SigningKey key, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using (var cmd = Cmd(conn,
            "INSERT OR IGNORE INTO signing_key (id, key_id, protected_private_key) VALUES (1, $kid, $key)",
            ("$kid", key.KeyId), ("$key", key.ProtectedPrivateKey)))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return (await ReadSigningKeyAsync(conn, ct))!;
    }

    // ---- housekeeping ----

    /// <summary>Removes expired codes, expired refresh tokens and family tombstones older than 60 days; returns the rows removed.</summary>
    public async Task<int> SweepExpiredAsync(CancellationToken ct = default)
    {
        var now = Now;
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, """
            DELETE FROM codes WHERE expires_at <= $now;
            DELETE FROM refresh_tokens WHERE expires_at <= $now;
            DELETE FROM revoked_families WHERE revoked_at <= $cutoff;
            """, ("$now", now), ("$cutoff", now - (long)FamilyTombstoneRetention.TotalMilliseconds));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
