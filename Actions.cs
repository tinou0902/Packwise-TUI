namespace NuKeeper;

/// <summary>Actions longues : chargement du projet, mise à jour, suppression, détail.</summary>
public sealed partial class NuKeeperApp
{
    // ---------- Chargement ----------

    async Task LoadProject(string path)
    {
        try
        {
            var list = await DotNetCli.ListAsync(path);
            if (!list.Success && (list.Stdout + list.Stderr).Contains("assets file", StringComparison.OrdinalIgnoreCase))
            {
                // Projet jamais restauré : `dotnet list package` n'a rien à lire.
                await DotNetCli.RestoreAsync(path);
                list = await DotNetCli.ListAsync(path);
            }
            if (!list.Success)
                throw new Exception(FirstLine(list.Stderr.Trim() == "" ? list.Stdout : list.Stderr));
            var data = ListJson.Parse(list.Stdout);
            data.ProjectFile = path;

            var res = await Task.WhenAll(DotNetCli.ListOutdatedAsync(path), DotNetCli.ListVulnerableAsync(path));
            string warning = "";
            // Hors-ligne ou flux injoignable : on garde la liste, sans dernières versions / vulnérabilités.
            if (res[0].Success && res[0].Stdout.Length > 0) ListJson.ApplyOutdated(data, res[0].Stdout);
            else warning += " (versions récentes indisponibles)";
            if (res[1].Success && res[1].Stdout.Length > 0) ListJson.ApplyVulnerable(data, res[1].Stdout);
            else warning += " (vulnérabilités indisponibles)";

            if (_data?.ProjectFile != path)
            {
                _sel = 0;
                _filter = PkgFilter.All;
            }
            _data = data;
            _statusOk = warning.Length == 0;
            int total = data.Frameworks.Values.SelectMany(f => f.Packages.Values).Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var all = Visible(PkgFilter.All);
            _status = $"{total} paquets — {all.Count(p => p.IsOutdated)} obsolètes, {all.Count(p => p.IsVulnerable)} vulnérables{warning}";
            _mode = AppMode.Packages;
        }
        catch (Exception e)
        {
            _status = "erreur : " + FirstLine(e.Message);
            _statusOk = false;
            _mode = AppMode.Select;
        }
    }

    // ---------- Mise à jour ----------

    async Task UpdateToLatest(Package pkg)
    {
        string version = pkg.Latest;
        if (version.Length == 0)
        {
            // Paquet pas signalé obsolète (info manquante) — on interroge les flux.
            var versions = await NuGetFeed.GetVersionsAsync(pkg.Id);
            version = versions.FirstOrDefault(v => !SemVersion.TryParse(v, out var sv) || !sv.IsPrerelease) ?? "";
        }
        if (version.Length == 0 || pkg.CompareWith(version) >= 0)
        {
            _status = $"{pkg.Id} est déjà à jour ({pkg.Resolved})";
            _statusOk = true;
            _mode = AppMode.Packages;
            return;
        }
        await UpdateToVersion(pkg, version);
    }

    async Task UpdateToVersion(Package pkg, string version)
    {
        var project = _data?.ProjectFile ?? "";
        if (project.Length > 0)
        {
            var r = await DotNetCli.AddPackageAsync(project, pkg.Id, version);
            if (!r.Success)
                throw new Exception(FirstLine(r.Stderr.Trim() == "" ? r.Stdout : r.Stderr));
        }
        _status = $"{pkg.Id} → {version}   (rechargement…)";
        _statusOk = true;
        await LoadProject(project);
    }

    // ---------- Suppression ----------

    async Task RemovePackage(Package pkg)
    {
        var project = _data?.ProjectFile ?? "";
        var r = await DotNetCli.RemovePackageAsync(project, pkg.Id);
        if (!r.Success)
        {
            _status = $"suppression impossible : " + FirstLine(r.Stderr);
            _statusOk = false;
            _mode = AppMode.Packages;
            return;
        }
        _status = $"{pkg.Id} supprimé   (rechargement…)";
        _statusOk = true;
        await LoadProject(project);
    }

    // ---------- Détail (versions + métadonnées via API NuGet) ----------

    void OpenDetail(Package pkg)
    {
        _mode = AppMode.Loading;
        _loadMsg = $"versions + métadonnées de {pkg.Id}…";
        _detail = pkg;
        _loading = LoadDetail(pkg);
    }

    async Task LoadDetail(Package pkg)
    {
        _versions = [];
        _verSel = 0;
        _detailInfo = "";
        try
        {
            var vTask = NuGetFeed.GetVersionsAsync(pkg.Id);
            var iTask = NuGetFeed.GetInfoAsync(pkg.Id, pkg.Resolved);
            _versions = await vTask;
            _verVulns = [];
            try
            {
                var db = await NuGetFeed.GetVulnDbAsync();
                if (db.TryGetValue(pkg.Id, out var ranges))
                    foreach (var v in _versions)
                    {
                        var hit = ranges.Where(r => NuGetFeed.InRange(r.Range, v)).ToList();
                        if (hit.Count > 0) _verVulns[v] = hit;
                    }
            }
            catch
            {
                // Base d'advisories indisponible : on affiche juste les versions.
            }
            try
            {
                var info = await iTask;
                _detailInfo = BuildInfoText(info, pkg);
            }
            catch
            {
                // Les versions suffisent; les métadonnées sont un bonus.
            }
            int idx = _versions.FindIndex(v => v.Equals(pkg.Resolved, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _verSel = idx;
        }
        catch (Exception e)
        {
            _status = $"détail indisponible ({pkg.Id}) : " + FirstLine(e.Message);
            _statusOk = false;
        }
        _mode = AppMode.Detail;
    }

    static string BuildInfoText(NuGetFeed.PackageInfo info, Package pkg)
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
                sb.AppendLine("    " + string.Join(", ", deps.Select(d => d.Id + " " + d.Item2)));
            }
        }
        return sb.ToString();
    }

    static string FirstLine(string s)
    {
        foreach (var line in s.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t.Length > 240 ? t[..240] : t;
        }
        return s.Length > 240 ? s[..240] : s;
    }
}
