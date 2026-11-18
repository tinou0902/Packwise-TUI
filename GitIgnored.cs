using System.Diagnostics;

namespace Packwise;

/// <summary>
/// Fichiers ignorés par git (.gitignore, .git/info/exclude, exclusions globales) : sorties de build comme `.next/` ou
/// `dist/`, qui recopient des package.json.
/// </summary>
public static class GitIgnored
{
    /// <summary>
    /// <paramref name="files"/> sans ceux que git ignore. Les fichiers hors dépôt sont gardés, et tous le sont sans git.
    /// </summary>
    public static List<string> Without(IReadOnlyList<string> files)
    {
        var repoCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in files.GroupBy(f => RepoOf(Path.GetDirectoryName(Path.GetFullPath(f))!, repoCache)))
            if (group.Key is { } repo)
                ignored.UnionWith(CheckIgnore(repo, group.ToList()));
        return files.Where(f => !ignored.Contains(Path.GetFullPath(f))).ToList();
    }

    /// <summary>Racine du dépôt d'un dossier (ancêtre le plus proche avec un `.git`, dossier ou fichier), null hors dépôt.</summary>
    static string? RepoOf(string dir, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(dir, out var known)) return known;
        var git = Path.Combine(dir, ".git");
        string? repo = Directory.Exists(git) || File.Exists(git) ? dir
            : Path.GetDirectoryName(dir) is { } parent ? RepoOf(parent, cache) : null;
        return cache[dir] = repo;
    }

    /// <summary>Fichiers ignorés parmi ceux d'un dépôt, d'après `git check-ignore`.</summary>
    static IEnumerable<string> CheckIgnore(string repo, List<string> files)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repo,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "check-ignore", "-z", "--stdin" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.StandardInput.Write(string.Concat(files.Select(f => Path.GetRelativePath(repo, f) + '\0')));
            p.StandardInput.Close();
            p.WaitForExit();
            // Code 1 : aucun fichier ignoré ; autre chose : erreur de git, rien n'est filtré.
            if (p.ExitCode != 0) return [];
            return stdout.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(f => Path.GetFullPath(f, repo));
        }
        catch
        {
            return []; // git absent
        }
    }
}
