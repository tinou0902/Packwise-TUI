using System.Diagnostics;

namespace Packwise;

/// <summary>Lancement des CLI des écosystèmes (dotnet, npm), sans jamais lever sur un code de sortie non nul.</summary>
public static class Proc
{
    public sealed class Result
    {
        public int ExitCode;
        public string Stdout = "";
        public string Stderr = "";
        public bool Success => ExitCode == 0;

        /// <summary>Première ligne utile de l'erreur (stderr, sinon stdout).</summary>
        public string Error => FirstLine(Stderr.Trim() == "" ? Stdout : Stderr);

        public Result EnsureSuccess() => Success ? this : throw new Exception(Error);
    }

    public static async Task<Result> RunAsync(string exe, IEnumerable<string> args, string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (cwd is not null) psi.WorkingDirectory = cwd;
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
            psi.Environment[k] = v;

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

    public static string FirstLine(string s)
    {
        foreach (var line in s.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t.Length > 240 ? t[..240] : t;
        }
        return s.Length > 240 ? s[..240] : s;
    }
}
