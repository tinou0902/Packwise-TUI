namespace Packwise;

public sealed class Package
{
    public string Id = "";
    public string Requested = "";   // plage déclarée dans le projet ("" si transitif)
    public string Resolved = "";    // version résolue
    public string Latest = "";      // dernière version stable connue ("" = à jour)
    public bool IsDirect;           // référence directe dans le projet
    public string? Kind;            // type de dépendance npm : dev, optional, peer (null = normale)
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

/// <summary>Paquets résolus pour un framework donné (ex. net10.0) ; un seul groupe "" pour npm.</summary>
public sealed class FrameworkPackages
{
    public string Framework = "";
    public Dictionary<string, Package> Packages = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>État d'un projet : fichier projet (csproj, package.json) + paquets par framework.</summary>
public sealed class ProjectData(IEcosystem ecosystem, string projectFile)
{
    public IEcosystem Ecosystem { get; } = ecosystem;
    public string ProjectFile { get; } = projectFile;
    /// <summary>Raison de la lecture seule (ex. projet pnpm), null si modifiable.</summary>
    public string? ReadOnly;
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

/// <summary>Version semver simplifiée, suffisante pour comparer des versions NuGet et npm.</summary>
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
            var a = Pre.Split('.', '-');
            var b = (o.Pre ?? "").Split('.', '-');
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
