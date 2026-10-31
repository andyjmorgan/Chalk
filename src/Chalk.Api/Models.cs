using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chalk;

public sealed class ChalkException(int status, string code, string message, string? path = null, int? index = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? Path { get; } = path;
    public int? Index { get; } = index;

    public static ChalkException NotFound(string message) => new(404, "not_found", message);
    public static ChalkException Forbidden(string message) => new(403, "forbidden", message);
    public static ChalkException BadRequest(string message, string? path = null, int? index = null) => new(400, "bad_request", message, path, index);
    public static ChalkException Conflict(string message) => new(409, "conflict", message);
}

/// <summary>Who is calling. Rendered as user:&lt;id&gt; / key:&lt;name&gt; / system:sweep in writer columns.</summary>
public sealed record Principal(string UserId, string Display, bool Read, bool Write, bool Delete, bool Admin, string[]? Boards)
{
    public bool CanBoard(string name) => Boards is null || Boards.Contains(name, StringComparer.Ordinal);
    public void RequireRead() { if (!Read) throw ChalkException.Forbidden("scope chalk:read required"); }
    public void RequireWrite() { if (!Write) throw ChalkException.Forbidden("scope chalk:write required"); }
    public void RequireDelete() { if (!Delete) throw ChalkException.Forbidden("scope chalk:delete required"); }
    public void RequireAdmin() { if (!Admin) throw ChalkException.Forbidden("scope chalk:admin required"); }
    /// <summary>A signed-in human (Keycloak) rather than an API key or the sweep.</summary>
    public bool IsUser => Display.StartsWith("user:", StringComparison.Ordinal);
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Picture { get; init; }
    /// <summary>Keys are minted by signed-in users; a key with chalk:admin may also manage keys (bootstrap path).</summary>
    public void RequireKeys() { if (!IsUser && !Admin) throw ChalkException.Forbidden("sign in, or use a key with chalk:admin, to manage API keys"); }
    public string[] Scopes()
    {
        var s = new List<string>();
        if (Read) s.Add("chalk:read");
        if (Write) s.Add("chalk:write");
        if (Delete) s.Add("chalk:delete");
        if (Admin) s.Add("chalk:admin");
        return s.ToArray();
    }
}

public sealed record SearchConfig(string[] Fields);
public sealed record EmbedConfig(string[] Fields, string Model, int Dims, double MinScore = EmbedConfig.DefaultMinScore)
{
    /// <summary>Cosine similarity floor for vector hits. Unrelated text scores ~0.45-0.56 with qwen3-embedding; real matches 0.65+.</summary>
    public const double DefaultMinScore = 0.6;
}

public sealed record BoardDef(
    Guid Id, string OwnerId, string Name, string Description, string SchemaJson, string[] KeyPointers,
    TimeSpan? Ttl, int MaxRevisions, SearchConfig Search, EmbedConfig? Embed, int MaxValueBytes, long SizeCap,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>Pointer names without the leading slash, used as tree level labels and segment names.</summary>
    public string[] SegmentNames => KeyPointers.Select(p => p.TrimStart('/').Replace("~1", "/").Replace("~0", "~")).ToArray();
}

public sealed record UpsertItem(JsonNode Value, TimeSpan? Ttl, DateTimeOffset? ExpiresAt, DateTimeOffset? ObservedAt);
public sealed record UpsertResult(string Key, int Revision, DateTimeOffset? ExpiresAt, string Kind);

public sealed record RecordRow(string Key, JsonNode Value, int Revision, DateTimeOffset ObservedAt, DateTimeOffset WrittenAt, DateTimeOffset? ExpiresAt, string Writer)
{
    public Dictionary<string, string>? Segments { get; init; }
    public double? Score { get; init; }
    public string? Snippet { get; init; }
}

public sealed record RevisionRow(int Revision, JsonNode Value, DateTimeOffset ObservedAt, DateTimeOffset WrittenAt, DateTimeOffset? ExpiresAt, string Writer, List<JsonDiffEntry>? Diff);
public sealed record JsonDiffEntry(string Path, JsonNode? From, JsonNode? To);
public sealed record ChangeRow(DateTimeOffset At, string Key, string Kind, int? FromRevision, int? ToRevision, JsonNode? From, JsonNode? To, string Writer);
public sealed record TreeNode(string Segment, string Prefix, long Live, long Expiring24h, long ChangedSince, bool Leaf);

public static class Durations
{
    /// <summary>Parses 30s / 15m / 2h / 14d / 2w. Also accepts ISO-8601 "P14D"-style via TimeSpan fallback.</summary>
    public static TimeSpan Parse(string s, string what = "duration")
    {
        s = s.Trim();
        if (s.Length >= 2 && char.IsDigit(s[0]))
        {
            var unit = s[^1];
            if (double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0)
            {
                return unit switch
                {
                    's' => TimeSpan.FromSeconds(n),
                    'm' => TimeSpan.FromMinutes(n),
                    'h' => TimeSpan.FromHours(n),
                    'd' => TimeSpan.FromDays(n),
                    'w' => TimeSpan.FromDays(7 * n),
                    _ => throw ChalkException.BadRequest($"{what} '{s}' must end in s, m, h, d or w"),
                };
            }
        }
        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts) && ts > TimeSpan.Zero) return ts;
        throw ChalkException.BadRequest($"{what} '{s}' is not a duration like 14d, 2h or 30m");
    }

    public static string Format(TimeSpan t)
    {
        if (t.TotalDays >= 1 && t.TotalDays == Math.Floor(t.TotalDays)) return $"{(int)t.TotalDays}d";
        if (t.TotalHours >= 1 && t.TotalHours == Math.Floor(t.TotalHours)) return $"{(int)t.TotalHours}h";
        if (t.TotalMinutes >= 1 && t.TotalMinutes == Math.Floor(t.TotalMinutes)) return $"{(int)t.TotalMinutes}m";
        return $"{(int)t.TotalSeconds}s";
    }
}

