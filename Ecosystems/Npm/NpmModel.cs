using System.Text.Json;

namespace Packwise;

/// <summary>Parsing des JSON du CLI npm (`ls`, `outdated`, `audit`) vers le modèle. Pur.</summary>
public static class NpmModel
{
    public static Dictionary<string, Package> Packages(ProjectData data) => data.Frameworks[""].Packages;

    /// <summary>Paquets directs déclarés par le manifeste, non résolus.</summary>
    public static ProjectData Declared(NpmManifest manifest, IEcosystem ecosystem, string projectFile)
    {
        var data = new ProjectData(ecosystem, projectFile);
        var group = new FrameworkPackages();
        foreach (var d in manifest.Declared().Values)
            group.Packages[d.Id] = new Package { Id = d.Id, Requested = d.Range, IsDirect = true, Kind = NpmManifest.Kind(d.Field) };
        data.Frameworks[""] = group;
        return data;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    static bool IsMissing(JsonElement e) => e.TryGetProperty("missing", out var m) && m.ValueKind == JsonValueKind.True;

    static IEnumerable<JsonProperty> Children(JsonElement node) =>
        node.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object ? deps.EnumerateObject() : [];

    /// <summary>
    /// Paquets d'un projet depuis `npm ls --json --all` : les dépendances du manifeste sont directes (avec leur plage
    /// déclarée), le reste de l'arbre est transitif (plus haute version gardée). `member` : nom du workspace dont lire le
    /// sous-arbre quand l'arbre est celui de la racine (`--workspace`). Les paquets locaux et liés sont ignorés.
    /// </summary>
    public static ProjectData ParseTree(string json, NpmManifest manifest, string? member, IEcosystem ecosystem, string projectFile)
    {
        using var doc = JsonDocument.Parse(json);
        var node = doc.RootElement;
        if (member is not null && Str(node, "name") != member)
        {
            var sub = Children(node).Where(c => c.Name == member).Select(c => (JsonElement?)c.Value).FirstOrDefault();
            node = sub ?? throw new InvalidDataException($"Workspace {member} absent de l'arbre de dépendances npm.");
        }
        var data = Declared(manifest, ecosystem, projectFile);
        var packages = Packages(data);
        foreach (var child in Children(node))
            if (packages.TryGetValue(child.Name, out var pkg) && !IsMissing(child.Value) && Str(child.Value, "version") is { Length: > 0 } v)
                pkg.Resolved = v;
        Walk(node, packages);
        return data;
    }

    static void Walk(JsonElement node, Dictionary<string, Package> packages)
    {
        foreach (var child in Children(node))
        {
            var c = child.Value;
            string version = Str(c, "version"), resolved = Str(c, "resolved");
            bool local = resolved.Length > 0 && !resolved.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
                                             && !resolved.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
            if (IsMissing(c) || version.Length == 0 || local) continue;
            if (!packages.TryGetValue(child.Name, out var pkg))
                packages[child.Name] = new Package { Id = child.Name, Resolved = version };
            else if (!pkg.IsDirect && pkg.CompareWith(version) < 0)
                pkg.Resolved = version;
            Walk(c, packages);
        }
    }

    /// <summary>
    /// Dernières versions depuis `npm outdated --json` (seuls les paquets en retard y figurent). Un paquet utilisé par
    /// plusieurs workspaces a une entrée par workspace : `dependent` choisit celle du projet.
    /// </summary>
    public static void ApplyOutdated(ProjectData data, string json, string? dependent)
    {
        using var doc = JsonDocument.Parse(json.Trim().Length == 0 ? "{}" : json);
        var packages = Packages(data);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var entries = prop.Value.ValueKind == JsonValueKind.Array ? prop.Value.EnumerateArray().ToList() : [prop.Value];
            var entry = entries.FirstOrDefault(e => dependent is not null && Str(e, "dependent") == dependent);
            if (entry.ValueKind == JsonValueKind.Undefined) entry = entries.FirstOrDefault();
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var latest = Str(entry, "latest");
            if (packages.TryGetValue(prop.Name, out var pkg) && pkg.IsDirect && latest.Length > 0 && NpmManifest.IsRegistryRange(latest))
                pkg.Latest = latest;
        }
    }

    /// <summary>Advisories de `npm audit --json` (format npm 7+), sur les paquets du projet qui en ont en propre.</summary>
    public static void ApplyAudit(ProjectData data, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("vulnerabilities", out var vulns) || vulns.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Sortie de npm audit inattendue.");
        var packages = Packages(data);
        foreach (var v in vulns.EnumerateObject())
        {
            if (!packages.TryGetValue(v.Name, out var pkg)) continue;
            pkg.Vulns.Clear();
            if (!v.Value.TryGetProperty("via", out var via) || via.ValueKind != JsonValueKind.Array) continue;
            // Les entrées texte nomment la dépendance par laquelle arrive la vulnérabilité : c'est elle qui la porte.
            foreach (var x in via.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Object) continue;
                var url = Str(x, "url");
                if (url.Length == 0 || pkg.Vulns.Any(e => e.AdvisoryUrl == url)) continue;
                var sev = Str(x, "severity");
                pkg.Vulns.Add(new Vuln(sev.Length == 0 ? "Unknown" : char.ToUpperInvariant(sev[0]) + sev[1..].ToLowerInvariant(), url));
            }
        }
    }
}
