using NuKeeper.Tui;

namespace NuKeeper;

public enum AppMode { Select, Packages, Detail, Loading }

public enum PkgFilter { All, Outdated, Vulnerable }

/// <summary>Machine à états de l'UI : sélection du projet → tableau → détail.</summary>
public sealed partial class NuKeeperApp
{
    readonly string _root;

    AppMode _mode = AppMode.Select;
    List<string> _projects = [];
    int _projSel;

    ProjectData? _data;
    int _sel;
    PkgFilter _filter = PkgFilter.All;

    Package? _detail;
    List<string> _versions = [];
    Dictionary<string, List<NuGetFeed.VulnRange>> _verVulns = new(StringComparer.OrdinalIgnoreCase);
    int _verSel;
    string _detailInfo = "";

    string _status = "";
    bool _statusOk = true;

    Task? _loading;
    string _loadMsg = "";

    bool _help;
    string _confirmText = "";
    string _confirmLoadMsg = "";
    Func<Task>? _confirmAction;

    void Ask(string question, string loadMsg, Func<Task> action)
    {
        _confirmText = question;
        _confirmLoadMsg = loadMsg;
        _confirmAction = action;
    }

    public NuKeeperApp(string root) => _root = root;

    public async Task RunAsync()
    {
        _projects = DotNetCli.FindProjects(_root);
        using var term = new Terminal();
        while (true)
        {
            Render(term);
            if (_loading is not null)
            {
                try { await Task.WhenAny(_loading, Task.Delay(250)); }
                catch (OperationCanceledException) { return; }
                if (!_loading.IsCompleted) continue;
                if (_loading.IsFaulted)
                {
                    _status = "erreur : " + FirstLine(_loading.Exception!.GetBaseException().Message);
                    _statusOk = false;
                    _mode = _data is null ? AppMode.Select : AppMode.Packages;
                }
                _loading = null;
                while (Console.KeyAvailable) Console.ReadKey(intercept: true); // touches tapées pendant le travail
                continue;
            }
            var key = term.ReadKey();
            if (key.KeyChar == '\u0003') return;
            _status = "";
            if (_help)
            {
                _help = false;
                continue;
            }
            if (_confirmAction is not null)
            {
                var action = _confirmAction;
                _confirmAction = null;
                if (key.Key == ConsoleKey.Enter || char.ToLowerInvariant(key.KeyChar) is 'o' or 'y')
                {
                    _status = "";
                    _mode = AppMode.Loading;
                    _loadMsg = _confirmLoadMsg;
                    _loading = action();
                }
                else
                {
                    _status = "annulé";
                    _statusOk = true;
                }
                continue;
            }
            if (key.KeyChar is '?' || key.Key == ConsoleKey.F1)
            {
                _help = true;
                continue;
            }
            if (key.Key == ConsoleKey.Escape)
            {
                // Échap = retour arrière : détail → liste → choix → sortie.
                switch (_mode)
                {
                    case AppMode.Detail: _mode = AppMode.Packages; break;
                    case AppMode.Packages: _mode = AppMode.Select; break;
                    default: return;
                }
                continue;
            }
            switch (_mode)
            {
                case AppMode.Select: if (HandleSelect(key)) return; break;
                case AppMode.Packages: if (HandlePackages(key)) return; break;
                case AppMode.Detail: if (HandleDetail(key)) return; break;
            }
        }
    }

    // ---------- Sélection du projet ----------

