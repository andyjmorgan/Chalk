using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Chalk;

[McpServerToolType]
public sealed class ChalkTools(Store store, Embedder embedder, IHttpContextAccessor http)
{
    private Principal P => (http.HttpContext ?? throw new InvalidOperationException("no HTTP context")).Principal();

    /// <summary>Surfaces Chalk errors to MCP clients as readable tool errors instead of the SDK's generic message.</summary>
    private static async Task<T> Run<T>(Func<Task<T>> f)
    {
        try { return await f(); }
        catch (ChalkException e) { throw new McpException($"{e.Code}: {e.Message}" + (e.Path is null ? "" : $" (at {e.Path})") + (e.Index is null ? "" : $" [record {e.Index}]")); }
    }

    private static JsonNode? Node(JsonElement? e) => e is null || e.Value.ValueKind == JsonValueKind.Undefined || e.Value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(e.Value.GetRawText());

    [McpServerTool(Name = "create_board", Title = "Create board")]
    [Description("Create a board: a named set of keyed, expiring JSON records under a JSON Schema. Idempotent on name. " +
                 "key is a list of JSON pointers into the value; their values join with '/' into the record key (one pointer = raw value, several = path like office/office-1/disk). " +
                 "ttl like 7d/14d/90d is the default lifetime from last write (omit to keep forever). search.fields get full-text weight; embed.fields turns on vector search for near-duplicates.")]
    public Task<object> CreateBoard(
        [Description("Slug: lowercase letters, digits, - and _.")] string name,
        [Description("Pointers that form the key, e.g. [\"/url\"] or [\"/cluster\",\"/node\",\"/check\"].")] string[] key,
        [Description("One line shown to models in listings.")] string? description = null,
        [Description("JSON Schema (draft 2020-12) for the record value. Defaults to any object.")] JsonElement? schema = null,
        [Description("Default record lifetime, e.g. 7d. Omit to keep records until deleted.")] string? ttl = null,
        [Description("Pointers given full-text weight A, e.g. [\"/title\"].")] string[]? searchFields = null,
        [Description("Pointers to embed for vector search, e.g. [\"/title\",\"/summary\"]. Off when omitted.")] string[]? embedFields = null,
        [Description("Revisions kept per key. Default 50.")] int? maxRevisions = null,
        CancellationToken ct = default)
    => Run(async () =>
    {
        var embed = embedFields is { Length: > 0 } ? new JsonObject { ["fields"] = new JsonArray(embedFields.Select(f => (JsonNode)f).ToArray()) } : null;
        var b = await store.CreateBoardAsync(P, name, description, Node(schema), key, ttl, maxRevisions, searchFields, embed, null, ct);
        return await store.DescribeBoardAsync(P, b.Name, ct);
    });

    [McpServerTool(Name = "describe_board", Title = "Describe board", ReadOnly = true)]
    [Description("Schema, key pointers, TTL, counts and search settings for a board. Call before the first write to a board you have not used.")]
    public Task<object> DescribeBoard([Description("Board name.")] string board, CancellationToken ct = default) =>
        Run(() => store.DescribeBoardAsync(P, board, ct));

    [McpServerTool(Name = "list_boards", Title = "List boards", ReadOnly = true)]
    [Description("Boards this key can see, with live counts, last write and a stale flag (no write for longer than the TTL).")]
    public Task<List<object>> ListBoards(CancellationToken ct = default) => Run(() => store.ListBoardsAsync(P, ct));

    [McpServerTool(Name = "upsert", Title = "Upsert records")]
    [Description("Write one or many records to a board. Each item is { value, ttl?, expiresAt?, observedAt? }. The key is derived from the value by the board's key pointers. " +
                 "A changed value writes a new revision and a 'changed' event; an identical value just refreshes expiry ('touched'). The batch is atomic: one schema violation fails the whole call with the item index. " +
                 "Returns { key, revision, expiresAt, kind } per item.")]
    public Task<List<UpsertResult>> Upsert(
        [Description("Board name.")] string board,
        [Description("Array of { value: object, ttl?: '2d', expiresAt?: ISO-8601, observedAt?: ISO-8601 }.")] JsonElement records,
        CancellationToken ct = default)
    => Run(async () =>
    {
        if (records.ValueKind != JsonValueKind.Array) throw ChalkException.BadRequest("records must be an array");
        var b = await store.GetBoardAsync(P, board, ct);
        return await store.UpsertAsync(P, b, RestEndpoints.ParseUpsertItems(JsonNode.Parse(records.GetRawText())!.AsArray()), embedder, ct);
    });

    [McpServerTool(Name = "get", Title = "Get records by key", ReadOnly = true)]
    [Description("Latest revision for each key that exists and has not expired. Keys that are absent are simply missing from the result, so one call dedups a whole run.")]
    public Task<Dictionary<string, object>> Get(
        [Description("Board name.")] string board,
        [Description("Keys to look up (max 1000).")] string[] keys,
        CancellationToken ct = default)
    => Run(async () =>
    {
        var b = await store.GetBoardAsync(P, board, ct);
        var found = await store.GetManyAsync(P, b, keys, ct);
        return found.ToDictionary(kv => kv.Key, kv => RestEndpoints.Envelope(kv.Value));
    });

