using System.Text;
using System.Text.RegularExpressions;

namespace Packwise;

/// <summary>Workspaces npm (motifs glob relatifs à la racine) et gestionnaire de paquets d'un dossier.</summary>
public static class NpmWorkspace
{
    /// <summary>Motif glob (`**`, `*`, `?`) sur un chemin relatif séparé par "/".</summary>
    public static Regex GlobToRegex(string glob)
    {
        var g = glob.Trim();
        if (g.StartsWith("./")) g = g[2..];
        g = g.TrimEnd('/');
        var re = new StringBuilder("^");
        for (int i = 0; i < g.Length; i++)
        {
            char c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
            {
                // "**/" : un nombre quelconque de dossiers ; "**" final : tout ce qui est en dessous.
                if (i + 2 < g.Length && g[i + 2] == '/') { re.Append("(?:[^/]+/)*"); i += 2; }
                else { re.Append(".*"); i += 1; }
            }
            else if (c == '*') re.Append("[^/]*");
            else if (c == '?') re.Append("[^/]");
            else re.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(re.Append('$').ToString());
    }

    /// <summary>Dit si un dossier (relatif à la racine, séparé par "/") est un workspace de ces motifs ("!" exclut).</summary>
    public static Func<string, bool> Matcher(IReadOnlyCollection<string> patterns)
    {
        var include = patterns.Where(p => !p.StartsWith('!')).Select(GlobToRegex).ToList();
        var exclude = patterns.Where(p => p.StartsWith('!')).Select(p => GlobToRegex(p[1..])).ToList();
        return dir => dir != "" && include.Any(r => r.IsMatch(dir)) && !exclude.Any(r => r.IsMatch(dir));
    }

    static string ToPosix(string p) => p.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Racine de workspaces dont `dir` est membre (null s'il n'est membre d'aucune).</summary>
    public static (string Dir, NpmManifest Manifest)? FindRoot(string dir)
    {
        // La première racine de workspaces rencontrée en remontant décide.
        for (var parent = Path.GetDirectoryName(dir); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            NpmManifest? manifest;
            try { manifest = NpmManifest.Read(Path.Combine(parent, "package.json")); }
            catch { continue; }
            if (manifest.Workspaces.Count == 0) continue;
            return Matcher(manifest.Workspaces)(ToPosix(Path.GetRelativePath(parent, dir))) ? (parent, manifest) : null;
        }
        return null;
    }

    static readonly (string File, string Manager)[] Lockfiles =
    [
        ("package-lock.json", "npm"),
        ("npm-shrinkwrap.json", "npm"),
        ("pnpm-lock.yaml", "pnpm"),
        ("yarn.lock", "yarn"),
    ];

    /// <summary>Gestionnaire d'un dossier : champ `packageManager`, puis le fichier de verrouillage ; npm par défaut.</summary>
    public static string DetectPackageManager(string dir, NpmManifest manifest)
    {
        var declared = Regex.Match(manifest.PackageManager ?? "", "^(npm|pnpm|yarn)@");
        if (declared.Success) return declared.Groups[1].Value;
        foreach (var (file, pm) in Lockfiles)
            if (File.Exists(Path.Combine(dir, file))) return pm;
        return "npm";
    }
}
