namespace Packwise;

using System.Text.Json;

/// <summary>Parseur des JSON produits par `dotnet list package` (et variantes).</summary>
public static class ListJson
{
    // { projects: [{ project:{...}, frameworks:[ { framework:"net10.0",
    //   topLevelPackages:[{ id, resolvedVersion, requestedVersion, ... }], transitivePackages:[...] } ] } ] }
    public static ProjectData Parse(string json, IEcosystem ecosystem, string projectFile)
    {
        var data = new ProjectData(ecosystem, projectFile);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("projects", out var projects))
            throw new InvalidDataException("JSON 'dotnet list' invalide (propriété 'projects' absente).");

        foreach (var proj in projects.EnumerateArray())
        {
            if (!proj.TryGetProperty("frameworks", out var fws))
                continue;
            foreach (var fw in fws.EnumerateArray())
            {
                var fp = new FrameworkPackages
                {
                    Framework = fw.TryGetProperty("framework", out var fwName) ? fwName.GetString()! : "?"
                };
                if (fw.TryGetProperty("topLevelPackages", out var top))
                    foreach (var p in top.EnumerateArray())
                        AddPkg(fp, p, direct: true);
                if (fw.TryGetProperty("transitivePackages", out var tr))
                    foreach (var p in tr.EnumerateArray())
                        AddPkg(fp, p, direct: false);
                data.Frameworks[fp.Framework] = fp;
            }
        }
        return data;
    }

    // JSON `--outdated` : mêmes champs + latestVersion sur les paquets en retard.
    public static void ApplyOutdated(ProjectData data, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!TryGetProjects(doc, out var projects)) return;
        foreach (var proj in projects.EnumerateArray())
        {
            if (!proj.TryGetProperty("frameworks", out var fws)) continue;
            foreach (var fw in fws.EnumerateArray())
            {
                if (!fw.TryGetProperty("framework", out var fwName)) continue;
                if (!data.Frameworks.TryGetValue(fwName.GetString()!, out var fp)) continue;
                if (!fw.TryGetProperty("topLevelPackages", out var top)) continue;
                foreach (var p in top.EnumerateArray())
                {
                    if (!p.TryGetProperty("id", out var id)) continue;
                    if (!p.TryGetProperty("latestVersion", out var latest)) continue;
                    string lv = latest.ValueKind == JsonValueKind.String ? latest.GetString()! : "";
                    if (lv.Length == 0) continue;
                    if (fp.Packages.TryGetValue(id.GetString()!, out var pkg))
                        pkg.Latest = lv;
                }
            }
        }
    }

    // JSON `--vulnerable --include-transitive` :
    // vulnerabilities: [{ severity, vulnerabilityId, status, advisoryUrl, ... }]
    public static void ApplyVulnerable(ProjectData data, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!TryGetProjects(doc, out var projects)) return;
        foreach (var proj in projects.EnumerateArray())
        {
            if (!proj.TryGetProperty("frameworks", out var fws)) continue;
            foreach (var fw in fws.EnumerateArray())
            {
                if (!fw.TryGetProperty("framework", out var fwName)) continue;
                if (!data.Frameworks.TryGetValue(fwName.GetString()!, out var fp)) continue;
                foreach (string section in new[] { "topLevelPackages", "transitivePackages" })
                {
                    if (!fw.TryGetProperty(section, out var list)) continue;
                    foreach (var p in list.EnumerateArray())
                    {
                        if (!p.TryGetProperty("id", out var idEl)) continue;
                        string id = idEl.GetString()!;
                        if (!fp.Packages.TryGetValue(id, out var pkg))
                            fp.Packages[id] = pkg = new Package { Id = id };
                        if (!p.TryGetProperty("vulnerabilities", out var vulns)) continue;
                        foreach (var v in vulns.EnumerateArray())
                        {
                            string sev = v.TryGetProperty("severity", out var s)
                                ? s.GetString() ?? "moderate" : "moderate";
                            string url = v.TryGetProperty("advisoryurl", out var u) || v.TryGetProperty("advisoryUrl", out u)
                                ? u.GetString() ?? "" : "";
                            if (!pkg.Vulns.Any(x => x.AdvisoryUrl == url))
                                pkg.Vulns.Add(new Vuln(sev, url));
                        }
                    }
                }
            }
        }
    }

    static bool TryGetProjects(JsonDocument doc, out JsonElement projects)
    {
        projects = default;
        return doc.RootElement.TryGetProperty("projects", out projects);
    }

    static void AddPkg(FrameworkPackages fp, JsonElement p, bool direct)
    {
        if (!p.TryGetProperty("id", out var name)) return;
        string id = name.GetString()!;
        string req = p.TryGetProperty("requestedVersion", out var v) ? v.GetString() ?? "" : "";
        string res = p.TryGetProperty("resolvedVersion", out var r) ? r.GetString()! : "";
        if (!fp.Packages.TryGetValue(id, out var pkg))
            fp.Packages[id] = pkg = new Package { Id = id };
        if (direct)
        {
            pkg.IsDirect = true;
            if (req.Length > 0 && req != "*") pkg.Requested = req;
        }
        if (res.Length > 0 &&
            (pkg.Resolved.Length == 0 || pkg.CompareWith(res) < 0))
            pkg.Resolved = res;
    }
}
