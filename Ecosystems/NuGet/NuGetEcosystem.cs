namespace Packwise;

/// <summary>NuGet : projets *.csproj pilotés par le CLI `dotnet`, détail via l'API de nuget.org.</summary>
public sealed class NuGetEcosystem : IEcosystem
{
    public string Label => "NuGet";
    public string DirectHint => "référencé dans le csproj";

    public bool IsProjectFile(string fileName) => fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    public string ProjectName(string projectFile) => Path.GetFileNameWithoutExtension(projectFile);

    public async Task<LoadResult> LoadAsync(string projectFile)
    {
        var list = await DotNetCli.ListAsync(projectFile);
        if (!list.Success && (list.Stdout + list.Stderr).Contains("assets file", StringComparison.OrdinalIgnoreCase))
        {
            // Projet jamais restauré : `dotnet list package` n'a rien à lire.
            await DotNetCli.RestoreAsync(projectFile);
            list = await DotNetCli.ListAsync(projectFile);
        }
        var data = ListJson.Parse(list.EnsureSuccess().Stdout, this, projectFile);

        var res = await Task.WhenAll(DotNetCli.ListOutdatedAsync(projectFile), DotNetCli.ListVulnerableAsync(projectFile));
        var warnings = new List<string>();
        // Hors-ligne ou flux injoignable : on garde la liste, sans dernières versions / vulnérabilités.
        if (res[0].Success && res[0].Stdout.Length > 0) ListJson.ApplyOutdated(data, res[0].Stdout);
        else warnings.Add("versions récentes indisponibles");
        if (res[1].Success && res[1].Stdout.Length > 0) ListJson.ApplyVulnerable(data, res[1].Stdout);
        else warnings.Add("vulnérabilités indisponibles");
        return new LoadResult(data, warnings);
    }

    public async Task<string> LatestStableAsync(ProjectData project, Package pkg)
    {
        var versions = await NuGetFeed.GetVersionsAsync(pkg.Id);
        return versions.FirstOrDefault(v => !SemVersion.TryParse(v, out var sv) || !sv.IsPrerelease) ?? "";
    }

    public async Task<PackageDetail> GetDetailAsync(ProjectData project, Package pkg)
    {
        var detail = new PackageDetail();
        var infoTask = NuGetFeed.GetInfoAsync(pkg.Id, pkg.Resolved);
        detail.Versions = await NuGetFeed.GetVersionsAsync(pkg.Id);
        try
        {
            var db = await NuGetFeed.GetVulnDbAsync();
            if (db.TryGetValue(pkg.Id, out var ranges))
                foreach (var v in detail.Versions)
                {
                    var hit = ranges.Where(r => NuGetFeed.InRange(r.Range, v)).ToList();
                    if (hit.Count > 0)
                        detail.Warnings[v] = $"vulnérable ({string.Join(", ", hit.Select(x => NuGetFeed.SeverityName(x.Severity)).Distinct())})";
                }
        }
        catch
        {
            // Base d'advisories indisponible : on affiche juste les versions.
        }
        try
        {
            detail.Info = BuildInfoText(await infoTask);
        }
        catch
        {
            // Les versions suffisent ; les métadonnées sont un bonus.
        }
        return detail;
    }

    static string BuildInfoText(NuGetFeed.PackageInfo info)
    {
        var sb = new System.Text.StringBuilder();
        if (info.Description.Length > 0)
            sb.AppendLine(info.Description.Replace('\n', ' ').Replace('\r', ' ').Trim());
        var meta = new System.Text.StringBuilder();
        if (info.Authors.Length > 0) meta.Append("Auteurs: ").Append(info.Authors);
        if (info.ProjectUrl.Length > 0) meta.Append("   Projet: ").Append(info.ProjectUrl);
        if (info.Downloads >= 0) meta.Append($"   Téléchargements: {info.Downloads:N0}");
        if (meta.Length > 0) sb.AppendLine(meta.ToString());

        // Dépendances de la version résolue — utile pour comprendre un paquet transitif.
        if (info.Dependencies.Count > 0)
        {
            sb.AppendLine("Dépendances:");
            foreach (var (fw, deps) in info.Dependencies)
            {
                if (deps.Count == 0) continue;
                sb.AppendLine($"  [{fw}]");
                sb.AppendLine("    " + string.Join(", ", deps.Select(d => d.Id + " " + d.Range)));
            }
        }
        return sb.ToString();
    }

    public async Task AddAsync(ProjectData project, string id, string version) =>
        (await DotNetCli.AddPackageAsync(project.ProjectFile, id, version)).EnsureSuccess();

    public async Task RemoveAsync(ProjectData project, string id) =>
        (await DotNetCli.RemovePackageAsync(project.ProjectFile, id)).EnsureSuccess();
}
