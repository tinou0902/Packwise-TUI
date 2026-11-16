using NuKeeper.Tui;

namespace NuKeeper;

/// <summary>Le dessin : chaque mode produit des lignes, le pied de page est commun.</summary>
public sealed partial class NuKeeperApp
{
    void Render(Terminal term)
    {
        var rows = new List<ScreenRow>();
        if (_mode != AppMode.Loading) _view = _mode;
        if (_help)
            RenderHelp(rows);
        else
        {
            rows.Add(new ScreenRow(T(" NuKeeper ", ScreenColor.Cyan, bold: true), T(Breadcrumb(), ScreenColor.Default)) { Highlight = true });
            // Pendant un chargement, on garde l'écran précédent : pas de saut visuel.
            switch (_view)
            {
                case AppMode.Select: RenderSelect(rows, term); break;
                case AppMode.Packages: RenderPackages(rows, term); break;
                case AppMode.Detail: RenderDetail(rows, term); break;
            }
        }
        int bottom = Math.Max(0, term.Height - 2);
        while (rows.Count < bottom)
            AddRow(rows, T(""));
        if (rows.Count > bottom) rows.RemoveRange(bottom, rows.Count - bottom);
        if (_confirmAction is not null)
        {
            AddRow(rows, T(_confirmText, ScreenColor.Yellow, bold: true));
            AddRow(rows, Key("o/Entrée"), T("confirmer   ", ScreenColor.Dim), Key("autre touche"), T("annuler", ScreenColor.Dim));
        }
        else if (_mode == AppMode.Loading)
        {
            AddRow(rows, T(" ● " + _loadMsg, ScreenColor.Yellow, bold: true));
            AddRow(rows, Key("Ctrl+C") , T("quitter", ScreenColor.Dim));
        }
        else if (_searching)
        {
            AddRow(rows, T(" / ", ScreenColor.Cyan, bold: true), T(_search + "▌", ScreenColor.Yellow, bold: true));
            AddRow(rows, Key("Entrée"), T("valider   ", ScreenColor.Dim), Key("Échap"), T("effacer   ", ScreenColor.Dim), Key("⌫"), T("corriger", ScreenColor.Dim));
        }
        else
        {
            AddRow(rows, T(_status.Length > 0 ? _status : SelectedInfo(), _status.Length == 0 && SelectedInfo().Length > 0 ? ScreenColor.Red : _statusOk ? ScreenColor.Green : ScreenColor.Red));
            AddRow(rows, FooterKeys().SelectMany(k => new[] { Key(k.Key), T(k.Label + "   ", ScreenColor.Dim) }).ToArray());
        }
        term.Render(rows);
    }

    string Breadcrumb() => _view switch
    {
        AppMode.Select => "› Choix du projet",
        AppMode.Packages => "› " + Path.GetFileName(_data?.ProjectFile ?? ""),
        AppMode.Detail => "› " + Path.GetFileName(_data?.ProjectFile ?? "") + " › " + _detail?.Id,
        _ => _data is null ? "" : "› " + Path.GetFileName(_data.ProjectFile),
    };

    string SelectedInfo()
    {
        if (_mode != AppMode.Packages || _data is null) return "";
        var vis = Visible();
        if (vis.Count == 0) return "";
        var p = vis[Math.Clamp(_sel, 0, vis.Count - 1)];
        return p.Vulns.Count == 0 ? "" : $"⚠ {p.Id} : " + string.Join("  |  ", p.Vulns.Select(v => $"{v.Severity} {v.AdvisoryUrl}"));
    }

    static ScreenCell Key(string k) => T(" " + k + " ", ScreenColor.Cyan, bold: true);

