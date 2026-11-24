using System.Text;

namespace SkarpSharp.Setup;

/// <summary>The shells a built command is printed for.</summary>
public enum Shell { PowerShell, Cmd, Sh }

/// <summary>
/// One argument written so the
/// shell hands it to Skarp unchanged. Plain arguments (letters, digits and <c>-_./:=,+@</c>) are written as they are.
/// A null result means the shell cannot carry the argument exactly; the caller leaves the setting out with a note.
/// </summary>
public static class ShellQuote
{
    /// <summary>Letters, digits and <c>-_./:=,+@</c> (with <paramref name="backslash"/>: also the backslash, which cmd and
    /// PowerShell pass on as it is), not empty.</summary>
    public static bool Plain(string s, bool backslash = false)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=' or ',' or '+' or '@' || (backslash && c == '\\'))) return false;
        return true;
    }

    /// <summary>The argument for <paramref name="shell"/>, or null when that shell cannot carry it.</summary>
    public static string? Quote(Shell shell, string arg) => shell switch
    {
        Shell.PowerShell => PowerShell(arg),
        Shell.Cmd => Cmd(arg),
        _ => Sh(arg),
    };

    /// <summary>POSIX sh: single quotes, each <c>'</c> written as <c>'\''</c>; safe for every character.</summary>
    public static string Sh(string arg) => Plain(arg) ? arg : "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// PowerShell (Windows PowerShell 5.1 and 7.3 or later): single quotes, each <c>'</c> and typographic single quote
    /// (U+2018 to U+201B) doubled. Plain arguments starting with <c>@</c> (splatting) or holding a comma (array syntax)
    /// are quoted too. Null for a value with <c>"</c> or with whitespace and a trailing backslash (5.1 and 7.3 pass them
    /// differently) and for a line break.
    /// </summary>
    public static string? PowerShell(string arg)
    {
        if (!PowerShellPortable(arg)) return null;
        if (Plain(arg, backslash: true) && arg[0] != '@' && !arg.Contains(',')) return arg;
        var sb = new StringBuilder("'");
        foreach (var c in arg)
        {
            sb.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>False when Windows PowerShell 5.1 and PowerShell 7.3 or later cannot both pass the argument exactly.</summary>
    public static bool PowerShellPortable(string arg) =>
        !arg.Contains('"') && !arg.Contains('\n') && !arg.Contains('\r')
        && !(arg.EndsWith('\\') && arg.AsSpan().IndexOfAny(" \t") >= 0);

    /// <summary>
    /// cmd at the prompt: a value without <c>"</c> in double quotes (backslashes before the closing quote doubled); with
    /// <c>"</c>, the Windows argument quoting with every cmd metacharacter caret-escaped. Null for <c>%</c> (expanded
    /// even inside quotes) and line breaks.
    /// </summary>
    public static string? Cmd(string arg)
    {
        if (arg.Contains('%') || arg.Contains('\n') || arg.Contains('\r')) return null;
        if (Plain(arg, backslash: true)) return arg;
        var quoted = WindowsArgument(arg);
        if (!arg.Contains('"')) return quoted;
        var sb = new StringBuilder();
        foreach (var c in quoted)
        {
            if (c is '(' or ')' or '%' or '!' or '^' or '"' or '<' or '>' or '&' or '|') sb.Append('^');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// One argument quoted for the Windows argument rules: in double quotes, a run of
    /// backslashes before a <c>"</c> doubled plus one, a run at the end doubled.
    /// </summary>
    public static string WindowsArgument(string arg)
    {
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        return sb.Append('"').ToString();
    }

    /// <summary>True when Git Bash (MSYS) may rewrite the argument as a path: it starts with <c>/</c> or holds <c>=/</c>.</summary>
    public static bool MsysMayConvert(string arg) => arg.StartsWith('/') || arg.Contains("=/", StringComparison.Ordinal);
}
