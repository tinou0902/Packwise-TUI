namespace Packwise;

/// <summary>Actions longues : chargement du projet, mise à jour, suppression, détail.</summary>
public sealed partial class PackwiseApp
{
    // ---------- Chargement ----------

    async Task LoadProject(ProjectRef project)
    {
        try
        {
            var (data, warnings) = await project.Ecosystem.LoadAsync(project.File);
            if (_data?.ProjectFile != project.File)
            {
                _sel = 0;
                _filter = PkgFilter.All;
            }
            _data = data;
            _statusOk = warnings.Count == 0;
            int total = data.Frameworks.Values.SelectMany(f => f.Packages.Values).Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var all = Visible(PkgFilter.All);
            _status = $"{total} paquets — {all.Count(p => p.IsOutdated)} obsolètes, {all.Count(p => p.IsVulnerable)} vulnérables"
                      + string.Concat(warnings.Select(w => $" ({w})"));
            _mode = AppMode.Packages;
        }
        catch (Exception e)
        {
            _status = "erreur : " + FirstLine(e.Message);
            _statusOk = false;
            _mode = AppMode.Select;
        }
    }

    Task Reload(ProjectData data) => LoadProject(new ProjectRef(data.ProjectFile, data.Ecosystem));

    // ---------- Mise à jour ----------

    async Task UpdateToLatest(ProjectData data, Package pkg)
    {
        string version = pkg.Latest;
        if (version.Length == 0)
        {
            // Paquet pas signalé obsolète (info manquante) — on interroge le registre.
            version = await data.Ecosystem.LatestStableAsync(data, pkg);
        }
        if (version.Length == 0 || pkg.CompareWith(version) >= 0)
        {
            _status = $"{pkg.Id} est déjà à jour ({pkg.Resolved})";
            _statusOk = true;
            _mode = AppMode.Packages;
            return;
        }
        await UpdateToVersion(data, pkg, version);
    }

    async Task UpdateToVersion(ProjectData data, Package pkg, string version)
    {
        await data.Ecosystem.AddAsync(data, pkg.Id, version);
        _status = $"{pkg.Id} → {version}   (rechargement…)";
        _statusOk = true;
        await Reload(data);
    }

    // ---------- Suppression ----------

    async Task RemovePackage(ProjectData data, Package pkg)
    {
        try
        {
            await data.Ecosystem.RemoveAsync(data, pkg.Id);
        }
        catch (Exception e)
        {
            _status = "suppression impossible : " + FirstLine(e.Message);
            _statusOk = false;
            _mode = AppMode.Packages;
            return;
        }
        _status = $"{pkg.Id} supprimé   (rechargement…)";
        _statusOk = true;
        await Reload(data);
    }

    // ---------- Détail (versions + métadonnées du registre) ----------

    void OpenDetail(Package pkg)
    {
        if (_data is null) return;
        _mode = AppMode.Loading;
        _loadMsg = $"versions + métadonnées de {pkg.Id}…";
        _detail = pkg;
        _loading = LoadDetail(_data, pkg);
    }

    async Task LoadDetail(ProjectData data, Package pkg)
    {
        _versions = [];
        _verWarnings = [];
        _verSel = 0;
        _detailInfo = "";
        try
        {
            var detail = await data.Ecosystem.GetDetailAsync(data, pkg);
            _versions = detail.Versions;
            _verWarnings = detail.Warnings;
            _detailInfo = detail.Info;
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

    static string FirstLine(string s) => Proc.FirstLine(s);
}