    (string Key, string Label)[] FooterKeys() => _mode switch
    {
        AppMode.Select => [("↑↓", "choisir"), ("Entrée", "ouvrir"), ("/", "rechercher"), ("F5", "re-scan"), ("?", "aide"), ("q", "quitter")],
        AppMode.Packages => [("↑↓", "choisir"), ("Entrée", "détail"), ("u", "maj"), ("x", "suppr."), ("/", "chercher"), ("1/2/3", "filtre"), ("?", "aide"), ("q", "retour")],
        AppMode.Detail => [("↑↓", "choisir"), ("Entrée", "installer cette version"), ("?", "aide"), ("Échap", "retour")],
        _ => [("Ctrl+C", "quitter")],
    };

    void RenderHelp(List<ScreenRow> rows)
    {
        AddRow(rows, T(" Aide — touches", ScreenColor.Cyan, bold: true));
        AddRow(rows, T(""));
        string[] lines =
        [
            "Recherche      / : chercher (projets, paquets)   Entrée : valider   Échap : effacer",
            "Partout        ↑↓ PgUp PgDn Home End : naviguer   ?/F1 : cette aide   Échap : retour   Ctrl+C : quitter",
            "Projets        Entrée : analyser le projet   F5 : re-scanner les dossiers   q : quitter",
            "Paquets        Entrée/d : détail   u : mettre à jour (dernière stable)   x : supprimer (directs seulement)",
            "               1 : tous   2 : obsolètes   3 : vulnérables   t : filtre suivant   F5 : recharger   q : retour",
            "Détail         ↑↓ : choisir une version   Entrée/a : installer cette version (confirmation demandée)",
            "",
            "Colonne TYPE   direct = référencé dans le csproj   transitif = tiré par une autre dépendance",
            "État           ⚠ N vuln. = vulnérable (détails en bas)   obsolete = plus récente dispo   à jour",
            "Versions       ⚠ vulnérable = advisory connu : à éviter   * = version installée",
            "",
            "Appuyez sur une touche pour fermer.",
        ];
        foreach (var l in lines) AddRow(rows, T(" " + l));
    }
    // ---------- Choix du projet ----------

    void RenderSelect(List<ScreenRow> rows, Terminal term)
    {
        var list = ProjList();
        AddRow(rows,
            T($" {list.Count}/{_projects.Count} projet(s) sous ", ScreenColor.Dim),
            T(Ellipsis(_root, Math.Max(10, term.Width - 45)), ScreenColor.Default),
            T(_search.Length > 0 ? $"   recherche : {_search}" : "", ScreenColor.Yellow, bold: true));
        if (list.Count == 0)
        {
            AddRow(rows, T(""));
            AddRow(rows, T(_projects.Count == 0 ? " Aucun fichier .csproj trouvé. F5 pour re-scanner." : " Aucun projet ne correspond à la recherche (Échap pour effacer).", ScreenColor.Yellow));
            return;
        }
        var names = list.Select(p => Path.GetFileNameWithoutExtension(p)).ToList();
        int nw = Math.Clamp(names.Max(n => n.Length) + 4, 12, Math.Max(12, term.Width / 2));
        AddRow(rows, T("   PROJET", ScreenColor.Dim, bold: true).Pad(nw + 3), T("DOSSIER", ScreenColor.Dim, bold: true));
        int cap = Math.Max(1, term.Height - 6);
        _projSel = Math.Clamp(_projSel, 0, list.Count - 1);
        var (first, count) = Window(_projSel, list.Count, cap);
        for (int i = first; i < first + count; i++)
        {
            bool sel = i == _projSel;
            string dir = Path.GetDirectoryName(list[i]) ?? "";
            string rel = dir;
            try { rel = Path.GetRelativePath(_root, dir); } catch { }
            if (rel == ".") rel = "(dossier racine)";
            rows.Add(new ScreenRow(
                T(sel ? " ▶ " : "   ", ScreenColor.Yellow, bold: sel),
                T(Fit(names[i], nw), sel ? ScreenColor.Yellow : ScreenColor.Default, bold: true),
                T(Ellipsis(rel, Math.Max(10, term.Width - nw - 5)), ScreenColor.Dim)) { Highlight = sel });
        }
    }

