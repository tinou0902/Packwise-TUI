using System.Text.Json;

namespace Packwise;

/// <summary>
/// Registre npm : dist-tags et packument (historique des versions, métadonnées). Les registres configurés
/// (`.npmrc`, portées `@scope:registry`) sont respectés ; les registres authentifiés ne sont pas gérés.
/// </summary>
public static class NpmRegistry
{
    public const string Npmjs = "https://registry.npmjs.org/";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Un registre : celui par défaut (Scope null) ou celui d'une portée ("@acme").</summary>
    public sealed record Source(string? Scope, string Url);

    static string WithSlash(string url) => url.EndsWith('/') ? url : url + "/";

    /// <summary>Registres de `npm config list --json` : celui par défaut, puis ceux des portées.</summary>
    public static List<Source> ParseConfig(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sources = new List<Source>();
        void Add(string? scope, string url)
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            if (!sources.Any(s => s.Scope == scope)) sources.Add(new Source(scope, WithSlash(url)));
        }
        var root = doc.RootElement;
        Add(null, root.TryGetProperty("registry", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : Npmjs);
        foreach (var p in root.EnumerateObject())
            if (p.Name.StartsWith('@') && p.Name.EndsWith(":registry") && p.Value.ValueKind == JsonValueKind.String)
                Add(p.Name[..^":registry".Length], p.Value.GetString()!);
        return sources;
    }

    /// <summary>Registre d'un paquet : celui de sa portée, sinon celui par défaut.</summary>
    public static string RegistryFor(string name, IReadOnlyList<Source> sources)
    {
        int slash = name.IndexOf('/');
        string? scope = name.StartsWith('@') && slash > 0 ? name[..slash] : null;
        return sources.FirstOrDefault(s => scope is not null && s.Scope == scope)?.Url
            ?? sources.FirstOrDefault(s => s.Scope is null)?.Url
            ?? Npmjs;
    }

    /// <summary>Chemin d'URL d'un nom ("@scope/nom" garde son "@" et échappe le "/").</summary>
    public static string EncodeName(string name)
    {
        var e = Uri.EscapeDataString(name);
        return e.StartsWith("%40") ? "@" + e[3..] : e;
    }

    public static async Task<Dictionary<string, string>> DistTagsAsync(string registry, string name)
    {
        var json = await Http.GetStringAsync($"{WithSlash(registry)}-/package/{EncodeName(name)}/dist-tags");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    /// <summary>Packument complet d'un paquet (à libérer par l'appelant).</summary>
    public static async Task<JsonDocument> PackumentAsync(string registry, string name)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, WithSlash(registry) + EncodeName(name));
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    /// <summary>Page d'un paquet sur npmjs.com.</summary>
    public static string NpmjsPage(string name) => $"https://www.npmjs.com/package/{name}";

    public static bool IsNpmjs(string registry) =>
        WithSlash(registry).Equals(Npmjs, StringComparison.OrdinalIgnoreCase)
        || WithSlash(registry).Equals("https://registry.yarnpkg.com/", StringComparison.OrdinalIgnoreCase);
}
