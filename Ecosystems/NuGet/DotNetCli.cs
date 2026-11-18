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

    public static Task<Proc.Result> ListAsync(string projectFile) =>
        RunAsync("list", projectFile, "package", "--include-transitive", "--format", "json");

    public static Task<Proc.Result> ListOutdatedAsync(string projectFile) =>
        RunAsync("list", projectFile, "package", "--outdated", "--format", "json");

    public static Task<Proc.Result> ListVulnerableAsync(string projectFile) =>
        RunAsync("list", projectFile, "package", "--vulnerable", "--include-transitive", "--format", "json");

    public static Task<Proc.Result> RestoreAsync(string projectFile) =>
        RunAsync("restore", projectFile);

    public static Task<Proc.Result> AddPackageAsync(string projectFile, string id, string version) =>
        RunAsync("add", projectFile, "package", id, "--version", version);

    public static Task<Proc.Result> RemovePackageAsync(string projectFile, string id) =>
        RunAsync("remove", projectFile, "package", id);
}
