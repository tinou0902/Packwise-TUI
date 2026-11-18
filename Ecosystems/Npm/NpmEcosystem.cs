using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Packwise;

/// <summary>
/// npm : projets package.json pilotés par le CLI `npm` (ls, outdated, audit, install, uninstall). Un membre de
/// workspaces passe par `--workspace` depuis la racine. Les projets pnpm et Yarn sont en lecture seule.
/// </summary>
public sealed class NpmEcosystem : IEcosystem
{
    public string Label => "npm";
    public string DirectHint => "déclaré dans le package.json";

    public bool IsProjectFile(string fileName) => fileName == "package.json";

    public string ProjectName(string projectFile) => Path.GetFileName(Path.GetDirectoryName(projectFile)) ?? projectFile;

    static readonly Dictionary<string, string> Env = new()
    {
        ["npm_config_update_notifier"] = "false",
        ["npm_config_fund"] = "false",
        ["NO_COLOR"] = "1",
    };

    // npm est un lanceur .cmd sous Windows : chaque argument est validé avant d'arriver ici.
    static Task<Proc.Result> Run(string cwd, params string[] args) =>
        Proc.RunAsync(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", args, cwd, Env);

    /// <summary>Où et comment lancer npm pour un projet.</summary>
    sealed record Context(string Cwd, NpmManifest Manifest, string[] WorkspaceArgs, string? Member, string PackageManager);

    static Context ContextOf(string projectFile)
    {
        var dir = Path.GetDirectoryName(projectFile)!;
        var manifest = NpmManifest.Read(projectFile);
        var root = NpmWorkspace.FindRoot(dir);
        if (root is not { } r)
            return new Context(dir, manifest, [], null, NpmWorkspace.DetectPackageManager(dir, manifest));
        var relative = Path.GetRelativePath(r.Dir, dir).Replace(Path.DirectorySeparatorChar, '/');
        if (!System.Text.RegularExpressions.Regex.IsMatch(relative, @"^[\w./@~-]+$"))
            throw new Exception($"Nom de dossier de workspace non pris en charge : {relative}");
        return new Context(r.Dir, manifest, ["--workspace", relative], manifest.Name ?? Path.GetFileName(dir),
            NpmWorkspace.DetectPackageManager(r.Dir, r.Manifest));
    }

    static bool IsJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; }
        catch (JsonException) { return false; }
    }

    public async Task<LoadResult> LoadAsync(string projectFile)
    {
        var ctx = ContextOf(projectFile);
        if (ctx.PackageManager != "npm") return await LoadReadOnly(projectFile, ctx);

        // Code 1 si des paquets manquent ou sont invalides : l'arbre est quand même produit.
        var ls = await Run(ctx.Cwd, ["ls", "--json", "--all", .. ctx.WorkspaceArgs]);
        if (!IsJson(ls.Stdout))
        {
            ls.EnsureSuccess();
            throw new Exception("Sortie de npm ls inattendue.");
        }
        var data = NpmModel.ParseTree(ls.Stdout, ctx.Manifest, ctx.Member, this, projectFile);

        // `npm outdated` sort en 1 quand un paquet est en retard, `npm audit` quand un paquet est vulnérable.
        var res = await Task.WhenAll(
            Run(ctx.Cwd, ["outdated", "--json", .. ctx.WorkspaceArgs]),
            Run(ctx.Cwd, ["audit", "--json", .. ctx.WorkspaceArgs]));
        var warnings = new List<string>();
        try
        {
            if (res[0].ExitCode > 1) throw new Exception();
            NpmModel.ApplyOutdated(data, res[0].Stdout, ctx.Member ?? ctx.Manifest.Name);
        }
        catch
        {
            warnings.Add("versions récentes indisponibles");
        }
        try
        {
            if (res[1].ExitCode > 1) throw new Exception();
            NpmModel.ApplyAudit(data, res[1].Stdout);
        }
        catch
        {
            warnings.Add("vulnérabilités indisponibles"); // Pas de package-lock, hors ligne, registre sans audit…
        }
        return new LoadResult(data, warnings);
    }

    /// <summary>
    /// Projets pnpm et Yarn : paquets déclarés, versions installées lues dans node_modules, dernières versions depuis le
    /// registre. Ni transitifs ni vulnérabilités (leurs CLI ne sont pas pilotés).
    /// </summary>
    async Task<LoadResult> LoadReadOnly(string projectFile, Context ctx)
    {
        var data = NpmModel.Declared(ctx.Manifest, this, projectFile);
        data.ReadOnly = $"géré par {ctx.PackageManager}";
        var dir = Path.GetDirectoryName(projectFile)!;
        var sources = await SourcesAsync(ctx.Cwd);
        bool offline = false;
        await Parallel.ForEachAsync(NpmModel.Packages(data).Values, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (pkg, _) =>
        {
            foreach (var b in new[] { dir, ctx.Cwd }.Distinct())
            {
                try
                {
                    var installed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(b, "node_modules", pkg.Id, "package.json")));
                    using var __ = installed;
                    if (installed.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        pkg.Resolved = v.GetString()!;
                        break;
                    }
                }
                catch
                {
                    // Pas installé ici.
                }
            }
            try
            {
                var latest = (await NpmRegistry.DistTagsAsync(NpmRegistry.RegistryFor(pkg.Id, sources), pkg.Id)).GetValueOrDefault("latest", "");
                if (latest.Length > 0 && latest != pkg.Resolved) pkg.Latest = latest;
            }
            catch
            {
                offline = true;
            }
        });
        var warnings = new List<string> { $"lecture seule : {data.ReadOnly}" };
        if (offline) warnings.Insert(0, "versions récentes indisponibles");
        return new LoadResult(data, warnings);
    }

    static readonly ConcurrentDictionary<string, Task<List<NpmRegistry.Source>>> Sources = new();

    /// <summary>Registres configurés pour un dossier (fichiers .npmrc, environnement), lus une fois par session.</summary>
    static Task<List<NpmRegistry.Source>> SourcesAsync(string cwd) => Sources.GetOrAdd(cwd, async dir =>
    {
        try
        {
            var r = await Run(dir, "config", "list", "--json");
            return NpmRegistry.ParseConfig(r.EnsureSuccess().Stdout);
        }
        catch
        {
            return [new NpmRegistry.Source(null, NpmRegistry.Npmjs)];
        }
    });

    async Task<string> RegistryOf(ProjectData project, string id) =>
        NpmRegistry.RegistryFor(id, await SourcesAsync(ContextOf(project.ProjectFile).Cwd));

    public async Task<string> LatestStableAsync(ProjectData project, Package pkg) =>
        (await NpmRegistry.DistTagsAsync(await RegistryOf(project, pkg.Id), pkg.Id)).GetValueOrDefault("latest", "");

    public async Task<PackageDetail> GetDetailAsync(ProjectData project, Package pkg)
    {
        var registry = await RegistryOf(project, pkg.Id);
        using var doc = await NpmRegistry.PackumentAsync(registry, pkg.Id);
        var root = doc.RootElement;
        var detail = new PackageDetail();
        if (!root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object)
            return detail;

        foreach (var v in versions.EnumerateObject())
        {
            detail.Versions.Add(v.Name);
            if (v.Value.TryGetProperty("deprecated", out var dep) && dep.ValueKind is JsonValueKind.String or JsonValueKind.True)
                detail.Warnings[v.Name] = "dépréciée";
        }
        detail.Versions.Sort((a, b) => -Compare(a, b));

        // Métadonnées de la version résolue, sinon de la dernière.
        var latest = root.TryGetProperty("dist-tags", out var tags) && tags.TryGetProperty("latest", out var l) ? l.GetString() ?? "" : "";
        if (!versions.TryGetProperty(pkg.Resolved, out var meta) && !versions.TryGetProperty(latest, out meta))
            return detail;
        detail.Info = BuildInfoText(meta, pkg.Id, NpmRegistry.IsNpmjs(registry));
        return detail;
    }

    static int Compare(string a, string b) =>
        SemVersion.TryParse(a, out var x) && SemVersion.TryParse(b, out var y) && x.CompareTo(y) is var c && c != 0
            ? c
            : string.CompareOrdinal(a, b);

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    // "Nom <mail> (url)" ou { name }.
    static string Person(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return "";
        var s = p.ValueKind == JsonValueKind.String ? p.GetString()! : Str(p, "name");
        int cut = s.IndexOfAny(['<', '(']);
        return (cut >= 0 ? s[..cut] : s).Trim();
    }

    static string BuildInfoText(JsonElement v, string id, bool onNpmjs)
    {
        var sb = new StringBuilder();
        var description = Str(v, "description");
        if (description.Length > 0) sb.AppendLine(description.Replace('\n', ' ').Replace('\r', ' ').Trim());
        if (v.TryGetProperty("deprecated", out var dep) && dep.ValueKind == JsonValueKind.String)
            sb.AppendLine("⚠ Dépréciée : " + dep.GetString());

        var repo = v.TryGetProperty("repository", out var r)
            ? (r.ValueKind == JsonValueKind.String ? r.GetString()! : Str(r, "url")) : "";
        repo = repo.Replace("git+", "").Replace("git://", "https://");
        if (repo.EndsWith(".git")) repo = repo[..^4];
        var license = v.TryGetProperty("license", out var lic)
            ? (lic.ValueKind == JsonValueKind.String ? lic.GetString()! : Str(lic, "type")) : "";
        var homepage = Str(v, "homepage");

        var meta = new StringBuilder();
        void Add(string label, string value)
        {
            if (value.Length == 0) return;
            if (meta.Length > 0) meta.Append("   ");
            meta.Append(label).Append(": ").Append(value);
        }
        Add("Auteur", Person(v, "author"));
        Add("Licence", license);
        Add("Projet", homepage.Length > 0 ? homepage : repo);
        if (onNpmjs) Add("npm", NpmRegistry.NpmjsPage(id));
        if (meta.Length > 0) sb.AppendLine(meta.ToString());

        foreach (var group in new[] { "dependencies", "optionalDependencies", "peerDependencies" })
        {
            if (!v.TryGetProperty(group, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
            var list = deps.EnumerateObject().Select(d => d.Name + " " + (d.Value.ValueKind == JsonValueKind.String ? d.Value.GetString() : "")).ToList();
            if (list.Count == 0) continue;
            sb.AppendLine($"Dépendances [{group}]:");
            sb.AppendLine("    " + string.Join(", ", list));
        }
        return sb.ToString();
    }

    static void EnsureWritable(Context ctx)
    {
        if (ctx.PackageManager != "npm")
            throw new Exception($"Ce projet est géré par {ctx.PackageManager}, que Packwise ne pilote pas encore : utilisez {ctx.PackageManager} pour modifier ses paquets.");
    }

    /// <summary>Installe ou met à jour un paquet en gardant son champ de dépendances et le préfixe de sa plage.</summary>
    public async Task AddAsync(ProjectData project, string id, string version)
    {
        var ctx = ContextOf(project.ProjectFile);
        EnsureWritable(ctx);
        if (!NpmManifest.IsPackageName(id) || !NpmManifest.IsVersion(version))
            throw new Exception($"Paquet ou version invalide : {id}@{version}");
        var declared = ctx.Manifest.Declared().GetValueOrDefault(id);
        var field = declared?.Field ?? "dependencies";
        // On installe la version exacte (une plage résoudrait la plus haute), enregistrée avec le préfixe gardé.
        var prefix = NpmManifest.SavePrefix(declared?.Range);
        var save = prefix.Length > 0 ? $"--save-prefix={prefix}" : "--save-exact";
        (await Run(ctx.Cwd, ["install", $"{id}@{version}", NpmManifest.SaveFlag(field), save, "--no-audit", .. ctx.WorkspaceArgs])).EnsureSuccess();
    }

    public async Task RemoveAsync(ProjectData project, string id)
    {
        var ctx = ContextOf(project.ProjectFile);
        EnsureWritable(ctx);
        if (!NpmManifest.IsPackageName(id)) throw new Exception($"Nom de paquet invalide : {id}");
        (await Run(ctx.Cwd, ["uninstall", id, "--no-audit", .. ctx.WorkspaceArgs])).EnsureSuccess();
    }
}
