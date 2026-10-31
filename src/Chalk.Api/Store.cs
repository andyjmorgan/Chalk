using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Npgsql;
using NpgsqlTypes;

namespace Chalk;

public sealed class Store(NpgsqlDataSource db, ILogger<Store> log)
{
    private static readonly TimeSpan EventRetentionFallback = TimeSpan.FromDays(30);
    private static readonly TimeSpan TombstoneRetention = TimeSpan.FromHours(24);

    // ---------- bootstrap ----------

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var s = typeof(Store).Assembly.GetManifestResourceStream("Chalk.Db.schema.sql")!;
        var sql = await new StreamReader(s).ReadToEndAsync(ct);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
        // Reload type info so the vector type registered by CREATE EXTENSION is known.
        await conn.ReloadTypesAsync(ct);
    }

    public async Task BootstrapKeyAsync(string user, string key, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var count = (long)(await new NpgsqlCommand("SELECT count(*) FROM api_keys", conn).ExecuteScalarAsync(ct))!;
        if (count > 0) return;
        await EnsureUserAsync(conn, user, ct);
        await InsertKeyAsync(conn, user, "bootstrap", key, new Dictionary<string, object?> { ["read"] = true, ["write"] = true, ["delete"] = true, ["admin"] = true, ["boards"] = null }, ct);
        log.LogWarning("Bootstrapped admin API key for user {User} from configuration", user);
    }

    private static async Task EnsureUserAsync(NpgsqlConnection conn, string user, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO users(id, display) VALUES (@u, @u) ON CONFLICT (id) DO NOTHING", conn);
        cmd.Parameters.AddWithValue("u", user);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Record who a signed-in user is (display/email) and when they were last here. Called from /me.</summary>
    public async Task UpsertUserAsync(Principal p, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("INSERT INTO users(id, display, email, last_seen_at) VALUES (@u, @d, @e, now()) ON CONFLICT (id) DO UPDATE SET display = EXCLUDED.display, email = COALESCE(EXCLUDED.email, users.email), last_seen_at = now()", conn);
        cmd.Parameters.AddWithValue("u", p.UserId);
        cmd.Parameters.AddWithValue("d", p.Name ?? p.Display);
        cmd.Parameters.AddWithValue("e", (object?)p.Email ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertKeyAsync(NpgsqlConnection conn, string user, string name, string key, Dictionary<string, object?> scopes, CancellationToken ct, DateTimeOffset? expiresAt = null)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO api_keys(id, user_id, name, prefix, hash, scopes, expires_at) VALUES (@id, @u, @n, @p, @h, @s, @x)", conn);
        cmd.Parameters.AddWithValue("x", (object?)expiresAt?.UtcDateTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("u", user);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("p", key.Length > 12 ? key[..12] : key);
        cmd.Parameters.AddWithValue("h", Hash(key));
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(scopes) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- auth & keys ----------

    public async Task<Principal?> AuthenticateAsync(string key, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE api_keys SET last_used_at = now() WHERE hash = @h AND enabled AND (expires_at IS NULL OR expires_at > now()) RETURNING user_id, name, scopes", conn);
        cmd.Parameters.AddWithValue("h", Hash(key));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var user = r.GetString(0);
        var name = r.GetString(1);
        var scopes = JsonNode.Parse(r.GetString(2))!.AsObject();
        string[]? boards = scopes["boards"] is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToArray() : null;
        return new Principal(user, $"key:{name}",
            scopes["read"]?.GetValue<bool>() ?? false, scopes["write"]?.GetValue<bool>() ?? false,
            scopes["delete"]?.GetValue<bool>() ?? false, scopes["admin"]?.GetValue<bool>() ?? false, boards);
    }

    public async Task<object> CreateKeyAsync(Principal p, string name, bool read, bool write, bool delete, bool admin, string[]? boards, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        p.RequireKeys();
        if (string.IsNullOrWhiteSpace(name)) throw ChalkException.BadRequest("name is required");
        if (expiresAt is not null && expiresAt <= DateTimeOffset.UtcNow) throw ChalkException.BadRequest("expiresAt must be in the future");
        if (admin && !p.Admin && !p.IsUser) throw ChalkException.Forbidden("only a signed-in user or an admin key may mint an admin key");
        var key = "chk_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        await using var conn = await db.OpenConnectionAsync(ct);
        await EnsureUserAsync(conn, p.UserId, ct);
        try
        {
            await InsertKeyAsync(conn, p.UserId, name.Trim(), key, new Dictionary<string, object?> { ["read"] = read, ["write"] = write, ["delete"] = delete, ["admin"] = admin, ["boards"] = boards }, ct, expiresAt);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw ChalkException.Conflict($"a key named '{name}' already exists");
        }
        await AuditAsync(conn, p.Display, "key.create", new { name, read, write, delete, admin, boards, expiresAt }, ct);
        return new { name = name.Trim(), key, prefix = key[..12], scopes = new { read, write, delete, admin, boards }, expiresAt };
    }

    public async Task<List<object>> ListKeysAsync(Principal p, CancellationToken ct)
    {
        p.RequireKeys();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, name, prefix, scopes, enabled, created_at, last_used_at, expires_at FROM api_keys WHERE user_id = @u ORDER BY created_at", conn);
        cmd.Parameters.AddWithValue("u", p.UserId);
        var list = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new { id = r.GetGuid(0), name = r.GetString(1), prefix = r.GetString(2), scopes = JsonNode.Parse(r.GetString(3)), enabled = r.GetBoolean(4), createdAt = r.GetFieldValue<DateTime>(5), lastUsedAt = r.IsDBNull(6) ? null : (DateTime?)r.GetFieldValue<DateTime>(6), expiresAt = r.IsDBNull(7) ? null : (DateTime?)r.GetFieldValue<DateTime>(7), expired = !r.IsDBNull(7) && r.GetFieldValue<DateTime>(7) <= DateTime.UtcNow });
        return list;
    }

    public async Task RevokeKeyAsync(Principal p, Guid id, CancellationToken ct)
    {
        p.RequireKeys();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE api_keys SET enabled = false WHERE id = @id AND user_id = @u", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("u", p.UserId);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw ChalkException.NotFound("no such key");
        await AuditAsync(conn, p.Display, "key.revoke", new { id }, ct);
    }

    private static async Task AuditAsync(NpgsqlConnection conn, string principal, string action, object detail, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO audit(principal, action, detail) VALUES (@p, @a, @d)", conn);
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(detail, Json.Options) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- boards ----------

    private static BoardDef ReadBoard(NpgsqlDataReader r)
    {
        var search = JsonNode.Parse(r.GetString(8))?.AsObject() ?? new JsonObject();
        var embedJson = r.IsDBNull(9) ? null : JsonNode.Parse(r.GetString(9))?.AsObject();
        return new BoardDef(
            r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            JsonNode.Parse(r.GetString(5))!.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
            r.IsDBNull(6) ? null : r.GetFieldValue<TimeSpan>(6), r.GetInt32(7),
            new SearchConfig(search["fields"] is JsonArray sf ? sf.Select(x => x!.GetValue<string>()).ToArray() : []),
            embedJson is null ? null : new EmbedConfig(
                embedJson["fields"] is JsonArray ef ? ef.Select(x => x!.GetValue<string>()).ToArray() : [],
                embedJson["model"]?.GetValue<string>() ?? "", embedJson["dims"]?.GetValue<int>() ?? 512),
            r.GetInt32(10), r.GetInt64(11), r.GetFieldValue<DateTime>(12), r.GetFieldValue<DateTime>(13));
    }

    private const string BoardColumns = "b.id, b.owner_id, b.name, b.description, b.schema::text, b.key_pointers::text, b.ttl, b.max_revisions, b.search::text, b.embed::text, b.max_value_bytes, b.size_cap, b.created_at, b.updated_at";

    public async Task<BoardDef> GetBoardAsync(Principal p, string name, CancellationToken ct)
    {
        p.RequireRead();
        if (!p.CanBoard(name)) throw ChalkException.Forbidden($"this key is not scoped to board {name}");
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetBoardAsync(conn, p, name, ct);
    }

    private static async Task<BoardDef> GetBoardAsync(NpgsqlConnection conn, Principal p, string name, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {BoardColumns} FROM boards b LEFT JOIN shares s ON s.board_id = b.id AND s.user_id = @u WHERE b.name = @n AND (b.owner_id = @u OR s.user_id IS NOT NULL) LIMIT 1", conn);
        cmd.Parameters.AddWithValue("u", p.UserId);
        cmd.Parameters.AddWithValue("n", name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw ChalkException.NotFound($"no board named {name}");
        return ReadBoard(r);
    }

    public async Task<BoardDef> CreateBoardAsync(Principal p, string name, string? description, JsonNode? schema, string[] keyPointers, string? ttl, int? maxRevisions, string[]? searchFields, JsonNode? embed, int? maxValueBytes, CancellationToken ct)
    {
        p.RequireWrite();
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-' || c == '_'))
            throw ChalkException.BadRequest("board name must be a slug: lowercase letters, digits, - and _");
        if (!p.CanBoard(name)) throw ChalkException.Forbidden($"this key is not scoped to board {name}");
        if (keyPointers is null || keyPointers.Length == 0) throw ChalkException.BadRequest("key must list at least one JSON pointer");
        foreach (var kp in keyPointers) if (!kp.StartsWith('/')) throw ChalkException.BadRequest($"key pointer '{kp}' must start with /");
        var schemaNode = schema ?? JsonNode.Parse("""{"type":"object"}""")!;
        try { JsonSchema.FromText(schemaNode.ToJsonString()); }
        catch (Exception e) { throw ChalkException.BadRequest($"schema is not valid JSON Schema: {e.Message}"); }
        var ttlSpan = ttl is null ? (TimeSpan?)null : Durations.Parse(ttl, "ttl");
        EmbedConfig? embedCfg = null;
        if (embed is JsonObject eo)
        {
            var fields = eo["fields"] is JsonArray fa ? fa.Select(x => x!.GetValue<string>()).ToArray() : [];
            if (fields.Length == 0) throw ChalkException.BadRequest("embed.fields must list at least one pointer");
            embedCfg = new EmbedConfig(fields, eo["model"]?.GetValue<string>() ?? Embedder.DefaultModel, 512);
        }
        var mvb = maxValueBytes ?? 65536;
        if (mvb < 1024 || mvb > 1048576) throw ChalkException.BadRequest("maxValueBytes must be between 1 KB and 1 MB");

        await using var conn = await db.OpenConnectionAsync(ct);
        await EnsureUserAsync(conn, p.UserId, ct);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO boards(id, owner_id, name, description, schema, key_pointers, ttl, max_revisions, search, embed, max_value_bytes)
            VALUES (@id, @u, @n, @d, @s, @k, @ttl, @mr, @search, @embed, @mvb)
            ON CONFLICT (owner_id, name) DO NOTHING
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("u", p.UserId);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("d", description ?? "");
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = schemaNode.ToJsonString() });
            cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(keyPointers) });
            cmd.Parameters.Add(new NpgsqlParameter("ttl", NpgsqlDbType.Interval) { Value = ttlSpan.HasValue ? ttlSpan.Value : DBNull.Value });
            cmd.Parameters.AddWithValue("mr", maxRevisions ?? 50);
            cmd.Parameters.Add(new NpgsqlParameter("search", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { fields = searchFields ?? [] }) });
            cmd.Parameters.Add(new NpgsqlParameter("embed", NpgsqlDbType.Jsonb) { Value = embedCfg is null ? DBNull.Value : JsonSerializer.Serialize(new { fields = embedCfg.Fields, model = embedCfg.Model, dims = embedCfg.Dims }) });
            cmd.Parameters.AddWithValue("mvb", mvb);
            if (await cmd.ExecuteNonQueryAsync(ct) == 1) await AuditAsync(conn, p.Display, "board.create", new { name }, ct);
        }
        return await GetBoardAsync(conn, p, name, ct);
    }

    public async Task<List<object>> ListBoardsAsync(Principal p, CancellationToken ct)
    {
        p.RequireRead();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT {BoardColumns},
              (SELECT count(*) FROM records r WHERE r.board_id = b.id AND r.current AND NOT r.tombstone AND (r.expires_at IS NULL OR r.expires_at > now())) AS live,
              (SELECT count(*) FROM records r WHERE r.board_id = b.id AND r.current AND NOT r.tombstone AND r.written_at >= date_trunc('day', now())) AS writes_today,
              (SELECT count(*) FROM events e WHERE e.board_id = b.id AND e.at >= date_trunc('day', now()) AND e.kind IN ('created','changed')) AS changed_today,
              (SELECT count(*) FROM records r WHERE r.board_id = b.id AND r.current AND NOT r.tombstone AND r.expires_at > now() AND r.expires_at < now() + interval '24 hours') AS expiring,
              (SELECT max(r.written_at) FROM records r WHERE r.board_id = b.id) AS last_write,
              (SELECT r.writer FROM records r WHERE r.board_id = b.id ORDER BY r.written_at DESC LIMIT 1) AS last_writer
            FROM boards b LEFT JOIN shares s ON s.board_id = b.id AND s.user_id = @u
            WHERE b.owner_id = @u OR s.user_id IS NOT NULL
            ORDER BY b.name
            """, conn);
        cmd.Parameters.AddWithValue("u", p.UserId);
        var list = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var b = ReadBoard(r);
            if (!p.CanBoard(b.Name)) continue;
            DateTime? lastWrite = r.IsDBNull(18) ? null : r.GetFieldValue<DateTime>(18);
            var stale = b.Ttl.HasValue && (lastWrite is null || DateTime.UtcNow - lastWrite.Value > b.Ttl.Value);
            list.Add(new
            {
                name = b.Name, description = b.Description, key = b.KeyPointers, ttl = b.Ttl is null ? null : Durations.Format(b.Ttl.Value),
                counts = new { live = r.GetInt64(14), writesToday = r.GetInt64(15), changedToday = r.GetInt64(16), expiring24h = r.GetInt64(17) },
                lastWrite = lastWrite is null ? null : new { at = lastWrite, writer = r.IsDBNull(19) ? null : r.GetString(19) },
                stale,
                search = b.Search.Fields.Length == 0 ? null : new { fields = b.Search.Fields },
                embed = b.Embed is null ? null : new { fields = b.Embed.Fields, model = b.Embed.Model, dims = b.Embed.Dims },
            });
        }
        return list;
    }

    public async Task<object> DescribeBoardAsync(Principal p, string name, CancellationToken ct)
    {
        var b = await GetBoardAsync(p, name, ct);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT
              count(*) FILTER (WHERE current AND NOT tombstone AND (expires_at IS NULL OR expires_at > now())),
              count(*),
              count(*) FILTER (WHERE current AND tombstone),
              coalesce(sum(pg_column_size(value)), 0)
            FROM records WHERE board_id = @b
            """, conn);
        cmd.Parameters.AddWithValue("b", b.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new
        {
            name = b.Name, owner = $"user:{b.OwnerId}", description = b.Description, key = b.KeyPointers, segments = b.SegmentNames,
            ttl = b.Ttl is null ? null : Durations.Format(b.Ttl.Value), maxRevisions = b.MaxRevisions,
            schema = JsonNode.Parse(b.SchemaJson),
            search = new { fields = b.Search.Fields },
            embed = b.Embed is null ? null : new { fields = b.Embed.Fields, model = b.Embed.Model, dims = b.Embed.Dims },
            counts = new { live = r.GetInt64(0), revisions = r.GetInt64(1), tombstoned = r.GetInt64(2), bytes = r.GetInt64(3) },
            maxValueBytes = b.MaxValueBytes, sizeCap = b.SizeCap, shares = Array.Empty<object>(),
            createdAt = b.CreatedAt, updatedAt = b.UpdatedAt,
        };
    }

    // ---------- upsert ----------

    public async Task<List<UpsertResult>> UpsertAsync(Principal p, BoardDef b, IReadOnlyList<UpsertItem> items, Embedder embedder, CancellationToken ct)
    {
        p.RequireWrite();
        if (items.Count == 0) throw ChalkException.BadRequest("records must contain at least one item");
        if (items.Count > 500) throw ChalkException.BadRequest("at most 500 records per call");
        var schema = JsonSchema.FromText(b.SchemaJson);
        var opts = new EvaluationOptions { OutputFormat = OutputFormat.List };
        var keyed = new List<(string Key, UpsertItem Item, string SearchA)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var bytes = Encoding.UTF8.GetByteCount(it.Value.ToJsonString());
            if (bytes > b.MaxValueBytes) throw new ChalkException(413, "value_too_large", $"record {i} is {bytes} bytes; board allows {b.MaxValueBytes}", null, i);
            var res = schema.Evaluate(JsonSerializer.SerializeToElement(it.Value), opts);
            if (!res.IsValid)
            {
                var (msg, where) = FirstError(res);
                throw new ChalkException(400, "schema_violation", $"record {i}: {msg}", where, i);
            }
            keyed.Add((KeyPath.Derive(it.Value, b.KeyPointers, i), it, BuildSearchA(it.Value, b.Search.Fields)));
        }

        var results = new List<UpsertResult>(items.Count);
        var toEmbed = new List<(string Key, int Revision, JsonNode Value)>();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cap = new NpgsqlCommand("SELECT coalesce(sum(pg_column_size(value)), 0) FROM records WHERE board_id = @b", conn, tx))
        {
            cap.Parameters.AddWithValue("b", b.Id);
            var used = (long)(await cap.ExecuteScalarAsync(ct))!;
            if (used > b.SizeCap) throw new ChalkException(413, "board_full", $"board {b.Name} holds {used} bytes, cap is {b.SizeCap}");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (key, it, searchA) in keyed)
        {
            DateTimeOffset? expires = it.ExpiresAt ?? (it.Ttl.HasValue ? now + it.Ttl.Value : b.Ttl.HasValue ? now + b.Ttl.Value : null);
            var observed = it.ObservedAt ?? now;

            int? prevRev = null; JsonNode? prevValue = null; var prevTomb = false;
            await using (var cur = new NpgsqlCommand("SELECT revision, value::text, tombstone FROM records WHERE board_id = @b AND key = @k AND current FOR UPDATE", conn, tx))
            {
                cur.Parameters.AddWithValue("b", b.Id);
                cur.Parameters.AddWithValue("k", key);
                await using var r = await cur.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { prevRev = r.GetInt32(0); prevValue = JsonNode.Parse(r.GetString(1)); prevTomb = r.GetBoolean(2); }
            }

            if (prevRev.HasValue && !prevTomb && JsonNode.DeepEquals(prevValue, it.Value))
            {
                // Same value: touch. Refresh expiry and observedAt; no revision, no event.
                await using var touch = new NpgsqlCommand("UPDATE records SET observed_at = @o, written_at = now(), expires_at = @e, writer = @w WHERE board_id = @b AND key = @k AND current", conn, tx);
                touch.Parameters.AddWithValue("b", b.Id);
                touch.Parameters.AddWithValue("k", key);
                touch.Parameters.AddWithValue("o", observed.UtcDateTime);
                touch.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.TimestampTz) { Value = expires.HasValue ? expires.Value.UtcDateTime : DBNull.Value });
                touch.Parameters.AddWithValue("w", p.Display);
                await touch.ExecuteNonQueryAsync(ct);
                results.Add(new UpsertResult(key, prevRev.Value, expires, "touched"));
                continue;
            }

            var rev = (prevRev ?? 0) + 1;
            if (prevRev.HasValue)
            {
                await using var demote = new NpgsqlCommand("UPDATE records SET current = false WHERE board_id = @b AND key = @k AND current", conn, tx);
                demote.Parameters.AddWithValue("b", b.Id);
                demote.Parameters.AddWithValue("k", key);
                await demote.ExecuteNonQueryAsync(ct);
            }
            await using (var ins = new NpgsqlCommand("INSERT INTO records(board_id, key, revision, value, observed_at, written_at, expires_at, writer, tombstone, current, search_a) VALUES (@b, @k, @r, @v, @o, now(), @e, @w, false, true, @sa)", conn, tx))
            {
                ins.Parameters.AddWithValue("b", b.Id);
                ins.Parameters.AddWithValue("k", key);
                ins.Parameters.AddWithValue("r", rev);
                ins.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = it.Value.ToJsonString() });
                ins.Parameters.AddWithValue("o", observed.UtcDateTime);
                ins.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.TimestampTz) { Value = expires.HasValue ? expires.Value.UtcDateTime : DBNull.Value });
                ins.Parameters.AddWithValue("w", p.Display);
                ins.Parameters.AddWithValue("sa", searchA);
                await ins.ExecuteNonQueryAsync(ct);
            }
            var kind = prevRev.HasValue && !prevTomb ? "changed" : "created";
            await InsertEventAsync(conn, tx, b, key, kind, prevTomb ? null : prevRev, rev, prevTomb ? null : prevValue, it.Value, p.Display, ct);
            await using (var trim = new NpgsqlCommand("DELETE FROM records WHERE board_id = @b AND key = @k AND NOT current AND revision <= @r - @keep", conn, tx))
            {
                trim.Parameters.AddWithValue("b", b.Id);
                trim.Parameters.AddWithValue("k", key);
                trim.Parameters.AddWithValue("r", rev);
                trim.Parameters.AddWithValue("keep", b.MaxRevisions);
                await trim.ExecuteNonQueryAsync(ct);
            }
            results.Add(new UpsertResult(key, rev, expires, kind));
            if (b.Embed is not null) toEmbed.Add((key, rev, it.Value));
        }
        await tx.CommitAsync(ct);

        if (toEmbed.Count > 0) await EmbedBestEffortAsync(conn, b, toEmbed, embedder, ct);
        return results;
    }

    private static (string Message, string? Location) FirstError(EvaluationResults res)
    {
        if (res.Errors is { Count: > 0 }) return (res.Errors.Values.First(), res.InstanceLocation.ToString());
        foreach (var d in res.Details)
        {
            var (m, w) = FirstError(d);
            if (m != "schema violation") return (m, w);
        }
        return ("schema violation", null);
    }

    private static string BuildSearchA(JsonNode value, string[] fields)
    {
        if (fields.Length == 0) return "";
        var parts = new List<string>();
        foreach (var f in fields)
        {
            var n = KeyPath.Resolve(value, f);
            if (n is JsonValue v) parts.Add(v.TryGetValue<string>(out var s) ? s : v.ToJsonString());
            else if (n is not null) parts.Add(n.ToJsonString());
        }
        return string.Join(' ', parts);
    }

    private static async Task InsertEventAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, BoardDef b, string key, string kind, int? fromRev, int? toRev, JsonNode? fromValue, JsonNode? toValue, string writer, CancellationToken ct)
    {
        await using var ev = new NpgsqlCommand("INSERT INTO events(board_id, key, kind, at, from_revision, to_revision, from_value, to_value, writer, expires_at) VALUES (@b, @k, @kind, now(), @fr, @tr, @fv, @tv, @w, now() + @ret)", conn, tx);
        ev.Parameters.AddWithValue("b", b.Id);
        ev.Parameters.AddWithValue("k", key);
        ev.Parameters.AddWithValue("kind", kind);
        ev.Parameters.Add(new NpgsqlParameter("fr", NpgsqlDbType.Integer) { Value = fromRev.HasValue ? fromRev.Value : DBNull.Value });
        ev.Parameters.Add(new NpgsqlParameter("tr", NpgsqlDbType.Integer) { Value = toRev.HasValue ? toRev.Value : DBNull.Value });
        ev.Parameters.Add(new NpgsqlParameter("fv", NpgsqlDbType.Jsonb) { Value = fromValue is null ? DBNull.Value : fromValue.ToJsonString() });
        ev.Parameters.Add(new NpgsqlParameter("tv", NpgsqlDbType.Jsonb) { Value = toValue is null ? DBNull.Value : toValue.ToJsonString() });
        ev.Parameters.AddWithValue("w", writer);
        ev.Parameters.Add(new NpgsqlParameter("ret", NpgsqlDbType.Interval) { Value = b.Ttl ?? EventRetentionFallback });
        await ev.ExecuteNonQueryAsync(ct);
    }

    private async Task EmbedBestEffortAsync(NpgsqlConnection conn, BoardDef b, List<(string Key, int Revision, JsonNode Value)> items, Embedder embedder, CancellationToken ct)
    {
        try
        {
            var texts = items.Select(i => EmbedText.Build(i.Value, b.Embed!.Fields)).ToList();
            var vectors = await embedder.EmbedAsync(b.Embed!.Model, texts.Select(t => t.Text).ToList(), b.Embed.Dims, ct);
            for (var i = 0; i < items.Count; i++)
            {
                await using var cmd = new NpgsqlCommand("INSERT INTO embeddings(board_id, key, revision, model, dims, truncated, vector) VALUES (@b, @k, @r, @m, @d, @t, @v::vector) ON CONFLICT (board_id, key, revision) DO UPDATE SET vector = EXCLUDED.vector, model = EXCLUDED.model", conn);
                cmd.Parameters.AddWithValue("b", b.Id);
                cmd.Parameters.AddWithValue("k", items[i].Key);
                cmd.Parameters.AddWithValue("r", items[i].Revision);
                cmd.Parameters.AddWithValue("m", b.Embed.Model);
                cmd.Parameters.AddWithValue("d", b.Embed.Dims);
                cmd.Parameters.AddWithValue("t", texts[i].Truncated);
                cmd.Parameters.AddWithValue("v", Embedder.ToLiteral(vectors[i]));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Embedding {Count} records in board {Board} failed; records are stored, vectors skipped", items.Count, b.Name);
        }
    }

    // ---------- reads ----------

    private const string LiveWhere = "r.board_id = @b AND r.current AND NOT r.tombstone AND (r.expires_at IS NULL OR r.expires_at > now())";

    private static RecordRow ReadRecord(NpgsqlDataReader r, BoardDef b, int offset = 0) => new(
        r.GetString(offset), JsonNode.Parse(r.GetString(offset + 1))!, r.GetInt32(offset + 2),
        r.GetFieldValue<DateTime>(offset + 3), r.GetFieldValue<DateTime>(offset + 4),
        r.IsDBNull(offset + 5) ? null : r.GetFieldValue<DateTime>(offset + 5), r.GetString(offset + 6))
    { Segments = KeyPath.Segments(r.GetString(offset), b) };

    private const string RecordCols = "r.key, r.value::text, r.revision, r.observed_at, r.written_at, r.expires_at, r.writer";

    public async Task<Dictionary<string, RecordRow>> GetManyAsync(Principal p, BoardDef b, string[] keys, CancellationToken ct)
    {
        p.RequireRead();
        if (keys.Length > 1000) throw ChalkException.BadRequest("at most 1000 keys per call");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {RecordCols} FROM records r WHERE {LiveWhere} AND r.key = ANY(@keys)", conn);
        cmd.Parameters.AddWithValue("b", b.Id);
        cmd.Parameters.AddWithValue("keys", keys);
        var d = new Dictionary<string, RecordRow>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { var row = ReadRecord(r, b); d[row.Key] = row; }
        return d;
    }

    public sealed record ListQuery(string? Prefix, string[]? Filter, DateTimeOffset? Since, int Limit, string? Cursor, TimeSpan? ExpiringWithin, string? Sort);

    private static (string Sql, List<NpgsqlParameter> Params) BuildFilters(BoardDef b, string? prefix, string[]? filter, DateTimeOffset? since, TimeSpan? expiringWithin)
    {
        var sb = new StringBuilder();
        var ps = new List<NpgsqlParameter>();
        if (!string.IsNullOrEmpty(prefix))
        {
            sb.Append(" AND r.key LIKE @prefix ESCAPE '\\'");
            ps.Add(new NpgsqlParameter("prefix", KeyPath.EscapeLike(prefix) + "%"));
        }
        if (since.HasValue)
        {
            sb.Append(" AND r.observed_at >= @since");
            ps.Add(new NpgsqlParameter("since", since.Value.UtcDateTime));
        }
        if (expiringWithin.HasValue)
        {
            sb.Append(" AND r.expires_at IS NOT NULL AND r.expires_at < now() + @within");
            ps.Add(new NpgsqlParameter("within", NpgsqlDbType.Interval) { Value = expiringWithin.Value });
        }
        if (filter is { Length: > 0 })
        {
            for (var i = 0; i < filter.Length; i++)
            {
                var eq = filter[i].IndexOf('=');
                if (eq <= 0 || filter[i][0] != '/') throw ChalkException.BadRequest($"filter '{filter[i]}' must look like /pointer=value");
                sb.Append($" AND r.value #>> @fp{i} = @fv{i}");
                ps.Add(new NpgsqlParameter($"fp{i}", KeyPath.PointerTokens(filter[i][..eq])));
                ps.Add(new NpgsqlParameter($"fv{i}", filter[i][(eq + 1)..]));
            }
        }
        return (sb.ToString(), ps);
    }

    public async Task<(List<RecordRow> Rows, string? NextCursor, long Total, Dictionary<string, Dictionary<string, long>> Facets)> ListAsync(Principal p, BoardDef b, ListQuery q, CancellationToken ct)
    {
        p.RequireRead();
        var limit = Math.Clamp(q.Limit <= 0 ? 50 : q.Limit, 1, 500);
        var (fsql, fps) = BuildFilters(b, q.Prefix, q.Filter, q.Since, q.ExpiringWithin);
        var byExpiry = string.Equals(q.Sort, "expiresAt", StringComparison.OrdinalIgnoreCase);
        await using var conn = await db.OpenConnectionAsync(ct);

        var rows = new List<RecordRow>();
        await using (var cmd = new NpgsqlCommand($"SELECT {RecordCols} FROM records r WHERE {LiveWhere}{fsql}" +
            (q.Cursor is not null && !byExpiry ? " AND r.key > @cursor" : "") +
            (byExpiry ? " ORDER BY r.expires_at ASC NULLS LAST, r.key" : " ORDER BY r.key") + " LIMIT @lim", conn))
        {
            cmd.Parameters.AddWithValue("b", b.Id);
            foreach (var pr in fps) cmd.Parameters.Add(pr.Clone());
            if (q.Cursor is not null && !byExpiry) cmd.Parameters.AddWithValue("cursor", Encoding.UTF8.GetString(Convert.FromBase64String(q.Cursor)));
            cmd.Parameters.AddWithValue("lim", limit + 1);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(ReadRecord(r, b));
        }
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            if (!byExpiry) next = Convert.ToBase64String(Encoding.UTF8.GetBytes(rows[^1].Key));
        }

        long total;
        await using (var cmd = new NpgsqlCommand($"SELECT count(*) FROM records r WHERE {LiveWhere}{fsql}", conn))
        {
            cmd.Parameters.AddWithValue("b", b.Id);
            foreach (var pr in fps) cmd.Parameters.Add(pr.Clone());
            total = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        var facets = new Dictionary<string, Dictionary<string, long>>();
        var facetFields = EnumFields(b).Select(f => ("/" + f, $"r.value ->> '{f.Replace("'", "''")}'")).Append(("writer", "r.writer"));
        foreach (var (name, expr) in facetFields)
        {
            await using var cmd = new NpgsqlCommand($"SELECT {expr} AS f, count(*) FROM records r WHERE {LiveWhere}{fsql} GROUP BY 1 ORDER BY 2 DESC LIMIT 20", conn);
            cmd.Parameters.AddWithValue("b", b.Id);
            foreach (var pr in fps) cmd.Parameters.Add(pr.Clone());
            var d = new Dictionary<string, long>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) if (!r.IsDBNull(0)) d[r.GetString(0)] = r.GetInt64(1);
            if (d.Count > 0) facets[name] = d;
        }
        return (rows, next, total, facets);
    }

    private static IEnumerable<string> EnumFields(BoardDef b)
    {
        var schema = JsonNode.Parse(b.SchemaJson) as JsonObject;
        if (schema?["properties"] is not JsonObject props) yield break;
        foreach (var (name, def) in props)
            if (def is JsonObject o && o["enum"] is JsonArray) yield return name;
    }

    public async Task<List<RevisionRow>> HistoryAsync(Principal p, BoardDef b, string key, int limit, CancellationToken ct)
    {
        p.RequireRead();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT revision, value::text, observed_at, written_at, expires_at, writer, tombstone FROM records WHERE board_id = @b AND key = @k ORDER BY revision DESC LIMIT @lim", conn);
        cmd.Parameters.AddWithValue("b", b.Id);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit <= 0 ? 50 : limit, 1, 500) + 1);
        var raw = new List<(int Rev, JsonNode Value, DateTime Obs, DateTime Wr, DateTime? Exp, string Writer, bool Tomb)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                raw.Add((r.GetInt32(0), JsonNode.Parse(r.GetString(1))!, r.GetFieldValue<DateTime>(2), r.GetFieldValue<DateTime>(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTime>(4), r.GetString(5), r.GetBoolean(6)));
        if (raw.Count == 0) throw ChalkException.NotFound($"board {b.Name} has no key {key}");
        var list = new List<RevisionRow>();
        for (var i = 0; i < raw.Count; i++)
        {
            var cur = raw[i];
            if (cur.Tomb) continue;
            var prev = i + 1 < raw.Count ? raw[i + 1] : default;
            var diff = i + 1 < raw.Count && !prev.Tomb ? JsonDiff.Compute(prev.Value, cur.Value) : null;
            list.Add(new RevisionRow(cur.Rev, cur.Value, cur.Obs, cur.Wr, cur.Exp, cur.Writer, diff));
        }
        return list;
    }

    public async Task<List<ChangeRow>> ChangesAsync(Principal p, BoardDef b, DateTimeOffset since, string? prefix, string[]? kinds, int limit, CancellationToken ct)
    {
        p.RequireRead();
        await using var conn = await db.OpenConnectionAsync(ct);
        var sql = new StringBuilder("SELECT at, key, kind, from_revision, to_revision, from_value::text, to_value::text, writer FROM events WHERE board_id = @b AND at > @since");
        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        cmd.Parameters.AddWithValue("b", b.Id);
        cmd.Parameters.AddWithValue("since", since.UtcDateTime);
        if (!string.IsNullOrEmpty(prefix)) { sql.Append(" AND key LIKE @prefix ESCAPE '\\'"); cmd.Parameters.AddWithValue("prefix", KeyPath.EscapeLike(prefix) + "%"); }
        if (kinds is { Length: > 0 }) { sql.Append(" AND kind = ANY(@kinds)"); cmd.Parameters.AddWithValue("kinds", kinds); }
        sql.Append(" ORDER BY at DESC, id DESC LIMIT @lim");
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit <= 0 ? 200 : limit, 1, 1000));
        cmd.CommandText = sql.ToString();
        var list = new List<ChangeRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ChangeRow(r.GetFieldValue<DateTime>(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4),
                r.IsDBNull(5) ? null : JsonNode.Parse(r.GetString(5)), r.IsDBNull(6) ? null : JsonNode.Parse(r.GetString(6)), r.GetString(7)));
        return list;
    }

    public async Task<(List<RecordRow> Rows, string Mode)> SearchAsync(Principal p, BoardDef b, string query, string? prefix, string[]? filter, DateTimeOffset? since, int limit, Embedder embedder, CancellationToken ct)
    {
        p.RequireRead();
        if (string.IsNullOrWhiteSpace(query)) throw ChalkException.BadRequest("query is required");
        limit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 100);
        var (fsql, fps) = BuildFilters(b, prefix, filter, since, null);
        await using var conn = await db.OpenConnectionAsync(ct);

        var ft = new List<(RecordRow Row, double Rank, string Snippet)>();
        await using (var cmd = new NpgsqlCommand($"""
            SELECT {RecordCols}, ts_rank_cd(r.search, qq.q) AS rank,
                   ts_headline('english', coalesce(nullif(r.search_a, ''), r.value::text), qq.q, 'MaxFragments=2, MaxWords=24, MinWords=6, StartSel=<mark>, StopSel=</mark>') AS snippet
            FROM records r, (SELECT websearch_to_tsquery('english', @q) || websearch_to_tsquery('simple', @q) AS q) qq
            WHERE {LiveWhere}{fsql} AND r.search @@ qq.q
            ORDER BY rank DESC LIMIT @lim
            """, conn))
        {
            cmd.Parameters.AddWithValue("b", b.Id);
            cmd.Parameters.AddWithValue("q", query);
            cmd.Parameters.AddWithValue("lim", limit * 2);
            foreach (var pr in fps) cmd.Parameters.Add(pr.Clone());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ft.Add((ReadRecord(r, b), r.GetFloat(7), r.GetString(8)));
        }

        if (b.Embed is null)
            return (ft.Take(limit).Select(x => x.Row with { Score = Math.Round(x.Rank, 4), Snippet = x.Snippet }).ToList(), "fulltext");

        List<RecordRow> vec = [];
        try
        {
            var qv = (await embedder.EmbedAsync(b.Embed.Model, [query], b.Embed.Dims, ct))[0];
            await using var cmd = new NpgsqlCommand($"""
                SELECT {RecordCols}, (e.vector <=> @v::vector) AS dist
                FROM embeddings e JOIN records r ON r.board_id = e.board_id AND r.key = e.key AND r.revision = e.revision
                WHERE {LiveWhere}{fsql}
                ORDER BY e.vector <=> @v::vector LIMIT @lim
                """, conn);
            cmd.Parameters.AddWithValue("b", b.Id);
            cmd.Parameters.AddWithValue("v", Embedder.ToLiteral(qv));
            cmd.Parameters.AddWithValue("lim", limit * 2);
            foreach (var pr in fps) cmd.Parameters.Add(pr.Clone());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) vec.Add(ReadRecord(r, b) with { Score = 1 - r.GetDouble(7) });
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Vector search failed for board {Board}; returning full-text only", b.Name);
            return (ft.Take(limit).Select(x => x.Row with { Score = Math.Round(x.Rank, 4), Snippet = x.Snippet }).ToList(), "fulltext");
        }

        // Reciprocal rank fusion.
        var scores = new Dictionary<string, (double Score, RecordRow Row, string? Snippet)>(StringComparer.Ordinal);
        for (var i = 0; i < ft.Count; i++)
        {
            var s = 1.0 / (60 + i + 1);
            scores[ft[i].Row.Key] = (s, ft[i].Row, ft[i].Snippet);
        }
        for (var i = 0; i < vec.Count; i++)
        {
            var s = 1.0 / (60 + i + 1);
            if (scores.TryGetValue(vec[i].Key, out var ex)) scores[vec[i].Key] = (ex.Score + s, ex.Row, ex.Snippet);
            else scores[vec[i].Key] = (s, vec[i], null);
        }
        var fused = scores.Values.OrderByDescending(x => x.Score).Take(limit)
            .Select(x => x.Row with { Score = Math.Round(x.Score, 5), Snippet = x.Snippet }).ToList();
        return (fused, "hybrid");
    }

    public async Task<(string Segment, List<TreeNode> Children)> TreeAsync(Principal p, BoardDef b, string prefix, DateTimeOffset? since, CancellationToken ct)
    {
        p.RequireRead();
        var depth = b.KeyPointers.Length == 1 ? 0 : (prefix.Length == 0 ? 0 : prefix.TrimEnd('/').Split('/').Length);
        if (depth >= b.KeyPointers.Length) throw ChalkException.BadRequest("prefix is already a full key");
        var leaf = depth + 1 == b.KeyPointers.Length;
        var single = b.KeyPointers.Length == 1;
        var segExpr = single ? "r.key" : "split_part(substr(r.key, @plen + 1), '/', 1)";
        await using var conn = await db.OpenConnectionAsync(ct);
        var nodes = new Dictionary<string, (long Live, long Exp)>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand($"""
            SELECT {segExpr} AS seg, count(*), count(*) FILTER (WHERE r.expires_at IS NOT NULL AND r.expires_at < now() + interval '24 hours')
            FROM records r WHERE {LiveWhere} AND r.key LIKE @prefix ESCAPE '\' GROUP BY 1 ORDER BY 1 LIMIT 2000
            """, conn))
        {
            cmd.Parameters.AddWithValue("b", b.Id);
            cmd.Parameters.AddWithValue("prefix", KeyPath.EscapeLike(prefix) + "%");
            cmd.Parameters.AddWithValue("plen", prefix.Length);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) nodes[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
        }
        var changed = new Dictionary<string, long>(StringComparer.Ordinal);
        if (since.HasValue)
        {
            var segExprE = single ? "e.key" : "split_part(substr(e.key, @plen + 1), '/', 1)";
            await using var cmd = new NpgsqlCommand($"SELECT {segExprE} AS seg, count(*) FROM events e WHERE e.board_id = @b AND e.at > @since AND e.key LIKE @prefix ESCAPE '\\' GROUP BY 1", conn);
            cmd.Parameters.AddWithValue("b", b.Id);
            cmd.Parameters.AddWithValue("since", since.Value.UtcDateTime);
            cmd.Parameters.AddWithValue("prefix", KeyPath.EscapeLike(prefix) + "%");
            cmd.Parameters.AddWithValue("plen", prefix.Length);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) changed[r.GetString(0)] = r.GetInt64(1);
        }
        var children = nodes.Select(kv => new TreeNode(
            single ? kv.Key : KeyPath.DecodeSegment(kv.Key),
            single ? kv.Key : (leaf ? prefix + kv.Key : prefix + kv.Key + "/"),
            kv.Value.Live, kv.Value.Exp, changed.GetValueOrDefault(kv.Key), leaf)).ToList();
        return (b.SegmentNames[depth], children);
    }

    // ---------- delete ----------

    public async Task<(int Deleted, List<string> Keys)> DeleteAsync(Principal p, BoardDef b, string? key, string? prefix, int? expect, CancellationToken ct)
    {
        p.RequireDelete();
        if (string.IsNullOrEmpty(key) == string.IsNullOrEmpty(prefix)) throw ChalkException.BadRequest("pass exactly one of key or prefix");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var cur = new List<(string Key, int Rev, string Value)>();
        await using (var cmd = new NpgsqlCommand($"SELECT r.key, r.revision, r.value::text FROM records r WHERE {LiveWhere} AND " + (key is not null ? "r.key = @k" : "r.key LIKE @prefix ESCAPE '\\'") + " ORDER BY r.key FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("b", b.Id);
            if (key is not null) cmd.Parameters.AddWithValue("k", key); else cmd.Parameters.AddWithValue("prefix", KeyPath.EscapeLike(prefix!) + "%");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) cur.Add((r.GetString(0), r.GetInt32(1), r.GetString(2)));
        }
        if (key is not null && cur.Count == 0) throw ChalkException.NotFound($"board {b.Name} has no key {key}");
        if (expect.HasValue && expect.Value != cur.Count) throw ChalkException.Conflict($"{expect.Value} expected, {cur.Count} live");
        foreach (var (k, rev, val) in cur)
        {
            await using (var demote = new NpgsqlCommand("UPDATE records SET current = false WHERE board_id = @b AND key = @k AND current", conn, tx))
            {
                demote.Parameters.AddWithValue("b", b.Id); demote.Parameters.AddWithValue("k", k);
                await demote.ExecuteNonQueryAsync(ct);
            }
            await using (var ins = new NpgsqlCommand("INSERT INTO records(board_id, key, revision, value, observed_at, written_at, expires_at, writer, tombstone, current, search_a) VALUES (@b, @k, @r, @v, now(), now(), now() + @ret, @w, true, true, '')", conn, tx))
            {
                ins.Parameters.AddWithValue("b", b.Id); ins.Parameters.AddWithValue("k", k); ins.Parameters.AddWithValue("r", rev + 1);
                ins.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = val });
                ins.Parameters.Add(new NpgsqlParameter("ret", NpgsqlDbType.Interval) { Value = TombstoneRetention });
                ins.Parameters.AddWithValue("w", p.Display);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await InsertEventAsync(conn, tx, b, k, "deleted", rev, null, JsonNode.Parse(val), null, p.Display, ct);
        }
        await AuditAsync(conn, p.Display, "records.delete", new { board = b.Name, key, prefix, count = cur.Count }, ct);
        await tx.CommitAsync(ct);
        return (cur.Count, cur.Select(c => c.Key).ToList());
    }

    // ---------- sweep ----------

    public async Task<(int Expired, int Trimmed, int Tombstones, int Events)> SweepAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var got = (bool)(await new NpgsqlCommand("SELECT pg_try_advisory_lock(72450)", conn).ExecuteScalarAsync(ct))!;
        if (!got) return (0, 0, 0, 0);
        try
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var expired = await new NpgsqlCommand("""
                WITH exp AS (
                  SELECT r.board_id, r.key, r.revision, r.value, b.ttl
                  FROM records r JOIN boards b ON b.id = r.board_id
                  WHERE r.current AND NOT r.tombstone AND r.expires_at IS NOT NULL AND r.expires_at < now())
                INSERT INTO events(board_id, key, kind, at, from_revision, to_revision, from_value, to_value, writer, expires_at)
                SELECT board_id, key, 'expired', now(), revision, NULL, value, NULL, 'system:sweep', now() + coalesce(ttl, interval '30 days') FROM exp
                """, conn, tx).ExecuteNonQueryAsync(ct);
            await new NpgsqlCommand("""
                DELETE FROM records r USING (
                  SELECT board_id, key FROM records WHERE current AND NOT tombstone AND expires_at IS NOT NULL AND expires_at < now()) x
                WHERE r.board_id = x.board_id AND r.key = x.key
                """, conn, tx).ExecuteNonQueryAsync(ct);
            var trimmed = await new NpgsqlCommand("DELETE FROM records WHERE NOT current AND expires_at IS NOT NULL AND expires_at < now()", conn, tx).ExecuteNonQueryAsync(ct);
            var tomb = await new NpgsqlCommand("""
                DELETE FROM records r USING (
                  SELECT board_id, key FROM records WHERE current AND tombstone AND expires_at < now()) x
                WHERE r.board_id = x.board_id AND r.key = x.key
                """, conn, tx).ExecuteNonQueryAsync(ct);
            var events = await new NpgsqlCommand("DELETE FROM events WHERE expires_at IS NOT NULL AND expires_at < now()", conn, tx).ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return (expired, trimmed, tomb, events);
        }
        finally
        {
            await new NpgsqlCommand("SELECT pg_advisory_unlock(72450)", conn).ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