    [McpServerTool(Name = "list", Title = "List records", ReadOnly = true)]
    [Description("Current records under a key prefix, optionally filtered by JSON pointer equality (filter: ['/status=warn']) and observedAt >= since. Cursor-paginated, ordered by key.")]
    public Task<object> List(
        [Description("Board name.")] string board,
        [Description("Key prefix such as 'office/' or '2026-10-15/'.")] string? prefix = null,
        [Description("Equality filters like '/status=warn'.")] string[]? filter = null,
        [Description("Only records observed at or after this ISO-8601 time.")] string? since = null,
        [Description("Page size, default 50, max 500.")] int? limit = null,
        [Description("Cursor from a previous page.")] string? cursor = null,
        CancellationToken ct = default)
    => Run<object>(async () =>
    {
        var b = await store.GetBoardAsync(P, board, ct);
        var (rows, next, total, _) = await store.ListAsync(P, b, new Store.ListQuery(prefix, filter, since is null ? null : RestEndpoints.ParseTime(since, "since"), limit ?? 50, cursor, null, null), ct);
        return new { records = rows.Select(RestEndpoints.Envelope), cursor = next, total };
    });

    [McpServerTool(Name = "history", Title = "Record history", ReadOnly = true)]
    [Description("Revisions of one key, newest first, each with a diff against the previous revision.")]
    public Task<List<RevisionRow>> History(
        [Description("Board name.")] string board,
        [Description("Record key.")] string key,
        [Description("Max revisions, default 50.")] int? limit = null,
        CancellationToken ct = default)
    => Run(async () =>
    {
        var b = await store.GetBoardAsync(P, board, ct);
        return await store.HistoryAsync(P, b, key, limit ?? 50, ct);
    });

    [McpServerTool(Name = "changes_since", Title = "Changes since", ReadOnly = true)]
    [Description("Keys created, changed, expired or deleted since a time, newest first, with from/to values. Call this at the start of a run to learn what moved since the last one. " +
                 "'expired' and 'deleted' entries include the last value so you can report what went away.")]
    public Task<object> ChangesSince(
        [Description("Board name.")] string board,
        [Description("ISO-8601 time, usually the previous run's start.")] string since,
        [Description("Key prefix to scope to, e.g. 'office/'.")] string? prefix = null,
        [Description("Subset of created, changed, expired, deleted.")] string[]? kind = null,
        [Description("Max entries, default 200.")] int? limit = null,
        CancellationToken ct = default)
    => Run<object>(async () =>
    {
        var b = await store.GetBoardAsync(P, board, ct);
        var rows = await store.ChangesAsync(P, b, RestEndpoints.ParseTime(since, "since"), prefix, kind, limit ?? 200, ct);
        return new { changes = rows, counts = rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count()) };
    });

    [McpServerTool(Name = "search", Title = "Search records", ReadOnly = true)]
    [Description("Search current records. Full-text always; hybrid with vectors when the board embeds. Returns records with score and snippet. Near-duplicates come back scored for you to judge; Chalk never blocks a write because something similar exists.")]
    public Task<object> Search(
        [Description("Board name.")] string board,
        [Description("Search text. Supports quoted phrases and -negation.")] string query,
        [Description("Key prefix to scope to.")] string? prefix = null,
        [Description("Equality filters like '/status=warn'.")] string[]? filter = null,
        [Description("Max results, default 20.")] int? limit = null,
        [Description("Cosine similarity floor for vector hits (0-1). Defaults to the board's embed.minScore, 0.6. Lower to widen, raise to tighten.")] double? minScore = null,
        CancellationToken ct = default)
    => Run<object>(async () =>
    {
        var b = await store.GetBoardAsync(P, board, ct);
        var (rows, mode) = await store.SearchAsync(P, b, query, prefix, filter, null, limit ?? 20, embedder, ct, minScore);
        return new { mode, results = rows.Select(RestEndpoints.Envelope) };
    });

    [McpServerTool(Name = "delete", Title = "Delete records", Destructive = true)]
    [Description("Tombstone one key or a whole key prefix. Rare for agents: expiry does this work. Pass expect to fail if the live count differs. Returns the keys removed.")]
    public Task<object> Delete(
        [Description("Board name.")] string board,
        [Description("Exact key to delete.")] string? key = null,
        [Description("Key prefix to delete, e.g. 'office/office-1/'.")] string? prefix = null,
        [Description("Expected live count under the prefix; the call fails with conflict if it differs.")] int? expect = null,
        CancellationToken ct = default)
    => Run<object>(async () =>
    {
        var b = await store.GetBoardAsync(P, board, ct);
        var (n, keys) = await store.DeleteAsync(P, b, key, prefix, expect, ct);
        return new { deleted = n, keys };
    });
}

[McpServerResourceType]
public static class ChalkResources
{
    public static string SkillMarkdown { get; } = Load();

    private static string Load()
    {
        using var s = typeof(ChalkResources).Assembly.GetManifestResourceStream("Chalk.Skill.SKILL.md")!;
        return new StreamReader(s).ReadToEnd();
    }

    [McpServerResource(UriTemplate = "skill://chalk/SKILL.md", Name = "chalk-skill", Title = "Chalk skill", MimeType = "text/markdown")]
    [Description("The Chalk skill: when to write a record, how keys derive from values, and how to use changes_since at the start of a run.")]
    public static string Skill() => SkillMarkdown;
}
