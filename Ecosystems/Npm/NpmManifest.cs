using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packwise;

/// <summary>Une dépendance déclarée dans un package.json (registre seulement).</summary>
public sealed record NpmDeclared(string Id, string Range, string Field);

/// <summary>package.json : champs de dépendances, workspaces, gestionnaire de paquets déclaré.</summary>
public sealed partial class NpmManifest
{
    public static readonly string[] Fields = ["dependencies", "devDependencies", "optionalDependencies", "peerDependencies"];

    public string? Name;
    /// <summary>"pnpm@9.1.0" (Corepack).</summary>
    public string? PackageManager;
    /// <summary>Motifs des workspaces (npm, Yarn) ; vide si ce n'est pas une racine de workspaces.</summary>
    public List<string> Workspaces = [];
    /// <summary>Champ → (nom → plage), dans l'ordre de <see cref="Fields"/>.</summary>
    public Dictionary<string, List<(string Id, string Range)>> Sections = [];

    public static NpmManifest Read(string file) => Parse(File.ReadAllText(file));

    public static NpmManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("package.json invalide.");
        var m = new NpmManifest
        {
            Name = Str(root, "name"),
            PackageManager = Str(root, "packageManager"),
        };
        if (root.TryGetProperty("workspaces", out var ws))
        {
            // Tableau de motifs, ou { packages: [...] } (Yarn classique).
            if (ws.ValueKind == JsonValueKind.Object && ws.TryGetProperty("packages", out var p)) ws = p;
            if (ws.ValueKind == JsonValueKind.Array)
                m.Workspaces = ws.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String && e.GetString()!.Trim().Length > 0)
                    .Select(e => e.GetString()!).ToList();
        }
        foreach (var field in Fields)
        {
            if (!root.TryGetProperty(field, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
            m.Sections[field] = deps.EnumerateObject()
                .Where(d => d.Value.ValueKind == JsonValueKind.String)
                .Select(d => (d.Name, d.Value.GetString()!)).ToList();
        }
        return m;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Dépendances du registre ; un paquet présent dans plusieurs champs garde le premier de <see cref="Fields"/>.</summary>
    public Dictionary<string, NpmDeclared> Declared()
    {
        var deps = new Dictionary<string, NpmDeclared>();
        foreach (var field in Fields)
            foreach (var (id, range) in Sections.GetValueOrDefault(field) ?? [])
                if (IsRegistryRange(range) && !deps.ContainsKey(id))
                    deps[id] = new NpmDeclared(id, range, field);
        return deps;
    }

    /// <summary>Badge d'un champ de dépendances (aucun pour les dépendances normales).</summary>
    public static string? Kind(string field) => field switch
    {
        "devDependencies" => "dev",
        "optionalDependencies" => "optional",
        "peerDependencies" => "peer",
        _ => null,
    };

    /// <summary>Option de `npm install` qui enregistre dans ce champ.</summary>
    public static string SaveFlag(string field) => field switch
    {
        "devDependencies" => "--save-dev",
        "optionalDependencies" => "--save-optional",
        "peerDependencies" => "--save-peer",
        _ => "--save-prod",
    };

    /// <summary>Plage résolue depuis le registre (pas un chemin local, une URL git, un alias ou le protocole workspace).</summary>
    public static bool IsRegistryRange(string range) => !ProtocolRe().IsMatch(range.Trim()) && !range.Contains('/');

    /// <summary>
    /// Préfixe d'enregistrement d'une version installée, celui de la plage actuelle : "^" et "~" sont gardés, une
    /// version exacte reste exacte (""). Les autres plages (et les nouvelles références) prennent le "^" de npm.
    /// </summary>
    public static string SavePrefix(string? current)
    {
        var r = current?.Trim() ?? "";
        if (PrefixedRe().IsMatch(r)) return r[..1];
        if (ExactRe().IsMatch(r)) return "";
        return "^";
    }

    /// <summary>Nom de paquet valide (vérifié avant d'atteindre un shell sous Windows).</summary>
    public static bool IsPackageName(string id) => id.Length <= 214 && NameRe().IsMatch(id);

    /// <summary>Version exacte valide (vérifiée avant d'atteindre un shell sous Windows).</summary>
    public static bool IsVersion(string version) => VersionRe().IsMatch(version);

    [GeneratedRegex("^[a-z][a-z0-9+]*:", RegexOptions.IgnoreCase)] private static partial Regex ProtocolRe();
    [GeneratedRegex(@"^[~^]\d")] private static partial Regex PrefixedRe();
    [GeneratedRegex(@"^=?v?\d+\.\d+\.\d+\S*$")] private static partial Regex ExactRe();
    [GeneratedRegex(@"^(@[a-z0-9][\w.~-]*/)?[a-z0-9][\w.~-]*$", RegexOptions.IgnoreCase)] private static partial Regex NameRe();
    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.+-]*)?$")] private static partial Regex VersionRe();
}
