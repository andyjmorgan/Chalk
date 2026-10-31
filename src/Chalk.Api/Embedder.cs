using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Chalk;

/// <summary>Ollama /api/embed client. Vectors are Matryoshka-truncated to the board's dims and L2-normalised.</summary>
public sealed class Embedder(IHttpClientFactory factory, IConfiguration cfg)
{
    public const string DefaultModel = "qwen3-embedding:0.6b";

    public async Task<List<float[]>> EmbedAsync(string model, List<string> inputs, int dims, CancellationToken ct)
    {
        var url = (cfg["Embeddings:OllamaUrl"] ?? "http://192.168.69.21:11434").TrimEnd('/');
        var http = factory.CreateClient("ollama");
        using var resp = await http.PostAsJsonAsync($"{url}/api/embed", new { model = string.IsNullOrEmpty(model) ? DefaultModel : model, input = inputs }, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonNode>(ct) ?? throw new InvalidOperationException("empty embed response");
        var arr = doc["embeddings"]?.AsArray() ?? throw new InvalidOperationException("embed response has no embeddings");
        var result = new List<float[]>(arr.Count);
        foreach (var e in arr)
        {
            var v = e!.AsArray().Select(x => (float)x!.GetValue<double>()).ToArray();
            if (v.Length > dims) v = v[..dims];
            if (v.Length < dims) throw new InvalidOperationException($"model returned {v.Length} dims, board needs {dims}");
            result.Add(Normalize(v));
        }
        return result;
    }

    private static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var n = Math.Sqrt(sum);
        if (n == 0) return v;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
        return v;
    }

    public static string ToLiteral(float[] v) => "[" + string.Join(',', v.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
}
