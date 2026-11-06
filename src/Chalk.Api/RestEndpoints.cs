using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Chalk;

public static class RestEndpoints
{
    private static IResult Ok(object? data, string? cursor = null, object? meta = null) =>
        Results.Json(new { data, cursor, meta }, Json.Options);

    public static IReadOnlyList<UpsertItem> ParseUpsertBody(JsonNode? body)
    {
        if (body is not JsonObject o) throw ChalkException.BadRequest("body must be a JSON object with records[] or value");
        JsonArray items;
        if (o["records"] is JsonArray ra) items = ra;
        else if (o["value"] is JsonNode v) items = [new JsonObject { ["value"] = v.DeepClone(), ["ttl"] = o["ttl"]?.DeepClone(), ["expiresAt"] = o["expiresAt"]?.DeepClone(), ["observedAt"] = o["observedAt"]?.DeepClone() }];
        else throw ChalkException.BadRequest("body needs records: [{ value, ttl?, expiresAt?, observedAt? }] or a single value");
        return ParseUpsertItems(items);
    }

    public static IReadOnlyList<UpsertItem> ParseUpsertItems(JsonArray items)
    {
        var list = new List<UpsertItem>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not JsonObject it) throw ChalkException.BadRequest($"records[{i}] must be an object", null, i);
            var value = it["value"] ?? throw ChalkException.BadRequest($"records[{i}].value is required", null, i);
            if (value is not JsonObject) throw ChalkException.BadRequest($"records[{i}].value must be an object", "/value", i);
            TimeSpan? ttl = it["ttl"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? Durations.Parse(ts, "ttl") : null;
            DateTimeOffset? exp = it["expiresAt"] is JsonValue ev && ev.TryGetValue<string>(out var es) ? ParseTime(es, "expiresAt") : null;
            DateTimeOffset? obs = it["observedAt"] is JsonValue ov && ov.TryGetValue<string>(out var os) ? ParseTime(os, "observedAt") : null;
            list.Add(new UpsertItem(value.DeepClone(), ttl, exp, obs));
        }
        return list;
    }

    public static DateTimeOffset ParseTime(string s, string what) =>
        DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
            ? t : throw ChalkException.BadRequest($"{what} '{s}' is not an ISO-8601 timestamp");

    public static object Envelope(RecordRow r) => new
    {
        key = r.Key, segments = r.Segments, value = r.Value, revision = r.Revision,
        observedAt = r.ObservedAt, writtenAt = r.WrittenAt, expiresAt = r.ExpiresAt, writer = r.Writer,
        score = r.Score, similarity = r.Similarity, rank = r.Rank, snippet = r.Snippet,
    };

    public static void MapChalkRest(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/me", async (HttpContext ctx, Store store, CancellationToken ct) =>
        {
            var p = ctx.Principal();
            if (p.IsUser) await store.UpsertUserAsync(p, ct);
            var boards = p.Read ? (await store.ListBoardsAsync(p, ct)).Select(b => ((dynamic)b).name as string).ToArray() : [];
            return Ok(new { principal = p.Display, user = p.UserId, kind = p.IsUser ? "user" : "key", name = p.Name, email = p.Email, picture = p.Picture, scopes = p.Scopes(), boards, boardScope = p.Boards, canManageKeys = p.IsUser || p.Admin });
        });

        api.MapGet("/boards", async (HttpContext ctx, Store store, CancellationToken ct) =>
            Ok(await store.ListBoardsAsync(ctx.Principal(), ct), null, new { asOf = DateTimeOffset.UtcNow }));

        api.MapPost("/boards", async (HttpContext ctx, Store store, [FromBody] JsonNode body, CancellationToken ct) =>
        {
            var o = body as JsonObject ?? throw ChalkException.BadRequest("body must be an object");
            var name = o["name"]?.GetValue<string>() ?? throw ChalkException.BadRequest("name is required");
            var key = o["key"] is JsonArray ka ? ka.Select(x => x!.GetValue<string>()).ToArray() : o["key"] is JsonValue kv ? [kv.GetValue<string>()] : throw ChalkException.BadRequest("key must be a list of JSON pointers");
            var search = o["search"] is JsonObject so && so["fields"] is JsonArray sf ? sf.Select(x => x!.GetValue<string>()).ToArray() : null;
            var b = await store.CreateBoardAsync(ctx.Principal(), name, o["description"]?.GetValue<string>(), o["schema"], key, o["ttl"]?.GetValue<string>(),
                o["maxRevisions"]?.GetValue<int>(), search, o["embed"], o["maxValueBytes"]?.GetValue<int>(), ct);
            return Results.Json(new { data = await store.DescribeBoardAsync(ctx.Principal(), b.Name, ct) }, Json.Options, statusCode: 201);
        });

        api.MapGet("/boards/{board}", async (HttpContext ctx, Store store, string board, CancellationToken ct) =>
            Ok(await store.DescribeBoardAsync(ctx.Principal(), board, ct)));

        api.MapDelete("/boards/{board}", async (HttpContext ctx, Store store, string board, string? confirm, CancellationToken ct) =>
        {
            if (!string.Equals(confirm, board, StringComparison.Ordinal)) throw ChalkException.BadRequest("pass confirm=<board name> to delete a board and all its records");
            return Ok(await store.DeleteBoardAsync(ctx.Principal(), board, ct));
        });
        api.MapGet("/boards/{board}/tree", async (HttpContext ctx, Store store, string board, string? prefix, string? since, CancellationToken ct) =>
        {
            var p = ctx.Principal();
            var b = await store.GetBoardAsync(p, board, ct);
            var (segment, children) = await store.TreeAsync(p, b, prefix ?? "", since is null ? null : ParseTime(since, "since"), ct);
            return Ok(new { prefix = prefix ?? "", segment, children }, null, new { board = b.Name, asOf = DateTimeOffset.UtcNow });
        });

        api.MapGet("/boards/{board}/records", async (HttpContext ctx, Store store, string board, string? prefix, [FromQuery(Name = "filter")] string[]? filter, string? since, int? limit, string? cursor, string? expiringWithin, string? sort, CancellationToken ct) =>
        {
            var p = ctx.Principal();
            var b = await store.GetBoardAsync(p, board, ct);
            var q = new Store.ListQuery(prefix, filter, since is null ? null : ParseTime(since, "since"), limit ?? 50, cursor,
                expiringWithin is null ? null : Durations.Parse(expiringWithin, "expiringWithin"), sort ?? (expiringWithin is null ? null : "expiresAt"));
            var (rows, next, total, facets) = await store.ListAsync(p, b, q, ct);
            return Ok(rows.Select(Envelope), next, new { board = b.Name, prefix = prefix ?? "", total, facets, asOf = DateTimeOffset.UtcNow });
        });

        api.MapPost("/boards/{board}/records", async (HttpContext ctx, Store store, Embedder embedder, string board, [FromBody] JsonNode body, CancellationToken ct) =>
        {
            var p = ctx.Principal();
            var b = await store.GetBoardAsync(p, board, ct);
            var results = await store.UpsertAsync(p, b, ParseUpsertBody(body), embedder, ct);
            return Ok(results, null, new { board = b.Name, count = results.Count });
        });

        api.MapPost("/boards/{board}/records/lookup", async (HttpContext ctx, Store store, string board, [FromBody] JsonNode body, CancellationToken ct) =>
        {
            var p = ctx.Principal();
            var b = await store.GetBoardAsync(p, board, ct);
            var keys = (body as JsonObject)?["keys"] as JsonArray ?? throw ChalkException.BadRequest("body needs keys: [...]");
            var found = await store.GetManyAsync(p, b, keys.Select(k => k!.GetValue<string>()).ToArray(), ct);
            return Ok(found.ToDictionary(kv => kv.Key, kv => Envelope(kv.Value)), null, new { board = b.Name, requested = keys.Count, found = found.Count });
        });

        api.MapGet("/boards/{board}/records/{key}", async (HttpContext ctx, Store store, string board, string key, CancellationToken ct) =>
        {
            key = Uri.UnescapeDataString(key);
            var p = ctx.Principal();
            var b = await store.GetBoardAsync(p, board, ct);
            var found = await store.GetManyAsync(p, b, [key], ct);
            return found.TryGetValue(key, out var r) ? Ok(Envelope(r)) : throw ChalkException.NotFound($"board {b.Name} has no key {key}");
        });

        api.MapGet("/boards/{board}/records/{key}/history", async (HttpContext ctx, Store store, string board, string key, int? limit, CancellationToken ct) =>
        {
            key = Uri.UnescapeDataString(key);
            var p = ctx.Principal();
            var b = await store.GetBoardAsync(p, board, ct);
            var hist = await store.HistoryAsync(p, b, key, limit ?? 50, ct);
            return Ok(hist, null, new { board = b.Name, key, total = hist.Count });
        });

        api.MapDelete("/boards/{board}/records/{key}", async (HttpContext ctx, Store store, string board, string key, CancellationToken ct) =>
        {
            key = Uri.UnescapeDataString(key);
            var p = ctx.Principal();
            var b = await store.GetBoardAsync(p, board, ct);
            var (n, keys) = await store.DeleteAsync(p, b, key, null, null, ct);
            return Ok(new { deleted = n, keys });
        });

        api.MapDelete("/boards/{board}/records", async (HttpContext ctx, Store store, string board, string? prefix, int? expect, CancellationToken ct) =>
        {
            var p = ctx.Principal();
            var b = await store.GetBoardAsync(p, board, ct);
            if (string.IsNullOrEmpty(prefix)) throw ChalkException.BadRequest("prefix is required for a subtree delete");
            var (n, keys) = await store.DeleteAsync(p, b, null, prefix, expect, ct);
            return Ok(new { deleted = n, keys });
        });

        api.MapGet("/boards/{board}/changes", async (HttpContext ctx, Store store, string board, string? since, string? prefix, string? kind, int? limit, CancellationToken ct) =>
        {
            var p = ctx.Principal();
            var b = await store.GetBoardAsync(p, board, ct);
            var sinceT = since is null ? DateTimeOffset.UtcNow.AddDays(-1) : ParseTime(since, "since");
            var kinds = string.IsNullOrEmpty(kind) ? null : kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var rows = await store.ChangesAsync(p, b, sinceT, prefix, kinds, limit ?? 200, ct);
            var counts = rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());
            return Ok(rows, null, new { board = b.Name, since = sinceT, prefix = prefix ?? "", counts, asOf = DateTimeOffset.UtcNow });
        });

        api.MapGet("/boards/{board}/search", async (HttpContext ctx, Store store, Embedder embedder, string board, string q, string? prefix, [FromQuery(Name = "filter")] string[]? filter, string? since, int? limit, double? minScore, CancellationToken ct) =>
        {
            var p = ctx.Principal();
            var b = await store.GetBoardAsync(p, board, ct);
            var (rows, mode) = await store.SearchAsync(p, b, q, prefix, filter, since is null ? null : ParseTime(since, "since"), limit ?? 20, embedder, ct, minScore);
            return Ok(rows.Select(Envelope), null, new { board = b.Name, mode, total = rows.Count });
        });

        api.MapGet("/keys", async (HttpContext ctx, Store store, CancellationToken ct) => Ok(await store.ListKeysAsync(ctx.Principal(), ct)));
        api.MapPost("/keys", async (HttpContext ctx, Store store, [FromBody] JsonNode body, CancellationToken ct) =>
        {
            var o = body as JsonObject ?? throw ChalkException.BadRequest("body must be an object");
            var boards = o["boards"] is JsonArray ba ? ba.Select(x => x!.GetValue<string>()).ToArray() : null;
            DateTimeOffset? expiresAt = o["expiresAt"] is JsonValue xa ? ParseTime(xa.GetValue<string>(), "expiresAt") : o["expiresIn"] is JsonValue xi ? DateTimeOffset.UtcNow + Durations.Parse(xi.GetValue<string>()) : null;
            var created = await store.CreateKeyAsync(ctx.Principal(), o["name"]?.GetValue<string>() ?? "", o["read"]?.GetValue<bool>() ?? true, o["write"]?.GetValue<bool>() ?? false,
                o["delete"]?.GetValue<bool>() ?? false, o["admin"]?.GetValue<bool>() ?? false, boards, expiresAt, ct);
            return Results.Json(new { data = created }, Json.Options, statusCode: 201);
        });
        api.MapDelete("/keys/{id:guid}", async (HttpContext ctx, Store store, Guid id, CancellationToken ct) =>
        {
            await store.RevokeKeyAsync(ctx.Principal(), id, ct);
            return Ok(new { revoked = id });
        });
    }

    public static async Task ErrorEnvelope(HttpContext ctx, Func<Task> next, ILogger log)
    {
        try { await next(); }
        catch (ChalkException e)
        {
            ctx.Response.Clear();
            ctx.Response.StatusCode = e.Status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code = e.Code, message = e.Message, path = e.Path, index = e.Index } }));
        }
        catch (BadHttpRequestException e)
        {
            ctx.Response.Clear();
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code = "bad_request", message = e.Message, path = (string?)null, index = (int?)null } }));
        }
        catch (JsonException e)
        {
            ctx.Response.Clear();
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code = "bad_request", message = "invalid JSON: " + e.Message, path = (string?)null, index = (int?)null } }));
        }
        catch (Exception e) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            log.LogError(e, "Unhandled error on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            if (ctx.Response.HasStarted) throw;
            ctx.Response.Clear();
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code = "internal", message = "internal error", path = (string?)null, index = (int?)null } }));
        }
    }
}