public static class KeyPath
{
    public static JsonNode? Resolve(JsonNode? node, string pointer)
    {
        if (pointer.Length == 0) return node;
        if (pointer[0] != '/') throw ChalkException.BadRequest($"pointer '{pointer}' must start with /");
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var tok = raw.Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(tok, out var v) ? v : null,
                JsonArray a when int.TryParse(tok, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    public static string[] PointerTokens(string pointer) =>
        pointer.Length == 0 ? [] : pointer.Split('/').Skip(1).Select(t => t.Replace("~1", "/").Replace("~0", "~")).ToArray();

    public static string ScalarText(JsonNode node, string pointer)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            return v.ToJsonString();
        }
        throw ChalkException.BadRequest($"key pointer {pointer} must point at a string, number or boolean", pointer);
    }

    public static string EncodeSegment(string s) => s.Replace("%", "%25").Replace("/", "%2F");
    public static string DecodeSegment(string s) => s.Replace("%2F", "/").Replace("%25", "%");

    /// <summary>Derives the record key from the value. Single pointer = raw value; several = encoded segments joined by '/'.</summary>
    public static string Derive(JsonNode value, string[] pointers, int? index = null)
    {
        var parts = new string[pointers.Length];
        for (var i = 0; i < pointers.Length; i++)
        {
            var n = Resolve(value, pointers[i]) ?? throw new ChalkException(400, "bad_request", $"key pointer {pointers[i]} is missing from the value", pointers[i], index);
            var text = ScalarText(n, pointers[i]);
            if (text.Length == 0) throw new ChalkException(400, "bad_request", $"key pointer {pointers[i]} is empty", pointers[i], index);
            parts[i] = pointers.Length == 1 ? text : EncodeSegment(text);
        }
        return string.Join('/', parts);
    }

    public static Dictionary<string, string> Segments(string key, BoardDef board)
    {
        var names = board.SegmentNames;
        var d = new Dictionary<string, string>();
        if (names.Length == 1) { d[names[0]] = key; return d; }
        var parts = key.Split('/');
        for (var i = 0; i < names.Length && i < parts.Length; i++) d[names[i]] = DecodeSegment(parts[i]);
        return d;
    }

    public static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

public static class JsonDiff
{
    public static List<JsonDiffEntry> Compute(JsonNode? from, JsonNode? to)
    {
        var list = new List<JsonDiffEntry>();
        Walk("", from, to, list);
        return list;
    }

    private static void Walk(string path, JsonNode? a, JsonNode? b, List<JsonDiffEntry> out_)
    {
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var k in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)).Distinct())
            {
                oa.TryGetPropertyValue(k, out var va); ob.TryGetPropertyValue(k, out var vb);
                Walk(path + "/" + k.Replace("~", "~0").Replace("/", "~1"), va, vb, out_);
            }
            return;
        }
        if (a is JsonArray aa && b is JsonArray ab && aa.Count == ab.Count)
        {
            for (var i = 0; i < aa.Count; i++) Walk($"{path}/{i}", aa[i], ab[i], out_);
            return;
        }
        if (!JsonNode.DeepEquals(a, b)) out_.Add(new JsonDiffEntry(path.Length == 0 ? "" : path, a?.DeepClone(), b?.DeepClone()));
    }
}

public static class EmbedText
{
    public const int MaxChars = 8000;

    /// <summary>Labelled lines from the declared fields, in order. Deterministic so re-embedding reproduces.</summary>
    public static (string Text, bool Truncated) Build(JsonNode value, string[] fields)
    {
        var sb = new StringBuilder();
        foreach (var f in fields)
        {
            var n = KeyPath.Resolve(value, f);
            if (n is null) continue;
            var label = f.TrimStart('/').Replace("/", ".");
            sb.Append(label).Append(": ").Append(Flatten(n)).Append('\n');
        }
        var text = sb.ToString().TrimEnd();
        if (text.Length > MaxChars) return (text[..MaxChars], true);
        return (text, false);
    }

    private static string Flatten(JsonNode n) => n switch
    {
        JsonValue v => v.TryGetValue<string>(out var s) ? s : v.ToJsonString(),
        JsonArray a => string.Join(", ", a.Where(x => x is not null).Select(x => Flatten(x!))),
        JsonObject o => string.Join("; ", o.Select(p => $"{p.Key}={(p.Value is null ? "null" : Flatten(p.Value))}")),
        _ => n.ToJsonString(),
    };
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