    bool HandleSelect(ConsoleKeyInfo key)
    {
        int n = _projects.Count;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (_projSel > 0) _projSel--;
                break;
            case ConsoleKey.DownArrow:
                if (_projSel < n - 1) _projSel++;
                break;
            case ConsoleKey.F5:
                _projects = DotNetCli.FindProjects(_root);
                _projSel = Math.Min(_projSel, Math.Max(0, _projects.Count - 1));
                break;
            case ConsoleKey.Enter when n > 0:
                _mode = AppMode.Loading;
                _loadMsg = $"analyse de {Path.GetFileName(_projects[_projSel])}…";
                _loading = LoadProject(_projects[_projSel]);
                break;
        }
        return key.KeyChar == 'q';
    }

    // ---------- Tableau des paquets ----------

    List<Package> Visible() => Visible(_filter);

    List<Package> Visible(PkgFilter filter)
    {
        var dict = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        if (_data is not null)
            foreach (var fw in _data.Frameworks.Values)
                foreach (var p in fw.Packages.Values)
                {
                    if (!dict.TryGetValue(p.Id, out var acc))
                        dict[p.Id] = acc = new Package { Id = p.Id };
                    acc.IsDirect |= p.IsDirect;
                    if (p.Requested.Length > 0) acc.Requested = p.Requested;
                    if (p.Resolved.Length > 0 && (acc.Resolved.Length == 0 || acc.CompareWith(p.Resolved) < 0))
                        acc.Resolved = p.Resolved;
                    if (p.Latest.Length > 0 && (acc.Latest.Length == 0 || acc.CompareWith(p.Latest) < 0))
                        acc.Latest = p.Latest;
                    foreach (var v in p.Vulns)
                        if (!acc.Vulns.Any(x => x.AdvisoryUrl == v.AdvisoryUrl))
                            acc.Vulns.Add(v);
                }
        var list = dict.Values.Where(p => filter switch
        {
            PkgFilter.Outdated => p.IsOutdated,
            PkgFilter.Vulnerable => p.IsVulnerable,
            _ => true,
        }).ToList();
        list.Sort((a, b) => a.IsDirect == b.IsDirect ? string.Compare(a.Id, b.Id, true)
                                                     : (a.IsDirect ? -1 : 1));
        return list;
    }

    bool HandlePackages(ConsoleKeyInfo key)
    {
        var vis = Visible();
        int n = vis.Count;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (_sel > 0) _sel--;
                break;
            case ConsoleKey.DownArrow:
                if (_sel < n - 1) _sel++;
                break;
            case ConsoleKey.PageUp:
                _sel = Math.Max(0, _sel - 10);
                break;
            case ConsoleKey.PageDown:
                if (n > 0) _sel = Math.Min(n - 1, _sel + 10);
                break;
            case ConsoleKey.Home:
                _sel = 0;
                break;
            case ConsoleKey.End:
                _sel = Math.Max(0, n - 1);
                break;
            case ConsoleKey.F5:
                if (_data?.ProjectFile is not null)
                {
                    _mode = AppMode.Loading;
                    _loadMsg = "rechargement…";
                    _loading = LoadProject(_data.ProjectFile);
                }
                break;
        }
        if (key.KeyChar == 'q')
        {
            _mode = AppMode.Select;
            return false;
        }
        if (n == 0) return false;
        _sel = Math.Min(_sel, n - 1);
        var pkg = vis[_sel];
        switch (key.KeyChar)
        {
            case '1': _filter = PkgFilter.All; _sel = 0; break;
            case '2': _filter = PkgFilter.Outdated; _sel = 0; break;
            case '3': _filter = PkgFilter.Vulnerable; _sel = 0; break;
            case 't':
                _filter = _filter switch
                {
                    PkgFilter.All => PkgFilter.Outdated,
                    PkgFilter.Outdated => PkgFilter.Vulnerable,
                    _ => PkgFilter.All,
                };
                _sel = 0;
                break;
            case 'u':
                string target = pkg.Latest.Length > 0 ? pkg.Latest : "la dernière version stable";
                Ask($"Mettre à jour {pkg.Id} {pkg.Resolved} → {target} ?", $"mise à jour de {pkg.Id}…", () => UpdateToLatest(pkg));
                break;
            case 'x':
                if (pkg.IsDirect)
                {
                    Ask($"Supprimer {pkg.Id} du projet ?", $"suppression de {pkg.Id}…", () => RemovePackage(pkg));
                }
                else
                {
                    _status = "les paquets transitifs ne se suppriment pas (supprimez la source)";
                    _statusOk = true;
                }
                break;
            case 'd':
                OpenDetail(pkg);
                break;
        }
        if (key.Key == ConsoleKey.Enter)
            OpenDetail(pkg);
        return false;
    }

    bool HandleDetail(ConsoleKeyInfo key)
    {
        int n = _versions.Count;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (_verSel > 0) _verSel--;
                break;
            case ConsoleKey.DownArrow:
                if (_verSel < n - 1) _verSel++;
                break;
            case ConsoleKey.PageUp:
                _verSel = Math.Max(0, _verSel - 10);
                break;
            case ConsoleKey.PageDown:
                _verSel = Math.Max(0, Math.Min(n - 1, _verSel + 10));
                break;
            case ConsoleKey.Home:
                _verSel = 0;
                break;
            case ConsoleKey.End:
                _verSel = Math.Max(0, n - 1);
                break;
        }
        if (key.KeyChar == 'q')
        {
            _mode = AppMode.Packages;
            return false;
        }
        if (n > 0 && (key.Key == ConsoleKey.Enter || key.KeyChar == 'a'))
        {
            string ver = _versions[_verSel];
            var pkg = _detail!;
            if (ver.Equals(pkg.Resolved, StringComparison.OrdinalIgnoreCase))
            {
                _status = $"{pkg.Id} est déjà en {ver}";
                _statusOk = true;
                return false;
            }
            string warn = _verVulns.ContainsKey(ver) ? "  ⚠ VERSION VULNÉRABLE" : "";
            Ask($"Passer {pkg.Id} {pkg.Resolved} → {ver} ?{warn}", $"mise à jour {pkg.Id} → {ver}…", () => UpdateToVersion(pkg, ver));
            return false;
        }
        return false;
    }
}
