using System.Diagnostics;

namespace Packwise;

/// <summary>
/// Tout passe par le CLI `dotnet` : résolution, versions obsolètes,
/// vulnérabilités (dépôt d'advisories NuGet) et mise à jour des paquets.
/// </summary>
public static class DotNetCli
{
    public sealed class Result
    {
        public int ExitCode;
        public string Stdout = "";
        public string Stderr = "";
        public bool Success => ExitCode == 0;
    }

    public static async Task<Result> RunAsync(string projectFile, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        // Franglais impossible : les clés JSON restent stables en locale française.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        using var p = Process.Start(psi)!;
        p.StandardInput.Close(); // le fils ne doit pas toucher à l'entrée console
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return new Result
        {
            ExitCode = p.ExitCode,
            Stdout = await stdout,
            Stderr = await stderr,
        };
    }

    public static Task<Result> ListAsync(string projectFile) =>
        RunAsync(projectFile, "list", projectFile, "package", "--include-transitive", "--format", "json");

    public static Task<Result> ListOutdatedAsync(string projectFile) =>
        RunAsync(projectFile, "list", projectFile, "package", "--outdated", "--format", "json");

    public static Task<Result> ListVulnerableAsync(string projectFile) =>
        RunAsync(projectFile, "list", projectFile, "package", "--vulnerable", "--include-transitive", "--format", "json");

    public static Task<Result> RestoreAsync(string projectFile) =>
        RunAsync(projectFile, "restore", projectFile);

    public static Task<Result> AddPackageAsync(string projectFile, string id, string version) =>
        RunAsync(projectFile, "add", projectFile, "package", id, "--version", version);

    public static Task<Result> RemovePackageAsync(string projectFile, string id) =>
        RunAsync(projectFile, "remove", projectFile, "package", id);

    /// <summary>Détecte les projets dans le répertoire donné (csproj/fxproj, sans obj/).</summary>
    public static List<string> FindProjects(string startDir)
    {
        var results = new List<string>();
        Search(startDir, results, depth: 4);
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    static void Search(string dir, List<string> results, int depth)
    {
        if (depth < 0) return;
        string[] files;
        try { files = Directory.GetFiles(dir, "*.csproj"); }
        catch { return; }
        foreach (var f in files)
            results.Add(f);
        string[] subdirs;
        try { subdirs = Directory.GetDirectories(dir); }
        catch { return; }
        foreach (var sub in subdirs)
        {
            var name = Path.GetFileName(sub);
            if (name is "bin" or "obj" or "node_modules" or "packages")
                continue;
            Search(sub, results, depth - 1);
        }
    }
}
