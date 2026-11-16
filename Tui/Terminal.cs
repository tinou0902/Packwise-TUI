namespace NuKeeper.Tui;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Petit terminal ANSI maison : écran alternatif, couleurs SGR, taille du hublot,
/// lecture des touches. Pas de dépendance externe.
/// </summary>
public sealed class Terminal : IDisposable
{
    const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    static readonly IntPtr STD_OUTPUT_HANDLE = new(-11);

    [DllImport("kernel32.dll")]
    static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll")]
    static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    uint _oldOutMode;
    bool _vtWasOn;
    bool _vtApplied;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public Terminal()
    {
        (Width, Height) = SafeSize();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && GetConsoleMode(STD_OUTPUT_HANDLE, out var mode))
        {
            _oldOutMode = mode;
            _vtWasOn = (mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0;
            if (!SetConsoleMode(STD_OUTPUT_HANDLE, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | ENABLE_PROCESSED_OUTPUT))
                throw new InvalidOperationException("Impossible d'activer le mode ANSI du terminal.");
            _vtApplied = true;
        }
        Console.CursorVisible = false;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Write("\x1b[?1049h"); // écran alternatif
        Console.TreatControlCAsInput = true;
    }

    public void Dispose()
    {
        try
        {
            Console.Write("\x1b[?1049l");
            Console.CursorVisible = true;
            if (!_vtApplied) return;
            GetConsoleMode(STD_OUTPUT_HANDLE, out var mode);
            mode = _vtWasOn ? mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING
                            : mode & ~ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            SetConsoleMode(STD_OUTPUT_HANDLE, mode);
        }
        catch
        {
            // Ne pas masquer la sortie réelle à la fermeture.
        }
    }

    static (int, int) SafeSize()
    {
        try
        {
            return (Math.Max(40, Console.WindowWidth), Math.Max(10, Console.WindowHeight));
        }
        catch
        {
            return (100, 30); // pas de console interactive (pipes, CI)
        }
    }

    /// <summary>Lit une touche ; Escape se déduit après un timeout court quand le terminal l'envoie en séquence.</summary>
    public ConsoleKeyInfo ReadKey()
    {
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.None)
                continue;
            return k; // Ctrl+C intercepté renvoyé tel quel (KeyChar 0x03)
        }
    }

    public void RefreshSize()
    {
        (Width, Height) = SafeSize();
    }

    /// <summary>
    /// Rafraîchit toute la vue. rows : lignes, chacune une suite de (texte, couleur, gras).
    /// Le curseur reste caché.
    /// </summary>
    public void Render(IReadOnlyList<ScreenRow> rows)
    {
        (Width, Height) = SafeSize();
        var sb = new StringBuilder();
        sb.Append("\x1b[H"); // curseur en haut à gauche ; chaque ligne est complétée à la largeur, pas d'effacement => pas de scintillement

        int rowIndex = 0;
        foreach (var row in rows.Take(Height))
        {
            if (rowIndex++ > 0) sb.Append("\r\n");
            // Assembler la ligne en (plain, segments) puis tronquer à la largeur.
            var plain = new StringBuilder();
            var segs = new List<(int start, int len, ScreenCell cell)>();
            foreach (var cell in row.Cells)
            {
                if (cell.Text.Length == 0) continue;
                segs.Add((plain.Length, cell.Text.Length, cell));
                plain.Append(cell.Text);
            }
            string text = plain.ToString();
            if (text.Length > Width)
                text = text[..Width];
            foreach (var (start, len, cell) in segs)
            {
                if (start >= text.Length) break;
                int end = Math.Min(start + len, text.Length);
                AppendStyled(sb, text.AsSpan(start, end - start), cell.Color, cell.Bold, row.Highlight);
            }
            if (text.Length < Width)
                AppendStyled(sb, new string(' ', Width - text.Length), ScreenColor.Default, false, row.Highlight);
        }
        sb.Append("\x1b[J");
        Console.Write(sb);
    }

    public static void AppendStyled(StringBuilder sb, ReadOnlySpan<char> text,     ScreenColor color, bool bold, bool highlight = false)
    {
        sb.Append('\x1b')
          .Append(bold ? "[1;" : "[0;")
              .Append(highlight ? "48;5;238;" : "")
          .Append(code(highlight && color == ScreenColor.Dim ? ScreenColor.Default : color))
          .Append('m');
        sb.Append(text);
        sb.Append("\x1b[0m");
    }

    static int code(ScreenColor c) => c switch
    {
        ScreenColor.Default => 39,
        ScreenColor.Dim => 90,
        ScreenColor.Red => 31,
        ScreenColor.Green => 32,
        ScreenColor.Yellow => 33,
        ScreenColor.Blue => 34,
        ScreenColor.Magenta => 35,
        ScreenColor.Cyan => 36,
        _ => 39,
    };
}

public enum ScreenColor { Default, Dim, Red, Green, Yellow, Blue, Magenta, Cyan }

public readonly struct ScreenCell(string text, ScreenColor color = ScreenColor.Default, bool bold = false)
{
    public string Text { get; } = text;
    public ScreenColor Color { get; } = color;
    public bool Bold { get; } = bold;
}

public readonly record struct ScreenRow(IReadOnlyList<ScreenCell> Cells)
{
    public bool Highlight { get; init; }
    public ScreenRow(params ScreenCell[] cells) : this((IReadOnlyList<ScreenCell>)cells) { }
}
