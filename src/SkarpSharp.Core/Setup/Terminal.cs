using System.Runtime.InteropServices;
using System.Text;

namespace SkarpSharp.Setup;

/// <summary>A key press, or a change of the terminal's size.</summary>
public readonly struct InputEvent(ConsoleKeyInfo key, bool resize = false)
{
    public ConsoleKeyInfo Key { get; } = key;
    public bool Resize { get; } = resize;
}

/// <summary>Where the menu's keys come from: the console, or a script in tests.</summary>
public interface IKeySource
{
    InputEvent Read();
}

/// <summary>What the menu draws on: the console, or a fixed-size fake in tests.</summary>
public interface ITerminal
{
    int Width { get; }
    int Height { get; }
    void Write(string text);
    void Flush();
}

public enum LineStyle { Normal, Header, Highlight, Dim, Error }

/// <summary>One logical line of a frame; <paramref name="cursor"/>: the column of the visible text cursor (an entry line,
/// always the frame's last line), or null.</summary>
public sealed class Line(string text, LineStyle style = LineStyle.Normal, int? cursor = null)
{
    public string Text { get; } = text;
    public LineStyle Style { get; } = style;
    public int? Cursor { get; } = cursor;
}

/// <summary>One frame: the logical lines of the inline block, independent of the terminal (golden frames read these).</summary>
public sealed class Frame(IReadOnlyList<Line> lines)
{
    public IReadOnlyList<Line> Lines { get; } = lines;

