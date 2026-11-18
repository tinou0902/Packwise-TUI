namespace Packwise;

/// <summary>
/// Un gestionnaire de paquets (NuGet, npm) : ses fichiers projet, le chargement des paquets d'un projet,
/// le détail d'un paquet et les modifications. Les erreurs remontent en exceptions au message lisible.
/// </summary>
public interface IEcosystem
{
    /// <summary>Nom affiché (NuGet, npm).</summary>
    string Label { get; }

    /// <summary>Où sont déclarées les références directes, pour l'aide et le détail.</summary>
    string DirectHint { get; }

    /// <summary>Vrai si ce nom de fichier est un projet de l'écosystème (ex. *.csproj, package.json).</summary>
    bool IsProjectFile(string fileName);

    /// <summary>Nom court d'un projet dans les listes.</summary>
    string ProjectName(string projectFile);

    /// <summary>Paquets directs et transitifs, dernières versions et vulnérabilités quand elles sont disponibles.</summary>
    Task<LoadResult> LoadAsync(string projectFile);

    /// <summary>Dernière version stable d'un paquet ("" si inconnue).</summary>
    Task<string> LatestStableAsync(ProjectData project, Package pkg);

    /// <summary>Versions publiées et métadonnées d'un paquet.</summary>
    Task<PackageDetail> GetDetailAsync(ProjectData project, Package pkg);

    /// <summary>Ajoute ou met à jour un paquet à une version exacte.</summary>
    Task AddAsync(ProjectData project, string id, string version);

    Task RemoveAsync(ProjectData project, string id);
}

/// <summary>Projet chargé + avertissements (ex. vulnérabilités indisponibles hors ligne).</summary>
public sealed record LoadResult(ProjectData Data, List<string> Warnings);

public sealed class PackageDetail
{
    /// <summary>Versions publiées, de la plus récente à la plus ancienne.</summary>
    public List<string> Versions = [];

    /// <summary>Versions à éviter → raison (ex. "vulnérable (High)", "dépréciée").</summary>
    public Dictionary<string, string> Warnings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Description, auteurs, dépendances… (texte libre, une info par ligne).</summary>
    public string Info = "";
}

/// <summary>Un projet trouvé sur le disque.</summary>
public sealed record ProjectRef(string File, IEcosystem Ecosystem)
{
    public string Name => Ecosystem.ProjectName(File);
}

public static class Discovery
{
    public static readonly IEcosystem[] Ecosystems = [new NuGetEcosystem(), new NpmEcosystem()];

    // Dossiers de build et de dépendances : jamais de projets à gérer dedans.
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", "bower_components" };

    // Dossier `packages` de l'ancien NuGet (packages.config) : un sous-dossier .nupkg par paquet. Un dossier
    // `packages` ordinaire (workspaces npm) est parcouru.
    static bool IsNuGetPackagesFolder(string dir)
    {
        if (!Path.GetFileName(dir).Equals("packages", StringComparison.OrdinalIgnoreCase)) return false;
        try { return Directory.EnumerateDirectories(dir).Any(d => Directory.EnumerateFiles(d, "*.nupkg").Any()); }
        catch { return false; }
    }

    /// <summary>
    /// Projets de tous les écosystèmes sous un dossier (4 niveaux, dossiers cachés et fichiers ignorés par git exclus),
    /// triés par chemin.
    /// </summary>
    public static List<ProjectRef> FindProjects(string startDir)
    {
        var found = new List<ProjectRef>();
        Search(startDir, found, depth: 4);
        var kept = GitIgnored.Without(found.Select(p => p.File).ToList()).ToHashSet(StringComparer.Ordinal);
        var results = found.Where(p => kept.Contains(p.File)).ToList();
        results.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.File, b.File));
        return results;
    }

    static void Search(string dir, List<ProjectRef> results, int depth)
    {
        if (depth < 0) return;
        string[] files;
        try { files = Directory.GetFiles(dir); }
        catch { return; }
        foreach (var f in files)
        {
            var eco = Ecosystems.FirstOrDefault(e => e.IsProjectFile(Path.GetFileName(f)));
            if (eco is not null) results.Add(new ProjectRef(f, eco));
        }
        string[] subdirs;
        try { subdirs = Directory.GetDirectories(dir); }
        catch { return; }
        foreach (var sub in subdirs)
        {
            var name = Path.GetFileName(sub);
            if (Skipped.Contains(name) || name.StartsWith('.') || IsNuGetPackagesFolder(sub))
                continue;
            Search(sub, results, depth - 1);
        }
    }
}
