// Packwise — petite TUI de gestion des dépendances NuGet et npm (zéro paquet NuGet).
// Usage: Packwise [dossier]   — par défaut, le dossier courant.

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"Dossier introuvable : {root}");
    return 1;
}

try
{
    await new Packwise.PackwiseApp(root).RunAsync();
}
catch (System.Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
return 0;