    // Garde la fin d'un chemin : le début est le moins informatif.
    static string Ellipsis(string s, int max) => s.Length <= max ? s : "…" + s[^(max - 1)..];
    // ---------- Liste des paquets ----------

    const int C_ID = 28, C_REQ = 12, C_RES = 13, C_LATEST = 14;

    void RenderPackages(List<ScreenRow> rows, Terminal term)
    {
        if (_data is null) return;
        var vis = Visible();
        var all = Visible(PkgFilter.All);
        int outdated = all.Count(p => p.IsOutdated);
        int vuln = all.Count(p => p.IsVulnerable);
        (ScreenColor fc, string filter) = _filter switch
        {
            PkgFilter.Outdated => (ScreenColor.Yellow, "obsolètes"),
            PkgFilter.Vulnerable => (ScreenColor.Red, "vulnérables"),
            _ => (ScreenColor.Default, "tous"),
        };
        AddRow(rows,
            T(Path.GetFileName(_data.ProjectFile), bold: true),
            T($"   {all.Count} paquets — {outdated} obsolètes, {vuln} vulnérables", ScreenColor.Dim),
            T($"   filtre: {filter} ({vis.Count})", fc),
            T(_search.Length > 0 ? $"   recherche : {_search}" : "", ScreenColor.Yellow, bold: true));
        int idw = Math.Clamp(vis.Select(p => p.Id.Length).DefaultIfEmpty(0).Max() + 3, 12, Math.Max(12, term.Width - 60));
        AddRow(rows,
            T(" PAQUET", ScreenColor.Dim, bold: true).Pad(idw),
            T("REQUIS", ScreenColor.Dim, bold: true).Pad(C_REQ),
            T("RÉSOLU", ScreenColor.Dim, bold: true).Pad(C_RES),
            T("DERNIÈRE", ScreenColor.Dim, bold: true).Pad(C_LATEST),
            T("TYPE", ScreenColor.Dim, bold: true).Pad(11),
            T("  ÉTAT", ScreenColor.Dim, bold: true));
        _sel = Math.Clamp(_sel, 0, Math.Max(0, vis.Count - 1));
        var (first, count) = Window(_sel, vis.Count, term.Height - 6);
        foreach ((Package p, int i) in vis.Select((p, i) => (p, i)).Skip(first).Take(count))
        {
            bool sel = i == _sel;
            var cId = !p.IsDirect ? ScreenColor.Dim : ScreenColor.Default;
            var cRes = cId;
            string latest = p.Latest;
            string arrow = latest.Length == 0 ? " " : (string.Equals(p.Resolved, latest, StringComparison.OrdinalIgnoreCase) ? " = " : " → ");
            var cLatest = p.IsOutdated ? ScreenColor.Yellow : ScreenColor.Dim;
            (ScreenColor, string) state =
                p.IsVulnerable ? (ScreenColor.Red, $"⚠ {p.Vulns.Count} vuln.")
                : p.IsOutdated ? (ScreenColor.Yellow, "obsolete")
                : (ScreenColor.Dim, "à jour");
            rows.Add(new ScreenRow(
                T(Fit((sel ? "▶ " : "  ") + p.Id, idw), sel ? ScreenColor.Yellow : cId, bold: sel || p.IsDirect),
                T(p.Requested, ScreenColor.Dim).Pad(C_REQ),
                T(p.Resolved, ScreenColor.Default).Pad(C_RES),
                T(arrow + latest, cLatest).Pad(C_LATEST),
                T(p.IsDirect ? "direct" : "transitif", p.IsDirect ? ScreenColor.Green : ScreenColor.Yellow).Pad(11),
                T(state.Item2, state.Item1, p.IsVulnerable)) { Highlight = sel });
        }
        if (vis.Count == 0)
            AddRow(rows, T("(aucun paquet ne correspond au filtre / à la recherche)", ScreenColor.Dim));
    }

    static string Fit(string s, int width) =>
        s.Length >= width ? s[..(width - 2)] + "… " : s.PadRight(width);