    /// <summary>The lines with a style mark: <c>H</c> header, <c>&gt;</c> highlight, <c>.</c> dim, <c>!</c> error. The text
    /// cursor shows as <c>_</c> inserted at its column.</summary>
    public string ToSnapshot()
    {
        var sb = new StringBuilder();
        foreach (var l in Lines)
        {
            char mark = l.Style switch
            {
                LineStyle.Header => 'H', LineStyle.Highlight => '>', LineStyle.Dim => '.', LineStyle.Error => '!', _ => ' ',
            };
            var text = l.Cursor is int c ? l.Text.Insert(Math.Min(c, l.Text.Length), "_") : l.Text;
            sb.Append(mark).Append('|').Append(text).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Writes frames inline (no alternate screen): the first frame where the cursor is, later frames over it
/// (cursor back to the block's first line, each line cleared and rewritten). Lines are cut at width - 1, so the
/// terminal never wraps. After a resize (a width change since the last draw counts too) it moves up by the rows the
/// last frame occupies after the terminal reflowed it, clears below and redraws. Without colour
/// (<c>NO_COLOR</c>, <c>TERM=dumb</c>) no SGR codes are written; the highlight is then the "&gt; " marker alone.
/// </summary>
public sealed class FrameWriter(ITerminal terminal, bool color = true)
{
    int _height;
    int[] _lengths = [];   // visible characters written on each line of the last frame
    int _width;            // terminal width when the last frame was drawn
    bool _cursorShown;     // the text cursor is visible on the last line

    /// <summary>
    /// Rows the last frame occupies now. Terminals that reflow on resize (Windows Terminal, conhost, VTE) re-wrap each
    /// old line at the new width, so a line of n characters takes ceil(n / width) rows. Terminals that truncate instead
    /// (xterm) can lose lines above the menu: a known limit.
    /// </summary>
    public static int RowsAfterReflow(IReadOnlyList<int> lengths, int width)
    {
        width = Math.Max(1, width);
        int rows = 0;
        foreach (int n in lengths) rows += Math.Max(1, (n + width - 1) / width);
        return rows;
    }

    public void Draw(Frame frame, bool resized = false)
    {
        var sb = new StringBuilder();
        if (_height == 0) sb.Append("\u001b[?25l"); // hide the cursor
        else
        {
            bool clear = resized || terminal.Width != _width;
            int rows = clear ? RowsAfterReflow(_lengths, terminal.Width) : _height;
            sb.Append('\r');
            if (rows > 1) sb.Append("\u001b[").Append(rows - 1).Append('A');
            if (clear) sb.Append("\u001b[J");
        }
        var lengths = new int[frame.Lines.Count];
        int width = Math.Max(10, terminal.Width) - 1;
        for (int i = 0; i < frame.Lines.Count; i++)
        {
            var line = frame.Lines[i];
            string text = line.Text.Length > width ? line.Text[..width] : line.Text;
            sb.Append("\r\u001b[2K");
            if (color)
                sb.Append(line.Style switch
                {
                    LineStyle.Header => "\u001b[1m",
                    LineStyle.Highlight => "\u001b[7m",
                    LineStyle.Dim => "\u001b[2m",
                    LineStyle.Error => "\u001b[31m",
                    _ => "",
                });
            string shown = color && line.Style == LineStyle.Highlight ? text.PadRight(width) : text;
            lengths[i] = shown.Length;
            sb.Append(shown);
            if (color && line.Style != LineStyle.Normal) sb.Append("\u001b[0m");
            if (i < frame.Lines.Count - 1) sb.Append('\n');
        }
        if (_height > frame.Lines.Count) sb.Append("\u001b[J");
        PlaceCursor(sb, frame.Lines.Count == 0 ? null : frame.Lines[^1].Cursor, width);
        _height = frame.Lines.Count;
        _lengths = lengths;
        _width = terminal.Width;
        terminal.Write(sb.ToString());
        terminal.Flush();
    }

    /// <summary>Shows the text cursor at <paramref name="column"/> of the last line (an entry), or hides it.</summary>
    void PlaceCursor(StringBuilder sb, int? column, int width)
    {
        if (column is not int c)
        {
            if (_cursorShown) sb.Append("\u001b[?25l");
            _cursorShown = false;
            return;
        }
        c = Math.Clamp(c, 0, width);
        sb.Append('\r');
        if (c > 0) sb.Append("\u001b[").Append(c).Append('C');
        sb.Append("\u001b[?25h");
        _cursorShown = true;
    }

    /// <summary>Leaves the block: cursor below it, visible, attributes reset.</summary>
    public void Close()
    {
        terminal.Write((color ? "\u001b[0m" : "") + "\u001b[?25h\n");
        terminal.Flush();
    }

    /// <summary>False when <c>NO_COLOR</c> is set and not empty, or <c>TERM</c> is <c>dumb</c> (no-color.org).</summary>
    public static bool ColorAllowed(Func<string, string?> variable) =>
        variable("NO_COLOR") is not { Length: > 0 } && variable("TERM") != "dumb";
}

/// <summary>The real console's keys: <c>KeyAvailable</c> polled every 40 ms, so a resize is seen without a key press.</summary>
public sealed class ConsoleKeySource : IKeySource
{
    int _w = Console.WindowWidth, _h = Console.WindowHeight;

    public InputEvent Read()
    {
        while (!Console.KeyAvailable)
        {
            Thread.Sleep(40);
            if (Console.WindowWidth != _w || Console.WindowHeight != _h)
            {
                _w = Console.WindowWidth; _h = Console.WindowHeight;
                return new(default, resize: true);
            }
        }
        return new(Console.ReadKey(intercept: true));
    }
}

/// <summary>The real console: UTF-8 bytes to standard output.</summary>
public sealed class ConsoleTerminal : ITerminal
{
    readonly Stream _out = Console.OpenStandardOutput();
    public int Width => Console.WindowWidth;
    public int Height => Console.WindowHeight;
    public void Write(string text) { var b = Encoding.UTF8.GetBytes(text); _out.Write(b, 0, b.Length); }
    public void Flush() => _out.Flush();
}

/// <summary>
/// The console set up for the menu: VT processing on (Windows, one <c>SetConsoleMode</c> call), Ctrl+C as a key, and
/// the cursor and attributes restored in <c>finally</c>, on <c>CancelKeyPress</c> and at process exit.
/// </summary>
public static partial class ConsoleSession
{
    public static IDisposable Begin()
    {
        EnableVirtualTerminal();
        bool treat = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        ConsoleCancelEventHandler cancel = (_, _) => Restore();
        EventHandler exit = (_, _) => Restore();
        Console.CancelKeyPress += cancel;
        AppDomain.CurrentDomain.ProcessExit += exit;
        return new End(() =>
        {
            Console.TreatControlCAsInput = treat;
            Console.CancelKeyPress -= cancel;
            AppDomain.CurrentDomain.ProcessExit -= exit;
            Restore();
        });
    }

    sealed class End(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    static void Restore()
    {
        try
        {
            using var o = Console.OpenStandardOutput();
            o.Write("\u001b[0m\u001b[?25h"u8);
        }
        catch (IOException) { return; } // the console is gone: nothing to restore
    }

    static void EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        nint h = GetStdHandle(-11);
        if (GetConsoleMode(h, out uint mode)) SetConsoleMode(h, mode | 0x0004 /* ENABLE_VIRTUAL_TERMINAL_PROCESSING */);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int n);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint h, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint h, uint mode);
}

/// <summary>A terminal of a set size that records every byte written and every cursor move up (golden frames, resize test).</summary>
public sealed class FakeTerminal(int width, int height) : ITerminal
{
    public StringBuilder Output { get; } = new();
    public int Width { get; set; } = width;
    public int Height { get; set; } = height;
    /// <summary>The rows of each cursor move up (<c>ESC [ n A</c>), in order.</summary>
    public List<int> MoveUps { get; } = [];

    public void Write(string text)
    {
        Output.Append(text);
        for (int i = text.IndexOf("\u001b[", StringComparison.Ordinal); i >= 0; i = text.IndexOf("\u001b[", i + 2, StringComparison.Ordinal))
        {
            int j = i + 2;
            while (j < text.Length && char.IsAsciiDigit(text[j])) j++;
            if (j > i + 2 && j < text.Length && text[j] == 'A') MoveUps.Add(int.Parse(text.AsSpan(i + 2, j - i - 2), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public void Flush() { }
}

/// <summary>
/// Keys from a script, one token per line or separated by spaces: <c>Down Up Left Right Enter Esc Backspace Delete
/// Space PageDown PageUp Home End Ctrl+C</c>, <c>Resize:WxH</c> (resizes the fake terminal), or literal text (each
/// character typed). Lines starting with <c>#</c> are comments. After the last token: Ctrl+C.
/// </summary>
public sealed class ScriptKeySource(IEnumerable<string> tokens, FakeTerminal? terminal = null) : IKeySource
{
    readonly Queue<string> _tokens = new(tokens.Where(t => !t.TrimStart().StartsWith('#'))
        .SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    readonly Queue<char> _typed = new();
    public string? Last { get; private set; }

    public InputEvent Read()
    {
        if (_typed.Count > 0) return new(Char(_typed.Dequeue()));
        if (_tokens.Count == 0) { Last = "(end)"; return new(new('\u0003', ConsoleKey.C, false, false, true)); }
        var t = Last = _tokens.Dequeue();
        if (t.StartsWith("Resize:", StringComparison.Ordinal) && terminal is not null)
        {
            var size = t["Resize:".Length..].Split('x');
            terminal.Width = int.Parse(size[0], System.Globalization.CultureInfo.InvariantCulture);
            terminal.Height = int.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture);
            return new(default, resize: true);
        }
        ConsoleKeyInfo? key = t switch
        {
            "Down" => K(ConsoleKey.DownArrow), "Up" => K(ConsoleKey.UpArrow), "Left" => K(ConsoleKey.LeftArrow), "Right" => K(ConsoleKey.RightArrow),
            "Enter" => new('\r', ConsoleKey.Enter, false, false, false), "Esc" => new('\u001b', ConsoleKey.Escape, false, false, false),
            "Backspace" => new('\b', ConsoleKey.Backspace, false, false, false), "Delete" => K(ConsoleKey.Delete),
            "Space" => new(' ', ConsoleKey.Spacebar, false, false, false),
            "PageDown" => K(ConsoleKey.PageDown), "PageUp" => K(ConsoleKey.PageUp), "Home" => K(ConsoleKey.Home), "End" => K(ConsoleKey.End),
            "Ctrl+C" => new('\u0003', ConsoleKey.C, false, false, true),
            _ => null,
        };
        if (key is { } k) return new(k);
        foreach (var c in t[1..]) _typed.Enqueue(c);
        return new(Char(t[0]));
    }

    static ConsoleKeyInfo K(ConsoleKey key) => new('\0', key, false, false, false);

    static ConsoleKeyInfo Char(char c) => new(c, char.IsAsciiLetter(c) ? ConsoleKey.A + (char.ToUpperInvariant(c) - 'A')
        : char.IsAsciiDigit(c) ? ConsoleKey.D0 + (c - '0') : c == ' ' ? ConsoleKey.Spacebar : 0, false, false, false);
}
