namespace NuKeeper;

using System.Text.Json;

/// <summary>
/// API HTTP NuGet (v3) — pour ce que le CLI ne fournit pas en un coup :
/// historique complet des versions, métadonnées (description, auteurs, dépendances), téléchargements.
/// </summary>
public static class NuGetFeed
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public sealed class PackageInfo
    {
        public string Description = "";
        public string Authors = "";
        public string ProjectUrl = "";
        public string LicenseUrl = "";
        public long Downloads = -1;   // -1 = inconnu
        public Dictionary<string, List<(string Id, string Range)>> Dependencies = new();
    }

    /// <summary>Toutes les versions publiées, de la plus récente à la plus ancienne.</summary>
    public static async Task<List<string>> GetVersionsAsync(string id)
    {
        var url = $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/index.json";
        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("versions", out var versions))
            throw new InvalidDataException($"Réponse inattendue pour {id}.");
        var list = versions.EnumerateArray().Select(e => e.GetString()!).ToList();
        // L'index flatcontainer est le plus souvent ordre ascendant.
        list.Reverse();
        return list;
    }

    /// <summary>Métadonnées d'une version donnée (registre de publication).</summary>
    public static async Task<PackageInfo> GetInfoAsync(string id, string version)
    {
        var url = $"https://api.nuget.org/v3/registration5-semver1/{id.ToLowerInvariant()}/{version.ToLowerInvariant()}.json";
        var json = await Http.GetStringAsync(url);
        using var leaf = JsonDocument.Parse(json);
        var info = new PackageInfo();

        if (!leaf.RootElement.TryGetProperty("catalogEntry", out var entry))
            return info;
        // Sur la feuille de registration, catalogEntry est une URL vers le catalogue (pas un objet).
        JsonDocument? catalogDoc = null;
        JsonElement ce = entry;
        if (entry.ValueKind == JsonValueKind.String)
        {
            catalogDoc = JsonDocument.Parse(await Http.GetStringAsync(entry.GetString()!));
            ce = catalogDoc.RootElement;
        }
        using var _ = catalogDoc;
        if (ce.TryGetProperty("description", out var d)) info.Description = d.GetString() ?? "";
        if (ce.TryGetProperty("authors", out var a)) info.Authors = a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
        if (ce.TryGetProperty("projectUrl", out var pu)) info.ProjectUrl = pu.GetString() ?? "";
        if (ce.TryGetProperty("licenseUrl", out var lu)) info.LicenseUrl = lu.GetString() ?? "";
        if (ce.TryGetProperty("dependencyGroups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var g in groups.EnumerateArray())
            {
                string fw = g.TryGetProperty("targetFramework", out var tf) ? tf.GetString() ?? "" : "";
                if (fw.Length == 0) fw = "any";
                var list = new List<(string, string)>();
                if (g.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
                    foreach (var dep in deps.EnumerateArray())
                    {
                        string i2 = dep.TryGetProperty("id", out var di) ? di.GetString() ?? "" : "";
                        string r = dep.TryGetProperty("range", out var dr) ? dr.GetString() ?? "" : "";
                        if (i2.Length > 0) list.Add((i2, r));
                    }
                info.Dependencies[fw] = list;
            }

        info.Downloads = await GetDownloadsAsync(id);
        return info;
    }

    // ---------- Vulnérabilités par version (ressource VulnerabilityInfo de nuget.org) ----------

    public sealed record VulnRange(string Url, int Severity, string Range);

    public static string SeverityName(int s) => s switch { 0 => "Low", 1 => "Moderate", 2 => "High", 3 => "Critical", _ => "Unknown" };

    static Task<Dictionary<string, List<VulnRange>>>? _vulnDb;

    /// <summary>Base d'advisories (id en minuscules → plages de versions touchées), chargée une seule fois.</summary>
    public static Task<Dictionary<string, List<VulnRange>>> GetVulnDbAsync()
    {
        var t = _vulnDb ??= LoadVulnDb();
        if (t.IsFaulted) _vulnDb = null;
        return t;
    }

    static async Task<Dictionary<string, List<VulnRange>>> LoadVulnDb()
    {
        var db = new Dictionary<string, List<VulnRange>>(StringComparer.OrdinalIgnoreCase);
        using var index = JsonDocument.Parse(await ReadMaybeGzipAsync("https://api.nuget.org/v3/vulnerabilities/index.json"));
        foreach (var page in index.RootElement.EnumerateArray())
        {
            using var doc = JsonDocument.Parse(await ReadMaybeGzipAsync(page.GetProperty("@id").GetString()!));
            foreach (var pkg in doc.RootElement.EnumerateObject())
            {
                if (!db.TryGetValue(pkg.Name, out var list))
                    db[pkg.Name] = list = [];
                foreach (var v in pkg.Value.EnumerateArray())
                    list.Add(new VulnRange(
                        v.GetProperty("url").GetString() ?? "",
                        v.TryGetProperty("severity", out var s) ? s.GetInt32() : -1,
                        v.GetProperty("versions").GetString() ?? ""));
            }
        }
        return db;
    }

    // Les pages de vulnérabilités sont du gzip brut, sans en-tête Content-Encoding.
    static async Task<byte[]> ReadMaybeGzipAsync(string url)
    {
        var bytes = await Http.GetByteArrayAsync(url);
        if (bytes.Length < 2 || bytes[0] != 0x1f || bytes[1] != 0x8b) return bytes;
        using var gz = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        await gz.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>Teste une version contre une plage NuGet : "[1.0,2.0)", "(, 1.5.0)", "[1.2.3]".</summary>
    public static bool InRange(string range, string version)
    {
        range = range.Trim();
        if (range.Length == 0 || !SemVersion.TryParse(version, out var v)) return false;
        if (range[0] is not ('[' or '('))
            return SemVersion.TryParse(range, out var min) && v.CompareTo(min) >= 0;
        var parts = range[1..^1].Split(',');
        if (parts.Length == 1)
            return SemVersion.TryParse(parts[0], out var exact) && v.CompareTo(exact) == 0;
        string lo = parts[0].Trim(), hi = parts[1].Trim();
        if (lo.Length > 0)
        {
            if (!SemVersion.TryParse(lo, out var l)) return false;
            int c = v.CompareTo(l);
            if (c < 0 || (c == 0 && range[0] == '(')) return false;
        }
        if (hi.Length > 0)
        {
            if (!SemVersion.TryParse(hi, out var h)) return false;
            int c = v.CompareTo(h);
            if (c > 0 || (c == 0 && range[^1] == ')')) return false;
        }
        return true;
    }

    public static async Task<long> GetDownloadsAsync(string id)
    {
        try
        {
            var url = $"https://azuresearch-usnc.nuget.org/query?id={Uri.EscapeDataString(id)}&take=1";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.GetArrayLength() > 0)
            {
                var first = data[0];
                if (first.TryGetProperty("totalDownloads", out var td))
                    return td.GetInt64();
            }
        }
        catch
        {
            // Le compteur est un bonus : pas bloquant s'il échoue.
        }
        return -1;
    }
}