    // Fenêtre défilante centrée sur la sélection : (premier index, nombre de lignes).
    static (int First, int Count) Window(int sel, int total, int capacity)
    {
        capacity = Math.Max(1, capacity);
        int first = Math.Clamp(sel - capacity / 2, 0, Math.Max(0, total - capacity));
        return (first, Math.Min(capacity, total - first));
    }

    // ---------- Détail d'un paquet ----------

    void RenderDetail(List<ScreenRow> rows, Terminal term)
    {
        var pkg = _detail;
        if (pkg is null) return;
        AddRow(rows,
            T(pkg.Id + " " + pkg.Resolved, ScreenColor.Cyan, bold: true),
            T(pkg.IsDirect ? "   [DIRECT — référencé dans le projet]" : "   [TRANSITIF — tiré par une autre dépendance]",
                pkg.IsDirect ? ScreenColor.Green : ScreenColor.Yellow, bold: true));
        if (pkg.Vulns.Count > 0)
        {
            var worst = pkg.Vulns.Select(v => v.Severity).OrderBy(s =>
                s.ToLowerInvariant() switch { "critical" => 0, "high" => 1, _ => 2 }).First();
            AddRow(rows, T($"⚠ {pkg.Vulns.Count} vulnérabilité(s), pire sévérité: {worst}", ScreenColor.Red, bold: true));
        }
        AddRow(rows, T(""));
        var detailLines = WrapLines(_detailInfo, term.Width).Take(Math.Max(3, term.Height / 2 - 3)).ToList();
        foreach (var line in detailLines)
            AddRow(rows, T(line, ScreenColor.Default));
        AddRow(rows, T(""));
        AddRow(rows, T("Versions disponibles (Entrée: passer à la version choisie, ⚠ = à éviter)", ScreenColor.Dim, bold: true));
        int max = Math.Max(1, term.Height - rows.Count - 2);
        if (_versions.Count == 0)
            AddRow(rows, T("(liste de versions indisponible)", ScreenColor.Dim));
        var (vFirst, vCount) = Window(_verSel, _versions.Count, max);
        foreach ((string v, int i) in _versions.Select((v, i) => (v, i)).Skip(vFirst).Take(vCount))
        {
            bool sel = i == _verSel;
            bool cur = v.Equals(pkg.Resolved, StringComparison.OrdinalIgnoreCase);
            ScreenColor c = sel ? ScreenColor.Yellow : cur ? ScreenColor.Green : ScreenColor.Dim;
            string mark = "";
            if (_verVulns.TryGetValue(v, out var vr))
                mark = $"  ⚠ vulnérable ({string.Join(", ", vr.Select(x => NuGetFeed.SeverityName(x.Severity)).Distinct())})";
            rows.Add(new ScreenRow(
                T((sel ? "▶ " : "  ") + (cur ? "*" : " ") + v, c, bold: sel || cur),
                T(mark, ScreenColor.Red, bold: sel)) { Highlight = sel });
        }
    }

    IEnumerable<string> WrapLines(string text, int max)
    {
        if (text.Length == 0) yield break;
        foreach (var para in text.Split('\n'))
        {
            string line = para.TrimEnd('\r');
            while (line.Length > max)
            {
                int cut = line.LastIndexOf(' ', max - 1);
                if (cut < 20) cut = max - 1;
                yield return line[..cut];
                line = line[cut..].TrimStart();
            }
            yield return line;
        }
    }

    // ---------- Aides ----------

    static void AddRow(List<ScreenRow> rows, params ScreenCell[] cells) => rows.Add(new ScreenRow(cells));
    static ScreenCell T(string text, ScreenColor color = ScreenColor.Default, bool bold = false)
        => new(text, color, bold);
}

static class CellPadding
{
    public static ScreenCell Pad(this ScreenCell c, int width)
        => c.Text.Length >= width ? c : new(c.Text + new string(' ', width - c.Text.Length), c.Color, c.Bold);
}
