namespace Packwise;

/// <summary>
/// Tout passe par le CLI `dotnet` : résolution, versions obsolètes,
/// vulnérabilités (dépôt d'advisories NuGet) et mise à jour des paquets.
/// </summary>
public static class DotNetCli
{
    // Franglais impossible : les clés JSON restent stables en locale française.
    static readonly Dictionary<string, string> Env = new()
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["DOTNET_NOLOGO"] = "1",
    };

    static Task<Proc.Result> RunAsync(params string[] args) => Proc.RunAsync("dotnet", args, env: Env);

    // `dotnet list package` sans sa restauration implicite : le projet est restauré une fois avant les trois appels,
    // qui ne relancent pas chacun la même restauration en parallèle.
    static Task<Proc.Result> ListAsync(string projectFile, params string[] extra) =>
        RunAsync(["list", projectFile, "package", .. extra, "--no-restore", "--format", "json"]);

    public static Task<Proc.Result> ListInstalledAsync(string projectFile) =>
        ListAsync(projectFile, "--include-transitive");

    public static Task<Proc.Result> ListOutdatedAsync(string projectFile) =>
        ListAsync(projectFile, "--outdated");

    public static Task<Proc.Result> ListVulnerableAsync(string projectFile) =>
        ListAsync(projectFile, "--vulnerable", "--include-transitive");

    public static Task<Proc.Result> RestoreAsync(string projectFile) =>
        RunAsync("restore", projectFile);

    public static Task<Proc.Result> AddPackageAsync(string projectFile, string id, string version) =>
        RunAsync("add", projectFile, "package", id, "--version", version);

    public static Task<Proc.Result> RemovePackageAsync(string projectFile, string id) =>
        RunAsync("remove", projectFile, "package", id);
}
