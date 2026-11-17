namespace Packwise;

using System.Text.Json;

public sealed class Package
{
    public string Id = "";
    public string Requested = "";   // plage déclarée dans le csproj ("" si transitif)
    public string Resolved = "";    // version résolue
    public string Latest = "";      // dernière version stable connue ("" = à jour)
    public bool IsDirect;           // référence directe dans le csproj
    public List<Vuln> Vulns = [];

    public bool IsOutdated => Latest.Length > 0 && CompareWith(Latest) < 0;
    public bool IsVulnerable => Vulns.Count > 0;

    public int CompareWith(string other)
        => SemVersion.TryParse(Resolved, out var a) && SemVersion.TryParse(other, out var b)
            ? a.CompareTo(b)
            : string.Compare(Resolved, other, StringComparison.OrdinalIgnoreCase);
}

public sealed class Vuln(string severity, string advisoryUrl)
{
    public string Severity { get; } = severity;
    public string AdvisoryUrl { get; } = advisoryUrl;
}

/// <summary>Paquets résolus pour un framework donné (ex. net10.0).</summary>
public sealed class FrameworkPackages
{
    public string Framework = "";
    public Dictionary<string, Package> Packages = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>État d'un projet : fichier csproj + paquets par framework.</summary>
public sealed class ProjectData
{
    public string ProjectFile = "";
    public Dictionary<string, FrameworkPackages> Frameworks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FrameworkIds => [.. Frameworks.Keys];

    public Package? Find(string id)
    {
        foreach (var fw in Frameworks.Values)
            if (fw.Packages.TryGetValue(id, out var p))
                return p;
        return null;
    }
}

/// <summary>Version semver simplifiée, suffisante pour comparer des versions NuGet.</summary>
public struct SemVersion : IComparable<SemVersion>
{
    public SemVersion() { }  // exigé tant que les champs ont des initialisateurs
    public int Major, Minor, Patch;
    public string Pre = "";         // partie pre-release ("" = stable)
    public bool IsPrerelease => Pre.Length > 0;

    public static bool TryParse(string? s, out SemVersion v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim();

        int dash = s.IndexOf('-');
        int plus = dash >= 0 ? s.IndexOf('+', dash) : s.IndexOf('+');
        int mainEnd = dash >= 0 ? dash : (plus >= 0 ? plus : s.Length);

        string main = s[..mainEnd];
        string pre = "";
        if (dash >= 0)
            pre = plus >= 0 ? s[(dash + 1)..plus] : s[(dash + 1)..];

        var parts = main.Split('.');
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], out int maj) ||
            !int.TryParse(parts[1], out int min))
            return false;
        int pat = 0;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out pat))
            return false;

        v = new SemVersion { Major = maj, Minor = min, Patch = pat, Pre = pre.ToLowerInvariant() };
        return true;
    }

    public int CompareTo(SemVersion o)
    {
        int c = Major.CompareTo(o.Major); if (c != 0) return c;
        c = Minor.CompareTo(o.Minor); if (c != 0) return c;
        c = Patch.CompareTo(o.Patch); if (c != 0) return c;
        c = (IsPrerelease ? 0 : 1).CompareTo(o.IsPrerelease ? 0 : 1); if (c != 0) return c;
        if (IsPrerelease && o.IsPrerelease)
        {
            var a = Pre.Split('-');
            var b = (o.Pre ?? "").Split('-');
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                bool an = int.TryParse(a[i], out int av), bn = int.TryParse(b[i], out int bv);
                int pc = an && bn ? av.CompareTo(bv)
                          : an ? 1 : (bn ? -1 : string.CompareOrdinal(a[i], b[i]));
                if (pc != 0) return pc;
            }
            return a.Length.CompareTo(b.Length);
        }
        return 0;
    }

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? "-" + Pre : "");
}

/// <summary>Parseur des JSON produits par `dotnet list package` (et variantes).</summary>
public static class ListJson
{
    // { projects: [{ project:{...}, frameworks:[ { framework:"net10.0",
    //   topLevelPackages:[{ id, resolvedVersion, requestedVersion, ... }], transitivePackages:[...] } ] } ] }
    public static ProjectData Parse(string json)
    {
        var data = new ProjectData();
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
